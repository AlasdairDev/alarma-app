// Security Considerations (OWASP Top 10)
// A03 Injection: DestinationQuery Entry is bounded by MaxLength="200" at the XAML layer;
//   the controller additionally validates length > 200 before issuing HTTP requests — no raw
//   user string reaches the geocoding HTTP layer without passing both guards.
// A04 Insecure Design: Debounce CancellationTokenSource is cancelled on back-press and on
//   result-tap so a stale in-flight search cannot overwrite the user's confirmed destination
//   selection with an outdated result set.
// No credentials, SQLite writes, or sensitive data are handled in this view layer.

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
            if ((e.NewTextValue?.Length ?? 0) >= 3)
                _controller.SearchDestinationCommand.Execute(null);
        }
        catch (OperationCanceledException) { }
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
