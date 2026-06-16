// The destination search screen. The query box is capped at MaxLength="200" in XAML, and the
// controller re-checks the length before it makes any HTTP call, so an over-long string can't reach
// the geocoding layer either way. The debounce CancellationTokenSource gets cancelled on back-press
// and when a result is tapped, so a slow search still in flight can't land late and clobber the
// destination the user just picked. No credentials, no database writes, nothing sensitive here.

using AlarmaApp.Controllers;
using AlarmaApp.Services;

namespace AlarmaApp.Views;

public partial class SearchView : ContentPage
{
    private readonly HomeController _controller;
    private CancellationTokenSource? _debounceCts;

    public SearchView(HomeController controller)
    {
        _controller = controller;
        BindingContext = _controller;
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        Content.TranslationY = 60;
        Content.Opacity = 0;
        base.OnAppearing();
        await Task.WhenAll(
            Content.FadeTo(1, 280, Easing.CubicOut),
            Content.TranslateTo(0, 0, 280, Easing.CubicOut));
        SearchEntry.Focus();
        _ = _controller.RefreshCurrentLocationAsync();
    }

    private async void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;
        try
        {
            await Task.Delay(500, token);
            if (!token.IsCancellationRequested && (e.NewTextValue?.Length ?? 0) >= 3)
                _controller.SearchDestinationCommand.Execute(null);
        }
        catch (OperationCanceledException) { }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = null;
    }

    protected override bool OnBackButtonPressed()
    {
        _ = ExitAsync();
        return true;
    }

    private async void OnBackClicked(object? sender, EventArgs e) => await ExitAsync();

    private async void OnResultTapped(object? sender, TappedEventArgs e)
    {
        if (sender is View v && v.BindingContext is GeocodingResult result)
        {
            _debounceCts?.Cancel();
            _controller.SelectResultCommand.Execute(result);
            await ExitAsync(cancelDebounce: false);
        }
    }

    private async Task ExitAsync(bool cancelDebounce = true)
    {
        if (cancelDebounce)
            _debounceCts?.Cancel();

        await Task.WhenAll(
            Content.FadeTo(0, 200, Easing.CubicIn),
            Content.TranslateTo(0, 40, 200, Easing.CubicIn));
        await Shell.Current.GoToAsync("..", animate: false);
    }
}
