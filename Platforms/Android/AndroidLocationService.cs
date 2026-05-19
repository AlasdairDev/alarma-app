using System.Diagnostics;
using System.Security;
using AlarmaApp.Models;
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

    public Task<LocationSnapshot?> GetLastKnownLocationAsync()
    {
        if (LocationTrackingService.LastKnownLocation is not null)
        {
            return Task.FromResult<LocationSnapshot?>(LocationTrackingService.LastKnownLocation);
        }

        var manager = AndroidApplication.Context.GetSystemService(Context.LocationService) as LocationManager;
        if (manager is null)
        {
            return Task.FromResult<LocationSnapshot?>(null);
        }

        AndroidLocation? bestLocation = null;
        try
        {
            foreach (var provider in manager.GetProviders(true) ?? new List<string>())
            {
                var location = manager.GetLastKnownLocation(provider);
                if (location is null)
                {
                    continue;
                }

                if (bestLocation is null || location.Time > bestLocation.Time)
                {
                    bestLocation = location;
                }
            }
        }
        catch (SecurityException)
        {
            System.Diagnostics.Debug.WriteLine("Location permission denied when reading last known location.");
            return Task.FromResult<LocationSnapshot?>(null);
        }

        if (bestLocation is null)
        {
            return Task.FromResult<LocationSnapshot?>(null);
        }

        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(bestLocation.Time);
        var accuracy = bestLocation.HasAccuracy ? bestLocation.Accuracy : 0f;
        var snapshot = new LocationSnapshot(bestLocation.Latitude, bestLocation.Longitude, accuracy, timestamp);
        return Task.FromResult<LocationSnapshot?>(snapshot);
    }

    private void OnLocationUpdated(object? sender, LocationSnapshot snapshot)
    {
        LocationUpdated?.Invoke(this, snapshot);
    }
}