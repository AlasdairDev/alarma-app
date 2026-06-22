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
using AlarmaApp.Services;
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
    // Primary alarm player. A MediaPlayer (not a Ringtone) so we can pin the AudioAttributes BEFORE prepare
    // and the OS locks the sound to one stream from the start — see StartAlarmPlaybackLocked for why that's
    // the whole fix for the speaker-plus-earbuds dual playback.
    private MediaPlayer? _mediaPlayer;
    // The Emergency-only second player: when the primary alarm routes to earbuds, this one pushes the same
    // wake-up out the phone speaker so a pocketed earbud can't swallow it. Null whenever it's not needed.
    private MediaPlayer? _emergencySpeakerPlayer;
    // Fallback only: if MediaPlayer ever refuses an OEM resource URI, we drop back to a plain Ringtone so
    // the rider is never left with a silent alarm.
    private Ringtone? _ringtone;
    private RingerMode? _savedRingerMode;
    private CancellationTokenSource? _playCts;

    // We ask the earphone service (the same one that drives the grey pill) whether earbuds / a wired
    // headset are plugged in, so the alarm can pick the right output stream at trigger time.
    private readonly IEarphoneService _earphoneService;

    // Lets us resolve the "Custom" sound key to the rider's imported file path at play time, and fall
    // back safely to a bundled voice if that file has since been deleted (Feature 1 whitelist safety).
    private readonly PreferencesService _preferencesService;

    public AndroidAlarmAudioService(IEarphoneService earphoneService, PreferencesService preferencesService)
    {
        _earphoneService = earphoneService;
        _preferencesService = preferencesService;
    }

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
                ReleaseAlarmPlayersLocked();
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

            // Stage 1 is the gentle nudge — vibration-only by design. It buzzes and bails out before any
            // audio path runs, never raising the alarm volume or touching the ringer/DND. It must never
            // route audio to the alarm channel regardless of the resolved sound key (which is always
            // "None" for Stage 1), so we hard-stop here.
            if (stage == AlarmStage.Stage1)
            {
                TriggerVibration(stage, vibrationIntensity);
                return;
            }

            if (vibrationOnly)
            {
                lock (_ringtoneLock) { _savedRingerMode ??= audioManager.RingerMode; }
                audioManager.RingerMode = RingerMode.Vibrate;
            }

            // A stage whose chosen sound is "None" (Feature 2 — e.g. a vibration-led Stage 1) buzzes but
            // plays no audio, so we skip the ringer/volume override and the playback entirely. The vibrate
            // path below still runs, keeping the rider covered.
            var isNoneSound = string.Equals(soundKey, AlarmSoundCatalog.NoneKey, StringComparison.OrdinalIgnoreCase);
            var playAudio = !vibrationOnly && !isNoneSound;

            // STREAM_ALARM always blasts the phone's own speaker by Android's design — even with earbuds
            // in — so the alarm used to double up, playing out loud AND in the rider's ears. To stop that
            // dual playback we check for earphones at the moment the alarm fires: if they're connected we
            // send the audio down the MUSIC stream (which honours the earbuds and stays out of the
            // speaker); if not, we use the ALARM stream so it punches through the phone speaker as normal.
            var routeToEarphones = false;
            var alsoDriveSpeaker = false;

            if (playAudio)
            {
                routeToEarphones = ShouldRouteToEarphones();
                // Emergency-only safeguard: if the alarm is routing to earbuds, ALSO drive the phone
                // speaker so a paired-but-pocketed earbud can't quietly swallow the wake-up. Stage 2 keeps
                // routing to the earbuds alone (the prior dual-playback annoyance fix stands).
                alsoDriveSpeaker = EmergencyAudioPolicy.ShouldAlsoDriveSpeaker(stage, routeToEarphones);
                var targetStream = routeToEarphones ? AndroidStream.Music : AndroidStream.Alarm;

                lock (_ringtoneLock) { _savedRingerMode ??= audioManager.RingerMode; }
                audioManager.RingerMode = RingerMode.Normal;
                var maxVolume = audioManager.GetStreamMaxVolume(targetStream);
                var targetVolume = GetStageVolume(stage, maxVolume);
                audioManager.SetStreamVolume(targetStream, targetVolume, VolumeNotificationFlags.ShowUi);

                // When we're also driving the speaker for the Emergency stage, max the alarm stream too so
                // the speaker copy is at full wake-up volume, not whatever it happened to be at.
                if (alsoDriveSpeaker)
                {
                    var alarmMax = audioManager.GetStreamMaxVolume(AndroidStream.Alarm);
                    audioManager.SetStreamVolume(AndroidStream.Alarm, alarmMax, VolumeNotificationFlags.ShowUi);
                }

                if (notificationManager?.IsNotificationPolicyAccessGranted == true)
                    notificationManager.SetInterruptionFilter(InterruptionFilter.All);
            }

            TriggerVibration(stage, vibrationIntensity);
            if (playAudio)
            {
                await PlayRingtoneAsync(soundKey, stage, routeToEarphones, alsoDriveSpeaker);
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

        // Every stage now buzzes CONTINUOUSLY (repeat from index 0) until it's superseded by the next stage
        // or the rider dismisses the alarm. A single one-shot buzz was too easy to sleep through, and Stage 1
        // in particular "didn't seem to vibrate" because it fired once and went silent. Re-issuing Vibrate()
        // for the next stage replaces the running waveform, and DisableCriticalAudioAsync (Slide to Stop /
        // Stop Trip) calls vibrator.Cancel(), which is what finally ends the loop.
        var repeat = 0;

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

    // True when there's an external listening device (Bluetooth A2DP/LE or a wired/USB headset) plugged
    // in right now. We ask the OS audio router directly via AudioManager.GetDevices, because that reflects
    // the ACTUAL output target the MediaPlayer will obey — so the routing decision and what the hardware
    // does can't disagree. The earphone service is kept only as a fallback if that query ever throws. Any
    // total failure falls back to "not connected" → the alarm plays through the speaker, which is the safe
    // default (better a loud speaker alarm than a silent one).
    private bool ShouldRouteToEarphones()
    {
        try
        {
            var audioManager = AndroidApplication.Context.GetSystemService(Context.AudioService) as AudioManager;
            if (audioManager is not null && HasExternalAudioOutput(audioManager))
                return true;
        }
        catch
        {
            // GetDevices unavailable / threw — fall through to the earphone-service check below.
        }

        try
        {
            return _earphoneService.GetConnectionStatus().IsConnected;
        }
        catch
        {
            return false;
        }
    }

    // Scans the live OUTPUT devices the OS would route audio to and returns true if any of them is an
    // external listening device — Bluetooth (classic A2DP/SCO or newer BLE audio) or a wired/USB headset.
    // This is the authoritative signal for "should the alarm stay out of the phone speaker?".
    private static bool HasExternalAudioOutput(AudioManager audioManager)
    {
        var devices = audioManager.GetDevices(GetDevicesTargets.Outputs);
        if (devices is null)
            return false;

        foreach (var device in devices)
        {
            switch (device.Type)
            {
                case AudioDeviceType.BluetoothA2dp:
                case AudioDeviceType.BluetoothSco:
                case AudioDeviceType.BleHeadset:
                case AudioDeviceType.BleSpeaker:
                case AudioDeviceType.WiredHeadset:
                case AudioDeviceType.WiredHeadphones:
                case AudioDeviceType.UsbHeadset:
                case AudioDeviceType.UsbDevice:
                    return true;
            }
        }

        return false;
    }

    private async Task PlayRingtoneAsync(string soundKey, AlarmStage stage, bool routeToEarphones, bool alsoDriveSpeaker)
    {
        // Stage 2 AND the Emergency stage now LOOP until they're superseded or dismissed. Stage 2 used to ring
        // exactly once, which a sleeping commuter could easily miss in the gap before the next escalation —
        // it now keeps sounding until Stage 3 takes over or the rider slides to stop. (Looping is API 28+;
        // on older devices it plays through once, the same limitation the emergency path always had.)
        var shouldLoop = stage >= AlarmStage.Stage2;

        // Cancel any previous play so its delay callback doesn't stop a newer sound.
        CancellationToken myToken;
        lock (_ringtoneLock)
        {
            _playCts?.Cancel();
            _playCts?.Dispose();
            _playCts = new CancellationTokenSource();
            myToken = _playCts.Token;

            // Completely release whatever was playing before we build a fresh player. Tearing the old one
            // all the way down (Stop → Reset → Release) frees its bound output stream so the new player's
            // routing is decided cleanly from scratch — this rebuild-on-every-trigger is the core of the
            // dual-playback fix: a stage escalation can never leave a stale player leaking onto the speaker.
            ReleaseAlarmPlayersLocked();

            var uri = GetRingtoneUri(soundKey);
            if (uri is not null)
                StartAlarmPlaybackLocked(uri, routeToEarphones, shouldLoop, alsoDriveSpeaker);
        }

        // Looping stages (Stage 2 + Emergency): leave them sounding. DisableCriticalAudioAsync (fired by
        // Slide to Stop / Stop Trip) or the next stage taking over is what stops the ringtone and restores
        // the ringer — we must NOT auto-stop it here.
        if (shouldLoop)
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

    // The five Settings options, in display order. ALL five are now bundled, intentionally high-intensity
    // alarm assets shipped in res/raw (see GetBundledAlarmUri) — real files, so they sound identical and
    // loud on every device and route through the alarm channel below. The device sound catalogue
    // (GetDistinctSoundUris) is kept only as an emergency fallback if a bundled resource ever fails to
    // resolve. (Soft notification tones like "Chime" were retired — too gentle to wake a sleeping commuter.)
    private static readonly string[] SoundOrder = { "Digital Clock", "Siren", "Buzzer", "Bell", "Air Horn" };
    private static IReadOnlyList<global::Android.Net.Uri>? _distinctSoundUris;
    private static readonly object _soundUriLock = new();

    // Build (once) a list of distinct, loud sound URIs from the device's own ringtone catalogue. We seed
    // with the three system defaults (alarm / ringtone / notification) and then top up from the ringtone
    // lists until we have five different sounds, so every Settings option is clearly distinguishable on
    // whatever device or emulator this runs on (the old map reused the same tone for several options).
    private static IReadOnlyList<global::Android.Net.Uri> GetDistinctSoundUris()
    {
        if (_distinctSoundUris is not null) return _distinctSoundUris;
        lock (_soundUriLock)
        {
            if (_distinctSoundUris is not null) return _distinctSoundUris;

            var list = new List<global::Android.Net.Uri>();
            void AddIfNew(global::Android.Net.Uri? u)
            {
                if (u is null) return;
                var s = u.ToString();
                if (!list.Any(x => x.ToString() == s)) list.Add(u);
            }

            AddIfNew(RingtoneManager.GetDefaultUri(RingtoneType.Alarm));
            AddIfNew(RingtoneManager.GetDefaultUri(RingtoneType.Ringtone));
            AddIfNew(RingtoneManager.GetDefaultUri(RingtoneType.Notification));

            foreach (var type in new[] { RingtoneType.Alarm, RingtoneType.Ringtone, RingtoneType.Notification })
            {
                if (list.Count >= 5) break;
                try
                {
                    var mgr = new RingtoneManager(AndroidApplication.Context);
                    mgr.SetType(type);
                    var cursor = mgr.Cursor;
                    if (cursor is not null)
                    {
                        while (list.Count < 5 && cursor.MoveToNext())
                            AddIfNew(mgr.GetRingtoneUri(cursor.Position));
                    }
                }
                catch { /* enumeration unsupported on this device — defaults above still stand */ }
            }

            _distinctSoundUris = list;
            return _distinctSoundUris;
        }
    }

    private global::Android.Net.Uri? GetRingtoneUri(string soundKey)
    {
        // Custom (Feature 1): play the rider's imported file from app-private storage. If it's been
        // deleted out from under us, fall through to the bundled default below rather than going silent —
        // this is the whitelist safety net the spec requires.
        if (string.Equals(soundKey, AlarmSoundCatalog.CustomKey, StringComparison.OrdinalIgnoreCase))
        {
            var custom = GetCustomAlarmUri();
            if (custom is not null) return custom;
            soundKey = AlarmSoundCatalog.Default;
        }

        // Bundled assets win next — they're real files we ship, so they sound identical on every device
        // and are guaranteed loud/aggressive regardless of what the device's ringtone catalogue holds.
        var bundled = GetBundledAlarmUri(soundKey);
        if (bundled is not null) return bundled;

        var uris = GetDistinctSoundUris();
        if (uris.Count == 0)
            return RingtoneManager.GetDefaultUri(RingtoneType.Alarm);

        var idx = Array.IndexOf(SoundOrder, soundKey);
        if (idx < 0) idx = 0;
        return uris[Math.Min(idx, uris.Count - 1)];
    }

    // Resolves each of the five Settings options to its bundled res/raw audio file via an
    // android.resource:// URI. Returns null for an unknown key so the caller falls back to the device
    // sound catalogue. Wrapped in try/catch so a missing resource can never take the alarm down — it just
    // degrades to a system sound.
    private static global::Android.Net.Uri? GetBundledAlarmUri(string soundKey)
    {
        try
        {
            int resId;
            switch (soundKey)
            {
                case "Digital Clock": resId = global::AlarmaApp.Resource.Raw.digital_clock; break;
                case "Siren":         resId = global::AlarmaApp.Resource.Raw.siren;         break;
                case "Buzzer":        resId = global::AlarmaApp.Resource.Raw.buzzer;        break;
                case "Bell":          resId = global::AlarmaApp.Resource.Raw.bell;          break;
                case "Air Horn":      resId = global::AlarmaApp.Resource.Raw.air_horn;      break;
                default: return null;
            }

            var pkg = AndroidApplication.Context.PackageName;
            return global::Android.Net.Uri.Parse($"android.resource://{pkg}/{resId}");
        }
        catch
        {
            return null;
        }
    }

    // Resolves the "Custom" key to a file:// URI for the rider's imported clip. Returns null if no custom
    // sound is configured OR the file is missing, so the caller falls back to a bundled voice — a deleted
    // custom file degrades gracefully instead of crashing or playing nothing. AudioAttributes are still
    // applied by StartAlarmPlaybackLocked, so a custom file rides the alarm channel exactly like a bundled one.
    private global::Android.Net.Uri? GetCustomAlarmUri()
    {
        try
        {
            var path = _preferencesService.CustomSoundPath;
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                return null;
            return global::Android.Net.Uri.FromFile(new Java.IO.File(path));
        }
        catch
        {
            return null;
        }
    }

    // Probe a candidate custom-sound file's playable duration (seconds) so the controller can reject a
    // file the alarm loop can't use. Returns 0 on any failure (unreadable / not really audio).
    public Task<double> GetAudioDurationSecondsAsync(string filePath)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
                return 0d;

            MediaMetadataRetriever? retriever = null;
            try
            {
                retriever = new MediaMetadataRetriever();
                retriever.SetDataSource(filePath);
                var raw = retriever.ExtractMetadata(MetadataKey.Duration);
                if (long.TryParse(raw, out var millis) && millis > 0)
                    return millis / 1000.0;
                return 0d;
            }
            catch
            {
                return 0d;
            }
            finally
            {
                try { retriever?.Release(); } catch { }
            }
        });
    }

    // Builds and starts the alarm with its routing baked in from the very first instant. We use a
    // MediaPlayer (not a Ringtone) on purpose: MediaPlayer lets us set the AudioAttributes BEFORE prepare,
    // so the OS binds the sound to exactly one stream up front. The old path set attributes on a Ringtone
    // AFTER it was created, which a lot of OEMs silently ignore — that ignored, too-late attribute is what
    // let UsageAlarm slip back onto the phone speaker even while it also played through the earbuds.
    //   - Earbuds in  → UsageMedia / STREAM_MUSIC → routes EXCLUSIVELY to the earbuds, like a music app.
    //   - Earbuds out → UsageAlarm / STREAM_ALARM → routes EXCLUSIVELY to the device speaker.
    // The caller already holds _ringtoneLock.
    private void StartAlarmPlaybackLocked(global::Android.Net.Uri uri, bool routeToEarphones, bool shouldLoop, bool alsoDriveSpeaker)
    {
        var usage = routeToEarphones ? AudioUsageKind.Media : AudioUsageKind.Alarm;
        var contentType = routeToEarphones ? AudioContentType.Music : AudioContentType.Sonification;

        var primary = TryBuildAlarmPlayer(uri, usage, contentType, shouldLoop);
        if (primary is not null)
        {
            _mediaPlayer = primary;
        }
        else
        {
            // MediaPlayer can occasionally reject an OEM resource URI. Rather than leave the rider with a
            // silent alarm, fall back to a plain Ringtone — it still gets the attributes applied, just via
            // the older, less reliable late-set path.
            try
            {
                _ringtone = RingtoneManager.GetRingtone(AndroidApplication.Context, uri);
                if (_ringtone is not null)
                {
                    RouteThroughAlarmChannel(_ringtone, routeToEarphones);
                    if (shouldLoop && Build.VERSION.SdkInt >= BuildVersionCodes.P)
                        _ringtone.Looping = true;
                    _ringtone.Play();
                }
            }
            catch
            {
                // Even the fallback failed — the vibration path still covers the rider.
            }
        }

        // Emergency stage with earbuds connected: spin up a SECOND player pinned to the alarm channel
        // (speaker) alongside the earbud copy above. Fully independent — if the speaker player fails to
        // build, the primary is untouched. This is the pocketed-earbud safeguard, Emergency-only.
        if (alsoDriveSpeaker)
        {
            _emergencySpeakerPlayer = TryBuildAlarmPlayer(
                uri, AudioUsageKind.Alarm, AudioContentType.Sonification, shouldLoop);
        }
    }

    // Builds a MediaPlayer with the given audio attributes pinned BEFORE prepare (so the OS locks it to one
    // output stream from the start) and starts it. Returns null if the player can't be built.
    private static MediaPlayer? TryBuildAlarmPlayer(
        global::Android.Net.Uri uri, AudioUsageKind usage, AudioContentType contentType, bool shouldLoop)
    {
        try
        {
            var player = new MediaPlayer();
            player.SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(usage)!
                .SetContentType(contentType)!
                .Build());
            player.SetDataSource(AndroidApplication.Context, uri);
            // Stage 2 + Emergency keep sounding until superseded/dismissed — MediaPlayer loops natively on
            // every API level we support, so we don't have the old API-28 looping limitation here.
            player.Looping = shouldLoop;
            player.Prepare();
            player.Start();
            return player;
        }
        catch
        {
            return null;
        }
    }

    // True when DND is on and we can't override it, so the Emergency lockout risks being muted.
    public bool IsDndRestrictingAlarm()
    {
        try
        {
            var nm = AndroidApplication.Context.GetSystemService(Context.NotificationService) as NotificationManager;
            if (nm is null)
                return false;

            // "None" (total silence) and "Priority only" DND can both gag the alarm channel; "Alarms only"
            // explicitly lets alarms through, and "All" means DND is off — neither of those is a risk.
            var dndActive = nm.CurrentInterruptionFilter == InterruptionFilter.None
                            || nm.CurrentInterruptionFilter == InterruptionFilter.Priority;
            return EmergencyAudioPolicy.ShouldWarnDndMayMuteAlarm(dndActive, nm.IsNotificationPolicyAccessGranted);
        }
        catch
        {
            return false;
        }
    }

    // Stop and FULLY release both possible alarm players: the primary MediaPlayer and the fallback
    // Ringtone. Releasing the MediaPlayer (not just stopping it) hands its bound output stream back to the
    // OS, which is what lets the next trigger rebuild its routing from scratch. Caller holds _ringtoneLock.
    private void ReleaseAlarmPlayersLocked()
    {
        if (_mediaPlayer is not null)
        {
            try { if (_mediaPlayer.IsPlaying) _mediaPlayer.Stop(); } catch { }
            try { _mediaPlayer.Reset(); } catch { }
            try { _mediaPlayer.Release(); } catch { }
            _mediaPlayer = null;
        }

        if (_emergencySpeakerPlayer is not null)
        {
            try { if (_emergencySpeakerPlayer.IsPlaying) _emergencySpeakerPlayer.Stop(); } catch { }
            try { _emergencySpeakerPlayer.Reset(); } catch { }
            try { _emergencySpeakerPlayer.Release(); } catch { }
            _emergencySpeakerPlayer = null;
        }

        if (_ringtone is not null)
        {
            try { if (_ringtone.IsPlaying) _ringtone.Stop(); } catch { }
            _ringtone = null;
        }
    }

    // Fallback-only now (the primary path is MediaPlayer in StartAlarmPlaybackLocked). Tags a Ringtone with
    // the audio attributes for whichever output we picked. ALARM usage rides the alarm channel we've maxed
    // out (GetStageVolume → Stage 3 = max alarm volume) and hits the speaker; MEDIA usage follows the MUSIC
    // stream so, with earbuds in, the sound stays in the rider's ears instead of also blaring out the phone
    // speaker. API 21+, so always available at our min SDK 26.
    private static void RouteThroughAlarmChannel(Ringtone ringtone, bool routeToEarphones)
    {
        try
        {
            var usage = routeToEarphones ? AudioUsageKind.Media : AudioUsageKind.Alarm;
            var contentType = routeToEarphones ? AudioContentType.Music : AudioContentType.Sonification;
            ringtone.AudioAttributes = new AudioAttributes.Builder()
                .SetUsage(usage)!
                .SetContentType(contentType)!
                .Build();
        }
        catch
        {
            // Some OEM ringtone implementations reject a late AudioAttributes set — harmless, the sound
            // still plays, just on its default stream.
        }
    }
}
