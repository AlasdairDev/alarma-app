// Real-time Bluetooth hardware watcher. A single BroadcastReceiver listens for three system broadcasts:
//   • ACTION_STATE_CHANGED      — the adapter itself toggling on/off (drives the Settings switch)
//   • ACTION_ACL_CONNECTED      — a device linking (earbuds put in)
//   • ACTION_ACL_DISCONNECTED   — a device unlinking (earbuds taken out)
// and surfaces clean events to the controller. Reading the adapter state and receiving these broadcasts
// need no runtime permission prompt — only active scanning/connecting or reading the connected device's
// details would, and we deliberately never read EXTRA_DEVICE. Everything here is C# against the Android
// bindings, the same way every other service in Platforms/Android works.

using AlarmaApp.Services;
using AlarmaApp.Services.Interfaces;
using Android.Bluetooth;
using Android.Content;
using Android.OS;
using AndroidApplication = Android.App.Application;

namespace AlarmaApp.Platforms.Android;

public class AndroidBluetoothMonitor : IBluetoothMonitor
{
    private StateReceiver? _receiver;

    public event EventHandler<bool>? StateChanged;
    public event EventHandler? DeviceConnectionChanged;

    public bool IsEnabled
    {
        get
        {
            try
            {
                var manager = AndroidApplication.Context
                    .GetSystemService(Context.BluetoothService) as BluetoothManager;
                return manager?.Adapter?.IsEnabled == true;
            }
            catch (Exception ex)
            {
                BlackBoxLogger.RecordHandledException(ex, "[AndroidBluetoothMonitor.IsEnabled]");
                return false;
            }
        }
    }

    public void Start()
    {
        if (_receiver is not null) return;

        try
        {
            _receiver = new StateReceiver(
                isOn => StateChanged?.Invoke(this, isOn),
                () => DeviceConnectionChanged?.Invoke(this, EventArgs.Empty));

            var filter = new IntentFilter();
            filter.AddAction(BluetoothAdapter.ActionStateChanged);
            filter.AddAction(BluetoothDevice.ActionAclConnected);
            filter.AddAction(BluetoothDevice.ActionAclDisconnected);

            // API 33+ requires an explicit export flag. These are system broadcasts meant only for us,
            // so the receiver is registered NotExported.
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
                AndroidApplication.Context.RegisterReceiver(_receiver, filter, ReceiverFlags.NotExported);
            else
                AndroidApplication.Context.RegisterReceiver(_receiver, filter);
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[AndroidBluetoothMonitor.Start]");
        }
    }

    public void Stop()
    {
        if (_receiver is null) return;
        try { AndroidApplication.Context.UnregisterReceiver(_receiver); }
        catch (Exception ex) { BlackBoxLogger.RecordHandledException(ex, "[AndroidBluetoothMonitor.Stop]"); }
        _receiver = null;
    }

    private sealed class StateReceiver : BroadcastReceiver
    {
        private readonly Action<bool> _onStateChanged;
        private readonly Action _onDeviceChanged;

        public StateReceiver(Action<bool> onStateChanged, Action onDeviceChanged)
        {
            _onStateChanged = onStateChanged;
            _onDeviceChanged = onDeviceChanged;
        }

        public override void OnReceive(Context? context, Intent? intent)
        {
            switch (intent?.Action)
            {
                case BluetoothAdapter.ActionStateChanged:
                    // Only report the settled ON / OFF states — ignore transient TURNING_ON / TURNING_OFF.
                    var state = intent.GetIntExtra(BluetoothAdapter.ExtraState, (int)State.Off);
                    if (state == (int)State.On)
                        _onStateChanged(true);
                    else if (state == (int)State.Off)
                        _onStateChanged(false);
                    break;

                case var a when a == BluetoothDevice.ActionAclConnected
                             || a == BluetoothDevice.ActionAclDisconnected:
                    // A device linked or unlinked. We don't trust the raw broadcast to mean "earbuds" —
                    // the listener re-queries the real audio-output state to decide. (We never touch
                    // EXTRA_DEVICE, so no BLUETOOTH_CONNECT permission is involved.)
                    _onDeviceChanged();
                    break;
            }
        }
    }
}
