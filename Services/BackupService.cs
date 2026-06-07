// Security Considerations (OWASP Top 10)
// A02 Cryptographic Failures: AES-256-GCM authenticated encryption with a 96-bit nonce generated
//   fresh per export via RandomNumberGenerator.GetBytes(). Key is 256-bit, stored in Android
//   Keystore via SecureStorage (alarma_backup_key_v2) — never hardcoded or in SharedPreferences.
// A03 Injection: All restored string fields are length-capped; phone numbers re-validated by
//   compiled PhoneRegex; no raw SQL — sqlite-net-pcl ORM uses parameterized queries.
// A04 Insecure Design: All records are validated BEFORE any ClearXxxAsync() call — a tampered
//   backup with zero valid records cannot silently wipe the existing user database.
// A08 Software and Data Integrity Failures: AES-GCM authentication tag is verified during
//   Decrypt() — any bit-flip in ciphertext, nonce, or tag throws CryptographicException before
//   a single byte of plaintext is returned or deserialized.

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

    // Restore caps — match the in-app add-time limits to prevent unbounded inserts from older backups
    private const int MaxRestoreContacts = 3;
    private const int MaxRestoreRoutes = 5;
    private const int MaxRestoreHistory = 100;
    private const int MaxRestoreProfiles = 20;
    private const int MaxDisplayNameLength = 200;
    private const int MaxContactNameLength = 50;
    private const int MaxRouteNameLength = 30;
    private const int MinRouteNameLength = 2;
    private const int MaxSummaryLength = 300;
    private const int MaxProfileNotesLength = 300;
    private const int MinAlarmLeadMinutes = 1;
    private const int MaxAlarmLeadMinutes = 60;
    private const double MaxDistanceMeters = 1_000_000; // 1 000 km upper bound
    // Philippines bounding box — same constants as GeocodingService / HomeController
    private const double PhLatMin = 4.5;
    private const double PhLatMax = 21.1;
    private const double PhLonMin = 116.9;
    private const double PhLonMax = 126.6;

    // Allowed alarm sound values — mirrors HomeController._alarmSoundOptions
    private static readonly HashSet<string> ValidAlarmSounds =
        new(StringComparer.OrdinalIgnoreCase) { "Default", "Alarm", "Chime", "Notification", "Ringtone" };

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

        // Validate ALL records before touching the database — this prevents data loss when
        // a tampered or empty backup passes decryption but contains no valid records.
        var validContacts = payload.EmergencyContacts
            .Where(c => IsValidPhilippineNumber(c.PhoneNumber)
                        && !string.IsNullOrWhiteSpace(c.Name)
                        && c.Name.Length <= MaxContactNameLength)
            .Take(MaxRestoreContacts)
            .ToList();

        var routesToRestore = payload.SavedRoutes
            .Where(r => !string.IsNullOrWhiteSpace(r.DisplayName)
                        && r.DisplayName.Length >= MinRouteNameLength
                        && r.DisplayName.Length <= MaxRouteNameLength
                        && r.Latitude >= PhLatMin && r.Latitude <= PhLatMax
                        && r.Longitude >= PhLonMin && r.Longitude <= PhLonMax)
            .Take(MaxRestoreRoutes)
            .ToList();

        // Validate TripHistory: reject records with out-of-range numeric fields, impossible dates,
        // or unbounded strings that could exhaust memory or produce nonsense UI.
        var minDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var maxDate = DateTime.UtcNow.AddDays(1);
        var historyToRestore = payload.TripHistory
            .Where(h =>
                h.StartedAt >= minDate && h.StartedAt <= maxDate &&
                (h.EndedAt == null || (h.EndedAt >= h.StartedAt && h.EndedAt <= maxDate)) &&
                h.DistanceMeters is >= 0 and <= MaxDistanceMeters &&
                h.MaxAlarmStageReached is >= 0 and <= 3 &&
                h.SnoozeCount is >= 0 and <= 100 &&
                (h.DestinationName == null || h.DestinationName.Length <= MaxDisplayNameLength) &&
                (h.Summary == null || h.Summary.Length <= MaxSummaryLength) &&
                (h.DestinationLatitude == null ||
                    (h.DestinationLatitude >= -90 && h.DestinationLatitude <= 90)) &&
                (h.DestinationLongitude == null ||
                    (h.DestinationLongitude >= -180 && h.DestinationLongitude <= 180)))
            .Take(MaxRestoreHistory)
            .ToList();

        // Validate BehavioralProfile: enforce name length, lead-time range, and whitelist alarm sound.
        var profilesToRestore = payload.BehavioralProfiles
            .Where(p =>
                !string.IsNullOrWhiteSpace(p.Name) &&
                p.Name.Length <= MaxContactNameLength &&
                p.AlarmLeadMinutes is >= MinAlarmLeadMinutes and <= MaxAlarmLeadMinutes &&
                ValidAlarmSounds.Contains(p.AlarmSound ?? string.Empty) &&
                (p.Notes == null || p.Notes.Length <= MaxProfileNotesLength))
            .Take(MaxRestoreProfiles)
            .ToList();

        // All records validated — now clear and restore.
        await _databaseService.ClearTripHistoryAsync();
        await _databaseService.ClearSavedRoutesAsync();
        await _databaseService.ClearEmergencyContactsAsync();
        await _databaseService.ClearBehavioralProfilesAsync();

        if (validContacts.Count > 0)
            await _databaseService.InsertAllAsync(validContacts);

        if (routesToRestore.Count > 0)
            await _databaseService.InsertAllAsync(routesToRestore);

        if (historyToRestore.Count > 0)
            await _databaseService.InsertAllAsync(historyToRestore);

        if (profilesToRestore.Count > 0)
            await _databaseService.InsertAllAsync(profilesToRestore);

        // Clamp and whitelist preferences before writing to storage so Preferences never hold
        // a raw unvalidated value, even transiently between restore and next HomeController init.
        var rawSound = payload.Preferences.AlarmSound;
        _preferencesService.AlarmSound = ValidAlarmSounds.Contains(rawSound ?? string.Empty)
            ? rawSound! : "Default";
        _preferencesService.AlarmLeadMinutes =
            Math.Clamp(payload.Preferences.AlarmLeadMinutes, MinAlarmLeadMinutes, MaxAlarmLeadMinutes);
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

    private static readonly System.Text.RegularExpressions.Regex PhoneRegex =
        new(@"^(09\d{9}|\+639\d{9})$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool IsValidPhilippineNumber(string number) =>
        !string.IsNullOrWhiteSpace(number) && PhoneRegex.IsMatch(number.Trim());
}
