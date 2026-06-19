// The full-screen "WAKE UP" alarm page. A bunch of little things keep its state honest:
//   - We override OnBackButtonPressed so the hardware back button can't sneak past the dismiss flow
//     — DismissAlarmCommand always runs, which resets CurrentAlarmStage cleanly.
//   - DismissAndExitAsync fires DismissAlarmCommand BEFORE popping the page, so the controller and UI
//     change together. Otherwise there'd be a brief moment where the stage is None but the alarm view
//     is still up, and the next stage event would just re-open it.
//   - OnStopTripClicked stops tracking and navigates back in the same handler, so nobody gets
//     stranded on this page after a trip ends with no alarm running.
//   - We subscribe to MapJsRequested in OnAppearing (and unsubscribe in OnDisappearing) so the trip
//     map gets live location + destination replay without reloading the whole WebView.
//   - The slide-to-stop gesture needs a drag of at least 75% of the track to count, so a stray tap
//     can't dismiss the alarm by accident.
//   - Anything we push into the map's JS is an InvariantCulture F6 number or JsonSerializer output —
//     never a raw user string in the eval context.

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
        // Subscribe to live GPS so the commuter's blue dot tracks on THIS map too (the active-trip view
        // has its own WebView, separate from Home's — without this it never received location updates,
        // which is why the dot didn't show up here).
        _controller.LiveLocationUpdated += OnLiveLocationUpdated;
        // The recenter FAB raises CenterMapRequested; this page has its own WebView, so we listen here
        // and run centerOnUser against AlarmMapWebView (same as Home does for its map).
        _controller.CenterMapRequested += OnCenterMapRequested;
        await Task.WhenAll(
            Content.FadeTo(1, 280, Easing.CubicOut),
            Content.TranslateTo(0, 0, 280, Easing.CubicOut));
        await SyncMapStateAsync();
        // Drop the dot right away from the last known fix so it's visible the moment the map opens,
        // rather than waiting for the next location update to arrive.
        await _controller.SeedLiveLocationAsync();

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
        _controller.LiveLocationUpdated -= OnLiveLocationUpdated;
        _controller.CenterMapRequested -= OnCenterMapRequested;
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

    // Had to build a custom guard here because the Leaflet map refresh kept wiping our destination pin.
    // The active-trip map is a SEPARATE WebView from Home's, so it reloads the Leaflet HTML from scratch
    // every time this page opens. When OnAppearing's SyncMapStateAsync raced ahead of that reload, our
    // setDestination() call landed on a page whose JS didn't exist yet and the pin just silently never
    // showed up. So we wait for Navigated to actually fire, then re-run the sync (and re-seed the blue
    // dot) — that's the only point we can trust the map is genuinely ready to take our markers.
    private async void OnAlarmMapNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (e.Result != WebNavigationResult.Success) return;
        await SyncMapStateAsync();
        await _controller.SeedLiveLocationAsync();
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

    // Push each accepted GPS fix into this map's WebView so the blue "you are here" dot keeps up with
    // the commuter. updateUserLocation adds the marker on the first fix and glides it on later ones.
    private void OnLiveLocationUpdated(object? sender, (double Lat, double Lon) loc)
    {
        var lat = loc.Lat.ToString("F6", CultureInfo.InvariantCulture);
        var lon = loc.Lon.ToString("F6", CultureInfo.InvariantCulture);
        MainThread.BeginInvokeOnMainThread(async () =>
            await AlarmMapWebView.EvaluateJavaScriptAsync($"updateUserLocation({lat},{lon})"));
    }

    // Recenter FAB tapped — fly this map's camera back to the rider's current GPS position.
    private void OnCenterMapRequested(object? sender, (double Lat, double Lon) loc)
    {
        var lat = loc.Lat.ToString("F6", CultureInfo.InvariantCulture);
        var lon = loc.Lon.ToString("F6", CultureInfo.InvariantCulture);
        MainThread.BeginInvokeOnMainThread(async () =>
            await AlarmMapWebView.EvaluateJavaScriptAsync($"centerOnUser({lat},{lon})"));
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
                var newX = Math.Clamp(_panStartX + e.TotalX, 0, maxX);
                thumb.TranslationX = newX;
                // The real bug behind "the slider moves but nothing happens": on Android the pan
                // recognizer often never raises Completed when you lift your finger, so the dismiss
                // sitting in that case below simply never ran. So we fire it the instant the thumb
                // crosses the 75% mark mid-drag instead of waiting for an event that may not come.
                // _isPanning is flipped off so this only triggers once per gesture, and
                // DismissAndExitAsync has its own re-entry guard as a second line of defence.
                if (maxX > 0 && newX >= maxX * 0.75)
                {
                    _isPanning = false;
                    thumb.TranslationX = maxX;
                    _ = StopTripAndExitAsync();
                }
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                if (!_isPanning) return;
                _isPanning = false;
                var maxSlide = Math.Max(0, track.Width - thumb.Width);
                if (maxSlide > 0 && thumb.TranslationX >= maxSlide * 0.75)
                {
                    _ = StopTripAndExitAsync();
                }
                else
                {
                    _ = thumb.TranslateTo(0, 0, 200, Easing.SpringOut);
                }
                break;
        }
    }

    // Floating Back arrow — just leave the page. We deliberately do NOT stop tracking or dismiss any
    // alarm here; the foreground location service keeps running and the rider can reopen the trip from
    // the "View Active Trip" pill on Home.
    private async void OnBackArrowTapped(object? sender, TappedEventArgs e)
    {
        await Shell.Current.GoToAsync("..", animate: false);
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

    // "Done" on the overshoot alert — the trip is over (the rider already passed their stop), so we END it
    // outright rather than just hiding the prompt. StopTrackingCommand tears down the location service,
    // silences the looping alarm, resets the overshoot state, and clears the destination, so the alarm can't
    // come back to life the next time the app is opened. Then we leave this page.
    private async void OnOvershootDoneClicked(object? sender, EventArgs e)
    {
        await EndTripAndExitAsync();
    }

    // "Open in Google Maps" on the overshoot alert — hand off directions back to the stop FIRST, then end the
    // trip the same way "Done" does. Both buttons close the trip out so the overshoot alarm never loops on
    // return; this one just launches Maps on the way out.
    private async void OnOvershootOpenMapsClicked(object? sender, EventArgs e)
    {
        _controller.OpenInGMapsCommand.Execute(null);
        await EndTripAndExitAsync();
    }

    // Shared teardown for the overshoot buttons: stop the whole trip, then animate off this page.
    private async Task EndTripAndExitAsync()
    {
        if (_isDismissing) return;
        _isDismissing = true;
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

    // Slide to Stop (Stage 1/2) and Slide to Dismiss (Stage 3) both end the WHOLE active trip — not just
    // close this screen. StopTrackingCommand tears down the background location service, silences all
    // media + vibration (DisableCriticalAudioAsync), clears the destination, and resets the alarm stage,
    // so after the swipe we simply return to the home screen with tracking fully stopped.
    private async Task StopTripAndExitAsync()
    {
        if (_isDismissing) return;
        _isDismissing = true;
        _controller.StopTrackingCommand.Execute(null);
        await Task.WhenAll(
            Content.FadeTo(0, 200, Easing.CubicIn),
            Content.TranslateTo(0, 40, 200, Easing.CubicIn));
        await Shell.Current.GoToAsync("..", animate: false);
    }
}
