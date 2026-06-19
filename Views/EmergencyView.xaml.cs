// The SOS + emergency-contacts screen. SOS needs a deliberate 2-second hold rather than a tap so it
// can't go off by accident, and we null out _holdTimer when the page disappears so a pending timer
// can't fire after the user has already swiped to another tab. Contact name is capped at 50 chars and
// the phone number runs through HomeController.IsValidPhilippineNumber, so nothing reaches the database
// without clearing the controller's validation first.

using AlarmaApp.Controllers;
using AlarmaApp.Models;
using AlarmaApp.Services;

namespace AlarmaApp.Views;

public partial class EmergencyView : ContentPage
{
    private readonly HomeController _controller;
    private IDispatcherTimer? _holdTimer;

    public EmergencyView(HomeController controller)
    {
        _controller = controller;
        BindingContext = controller;
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        Content.Opacity = 0;
        base.OnAppearing();
        // Start with a clean status line so the inline form feedback shows only contact-related
        // messages produced on this screen, not a stale one carried over from another tab.
        _controller.ClearLastAction();
        Content.FadeTo(1, 220, Easing.CubicOut);
        _controller.SosDispatched += OnSosDispatched;
        _controller.SmsDenied += OnSmsDenied;
        _controller.LocationServicesDisabled += OnLocationServicesDisabled;
        _controller.EmergencyContactValidationFailed += OnContactValidationFailed;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _holdTimer?.Stop();
        _holdTimer = null;
        SosButton.BackgroundColor = Color.FromArgb("#FF3B30");
        _controller.SosDispatched -= OnSosDispatched;
        _controller.SmsDenied -= OnSmsDenied;
        _controller.LocationServicesDisabled -= OnLocationServicesDisabled;
        _controller.EmergencyContactValidationFailed -= OnContactValidationFailed;
    }

    // Contact-form validation errors (especially the phone-number format) now interrupt with a styled
    // modal the user must acknowledge, instead of a quiet inline line that's easy to miss.
    private void OnContactValidationFailed(object? sender, string message)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
            await DisplayAlert("Invalid Contact", message, "OK"));
    }

    // SOS was halted because the device's location/GPS is switched off. Force the issue with an alert
    // that sends the rider straight to the location settings — an SOS without a location is half-blind.
    private void OnLocationServicesDisabled(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            var goToSettings = await DisplayAlert(
                "Location is Off",
                "Turn on your device's location so your SOS can include where you are. Open location settings now?",
                "Open Settings",
                "Cancel");
            if (goToSettings)
                _controller.OpenLocationSettings();
        });
    }

    // Removing a contact is destructive (they stop receiving SOS alerts), so make the user confirm in a
    // modal first. The contact rides in on the gesture's CommandParameter, surfaced as e.Parameter.
    private async void OnRemoveContactTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not EmergencyContact contact) return;

        // Strict confirmation: we await the boolean and only delete on "Yes". "Cancel" aborts entirely.
        var confirm = await DisplayAlert("Confirm", "Are you sure you want to delete this?", "Yes", "Cancel");
        if (!confirm) return;
        _controller.RemoveEmergencyContactCommand.Execute(contact);
    }

    private void OnSosPressed(object? sender, EventArgs e)
    {
        _holdTimer?.Stop();
        _holdTimer = Dispatcher.CreateTimer();
        _holdTimer.Interval = TimeSpan.FromSeconds(2);
        _holdTimer.IsRepeating = false;
        _holdTimer.Tick += OnHoldComplete;
        _holdTimer.Start();
        SosButton.BackgroundColor = Color.FromArgb("#CC1A0F");
    }

    private void OnSosReleased(object? sender, EventArgs e)
    {
        _holdTimer?.Stop();
        _holdTimer = null;
        SosButton.BackgroundColor = Color.FromArgb("#FF3B30");
    }

    private void OnHoldComplete(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _holdTimer = null;
            _controller.TriggerSosCommand.Execute(null);
            SosButton.BackgroundColor = Color.FromArgb("#FF3B30");
        });
    }

    private void OnSosDispatched(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(ShowSosSentOverlay);
    }

    private void OnSmsDenied(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() => SmsDeniedOverlay.IsVisible = true);
    }

    private void OnSmsDeniedOverlayDismissed(object? sender, TappedEventArgs e)
    {
        SmsDeniedOverlay.IsVisible = false;
    }

    private void OnOpenSmsSettingsClicked(object? sender, EventArgs e)
    {
        SmsDeniedOverlay.IsVisible = false;
        try { AppInfo.Current.ShowSettingsUI(); } catch { }
    }

    private void ShowSosSentOverlay()
    {
        // Show the destination name (or fallback) in the overlay location chip
        var dest = _controller.DestinationSummaryText;
        SosSentLocationLabel.Text = string.IsNullOrWhiteSpace(dest) || dest == "Search"
            ? "Current location"
            : dest;
        SosSentOverlay.IsVisible = true;
    }

    private void OnSosSentOverlayDismissed(object? sender, TappedEventArgs e)
    {
        SosSentOverlay.IsVisible = false;
    }

    private async void OnCall911Clicked(object? sender, EventArgs e)
    {
        SosSentOverlay.IsVisible = false;
        try
        {
            // Hand the emergency number straight to the system dialer, pre-filled (the user still taps
            // call). Launcher.OpenAsync("tel:911") resolves via the DIAL/tel <queries> entry we declare
            // in the manifest, so Android 11+ package-visibility can't hide the dialer from us. It
            // returns false when nothing can handle the URI (no dialer at all).
            var opened = await Launcher.Default.OpenAsync("tel:911");
            if (!opened)
                await ShowToastAsync("Dialer not found on this device");
        }
        catch (Exception ex)
        {
            // Some tablets / emulators have no dialer activity at all and throw. Surface a transient
            // grey-pill instead of failing silently so the rider knows to call another way.
            BlackBoxLogger.RecordHandledException(ex, "[EmergencyView.OnCall911Clicked]");
            await ShowToastAsync("Dialer not found on this device");
        }
    }

    // Pops the grey pill up with a message, holds it for 3 seconds, then fades it away. The sequence
    // guard means a second trigger mid-display restarts the timer instead of hiding the newer message
    // early.
    private int _toastSeq;
    private async Task ShowToastAsync(string message)
    {
        var seq = ++_toastSeq;
        ToastLabel.Text = message;
        ToastPill.Opacity = 1;
        ToastPill.IsVisible = true;

        await Task.Delay(3000);
        if (seq != _toastSeq) return; // a newer toast took over — let it own the lifecycle

        await ToastPill.FadeTo(0, 250, Easing.CubicIn);
        if (seq != _toastSeq) return;
        ToastPill.IsVisible = false;
    }
}
