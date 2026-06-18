namespace AlarmaApp.Services.Interfaces;

// Watches the device's Bluetooth adapter in real time so the UI can mirror the hardware state live —
// flipping the Settings switch and hiding the "earphones connected" pill the instant Bluetooth goes off.
public interface IBluetoothMonitor
{
    // Current adapter state at the moment it's read.
    bool IsEnabled { get; }

    // Raised whenever the adapter flips fully ON (true) or fully OFF (false). Fires on the main thread.
    event EventHandler<bool>? StateChanged;

    // Begin/stop listening. Start is idempotent; Stop tears the OS listener down.
    void Start();
    void Stop();
}
