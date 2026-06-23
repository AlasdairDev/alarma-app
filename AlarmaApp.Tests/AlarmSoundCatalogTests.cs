// =============================================================================
//  AlarmSoundCatalogTests.cs
// -----------------------------------------------------------------------------
//  ISOLATED tests for the alarm-sound whitelist + custom-sound import validation
//  (Feature 1) and the per-stage sound resolution (Feature 2).
//
//  Self-contained per project convention (see AdaptiveAlarmTests.cs): the real
//  AlarmaApp.Services.AlarmSoundCatalog lives in the net9.0-android app project
//  and can't be referenced from this net9.0 test project, so its rules and
//  constants are mirrored here. Keep the two in lock-step.
// =============================================================================

using Xunit;

namespace AlarmaApp.Tests;

/// <summary>Mirror of AlarmaApp.Services.AlarmSoundCatalog.</summary>
internal static class SoundCatalogSpec
{
    public const string Default = "Digital Clock";
    public const string CustomKey = "Custom";
    public const string NoneKey = "None";

    public static readonly string[] BundledSounds =
        { "Digital Clock", "Siren", "Buzzer", "Bell", "Air Horn" };

    public static readonly string[] AllowedExtensions =
        { ".mp3", ".ogg", ".wav", ".m4a", ".aac" };
    public const long MaxImportBytes = 10L * 1024 * 1024;
    public const double MinDurationSeconds = 0.5;
    public const double MaxDurationSeconds = 300.0;

    public static string Normalize(string? key, bool customAvailable, bool allowNone)
    {
        if (string.IsNullOrWhiteSpace(key)) return Default;
        var t = key.Trim();
        if (t.Equals(NoneKey, StringComparison.OrdinalIgnoreCase))
            return allowNone ? NoneKey : Default;
        if (t.Equals(CustomKey, StringComparison.OrdinalIgnoreCase))
            return customAvailable ? CustomKey : Default;
        foreach (var b in BundledSounds)
            if (b.Equals(t, StringComparison.OrdinalIgnoreCase)) return b;
        return Default;
    }

