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
}
