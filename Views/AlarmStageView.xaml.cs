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
//   The slide-to-stop/dismiss gesture requires a horizontal drag of ≥75 % of the track width to
//   trigger, preventing accidental tap-through dismissals (A04 defense-in-depth).
// A03 Injection: All JS values forwarded via MapJsRequested use InvariantCulture F6 numeric
//   strings or JsonSerializer.Serialize — no user-supplied strings reach the JS eval context.

using AlarmaApp.Controllers;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace AlarmaApp.Views;

public partial class AlarmStageView : ContentPage
{
    private readonly HomeController _controller;
    private double _panStartX;
    private bool _isPanning;
    private bool _isDismissing;
    private bool _stage3Announced;

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
        _controller.PropertyChanged += OnControllerPropertyChanged;
        await Task.WhenAll(
            Content.FadeTo(1, 280, Easing.CubicOut),
            Content.TranslateTo(0, 0, 280, Easing.CubicOut));
        await SyncMapStateAsync();

        // Announce Stage 3 immediately if it was already active when the view appeared.
        if (_controller.IsStage3Wake && !_stage3Announced)
        {
            _stage3Announced = true;
            SemanticScreenReader.Announce(
                "WAKE UP! You might miss your stop. Slide right to dismiss the alarm.");
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _controller.MapJsRequested -= OnMapJsRequested;
        _controller.PropertyChanged -= OnControllerPropertyChanged;
        _stage3Announced = false;
    }

    private void OnControllerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(HomeController.IsStage3Wake)) return;

        if (!_controller.IsStage3Wake)
        {
            _stage3Announced = false;
            return;
        }

        if (_stage3Announced) return;
        _stage3Announced = true;
        MainThread.BeginInvokeOnMainThread(() =>
            SemanticScreenReader.Announce(
                "WAKE UP! You might miss your stop. Slide right to dismiss the alarm."));
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

    // Stage 1/2 — "Slide to Stop" pan handler
    private void OnSlideToStopPanUpdated(object? sender, PanUpdatedEventArgs e)
        => HandleSliderPan(e, SlideToStopThumb, SlideToStopTrack);

    // Stage 3 — "Slide to dismiss" pan handler
    private void OnSlideToDismissPanUpdated(object? sender, PanUpdatedEventArgs e)
        => HandleSliderPan(e, SlideToDismissThumb, SlideToDismissTrack);

    private void HandleSliderPan(PanUpdatedEventArgs e, View thumb, View track)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _panStartX = thumb.TranslationX;
                _isPanning = true;
                break;

            case GestureStatus.Running:
                if (!_isPanning) return;
                var maxX = Math.Max(0, track.Width - thumb.Width);
                thumb.TranslationX = Math.Clamp(_panStartX + e.TotalX, 0, maxX);
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                if (!_isPanning) return;
                _isPanning = false;
                var maxSlide = Math.Max(0, track.Width - thumb.Width);
                if (maxSlide > 0 && thumb.TranslationX >= maxSlide * 0.75)
                {
                    _ = DismissAndExitAsync();
                }
                else
                {
                    _ = thumb.TranslateTo(0, 0, 200, Easing.SpringOut);
                }
                break;
        }
    }

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
        if (_isDismissing) return;
        _isDismissing = true;
        _controller.DismissAlarmCommand.Execute(null);
        await Task.WhenAll(
            Content.FadeTo(0, 200, Easing.CubicIn),
            Content.TranslateTo(0, 40, 200, Easing.CubicIn));
        await Shell.Current.GoToAsync("..", animate: false);
    }
}
