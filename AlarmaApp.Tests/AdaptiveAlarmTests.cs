// =============================================================================
//  AdaptiveAlarmTests.cs
// -----------------------------------------------------------------------------
//  ISOLATED unit tests for the Multi-Stage Adaptive Alarm System, derived
//  strictly from:
//      Docs/NavAlert_Revised_Alarm_Computation.pdf  (Revised Technical Specs)
//
//  These tests are intentionally SELF-CONTAINED. Following the existing
//  convention in ValidationTests.cs, the spec's formulas are re-implemented
//  here as small pure helpers and asserted against the document's own
//  "Worked Example" / table values. This means:
//
//    * The suite runs on plain net9.0 with NO Android / MAUI SDK required.
//    * NO production code is referenced, imported, or modified.
//      In particular LocationTrackingService.cs, every .xaml file, and the
//      database are left completely untouched (see FINAL DIRECTIVE compliance
//      note at the bottom of this file).
//
//  If/when the production implementation is wired up, these helpers become the
//  executable specification the real code must satisfy — the hardcoded Asserts
//  are taken directly from the PDF tables, not from any implementation.
//
//  Spec section map:
//    §2  Rolling Average Speed         -> RollingAverageSpeedTests
//    §3  Adaptive Reaction Window      -> AdaptiveReactionWindowTests
//    §4  Speed-Based Boundaries        -> SpeedBasedBoundaryTests
//    §6  Worked Example (escalation)   -> WorkedExampleEscalationTests
// =============================================================================

using Xunit;

namespace AlarmaApp.Tests;

/// <summary>
/// Pure, dependency-free re-implementation of the formulas defined in
/// NavAlert_Revised_Alarm_Computation.pdf. Every constant and expression
/// traces back to a specific section of that document (cited inline).
/// </summary>
internal static class AdaptiveAlarmSpec
{
    // --- §2 Rolling Average Speed -------------------------------------------
    // "a rolling average over the most recent 90 seconds, sampled every 5 sec"
    public const int RollingWindowSeconds = 90;
    public const int SampleIntervalSeconds = 5;

    // "This produces 18 readings per window (90 sec / 5 sec = 18)"
    public const int ExpectedReadingCount = RollingWindowSeconds / SampleIntervalSeconds; // 18

    // --- §3 Adaptive Reaction Window ----------------------------------------
    public const double DefaultWindowMinutes = 8.0; // Trips 1-3 (first-time user)
    public const double MinWindowMinutes = 3.0;     // clamp floor
    public const double MaxWindowMinutes = 8.0;     // clamp ceiling
    public const double DisembarkBufferMinutes = 2.0;
    public const int AdaptiveTripThreshold = 4;     // adaptive logic starts at Trip 4

    // --- §4 Speed-Based Boundaries ------------------------------------------
    // "A ceiling of 5.0 km is enforced ... No fixed distance floor is applied"
    public const double BoundaryCeilingKm = 5.0;

    /// <summary>
    /// §2 — Rolling Average Speed = Sum of 18 readings / 18.
    /// </summary>
    public static double RollingAverageSpeed(IReadOnlyList<double> readings)
    {
        if (readings is null || readings.Count == 0)
            throw new ArgumentException("At least one reading is required.", nameof(readings));
        return readings.Sum() / readings.Count;
    }

    /// <summary>
    /// §3.2 — Adaptive window from response times (seconds):
    ///   Window(min) = avg(last 3 response times / 60) + 2-min buffer,
    ///   clamped to [3, 8].
    /// </summary>
    public static double AdaptiveWindowMinutes(IReadOnlyList<int> lastThreeResponseSeconds)
    {
        if (lastThreeResponseSeconds is null || lastThreeResponseSeconds.Count != 3)
            throw new ArgumentException("Exactly 3 response times are required.",
                nameof(lastThreeResponseSeconds));

        double avgMinutes = lastThreeResponseSeconds.Average() / 60.0;
        double raw = avgMinutes + DisembarkBufferMinutes;
        return Math.Clamp(raw, MinWindowMinutes, MaxWindowMinutes);
    }

    /// <summary>
    /// §3.1 / §3.2 — Window selection by trip number. Trips 1-3 use the 8-min
    /// default; from Trip 4 onward the adaptive formula applies.
    /// </summary>
    public static double WindowForTrip(int tripNumber, IReadOnlyList<int>? lastThreeResponseSeconds)
    {
        if (tripNumber < AdaptiveTripThreshold || lastThreeResponseSeconds is null)
            return DefaultWindowMinutes;
        return AdaptiveWindowMinutes(lastThreeResponseSeconds);
    }

