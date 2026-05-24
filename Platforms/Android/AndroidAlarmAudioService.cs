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
    private RingerMode? _savedRingerMode;
    private CancellationTokenSource? _playCts;

    public Task EnableCriticalAudioAsync()
    {
        var audioManager = AndroidApplication.Context.GetSystemService(Context.AudioService) as AudioManager;
        var notificationManager = AndroidApplication.Context.GetSystemService(Context.NotificationService) as NotificationManager;
        if (audioManager is null)
        {
            return Task.CompletedTask;
        }

        _savedRingerMode ??= audioManager.RingerMode;
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

        var audioManager = AndroidApplication.Context.GetSystemService(Context.AudioService) as AudioManager;
        if (audioManager is not null && _savedRingerMode.HasValue)
        {
            audioManager.RingerMode = _savedRingerMode.Value;
            _savedRingerMode = null;
        }

        return Task.CompletedTask;
    }

    public async Task TriggerAlarmAsync(AlarmStage stage, string soundKey, bool vibrationOnly, string vibrationIntensity = "Medium")
    {
        var audioManager = AndroidApplication.Context.GetSystemService(Context.AudioService) as AudioManager;
        var notificationManager = AndroidApplication.Context.GetSystemService(Context.NotificationService) as NotificationManager;
        if (audioManager is null)
        {
            return;
        }

        if (vibrationOnly)
        {
            if (stage >= AlarmStage.Stage2)
            {
                _savedRingerMode ??= audioManager.RingerMode;
                audioManager.RingerMode = RingerMode.Vibrate;
            }
        }
        else if (stage >= AlarmStage.Stage2)
        {
            _savedRingerMode ??= audioManager.RingerMode;
            audioManager.RingerMode = RingerMode.Normal;
            var maxVolume = audioManager.GetStreamMaxVolume(AndroidStream.Alarm);
            var targetVolume = GetStageVolume(stage, maxVolume);
            audioManager.SetStreamVolume(AndroidStream.Alarm, targetVolume, VolumeNotificationFlags.ShowUi);

            if (notificationManager?.IsNotificationPolicyAccessGranted == true)
                notificationManager.SetInterruptionFilter(InterruptionFilter.All);
        }
        else
        {
            var maxVolume = audioManager.GetStreamMaxVolume(AndroidStream.Alarm);
            var targetVolume = GetStageVolume(stage, maxVolume);
            audioManager.SetStreamVolume(AndroidStream.Alarm, targetVolume, VolumeNotificationFlags.ShowUi);
        }

        TriggerVibration(stage, vibrationIntensity);
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

    private void TriggerVibration(AlarmStage stage, string intensity)
    {
        var vibrator = AndroidApplication.Context.GetSystemService(Context.VibratorService) as Vibrator;
        if (vibrator is null || !vibrator.HasVibrator)
        {
            return;
        }

        long[] basePattern = stage switch
        {
            AlarmStage.Stage1 => [0, 200, 100, 200],
            AlarmStage.Stage2 => [0, 300, 150, 300, 150, 300],
            AlarmStage.Stage3 => [0, 500, 200, 500, 200, 500],
            _ => [0, 200]
        };

        // Scale pulse durations by intensity (gaps at index 0 and even indices stay 0 or minimal)
        var scale = intensity switch
        {
            "Low"  => 0.5,
            "High" => 1.5,
            _      => 1.0   // Medium
        };
        var pattern = basePattern
            .Select((v, i) => i == 0 ? 0L : Math.Max(50L, (long)(v * scale)))
            .ToArray();

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
        // Cancel any previous play so its delay callback doesn't stop a newer ringtone.
        CancellationToken myToken;
        lock (_ringtoneLock)
        {
            _playCts?.Cancel();
            _playCts?.Dispose();
            _playCts = new CancellationTokenSource();
            myToken = _playCts.Token;

            if (_ringtone?.IsPlaying == true)
                _ringtone.Stop();

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

        try
        {
            await Task.Delay(duration, myToken);
        }
        catch (System.OperationCanceledException)
        {
            return; // A newer ringtone took over — don't touch ringer mode.
        }
        await DisableCriticalAudioAsync();
    }

    private static global::Android.Net.Uri? GetRingtoneUri(string soundKey)
    {
        return soundKey switch
        {
            "Alarm" => RingtoneManager.GetDefaultUri(RingtoneType.Alarm),
            "Chime" => RingtoneManager.GetDefaultUri(RingtoneType.Notification),
            "Notification" => RingtoneManager.GetDefaultUri(RingtoneType.Notification),
            "Ringtone" => RingtoneManager.GetDefaultUri(RingtoneType.Ringtone),
            _ => RingtoneManager.GetDefaultUri(RingtoneType.Alarm)
        };
    }
}
