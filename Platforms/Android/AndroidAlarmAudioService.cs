// Handles the actual sound + vibration for the alarm stages (and the quieter SOS cue). Two subtle
// bugs we had to design around:
//   - We only want to remember the rider's ORIGINAL ringer mode once, so _savedRingerMode uses ??=
//     — the first Stage-2+ override saves it, and every DisableCriticalAudioAsync restores it and
//     clears it again. That way a string of alarm escalations can't permanently leave the phone
//     stuck off silent.
//   - Each ringtone gets its own CancellationTokenSource (_playCts). If a newer, higher-priority
//     alarm takes over, the older one's Task.Delay throws OperationCanceledException and bails out
//     WITHOUT restoring the ringer — otherwise it would silence the alarm that just superseded it.
// Nothing sensitive here: no user data, no credentials, no network.

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

        lock (_ringtoneLock) { _savedRingerMode ??= audioManager.RingerMode; }
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
        RingerMode? saved;
        lock (_ringtoneLock)
        {
            if (_ringtone?.IsPlaying == true)
                _ringtone.Stop();
            saved = _savedRingerMode;
            _savedRingerMode = null;
        }

        if (saved.HasValue)
        {
            var audioManager = AndroidApplication.Context.GetSystemService(Context.AudioService) as AudioManager;
            if (audioManager is not null)
                audioManager.RingerMode = saved.Value;
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
                lock (_ringtoneLock) { _savedRingerMode ??= audioManager.RingerMode; }
                audioManager.RingerMode = RingerMode.Vibrate;
            }
        }
        else if (stage >= AlarmStage.Stage2)
        {
            lock (_ringtoneLock) { _savedRingerMode ??= audioManager.RingerMode; }
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

    public Task PlaySosFeedbackAsync()
    {
        // A single short buzz so the press is felt even in a pocket. No ringer/DND changes.
        var vibrator = AndroidApplication.Context.GetSystemService(Context.VibratorService) as Vibrator;
        if (vibrator?.HasVibrator == true)
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                vibrator.Vibrate(VibrationEffect.CreateOneShot(400, VibrationEffect.DefaultAmplitude));
            else
                vibrator.Vibrate(400);
        }

        // A brief, self-contained beep on the notification stream — respects the user's current
        // volume and never forces audio the way the trip alarm does. ToneGenerator is fully
        // independent of RingerMode/DND, so a silenced phone simply stays silent.
        try
        {
            var tone = new ToneGenerator(AndroidStream.Notification, 90);
            tone.StartTone(Tone.PropBeep2, 600);
            // Release after the tone finishes so the native object isn't leaked.
            _ = Task.Delay(800).ContinueWith(_ =>
            {
                try { tone.Release(); } catch { }
            });
        }
        catch
        {
            // ToneGenerator can throw on some devices when audio resources are unavailable — the
            // haptic buzz above is enough confirmation, so swallow and carry on.
        }

        return Task.CompletedTask;
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

        // Rider dials alarm urgency Low/Med/High, so stretch or shrink the buzz pulses to match.
        // Index 0 is the lead-in delay and stays 0; everything else is floored at 50ms so even a
        // "Low" scale can't shrink a pulse down to something the rider wouldn't feel through a pocket.
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
