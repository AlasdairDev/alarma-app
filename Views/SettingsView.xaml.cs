using AlarmaApp.Controllers;

namespace AlarmaApp.Views;

public partial class SettingsView : ContentPage
{
    private readonly HomeController _controller;
    // Suppresses the preview that the Picker's SelectedIndexChanged fires when the binding first
    // assigns the saved sound on load — we only want to preview an actual user pick.
    private bool _pickerReady;

    public SettingsView(HomeController controller)
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
        // The initial binding-driven selection has already happened by now, so any change from here on
        // is the user choosing a sound — enable previews.
        _pickerReady = true;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // Don't let a preview keep playing once we leave, and re-arm the load guard for next entry.
        _pickerReady = false;
        _controller.StopSoundPreview();
    }

    // Whenever the rider picks a different alarm sound from the dropdown, play a live preview. The
    // selection itself is saved to Preferences by the Picker's two-way SelectedItem binding.
    private void OnAlarmSoundChanged(object? sender, EventArgs e)
    {
        if (!_pickerReady) return;
        _controller.PreviewSelectedSound();
    }

    // Strip the native Android underline/background off the Picker so our rounded pill wrapper is the
    // only thing the user sees. No-op on other platforms.
    private void OnPickerHandlerChanged(object? sender, EventArgs e)
    {
#if ANDROID
        if (sender is Picker picker && picker.Handler?.PlatformView is Android.Widget.EditText editText)
            editText.Background = null;
#endif
    }

    // Run the export, then surface the result in the transient grey pill (not a blocking modal).
    private async void OnExportBackupTapped(object? sender, TappedEventArgs e)
    {
        await _controller.ExportBackupAsync();
        await ShowToastAsync(_controller.BackupStatusText);
    }

    // Same transient grey-pill feedback for restore (import).
    private async void OnRestoreBackupTapped(object? sender, TappedEventArgs e)
    {
        await _controller.RestoreBackupAsync();
        await ShowToastAsync(_controller.BackupStatusText);
    }

    // Pops the grey pill up with a message, holds it for exactly 3 seconds, then fades it away. The
    // sequence guard means a second tap mid-display simply restarts the timer instead of the first
    // toast's delay hiding the newer message early.
    private int _toastSeq;
    private async Task ShowToastAsync(string message)
    {
        var seq = ++_toastSeq;
        ToastLabel.Text = message;
        ToastPill.Opacity = 1;
        ToastPill.IsVisible = true;

        await Task.Delay(3000);
        if (seq != _toastSeq) return; // a newer toast took over — let it own the lifecycle

        await ToastPill.FadeTo(0, 250, Easing.CubicIn);
        if (seq != _toastSeq) return;
        ToastPill.IsVisible = false;
    }

    private async void OnTermsClicked(object? sender, EventArgs e)
    {
        await Navigation.PushModalAsync(new TermsAndPrivacyView(0), animated: false);
    }

    private async void OnPrivacyClicked(object? sender, EventArgs e)
    {
        await Navigation.PushModalAsync(new TermsAndPrivacyView(1), animated: false);
    }
}
