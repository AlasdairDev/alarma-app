// Tells the rest of the app whether we actually have usable internet. The tricky part is
// captive-portal WiFi — those hotel/airport "sign in here" networks. Android flags them with
// NetCapability.CaptivePortal before the user logs in, and we reject those so a destination search
// never leaks in plaintext to whoever runs the portal. We first gated on NetCapability.Validated,
// which is right on real phones but gives false negatives on emulators (Google's
// connectivitycheck.gstatic.com probe fails there even when the internet works fine). So the rule we
// landed on is simpler: has INTERNET and is NOT a captive portal → good to go. That keeps real
// networks and emulators working while still blocking any network the OS has confirmed is a login-wall.

using AlarmaApp.Services.Interfaces;
using Android.Content;
using Android.Net;
using AndroidApplication = Android.App.Application;

namespace AlarmaApp.Platforms.Android;

public class AndroidConnectivityService : IConnectivityService
{
    public bool HasInternet()
    {
        var manager = AndroidApplication.Context.GetSystemService(Context.ConnectivityService) as ConnectivityManager;
        if (manager is null) return false;

        var network = manager.ActiveNetwork;
        if (network is null) return false;

        var capabilities = manager.GetNetworkCapabilities(network);
        if (capabilities is null) return false;

        // Must have basic internet routing
        if (!capabilities.HasCapability(NetCapability.Internet)) return false;

        // Explicitly block networks the OS has flagged as captive portals
        // (hotel/airport login walls). API 23+, safe since minSdk=26.
        if (capabilities.HasCapability(NetCapability.CaptivePortal)) return false;

        return true;
    }
}
