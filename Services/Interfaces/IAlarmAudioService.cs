using AlarmaApp.Models;

namespace AlarmaApp.Services.Interfaces;

public interface IAlarmAudioService
{
    Task EnableCriticalAudioAsync();
    Task DisableCriticalAudioAsync();
    Task TriggerAlarmAsync(AlarmStage stage, string soundKey, bool vibrationOnly, string vibrationIntensity = "Medium");

    // Short confirmation cue (a brief beep + buzz) for the SOS button. Deliberately does NOT alter
    // ringer mode, stream volume, or Do-Not-Disturb — unlike the escalating trip alarm.
    Task PlaySosFeedbackAsync();

    // Settings sound picker — play a short, self-contained preview of the given sound key so the rider
    // can hear what they're choosing. Like the SOS cue, it leaves ringer/volume/DND untouched.
    Task PreviewSoundAsync(string soundKey);

    // Stop any in-flight preview immediately (another sound tapped, or the user left Settings).
    Task StopPreviewAsync();

    // Probe the playable duration (seconds) of an on-device audio file, used to validate a custom
    // alarm import. Returns 0 if the file can't be decoded so the caller can reject it.
    Task<double> GetAudioDurationSecondsAsync(string filePath);

    // True when Do-Not-Disturb is on AND we lack the notification-policy access needed to override it, so
    // the Emergency lockout could be muted. The caller warns the rider (and offers to grant access).
    bool IsDndRestrictingAlarm();
}
