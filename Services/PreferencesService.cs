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
    private const string RecentSearchesKey = "recent_searches";
    // Feature 1 — user-imported custom alarm sound: a stable on-device path + the label to show.
    private const string CustomSoundPathKey = "custom_alarm_sound_path";
    private const string CustomSoundNameKey = "custom_alarm_sound_name";
    // Feature 2 — distinct sound per escalation stage. Empty string = "inherit the single AlarmSound".
    // Stage 1 is vibration-only by design, so it has no stored per-stage sound.
    private const string Stage2SoundKey = "alarm_sound_stage2";
    private const string Stage3SoundKey = "alarm_sound_stage3";
    // Feature 3 — how far past the drop-off (metres) before an overshoot can latch.
    private const string OvershootDistanceKey = "overshoot_distance_meters";
    // The live trip, serialized, so a process kill mid-commute doesn't lose the destination + alarm state.
    private const string ActiveTripKey = "active_trip_state";

    public string AlarmSound
    {
        get => Preferences.Get(AlarmSoundKey, "Digital Clock");
        set => Preferences.Set(AlarmSoundKey, value);
    }

    // Absolute path to the rider's imported custom alarm clip in app-private storage (empty if none).
    public string CustomSoundPath
    {
        get => Preferences.Get(CustomSoundPathKey, string.Empty);
        set => Preferences.Set(CustomSoundPathKey, value ?? string.Empty);
    }

    // Friendly label for the imported clip (typically the original file name), shown in Settings.
    public string CustomSoundName
    {
        get => Preferences.Get(CustomSoundNameKey, string.Empty);
        set => Preferences.Set(CustomSoundNameKey, value ?? string.Empty);
    }

    // Per-stage sound choices. Empty = inherit the single AlarmSound (no regression for existing users).
    // Stage 1 has no entry — it's vibration-only by design.
    public string Stage2Sound
    {
        get => Preferences.Get(Stage2SoundKey, string.Empty);
        set => Preferences.Set(Stage2SoundKey, value ?? string.Empty);
    }

    public string Stage3Sound
    {
        get => Preferences.Get(Stage3SoundKey, string.Empty);
        set => Preferences.Set(Stage3SoundKey, value ?? string.Empty);
    }

    // Distance past the destination (metres) before a confirmed overshoot latches. Default 250 keeps
    // today's behaviour (ArrivalThreshold 200 + 250 buffer = 450 m). Clamped by HomeController/BackupService.
    public int OvershootDistanceMeters
    {
        get => Preferences.Get(OvershootDistanceKey, 250);
        set => Preferences.Set(OvershootDistanceKey, value);
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

    // The rider's last few destination picks, stored as a JSON blob. The controller is the one that
    // serializes/deserializes the list and enforces the 5-item cap — here we just keep the raw string so
    // the recent-searches list survives an app restart. Empty string means "nothing saved yet".
    public string RecentSearchesJson
    {
        get => Preferences.Get(RecentSearchesKey, string.Empty);
        set => Preferences.Set(RecentSearchesKey, value);
    }

    // The in-flight trip as a JSON blob (destination, accumulated distance, alarm stage + latches).
    // Written as the trip progresses and replayed on launch to recover from a process kill; empty string
    // means "no active trip", which is also how a clean stop wipes it so there's no ghost trip to resume.
    public string ActiveTripJson
    {
        get => Preferences.Get(ActiveTripKey, string.Empty);
        set => Preferences.Set(ActiveTripKey, value ?? string.Empty);
    }

}
