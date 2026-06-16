// =============================================================================
//  RouteDeviationTests.cs
// -----------------------------------------------------------------------------
//  ISOLATED unit tests for the multi-leg-commute-aware route-deviation logic in
//  HomeController.HandleDestinationDistanceAsync.
//
//  Following the existing convention in ValidationTests.cs / AdaptiveAlarmTests.cs,
//  the deviation decision is re-implemented here as small pure helpers that mirror
//  the production constants and predicate. The suite runs on plain net9.0 with no
//  Android / MAUI SDK and references no production code.
//
//  Behaviour under test (matches HomeController):
//    * Proportional buffer:  max(400 m, 0.5 × closestApproach) + accuracy.
//    * Arm radius:           max(adaptiveLeadDistance, 3000 m); deviation only
//                            evaluated once the closest approach is within it.
//    * Persistence:          requires 4 consecutive "moving away" fixes to fire.
//    * Dwell re-baselining:  at/under 0.5 m/s the closest-approach anchor resets
//                            to the current distance (transfer terminal grace).
// =============================================================================

using Xunit;

namespace AlarmaApp.Tests;

internal static class RouteDeviationSpec
{
    public const double BaseBufferMeters = 400;
    public const double ProportionalFraction = 0.5;
    public const double ArmRadiusMeters = 3000;
    public const double DwellSpeedThresholdMps = 0.5;
    public const int PersistenceFixes = 4;

    public static double DeviationBuffer(double closestApproach, double accuracy)
        => System.Math.Max(BaseBufferMeters, ProportionalFraction * closestApproach) + accuracy;

    public static double ArmRadius(double adaptiveLeadDistance)
        => System.Math.Max(adaptiveLeadDistance, ArmRadiusMeters);

    public static bool IsArmed(double closestApproach, double adaptiveLeadDistance)
        => closestApproach <= ArmRadius(adaptiveLeadDistance);

    public static bool IsMovingAway(double distance, double closestApproach, double accuracy)
        => distance > closestApproach + DeviationBuffer(closestApproach, accuracy);

    public static bool IsDwell(double speedMps) => speedMps <= DwellSpeedThresholdMps;
}

// =============================================================================
//  PROPORTIONAL BUFFER — the 400 m base absorbs a typical transfer walk
// =============================================================================
public class DeviationBufferTests
{
    // A ~350 m walk from a jeepney drop-off to an FX terminal, off a 600 m closest
    // approach, sits inside the 400 m base buffer → NOT a deviation.
    [Fact]
    public void TransferWalk_WithinBaseBuffer_IsNotDeviation()
        => Assert.False(RouteDeviationSpec.IsMovingAway(distance: 950, closestApproach: 600, accuracy: 0));

    // Genuine wrong-direction travel: 300 m closest, now 900 m (> 300 + 400) → deviation.
    [Fact]
    public void SustainedWrongDirection_ExceedsBuffer_IsDeviation()
        => Assert.True(RouteDeviationSpec.IsMovingAway(distance: 900, closestApproach: 300, accuracy: 0));

    // Proportional component dominates when far out: 4 km closest → buffer = 2 km, so
    // drifting to 5 km (1 km past) is still within tolerance.
    [Fact]
    public void ProportionalBuffer_ScalesWithDistance()
    {
        double buffer = RouteDeviationSpec.DeviationBuffer(closestApproach: 4000, accuracy: 0);
        Assert.Equal(2000, buffer, 3);
        Assert.False(RouteDeviationSpec.IsMovingAway(distance: 5000, closestApproach: 4000, accuracy: 0));
    }

    // Base buffer is the floor for near-destination distances.
    [Fact]
    public void BaseBuffer_IsFloor_WhenClose()
        => Assert.Equal(400, RouteDeviationSpec.DeviationBuffer(closestApproach: 100, accuracy: 0), 3);
}

// =============================================================================
//  ARM RADIUS — deviation is dormant during the far early-trip legs
// =============================================================================
public class DeviationArmingTests
{
    // 5 km out with a city-speed lead distance → not armed (early-trip legs to a
    // main thoroughfare must not trip the alarm).
    [Fact]
    public void FarFromDestination_NotArmed()
        => Assert.False(RouteDeviationSpec.IsArmed(closestApproach: 5000, adaptiveLeadDistance: 300));

    // Within the 3 km default arm radius → armed.
    [Fact]
    public void WithinArmRadius_IsArmed()
        => Assert.True(RouteDeviationSpec.IsArmed(closestApproach: 2000, adaptiveLeadDistance: 300));

    // Highway speed widens the arm radius via the adaptive lead distance (e.g. 5 km),
    // so deviation arms earlier when ground is covered fast.
    [Fact]
    public void HighSpeed_WidensArmRadius_ViaAdaptiveLead()
    {
        Assert.Equal(5000, RouteDeviationSpec.ArmRadius(adaptiveLeadDistance: 5000), 3);
        Assert.True(RouteDeviationSpec.IsArmed(closestApproach: 4500, adaptiveLeadDistance: 5000));
    }
}

// =============================================================================
//  PERSISTENCE — a single noisy fix or brief detour must not fire
// =============================================================================
public class DeviationPersistenceTests
{
    // Simulate the controller's streak counter over a fix sequence. `true` = an
    // armed "moving away" fix; `false` = improving / not armed / dwell (streak resets).
    private static bool FiresOverSequence(IEnumerable<bool> movingAwayFixes)
    {
        int streak = 0;
        foreach (var away in movingAwayFixes)
        {
            if (away)
            {
                streak++;
                if (streak >= RouteDeviationSpec.PersistenceFixes) return true;
            }
            else
            {
                streak = 0;
            }
        }
        return false;
    }

    [Fact]
    public void ThreeAwayFixes_DoNotFire()
        => Assert.False(FiresOverSequence(new[] { true, true, true }));

    [Fact]
    public void FourConsecutiveAwayFixes_Fire()
        => Assert.True(FiresOverSequence(new[] { true, true, true, true }));

    // A detour that resolves (away, away, improve) restarts the count, so the
    // following two away fixes do not reach the threshold.
    [Fact]
    public void DetourThatResolves_DoesNotFire()
        => Assert.False(FiresOverSequence(new[] { true, true, false, true, true }));
}

// =============================================================================
//  DWELL RE-BASELINING — the transfer-terminal grace
// =============================================================================
public class DeviationDwellTests
{
    [Fact]
    public void StoppedAtTerminal_IsDwell_WalkingIsNot()
    {
        Assert.True(RouteDeviationSpec.IsDwell(0.0));    // waiting
        Assert.True(RouteDeviationSpec.IsDwell(0.4));    // shuffling
        Assert.False(RouteDeviationSpec.IsDwell(1.4));   // walking pace
    }

    // Without re-baselining, a stale 300 m closest approach would flag the 950 m
    // post-transfer leg. After a dwell re-baselines the anchor to the 700 m terminal,
    // that same leg is within the buffer → no false positive.
    [Fact]
    public void DwellRebaseline_PreventsFalsePositiveAfterTransfer()
    {
        double staleClosest = 300;
        Assert.True(RouteDeviationSpec.IsMovingAway(distance: 950, closestApproach: staleClosest, accuracy: 0));

        double rebaselined = 700; // anchor reset to current distance on dwell
        Assert.False(RouteDeviationSpec.IsMovingAway(distance: 950, closestApproach: rebaselined, accuracy: 0));
    }
}
