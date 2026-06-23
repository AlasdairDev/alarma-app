// =============================================================================
//  AlarmSettingsBackupTests.cs
// -----------------------------------------------------------------------------
//  ISOLATED tests that the NEW user settings survive backup/restore:
//    * per-stage sound choices (Feature 2),
//    * the custom-sound reference (Feature 1) — re-validated for existence,
//    * the overshoot trigger distance (Feature 3) — clamped on restore.
//
//  Self-contained per project convention: mirrors the extended BackupPreferences
//  record (System.Text.Json round-trip with optional members) and BackupService's
//  validate-before-restore rules (NormalizeRestoredSound / ValidateStageSound /
//  overshoot clamp). The production envelope itself is covered by
//  BackupSerializationTests; here we focus on the new fields.
// =============================================================================

using System.Text.Json;
using Xunit;

namespace AlarmaApp.Tests;

// Mirror of the extended AlarmaApp.Models.BackupPreferences (optional members default like the record).
// Stage 1 is vibration-only by design, so there's no Stage1Sound member — a legacy backup that still
// carries that JSON property loads fine because System.Text.Json ignores unknown properties.
internal sealed record PrefsBackupT(
    string AlarmSound,
    int AlarmLeadMinutes,
    bool VibrationOnly,
    string? Stage2Sound = null,
    string? Stage3Sound = null,
    string? CustomSoundPath = null,
    string? CustomSoundName = null,
    int OvershootDistanceMeters = 250);

// Mirror of BackupService's restore-time preference validators.
internal static class PrefsRestoreSpec
{
    private static readonly HashSet<string> Valid =
        new(StringComparer.OrdinalIgnoreCase) { "Digital Clock", "Siren", "Buzzer", "Bell", "Air Horn" };
    private const string CustomKey = "Custom";
    private const string NoneKey = "None";
    private const int OvDefault = 250, OvMin = 50, OvMax = 500;

    public static string NormalizeRestoredSound(string? raw, bool customAvailable)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Digital Clock";
        var t = raw.Trim();
        if (t.Equals(CustomKey, StringComparison.OrdinalIgnoreCase))
            return customAvailable ? CustomKey : "Digital Clock";
        return Valid.Contains(t) ? t : "Digital Clock";
    }

    public static string ValidateStageSound(string? raw, bool customAvailable)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var t = raw.Trim();
        if (t.Equals(NoneKey, StringComparison.OrdinalIgnoreCase)) return NoneKey;
        if (t.Equals(CustomKey, StringComparison.OrdinalIgnoreCase))
            return customAvailable ? CustomKey : string.Empty;
        return Valid.Contains(t) ? t : string.Empty;
    }

    public static int ClampOvershoot(int raw)
        => Math.Clamp(raw <= 0 ? OvDefault : raw, OvMin, OvMax);
}

public class AlarmSettingsBackupRoundTripTests
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    [Fact]
    public void NewSettings_RoundTripThroughJson()
    {
        var original = new PrefsBackupT(
            AlarmSound: "Custom",
            AlarmLeadMinutes: 5,
            VibrationOnly: false,
            Stage2Sound: "Bell",
            Stage3Sound: "Custom",
            CustomSoundPath: "/data/user/0/com.alarma.app/files/alarm_sounds/custom.mp3",
            CustomSoundName: "my-ringtone.mp3",
            OvershootDistanceMeters: 400);

        var json = JsonSerializer.Serialize(original, Opts);
        var restored = JsonSerializer.Deserialize<PrefsBackupT>(json, Opts);

        Assert.Equal(original, restored);
    }

    // An OLD backup whose JSON predates these fields still deserialises: the new members fall to their
    // record defaults rather than throwing.
    [Fact]
    public void LegacyBackupWithoutNewFields_DeserialisesWithDefaults()
    {
        const string legacyJson = """
        {
          "AlarmSound": "Siren",
          "AlarmLeadMinutes": 5,
          "VibrationOnly": true
        }
        """;

        var restored = JsonSerializer.Deserialize<PrefsBackupT>(legacyJson, Opts);

        Assert.NotNull(restored);
        Assert.Equal("Siren", restored!.AlarmSound);
        Assert.Null(restored.Stage2Sound);
        Assert.Null(restored.CustomSoundPath);
        Assert.Equal(250, restored.OvershootDistanceMeters); // record default
    }

    // A backup written before Stage 1 became vibration-only still carries a "Stage1Sound" property.
    // It must load without error — the now-unknown property is simply ignored (treated as absent).
    [Fact]
    public void LegacyBackupWithStage1Sound_LoadsAndIgnoresIt()
    {
        const string legacyJson = """
        {
          "AlarmSound": "Siren",
          "AlarmLeadMinutes": 5,
          "VibrationOnly": false,
          "Stage1Sound": "Bell",
          "Stage2Sound": "Air Horn",
          "OvershootDistanceMeters": 300
        }
        """;

        var restored = JsonSerializer.Deserialize<PrefsBackupT>(legacyJson, Opts);

        Assert.NotNull(restored);
        Assert.Equal("Siren", restored!.AlarmSound);
        Assert.Equal("Air Horn", restored.Stage2Sound); // still honoured
        Assert.Equal(300, restored.OvershootDistanceMeters);
    }
}

public class AlarmSettingsRestoreValidationTests
{
    // The custom reference is honoured only when the referenced file still exists on this device.
    [Fact]
    public void CustomSound_KeptWhenFilePresent()
        => Assert.Equal("Custom", PrefsRestoreSpec.NormalizeRestoredSound("Custom", customAvailable: true));

    [Fact]
    public void CustomSound_FallsBackWhenFileMissing()
        => Assert.Equal("Digital Clock", PrefsRestoreSpec.NormalizeRestoredSound("Custom", customAvailable: false));

    [Theory]
    [InlineData("None", true, "None")]
    [InlineData("Bell", true, "Bell")]
    [InlineData("", true, "")]                 // inherit
    [InlineData(null, true, "")]               // inherit
    [InlineData("Custom", true, "Custom")]     // file present
    [InlineData("Custom", false, "")]          // file gone -> inherit
    [InlineData("Bogus", true, "")]            // junk -> inherit
    public void StageSound_RestoreValidation(string? raw, bool customAvailable, string expected)
        => Assert.Equal(expected, PrefsRestoreSpec.ValidateStageSound(raw, customAvailable));

    [Theory]
    [InlineData(400, 400)]
    [InlineData(0, 250)]       // absent -> default
    [InlineData(10, 50)]       // below floor
    [InlineData(600, 500)]     // above ceiling -> clamps to 500, not 1000
    [InlineData(5000, 500)]    // far above ceiling -> 500
    public void Overshoot_ClampedOnRestore(int stored, int expected)
        => Assert.Equal(expected, PrefsRestoreSpec.ClampOvershoot(stored));

    // A backup that referenced a custom sound, restored on a device where the file is absent, downgrades
    // every custom reference (global + per-stage) to a safe bundled/inherit value — never an unplayable key.
    [Fact]
    public void MissingCustomFile_DowngradesAllReferences()
    {
        const bool customAvailable = false;
        Assert.Equal("Digital Clock", PrefsRestoreSpec.NormalizeRestoredSound("Custom", customAvailable));
        Assert.Equal(string.Empty, PrefsRestoreSpec.ValidateStageSound("Custom", customAvailable));
    }
}
