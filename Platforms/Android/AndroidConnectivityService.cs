// Security Considerations (OWASP Top 10)
// A05 Security Misconfiguration: Captive-portal WiFi (hotel, airport) is explicitly rejected
//   by checking NetCapability.CaptivePortal — when Android detects a login-wall it sets this
//   flag before the user authenticates, so geocoding queries are never sent to the portal
//   operator in plaintext. Previously the check required NetCapability.Validated, which is
//   correct on production devices but causes false-negatives on Android emulators where
//   Google's connectivitycheck.gstatic.com probe fails even though real internet is reachable.
//   The revised logic: INTERNET required + CaptivePortal absent → connection allowed. This
//   covers (1) validated production networks, (2) emulator networks that lack the Validated
//   flag, and (3) correctly blocks any network the OS has confirmed is a captive portal.

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
