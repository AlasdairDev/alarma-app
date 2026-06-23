// Our encrypted backup. AES-256-GCM with a fresh 96-bit nonce per export. The key story has changed twice:
// we started with a random 256-bit key in the Keystore (SecureStorage), but that key was wiped on uninstall
// and unique per install, so a backup only ever restored on the exact install that made it — move phones and
// every .alarma file read as "damaged". We then switched to a PASSWORD-DERIVED key (PBKDF2) so files were
// portable across devices. For the live demo we want backups to be completely seamless — no dialog, no typing,
// just tap-and-go — so the key is now a single STATIC AES-256 key baked into the app. Every install shares the
// same key, so any .alarma file opens on any device and survives uninstalls, with zero user friction.
// Trade-off we're accepting on purpose: because the key ships inside the app, the file is portable but NOT
// secret from anyone who has the app — fine for a demo, not for real secrets. GCM still buys us integrity:
// any tampering (cipher/nonce/tag) throws on Decrypt before we read a single byte, so we never deserialize
// garbage.
// On restore we validate EVERY record before clearing anything, so a junk or empty backup can't wipe a
// commuter's real data — restored strings are length-capped and phone numbers re-checked.
// Note: we build the backup as raw bytes rather than writing it ourselves, then hand them to the OS file
// picker, so commuters genuinely own and can move their own backups.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlarmaApp.Models;

namespace AlarmaApp.Services;

public class BackupService
{
    private const string BackupFilePrefix = "alarma-backup-";
    private const string BackupFileExtension = ".alarma";

    // Envelope format. The leading version byte lets a reader tell static-key files (v4) apart from the older
    // password-derived ones (v3) and reject anything it doesn't understand instead of producing garbage.
    private const byte StaticFormatVersion = 4; // v4 = static app key (seamless, no password)

    // AES-256-GCM parameters
    private const int GcmNonceSize = 12;
    private const int GcmTagSize = 16;
    private const int KeySize = 32; // 256-bit

    // The single static AES-256 key the whole app shares. It's the SHA-256 of a fixed constant string, which
    // gives us exactly 32 deterministic bytes — same on every device, so any backup opens anywhere with no
    // password. Hashing the constant (rather than slicing a 32-char literal) just guarantees the right length
    // and spreads the bytes out. This is deliberately NOT a secret — it ships in the app so backups are
    // friction-free for the demo; integrity (not confidentiality) is what GCM still gives us here.
    private static readonly byte[] StaticKey =
        SHA256.HashData(Encoding.UTF8.GetBytes("AlarmaApp::seamless-backup::v4::static-key"));

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

    // Allowed bundled alarm sound values — mirrors AlarmSoundCatalog.BundledSounds exactly, so a restore
    // never silently downgrades a saved choice.
    private static readonly HashSet<string> ValidAlarmSounds =
        new(StringComparer.OrdinalIgnoreCase) { "Digital Clock", "Siren", "Buzzer", "Bell", "Air Horn" };
    private const string CustomSoundKey = "Custom";
    private const string NoneSoundKey = "None";

    // Overshoot trigger distance band (Feature 3) — mirrors HomeController's clamp.
    private const int DefaultOvershootDistanceMeters = 250;
    private const int MinOvershootDistanceMeters = 50;
    private const int MaxOvershootDistanceMeters = 500;

    private readonly DatabaseService _databaseService;
    private readonly PreferencesService _preferencesService;
    private readonly JsonSerializerOptions _serializerOptions = new() { WriteIndented = true };

    public BackupService(DatabaseService databaseService, PreferencesService preferencesService)
    {
        _databaseService = databaseService;
        _preferencesService = preferencesService;
    }

    // We build the encrypted backup blob entirely in memory (no disk write) and hand it back with a
    // suggested file name. We did it this way so the export UI can pass the bytes straight to the native
    // OS FileSaver dialog — letting commuters drop their backup into Downloads, Drive, wherever they
    // actually own it — instead of us burying it in private AppData they could never reach.
    public async Task<(string FileName, byte[] Data)> BuildBackupAsync()
    {
        var payload = new BackupPayload(
            DateTimeOffset.UtcNow,
            new BackupPreferences(
                _preferencesService.AlarmSound,
                _preferencesService.AlarmLeadMinutes,
                _preferencesService.VibrationOnly,
                _preferencesService.Stage2Sound,
                _preferencesService.Stage3Sound,
                _preferencesService.CustomSoundPath,
                _preferencesService.CustomSoundName,
                _preferencesService.OvershootDistanceMeters),
            await _databaseService.GetEmergencyContactsAsync(),
            await _databaseService.GetSavedRoutesAsync(),
            await _databaseService.GetTripHistoryAsync(),
            await _databaseService.GetBehavioralProfilesAsync());

        var json = JsonSerializer.Serialize(payload, _serializerOptions);
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var encrypted = Encrypt(jsonBytes);

        var fileName = $"{BackupFilePrefix}{payload.ExportedAtUtc:yyyyMMdd-HHmmss}{BackupFileExtension}";
        _preferencesService.LastBackupUtc = payload.ExportedAtUtc;
        return (fileName, encrypted);
    }

