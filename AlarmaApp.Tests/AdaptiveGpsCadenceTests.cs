// =============================================================================
//  AdaptiveGpsCadenceTests.cs
// -----------------------------------------------------------------------------
//  Tests for the battery-saving GPS cadence policy: poll slowly when the stop is
//  far off, snap back to the 2 s baseline (and hold the wake lock) on approach —
//  WITHOUT changing the cadence near the stop, so the adaptive-lead/stage math is
//  untouched.
//
//  Self-contained per project convention (see AdaptiveAlarmTests.cs): the real
//  AlarmaApp.Services.AdaptiveGpsCadence lives in the net9.0-android app project,
//  so its constants and rules are mirrored here. Keep them in lock-step. The lead
//  distance is computed exactly as HomeController does (speed × lead-min × 60,
//  clamped to [300, 5000]).
// =============================================================================

using Xunit;

namespace AlarmaApp.Tests;

internal readonly record struct GpsCadencePlanSpec(long IntervalMillis, bool HoldWakeLock);

/// <summary>Mirror of AlarmaApp.Services.AdaptiveGpsCadence.</summary>
internal static class AdaptiveGpsCadenceSpec
{
    public const long BaselineIntervalMillis = 2000;
    public const long MediumIntervalMillis = 5000;
    public const long FarIntervalMillis = 10000;
    public const double ApproachLeadMultiple = 1.0;
    public const double MediumLeadMultiple = 3.0;
    public const double ApproachFloorMeters = 800;
    public const double MediumFloorMeters = 2500;

    public static GpsCadencePlanSpec Select(double distanceToStopMeters, double adaptiveLeadDistanceMeters)
    {
        if (double.IsNaN(distanceToStopMeters) || distanceToStopMeters < 0)
            return new GpsCadencePlanSpec(BaselineIntervalMillis, true);

        var lead = double.IsNaN(adaptiveLeadDistanceMeters) || adaptiveLeadDistanceMeters < 0
            ? 0 : adaptiveLeadDistanceMeters;
        var approach = System.Math.Max(ApproachFloorMeters, lead * ApproachLeadMultiple);
        var medium = System.Math.Max(MediumFloorMeters, lead * MediumLeadMultiple);

        if (distanceToStopMeters <= approach)
            return new GpsCadencePlanSpec(BaselineIntervalMillis, true);
        if (distanceToStopMeters <= medium)
            return new GpsCadencePlanSpec(MediumIntervalMillis, false);
        return new GpsCadencePlanSpec(FarIntervalMillis, false);
    }
}

// Mirror of HomeController's adaptive lead distance.
internal static class LeadDistanceSpec
{
    private const double MinAlarmDistanceMeters = 300;
    private const double MaxAlarmDistanceMeters = 5000;
    private const double DefaultAlarmLeadMinutes = 5;

    public static double For(double speedMetersPerSecond) =>
        System.Math.Clamp(speedMetersPerSecond * DefaultAlarmLeadMinutes * 60,
            MinAlarmDistanceMeters, MaxAlarmDistanceMeters);
}

public class AdaptiveGpsCadenceTests
{
    [Fact]
    public void FarFromStop_PollsSlowly_AndReleasesWakeLock()
    {
        var plan = AdaptiveGpsCadenceSpec.Select(distanceToStopMeters: 40_000, adaptiveLeadDistanceMeters: 3000);

        Assert.Equal(AdaptiveGpsCadenceSpec.FarIntervalMillis, plan.IntervalMillis);
        Assert.False(plan.HoldWakeLock);
    }

    [Fact]
    public void MediumRange_PollsMediumCadence_AndReleasesWakeLock()
    {
        // lead 3000 → approach band 3000, medium band 9000. 6 km sits in the medium band.
        var plan = AdaptiveGpsCadenceSpec.Select(distanceToStopMeters: 6_000, adaptiveLeadDistanceMeters: 3000);

        Assert.Equal(AdaptiveGpsCadenceSpec.MediumIntervalMillis, plan.IntervalMillis);
        Assert.False(plan.HoldWakeLock);
    }

