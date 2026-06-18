// Read-only list of past trips, pulled from the encrypted local database. There's no way to reach
// another user's or another device's data here — every record belongs to this device and is decrypted
// with the device's own key from SecureStorage. The text we show (destination names, summaries) was
// validated and length-capped when it was written, and nothing here is rendered as HTML or run as code.

using AlarmaApp.Controllers;

namespace AlarmaApp.Views;

public partial class HistoryView : ContentPage
{
    private readonly HomeController _controller;

    public HistoryView(HomeController controller)
    {
        _controller = controller;
        BindingContext = controller;
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        Content.Opacity = 0;
        base.OnAppearing();
        Content.FadeTo(1, 220, Easing.CubicOut);
    }

    // Wiping history has no undo, so make the rider confirm first. The prompt spells out exactly what's
    // about to go: with a search active we only delete the filtered trips on screen, otherwise everything.
    // Per-trip swipe/trash deletes are low-stakes and skip this prompt.
    private async void OnClearAllClicked(object? sender, EventArgs e)
    {
        var isFiltered = !string.IsNullOrWhiteSpace(_controller.HistorySearchQuery);
        var shownCount = _controller.TripHistoryEntries.Count;

        var title = isFiltered ? "Delete Filtered Trips" : "Clear Trip History";
        var message = isFiltered
            ? $"This deletes the {shownCount} trip(s) currently shown by your search. Other trips stay. This can't be undone."
            : "This permanently deletes every recorded trip. This can't be undone.";
        var accept = isFiltered ? "Delete Shown" : "Clear All";

        var confirm = await DisplayAlert(title, message, accept, "Cancel");
        if (confirm)
            _controller.ClearTripHistoryCommand.Execute(null);
    }
}
