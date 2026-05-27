// Security Considerations (OWASP Top 10)
// A04 Insecure Design: Event handlers (LiveLocationUpdated, CenterMapRequested, MapJsRequested)
//   are subscribed in OnAppearing and unsubscribed in OnDisappearing, preventing memory leaks and
//   stale handler invocations from prior Activity instances. Subscriptions are skipped for the
//   initial OnAppearing that pushes the launch modal (App.LaunchDone = false) — they are only
//   registered after the modal is dismissed and OnAppearing fires again. AlarmStageActivated is
//   handled at the AppShell level (singleton) so the alarm modal fires on any tab.
//   MapJsRequested fires JS against the live WebView in-place instead of replacing MapHtmlSource
//   with a new HtmlWebViewSource — this eliminates the full-reload that caused gray tiles when
//   the user tapped Center-on-me or picked a destination. SyncMapStateAsync() replays the current
//   destination state on every OnAppearing so the map is consistent even if SetDestination was
//   called while the view was off-screen (e.g. returning from SearchView or FavoritesView).
// A03 Injection: All values forwarded to EvaluateJavaScriptAsync use InvariantCulture F6
//   numeric strings or JsonSerializer.Serialize — no user-supplied strings reach the JS context.
// A01 Broken Access Control: Onboarding, permissions, and biometric gates are enforced here
//   before InitializeAsync so location/SMS init cannot be bypassed by navigating directly to //home.
//   Gate 3 (biometric) runs in release builds only (#if !DEBUG) via IBiometricAuthService.

using AlarmaApp.Controllers;
using AlarmaApp.Services;
using AlarmaApp.Services.Interfaces;
using System.Globalization;
using System.Text.Json;

namespace AlarmaApp.Views;

public partial class HomeView : ContentPage
{
    private readonly HomeController _controller;
    private readonly PreferencesService _preferencesService;
    private readonly IBiometricAuthService _biometricAuthService;
    private readonly LaunchView _launchView;

    public HomeView(HomeController controller, PreferencesService preferencesService, IBiometricAuthService biometricAuthService, LaunchView launchView)
    {
        _controller = controller;
        _preferencesService = preferencesService;
        _biometricAuthService = biometricAuthService;
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

        _controller.LiveLocationUpdated += OnLiveLocationUpdated;
        _controller.CenterMapRequested += OnCenterMapRequested;
        _controller.MapJsRequested += OnMapJsRequested;

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

        // Gate 3: Biometric / PIN authentication (release builds only).
#if !DEBUG
        {
            var authenticated = await _biometricAuthService.AuthenticateAsync(
                "Verify your identity to access Alarma",
                CancellationToken.None);
            if (!authenticated)
            {
                Content.Opacity = 0;
                return;
            }
        }
#endif

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
