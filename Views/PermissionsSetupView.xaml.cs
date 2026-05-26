// Security Considerations (OWASP Top 10)
// A01 Broken Access Control: HasCompletedPermissionsSetup is set only after the user
//   explicitly taps Continue or Skip — not on any intermediate row tap. This prevents
//   the flag from being set before the user has consciously acknowledged the screen.
// A04 Insecure Design: Each permission row tap requests only that specific permission;
//   denial opens OS App Settings for the user to manually grant. No silent failures.

using AlarmaApp.Services;
using Microsoft.Maui.ApplicationModel;

namespace AlarmaApp.Views;

public partial class PermissionsSetupView : ContentPage
{
    private readonly PermissionsService _permissionsService;
    private readonly PreferencesService _preferencesService;
    private bool _finishing;

    public PermissionsSetupView(PermissionsService permissionsService, PreferencesService preferencesService)
    {
        _permissionsService = permissionsService;
        _preferencesService = preferencesService;
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        RootGrid.Opacity = 0;
        base.OnAppearing();
        await RefreshPermissionSwitchesAsync();
        await RootGrid.FadeTo(1, 400, Easing.CubicOut);
    }

    private async Task RefreshPermissionSwitchesAsync()
    {
        NotifSwitch.IsToggled =
            await Permissions.CheckStatusAsync<PostNotificationsPermission>() == PermissionStatus.Granted;

        LocationSwitch.IsToggled =
            await Permissions.CheckStatusAsync<Permissions.LocationAlways>() == PermissionStatus.Granted;

#if ANDROID
        try
        {
            var btAdapter = Android.Bluetooth.BluetoothAdapter.DefaultAdapter;
            BtSwitch.IsToggled = btAdapter?.IsEnabled == true;
        }
        catch { }

        try
        {
            if (Android.App.Application.Context.GetSystemService(Android.Content.Context.PowerService)
                is Android.OS.PowerManager pm)
            {
                BatterySwitch.IsToggled =
                    pm.IsIgnoringBatteryOptimizations(Android.App.Application.Context.PackageName);
            }
        }
        catch { }
#endif
        await Task.CompletedTask;
    }

    private async void OnNotificationsTapped(object? sender, TappedEventArgs e)
    {
        StatusLabel.Text = "Requesting notifications permission…";
        var granted = await _permissionsService.EnsureNotificationPermissionAsync();
        NotifSwitch.IsToggled = granted;
        StatusLabel.Text = granted ? "Notifications: granted." : "Notifications: denied. Tap to open Settings.";
    }

    private async void OnLocationTapped(object? sender, TappedEventArgs e)
    {
        StatusLabel.Text = "Requesting location permission…";
        var granted = await _permissionsService.EnsureLocationPermissionsAsync(requireBackground: true);
        LocationSwitch.IsToggled = granted;
        StatusLabel.Text = granted ? "Location: granted." : "Location: denied. Tap to open Settings.";
    }

    private void OnBluetoothTapped(object? sender, TappedEventArgs e)
    {
#if ANDROID
        try
        {
            var intent = new Android.Content.Intent(Android.Provider.Settings.ActionBluetoothSettings);
            intent.SetFlags(Android.Content.ActivityFlags.NewTask);
            Android.App.Application.Context.StartActivity(intent);
            StatusLabel.Text = "Bluetooth: enable in Settings, then return here.";
        }
        catch
        {
            StatusLabel.Text = "Could not open Bluetooth settings.";
        }
#endif
    }

    private async void OnBatteryTapped(object? sender, TappedEventArgs e)
    {
#if ANDROID
        try
        {
            var pm = Android.App.Application.Context.GetSystemService(Android.Content.Context.PowerService)
                as Android.OS.PowerManager;
            if (pm?.IsIgnoringBatteryOptimizations(Android.App.Application.Context.PackageName) == true)
            {
                BatterySwitch.IsToggled = true;
                StatusLabel.Text = "Battery optimization: already ignored.";
                return;
            }

            StatusLabel.Text = "Requesting battery optimization exemption…";
            var intent = new Android.Content.Intent(
                Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations,
                Android.Net.Uri.Parse($"package:{Android.App.Application.Context.PackageName}"));
            intent.SetFlags(Android.Content.ActivityFlags.NewTask);
            Android.App.Application.Context.StartActivity(intent);
            StatusLabel.Text = "Battery: allow exemption in the dialog, then return here.";
        }
        catch
        {
            StatusLabel.Text = "Could not request battery exemption.";
        }
        await Task.CompletedTask;
#endif
    }

    private async void OnContinueClicked(object? sender, EventArgs e) => await FinishAsync();

    private async void OnSkipTapped(object? sender, TappedEventArgs e) => await FinishAsync();

    private async Task FinishAsync()
    {
        if (_finishing) return;
        _finishing = true;
        _preferencesService.HasCompletedPermissionsSetup = true;
        await RootGrid.FadeTo(0, 220, Easing.CubicIn);
        await Shell.Current.GoToAsync("//home", animate: false);
    }
}
