using AlarmaApp.Controllers;

namespace AlarmaApp.Views;

public partial class AddFavoriteView : ContentPage
{
    private readonly HomeController _controller;
    private CancellationTokenSource? _debounceCts;

    public AddFavoriteView(HomeController controller)
    {
        _controller = controller;
        BindingContext = controller;
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        _controller.FavoriteSaved += OnFavoriteSaved;
        Content.TranslationY = 60;
        Content.Opacity = 0;
        base.OnAppearing();
        _controller.ResetSearchState();
        await Task.WhenAll(
            Content.FadeTo(1, 280, Easing.CubicOut),
            Content.TranslateTo(0, 0, 280, Easing.CubicOut));
        SearchEntry.Focus();
    }

    protected override void OnDisappearing()
    {
        _controller.FavoriteSaved -= OnFavoriteSaved;
        base.OnDisappearing();
    }

    private async void OnFavoriteSaved(object? sender, EventArgs e) => await ExitAsync();

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

    protected override bool OnBackButtonPressed()
    {
        _ = ExitAsync();
        return true;
    }

    private async void OnBackClicked(object? sender, EventArgs e) => await ExitAsync();

    private async Task ExitAsync()
    {
        _debounceCts?.Cancel();
        await Task.WhenAll(
            Content.FadeTo(0, 200, Easing.CubicIn),
            Content.TranslateTo(0, 40, 200, Easing.CubicIn));
        await Shell.Current.GoToAsync("..", animate: false);
    }
}
