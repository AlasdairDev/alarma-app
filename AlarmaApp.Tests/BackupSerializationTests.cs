// =============================================================================
//  BackupSerializationTests.cs
// -----------------------------------------------------------------------------
//  ISOLATED tests for the encrypted .alarma backup format and the
//  validate-before-restore contract.
//
//  These faithfully re-implement the production envelope from BackupService.cs:
//      Format:  [12-byte nonce][16-byte GCM tag][AES-256-GCM ciphertext]
//      Payload: System.Text.Json (WriteIndented) of the backup profile
//  and the restore-time validation rules (phone regex, name/route length caps,
//  PH bounding box, Take limits).
//
//  Self-contained per project convention (see AdaptiveAlarmTests.cs): the real
//  BackupService lives in the net9.0-android app project and can't be referenced
//  from a net9.0 test project, so the algorithm and models are mirrored here. The
//  point is to prove the SERIALIZATION + INTEGRITY + VALIDATION logic is sound:
//  a real profile round-trips byte-for-byte, and any tampering or junk is rejected
//  before a single record would touch a database.
// =============================================================================

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AlarmaApp.Tests;

// ── Models mirroring the production backup payload shape ──────────────────────
public sealed record BackupContactT(string Name, string PhoneNumber, bool IsPrimary);
public sealed record BackupRouteT(string DisplayName, double Latitude, double Longitude);
public sealed record BackupPrefsT(string AlarmSound, int AlarmLeadMinutes, bool VibrationOnly);
public sealed record BackupPayloadT(
    DateTimeOffset ExportedAtUtc,
    BackupPrefsT Preferences,
    List<BackupContactT> EmergencyContacts,
    List<BackupRouteT> SavedRoutes);

/// <summary>
/// Mirror of BackupService's PORTABLE encrypt/serialize/decrypt pipeline. The key is derived from a
/// password via PBKDF2-HMAC-SHA256 with a random salt that travels inside the file, so a backup carries
/// everything needed to decrypt it given the password — no device key, no SecureStorage, nothing
/// install-specific. That's what lets a backup made on one phone restore on a fresh install of another.
/// Envelope: [1 version][16 salt][4 iterations, big-endian][12 nonce][16 GCM tag][ciphertext].
/// </summary>
public static class BackupCodec
{
    public const byte FormatVersion = 3; // v3 = PBKDF2 password-derived key (portable)
    public const int SaltSize = 16;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int Iterations = 210_000;
    public const int HeaderSize = 1 + SaltSize + 4 + NonceSize + TagSize; // 49
    private static readonly HashAlgorithmName Hash = HashAlgorithmName.SHA256;
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static byte[] Serialize(BackupPayloadT payload, string password)
    {
        var json = JsonSerializer.Serialize(payload, Opts);
        return Encrypt(Encoding.UTF8.GetBytes(json), password);
    }

    public static BackupPayloadT? Deserialize(byte[] data, string password)
    {
        var plaintext = Decrypt(data, password);
        var json = Encoding.UTF8.GetString(plaintext);
        return JsonSerializer.Deserialize<BackupPayloadT>(json, Opts);
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, Hash, 32);

    public static byte[] Encrypt(byte[] plaintext, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key = DeriveKey(password, salt, Iterations);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aesGcm = new AesGcm(key, TagSize);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);

        var output = new byte[HeaderSize + ciphertext.Length];
        var offset = 0;
        output[offset++] = FormatVersion;
        salt.CopyTo(output, offset); offset += SaltSize;
        BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(offset), Iterations); offset += 4;
        nonce.CopyTo(output, offset); offset += NonceSize;
        tag.CopyTo(output, offset); offset += TagSize;
        ciphertext.CopyTo(output, offset);
        return output;
    }

    public static byte[] Decrypt(byte[] data, string password)
    {
        if (data.Length < HeaderSize + 1)
            throw new CryptographicException("Backup data is too short to be valid.");

        var offset = 0;
        var version = data[offset++];
        if (version != FormatVersion)
            throw new CryptographicException($"Unsupported backup format version: {version}.");

        var salt = data[offset..(offset + SaltSize)]; offset += SaltSize;
        var iterations = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset)); offset += 4;
        var nonce = data[offset..(offset + NonceSize)]; offset += NonceSize;
        var tag = data[offset..(offset + TagSize)]; offset += TagSize;
        var ciphertext = data[offset..];

        var key = DeriveKey(password, salt, iterations);
        var plaintext = new byte[ciphertext.Length];
        using var aesGcm = new AesGcm(key, TagSize);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}