    [Fact]
    public void OnApproach_SnapsBackToBaseline_AndHoldsWakeLock()
    {
        var plan = AdaptiveGpsCadenceSpec.Select(distanceToStopMeters: 400, adaptiveLeadDistanceMeters: 3000);

        Assert.Equal(AdaptiveGpsCadenceSpec.BaselineIntervalMillis, plan.IntervalMillis);
        Assert.True(plan.HoldWakeLock);
    }

    [Fact]
    public void UnknownDistance_UsesSafeBaselineAndHolds()
    {
        Assert.True(AdaptiveGpsCadenceSpec.Select(double.NaN, 3000).HoldWakeLock);
        Assert.Equal(AdaptiveGpsCadenceSpec.BaselineIntervalMillis,
            AdaptiveGpsCadenceSpec.Select(-1, 3000).IntervalMillis);
    }

    // The critical guarantee: at EVERY stage boundary the cadence is the original 2 s baseline, so the
    // worked-example stage timing is unaffected by the adaptive far-away polling.
    [Theory]
    [InlineData(5.0)]   // ~18 km/h jeepney, lead 1500
    [InlineData(10.0)]  // ~36 km/h, lead 3000
    [InlineData(25.0)]  // ~90 km/h highway bus, lead clamped to 5000
    [InlineData(0.5)]   // crawling traffic, lead clamps up to the 300 m floor
    public void AllStageBoundaries_PollAtBaseline(double speed)
    {
        var lead = LeadDistanceSpec.For(speed);
        double arrival = 200;
        double stage3 = lead * (1.0 / 3.0);
        double stage2 = lead * (2.0 / 3.0);
        double stage1 = lead; // Stage 1 arms at the full lead radius

        foreach (var d in new[] { arrival, stage3, stage2, stage1 })
        {
            var plan = AdaptiveGpsCadenceSpec.Select(d, lead);
            Assert.Equal(AdaptiveGpsCadenceSpec.BaselineIntervalMillis, plan.IntervalMillis);
            Assert.True(plan.HoldWakeLock);
        }
    }

    // A slow/stationary vehicle (tiny lead) must still get the fast cadence well outside the arrival ring,
    // thanks to the absolute approach floor.
    [Fact]
    public void SlowVehicle_StillFastWithinApproachFloor()
    {
        var lead = LeadDistanceSpec.For(0.1); // clamps to 300 m lead
        var plan = AdaptiveGpsCadenceSpec.Select(distanceToStopMeters: 700, adaptiveLeadDistanceMeters: lead);

        Assert.Equal(AdaptiveGpsCadenceSpec.BaselineIntervalMillis, plan.IntervalMillis);
        Assert.True(plan.HoldWakeLock);
    }

    // Interval must never increase as the rider gets closer — cadence only tightens on approach.
    [Fact]
    public void Interval_IsMonotonicNonIncreasing_AsDistanceShrinks()
    {
        const double lead = 3000;
        long previous = long.MaxValue;
        for (double d = 50_000; d >= 100; d -= 500)
        {
            var interval = AdaptiveGpsCadenceSpec.Select(d, lead).IntervalMillis;
            Assert.True(interval <= previous,
                $"cadence got slower while approaching at {d} m ({interval} > {previous})");
            previous = interval;
        }
    }

    // Near the stop the cadence is never slower than the baseline the alarm math was tuned against.
    [Fact]
    public void NearStop_NeverSlowerThanBaseline()
    {
        const double lead = 3000;
        for (double d = 0; d <= lead; d += 100)
            Assert.True(AdaptiveGpsCadenceSpec.Select(d, lead).IntervalMillis
                        <= AdaptiveGpsCadenceSpec.BaselineIntervalMillis);
    }
}