    /// <summary>
    /// §4.1 — Stage 1 Boundary = RollingAvgSpeed(km/h) * Window(min) / 60,
    /// capped at the 5.0 km ceiling. No floor.
    /// </summary>
    public static double Stage1BoundaryKm(double rollingAvgSpeedKmh, double windowMinutes)
        => Math.Min(rollingAvgSpeedKmh * windowMinutes / 60.0, BoundaryCeilingKm);

    /// <summary>
    /// §4.2 — Stage 2 Boundary = full boundary * (2/3).
    /// Per Table 2 ("100+ km/h" row), the thirds are taken from the *ceilinged*
    /// full boundary (5.0 km -> 3.33 km), so we derive from Stage1BoundaryKm.
    /// </summary>
    public static double Stage2BoundaryKm(double rollingAvgSpeedKmh, double windowMinutes)
        => Stage1BoundaryKm(rollingAvgSpeedKmh, windowMinutes) * 2.0 / 3.0;

    /// <summary>
    /// §4.2 — Stage 3 Boundary = full boundary * (1/3). Derived from the
    /// ceilinged full boundary for the same reason as Stage 2 (see Table 2).
    /// </summary>
    public static double Stage3BoundaryKm(double rollingAvgSpeedKmh, double windowMinutes)
        => Stage1BoundaryKm(rollingAvgSpeedKmh, windowMinutes) * 1.0 / 3.0;

    /// <summary>
    /// §5 — A stage fires the instant remaining distance drops below its
    /// continuously recalculated boundary (strict less-than).
    /// </summary>
    public static bool StageFires(double remainingDistanceKm, double boundaryKm)
        => remainingDistanceKm < boundaryKm;
}

// =============================================================================
//  §2  ROLLING AVERAGE SPEED
// =============================================================================
public class RollingAverageSpeedTests
{
    // The window must contain exactly 18 readings (90s / 5s). This guards the
    // sampling contract itself, independent of the arithmetic.
    [Fact]
    public void Window_ProducesExactly18Readings()
        => Assert.Equal(18, AdaptiveAlarmSpec.ExpectedReadingCount);

    // Steady 15 km/h across all 18 samples averages to 15 km/h — the starting
    // speed used by the §6 Worked Example.
    [Fact]
    public void SteadySpeed_AveragesToThatSpeed()
    {
        var readings = Enumerable.Repeat(15.0, AdaptiveAlarmSpec.ExpectedReadingCount).ToList();
        Assert.Equal(15.0, AdaptiveAlarmSpec.RollingAverageSpeed(readings), 3);
    }

    // §2 rationale: "A momentary stop at a red light does not collapse the
    // boundary." Seventeen 15 km/h readings + one 0 km/h (red-light stop)
    // dampens to ~14.167 km/h rather than collapsing toward 0.
    [Fact]
    public void MomentaryStop_IsDampened_NotCollapsed()
    {
        var readings = Enumerable.Repeat(15.0, 17).Append(0.0).ToList();
        // (17 * 15 + 0) / 18 = 255 / 18 = 14.1666...
        Assert.Equal(14.167, AdaptiveAlarmSpec.RollingAverageSpeed(readings), 3);
    }

    // §2 rationale: "a brief burst of speed does not inflate it." Seventeen
    // 15 km/h readings + one 60 km/h burst rises only to ~17.5 km/h.
    [Fact]
    public void BriefBurst_IsDampened_NotInflated()
    {
        var readings = Enumerable.Repeat(15.0, 17).Append(60.0).ToList();
        // (17 * 15 + 60) / 18 = 315 / 18 = 17.5
        Assert.Equal(17.5, AdaptiveAlarmSpec.RollingAverageSpeed(readings), 3);
    }

    // General arithmetic check with a fully mixed stop-and-go window.
    [Fact]
    public void MixedReadings_AverageCorrectly()
    {
        // 9 readings @ 10 and 9 readings @ 20 -> mean 15.
        var readings = Enumerable.Repeat(10.0, 9)
            .Concat(Enumerable.Repeat(20.0, 9))
            .ToList();
        Assert.Equal(15.0, AdaptiveAlarmSpec.RollingAverageSpeed(readings), 3);
    }

    [Fact]
    public void EmptyReadings_Throws()
        => Assert.Throws<ArgumentException>(
            () => AdaptiveAlarmSpec.RollingAverageSpeed(Array.Empty<double>()));
}

