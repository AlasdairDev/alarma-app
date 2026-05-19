using System.Text.Json;
using AlarmaApp.Models;
using Microsoft.Maui.Storage;

namespace AlarmaApp.Services;

public class BackupService
{
    private const string BackupFolderName = "backup";
    private const string BackupFilePrefix = "alarma-backup-";
    private readonly DatabaseService _databaseService;
    private readonly PreferencesService _preferencesService;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true
    };

    public BackupService(DatabaseService databaseService, PreferencesService preferencesService)
    {
        _databaseService = databaseService;
        _preferencesService = preferencesService;
    }

    public async Task<string> ExportAsync()
    {
        var payload = new BackupPayload(
            DateTimeOffset.UtcNow,
            new BackupPreferences(
                _preferencesService.AlarmSound,
                _preferencesService.AlarmLeadMinutes,
                _preferencesService.VibrationOnly),
            await _databaseService.GetEmergencyContactsAsync(),
            await _databaseService.GetSavedRoutesAsync(),
            await _databaseService.GetTripHistoryAsync(),
            await _databaseService.GetBehavioralProfilesAsync());

        var backupFolder = Path.Combine(FileSystem.AppDataDirectory, BackupFolderName);
        Directory.CreateDirectory(backupFolder);
        var fileName = $"{BackupFilePrefix}{payload.ExportedAtUtc:yyyyMMdd-HHmmss}.json";
        var filePath = Path.Combine(backupFolder, fileName);
        var json = JsonSerializer.Serialize(payload, _serializerOptions);
        await File.WriteAllTextAsync(filePath, json);
        _preferencesService.LastBackupUtc = payload.ExportedAtUtc;
        return filePath;
    }

    public async Task<string?> RestoreLatestAsync()
    {
        var backupFolder = Path.Combine(FileSystem.AppDataDirectory, BackupFolderName);
        if (!Directory.Exists(backupFolder))
        {
            return null;
        }

        var latestFile = Directory.GetFiles(backupFolder, $"{BackupFilePrefix}*.json")
            .OrderByDescending(path => path)
            .FirstOrDefault();
        if (latestFile is null)
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(latestFile);
        var payload = JsonSerializer.Deserialize<BackupPayload>(json, _serializerOptions);
        if (payload is null)
        {
            return null;
        }

        await _databaseService.ClearTripHistoryAsync();
        await _databaseService.ClearSavedRoutesAsync();
        await _databaseService.ClearEmergencyContactsAsync();
        await _databaseService.ClearBehavioralProfilesAsync();

        if (payload.EmergencyContacts.Count > 0)
        {
            await _databaseService.InsertAllAsync(payload.EmergencyContacts);
        }

        if (payload.SavedRoutes.Count > 0)
        {
            await _databaseService.InsertAllAsync(payload.SavedRoutes);
        }

        if (payload.TripHistory.Count > 0)
        {
            await _databaseService.InsertAllAsync(payload.TripHistory);
        }

        if (payload.BehavioralProfiles.Count > 0)
        {
            await _databaseService.InsertAllAsync(payload.BehavioralProfiles);
        }

        _preferencesService.AlarmSound = payload.Preferences.AlarmSound;
        _preferencesService.AlarmLeadMinutes = payload.Preferences.AlarmLeadMinutes;
        _preferencesService.VibrationOnly = payload.Preferences.VibrationOnly;
        _preferencesService.LastBackupUtc = payload.ExportedAtUtc;

        return latestFile;
    }
}
