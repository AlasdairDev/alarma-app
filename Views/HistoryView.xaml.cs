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

    // The native Android EditText draws an underline and its own background, which would poke out of
    // our lavender pill. Once the handler is ready we strip that background so the input blends into the
    // Border and the pill reads as one seamless shape. No-op on other platforms.
    private void OnSearchEntryHandlerChanged(object? sender, EventArgs e)
    {
#if ANDROID
        if (sender is Entry entry && entry.Handler?.PlatformView is Android.Widget.EditText editText)
        {
            editText.Background = null;
            editText.SetPadding(0, editText.PaddingTop, 0, editText.PaddingBottom);
        }
#endif
    }

    // Wiping history has no undo, so make the rider confirm first. The prompt spells out exactly what's
    // about to go: with a search active we only delete the filtered trips on screen, otherwise everything.
    // Per-trip swipe/trash deletes are low-stakes and skip this prompt.
    private async void OnClearAllClicked(object? sender, EventArgs e)
    {
        // Strict confirmation: await the boolean and abort the purge entirely if the user taps "Cancel".
        var confirm = await DisplayAlert("Confirm", "Are you sure you want to delete this?", "Yes", "Cancel");
        if (!confirm) return;
        _controller.ClearTripHistoryCommand.Execute(null);
    }

    // Tapping the trash can on a single trip card. Same strict confirm as Clear All — await the boolean,
    // bail out on "Cancel", and only then run the delete command with this card's trip as the parameter.
    private async void OnDeleteTripClicked(object? sender, EventArgs e)
    {
        if (sender is not Button button) return;
        var trip = button.CommandParameter;
        if (trip is null) return;
        var confirm = await DisplayAlert("Confirm", "Are you sure you want to delete this?", "Yes", "Cancel");
        if (!confirm) return;
        _controller.DeleteTripHistoryCommand.Execute(trip);
    }
}
