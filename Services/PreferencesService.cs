using Microsoft.Maui.Storage;

namespace AlarmaApp.Services;

public class PreferencesService
{
    private const string AlarmSoundKey = "alarm_sound";
    private const string AlarmLeadMinutesKey = "alarm_lead_minutes";
    private const string VibrationOnlyKey = "vibration_only";
    private const string OnboardingCompleteKey = "onboarding_complete";
    private const string LastBackupUtcKey = "last_backup_utc";
    private const string HasSeenTutorialKey = "has_seen_tutorial";
    private const string HasAgreedToTermsKey = "has_agreed_to_terms";
    private const string HasCompletedPermissionsSetupKey = "has_completed_permissions_setup";
    private const string HasSeenSosWarningKey = "has_seen_sos_warning";

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

    public bool HasAgreedToTerms
    {
        get => Preferences.Get(HasAgreedToTermsKey, false);
        set => Preferences.Set(HasAgreedToTermsKey, value);
    }

    public bool HasCompletedPermissionsSetup
    {
        get => Preferences.Get(HasCompletedPermissionsSetupKey, false);
        set => Preferences.Set(HasCompletedPermissionsSetupKey, value);
    }

    public bool HasSeenSosWarning
    {
        get => Preferences.Get(HasSeenSosWarningKey, false);
        set => Preferences.Set(HasSeenSosWarningKey, value);
    }

}
