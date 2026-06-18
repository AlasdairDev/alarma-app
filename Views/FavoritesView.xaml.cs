// Just shows the rider's saved routes (read-only) out of the encrypted local database. Every action
// here — apply, remove — goes back through HomeController's commands, which re-validate the route
// before any write, so this screen can't shortcut the controller's rules. Nothing is typed in on this
// page; the route data was already validated when it was saved (name 2–30 chars, coordinates inside
// the PH box) and again on backup restore. OnFavoriteRouteTapped works off the bound item itself, so
// there's no string parsing or user-supplied id flowing through here.

using AlarmaApp.Controllers;
using AlarmaApp.Models;

namespace AlarmaApp.Views;

public partial class FavoritesView : ContentPage
{
    private readonly HomeController _controller;
    private SavedRoute? _lastRemoved;
    private IDispatcherTimer? _snackbarTimer;

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

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // Don't leave a snackbar timer ticking after the page is gone.
        _snackbarTimer?.Stop();
        _snackbarTimer = null;
        UndoSnackbar.IsVisible = false;
    }

    // Removing a favorite is destructive, so confirm first; on confirm we delete and offer a brief
    // "Undo" snackbar so an accidental removal can be restored with one tap.
    private async void OnRemoveFavoriteTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not View view || view.BindingContext is not SavedRoute route)
            return;

        var confirm = await DisplayAlert(
            "Remove Favorite",
            $"Remove “{route.DisplayName}” from your favorites?",
            "Remove",
            "Cancel");
        if (!confirm)
            return;

        _lastRemoved = route;
        _controller.RemoveSavedRouteCommand.Execute(route);
        ShowUndoSnackbar(route.DisplayName);
    }

    private void ShowUndoSnackbar(string name)
    {
        SnackbarLabel.Text = $"Removed “{name}”";
        UndoSnackbar.IsVisible = true;

        _snackbarTimer?.Stop();
        _snackbarTimer = Dispatcher.CreateTimer();
        _snackbarTimer.Interval = TimeSpan.FromSeconds(4);
        _snackbarTimer.IsRepeating = false;
        _snackbarTimer.Tick += (_, _) =>
        {
            UndoSnackbar.IsVisible = false;
            _lastRemoved = null;
            _snackbarTimer?.Stop();
            _snackbarTimer = null;
        };
        _snackbarTimer.Start();
    }

    private async void OnUndoRemoveClicked(object? sender, EventArgs e)
    {
        _snackbarTimer?.Stop();
        _snackbarTimer = null;
        UndoSnackbar.IsVisible = false;

        if (_lastRemoved is not null)
        {
            await _controller.RestoreFavoriteAsync(_lastRemoved);
            _lastRemoved = null;
        }
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
