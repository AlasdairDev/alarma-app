using AlarmaApp.Models;
using AlarmaApp.Services.Interfaces;
using Android.App;
using Android.Content;
using Android.Media;
using Android.OS;
using AndroidApplication = Android.App.Application;
using AndroidStream = Android.Media.Stream;

namespace AlarmaApp.Platforms.Android;

public class AndroidAlarmAudioService : IAlarmAudioService
{
    private readonly object _ringtoneLock = new();
    private Ringtone? _ringtone;

    public Task EnableCriticalAudioAsync()
    {
        var audioManager = AndroidApplication.Context.GetSystemService(Context.AudioService) as AudioManager;
        var notificationManager = AndroidApplication.Context.GetSystemService(Context.NotificationService) as NotificationManager;
        if (audioManager is null)
        {
            return Task.CompletedTask;
        }

        audioManager.RingerMode = RingerMode.Normal;
        var maxVolume = audioManager.GetStreamMaxVolume(AndroidStream.Alarm);
        audioManager.SetStreamVolume(AndroidStream.Alarm, maxVolume, VolumeNotificationFlags.ShowUi);

        if (notificationManager?.IsNotificationPolicyAccessGranted == true)
        {
            notificationManager.SetInterruptionFilter(InterruptionFilter.All);
        }
        return Task.CompletedTask;
    }

    public Task DisableCriticalAudioAsync()
    {
        lock (_ringtoneLock)
        {
            if (_ringtone?.IsPlaying == true)
            {
                _ringtone.Stop();
            }
        }
        return Task.CompletedTask;
    }

    public async Task TriggerAlarmAsync(AlarmStage stage, string soundKey, bool vibrationOnly)
    {
        var audioManager = AndroidApplication.Context.GetSystemService(Context.AudioService) as AudioManager;
        var notificationManager = AndroidApplication.Context.GetSystemService(Context.NotificationService) as NotificationManager;
        if (audioManager is null)
        {
            return;
        }

        if (vibrationOnly)
        {
            audioManager.RingerMode = RingerMode.Vibrate;
        }
        else
        {
            audioManager.RingerMode = RingerMode.Normal;
            var maxVolume = audioManager.GetStreamMaxVolume(AndroidStream.Alarm);
            var targetVolume = GetStageVolume(stage, maxVolume);
            audioManager.SetStreamVolume(AndroidStream.Alarm, targetVolume, VolumeNotificationFlags.ShowUi);
        }

        if (notificationManager?.IsNotificationPolicyAccessGranted == true)
        {
            notificationManager.SetInterruptionFilter(InterruptionFilter.All);
        }

        TriggerVibration(stage);
        if (!vibrationOnly)
        {
            await PlayRingtoneAsync(soundKey, stage);
        }
    }

    private static int GetStageVolume(AlarmStage stage, int maxVolume)
    {
        return stage switch
        {
            AlarmStage.Stage1 => Math.Max(1, (int)Math.Round(maxVolume * 0.5)),
            AlarmStage.Stage2 => Math.Max(1, (int)Math.Round(maxVolume * 0.75)),
            AlarmStage.Stage3 => maxVolume,
            _ => maxVolume
        };
    }

    private void TriggerVibration(AlarmStage stage)
    {
        var vibrator = AndroidApplication.Context.GetSystemService(Context.VibratorService) as Vibrator;
        if (vibrator is null || !vibrator.HasVibrator)
        {
            return;
        }

        var pattern = stage switch
        {
            AlarmStage.Stage1 => new long[] { 0, 200, 100, 200 },
            AlarmStage.Stage2 => new long[] { 0, 300, 150, 300, 150, 300 },
            AlarmStage.Stage3 => new long[] { 0, 500, 200, 500, 200, 500 },
            _ => new long[] { 0, 200 }
        };

        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var effect = VibrationEffect.CreateWaveform(pattern, -1);
            vibrator.Vibrate(effect);
        }
        else
        {
            vibrator.Vibrate(pattern, -1);
        }
    }

    private async Task PlayRingtoneAsync(string soundKey, AlarmStage stage)
    {
        lock (_ringtoneLock)
        {
            if (_ringtone?.IsPlaying == true)
            {
                _ringtone.Stop();
            }

            var uri = GetRingtoneUri(soundKey);
            _ringtone = RingtoneManager.GetRingtone(AndroidApplication.Context, uri);
            _ringtone?.Play();
        }

        var duration = stage switch
        {
            AlarmStage.Stage1 => TimeSpan.FromSeconds(2),
            AlarmStage.Stage2 => TimeSpan.FromSeconds(4),
            AlarmStage.Stage3 => TimeSpan.FromSeconds(6),
            _ => TimeSpan.FromSeconds(2)
        };

        await Task.Delay(duration);
        await DisableCriticalAudioAsync();
    }

    private static global::Android.Net.Uri? GetRingtoneUri(string soundKey)
    {
        return soundKey switch
        {
            "Alarm" => RingtoneManager.GetDefaultUri(RingtoneType.Alarm),
            "Notification" => RingtoneManager.GetDefaultUri(RingtoneType.Notification),
            "Ringtone" => RingtoneManager.GetDefaultUri(RingtoneType.Ringtone),
            _ => RingtoneManager.GetDefaultUri(RingtoneType.Alarm)
        };
    }
}
