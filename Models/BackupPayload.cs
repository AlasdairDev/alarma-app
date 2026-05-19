namespace AlarmaApp.Models;

public record BackupPreferences(
    string AlarmSound,
    int AlarmLeadMinutes,
    bool VibrationOnly);

public record BackupPayload(
    DateTimeOffset ExportedAtUtc,
    BackupPreferences Preferences,
    List<EmergencyContact> EmergencyContacts,
    List<SavedRoute> SavedRoutes,
    List<TripHistory> TripHistory,
    List<BehavioralProfile> BehavioralProfiles);
