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

    // Wiping the whole history has no undo, so make the rider confirm before we hand off to the
    // controller's purge command. Per-trip deletes are low-stakes and skip this prompt.
    private async void OnClearAllClicked(object? sender, EventArgs e)
    {
        var confirm = await DisplayAlert(
            "Clear Trip History",
            "This permanently deletes every recorded trip. This can't be undone.",
            "Clear All",
            "Cancel");
        if (confirm)
            _controller.ClearTripHistoryCommand.Execute(null);
    }
}
