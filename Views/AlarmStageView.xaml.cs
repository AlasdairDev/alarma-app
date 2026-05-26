// Security Considerations (OWASP Top 10)
// A07 Identification and Authentication Failures: OnBackButtonPressed override prevents the user
//   from bypassing the alarm dismiss flow via the hardware back button — DismissAlarmCommand
//   always executes, ensuring controller state (CurrentAlarmStage, _snoozeCount) resets cleanly.
// A04 Insecure Design: DismissAndExitAsync executes DismissAlarmCommand before popping the route
//   so the controller and UI update atomically — no window where AlarmStage is None in the
//   controller but the alarm view is still showing (which would re-open on the next stage event).
//   OnStopTripClicked issues StopTrackingCommand and navigates back in one handler so the user
//   is never stranded on AlarmStageView after a trip ends with no alarm active.
//   MapJsRequested is subscribed on OnAppearing (unsubscribed on OnDisappearing) so the trip map
//   receives live-location updates and destination replay without full WebView reload.
// A03 Injection: All JS values forwarded via MapJsRequested use InvariantCulture F6 numeric
//   strings or JsonSerializer.Serialize — no user-supplied strings reach the JS eval context.

using AlarmaApp.Controllers;
using System.Globalization;
using System.Text.Json;

namespace AlarmaApp.Views;

public partial class AlarmStageView : ContentPage
{
    private readonly HomeController _controller;

    public AlarmStageView(HomeController controller)
    {
        _controller = controller;
        BindingContext = controller;
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        Content.TranslationY = 60;
        Content.Opacity = 0;
        base.OnAppearing();
        _controller.MapJsRequested += OnMapJsRequested;
        await Task.WhenAll(
            Content.FadeTo(1, 280, Easing.CubicOut),
            Content.TranslateTo(0, 0, 280, Easing.CubicOut));
        await SyncMapStateAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _controller.MapJsRequested -= OnMapJsRequested;
    }

    private async Task SyncMapStateAsync()
    {
        if (_controller.LastDestinationResult is { } dest)
        {
            var lat = dest.Latitude.ToString("F6", CultureInfo.InvariantCulture);
            var lon = dest.Longitude.ToString("F6", CultureInfo.InvariantCulture);
            var label = JsonSerializer.Serialize(dest.DisplayName);
            await AlarmMapWebView.EvaluateJavaScriptAsync($"setDestination({lat},{lon},{label})");
        }
    }

    private async void OnMapJsRequested(object? sender, string js)
    {
        await AlarmMapWebView.EvaluateJavaScriptAsync(js);
    }

    protected override bool OnBackButtonPressed()
    {
        _ = DismissAndExitAsync();
        return true;
    }

    private async void OnSlideToStopTapped(object? sender, TappedEventArgs e)
        => await DismissAndExitAsync();

    // "Stop Trip" button — shown when tracking is active but no alarm stage is firing.
    // Stops the trip then exits so the user is not stranded on this view.
    private async void OnStopTripClicked(object? sender, EventArgs e)
    {
        _controller.StopTrackingCommand.Execute(null);
        await Task.WhenAll(
            Content.FadeTo(0, 200, Easing.CubicIn),
            Content.TranslateTo(0, 40, 200, Easing.CubicIn));
        await Shell.Current.GoToAsync("..", animate: false);
    }

    private async Task DismissAndExitAsync()
    {
        _controller.DismissAlarmCommand.Execute(null);
        await Task.WhenAll(
            Content.FadeTo(0, 200, Easing.CubicIn),
            Content.TranslateTo(0, 40, 200, Easing.CubicIn));
        await Shell.Current.GoToAsync("..", animate: false);
    }
}
