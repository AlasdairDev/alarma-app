using AlarmaApp.Services.Interfaces;
using Android.Content;
using AndroidApplication = Android.App.Application;

namespace AlarmaApp.Platforms.Android;

public class AndroidGoogleMapsLauncher : IGoogleMapsLauncher
{
    public Task OpenRerouteAsync(double latitude, double longitude)
    {
        var lat = latitude.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        var lon = longitude.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);

        var context = AndroidApplication.Context;
        var mapsUri = global::Android.Net.Uri.Parse($"google.navigation:q={lat},{lon}");
        var mapsIntent = new Intent(Intent.ActionView, mapsUri);
        mapsIntent.SetPackage("com.google.android.apps.maps");
        mapsIntent.AddFlags(ActivityFlags.NewTask);

        if (mapsIntent.ResolveActivity(context.PackageManager) is not null)
        {
            context.StartActivity(mapsIntent);
            return Task.CompletedTask;
        }

        var fallbackUri = global::Android.Net.Uri.Parse($"geo:{lat},{lon}?q={lat},{lon}");
        var fallbackIntent = new Intent(Intent.ActionView, fallbackUri);
        fallbackIntent.AddFlags(ActivityFlags.NewTask);
        if (fallbackIntent.ResolveActivity(context.PackageManager) is not null)
        {
            context.StartActivity(fallbackIntent);
            return Task.CompletedTask;
        }

        throw new InvalidOperationException("No compatible maps application found.");
    }
}
