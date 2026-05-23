using Microsoft.Maui.Storage;

namespace AlarmaApp.Services;

public class PreferencesService
{
    private const string AlarmSoundKey = "alarm_sound";
    private const string AlarmLeadMinutesKey = "alarm_lead_minutes";
    private const string VibrationOnlyKey = "vibration_only";
    private const string VibrationIntensityKey = "vibration_intensity";
    private const string VehicleTypeKey = "vehicle_type";
    private const string OnboardingCompleteKey = "onboarding_complete";
    private const string LastBackupUtcKey = "last_backup_utc";
    private const string HasSeenTutorialKey = "has_seen_tutorial";

    public string AlarmSound
    {
        get => Preferences.Get(AlarmSoundKey, "Default");
        set => Preferences.Set(AlarmSoundKey, value);
    }

    public int AlarmLeadMinutes
    {
        get => Preferences.Get(AlarmLeadMinutesKey, 5);
        set => Preferences.Set(AlarmLeadMinutesKey, value);
    }

    public bool VibrationOnly
    {
        get => Preferences.Get(VibrationOnlyKey, false);
        set => Preferences.Set(VibrationOnlyKey, value);
    }

    public string VibrationIntensity
    {
        get => Preferences.Get(VibrationIntensityKey, "Medium");
        set => Preferences.Set(VibrationIntensityKey, value);
    }

    public string VehicleType
    {
        get => Preferences.Get(VehicleTypeKey, "Jeepney");
        set => Preferences.Set(VehicleTypeKey, value);
    }

    public bool IsOnboardingComplete
    {
        get => Preferences.Get(OnboardingCompleteKey, false);
        set => Preferences.Set(OnboardingCompleteKey, value);
    }

    public DateTimeOffset? LastBackupUtc
    {
        get
        {
            var stored = Preferences.Get(LastBackupUtcKey, string.Empty);
            return DateTimeOffset.TryParse(stored, out var parsed) ? parsed : null;
        }
        set => Preferences.Set(LastBackupUtcKey, value?.ToString("O") ?? string.Empty);
    }

    public bool HasSeenTutorial
    {
        get => Preferences.Get(HasSeenTutorialKey, false);
        set => Preferences.Set(HasSeenTutorialKey, value);
    }

}
