// The home/map screen — most of the app's moving parts meet here, so a few notes:
//   - We subscribe to the controller events (LiveLocationUpdated, CenterMapRequested, MapJsRequested)
//     in OnAppearing and drop them in OnDisappearing, which avoids leaks and stops stale handlers from
//     an old Activity firing. We skip the very first OnAppearing (the one that pushes the launch modal,
//     while App.LaunchDone is false) and only wire up once the modal is gone and OnAppearing runs again.
//   - The alarm event itself lives on AppShell, not here, so the alarm fires on whatever tab you're on.
//   - MapJsRequested runs JS straight against the live WebView instead of swapping in a fresh
//     HtmlWebViewSource — reloading the source was what turned the tiles gray whenever you hit
//     Center-on-me or picked a destination. SyncMapStateAsync() replays the current destination on
//     every OnAppearing so the map stays correct even if the destination changed while this page was
//     off-screen (say, coming back from Search or Favorites).
//   - Everything we hand to EvaluateJavaScriptAsync is an InvariantCulture F6 number or JsonSerializer
//     output, so no raw user text ever lands in the JS context.
//   - The onboarding and permission gates are enforced here before InitializeAsync, so you can't skip
//     them by navigating straight to //home.

using AlarmaApp.Controllers;
using AlarmaApp.Services;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace AlarmaApp.Views;

public partial class HomeView : ContentPage
{
    private readonly HomeController _controller;
    private readonly PreferencesService _preferencesService;
    private readonly LaunchView _launchView;

    public HomeView(HomeController controller, PreferencesService preferencesService, LaunchView launchView)
    {
        _controller = controller;
        _preferencesService = preferencesService;
        _launchView = launchView;
        BindingContext = _controller;
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        Content.Opacity = 0;
        base.OnAppearing();

        // Gate 0: Show launch animation once per process lifetime.
        // LaunchView is pushed as a modal so the window/input-channel is never swapped
        // (Windows[0].Page swap caused Android 14+ ActivityRecordInputSink NO_INPUT_CHANNEL).
        if (!App.LaunchDone)
        {
            await Navigation.PushModalAsync(_launchView, animated: false);
            return; // OnAppearing fires again after the modal is dismissed
        }

        // Unsubscribe before subscribing to prevent duplicate handlers if OnAppearing
        // fires again before OnDisappearing completes (rapid tab re-entry).
        _controller.LiveLocationUpdated -= OnLiveLocationUpdated;
        _controller.CenterMapRequested -= OnCenterMapRequested;
        _controller.MapJsRequested -= OnMapJsRequested;
        _controller.PropertyChanged -= OnControllerPropertyChanged;
        _controller.LocationServicesDisabled -= OnLocationServicesDisabled;
        _controller.LiveLocationUpdated += OnLiveLocationUpdated;
        _controller.CenterMapRequested += OnCenterMapRequested;
        _controller.MapJsRequested += OnMapJsRequested;
        _controller.PropertyChanged += OnControllerPropertyChanged;
        _controller.LocationServicesDisabled += OnLocationServicesDisabled;

        // Re-attach WebView to the root grid if CleanupWebViewAsync detached it on prior exit.
        if (MapWebView.Parent is null && Content is Grid mapHost)
        {
            mapHost.Children.Insert(0, MapWebView);
            MapWebView.Source = _controller.MapHtmlSource;
        }

        // Gate 1: Tutorial + T&C must be completed first.
        if (!_preferencesService.HasSeenTutorial || !_preferencesService.HasAgreedToTerms)
        {
            await Shell.Current.GoToAsync("onboarding", animate: false);
            return;
        }

        // Gate 2: Permissions setup screen must have been shown.
        if (!_preferencesService.HasCompletedPermissionsSetup)
        {
            await Shell.Current.GoToAsync("permissions-setup", animate: false);
            return;
        }

        _ = Content.FadeTo(1, 220, Easing.CubicOut);

        await Task.Delay(350);
        await _controller.InitializeAsync();

        // Show SOS Warning once (Figma "SOS Warning.png") — inform user about SIM load requirement.
        if (!_preferencesService.HasSeenSosWarning)
        {
            _preferencesService.HasSeenSosWarning = true;
            SosWarningOverlay.IsVisible = true;
        }

        // Replay destination state in case SetDestination or ClearDestination fired
        // while this view was off-screen (e.g. navigated back from Search/Favorites).
        await SyncMapStateAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _controller.LiveLocationUpdated -= OnLiveLocationUpdated;
        _controller.CenterMapRequested -= OnCenterMapRequested;
        _controller.MapJsRequested -= OnMapJsRequested;
        _controller.PropertyChanged -= OnControllerPropertyChanged;
        _controller.LocationServicesDisabled -= OnLocationServicesDisabled;
        _ = CleanupWebViewAsync();
    }