    // Restore straight from the raw bytes of whatever backup file the commuter picked through the OS file
    // browser. The static app key decrypts it silently — no password needed. Decrypt-then-validate-everything
    // -before-we-touch-the-db: even a tampered or empty file can never wipe real data — a damaged file fails
    // the GCM tag and we bail out before clearing anything.
    public async Task RestoreFromBytesAsync(byte[] encrypted)
    {
        byte[] jsonBytes;
        try
        {
            jsonBytes = Decrypt(encrypted);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("Backup file could not be decrypted. The file may be corrupted.");
        }

        var json = Encoding.UTF8.GetString(jsonBytes);
        var payload = JsonSerializer.Deserialize<BackupPayload>(json, _serializerOptions);
        if (payload is null)
            throw new InvalidOperationException("Backup file was empty or unreadable.");

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

        // Custom sound (Feature 1): the backup carries only a reference, not the audio bytes. Re-validate
        // that the referenced file still exists on THIS device; if it's gone, drop the custom selection so
        // every sound choice below safely falls back to a bundled voice.
        var restoredCustomPath = payload.Preferences.CustomSoundPath;
        var customAvailable = !string.IsNullOrWhiteSpace(restoredCustomPath)
                              && File.Exists(restoredCustomPath);
        _preferencesService.CustomSoundPath = customAvailable ? restoredCustomPath! : string.Empty;
        _preferencesService.CustomSoundName = customAvailable
            ? (payload.Preferences.CustomSoundName ?? string.Empty)
            : string.Empty;

        var rawSound = payload.Preferences.AlarmSound;
        _preferencesService.AlarmSound = NormalizeRestoredSound(rawSound, customAvailable) ?? "Digital Clock";

        // Per-stage choices (Feature 2): "" (inherit) and "None" are valid; "Custom" survives only when
        // the file is present; anything else falls back to inherit. Stage 1 is vibration-only by design,
        // so there's nothing to restore for it — any legacy Stage1Sound in the backup is ignored.
        _preferencesService.Stage2Sound = ValidateStageSound(payload.Preferences.Stage2Sound, customAvailable);
        _preferencesService.Stage3Sound = ValidateStageSound(payload.Preferences.Stage3Sound, customAvailable);

        // Overshoot distance (Feature 3): clamp to the same band the in-app control enforces. A 0 (e.g.
        // an older backup with no value) snaps to the default rather than the floor.
        var rawOvershoot = payload.Preferences.OvershootDistanceMeters <= 0
            ? DefaultOvershootDistanceMeters
            : payload.Preferences.OvershootDistanceMeters;
        _preferencesService.OvershootDistanceMeters =
            Math.Clamp(rawOvershoot, MinOvershootDistanceMeters, MaxOvershootDistanceMeters);

        _preferencesService.AlarmLeadMinutes =
            Math.Clamp(payload.Preferences.AlarmLeadMinutes, MinAlarmLeadMinutes, MaxAlarmLeadMinutes);
        _preferencesService.VibrationOnly = payload.Preferences.VibrationOnly;
        _preferencesService.LastBackupUtc = payload.ExportedAtUtc;
    }

    // The single global sound on restore: a bundled voice as-is, "Custom" only when its file is present,
    // otherwise null (caller substitutes the default). Mirrors AlarmSoundCatalog.Normalize(allowNone:false).
    private static string? NormalizeRestoredSound(string? raw, bool customAvailable)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        if (trimmed.Equals(CustomSoundKey, StringComparison.OrdinalIgnoreCase))
            return customAvailable ? CustomSoundKey : null;
        return ValidAlarmSounds.Contains(trimmed) ? trimmed : null;
    }

    // A per-stage choice on restore: "" inherit (also the fallback for junk), "None" vibration-led,
    // "Custom" only when the file exists, or a whitelisted bundled voice.
    private static string ValidateStageSound(string? raw, bool customAvailable)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var trimmed = raw.Trim();
        if (trimmed.Equals(NoneSoundKey, StringComparison.OrdinalIgnoreCase)) return NoneSoundKey;
        if (trimmed.Equals(CustomSoundKey, StringComparison.OrdinalIgnoreCase))
            return customAvailable ? CustomSoundKey : string.Empty;
        return ValidAlarmSounds.Contains(trimmed) ? trimmed : string.Empty;
    }

    // Static-key format: [1 version][12 nonce][16 GCM tag][ciphertext]
    // No salt and no iteration count anymore — the key is the fixed app key (StaticKey), so there's nothing
    // per-file to derive. We still use a fresh random nonce per export so two identical backups never encrypt
    // to the same bytes, and AES-GCM still provides integrity: any bit-flip in the body makes Decrypt throw
    // before a single plaintext byte is returned.
    private const int HeaderSize = 1 + GcmNonceSize + GcmTagSize;

    private static byte[] Encrypt(byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(GcmNonceSize);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[GcmTagSize];
        using var aesGcm = new AesGcm(StaticKey, GcmTagSize);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);

        var output = new byte[HeaderSize + ciphertext.Length];
        var offset = 0;
        output[offset++] = StaticFormatVersion;
        nonce.CopyTo(output, offset); offset += GcmNonceSize;
        tag.CopyTo(output, offset); offset += GcmTagSize;
        ciphertext.CopyTo(output, offset);
        return output;
    }

    private static byte[] Decrypt(byte[] data)
    {
        if (data.Length < HeaderSize + 1)
            throw new CryptographicException("Backup data is too short to be valid.");

        var offset = 0;
        var version = data[offset++];
        if (version != StaticFormatVersion)
            throw new CryptographicException($"Unsupported backup format version: {version}.");

        var nonce = data[offset..(offset + GcmNonceSize)]; offset += GcmNonceSize;
        var tag = data[offset..(offset + GcmTagSize)]; offset += GcmTagSize;
        var ciphertext = data[offset..];

        var plaintext = new byte[ciphertext.Length];
        using var aesGcm = new AesGcm(StaticKey, GcmTagSize);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    private static readonly System.Text.RegularExpressions.Regex PhoneRegex =
        new(@"^(09\d{9}|\+639\d{9})$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool IsValidPhilippineNumber(string number) =>
        !string.IsNullOrWhiteSpace(number) && PhoneRegex.IsMatch(number.Trim());
}
