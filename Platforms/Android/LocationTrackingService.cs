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
using System.Globalization;
using System.Security;

// Alias to avoid ambiguity between AlarmaApp.Resource and Android.Resource
using AndroidResource = Android.Resource;

namespace AlarmaApp.Platforms.Android;

[Service(
    Exported = false,
    ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeLocation)]
public class LocationTrackingService : Service, ILocationListener
{
    private const int NotificationId = 1001;
    private const string ChannelId = "alarma_location_tracking";
    private const long LocationUpdateIntervalMillis = 5000;
    private const float MinDistanceMetersGps = 5f;
    private const float MinDistanceMetersNetwork = 10f;
    private const double EarthRadiusMeters = 6_371_000;
    private LocationManager? _locationManager;
    private PowerManager.WakeLock? _wakeLock;
    private bool _isStarted;
    private LocationSnapshot? _lastLocation;
    private double _totalDistanceMeters;

    public static event EventHandler<LocationSnapshot>? LocationUpdated;

    public static LocationSnapshot? LastKnownLocation { get; private set; }

    public override void OnCreate()
    {
        base.OnCreate();
        StartForeground(NotificationId, BuildNotification("Tracking trip location in the background."));
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
        var snapshot = new LocationSnapshot(location.Latitude, location.Longitude, accuracy, timestamp);
        if (_lastLocation is not null)
        {
            _totalDistanceMeters += CalculateDistanceMeters(_lastLocation, snapshot);
        }

        _lastLocation = snapshot;
        LastKnownLocation = snapshot;
        LocationUpdated?.Invoke(this, snapshot);
        UpdateNotification(snapshot);
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

            _lastLocation = null;
            _totalDistanceMeters = 0;
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
        _lastLocation = null;
        _totalDistanceMeters = 0;
        LastKnownLocation = null;
        ReleaseWakeLock();
    }

    private void UpdateNotification(LocationSnapshot snapshot)
    {
        var manager = GetSystemService(NotificationService) as NotificationManager;
        if (manager is null)
        {
            return;
        }

        var distanceKm = _totalDistanceMeters / 1000.0;
        var timestamp = snapshot.Timestamp.ToLocalTime().ToString("HH:mm zzz", CultureInfo.InvariantCulture);
        var contentText = $"Tracking active · {distanceKm:F2} km · last fix {timestamp}.";
        manager.Notify(NotificationId, BuildNotification(contentText));
    }

    private Notification BuildNotification(string contentText)
    {
        var manager = GetSystemService(NotificationService) as NotificationManager;
        if (manager is not null && Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            if (manager.GetNotificationChannel(ChannelId) is null)
            {
                var channel = new NotificationChannel(
                    ChannelId,
                    "Alarma Trip Tracking",
                    NotificationImportance.Low)
                {
                    Description = "Background location tracking for active trips"
                };
                manager.CreateNotificationChannel(channel);
            }
        }

        var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Notification.Builder(this, ChannelId)
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

    private static double CalculateDistanceMeters(LocationSnapshot start, LocationSnapshot end)
    {
        var lat1 = DegreesToRadians(start.Latitude);
        var lat2 = DegreesToRadians(end.Latitude);
        var deltaLat = DegreesToRadians(end.Latitude - start.Latitude);
        var deltaLon = DegreesToRadians(end.Longitude - start.Longitude);

        var sinHalfDeltaLat = Math.Sin(deltaLat / 2);
        var sinHalfDeltaLon = Math.Sin(deltaLon / 2);
        var a = sinHalfDeltaLat * sinHalfDeltaLat
                + Math.Cos(lat1) * Math.Cos(lat2)
                * sinHalfDeltaLon * sinHalfDeltaLon;
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return EarthRadiusMeters * c;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;
}