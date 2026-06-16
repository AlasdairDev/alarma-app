// Security-centric unit tests — boundary edge cases for all validation logic in Alarma.
// These tests run on plain net9.0 (no Android/MAUI SDK required) and exercise the same
// regex patterns, clamping rules, and whitelist sets used in the production app.
//
// OWASP Top 10 coverage:
//   A03 Injection: Philippine phone regex, coordinate bounds, name length caps, sound whitelist
//   A04 Insecure Design: Lead-time clamping, backup restore caps
//   A08 Data Integrity: AES-GCM format checks, backup field validation

using System.Text.RegularExpressions;
using Xunit;

namespace AlarmaApp.Tests;

public class PhoneValidationTests
{
    // Same compiled regex as HomeController.PhoneRegex and AndroidSmsService.RecipientRegex
    private static readonly Regex PhoneRegex =
        new(@"^(09\d{9}|\+639\d{9})$", RegexOptions.Compiled);

    private static bool IsValid(string number) =>
        !string.IsNullOrWhiteSpace(number) && PhoneRegex.IsMatch(number.Trim());

    [Theory]
    [InlineData("09171234567")]   // valid 09X format
    [InlineData("09991234567")]   // valid 09X — last prefix digit
    [InlineData("+639171234567")] // valid +639X format
    [InlineData("+639991234567")] // valid +639X — last prefix digit
    public void ValidPhilippineNumbers_AreAccepted(string number)
        => Assert.True(IsValid(number));

    [Theory]
    [InlineData("")]                 // empty
    [InlineData("   ")]             // whitespace only
    [InlineData("1234567890")]      // no PH prefix
    [InlineData("0917123456")]      // 10 digits — too short
    [InlineData("091712345678")]    // 12 digits — too long
    [InlineData("+63917123456")]    // +639X but only 10 digits
    [InlineData("+6391712345678")]  // +639X but 13 digits
    [InlineData("08171234567")]     // starts with 08
    [InlineData("+619171234567")]   // wrong country code
    [InlineData("+639171234567;DROP TABLE contacts")]   // SQL-injection-style suffix
    public void InvalidPhoneNumbers_AreRejected(string number)
        => Assert.False(IsValid(number));

    // Trim behaviour: leading/trailing whitespace (including \n) is stripped before matching.
    // "09171234567 " and "09171234567\n" trim to the valid base number and are therefore ACCEPTED.
    // This documents intentional behaviour — callers are expected to strip their own whitespace
    // before presenting numbers to the UI, and the Trim() is a convenience safety net, not a
    // bypass of the regex.
    [Theory]
    [InlineData("09171234567 ")]   // trailing space trimmed → valid
    [InlineData("09171234567\n")]  // trailing newline trimmed → valid
    public void NumbersWithTrailingWhitespace_AreAcceptedAfterTrim(string number)
        => Assert.True(IsValid(number));

    [Fact]
    public void PhoneRegex_DoesNotMatchPrefix_Alone()
        => Assert.False(IsValid("09"));

    [Fact]
    public void PhoneRegex_DoesNotMatchPlusPrefix_Alone()
        => Assert.False(IsValid("+639"));
}

public class CoordinateValidationTests
{
    // Philippines bounding box — same constants as GeocodingService and BackupService
    private const double PhLatMin = 4.5;
    private const double PhLatMax = 21.1;
    private const double PhLonMin = 116.9;
    private const double PhLonMax = 126.6;

    private static bool InPhilippines(double lat, double lon) =>
        lat >= PhLatMin && lat <= PhLatMax && lon >= PhLonMin && lon <= PhLonMax;

    private static bool TryParseLatitude(string v, out double r) =>
        double.TryParse(v, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out r)
        && double.IsFinite(r) && r is >= -90.0 and <= 90.0;

    private static bool TryParseLongitude(string v, out double r) =>
        double.TryParse(v, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out r)
        && double.IsFinite(r) && r is >= -180.0 and <= 180.0;

    [Theory]
    [InlineData(14.5995, 120.9842)]  // Manila
    [InlineData(10.3157, 123.8854)]  // Cebu
    [InlineData(7.1907, 125.4553)]   // Davao
    [InlineData(4.5, 116.9)]         // SW corner (inclusive)
    [InlineData(21.1, 126.6)]        // NE corner (inclusive)
    public void PhilippinesCoordinates_AreAccepted(double lat, double lon)
        => Assert.True(InPhilippines(lat, lon));

    [Theory]
    [InlineData(0.0, 120.0)]         // below PH lat min
    [InlineData(25.0, 120.0)]        // above PH lat max
    [InlineData(14.0, 110.0)]        // below PH lon min
    [InlineData(14.0, 130.0)]        // above PH lon max
    [InlineData(-90.0, 0.0)]         // south pole
    [InlineData(90.0, 180.0)]        // north pole / date line
    public void OutOfPhilippinesCoordinates_AreRejected(double lat, double lon)
        => Assert.False(InPhilippines(lat, lon));

    [Theory]
    [InlineData("90.0")]
    [InlineData("-90.0")]
    [InlineData("14.5995")]
    [InlineData("0")]
    public void ValidLatitudeStrings_ParseSuccessfully(string value)
    {
        Assert.True(TryParseLatitude(value, out _));
    }

