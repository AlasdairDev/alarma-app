namespace AlarmaApp.Services.Interfaces;

// Watches the device's Bluetooth adapter in real time so the UI can mirror the hardware state live —
// flipping the Settings switch and hiding the "earphones connected" pill the instant Bluetooth goes off.
public interface IBluetoothMonitor
{
    // Current adapter state at the moment it's read.
    bool IsEnabled { get; }

    // Raised whenever the adapter flips fully ON (true) or fully OFF (false). Fires on the main thread.
    event EventHandler<bool>? StateChanged;

    // Raised whenever a Bluetooth device links or unlinks (ACL connect/disconnect) — e.g. earbuds being
    // put in or taken out. Carries no device detail (reading it would need BLUETOOTH_CONNECT); the
    // listener re-queries the real audio-output state itself. May fire for non-audio devices too, so the
    // listener is expected to filter on whether an audio device is actually present.
    event EventHandler? DeviceConnectionChanged;

    // Begin/stop listening. Start is idempotent; Stop tears the OS listener down.
    void Start();
    void Stop();
}
