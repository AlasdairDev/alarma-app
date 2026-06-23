// =============================================================================
//  OvershootDistanceTests.cs
// -----------------------------------------------------------------------------
//  ISOLATED tests for the user-configurable overshoot trigger distance (Feature 3):
//    * the [50, 500] m clamp on input and on backup-restore (0 -> default 250),
//    * the threshold fed into detection: ArrivalThreshold + buffer + accuracy,
//    * the monotonic, outlier-resistant latch rule (consecutive increasing fixes,
//      reset on any move back toward the destination) — unchanged by the feature.
//
//  Self-contained per project convention; mirrors HomeController / BackupService.
// =============================================================================

using Xunit;

namespace AlarmaApp.Tests;

internal static class OvershootSpec
{
    public const int Default = 250;
    public const int Min = 50;
    public const int Max = 500;
    public const double ArrivalThresholdMeters = 200;
    public const int IncreasePersistenceFixes = 3;

    public static int Clamp(int meters) => System.Math.Clamp(meters, Min, Max);

    // Backup restore: a 0 / absent value snaps to the default rather than the floor.
    public static int ClampRestored(int meters)
        => Clamp(meters <= 0 ? Default : meters);

    // Mirror of HomeController.CommitOvershootDistance: parse the typed text and either keep an in-range
    // number, clamp an out-of-range one to [Min,Max], or revert empty/garbage to the last valid value.
    // Returns the value to store and whether the entry was reverted (empty/non-numeric).
    public static (int Value, bool Reverted, bool Clamped) Commit(string? raw, int lastValid)
    {
        var t = (raw ?? string.Empty).Trim();
        if (!int.TryParse(t, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return (lastValid, true, false);   // empty / non-numeric -> revert, never persist
        var clamped = Clamp(parsed);
        return (clamped, false, clamped != parsed);
    }

    public static double Threshold(int bufferMeters, double accuracyMeters)
        => ArrivalThresholdMeters + bufferMeters + System.Math.Max(accuracyMeters, 0);
}

public class OvershootDistanceClampTests
{
    [Theory]
    [InlineData(50, 50)]
    [InlineData(250, 250)]
    [InlineData(500, 500)]
    public void InBand_Unchanged(int input, int expected)
        => Assert.Equal(expected, OvershootSpec.Clamp(input));

    [Theory]
    [InlineData(49, 50)]
    [InlineData(-100, 50)]
    [InlineData(501, 500)]
    [InlineData(1000, 500)]       // the old maximum now clamps down to 500
    [InlineData(int.MaxValue, 500)]
    public void OutOfBand_Clamped(int input, int expected)
        => Assert.Equal(expected, OvershootSpec.Clamp(input));

    [Theory]
    [InlineData(0, 250)]      // absent in an older backup -> default, not the floor
    [InlineData(-1, 250)]
    [InlineData(300, 300)]
    [InlineData(5000, 500)]   // out of range -> clamps to 500, not 1000
    public void RestoreClamp_DefaultsThenBounds(int stored, int expected)
        => Assert.Equal(expected, OvershootSpec.ClampRestored(stored));
}

// =============================================================================
//  MANUAL NUMERIC ENTRY — commit/parse path for the typed overshoot field
// =============================================================================
public class OvershootEntryCommitTests
{
    // A valid in-range number sticks exactly, with no revert and no clamp.
    [Theory]
    [InlineData("250", 250)]
    [InlineData("50", 50)]
    [InlineData("500", 500)]
    [InlineData("  400 ", 400)]   // surrounding whitespace is trimmed
    public void InRange_Sticks(string typed, int expected)
    {
        var (value, reverted, clamped) = OvershootSpec.Commit(typed, lastValid: 250);
        Assert.Equal(expected, value);
        Assert.False(reverted);
        Assert.False(clamped);
    }

    // An out-of-range number is accepted but clamped to the nearest bound (not reverted).
    [Theory]
    [InlineData("999", 500)]
    [InlineData("501", 500)]
    [InlineData("49", 50)]
    [InlineData("0", 50)]
    public void OutOfRange_Clamps(string typed, int expected)
    {
        var (value, reverted, clamped) = OvershootSpec.Commit(typed, lastValid: 250);
        Assert.Equal(expected, value);
        Assert.False(reverted);
        Assert.True(clamped);
    }

    // Empty or non-numeric input never persists — it reverts to the last valid value and flags the revert
    // (so the UI can show inline feedback). Crucially it does not throw.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("12a")]
    [InlineData("250.5")]   // not a whole number
    public void EmptyOrGarbage_RevertsToLastValid(string typed)
    {
        var lastValid = 175;
        var (value, reverted, _) = OvershootSpec.Commit(typed, lastValid);
        Assert.Equal(lastValid, value);   // last valid value preserved, never persisted as garbage
        Assert.True(reverted);
    }
}

public class OvershootThresholdTests
{
    // Default buffer reproduces today's 450 m (200 arrival + 250) with no accuracy slack.
    [Fact]
    public void DefaultBuffer_Reproduces450m()
        => Assert.Equal(450, OvershootSpec.Threshold(OvershootSpec.Default, 0));

    // A tighter buffer triggers sooner; a wider one later.
    [Theory]
    [InlineData(50, 250)]
    [InlineData(500, 700)]
    public void Threshold_TracksConfiguredBuffer(int buffer, double expectedNoAccuracy)
        => Assert.Equal(expectedNoAccuracy, OvershootSpec.Threshold(buffer, 0));

    // The per-fix accuracy buffer still widens the threshold on top of the user value.
    [Fact]
    public void AccuracyBuffer_AddsOnTop()
        => Assert.Equal(450 + 30, OvershootSpec.Threshold(OvershootSpec.Default, 30));
}

// =============================================================================
//  MONOTONIC, OUTLIER-RESISTANT LATCH — unchanged by making the distance configurable
// =============================================================================
public class OvershootLatchTests
{
    // Mirrors HomeController's streak: increment only when past the threshold AND farther than the last
    // fix; reset the moment the rider moves back toward the stop; latch after 3 consecutive increases.
    private static bool Latches(int buffer, IEnumerable<double> distances, double accuracy = 0)
    {
        var threshold = OvershootSpec.Threshold(buffer, accuracy);
        int streak = 0;
        double last = double.MaxValue;
        foreach (var d in distances)
        {
            if (d >= threshold && d > last) streak++;
            else if (d < last) streak = 0;
            last = d;
            if (streak >= OvershootSpec.IncreasePersistenceFixes) return true;
        }
        return false;
    }

    // Three consecutive increasing fixes past a 250 m buffer (>450 m) latch the overshoot.
    [Fact]
    public void ThreeIncreasingFixesPastThreshold_Latch()
        => Assert.True(Latches(250, new double[] { 300, 500, 600, 700 }));

    // A single far fix that then comes back does NOT latch (outlier resistance).
    [Fact]
    public void SingleOutlierThenReturn_DoesNotLatch()
        => Assert.False(Latches(250, new double[] { 460, 470, 300, 250 }));

    // With the widest custom buffer (500 m -> 700 m threshold), a short 500–650 m drift stays under
    // threshold -> no latch.
    [Fact]
    public void WideBuffer_SuppressesShortOvershoot()
        => Assert.False(Latches(500, new double[] { 500, 550, 600, 650 }));

    // With a tight custom buffer (50 m), an overshoot past ~250 m latches where the default wouldn't.
    [Fact]
    public void TightBuffer_LatchesEarlierThanDefault()
    {
        var run = new double[] { 260, 300, 340, 380 };
        Assert.True(Latches(50, run));    // tight: 260 > 250 threshold, increasing -> latches
        Assert.False(Latches(250, run));  // default: all < 450 threshold -> never latches
    }
}