// =============================================================================
//  §3  ADAPTIVE REACTION WINDOW
// =============================================================================
public class AdaptiveReactionWindowTests
{
    // §3.1 — Trips 1-3 always use the 8-minute default while data is collected,
    // regardless of any response times supplied. (Table 1, rows 1-3.)
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FirstThreeTrips_UseDefault8MinuteWindow(int tripNumber)
        => Assert.Equal(8.0,
            AdaptiveAlarmSpec.WindowForTrip(tripNumber, new[] { 10, 12, 8 }), 3);

    // §3.2 / Table 1, row 4 — the document's adaptive worked example:
    //   avg(10, 12, 8) = 10s ; 10/60 + 2 = 2.167 min ; clamped up to 3 min.
    [Fact]
    public void Trip4_WorkedExample_ClampsToMinimum3Minutes()
    {
        double window = AdaptiveAlarmSpec.WindowForTrip(4, new[] { 10, 12, 8 });
        Assert.Equal(3.0, window, 3);
    }

    // The raw (pre-clamp) value from Table 1 is exactly 2.167 min — verify the
    // formula itself before the clamp is applied.
    [Fact]
    public void Trip4_RawFormula_Yields2Point167_BeforeClamp()
    {
        double rawAvgMinutes = new[] { 10, 12, 8 }.Average() / 60.0; // 0.16667
        double raw = rawAvgMinutes + AdaptiveAlarmSpec.DisembarkBufferMinutes;
        Assert.Equal(2.167, raw, 3);
    }

    // Mid-range case (no clamping): avg response 180s -> 180/60 + 2 = 5 min,
    // which sits inside [3, 8] and is returned unchanged.
    [Fact]
    public void MidRangeResponses_ReturnedWithinBounds()
        => Assert.Equal(5.0,
            AdaptiveAlarmSpec.AdaptiveWindowMinutes(new[] { 180, 180, 180 }), 3);

    // Upper clamp: very slow responders (e.g. 600s each) compute to
    // 10 + 2 = 12 min but must be capped at the 8-minute maximum.
    [Fact]
    public void VerySlowResponses_ClampToMaximum8Minutes()
        => Assert.Equal(8.0,
            AdaptiveAlarmSpec.AdaptiveWindowMinutes(new[] { 600, 600, 600 }), 3);

    // Lower clamp boundary: instantaneous (0s) responses -> 0 + 2 = 2 min,
    // clamped up to the 3-minute floor.
    [Fact]
    public void InstantResponses_ClampToMinimum3Minutes()
        => Assert.Equal(3.0,
            AdaptiveAlarmSpec.AdaptiveWindowMinutes(new[] { 0, 0, 0 }), 3);

    // Exact-boundary check: avg 60s -> 1 + 2 = 3 min lands exactly on the floor
    // and must NOT be pushed off it.
    [Fact]
    public void ResponsesLandingExactlyOnFloor_AreUnchanged()
        => Assert.Equal(3.0,
            AdaptiveAlarmSpec.AdaptiveWindowMinutes(new[] { 60, 60, 60 }), 3);

    // Exact-boundary check: avg 360s -> 6 + 2 = 8 min lands exactly on the
    // ceiling and must NOT be pushed off it.
    [Fact]
    public void ResponsesLandingExactlyOnCeiling_AreUnchanged()
        => Assert.Equal(8.0,
            AdaptiveAlarmSpec.AdaptiveWindowMinutes(new[] { 360, 360, 360 }), 3);

    [Fact]
    public void WrongNumberOfResponseTimes_Throws()
        => Assert.Throws<ArgumentException>(
            () => AdaptiveAlarmSpec.AdaptiveWindowMinutes(new[] { 10, 12 }));
}

// =============================================================================
//  §4  SPEED-BASED BOUNDARY CALCULATION
// =============================================================================
public class SpeedBasedBoundaryTests
{
    // -- §6.2 Worked Example anchor -----------------------------------------
    // "At 15 km/h with 8-minute window: Stage 1 Boundary = 15 * 8 / 60 = 2.000 km"
    [Fact]
    public void Stage1_WorkedExample_15kmh_8minWindow_Equals2km()
        => Assert.Equal(2.000, AdaptiveAlarmSpec.Stage1BoundaryKm(15.0, 8.0), 3);

