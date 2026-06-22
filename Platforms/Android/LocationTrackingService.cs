// This is the background service that keeps following the rider's GPS once a trip starts.
// A few things we had to get right for it to stay safe and reliable:
//   - It's Exported=false, so no other app (or ADB) can start or bind to it — only we can.
//   - On Android 14+ it MUST declare ForegroundServiceType=TypeLocation, otherwise the OS just
//     kills it and tracking quietly dies mid-commute, which is the worst case for an alarm app.
//   - If the OS throws a SecurityException when we ask for location updates (e.g. permission was
//     pulled), we StopSelf() instead of lingering as a zombie that drains battery for nothing.
// We never touch credentials and never write coordinates to disk or the network here — the raw fix
// just goes out through the static LocationUpdated event for AndroidLocationService to pick up.

using AlarmaApp.Models;
using AlarmaApp.Services;
using Android.App;
using Android.Content;
using Android.Locations;
using Android.OS;
using Android.Runtime;
using System.Security;

// Alias to avoid ambiguity between AlarmaApp.Resource and Android.Resource
using AndroidResource = Android.Resource;

namespace AlarmaApp.Platforms.Android;

[Service(
    Exported = false,
    ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeLocation)]
public class LocationTrackingService : Service, ILocationListener
{
    // Shared so IAlarmNotificationService can update this same ongoing notification in place.
    internal const int TrackingNotificationId = 1001;
    internal const string TrackingChannelId = "alarma_location_tracking";
    // GPS is our precise source, so we poll it a bit more aggressively than the coarse network
    // provider. This is just the STARTING cadence — once the controller knows the distance to the stop it
    // adjusts the GPS minTime through ApplyCadence (slow far away, back to this 2 s baseline on approach).
    private const long DefaultGpsUpdateIntervalMillis = 2000;
    private const long NetworkUpdateIntervalMillis = 5000;

    // The cadence the controller has asked for, and the wake-lock decision that rides with it. Static so
    // the .NET-side AndroidLocationService can push a new value into whichever service instance is running.
    private static volatile int s_requestedGpsIntervalMillis = (int)DefaultGpsUpdateIntervalMillis;
    private static volatile bool s_holdWakeLockRequested = true;
    private static LocationTrackingService? s_instance;

    // The cadence currently registered with LocationManager — we only re-register when the band actually
    // changes, not on every fix.
    private long _currentGpsIntervalMillis = DefaultGpsUpdateIntervalMillis;
    private const float MinDistanceMetersGps = 5f;
    private const float MinDistanceMetersNetwork = 10f;
    // We enforce a strict accuracy gate here because indoor cell-tower triangulation is far too sloppy
    // for the alarm math — a fix that's off by a few hundred metres makes the live pin and the
    // distance-to-destination readout jump around, and worst case it would fire the wake-up alarm at the
    // wrong stop. So we just drop any fix whose accuracy radius is wider than this threshold. (An accuracy
    // of 0 means the provider didn't report one at all, which we still let through.)
#if DEBUG
    // We loosen the gate to 200 m in Debug because we develop against the emulator at a desk indoors,
    // where every fix comes back at 100 m+ — without this we could never exercise the whole tracking flow
    // without physically walking a route outside.
    private const float MaxAcceptableAccuracyMeters = 200f;
#else
    // Release holds the line strictly at 50 m — the accuracy our alarm timing was actually tuned against.
    private const float MaxAcceptableAccuracyMeters = 50f;
#endif
    private LocationManager? _locationManager;
    private PowerManager.WakeLock? _wakeLock;
    private bool _isStarted;

    public static event EventHandler<LocationSnapshot>? LocationUpdated;

    public static LocationSnapshot? LastKnownLocation { get; private set; }

    public override void OnCreate()
    {
        base.OnCreate();
        StartForeground(TrackingNotificationId, BuildNotification("Acquiring GPS…"));
        StartLocationUpdates();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (!_isStarted)
        {
            StartLocationUpdates();
        }

        return StartCommandResult.Sticky;
    }

    public override IBinder? OnBind(Intent? intent)
    {
        return null;
    }

    public override void OnDestroy()
    {
        StopLocationUpdates();
        if (ReferenceEquals(s_instance, this))
            s_instance = null;
        base.OnDestroy();
    }

    // Called from AndroidLocationService when the controller recomputes the cadence for a fix. We stash the
    // request statically (so it survives a service restart) and, if we're the live instance, apply it now.
    public static void ApplyCadence(long intervalMillis, bool holdWakeLock)
    {
        // Clamp to a sane band: never tighter than 1 s (pointless battery burn) or looser than a minute
        // (we'd risk sailing past the stop between fixes on a fast vehicle).
        s_requestedGpsIntervalMillis = (int)Math.Clamp(intervalMillis, 1000, 60000);
        s_holdWakeLockRequested = holdWakeLock;
        s_instance?.ApplyCadenceInternal();
    }

    private void ApplyCadenceInternal()
    {
        // Wake-lock scoping is cheap and safe to re-evaluate every call.
        UpdateWakeLock(s_holdWakeLockRequested);

        if (!_isStarted || _locationManager is null)
            return;

        var requested = s_requestedGpsIntervalMillis;
        if (requested == _currentGpsIntervalMillis)
            return;

        // Changing the GPS minTime means re-registering. RemoveUpdates drops the listener from both
        // providers, so we re-request both at the new GPS cadence (network keeps its coarse fallback rate).
        _currentGpsIntervalMillis = requested;
        try
        {
            _locationManager.RemoveUpdates(this);
            RequestProviders();
        }
        catch (SecurityException)
        {
            StopSelf();
        }
    }

