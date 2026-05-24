using AlarmaApp.Models;

namespace AlarmaApp.Services.Interfaces;

public interface IAlarmAudioService
{
    Task EnableCriticalAudioAsync();
    Task DisableCriticalAudioAsync();
    Task TriggerAlarmAsync(AlarmStage stage, string soundKey, bool vibrationOnly, string vibrationIntensity = "Medium");
}