    public static bool IsExtensionAllowed(string? fileNameOrExt)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrExt)) return false;
        var ext = fileNameOrExt.Contains('.')
            ? System.IO.Path.GetExtension(fileNameOrExt)
            : "." + fileNameOrExt.TrimStart('.');
        foreach (var a in AllowedExtensions)
            if (a.Equals(ext, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static bool IsSizeAllowed(long bytes) => bytes > 0 && bytes <= MaxImportBytes;

    public static bool IsDurationAllowed(double s) => s >= MinDurationSeconds && s <= MaxDurationSeconds;

    // Mirror of HomeController.ResolveSoundForStage: Stage 1 is vibration-only by design, so it ALWAYS
    // resolves to "None" and never routes audio — there's no per-stage sound for it. Stages 2/3 use
    // their explicit choice, or "" inherits the single AlarmSound, so existing users see no change.
    public static string ResolveForStage(string? raw, string singleSound, bool customAvailable, int stage)
    {
        if (stage == 1) return NoneKey;
        if (string.IsNullOrWhiteSpace(raw))
            return singleSound;
        return Normalize(raw, customAvailable, allowNone: true);
    }
}

// =============================================================================
//  WHITELIST / NORMALISATION
// =============================================================================
public class AlarmSoundCatalogNormalizeTests
{
    [Theory]
    [InlineData("Digital Clock")]
    [InlineData("Siren")]
    [InlineData("Buzzer")]
    [InlineData("Bell")]
    [InlineData("Air Horn")]
    public void BundledVoices_PassThrough(string key)
        => Assert.Equal(key, SoundCatalogSpec.Normalize(key, customAvailable: false, allowNone: false));

    [Theory]
    [InlineData("siren", "Siren")]
    [InlineData("BUZZER", "Buzzer")]
    [InlineData("  Bell  ", "Bell")]
    public void BundledVoices_AreCanonicalised(string input, string expected)
        => Assert.Equal(expected, SoundCatalogSpec.Normalize(input, false, false));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Chime")]      // retired
    [InlineData("Default")]    // retired
    [InlineData("../../etc/passwd")]
    public void UnknownOrBlank_FallsBackToDefault(string? input)
        => Assert.Equal(SoundCatalogSpec.Default, SoundCatalogSpec.Normalize(input, true, true));

    // "Custom" is honoured ONLY while a custom file is actually available — otherwise it falls back to a
    // bundled voice (the deleted-custom-file safety net the spec requires).
    [Fact]
    public void Custom_KeptWhenAvailable()
        => Assert.Equal("Custom", SoundCatalogSpec.Normalize("Custom", customAvailable: true, allowNone: false));

    [Fact]
    public void Custom_FallsBackWhenMissing()
        => Assert.Equal(SoundCatalogSpec.Default,
            SoundCatalogSpec.Normalize("Custom", customAvailable: false, allowNone: false));

    // "None" is only valid where a stage allows a silent/vibration-led choice.
    [Fact]
    public void None_KeptWhenAllowed()
        => Assert.Equal("None", SoundCatalogSpec.Normalize("None", false, allowNone: true));

    [Fact]
    public void None_FallsBackWhenNotAllowed()
        => Assert.Equal(SoundCatalogSpec.Default, SoundCatalogSpec.Normalize("None", false, allowNone: false));
}

// =============================================================================
//  CUSTOM-SOUND IMPORT VALIDATION
// =============================================================================
public class CustomSoundImportValidationTests
{
    [Theory]
    [InlineData("ringtone.mp3")]
    [InlineData("alarm.OGG")]
    [InlineData("sound.wav")]
    [InlineData("clip.m4a")]
    [InlineData("beep.aac")]
    public void AllowedAudioExtensions_Accepted(string name)
        => Assert.True(SoundCatalogSpec.IsExtensionAllowed(name));

    [Theory]
    [InlineData("malware.exe")]
    [InlineData("song.flac")]   // not in the allowed set
    [InlineData("video.mp4")]
    [InlineData("noextension")]
    [InlineData("")]
    [InlineData(null)]
    public void DisallowedExtensions_Rejected(string? name)
        => Assert.False(SoundCatalogSpec.IsExtensionAllowed(name));

    [Theory]
    [InlineData(1, true)]
    [InlineData(10L * 1024 * 1024, true)]       // exactly at the cap
    [InlineData(10L * 1024 * 1024 + 1, false)]  // one byte over
    [InlineData(0, false)]                       // empty
    [InlineData(-5, false)]                      // nonsense
    public void SizeCap_EnforcedAtBoundary(long bytes, bool ok)
        => Assert.Equal(ok, SoundCatalogSpec.IsSizeAllowed(bytes));

    [Theory]
    [InlineData(0.5, true)]      // floor
    [InlineData(30.0, true)]
    [InlineData(300.0, true)]    // ceiling
    [InlineData(0.49, false)]    // too short
    [InlineData(300.01, false)]  // too long
    [InlineData(0.0, false)]     // unreadable / not audio
    public void Duration_MustBeUsableByTheAlarmLoop(double seconds, bool ok)
        => Assert.Equal(ok, SoundCatalogSpec.IsDurationAllowed(seconds));
}

// =============================================================================
//  PER-STAGE RESOLUTION (Feature 2)
// =============================================================================
public class PerStageSoundResolutionTests
{
    // Stage 1 is vibration-only by design: it NEVER resolves to an audio sound, no matter what key is
    // passed (empty, a bundled voice, or "Custom") — it always stays "None" (vibration-led).
    [Theory]
    [InlineData("")]
    [InlineData("Siren")]
    [InlineData("Custom")]
    [InlineData("Bogus")]
    public void Stage1_NeverResolvesAudio_AlwaysNone(string raw)
        => Assert.Equal("None", SoundCatalogSpec.ResolveForStage(raw, "Siren", customAvailable: true, stage: 1));

    // Default (no explicit per-stage choice): Stages 2 & 3 inherit the single chosen sound — i.e. exactly
    // the pre-feature behaviour, so existing users see no change.
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void EmptyDeeperStages_InheritSingleSound(int stage)
        => Assert.Equal("Siren", SoundCatalogSpec.ResolveForStage("", "Siren", false, stage));

    // Each stage can independently pick any bundled voice.
    [Fact]
    public void ExplicitPerStageChoices_AreIndependent()
    {
        Assert.Equal("Bell", SoundCatalogSpec.ResolveForStage("Bell", "Siren", false, 2));
        Assert.Equal("Air Horn", SoundCatalogSpec.ResolveForStage("Air Horn", "Siren", false, 3));
    }

    // A stage may be set to "None" (silent / vibration-led) explicitly.
    [Fact]
    public void StageCanBeExplicitlyNone()
        => Assert.Equal("None", SoundCatalogSpec.ResolveForStage("None", "Siren", false, 2));

    // A stage pointed at a custom sound that's gone falls back to the single sound's bundled default,
    // never an unplayable key.
    [Fact]
    public void StageCustom_FallsBackWhenMissing()
        => Assert.Equal(SoundCatalogSpec.Default,
            SoundCatalogSpec.ResolveForStage("Custom", "Siren", customAvailable: false, stage: 2));

    [Fact]
    public void StageCustom_KeptWhenAvailable()
        => Assert.Equal("Custom",
            SoundCatalogSpec.ResolveForStage("Custom", "Siren", customAvailable: true, stage: 2));
}
