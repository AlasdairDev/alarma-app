using AlarmaApp.Services.Interfaces;
using Android.Content;
using Android.OS;
using Android.Provider;
using AndroidApplication = Android.App.Application;

namespace AlarmaApp.Platforms.Android;

public class AndroidBatteryOptimizationService : IBatteryOptimizationService
{
    public bool IsIgnoringOptimizations()
    {
        var manager = AndroidApplication.Context.GetSystemService(Context.PowerService) as PowerManager;
        if (manager is null)
        {
            return false;
        }

        return manager.IsIgnoringBatteryOptimizations(AndroidApplication.Context.PackageName);
    }

    public Task RequestIgnoreOptimizationsAsync()
    {
        var intent = new Intent(Settings.ActionRequestIgnoreBatteryOptimizations);
        intent.SetData(global::Android.Net.Uri.Parse($"package:{AndroidApplication.Context.PackageName}"));
        intent.AddFlags(ActivityFlags.NewTask);
        AndroidApplication.Context.StartActivity(intent);
        return Task.CompletedTask;
    }
}
