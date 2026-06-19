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

/// <summary>Mirror of BackupService's encrypt/serialize/decrypt pipeline.</summary>
public static class BackupCodec
{
    public const int NonceSize = 12;
    public const int TagSize = 16;
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static byte[] Serialize(BackupPayloadT payload, byte[] key)
    {
        var json = JsonSerializer.Serialize(payload, Opts);
        return Encrypt(Encoding.UTF8.GetBytes(json), key);
    }

    public static BackupPayloadT? Deserialize(byte[] data, byte[] key)
    {
        var plaintext = Decrypt(data, key);
        var json = Encoding.UTF8.GetString(plaintext);
        return JsonSerializer.Deserialize<BackupPayloadT>(json, Opts);
    }

    public static byte[] Encrypt(byte[] plaintext, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aesGcm = new AesGcm(key, TagSize);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);

        var output = new byte[NonceSize + TagSize + ciphertext.Length];
        nonce.CopyTo(output, 0);
        tag.CopyTo(output, NonceSize);
        ciphertext.CopyTo(output, NonceSize + TagSize);
        return output;
    }

    public static byte[] Decrypt(byte[] data, byte[] key)
    {
        if (data.Length < NonceSize + TagSize + 1)
            throw new CryptographicException("Backup data is too short to be valid.");

        var nonce = data[..NonceSize];
        var tag = data[NonceSize..(NonceSize + TagSize)];
        var ciphertext = data[(NonceSize + TagSize)..];
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
    private static byte[] Key() => RandomNumberGenerator.GetBytes(32); // AES-256

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

    // A real profile must round-trip exactly through encrypt → decrypt → deserialize.
    [Fact]
    public void Profile_RoundTrips_Exactly()
    {
        var key = Key();
        var original = SampleProfile();

        var blob = BackupCodec.Serialize(original, key);
        var restored = BackupCodec.Deserialize(blob, key);

        Assert.NotNull(restored);
        AssertProfilesEqual(original, restored!);
    }

    // The .alarma envelope layout: [12 nonce][16 tag][ciphertext], and GCM ciphertext is the
    // same length as the plaintext JSON.
    [Fact]
    public void EnvelopeLayout_MatchesAlarmaFormat()
    {
        var key = Key();
        var payload = SampleProfile();
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        var plaintextLen = Encoding.UTF8.GetByteCount(json);

        var blob = BackupCodec.Serialize(payload, key);

        Assert.Equal(BackupCodec.NonceSize + BackupCodec.TagSize + plaintextLen, blob.Length);
    }

    // Each export uses a fresh random nonce, so the same profile encrypts to different bytes
    // every time (no deterministic ciphertext leakage) yet still decrypts back to the same data.
    [Fact]
    public void SameProfile_EncryptsToDifferentBytes_EachExport()
    {
        var key = Key();
        var payload = SampleProfile();

        var a = BackupCodec.Serialize(payload, key);
        var b = BackupCodec.Serialize(payload, key);

        Assert.False(a.AsSpan().SequenceEqual(b), "Nonce reuse — ciphertext should differ per export.");
        AssertProfilesEqual(BackupCodec.Deserialize(a, key)!, BackupCodec.Deserialize(b, key)!);
    }

    // Integrity: flipping a single ciphertext byte must fail the GCM tag and throw BEFORE any
    // plaintext is returned (no chance to deserialize garbage).
    [Fact]
    public void TamperedCiphertext_FailsAuthentication()
    {
        var key = Key();
        var blob = BackupCodec.Serialize(SampleProfile(), key);
        blob[^1] ^= 0xFF; // flip last ciphertext byte

        Assert.Throws<AuthenticationTagMismatchException>(() => BackupCodec.Deserialize(blob, key));
    }

    // Integrity: corrupting the GCM tag itself is equally rejected.
    [Fact]
    public void TamperedTag_FailsAuthentication()
    {
        var key = Key();
        var blob = BackupCodec.Serialize(SampleProfile(), key);
        blob[BackupCodec.NonceSize] ^= 0xFF; // first tag byte

        Assert.Throws<AuthenticationTagMismatchException>(() => BackupCodec.Deserialize(blob, key));
    }

    // A backup from a different device (different key) can't be decrypted on this one.
    [Fact]
    public void WrongKey_FailsAuthentication()
    {
        var blob = BackupCodec.Serialize(SampleProfile(), Key());
        Assert.Throws<AuthenticationTagMismatchException>(() => BackupCodec.Deserialize(blob, Key()));
    }

    // Truncated / junk data shorter than the envelope header is rejected as too short.
    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(27)] // one byte short of nonce+tag+1
    public void TooShortData_Throws(int length)
        => Assert.Throws<CryptographicException>(
            () => BackupCodec.Decrypt(new byte[length], Key()));
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
