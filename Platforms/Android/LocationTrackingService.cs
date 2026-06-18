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
    // provider — a fresher fix every couple of seconds keeps the live dot accurate and gives the
    // map's interpolation something recent to glide toward.
    private const long GpsUpdateIntervalMillis = 2000;
    private const long NetworkUpdateIntervalMillis = 5000;
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
        base.OnDestroy();
    }

    public void OnLocationChanged(global::Android.Locations.Location location)
    {
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(location.Time);
        var accuracy = location.HasAccuracy ? location.Accuracy : 0f;

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
            var hasProvider = false;
            // Prefer the GPS provider — it's the fine/precise hardware source. The network provider is
            // only a coarse fallback for when GPS hasn't locked yet (indoors, tunnels, cold start).
            if (_locationManager.IsProviderEnabled(LocationManager.GpsProvider))
            {
                _locationManager.RequestLocationUpdates(LocationManager.GpsProvider, GpsUpdateIntervalMillis, MinDistanceMetersGps, this);
                hasProvider = true;
            }

            if (_locationManager.IsProviderEnabled(LocationManager.NetworkProvider))
            {
                _locationManager.RequestLocationUpdates(LocationManager.NetworkProvider, NetworkUpdateIntervalMillis, MinDistanceMetersNetwork, this);
                hasProvider = true;
            }

            if (!hasProvider)
            {
                StopSelf();
                return;
            }

            _isStarted = true;
            AcquireWakeLock();
        }
        catch (SecurityException)
        {
            StopSelf();
        }
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