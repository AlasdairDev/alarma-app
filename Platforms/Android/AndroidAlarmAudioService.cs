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

    // Preview playback (Settings) is kept completely separate from the trip alarm above so previewing a
    // sound can never touch the ringer mode the alarm relies on, nor get cut off by an alarm escalation.
    private readonly object _previewLock = new();
    private Ringtone? _previewRingtone;
    private CancellationTokenSource? _previewCts;

    // Every public entry point hops onto a background thread before touching AudioManager / Vibrator /
    // RingtoneManager. Those calls are cheap-ish but not free — grabbing a Ringtone and flipping the
    // ringer can stall for tens of milliseconds, and the alarm fires from the location-update handler
    // that runs on the UI thread. Doing it inline there is exactly what made the "Slide to Stop" pan
    // gesture freeze, so we keep the main thread clear and let the swipe stay buttery.
    public Task EnableCriticalAudioAsync()
    {
        return Task.Run(() =>
        {
            var audioManager = AndroidApplication.Context.GetSystemService(Context.AudioService) as AudioManager;
            var notificationManager = AndroidApplication.Context.GetSystemService(Context.NotificationService) as NotificationManager;
            if (audioManager is null)
            {
                return;
            }

            lock (_ringtoneLock) { _savedRingerMode ??= audioManager.RingerMode; }
            audioManager.RingerMode = RingerMode.Normal;
            var maxVolume = audioManager.GetStreamMaxVolume(AndroidStream.Alarm);
            audioManager.SetStreamVolume(AndroidStream.Alarm, maxVolume, VolumeNotificationFlags.ShowUi);

            if (notificationManager?.IsNotificationPolicyAccessGranted == true)
            {
                notificationManager.SetInterruptionFilter(InterruptionFilter.All);
            }
        });
    }

    public Task DisableCriticalAudioAsync()
    {
        return Task.Run(() =>
        {
            RingerMode? saved;
            lock (_ringtoneLock)
            {
                if (_ringtone?.IsPlaying == true)
                    _ringtone.Stop();
                saved = _savedRingerMode;
                _savedRingerMode = null;
            }

            // Kill any vibration still mid-pattern too — when the rider slides to stop we want the
            // phone to go quiet AND still, not keep buzzing out the tail of the last waveform.
            var vibrator = AndroidApplication.Context.GetSystemService(Context.VibratorService) as Vibrator;
            try { vibrator?.Cancel(); } catch { }

            if (saved.HasValue)
            {
                var audioManager = AndroidApplication.Context.GetSystemService(Context.AudioService) as AudioManager;
                if (audioManager is not null)
                    audioManager.RingerMode = saved.Value;
            }
        });
    }

    public Task TriggerAlarmAsync(AlarmStage stage, string soundKey, bool vibrationOnly, string vibrationIntensity = "Medium")
    {
        return Task.Run(async () =>
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
        });
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

    public Task PreviewSoundAsync(string soundKey)
    {
        return Task.Run(async () =>
        {
            CancellationToken token;
            lock (_previewLock)
            {
                // Tearing down any previous preview first means tapping sound after sound just swaps the
                // audio instantly rather than stacking overlapping ringtones.
                _previewCts?.Cancel();
                _previewCts?.Dispose();
                _previewCts = new CancellationTokenSource();
                token = _previewCts.Token;

                if (_previewRingtone?.IsPlaying == true)
                    _previewRingtone.Stop();

                var uri = GetRingtoneUri(soundKey);
                _previewRingtone = RingtoneManager.GetRingtone(AndroidApplication.Context, uri);
                _previewRingtone?.Play();
            }

            // Keep the preview short — a ~3s taste, then stop it ourselves in case the ringtone would
            // otherwise run long. A newer tap (or leaving the page) cancels this wait early.
            try { await Task.Delay(TimeSpan.FromSeconds(3), token); }
            catch (System.OperationCanceledException) { return; }

            lock (_previewLock)
            {
                if (_previewRingtone?.IsPlaying == true)
                    _previewRingtone.Stop();
            }
        });
    }

    public Task StopPreviewAsync()
    {
        return Task.Run(() =>
        {
            lock (_previewLock)
            {
                _previewCts?.Cancel();
                if (_previewRingtone?.IsPlaying == true)
                    _previewRingtone.Stop();
                _previewRingtone = null;
            }
        });
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

        // Emergency (Stage 3) is a full-screen lockout that must keep buzzing until the rider slides to
        // stop, so we loop the waveform (repeat index 0). Earlier stages buzz once (-1 = no repeat).
        // DisableCriticalAudioAsync calls vibrator.Cancel(), which is what ends the Emergency loop.
        var repeat = stage >= AlarmStage.Stage3 ? 0 : -1;

        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var effect = VibrationEffect.CreateWaveform(pattern, repeat);
            vibrator.Vibrate(effect);
        }
        else
        {
            vibrator.Vibrate(pattern, repeat);
        }
    }

    private async Task PlayRingtoneAsync(string soundKey, AlarmStage stage)
    {
        var isEmergency = stage >= AlarmStage.Stage3;

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

            // Emergency keeps sounding until the rider slides to stop, so loop the ringtone instead of
            // letting it play through once. Looping is API 28+; on older devices it simply plays once.
            if (_ringtone is not null && isEmergency && Build.VERSION.SdkInt >= BuildVersionCodes.P)
                _ringtone.Looping = true;

            _ringtone?.Play();
        }

        // Emergency: leave it looping. DisableCriticalAudioAsync (fired by Slide to Stop / Stop Trip)
        // is what stops the ringtone and restores the ringer — we must NOT auto-stop it here.
        if (isEmergency)
            return;

        var duration = stage switch
        {
            AlarmStage.Stage1 => TimeSpan.FromSeconds(2),
            AlarmStage.Stage2 => TimeSpan.FromSeconds(4),
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