/// <summary>Mirror of BackupService's validate-before-restore filters.</summary>
public static class BackupValidator
{
    private const int MaxRestoreContacts = 3, MaxRestoreRoutes = 5;
    private const int MaxContactNameLength = 50, MinRouteNameLength = 2, MaxRouteNameLength = 30;
    private const double PhLatMin = 4.5, PhLatMax = 21.1, PhLonMin = 116.9, PhLonMax = 126.6;

    private static readonly System.Text.RegularExpressions.Regex PhoneRegex =
        new(@"^(09\d{9}|\+639\d{9})$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static List<BackupContactT> ValidContacts(IEnumerable<BackupContactT> contacts) =>
        contacts.Where(c => !string.IsNullOrWhiteSpace(c.PhoneNumber)
                            && PhoneRegex.IsMatch(c.PhoneNumber.Trim())
                            && !string.IsNullOrWhiteSpace(c.Name)
                            && c.Name.Length <= MaxContactNameLength)
                .Take(MaxRestoreContacts)
                .ToList();

    public static List<BackupRouteT> ValidRoutes(IEnumerable<BackupRouteT> routes) =>
        routes.Where(r => !string.IsNullOrWhiteSpace(r.DisplayName)
                          && r.DisplayName.Length >= MinRouteNameLength
                          && r.DisplayName.Length <= MaxRouteNameLength
                          && r.Latitude >= PhLatMin && r.Latitude <= PhLatMax
                          && r.Longitude >= PhLonMin && r.Longitude <= PhLonMax)
              .Take(MaxRestoreRoutes)
              .ToList();
}

// ── Tests ────────────────────────────────────────────────────────────────────

public class BackupSerializationTests
{
    // The secret the key is derived from. There is no per-device key anymore — (password + the salt baked
    // into the file) is everything needed to decrypt, which is the whole portability fix.
    private const string Password = "correct horse battery staple";
    private const string WrongPassword = "Tr0ub4dor&3";

    private static BackupPayloadT SampleProfile() => new(
        ExportedAtUtc: new DateTimeOffset(2026, 6, 19, 8, 30, 0, TimeSpan.Zero),
        Preferences: new BackupPrefsT("Digital Clock", 5, VibrationOnly: false),
        EmergencyContacts: new()
        {
            new("Mom", "09171234567", IsPrimary: true),
            new("Brother", "+639998887777", IsPrimary: false),
        },
        SavedRoutes: new()
        {
            new("Home", 14.5995, 120.9842),
            new("Office", 14.5547, 121.0244),
        });

    // Compares two payloads element-by-element. (Record equality compares List<> members by
    // reference, so we assert the collections via xUnit's IEnumerable overload, which deep-compares.)
    private static void AssertProfilesEqual(BackupPayloadT expected, BackupPayloadT actual)
    {
        Assert.Equal(expected.ExportedAtUtc, actual.ExportedAtUtc);
        Assert.Equal(expected.Preferences, actual.Preferences);
        Assert.Equal(expected.EmergencyContacts, actual.EmergencyContacts);
        Assert.Equal(expected.SavedRoutes, actual.SavedRoutes);
    }

    // PORTABILITY: a backup encrypted with a password must round-trip exactly when the SAME password is
    // supplied — with no shared device/install state. BackupCodec holds nothing between calls, so encrypting
    // here and decrypting there (with the same password) is exactly the "export on Phone A, restore on a
    // fresh install of Phone B" scenario the fix is about.
    [Fact]
    public void Profile_RoundTrips_AcrossInstalls_WithSamePassword()
    {
        var original = SampleProfile();

        var blob = BackupCodec.Serialize(original, Password);   // "Phone A"
        var restored = BackupCodec.Deserialize(blob, Password); // "Phone B", fresh — only the password is shared

        Assert.NotNull(restored);
        AssertProfilesEqual(original, restored!);
    }

    // The portable .alarma envelope: [1 version][16 salt][4 iterations][12 nonce][16 tag][ciphertext], and
    // the GCM ciphertext is the same length as the plaintext JSON. The leading byte tags the format version.
    [Fact]
    public void EnvelopeLayout_MatchesPortableAlarmaFormat()
    {
        var payload = SampleProfile();
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        var plaintextLen = Encoding.UTF8.GetByteCount(json);

        var blob = BackupCodec.Serialize(payload, Password);

        Assert.Equal(BackupCodec.HeaderSize + plaintextLen, blob.Length);
        Assert.Equal(BackupCodec.FormatVersion, blob[0]);
    }

