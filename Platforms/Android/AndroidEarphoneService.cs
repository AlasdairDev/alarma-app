using AlarmaApp.Services.Interfaces;
using Android.Content;
using Android.Media;
using AndroidApplication = Android.App.Application;

namespace AlarmaApp.Platforms.Android;

public class AndroidEarphoneService : IEarphoneService
{
    public (bool IsConnected, string Details) GetConnectionStatus()
    {
        var audioManager = AndroidApplication.Context.GetSystemService(Context.AudioService) as AudioManager;
        if (audioManager is null)
        {
            return (false, "Audio manager unavailable.");
        }

        bool wired = false;
        bool bluetooth = false;

        // GetDevices is the correct API for API 26+ (MAUI minimum)
        var devices = audioManager.GetDevices(GetDevicesTargets.Outputs);
        if (devices != null)
        {
            foreach (var device in devices)
            {
                if (device.Type == AudioDeviceType.WiredHeadphones ||
                    device.Type == AudioDeviceType.WiredHeadset ||
                    device.Type == AudioDeviceType.UsbHeadset ||
                    device.Type == AudioDeviceType.UsbDevice)
                {
                    wired = true;
                }
                else if (device.Type == AudioDeviceType.BluetoothA2dp ||
                         device.Type == AudioDeviceType.BluetoothSco ||
                         device.Type == AudioDeviceType.BleHeadset)
                {
                    bluetooth = true;
                }
            }
        }

        if (wired && bluetooth) return (true, "Wired + Bluetooth audio connected.");
        if (wired) return (true, "Wired earphones connected.");
        if (bluetooth) return (true, "Bluetooth audio connected.");

        return (false, "No earphones detected.");
    }
}