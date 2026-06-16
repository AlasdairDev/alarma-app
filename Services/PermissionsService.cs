using Microsoft.Maui.ApplicationModel;

#if ANDROID
using Android.App;
using Android.Content;
using Android.Provider;
using AndroidApplication = Android.App.Application;
#endif

namespace AlarmaApp.Services;

public class PermissionsService
{
    public async Task<bool> EnsureLocationPermissionsAsync(bool requireBackground)
    {
        var status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
        if (status != PermissionStatus.Granted)
        {
            status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
        }

        if (status == PermissionStatus.Denied)
        {
            OpenAppSettings();
            return false;
        }

        if (status != PermissionStatus.Granted)
        {
            return false;
        }

        if (!requireBackground)
        {
            return true;
        }

        var backgroundStatus = await Permissions.CheckStatusAsync<Permissions.LocationAlways>();
        if (backgroundStatus != PermissionStatus.Granted)
        {
            backgroundStatus = await Permissions.RequestAsync<Permissions.LocationAlways>();
        }

        if (backgroundStatus == PermissionStatus.Denied)
        {
            OpenAppSettings();
            return false;
        }

        return backgroundStatus == PermissionStatus.Granted;
    }

    public async Task<bool> EnsureSmsPermissionAsync()
    {
        var status = await Permissions.CheckStatusAsync<SmsPermission>();
        if (status != PermissionStatus.Granted)
        {
            status = await Permissions.RequestAsync<SmsPermission>();
        }

        if (status == PermissionStatus.Denied)
        {
            OpenAppSettings();
            return false;
        }

        return status == PermissionStatus.Granted;
    }

    public async Task<bool> EnsureNotificationPermissionAsync()
    {
        var status = await Permissions.CheckStatusAsync<PostNotificationsPermission>();
        if (status != PermissionStatus.Granted)
        {
            status = await Permissions.RequestAsync<PostNotificationsPermission>();
        }

        if (status == PermissionStatus.Denied)
        {
            OpenAppSettings();
            return false;
        }

        return status == PermissionStatus.Granted;
    }

    // Public so the permission switches can route a user to the OS page when they want to revoke —
    // Android offers no API to drop a runtime permission programmatically; only the user can, here.
    public static void OpenAppSettings()
    {
#if ANDROID
        try
        {
            var intent = new Intent(Settings.ActionApplicationDetailsSettings);
            intent.SetData(Android.Net.Uri.Parse(
                $"package:{AndroidApplication.Context.PackageName}"));
            intent.AddFlags(ActivityFlags.NewTask);
            AndroidApplication.Context.StartActivity(intent);
        }
        catch { }
#endif
    }

    // Opens the device-wide Location source settings (not the app page) so the user can flip the
    // master location switch back on after we block a trip start on it.
    public static void OpenLocationSettings()
    {
#if ANDROID
        try
        {
            var intent = new Intent(Settings.ActionLocationSourceSettings);
            intent.AddFlags(ActivityFlags.NewTask);
            AndroidApplication.Context.StartActivity(intent);
        }
        catch { }
#endif
    }
}

public class SmsPermission : Permissions.BasePlatformPermission
{
#if ANDROID
    public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
        new[] { (Android.Manifest.Permission.SendSms, true) };
#else
    public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
        Array.Empty<(string, bool)>();
#endif
}

public class PostNotificationsPermission : Permissions.BasePlatformPermission
{
#if ANDROID
    public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
        Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.Tiramisu
            ? new[] { (Android.Manifest.Permission.PostNotifications, true) }
            : Array.Empty<(string, bool)>();
#else
    public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
        Array.Empty<(string, bool)>();
#endif
}