    // Each export uses a fresh random salt AND nonce, so the same profile + same password encrypts to
    // different bytes every time (no deterministic ciphertext leakage) yet both decrypt back to the same data.
    [Fact]
    public void SameProfileAndPassword_EncryptsToDifferentBytes_EachExport()
    {
        var payload = SampleProfile();

        var a = BackupCodec.Serialize(payload, Password);
        var b = BackupCodec.Serialize(payload, Password);

        Assert.False(a.AsSpan().SequenceEqual(b), "Salt/nonce reuse — ciphertext should differ per export.");
        AssertProfilesEqual(BackupCodec.Deserialize(a, Password)!, BackupCodec.Deserialize(b, Password)!);
    }

    // Integrity: flipping a single ciphertext byte must fail the GCM tag and throw BEFORE any
    // plaintext is returned (no chance to deserialize garbage).
    [Fact]
    public void TamperedCiphertext_FailsAuthentication()
    {
        var blob = BackupCodec.Serialize(SampleProfile(), Password);
        blob[^1] ^= 0xFF; // flip last ciphertext byte

        Assert.Throws<AuthenticationTagMismatchException>(() => BackupCodec.Deserialize(blob, Password));
    }

    // Integrity: corrupting the GCM tag itself is equally rejected. The tag now sits after the
    // [version][salt][iterations][nonce] header.
    [Fact]
    public void TamperedTag_FailsAuthentication()
    {
        var blob = BackupCodec.Serialize(SampleProfile(), Password);
        var tagOffset = 1 + BackupCodec.SaltSize + 4 + BackupCodec.NonceSize;
        blob[tagOffset] ^= 0xFF; // first tag byte

        Assert.Throws<AuthenticationTagMismatchException>(() => BackupCodec.Deserialize(blob, Password));
    }

    // The cross-device safety check: the WRONG password derives the wrong key and fails the GCM tag, so a
    // backup can't be opened by anyone who doesn't know its password.
    [Fact]
    public void WrongPassword_FailsAuthentication()
    {
        var blob = BackupCodec.Serialize(SampleProfile(), Password);
        Assert.Throws<AuthenticationTagMismatchException>(() => BackupCodec.Deserialize(blob, WrongPassword));
    }

    // Truncated / junk data shorter than the envelope header is rejected as too short.
    [Theory]
    [InlineData(0)]
    [InlineData(24)]
    [InlineData(49)] // header only (49 bytes), one byte short of header+1 — no ciphertext at all
    public void TooShortData_Throws(int length)
        => Assert.Throws<CryptographicException>(
            () => BackupCodec.Decrypt(new byte[length], Password));
}

public class BackupRestoreFilterTests
{
    // Invalid phone numbers and over-long names are filtered out before restore.
    [Fact]
    public void Contacts_WithBadNumbersOrNames_AreRejected()
    {
        var contacts = new List<BackupContactT>
        {
            new("Valid", "09171234567", true),               // kept
            new("BadNumber", "12345", false),                // rejected: not a PH mobile
            new("", "09181234567", false),                   // rejected: blank name
            new(new string('x', 51), "09191234567", false),  // rejected: name too long
        };

        var kept = BackupValidator.ValidContacts(contacts);

        Assert.Single(kept);
        Assert.Equal("Valid", kept[0].Name);
    }

    // Contact restore is capped at 3, even if the backup carries more valid ones.
    [Fact]
    public void Contacts_AreCappedAtThree()
    {
        var contacts = Enumerable.Range(0, 6)
            .Select(i => new BackupContactT($"C{i}", "09171234567", false))
            .ToList();

        Assert.Equal(3, BackupValidator.ValidContacts(contacts).Count);
    }

    // Routes outside the Philippines bounding box, or with bad names, are rejected.
    [Theory]
    [InlineData("Tokyo", 35.6762, 139.6503, false)]   // outside PH bbox
    [InlineData("X", 14.6, 121.0, false)]             // name too short (< 2)
    [InlineData("Valid Stop", 14.6, 121.0, true)]     // kept
    public void Routes_AreBoundsAndNameChecked(string name, double lat, double lon, bool kept)
    {
        var routes = new List<BackupRouteT> { new(name, lat, lon) };
        Assert.Equal(kept ? 1 : 0, BackupValidator.ValidRoutes(routes).Count);
    }

    // An all-junk backup decrypts fine but validates to ZERO records — the caller would therefore
    // never clear real data (the validate-before-clear guarantee).
    [Fact]
    public void AllInvalidProfile_YieldsNoRestorableRecords()
    {
        var contacts = new List<BackupContactT> { new("", "nope", false) };
        var routes = new List<BackupRouteT> { new("", 0, 0) };

        Assert.Empty(BackupValidator.ValidContacts(contacts));
        Assert.Empty(BackupValidator.ValidRoutes(routes));
    }
}