    public void OnLocationChanged(global::Android.Locations.Location location)
    {
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(location.Time);
        var accuracy = location.HasAccuracy ? location.Accuracy : 0f;
        if (!float.IsFinite(accuracy))
        {
            accuracy = 0f;
        }

        // Drop a malformed fix (NaN/Infinity or off-the-globe coordinates) at the source. A degraded
        // provider in a crowded urban canyon can emit one, and a single bad value downstream would poison
        // the running position estimate and the trip distance for the rest of the trip — so it never
        // leaves this service.
        if (!CrowdedGpsFilter.IsUsableFix(location.Latitude, location.Longitude))
        {
            return;
        }

        // Toss out the shaky fixes before they ever leave this service — a bad cell-tower reading sneaking
        // through here is what used to make the position and distance jump around. (accuracy == 0 just
        // means the provider gave us no estimate, so we trust those.)
        if (accuracy > MaxAcceptableAccuracyMeters)
        {
            return;
        }

        var snapshot = new LocationSnapshot(location.Latitude, location.Longitude, accuracy, timestamp);
        LastKnownLocation = snapshot;

        // Single source of truth: the foreground (HomeController) owns distance computation and
        // the ongoing notification text. This service only publishes the raw fix.
        LocationUpdated?.Invoke(this, snapshot);
    }

    public void OnProviderDisabled(string provider) { }

    public void OnProviderEnabled(string provider) { }

    public void OnStatusChanged(string? provider, [GeneratedEnum] Availability status, Bundle? extras) { }

    private void StartLocationUpdates()
    {
        if (_isStarted)
        {
            return;
        }

        _locationManager = GetSystemService(LocationService) as LocationManager;
        if (_locationManager is null)
        {
            StopSelf();
            return;
        }

        try
        {
            // Pick up whatever cadence the controller last asked for (it persists across a service
            // restart), so a sticky restart resumes at the right polling rate rather than the default.
            _currentGpsIntervalMillis = s_requestedGpsIntervalMillis;
            if (!RequestProviders())
            {
                StopSelf();
                return;
            }

            _isStarted = true;
            s_instance = this;
            // Honour the current wake-lock request rather than blindly grabbing the lock for the whole
            // trip — far from the stop the controller asks us to let the CPU sleep between fixes.
            UpdateWakeLock(s_holdWakeLockRequested);
        }
        catch (SecurityException)
        {
            StopSelf();
        }
    }

    // Registers both providers at the current cadence. Returns false if neither provider is available,
    // which the caller treats as "nothing to track" and stops the service.
    private bool RequestProviders()
    {
        if (_locationManager is null)
            return false;

        var hasProvider = false;
        // Prefer the GPS provider — it's the fine/precise hardware source. The network provider is
        // only a coarse fallback for when GPS hasn't locked yet (indoors, tunnels, cold start).
        if (_locationManager.IsProviderEnabled(LocationManager.GpsProvider))
        {
            _locationManager.RequestLocationUpdates(
                LocationManager.GpsProvider, _currentGpsIntervalMillis, MinDistanceMetersGps, this);
            hasProvider = true;
        }

        if (_locationManager.IsProviderEnabled(LocationManager.NetworkProvider))
        {
            _locationManager.RequestLocationUpdates(
                LocationManager.NetworkProvider, NetworkUpdateIntervalMillis, MinDistanceMetersNetwork, this);
            hasProvider = true;
        }

        return hasProvider;
    }

    private void StopLocationUpdates()
    {
        if (!_isStarted || _locationManager is null)
        {
            return;
        }

        _locationManager.RemoveUpdates(this);
        _locationManager = null;
        _isStarted = false;
        LastKnownLocation = null;
        ReleaseWakeLock();
    }

    private Notification BuildNotification(string contentText)
    {
        var manager = GetSystemService(NotificationService) as NotificationManager;
        if (manager is not null && Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            if (manager.GetNotificationChannel(TrackingChannelId) is null)
            {
                var channel = new NotificationChannel(
                    TrackingChannelId,
                    "Alarma Trip Tracking",
                    NotificationImportance.Low)
                {
                    Description = "Background location tracking for active trips"
                };
                manager.CreateNotificationChannel(channel);
            }
        }

        var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Notification.Builder(this, TrackingChannelId)
            : new Notification.Builder(this);

        return builder
            .SetContentTitle("Alarma tracking active")
            .SetContentText(contentText)
            .SetSmallIcon(AndroidResource.Drawable.IcDialogMap)  // Fixed: explicit alias
            .SetOngoing(true)
            .Build();
    }

    // Scope the partial wake lock to when it actually earns its battery cost. Holding it for an entire
    // 1–2 hour commute needlessly drains a budget phone; the foreground service keeps receiving fixes
    // regardless, and the controller only asks us to hold the lock once the rider is near the stop, where
    // we can't afford the CPU to nap between a fix and processing it. Far away, we let it sleep.
    private void UpdateWakeLock(bool shouldHold)
    {
        if (shouldHold)
            AcquireWakeLock();
        else
            ReleaseWakeLock();
    }

    private void AcquireWakeLock()
    {
        if (_wakeLock?.IsHeld == true) return;
        var pm = GetSystemService(PowerService) as PowerManager;
        _wakeLock = pm?.NewWakeLock(WakeLockFlags.Partial, "AlarmaApp:LocationTracking");
        _wakeLock?.Acquire();
    }

    private void ReleaseWakeLock()
    {
        if (_wakeLock?.IsHeld == true)
            _wakeLock.Release();
        _wakeLock = null;
    }
}