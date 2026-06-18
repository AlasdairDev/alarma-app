// Real-time Bluetooth adapter watcher. We register a BroadcastReceiver for the system's
// ACTION_STATE_CHANGED broadcast (the OS fires it whenever Bluetooth is toggled) and surface a clean
// ON/OFF event to the controller. Reading the adapter's enabled state and receiving this broadcast need
// no runtime permission prompt — only active scanning/connecting would. Nothing here is native Java: it
// is all C# against the Android bindings, the same way every other service in Platforms/Android works.

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
            _receiver = new StateReceiver(isOn => StateChanged?.Invoke(this, isOn));
            var filter = new IntentFilter(BluetoothAdapter.ActionStateChanged);

            // API 33+ requires an explicit export flag. This is a system broadcast meant only for us,
            // so it's registered NotExported.
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
        private readonly Action<bool> _onChanged;
        public StateReceiver(Action<bool> onChanged) => _onChanged = onChanged;

        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action != BluetoothAdapter.ActionStateChanged) return;

            // Only report the settled ON / OFF states — ignore the transient TURNING_ON / TURNING_OFF.
            var state = intent.GetIntExtra(BluetoothAdapter.ExtraState, (int)State.Off);
            if (state == (int)State.On)
                _onChanged(true);
            else if (state == (int)State.Off)
                _onChanged(false);
        }
    }
}
