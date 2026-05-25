// Security Considerations (OWASP Top 10)
// A04 Insecure Design: Event handlers (LiveLocationUpdated, CenterMapRequested, MapJsRequested)
//   are subscribed in OnAppearing and unsubscribed in OnDisappearing, preventing memory leaks and
//   stale handler invocations from prior Activity instances. AlarmStageActivated is handled at the
//   AppShell level (singleton) so the alarm modal fires on any tab — not just when HomeView is
//   visible. The Leaflet tile layer attribution uses plain text (no <a href>) so clicking the
//   attribution area cannot navigate the WebView to an external page.
//   MapJsRequested fires JS against the live WebView in-place instead of replacing MapHtmlSource
//   with a new HtmlWebViewSource — this eliminates the full-reload that caused gray tiles when
//   the user tapped Center-on-me or picked a destination. SyncMapStateAsync() replays the current
//   destination state on every OnAppearing so the map is consistent even if SetDestination was
//   called while the view was off-screen (e.g. returning from SearchView or FavoritesView).
// A03 Injection: All values forwarded to EvaluateJavaScriptAsync use InvariantCulture F6
//   numeric strings or JsonSerializer.Serialize — no user-supplied strings reach the JS context.
// A01 Broken Access Control: Onboarding gate (HasSeenTutorial check) is enforced here
//   before InitializeAsync so biometric/location init cannot be bypassed by navigating
//   directly to the Home shell route.

using AlarmaApp.Controllers;
using AlarmaApp.Services;
using System.Globalization;
using System.Text.Json;

namespace AlarmaApp.Views;

public partial class HomeView : ContentPage
{
    private readonly HomeController _controller;
    private readonly PreferencesService _preferencesService;

    public HomeView(HomeController controller, PreferencesService preferencesService)
    {
        _controller = controller;
        _preferencesService = preferencesService;
        BindingContext = _controller;
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        Content.Opacity = 0;
        base.OnAppearing();
        _controller.LiveLocationUpdated += OnLiveLocationUpdated;
        _controller.CenterMapRequested += OnCenterMapRequested;
        _controller.MapJsRequested += OnMapJsRequested;

        // Check tutorial gate before starting the fade-in so the animation
        // does not run concurrently with a GoToAsync redirect away from Home.
        if (!_preferencesService.HasSeenTutorial)
        {
            await Shell.Current.GoToAsync("onboarding", animate: false);
            return;
        }

        _ = Content.FadeTo(1, 220, Easing.CubicOut);

        // Brief delay so the page renders before the biometric prompt appears.
        await Task.Delay(350);
        await _controller.InitializeAsync();

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

    private async void OnSearchTapped(object? sender, TappedEventArgs e)
    {
        await Shell.Current.GoToAsync("search", animate: false);
    }

    private async void OnViewActiveTripTapped(object? sender, TappedEventArgs e)
    {
        await Shell.Current.GoToAsync("alarmstage", animate: false);
    }

    private async void OnLiveLocationUpdated(object? sender, (double Lat, double Lon) loc)
    {
        var lat = loc.Lat.ToString("F6", CultureInfo.InvariantCulture);
        var lon = loc.Lon.ToString("F6", CultureInfo.InvariantCulture);
        await MapWebView.EvaluateJavaScriptAsync($"updateUserLocation({lat},{lon})");
    }

    private async void OnCenterMapRequested(object? sender, (double Lat, double Lon) loc)
    {
        var lat = loc.Lat.ToString("F6", CultureInfo.InvariantCulture);
        var lon = loc.Lon.ToString("F6", CultureInfo.InvariantCulture);
        await MapWebView.EvaluateJavaScriptAsync($"centerOnUser({lat},{lon})");
    }

    private async void OnMapJsRequested(object? sender, string js)
    {
        await MapWebView.EvaluateJavaScriptAsync(js);
    }
}