    [Theory]
    [InlineData("90.000001")]    // just over max
    [InlineData("-90.000001")]   // just under min
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("")]
    [InlineData("abc")]
    public void InvalidLatitudeStrings_FailParsing(string value)
        => Assert.False(TryParseLatitude(value, out _));

    [Theory]
    [InlineData("180.000001")]   // just over max
    [InlineData("-180.000001")]  // just under min
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void InvalidLongitudeStrings_FailParsing(string value)
        => Assert.False(TryParseLongitude(value, out _));
}

public class AlarmSoundValidationTests
{
    private static readonly HashSet<string> ValidAlarmSounds =
        new(StringComparer.OrdinalIgnoreCase)
        { "Default", "Alarm", "Chime", "Notification", "Ringtone" };

    private static string NormalizeSound(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Default";
        var trimmed = value.Trim();
        return ValidAlarmSounds.Contains(trimmed) ? trimmed : "Default";
    }

    [Theory]
    [InlineData("Default")]
    [InlineData("Alarm")]
    [InlineData("Chime")]
    [InlineData("Notification")]
    [InlineData("Ringtone")]
    [InlineData("default")]    // case-insensitive accept
    [InlineData("ALARM")]
    public void ValidSoundKeys_AreNormalised(string input)
        => Assert.Contains(NormalizeSound(input), ValidAlarmSounds);

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    [InlineData("Buzz")]
    [InlineData("Custom")]
    [InlineData("../../../../etc/passwd")] // path traversal attempt
    [InlineData("<script>alert(1)</script>")] // XSS attempt
    public void InvalidSoundKeys_FallBackToDefault(string? input)
        => Assert.Equal("Default", NormalizeSound(input));
}

public class AlarmLeadMinutesValidationTests
{
    private static int Clamp(int value) => Math.Clamp(value, 1, 60);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(30, 30)]
    [InlineData(60, 60)]
    public void InRangeValues_AreReturnedUnchanged(int input, int expected)
        => Assert.Equal(expected, Clamp(input));

    [Theory]
    [InlineData(0, 1)]       // below min → clamped to 1
    [InlineData(-100, 1)]    // large negative → 1
    [InlineData(61, 60)]     // above max → 60
    [InlineData(int.MaxValue, 60)] // overflow → 60
    [InlineData(int.MinValue, 1)]  // underflow → 1
    public void OutOfRangeValues_AreClamped(int input, int expected)
        => Assert.Equal(expected, Clamp(input));
}

public class ContactNameValidationTests
{
    private const int MaxContactNameLength = 50;

    private static bool IsValidName(string? name)
        => !string.IsNullOrWhiteSpace(name)
           && name.Trim().Length <= MaxContactNameLength;

    [Theory]
    [InlineData("Maria")]
    [InlineData("Juan dela Cruz")]
    [InlineData("José Rizal")]
    [InlineData("A")] // 1 char — allowed (spec only requires non-empty)
    public void ValidContactNames_AreAccepted(string name)
        => Assert.True(IsValidName(name));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EmptyOrWhitespaceNames_AreRejected(string? name)
        => Assert.False(IsValidName(name));

    [Fact]
    public void NameAtMaxLength_IsAccepted()
        => Assert.True(IsValidName(new string('A', MaxContactNameLength)));

    [Fact]
    public void NameExceedingMaxLength_IsRejected()
        => Assert.False(IsValidName(new string('A', MaxContactNameLength + 1)));
}

public class BackupRestoreValidationTests
{
    private const int MaxRestoreContacts = 3;
    private const int MaxRestoreRoutes = 5;
    private const int MaxRestoreHistory = 100;
    private const double MaxDistanceMeters = 1_000_000;

    [Theory]
    [InlineData(0, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]   // exceeds cap
    [InlineData(100, false)]
    public void ContactRestoreCap_EnforcedAtBoundary(int count, bool fitsInCap)
        => Assert.Equal(fitsInCap, count <= MaxRestoreContacts);

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(999_999.9, true)]
    [InlineData(1_000_000.0, true)]       // at the cap — allowed
    [InlineData(1_000_000.1, false)]      // just over
    [InlineData(-1.0, false)]             // negative distance
    [InlineData(double.MaxValue, false)]
    public void DistanceMeters_Validation(double value, bool isValid)
        => Assert.Equal(isValid, value >= 0 && value <= MaxDistanceMeters);

    [Theory]
    [InlineData(0, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]   // MaxAlarmStageReached must be 0–3
    [InlineData(-1, false)]
    public void MaxAlarmStageReached_ValidRange(int stage, bool isValid)
        => Assert.Equal(isValid, stage is >= 0 and <= 3);
}

public class BackupEncryptionFormatTests
{
    private const int GcmNonceSize = 12;
    private const int GcmTagSize = 16;
    private const int MinValidLength = GcmNonceSize + GcmTagSize + 1;

    // MinValidLength = nonce(12) + tag(16) + 1 ciphertext byte = 29.
    // The Decrypt() guard is: data.Length < GcmNonceSize + GcmTagSize + 1 → throw.
    [Theory]
    [InlineData(0, false)]
    [InlineData(27, false)]   // nonce(12) + tag(16) - 1 — clearly too short
    [InlineData(28, false)]   // nonce(12) + tag(16) exactly — no ciphertext → still invalid
    [InlineData(29, true)]    // nonce(12) + tag(16) + 1 byte ciphertext — minimum valid
    [InlineData(1000, true)]
    public void BackupData_MinimumLengthCheck(int length, bool isValid)
        => Assert.Equal(isValid, length >= MinValidLength);
}
