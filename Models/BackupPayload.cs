namespace AlarmaApp.Models;

public record BackupPreferences(
    string AlarmSound,
    int AlarmLeadMinutes,
    bool VibrationOnly,
    // Feature 2 — per-stage sound choices (null/empty = inherit AlarmSound). Stage 1 is vibration-only
    // by design and has no sound; older backups that still carry a "Stage1Sound" key load fine — the
    // unknown JSON property is simply ignored on restore.
    string? Stage2Sound = null,
    string? Stage3Sound = null,
    // Feature 1 — a stable reference to the imported custom clip. The audio bytes are NOT backed up;
    // on restore the path is re-validated for existence and the selection falls back if it's gone.
    string? CustomSoundPath = null,
    string? CustomSoundName = null,
    // Feature 3 — adjustable overshoot trigger distance (metres past the drop-off).
    int OvershootDistanceMeters = 250);

public record BackupPayload(
    DateTimeOffset ExportedAtUtc,
    BackupPreferences Preferences,
    List<EmergencyContact> EmergencyContacts,
    List<SavedRoute> SavedRoutes,
    List<TripHistory> TripHistory,
    List<BehavioralProfile> BehavioralProfiles);
