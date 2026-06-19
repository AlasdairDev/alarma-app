using AlarmaApp.Controllers;
using CommunityToolkit.Maui.Storage;

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

    // Guards against the revert below re-triggering this handler when we snap the switch back to the real
    // hardware state.
    private bool _suppressBluetoothToggle;

    // STRICT two-way sync. The switch is a pure read-out of the Bluetooth ADAPTER — a tap must never move it
    // on its own. The only thing allowed to move this switch is the BroadcastReceiver confirming the hardware
    // actually changed (OnBluetoothStateChanged → IsBluetoothOn → the OneWay binding).
    //
    // So on every tap we do two things, in order:
    //   1. Snap the switch straight back to the live adapter state, ALWAYS — whatever the user tapped. This
    //      is what makes it look like the switch never reacted to the tap at all (no flip, no flicker). We
    //      also re-assert IsBluetoothOn from the live adapter so the bound mirror can't drift out of sync
    //      with the real hardware if a broadcast was ever missed.
    //   2. If the tap actually requested a DIFFERENT state, hand the real change to the OS — the system
    //      "turn Bluetooth on?" dialog (ACTION_REQUEST_ENABLE) when enabling, or the Bluetooth settings page
    //      when disabling. We never call BluetoothAdapter.Enable()/Disable() ourselves (blocked + crashes on
    //      Android 13+). If the rider goes through with the prompt, the receiver flips the switch for real; if
    //      they cancel, the switch is already sitting on the true state.
    private void OnBluetoothToggled(object? sender, ToggledEventArgs e)
    {
        if (_suppressBluetoothToggle) return;

        var requested = e.Value;
        var hardware = _controller.IsBluetoothHardwareOn; // live adapter truth, read right now

        // Keep the bound mirror locked to the real adapter state before we touch the control.
        if (_controller.IsBluetoothOn != hardware)
            _controller.IsBluetoothOn = hardware;

        // (1) Revert the visual to hardware no matter what was tapped — the switch only ever shows the truth.
        _suppressBluetoothToggle = true;
        BluetoothSwitch.IsToggled = hardware;
        _suppressBluetoothToggle = false;

        // (2) Only a tap that asks for a genuinely different state triggers the OS prompt.
        if (requested != hardware)
            _controller.RequestBluetoothChange(requested);
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

    // Export = "Save As". Seamless for the demo: no password, no dialog. We build the blob immediately on tap
    // (encrypted with the app's static key) and hand it straight to the OS file browser so the rider can drop
    // it wherever they want (Downloads, Drive, etc.). The grey pill only fires AFTER a successful save — a
    // cancel or failure shows its own message instead.
    private async void OnExportBackupTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            var (fileName, data) = await _controller.BuildBackupForSaveAsync();
            using var stream = new MemoryStream(data);
            var result = await FileSaver.Default.SaveAsync(fileName, stream, CancellationToken.None);
            if (result.IsSuccessful)
                await ShowToastAsync($"Backup saved to {result.FilePath}");
            else
                await ShowToastAsync("Export cancelled.");
        }
        catch (Exception ex)
        {
            Services.BlackBoxLogger.RecordHandledException(ex, "[SettingsView.OnExportBackupTapped]");
            await ShowToastAsync("Backup export failed. Please try again.");
        }
    }

    // Import = file browse. Open the native picker so the rider can navigate to their saved .alarma file —
    // that's the only tap needed. Cancelling returns null and we bail out quietly. Otherwise read the bytes
    // and hand them to the controller to decrypt (with the static key) + restore, then report the outcome in
    // the grey pill. No password to type.
    private async void OnRestoreBackupTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            var picked = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Select an Alarma backup (.alarma)"
            });
            if (picked is null) return; // user cancelled — abort safely, no crash

            using var source = await picked.OpenReadAsync();
            using var ms = new MemoryStream();
            await source.CopyToAsync(ms);

            await _controller.RestoreFromBytesAsync(ms.ToArray());
            await ShowToastAsync(_controller.BackupStatusText);
        }
        catch (Exception ex)
        {
            Services.BlackBoxLogger.RecordHandledException(ex, "[SettingsView.OnRestoreBackupTapped]");
            await ShowToastAsync("Backup restore failed. Please try again.");
        }
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
