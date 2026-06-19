// =============================================================================
//  ProgressiveAlarmStateMachineTests.cs
// -----------------------------------------------------------------------------
//  ISOLATED tests for the 3-stage progressive alarm STATE MACHINE — the decision
//  layer that turns a continuously-updated "remaining distance to destination"
//  into the current alarm stage (None -> Stage 1 -> Stage 2 -> Stage 3).
//
//  AdaptiveAlarmTests.cs already proves the BOUNDARY MATH (how each stage's
//  trigger distance is computed). This file proves the ESCALATION LOGIC layered
//  on top of it:
//    * a stage fires strictly when remaining < its boundary,
//    * escalation latches upward (Stage 3 is a wake-up lockout; it never
//      silently downgrades if the bus drifts back out a few metres),
//    * a single fix already deep inside the inner ring jumps straight to Stage 3.
//
//  Self-contained per the project convention (see AdaptiveAlarmTests.cs): no
//  production code is referenced or modified. The boundary inputs are taken from
//  the same AdaptiveAlarmSpec helper used by the math tests so the two stay in
//  lock-step.
// =============================================================================

using Xunit;

namespace AlarmaApp.Tests;

public enum AlarmStageT { None = 0, Stage1 = 1, Stage2 = 2, Stage3 = 3 }

/// <summary>
/// Pure decision logic: given the three (already-computed) stage boundaries in
/// the same units as the fed distances, escalate and latch the alarm stage as
/// remaining distance is reported. Mirrors the production rule
/// (HomeController staging + AdaptiveAlarmSpec.StageFires: strict remaining &lt; boundary).
/// </summary>
public sealed class ProgressiveAlarmStateMachine
{
    private readonly double _s1, _s2, _s3;
    public AlarmStageT Current { get; private set; } = AlarmStageT.None;

    public ProgressiveAlarmStateMachine(double s1Boundary, double s2Boundary, double s3Boundary)
    {
        if (!(s1Boundary > s2Boundary && s2Boundary > s3Boundary))
            throw new ArgumentException("Boundaries must shrink: s1 > s2 > s3.");
        _s1 = s1Boundary; _s2 = s2Boundary; _s3 = s3Boundary;
    }

    /// <summary>Feed the latest remaining distance; returns the (possibly escalated) stage.</summary>
    public AlarmStageT Update(double remainingDistance)
    {
        var target =
            remainingDistance < _s3 ? AlarmStageT.Stage3 :
            remainingDistance < _s2 ? AlarmStageT.Stage2 :
            remainingDistance < _s1 ? AlarmStageT.Stage1 :
                                      AlarmStageT.None;

        // Escalate + latch — the alarm only ever climbs; a GPS wobble outward can't downgrade it.
        if (target > Current)
            Current = target;
        return Current;
    }
}

public class ProgressiveAlarmStateMachineTests
{
    // §6.2 worked example boundaries (15 km/h, 8-min window): 2.000 / 1.333 / 0.667 km.
    private const double S1 = 2.000, S2 = 4.0 / 3.0, S3 = 2.0 / 3.0;

    private static ProgressiveAlarmStateMachine Fresh() => new(S1, S2, S3);

    // -- Threshold table: a FRESH machine maps a single distance to the right stage ----------
    [Theory]
    [InlineData(5.000, AlarmStageT.None)]    // far away
    [InlineData(2.001, AlarmStageT.None)]    // just outside Stage 1
    [InlineData(2.000, AlarmStageT.None)]    // exactly on the boundary -> strict '<' means NO fire
    [InlineData(1.999, AlarmStageT.Stage1)]  // just inside Stage 1
    [InlineData(1.500, AlarmStageT.Stage1)]  // between S2 and S1
    [InlineData(1.400, AlarmStageT.Stage1)]  // still above S2 (1.333) -> Stage 1
    [InlineData(1.000, AlarmStageT.Stage2)]  // inside Stage 2
    [InlineData(0.667, AlarmStageT.Stage2)]  // exactly on S3 -> not yet Stage 3
    [InlineData(0.500, AlarmStageT.Stage3)]  // inside Stage 3
    [InlineData(0.000, AlarmStageT.Stage3)]  // arrived
    public void FreshMachine_MapsDistanceToStage(double remaining, AlarmStageT expected)
        => Assert.Equal(expected, Fresh().Update(remaining));

    // -- Ordered escalation as the bus approaches --------------------------------------------
    [Fact]
    public void Approaching_EscalatesNoneToStage1ToStage2ToStage3_InOrder()
    {
        var sm = Fresh();
        Assert.Equal(AlarmStageT.None, sm.Update(3.0));
        Assert.Equal(AlarmStageT.Stage1, sm.Update(1.8));
        Assert.Equal(AlarmStageT.Stage2, sm.Update(1.1));
        Assert.Equal(AlarmStageT.Stage3, sm.Update(0.4));
    }

    // -- Latching: Stage 3 is a lockout; drifting back out does NOT downgrade ----------------
    [Fact]
    public void OnceStage3_DoesNotDowngrade_WhenDistanceGrows()
    {
        var sm = Fresh();
        sm.Update(0.3);                          // -> Stage 3
        Assert.Equal(AlarmStageT.Stage3, sm.Current);

        Assert.Equal(AlarmStageT.Stage3, sm.Update(1.2)); // overshoot/wobble outward
        Assert.Equal(AlarmStageT.Stage3, sm.Update(5.0)); // way back out — still latched
    }

    [Fact]
    public void Stage2_DoesNotDowngradeToStage1_OnOutwardWobble()
    {
        var sm = Fresh();
        sm.Update(1.0); // Stage 2
        Assert.Equal(AlarmStageT.Stage2, sm.Update(1.9)); // back between S2 and S1 — stays Stage 2
    }

    // -- A single fix already deep inside jumps straight to Stage 3 (no intermediate replay) --
    [Fact]
    public void FirstFixDeepInside_JumpsStraightToStage3()
    {
        var sm = Fresh();
        Assert.Equal(AlarmStageT.Stage3, sm.Update(0.1));
    }

    // -- Guard: boundaries must be strictly ordered ------------------------------------------
    [Theory]
    [InlineData(1.0, 1.0, 0.5)]   // s1 == s2
    [InlineData(2.0, 0.5, 1.0)]   // s2 < s3
    [InlineData(0.5, 1.0, 2.0)]   // fully inverted
    public void Constructor_RejectsNonShrinkingBoundaries(double s1, double s2, double s3)
        => Assert.Throws<ArgumentException>(() => new ProgressiveAlarmStateMachine(s1, s2, s3));

    // -- Ties to the real boundary math: thresholds come from AdaptiveAlarmSpec --------------
    [Fact]
    public void Boundaries_DerivedFromAdaptiveSpec_FireAtComputedDistances()
    {
        // 15 km/h, 8-min window → 2.000 / 1.333 / 0.667 km (see AdaptiveAlarmTests).
        var s1 = AdaptiveAlarmSpec.Stage1BoundaryKm(15.0, 8.0);
        var s2 = AdaptiveAlarmSpec.Stage2BoundaryKm(15.0, 8.0);
        var s3 = AdaptiveAlarmSpec.Stage3BoundaryKm(15.0, 8.0);
        var sm = new ProgressiveAlarmStateMachine(s1, s2, s3);

        Assert.Equal(AlarmStageT.Stage1, sm.Update(s1 - 0.001));
        Assert.Equal(AlarmStageT.Stage2, sm.Update(s2 - 0.001));
        Assert.Equal(AlarmStageT.Stage3, sm.Update(s3 - 0.001));
    }
}
