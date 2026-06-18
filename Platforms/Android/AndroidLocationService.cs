// The .NET-facing wrapper around our Android location plumbing. The main thing to watch here is
// that the user can revoke location permission from Settings while we're in the background — so if
// GetLastKnownLocationAsync hits a SecurityException we just return null instead of crashing, and
// HomeController null-checks before it builds the SOS message. Like the service itself, this layer
// never saves or logs a coordinate; it only hands LocationSnapshot value objects to its subscribers.
// No credentials, no personal data, no network calls live here.

using System.Diagnostics;
using System.Security;
using AlarmaApp.Models;
using AlarmaApp.Services;
using AlarmaApp.Services.Interfaces;
using Android.Content;
using Android.Locations;
using Android.OS;
using AndroidApplication = Android.App.Application;
using AndroidLocation = Android.Locations.Location;

namespace AlarmaApp.Platforms.Android;

public class AndroidLocationService : ILocationService
{
    public event EventHandler<LocationSnapshot>? LocationUpdated;

    public bool IsTracking { get; private set; }

    public bool IsLocationServiceEnabled()
    {
        var manager = AndroidApplication.Context.GetSystemService(Context.LocationService) as LocationManager;
        if (manager is null)
        {
            return false;
        }

        // API 28+ exposes a single device-wide flag; older devices fall back to checking the
        // individual providers. Either GPS or network being on is enough for us to get fixes.
        if (Build.VERSION.SdkInt >= BuildVersionCodes.P)
        {
            return manager.IsLocationEnabled;
        }

        try
        {
            return manager.IsProviderEnabled(LocationManager.GpsProvider)
                || manager.IsProviderEnabled(LocationManager.NetworkProvider);
        }
        catch
        {
            return false;
        }
    }

    public Task StartTrackingAsync(CancellationToken cancellationToken)
    {
        if (IsTracking)
        {
            return Task.CompletedTask;
        }

        IsTracking = true;
        LocationTrackingService.LocationUpdated += OnLocationUpdated;
        var context = AndroidApplication.Context;
        var intent = new Intent(context, typeof(LocationTrackingService));
        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                context.StartForegroundService(intent);
            }
            else
            {
                context.StartService(intent);
            }

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            LocationTrackingService.LocationUpdated -= OnLocationUpdated;
            IsTracking = false;
            throw new InvalidOperationException("Failed to start tracking service.", ex);
        }
    }

    public Task StopTrackingAsync()
    {
        if (!IsTracking)
        {
            return Task.CompletedTask;
        }

        IsTracking = false;
        LocationTrackingService.LocationUpdated -= OnLocationUpdated;
        var context = AndroidApplication.Context;
        var intent = new Intent(context, typeof(LocationTrackingService));
        try
        {
            context.StopService(intent);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to stop tracking service.", ex);
        }
        return Task.CompletedTask;
    }

    public async Task<LocationSnapshot?> GetLastKnownLocationAsync()
    {
        if (LocationTrackingService.LastKnownLocation is not null)
            return LocationTrackingService.LastKnownLocation;

        var manager = AndroidApplication.Context.GetSystemService(Context.LocationService) as LocationManager;
        if (manager is not null)
        {
            AndroidLocation? bestLocation = null;
            try
            {
                foreach (var provider in manager.GetProviders(true) ?? new List<string>())
                {
                    var location = manager.GetLastKnownLocation(provider);
                    if (location is null) continue;
                    if (bestLocation is null || location.Time > bestLocation.Time)
                        bestLocation = location;
                }
            }
            catch (SecurityException ex)
            {
                BlackBoxLogger.RecordHandledException(ex, "[AndroidLocationService.GetLastKnownLocationAsync.PermissionDenied]");
            }

            if (bestLocation is not null)
            {
                var ts = DateTimeOffset.FromUnixTimeMilliseconds(bestLocation.Time);
                var acc = bestLocation.HasAccuracy ? bestLocation.Accuracy : 0f;
                return new LocationSnapshot(bestLocation.Latitude, bestLocation.Longitude, acc, ts);
            }
        }

        try
        {
            var loc = await Microsoft.Maui.Devices.Sensors.Geolocation.GetLastKnownLocationAsync();
            if (loc is not null)
                return new LocationSnapshot(loc.Latitude, loc.Longitude, (float)(loc.Accuracy ?? 0), loc.Timestamp);
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[AndroidLocationService.GetLastKnownLocationAsync.MauiGetLastKnown]");
        }

        try
        {
            // Ask the platform for the finest fix it can manage — this is the commuter's live dot, so a
            // coarse "good enough" estimate isn't good enough. Best leans on GPS/fused hardware rather
            // than a cell-tower guess. We give it a slightly longer window since a precise fix can take a
            // moment to settle, especially on a cold start.
            var request = new Microsoft.Maui.Devices.Sensors.GeolocationRequest(
                Microsoft.Maui.Devices.Sensors.GeolocationAccuracy.Best,
                TimeSpan.FromSeconds(10))
            {
                RequestFullAccuracy = true
            };
            var loc = await Microsoft.Maui.Devices.Sensors.Geolocation.GetLocationAsync(request);
            if (loc is not null)
                return new LocationSnapshot(loc.Latitude, loc.Longitude, (float)(loc.Accuracy ?? 0), loc.Timestamp);
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[AndroidLocationService.GetLastKnownLocationAsync.MauiGetLocation]");
        }

        return null;
    }

    private void OnLocationUpdated(object? sender, LocationSnapshot snapshot)
    {
        LocationUpdated?.Invoke(this, snapshot);
    }
}