using Microsoft.Maui.Storage;

namespace AlarmaApp.Services;

public class PreferencesService
{
    private const string AlarmSoundKey = "alarm_sound";
    private const string AlarmLeadMinutesKey = "alarm_lead_minutes";
    private const string VibrationOnlyKey = "vibration_only";
    private const string OnboardingCompleteKey = "onboarding_complete";
    private const string EmergencyContactKey = "emergency_contact_number";
    private const string LastBackupUtcKey = "last_backup_utc";

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

    public bool IsOnboardingComplete
    {
        get => Preferences.Get(OnboardingCompleteKey, false);
        set => Preferences.Set(OnboardingCompleteKey, value);
    }

    public string EmergencyContactNumber
    {
        get => Preferences.Get(EmergencyContactKey, string.Empty);
        set => Preferences.Set(EmergencyContactKey, value);
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
}
