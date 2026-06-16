// Hands a reroute off to Google Maps. Small details that matter:
//   - We format lat/lon with InvariantCulture "F6" so a phone set to a locale that uses commas for
//     decimals can't produce a broken google.navigation:q=LAT,LON and quietly send the rider to the
//     wrong spot.
//   - The Intent is locked to com.google.android.apps.maps by exact package via SetPackage(), and the
//     fallback is a plain geo: URI — never a URL we build from user input. This class makes no HTTP
//     calls at all; it just delegates to whatever Maps app is installed.
//   - We ResolveActivity() before StartActivity() so the app doesn't crash if Maps isn't installed;
//     the geo: fallback then covers any generic maps app.

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
