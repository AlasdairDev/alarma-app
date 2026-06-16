// Security Considerations (OWASP Top 10)
// A05 Security Misconfiguration:
//   - Service declared Exported=false — cannot be started or bound by external apps or ADB.
//   - ForegroundServiceType=TypeLocation required by Android 14+ (API 34+); without it the OS
//     kills the service on API 34+ and location tracking silently stops.
// A04 Insecure Design: SecurityException on RequestLocationUpdates causes StopSelf() — the
//   foreground service terminates cleanly rather than running as a zombie that consumes battery
//   without producing location updates.
// No user credentials, PII beyond GPS coordinates, or network calls are made by this service.
// GPS coordinates are only published via the static LocationUpdated event to AndroidLocationService.

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
    private const long LocationUpdateIntervalMillis = 5000;
    private const float MinDistanceMetersGps = 5f;
    private const float MinDistanceMetersNetwork = 10f;
    // Drop fixes less accurate than this (aggressive cell-tower bounce) so they cannot move the
    // live position or corrupt the distance the foreground shows. 0 = provider reported none.
    private const float MaxAcceptableAccuracyMeters = 75f;
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

        // Gate out low-confidence fixes so cell-tower bounce can't move the live position or the
        // distance. accuracy == 0 means the provider reported none → accept.
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
            if (_locationManager.IsProviderEnabled(LocationManager.GpsProvider))
            {
                _locationManager.RequestLocationUpdates(LocationManager.GpsProvider, LocationUpdateIntervalMillis, MinDistanceMetersGps, this);
                hasProvider = true;
            }

            if (_locationManager.IsProviderEnabled(LocationManager.NetworkProvider))
            {
                _locationManager.RequestLocationUpdates(LocationManager.NetworkProvider, LocationUpdateIntervalMillis, MinDistanceMetersNetwork, this);
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