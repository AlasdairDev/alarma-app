// Security Considerations (OWASP Top 10)
// A04 Insecure Design: SOS requires a deliberate 2-second hold (not a tap) to prevent
//   accidental dispatch; _holdTimer is nulled on page disappear so a background timer
//   cannot fire after navigation to another tab.
// A03 Injection: Contact Name (MaxLength=50) and Phone validated in
//   HomeController.IsValidPhilippineNumber — no raw input reaches SQLite without going
//   through the controller's validation gate first.

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
        Content.FadeTo(1, 220, Easing.CubicOut);
        _controller.SosDispatched += OnSosDispatched;
        _controller.SmsDenied += OnSmsDenied;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _holdTimer?.Stop();
        _holdTimer = null;
        SosButton.BackgroundColor = Color.FromArgb("#FF3B30");
        _controller.SosDispatched -= OnSosDispatched;
        _controller.SmsDenied -= OnSmsDenied;
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