    // -- Table 2: Boundary Values Across Speed Ranges (3-min Window) ----------
    // Each row is asserted to 2 decimals to match the document's rounding.
    [Theory]
    [InlineData(5.0, 0.25, 0.17, 0.08)]   // Heavy traffic
    [InlineData(15.0, 0.75, 0.50, 0.25)]  // Typical urban
    [InlineData(25.0, 1.25, 0.83, 0.42)]  // Moderate flow
    [InlineData(40.0, 2.00, 1.33, 0.67)]  // Clear road
    public void Table2_BoundaryValues_3MinuteWindow(
        double speedKmh, double expectedS1, double expectedS2, double expectedS3)
    {
        const double window = 3.0;
        Assert.Equal(expectedS1, AdaptiveAlarmSpec.Stage1BoundaryKm(speedKmh, window), 2);
        Assert.Equal(expectedS2, AdaptiveAlarmSpec.Stage2BoundaryKm(speedKmh, window), 2);
        Assert.Equal(expectedS3, AdaptiveAlarmSpec.Stage3BoundaryKm(speedKmh, window), 2);
    }

    // Table 2, "100+ km/h" row: 100 * 3 / 60 = 5.0 km, exactly on the ceiling.
    // Stage 2/3 are thirds of that ceilinged boundary: 3.33 km and 1.67 km.
    [Fact]
    public void Table2_CeilingRow_100kmh_3minWindow()
    {
        const double window = 3.0;
        Assert.Equal(5.00, AdaptiveAlarmSpec.Stage1BoundaryKm(100.0, window), 2);
        Assert.Equal(3.33, AdaptiveAlarmSpec.Stage2BoundaryKm(100.0, window), 2);
        Assert.Equal(1.67, AdaptiveAlarmSpec.Stage3BoundaryKm(100.0, window), 2);
    }

    // §4.1 — the 5.0 km ceiling holds even when raw speed*window/60 blows past
    // it (e.g. 120 km/h * 8 / 60 = 16 km would-be boundary, capped to 5.0 km).
    [Fact]
    public void Stage1_CeilingIsEnforced_AtHighSpeed()
        => Assert.Equal(5.0, AdaptiveAlarmSpec.Stage1BoundaryKm(120.0, 8.0), 3);

    // §4.1 — "No fixed distance floor is applied": slow speeds yield genuinely
    // small boundaries rather than being floored to 1.0 km (the removed legacy
    // behavior). 5 km/h * 3 / 60 = 0.25 km, well under any old floor.
    [Fact]
    public void Stage1_NoDistanceFloor_AtLowSpeed()
    {
        double boundary = AdaptiveAlarmSpec.Stage1BoundaryKm(5.0, 3.0);
        Assert.Equal(0.25, boundary, 3);
        Assert.True(boundary < 1.0, "A fixed 1.0 km floor must NOT be applied.");
    }

    // Structural invariant from §4.2: the three boundaries are strict thirds,
    // so Stage1 : Stage2 : Stage3 == 3 : 2 : 1 at any (non-ceilinged) speed.
    [Theory]
    [InlineData(15.0, 8.0)]
    [InlineData(25.0, 5.0)]
    [InlineData(40.0, 3.0)]
    public void Boundaries_AreOrderedThirds_BelowCeiling(double speedKmh, double window)
    {
        double s1 = AdaptiveAlarmSpec.Stage1BoundaryKm(speedKmh, window);
        double s2 = AdaptiveAlarmSpec.Stage2BoundaryKm(speedKmh, window);
        double s3 = AdaptiveAlarmSpec.Stage3BoundaryKm(speedKmh, window);

        Assert.True(s1 > s2 && s2 > s3, "Boundaries must shrink Stage1 > Stage2 > Stage3.");
        Assert.Equal(s1 * 2.0 / 3.0, s2, 6);
        Assert.Equal(s1 * 1.0 / 3.0, s3, 6);
    }
}

// =============================================================================
//  §6  WORKED EXAMPLE — CONTINUOUS ESCALATION
//      (First-time user, 8-minute window, vehicle accelerating after Stage 1)
// =============================================================================
public class WorkedExampleEscalationTests
{
    private const double WorkedExampleWindow = 8.0; // §6.1 first-time user

    // -- §6.2 Stage 1 ---------------------------------------------------------
    // Stage 1 fires at 2.000 km remaining (15 km/h * 8 / 60).
    [Fact]
    public void Stage1_FiresAt2km()
    {
        double boundary = AdaptiveAlarmSpec.Stage1BoundaryKm(15.0, WorkedExampleWindow);
        Assert.Equal(2.000, boundary, 3);
        // Remaining distance has just reached the boundary -> fires.
        Assert.True(AdaptiveAlarmSpec.StageFires(1.999, boundary));
    }

