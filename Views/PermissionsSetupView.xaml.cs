// Security Considerations (OWASP Top 10)
// A01 Broken Access Control: HasCompletedPermissionsSetup is set only after the user
//   explicitly taps Continue or Skip — not on any intermediate row tap or toggle.
// A04 Insecure Design: Each toggle requests only that specific permission; denial
//   immediately resets the switch to OFF and surfaces an error — no silent failures.

using AlarmaApp.Services;
using Microsoft.Maui.ApplicationModel;

namespace AlarmaApp.Views;

public partial class PermissionsSetupView : ContentPage
{
    private readonly PermissionsService _permissionsService;
    private readonly PreferencesService _preferencesService;
    private bool _finishing;
    private bool _suppressToggle;

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
        _suppressToggle = true;
        try
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
                    BatterySwitch.IsToggled =
                        pm.IsIgnoringBatteryOptimizations(Android.App.Application.Context.PackageName);
            }
            catch { }
#endif
        }
        finally
        {
            _suppressToggle = false;
        }
    }

    // ── Switch Toggled handlers ───────────────────────────────────────────

    private async void OnNotificationSwitchToggled(object? sender, ToggledEventArgs e)
    {
        if (_suppressToggle || !e.Value) return;
        NotifSwitch.IsEnabled = false;
        StatusLabel.Text = "Requesting notifications permission…";
        var granted = await _permissionsService.EnsureNotificationPermissionAsync();
        _suppressToggle = true;
        NotifSwitch.IsToggled = granted;
        _suppressToggle = false;
        NotifSwitch.IsEnabled = true;
        StatusLabel.Text = granted
            ? "Notifications: granted ✓"
            : "Notifications: denied. Tap the row to open Settings.";
    }

    private async void OnLocationSwitchToggled(object? sender, ToggledEventArgs e)
    {
        if (_suppressToggle || !e.Value) return;
        LocationSwitch.IsEnabled = false;
        StatusLabel.Text = "Requesting location permission…";
        var granted = await _permissionsService.EnsureLocationPermissionsAsync(requireBackground: true);
        _suppressToggle = true;
        LocationSwitch.IsToggled = granted;
        _suppressToggle = false;
        LocationSwitch.IsEnabled = true;
        StatusLabel.Text = granted
            ? "Location (Always): granted ✓"
            : "Location: denied. Tap the row to open Settings.";
    }

    private void OnBluetoothSwitchToggled(object? sender, ToggledEventArgs e)
    {
        if (_suppressToggle) return;
        if (e.Value) OnBluetoothTapped(sender, null!);
        else
        {
            _suppressToggle = true;
            BtSwitch.IsToggled = false;
            _suppressToggle = false;
        }
    }

    private void OnBatterySwitchToggled(object? sender, ToggledEventArgs e)
    {
        if (_suppressToggle) return;
        if (e.Value) _ = TriggerBatteryExemptionAsync();
    }

    // ── Row tap handlers (row tap == same logic as the switch) ────────────

    private async void OnNotificationsTapped(object? sender, TappedEventArgs e)
    {
        if (NotifSwitch.IsToggled)
        {
            StatusLabel.Text = "Notifications already granted.";
            return;
        }
        StatusLabel.Text = "Requesting notifications permission…";
        var granted = await _permissionsService.EnsureNotificationPermissionAsync();
        _suppressToggle = true;
        NotifSwitch.IsToggled = granted;
        _suppressToggle = false;
        StatusLabel.Text = granted
            ? "Notifications: granted ✓"
            : "Notifications: denied. Opening Settings…";
    }

    private async void OnLocationTapped(object? sender, TappedEventArgs e)
    {
        if (LocationSwitch.IsToggled)
        {
            StatusLabel.Text = "Location already granted.";
            return;
        }
        StatusLabel.Text = "Requesting location permission…";
        var granted = await _permissionsService.EnsureLocationPermissionsAsync(requireBackground: true);
        _suppressToggle = true;
        LocationSwitch.IsToggled = granted;
        _suppressToggle = false;
        StatusLabel.Text = granted
            ? "Location (Always): granted ✓"
            : "Location: denied. Opening Settings…";
    }

    private void OnBluetoothTapped(object? sender, TappedEventArgs e)
    {
#if ANDROID
        try
        {
            var intent = new Android.Content.Intent(Android.Provider.Settings.ActionBluetoothSettings);
            intent.SetFlags(Android.Content.ActivityFlags.NewTask);
            Android.App.Application.Context.StartActivity(intent);
            StatusLabel.Text = "Enable Bluetooth in Settings, then return here.";
        }
        catch
        {
            StatusLabel.Text = "Could not open Bluetooth settings.";
        }
#endif
    }

    private async void OnBatteryTapped(object? sender, TappedEventArgs e)
        => await TriggerBatteryExemptionAsync();

    private async Task TriggerBatteryExemptionAsync()
    {
#if ANDROID
        try
        {
            var pm = Android.App.Application.Context.GetSystemService(Android.Content.Context.PowerService)
                as Android.OS.PowerManager;
            if (pm?.IsIgnoringBatteryOptimizations(Android.App.Application.Context.PackageName) == true)
            {
                _suppressToggle = true;
                BatterySwitch.IsToggled = true;
                _suppressToggle = false;
                StatusLabel.Text = "Battery optimization: already exempted ✓";
                return;
            }

            StatusLabel.Text = "Requesting battery optimization exemption…";
            var intent = new Android.Content.Intent(
                Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations,
                Android.Net.Uri.Parse($"package:{Android.App.Application.Context.PackageName}"));
            intent.SetFlags(Android.Content.ActivityFlags.NewTask);
            Android.App.Application.Context.StartActivity(intent);
            StatusLabel.Text = "Allow exemption in the dialog, then return here.";
        }
        catch
        {
            StatusLabel.Text = "Could not request battery exemption.";
            _suppressToggle = true;
            BatterySwitch.IsToggled = false;
            _suppressToggle = false;
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
