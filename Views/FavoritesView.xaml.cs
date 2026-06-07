// Security Considerations (OWASP Top 10)
// A01 Broken Access Control: Displays read-only SavedRoute records from the device-local encrypted
//   SQLite database. Actions (apply, remove) go through HomeController command pipeline which
//   re-validates the route object before any DB write — this view cannot bypass controller logic.
// A04 Insecure Design: No user-typed input is accepted in this view; route data presented here
//   was validated (name 2–30 chars, PH coordinate bounds) at save-time and at backup restore-time.
//   OnFavoriteRouteTapped uses BindingContext (DI-resolved item reference) — no string parsing
//   or user-supplied identifiers pass through this handler.

using AlarmaApp.Controllers;
using AlarmaApp.Models;

namespace AlarmaApp.Views;

public partial class FavoritesView : ContentPage
{
    private readonly HomeController _controller;

    public FavoritesView(HomeController controller)
    {
        _controller = controller;
        BindingContext = controller;
        InitializeComponent();
        _controller.NavigateToAddFavoriteRequested += OnNavigateToAddFavoriteRequested;
    }

    protected override void OnAppearing()
    {
        Content.Opacity = 0;
        base.OnAppearing();
        Content.FadeTo(1, 220, Easing.CubicOut);
        _ = _controller.RefreshFavoritesAsync();
    }

    private async void OnNavigateToAddFavoriteRequested(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("add-favorite", animate: false);
    }

    private async void OnFavoriteRouteTapped(object? sender, TappedEventArgs e)
    {
        if (sender is View v && v.BindingContext is SavedRoute route)
        {
            _controller.ApplySavedRouteCommand.Execute(route);
            await Shell.Current.GoToAsync("//home", animate: false);
        }
    }
}