    // -- §6.3 / Table 4: Stage 2 Escalation Tracking -------------------------
    // Each row: (elapsed sec, rolling avg km/h, expected Stage 2 boundary km,
    //            remaining distance km, does Stage 2 fire?).
    // The PDF's "Rolling Avg" column is rounded to 2 decimals but its boundary
    // column was computed from the *unrounded* speed, so we assert the boundary
    // within a 2 m (0.002 km) tolerance — the precision the rounded speed input
    // actually carries. The fire/no-fire column verifies the strict
    // remaining < boundary rule.
    [Theory]
    [InlineData(15.00, 1.333, 2.000, false)] //  0s
    [InlineData(15.17, 1.348, 1.979, false)] //  5s
    [InlineData(15.50, 1.378, 1.956, false)] // 10s
    [InlineData(16.00, 1.422, 1.929, false)] // 15s
    [InlineData(16.67, 1.481, 1.897, false)] // 20s
    [InlineData(17.33, 1.541, 1.862, false)] // 25s
    [InlineData(18.17, 1.615, 1.820, false)] // 30s
    [InlineData(19.17, 1.704, 1.771, false)] // 35s
    [InlineData(20.17, 1.793, 1.722, true)]  // 40s -> FIRES (1.722 < 1.793)
    public void Table4_Stage2_EscalationTracking(
        double rollingAvgKmh, double expectedBoundaryKm,
        double remainingKm, bool expectedToFire)
    {
        double boundary = AdaptiveAlarmSpec.Stage2BoundaryKm(rollingAvgKmh, WorkedExampleWindow);
        Assert.Equal(expectedBoundaryKm, boundary, 0.002);
        Assert.Equal(expectedToFire, AdaptiveAlarmSpec.StageFires(remainingKm, boundary));
    }

    // -- §6.4 / Table 5: Stage 3 Escalation Tracking -------------------------
    [Theory]
    [InlineData(20.17, 0.896, 1.722, false)] // 40s
    [InlineData(21.00, 0.933, 1.673, false)] // 45s
    [InlineData(21.83, 0.970, 1.624, false)] // 50s
    [InlineData(22.50, 1.000, 1.575, false)] // 55s
    [InlineData(23.17, 1.030, 1.523, false)] // 60s
    [InlineData(23.83, 1.059, 1.468, false)] // 65s
    [InlineData(24.33, 1.081, 1.411, false)] // 70s
    [InlineData(24.83, 1.104, 1.352, false)] // 75s
    [InlineData(25.17, 1.119, 1.292, false)] // 80s
    [InlineData(25.50, 1.133, 1.230, false)] // 85s
    [InlineData(25.67, 1.141, 1.166, false)] // 90s
    [InlineData(25.83, 1.148, 1.101, true)]  // 95s -> FIRES (1.101 < 1.148)
    public void Table5_Stage3_EscalationTracking(
        double rollingAvgKmh, double expectedBoundaryKm,
        double remainingKm, bool expectedToFire)
    {
        double boundary = AdaptiveAlarmSpec.Stage3BoundaryKm(rollingAvgKmh, WorkedExampleWindow);
        Assert.Equal(expectedBoundaryKm, boundary, 0.002);
        Assert.Equal(expectedToFire, AdaptiveAlarmSpec.StageFires(remainingKm, boundary));
    }

    // -- §6.5 Summary --------------------------------------------------------
    // End-to-end snapshot of the three fire points the document summarizes.
    [Fact]
    public void Summary_AllThreeStagesFireAtDocumentedDistances()
    {
        // Stage 1: boundary 2.000 km at 15.00 km/h.
        Assert.True(AdaptiveAlarmSpec.StageFires(
            2.000 - 0.001, AdaptiveAlarmSpec.Stage1BoundaryKm(15.00, WorkedExampleWindow)));

        // Stage 2: remaining 1.722 km < boundary at 20.17 km/h.
        Assert.True(AdaptiveAlarmSpec.StageFires(
            1.722, AdaptiveAlarmSpec.Stage2BoundaryKm(20.17, WorkedExampleWindow)));

        // Stage 3: remaining 1.101 km < boundary at 25.83 km/h.
        Assert.True(AdaptiveAlarmSpec.StageFires(
            1.101, AdaptiveAlarmSpec.Stage3BoundaryKm(25.83, WorkedExampleWindow)));
    }
}

// =============================================================================
//  FINAL DIRECTIVE COMPLIANCE
// -----------------------------------------------------------------------------
//  This file is the ONLY artifact generated for this task. It was ADDED to the
//  existing AlarmaApp.Tests project (which uses SDK-style wildcard .cs
//  inclusion, so no .csproj edit was needed). No existing project file was
//  modified — specifically NOT LocationTrackingService.cs, NOT any .xaml file,
//  and NOT the database. The tests reference no production code.
// =============================================================================
