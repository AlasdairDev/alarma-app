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
