using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlarmaApp.Models;
using Microsoft.Maui.Storage;

namespace AlarmaApp.Services;

public class BackupService
{
    private const string BackupFolderName = "backup";
    private const string BackupFilePrefix = "alarma-backup-";
    private const string BackupFileExtension = ".alarma";
    private const string BackupKeyName = "alarma_backup_key_v2"; // v2 = AES-GCM key slot

    // AES-256-GCM parameters
    private const int GcmNonceSize = 12;
    private const int GcmTagSize = 16;

    // Restore caps — matches the in-app add limits to prevent unbounded inserts from tampered backups
    private const int MaxRestoreContacts = 3;
    private const int MaxRestoreRoutes = 5;
    private const int MaxRestoreHistory = 100;
    private const int MaxRestoreProfiles = 20;
    private const int MaxDisplayNameLength = 200;

    private readonly DatabaseService _databaseService;
    private readonly PreferencesService _preferencesService;
    private readonly JsonSerializerOptions _serializerOptions = new() { WriteIndented = true };

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

        var json = JsonSerializer.Serialize(payload, _serializerOptions);
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var key = await GetOrCreateBackupKeyAsync();
        var encrypted = Encrypt(jsonBytes, key);

        var backupFolder = Path.Combine(FileSystem.AppDataDirectory, BackupFolderName);
        Directory.CreateDirectory(backupFolder);
        var fileName = $"{BackupFilePrefix}{payload.ExportedAtUtc:yyyyMMdd-HHmmss}{BackupFileExtension}";
        var filePath = Path.Combine(backupFolder, fileName);
        await File.WriteAllBytesAsync(filePath, encrypted);
        _preferencesService.LastBackupUtc = payload.ExportedAtUtc;
        return filePath;
    }

    public async Task<string?> RestoreLatestAsync()
    {
        var backupFolder = Path.Combine(FileSystem.AppDataDirectory, BackupFolderName);
        if (!Directory.Exists(backupFolder))
            return null;

        var latestFile = Directory.GetFiles(backupFolder, $"{BackupFilePrefix}*{BackupFileExtension}")
            .OrderByDescending(p => p)
            .FirstOrDefault();
        if (latestFile is null)
            return null;

        var key = await GetOrCreateBackupKeyAsync();
        var encrypted = await File.ReadAllBytesAsync(latestFile);
        byte[] jsonBytes;
        try
        {
            jsonBytes = Decrypt(encrypted, key);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("Backup file could not be decrypted. It may be corrupted or from a different device.");
        }

        var json = Encoding.UTF8.GetString(jsonBytes);
        var payload = JsonSerializer.Deserialize<BackupPayload>(json, _serializerOptions);
        if (payload is null)
            return null;

        await _databaseService.ClearTripHistoryAsync();
        await _databaseService.ClearSavedRoutesAsync();
        await _databaseService.ClearEmergencyContactsAsync();
        await _databaseService.ClearBehavioralProfilesAsync();

        var validContacts = payload.EmergencyContacts
            .Where(c => IsValidPhilippineNumber(c.PhoneNumber))
            .Take(MaxRestoreContacts)
            .ToList();
        if (validContacts.Count > 0)
            await _databaseService.InsertAllAsync(validContacts);

        var routesToRestore = payload.SavedRoutes.Take(MaxRestoreRoutes).ToList();
        if (routesToRestore.Count > 0)
            await _databaseService.InsertAllAsync(routesToRestore);

        var historyToRestore = payload.TripHistory.Take(MaxRestoreHistory).ToList();
        if (historyToRestore.Count > 0)
            await _databaseService.InsertAllAsync(historyToRestore);

        var profilesToRestore = payload.BehavioralProfiles.Take(MaxRestoreProfiles).ToList();
        if (profilesToRestore.Count > 0)
            await _databaseService.InsertAllAsync(profilesToRestore);

        _preferencesService.AlarmSound = payload.Preferences.AlarmSound;
        _preferencesService.AlarmLeadMinutes = payload.Preferences.AlarmLeadMinutes;
        _preferencesService.VibrationOnly = payload.Preferences.VibrationOnly;
        _preferencesService.LastBackupUtc = payload.ExportedAtUtc;
        return latestFile;
    }

    private static async Task<byte[]> GetOrCreateBackupKeyAsync()
    {
        var stored = await SecureStorage.GetAsync(BackupKeyName);
        if (!string.IsNullOrEmpty(stored))
            return Convert.FromBase64String(stored);

        var key = RandomNumberGenerator.GetBytes(32);
        await SecureStorage.SetAsync(BackupKeyName, Convert.ToBase64String(key));
        return key;
    }

    // Format: [12-byte nonce][16-byte GCM tag][AES-256-GCM ciphertext]
    // AES-GCM provides both confidentiality and integrity — any bit-flip in the ciphertext or tag
    // causes Decrypt to throw CryptographicException before any plaintext is returned.
    private static byte[] Encrypt(byte[] plaintext, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(GcmNonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[GcmTagSize];

        using var aesGcm = new AesGcm(key, GcmTagSize);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);

        var output = new byte[GcmNonceSize + GcmTagSize + ciphertext.Length];
        nonce.CopyTo(output, 0);
        tag.CopyTo(output, GcmNonceSize);
        ciphertext.CopyTo(output, GcmNonceSize + GcmTagSize);
        return output;
    }

    private static byte[] Decrypt(byte[] data, byte[] key)
    {
        if (data.Length < GcmNonceSize + GcmTagSize + 1)
            throw new CryptographicException("Backup data is too short to be valid.");

        var nonce = data[..GcmNonceSize];
        var tag = data[GcmNonceSize..(GcmNonceSize + GcmTagSize)];
        var ciphertext = data[(GcmNonceSize + GcmTagSize)..];
        var plaintext = new byte[ciphertext.Length];

        using var aesGcm = new AesGcm(key, GcmTagSize);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    private static bool IsValidPhilippineNumber(string number) =>
        !string.IsNullOrWhiteSpace(number)
        && System.Text.RegularExpressions.Regex.IsMatch(
            number.Trim(), @"^(09\d{9}|\+639\d{9})$");
}
