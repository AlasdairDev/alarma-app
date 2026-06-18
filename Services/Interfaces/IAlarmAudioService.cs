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
}
