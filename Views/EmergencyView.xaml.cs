// The SOS + emergency-contacts screen. SOS needs a deliberate 2-second hold rather than a tap so it
// can't go off by accident, and we null out _holdTimer when the page disappears so a pending timer
// can't fire after the user has already swiped to another tab. Contact name is capped at 50 chars and
// the phone number runs through HomeController.IsValidPhilippineNumber, so nothing reaches the database
// without clearing the controller's validation first.

using AlarmaApp.Controllers;

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

    private void OnCall911Clicked(object? sender, EventArgs e)
    {
        SosSentOverlay.IsVisible = false;
        try
        {
            PhoneDialer.Default.Open("911");
        }
        catch
        {
            // PhoneDialer not supported on this device/emulator — silently ignore
        }
    }
}