    // Stops background Leaflet JS execution, severs the Source binding, and removes
    // the WebView from its parent grid so the tile-loading XHR loop cannot continue
    // off-screen. OnAppearing re-attaches and restores the source on the next entry.
    private async Task CleanupWebViewAsync()
    {
        try { await MapWebView.EvaluateJavaScriptAsync("try{map.stop();}catch(e){}"); }
        catch { }
        if (MapWebView.Parent is Grid parentGrid)
            parentGrid.Children.Remove(MapWebView);
        MapWebView.Source = null;
    }

    private void OnControllerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(HomeController.ShowStartTripCard)) return;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (_controller.ShowStartTripCard)
            {
                DestinationCard.TranslationY = -140;
                DestinationCard.IsVisible = true;
                await DestinationCard.TranslateTo(0, 0, 320, Easing.CubicOut);
            }
            else
            {
                await DestinationCard.TranslateTo(0, -140, 220, Easing.CubicIn);
                DestinationCard.IsVisible = false;
            }
        });
    }

    private async Task SyncMapStateAsync()
    {
        if (_controller.LastDestinationResult is { } dest)
        {
            var lat = dest.Latitude.ToString("F6", CultureInfo.InvariantCulture);
            var lon = dest.Longitude.ToString("F6", CultureInfo.InvariantCulture);
            var label = JsonSerializer.Serialize(dest.DisplayName);
            await MapWebView.EvaluateJavaScriptAsync($"setDestination({lat},{lon},{label})");
        }
        else
        {
            await MapWebView.EvaluateJavaScriptAsync("clearDestination()");
        }
    }

    private void OnLocationServicesDisabled(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            var goToSettings = await DisplayAlert(
                "Location is Off",
                "Alarma needs your device's location turned on to track your trip. Open location settings now?",
                "Open Settings",
                "Cancel");
            if (goToSettings)
                _controller.OpenLocationSettings();
        });
    }

    private void OnSosWarningClosed(object? sender, EventArgs e)
    {
        SosWarningOverlay.IsVisible = false;
    }

    private async void OnSearchTapped(object? sender, TappedEventArgs e)
    {
        await Shell.Current.GoToAsync("search", animate: false);
    }

    private async void OnViewActiveTripTapped(object? sender, TappedEventArgs e)
    {
        await Shell.Current.GoToAsync("alarmstage", animate: false);
    }

    private void OnMapNavigating(object? sender, WebNavigatingEventArgs e)
    {
        // Cancel any real external page navigation to prevent white-out from link taps.
        // Tile images are loaded as resources by Leaflet (not navigation events) — safe to cancel all http(s) new-page navigations.
        if (e.NavigationEvent == WebNavigationEvent.NewPage &&
            (e.Url.StartsWith("https://") || e.Url.StartsWith("http://")))
            e.Cancel = true;
    }

    private void OnLiveLocationUpdated(object? sender, (double Lat, double Lon) loc)
    {
        var lat = loc.Lat.ToString("F6", CultureInfo.InvariantCulture);
        var lon = loc.Lon.ToString("F6", CultureInfo.InvariantCulture);
        MainThread.BeginInvokeOnMainThread(async () =>
            await MapWebView.EvaluateJavaScriptAsync($"updateUserLocation({lat},{lon})"));
    }

    private void OnCenterMapRequested(object? sender, (double Lat, double Lon) loc)
    {
        var lat = loc.Lat.ToString("F6", CultureInfo.InvariantCulture);
        var lon = loc.Lon.ToString("F6", CultureInfo.InvariantCulture);
        MainThread.BeginInvokeOnMainThread(async () =>
            await MapWebView.EvaluateJavaScriptAsync($"centerOnUser({lat},{lon})"));
    }

    private void OnMapJsRequested(object? sender, string js)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
            await MapWebView.EvaluateJavaScriptAsync(js));
    }
}
