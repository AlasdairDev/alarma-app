using AlarmaApp.Controllers;
using AlarmaApp.Models;
using AlarmaApp.Services;
using System.Globalization;

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
        Appearing += OnAppearing;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _controller.AlarmStageActivated += OnAlarmStageActivated;
        _controller.LiveLocationUpdated += OnLiveLocationUpdated;
        _controller.CenterMapRequested += OnCenterMapRequested;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _controller.AlarmStageActivated -= OnAlarmStageActivated;
        _controller.LiveLocationUpdated -= OnLiveLocationUpdated;
        _controller.CenterMapRequested -= OnCenterMapRequested;
    }

    private async void OnAppearing(object? sender, EventArgs e)
    {
        if (!_preferencesService.HasSeenTutorial)
        {
            await Shell.Current.GoToAsync("onboarding");
            return;
        }

        // Brief delay so the page renders before the biometric prompt appears.
        await Task.Delay(350);
        await _controller.InitializeAsync();
    }

    private async void OnSearchTapped(object? sender, TappedEventArgs e)
    {
        await Shell.Current.GoToAsync("search");
    }

    private async void OnViewActiveTripTapped(object? sender, TappedEventArgs e)
    {
        await Shell.Current.GoToAsync("alarmstage");
    }

    private async void OnAlarmStageActivated(object? sender, AlarmStage stage)
    {
        await Shell.Current.GoToAsync("alarmstage");
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
}
