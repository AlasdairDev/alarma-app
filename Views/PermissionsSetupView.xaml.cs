// The first-run permissions screen. We only mark HasCompletedPermissionsSetup once the user actually
// taps Continue or Skip — never on some intermediate row tap or toggle — so the gate can't be tripped
// by accident. Each switch requests just its own permission, and if it's denied the switch snaps back
// to OFF with a visible message rather than failing silently.

using AlarmaApp.Services;
using AlarmaApp.Services.Interfaces;
using Microsoft.Maui.ApplicationModel;

namespace AlarmaApp.Views;

public partial class PermissionsSetupView : ContentPage
{
    private readonly PermissionsService _permissionsService;
    private readonly PreferencesService _preferencesService;
    private readonly ILocationService _locationService;
    private bool _finishing;
    private bool _suppressToggle;

    public PermissionsSetupView(
        PermissionsService permissionsService,
        PreferencesService preferencesService,
        ILocationService locationService)
    {
        _permissionsService = permissionsService;
        _preferencesService = preferencesService;
        _locationService = locationService;
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
        if (_suppressToggle) return;
        if (!e.Value)
        {
            // Toggling OFF = the user wants to revoke. We can't drop the grant ourselves, so send
            // them to the OS page; OnAppearing re-reads the real status and re-syncs the switch.
            await HandlePermissionRevokeAsync("Notifications", stopTracking: false);
            return;
        }
        NotifSwitch.IsEnabled = false;
        StatusLabel.Text = "Requesting notifications permission…";
        var granted = await _permissionsService.EnsureNotificationPermissionAsync();
        _suppressToggle = true;
        NotifSwitch.IsToggled = granted;
        _suppressToggle = false;
        NotifSwitch.IsEnabled = true;
        StatusLabel.Text = granted
            ? "Notifications: granted"
            : "Notifications: denied. Tap the row to open Settings.";
    }

    private async void OnLocationSwitchToggled(object? sender, ToggledEventArgs e)
    {
        if (_suppressToggle) return;
        if (!e.Value)
        {
            // Revoking location: also tear down any live tracking so a foreground service +
            // wake lock can't outlive the permission the user is dropping.
            await HandlePermissionRevokeAsync("Location", stopTracking: true);
            return;
        }
        LocationSwitch.IsEnabled = false;
        StatusLabel.Text = "Requesting location permission…";
        var granted = await _permissionsService.EnsureLocationPermissionsAsync(requireBackground: true);
        _suppressToggle = true;
        LocationSwitch.IsToggled = granted;
        _suppressToggle = false;
        LocationSwitch.IsEnabled = true;
        StatusLabel.Text = granted
            ? "Location (Always): granted"
            : "Location: denied. Tap the row to open Settings.";
    }

    // Shared OFF-path: clean up local state for the permission being dropped, then hand the user
    // to App Settings (the only place Android lets a runtime permission actually be revoked).
    private async Task HandlePermissionRevokeAsync(string label, bool stopTracking)
    {
        if (stopTracking)
        {
            try { await _locationService.StopTrackingAsync(); }
            catch (Exception ex) { BlackBoxLogger.RecordHandledException(ex, "[PermissionsSetupView.HandlePermissionRevokeAsync]"); }
        }

        StatusLabel.Text = $"{label}: opening Settings so you can revoke access…";
        PermissionsService.OpenAppSettings();
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
            ? "Notifications: granted"
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
            ? "Location (Always): granted"
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
                StatusLabel.Text = "Battery optimization: already exempted";
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
