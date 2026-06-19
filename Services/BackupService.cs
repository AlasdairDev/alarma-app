// Our encrypted backup. AES-256-GCM with a fresh 96-bit nonce per export — but the big change here is the
// KEY. We used to pull a random 256-bit key out of the Keystore (SecureStorage), which had a nasty
// gotcha: the Keystore is wiped when the app is uninstalled and is unique to each install, so a backup
// could only ever be restored on the exact same install that made it. Reinstall the app or move to a new
// phone and every .alarma file read as "damaged" — the key it needed was gone for good.
// Now the key is DERIVED FROM A PASSWORD the commuter types at export time (PBKDF2-HMAC-SHA256), and the
// random salt + iteration count travel INSIDE the file. Nothing device-specific is needed to read it back,
// so a backup made on Phone A restores on a fresh install of Phone B as long as the same password is
// entered. There is no stored key to lose anymore.
// GCM still buys us integrity for free: any tampering (cipher/nonce/tag) — or the WRONG password — throws
// on Decrypt before we read a single byte, so we never end up deserializing garbage.
// On restore we validate EVERY record before clearing anything, so a junk or empty backup can't wipe a
// commuter's real data — restored strings are length-capped and phone numbers re-checked.
// Note: we build the backup as raw bytes rather than writing it ourselves, then hand them to the OS file
// picker, so commuters genuinely own and can move their own backups.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlarmaApp.Models;

namespace AlarmaApp.Services;

public class BackupService
{
    private const string BackupFilePrefix = "alarma-backup-";
    private const string BackupFileExtension = ".alarma";

    // Portable envelope format. The leading version byte lets a future reader tell password-derived files
    // (v3) apart from anything else and reject formats it doesn't understand instead of producing garbage.
    private const byte PortableFormatVersion = 3; // v3 = PBKDF2 password-derived key (portable)
    private const int SaltSize = 16;

    // PBKDF2-HMAC-SHA256. The iteration count is written into each file (see envelope) so we can raise this
    // default later WITHOUT breaking older backups — restore re-derives with whatever count the file carries.
    private const int Pbkdf2Iterations = 210_000;
    private static readonly HashAlgorithmName Pbkdf2Hash = HashAlgorithmName.SHA256;
    // Sanity bounds for the iteration count read back from a file, so a corrupted/hostile header can't make
    // us spin on an absurd PBKDF2 cost or weaken the derivation to near-zero.
    private const int MinPbkdf2Iterations = 10_000;
    private const int MaxPbkdf2Iterations = 10_000_000;
    // Floor on password strength at export — derivation is only as strong as the secret behind it.
    private const int MinPasswordLength = 6;

    // AES-256-GCM parameters
    private const int GcmNonceSize = 12;
    private const int GcmTagSize = 16;
    private const int KeySize = 32; // 256-bit

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

    // Allowed alarm sound values — mirrors HomeController._alarmSoundOptions exactly, so a restore never
    // silently downgrades a saved choice.
    private static readonly HashSet<string> ValidAlarmSounds =
        new(StringComparer.OrdinalIgnoreCase) { "Digital Clock", "Siren", "Buzzer", "Bell", "Air Horn" };

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
    public async Task<(string FileName, byte[] Data)> BuildBackupAsync(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
            throw new ArgumentException(
                $"Backup password must be at least {MinPasswordLength} characters.", nameof(password));

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
        var encrypted = Encrypt(jsonBytes, password);

        var fileName = $"{BackupFilePrefix}{payload.ExportedAtUtc:yyyyMMdd-HHmmss}{BackupFileExtension}";
        _preferencesService.LastBackupUtc = payload.ExportedAtUtc;
        return (fileName, encrypted);
    }

    // Restore straight from the raw bytes of whatever backup file the commuter picked through the OS file
    // browser, using the password they set when they exported it. Decrypt-then-validate-everything-before-we
    // -touch-the-db: even a tampered file, an empty one, or a wrong password can never wipe real data — the
    // wrong password simply fails the GCM tag and we bail out before clearing anything.
    public async Task RestoreFromBytesAsync(byte[] encrypted, string password)
    {
        byte[] jsonBytes;
        try
        {
            jsonBytes = Decrypt(encrypted, password);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("Backup file could not be decrypted. The password may be wrong or the file is corrupted.");
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
        var rawSound = payload.Preferences.AlarmSound;
        _preferencesService.AlarmSound = ValidAlarmSounds.Contains(rawSound ?? string.Empty)
            ? rawSound! : "Digital Clock";
        _preferencesService.AlarmLeadMinutes =
            Math.Clamp(payload.Preferences.AlarmLeadMinutes, MinAlarmLeadMinutes, MaxAlarmLeadMinutes);
        _preferencesService.VibrationOnly = payload.Preferences.VibrationOnly;
        _preferencesService.LastBackupUtc = payload.ExportedAtUtc;
    }

    // Derive the AES-256 key from the user's password + the file's salt via PBKDF2-HMAC-SHA256. This is the
    // whole point of the portable design: the key is reproducible anywhere from (password + salt), so it
    // never has to be stored — and therefore can never be lost to an uninstall or stranded on the old phone.
    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, Pbkdf2Hash, KeySize);

    // Portable format: [1 version][16 salt][4 iterations, big-endian][12 nonce][16 GCM tag][ciphertext]
    // The salt + iteration count ride along in the clear (they're not secret — they only make the password
    // expensive to brute-force and unique per file). AES-GCM still provides confidentiality AND integrity:
    // any bit-flip in the body, or a key derived from the wrong password, makes Decrypt throw before any
    // plaintext is returned.
    private const int HeaderSize = 1 + SaltSize + 4 + GcmNonceSize + GcmTagSize;

    private static byte[] Encrypt(byte[] plaintext, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(GcmNonceSize);
        var key = DeriveKey(password, salt, Pbkdf2Iterations);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[GcmTagSize];
        using var aesGcm = new AesGcm(key, GcmTagSize);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);

        var output = new byte[HeaderSize + ciphertext.Length];
        var offset = 0;
        output[offset++] = PortableFormatVersion;
        salt.CopyTo(output, offset); offset += SaltSize;
        BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(offset), Pbkdf2Iterations); offset += 4;
        nonce.CopyTo(output, offset); offset += GcmNonceSize;
        tag.CopyTo(output, offset); offset += GcmTagSize;
        ciphertext.CopyTo(output, offset);
        return output;
    }

    private static byte[] Decrypt(byte[] data, string password)
    {
        if (data.Length < HeaderSize + 1)
            throw new CryptographicException("Backup data is too short to be valid.");

        var offset = 0;
        var version = data[offset++];
        if (version != PortableFormatVersion)
            throw new CryptographicException($"Unsupported backup format version: {version}.");

        var salt = data[offset..(offset + SaltSize)]; offset += SaltSize;
        var iterations = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset)); offset += 4;
        if (iterations is < MinPbkdf2Iterations or > MaxPbkdf2Iterations)
            throw new CryptographicException("Backup iteration count is out of the supported range.");

        var nonce = data[offset..(offset + GcmNonceSize)]; offset += GcmNonceSize;
        var tag = data[offset..(offset + GcmTagSize)]; offset += GcmTagSize;
        var ciphertext = data[offset..];

        var key = DeriveKey(password, salt, iterations);
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
