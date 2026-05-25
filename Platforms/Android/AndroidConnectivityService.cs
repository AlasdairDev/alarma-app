// Security Considerations (OWASP Top 10)
// A05 Security Misconfiguration: NetCapability.Validated is checked alongside NetCapability.Internet
//   to detect real internet access. Without the Validated check, a captive-portal WiFi (hotel,
//   airport) would be reported as "online", causing destination search requests to hit the portal
//   login page instead of Photon/Nominatim — a potential information disclosure vector (query
//   terms sent in plaintext to an untrusted network operator). The Validated check ensures the OS
//   has confirmed reachability to the open internet before geocoding requests are issued.

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
        if (manager is null)
        {
            return false;
        }

        var network = manager.ActiveNetwork;
        if (network is null)
        {
            return false;
        }

        var capabilities = manager.GetNetworkCapabilities(network);
        return capabilities?.HasCapability(NetCapability.Internet) == true
            && capabilities.HasCapability(NetCapability.Validated);
    }
}
