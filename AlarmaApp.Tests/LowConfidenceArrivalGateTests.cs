// =============================================================================
//  LowConfidenceArrivalGateTests.cs
// -----------------------------------------------------------------------------
//  Tests for the bounded last-resort Emergency latch used when a dense terminal /
//  urban canyon keeps every final-approach fix low-confidence, so the normal
//  arrival latch would never fire. It must eventually latch (flagged approximate)
//  for a sustained low-confidence cluster INSIDE the arrival ring, yet NEVER latch
//  away from the destination.
//
//  Self-contained per project convention (see CrowdedGpsRobustnessTests.cs): the
//  real AlarmaApp.Services.LowConfidenceArrivalGate lives in the net9.0-android
//  app project, so its state machine is mirrored here. Keep them in lock-step.
// =============================================================================

using Xunit;

namespace AlarmaApp.Tests;

/// <summary>Mirror of AlarmaApp.Services.LowConfidenceArrivalGate.</summary>
internal sealed class LowConfidenceArrivalGateSpec
{
    public const double DefaultDwellSeconds = 30.0;
    public const int DefaultMinFixes = 3;

    private readonly double _arrivalRingMeters;
    private readonly double _dwellSeconds;
    private readonly int _minFixes;
    private DateTime? _since;
    private int _fixesInsideRing;

    public LowConfidenceArrivalGateSpec(double arrivalRingMeters,
        double dwellSeconds = DefaultDwellSeconds, int minFixes = DefaultMinFixes)
    {
        _arrivalRingMeters = arrivalRingMeters;
        _dwellSeconds = dwellSeconds;
        _minFixes = minFixes;
    }

    public void Reset() { _since = null; _fixesInsideRing = 0; }

    public bool ShouldLatchApproximate(double smoothedDistanceToStopMeters, bool isConfident, DateTime fixTimeUtc)
    {
        if (!(smoothedDistanceToStopMeters <= _arrivalRingMeters)) { Reset(); return false; }
        if (isConfident) return false;
        _since ??= fixTimeUtc;
        _fixesInsideRing++;
        var dwell = (fixTimeUtc - _since.Value).TotalSeconds;
        return _fixesInsideRing >= _minFixes && dwell >= _dwellSeconds;
    }
}

public class LowConfidenceArrivalGateTests
{
    private const double ArrivalRing = 200;
    private static readonly DateTime T0 = new(2026, 6, 23, 22, 0, 0, DateTimeKind.Utc);

    // A sustained low-confidence cluster INSIDE the ring eventually latches (approximate).
    [Fact]
    public void SustainedLowConfidenceInsideRing_EventuallyLatches()
    {
        var gate = new LowConfidenceArrivalGateSpec(ArrivalRing);
        bool latched = false;

        // A fix every 5 s, all low-confidence, all ~120 m from the stop (inside the 200 m ring).
        for (var t = 0; t <= 35 && !latched; t += 5)
            latched = gate.ShouldLatchApproximate(120, isConfident: false, T0.AddSeconds(t));

        Assert.True(latched);
    }

    // A low-confidence cluster OUTSIDE the ring must NEVER latch, however long it persists.
    [Fact]
    public void LowConfidenceOutsideRing_NeverLatches()
    {
        var gate = new LowConfidenceArrivalGateSpec(ArrivalRing);

        for (var t = 0; t <= 300; t += 5)
            Assert.False(gate.ShouldLatchApproximate(450, isConfident: false, T0.AddSeconds(t)));
    }

    // A confident fix is handled by the normal arrival latch, not this fallback — the gate stays quiet.
    [Fact]
    public void ConfidentFix_DoesNotLatchHere()
    {
        var gate = new LowConfidenceArrivalGateSpec(ArrivalRing);

        // Even well past the dwell window, a confident fix never triggers the approximate path.
        for (var t = 0; t <= 60; t += 5)
            Assert.False(gate.ShouldLatchApproximate(120, isConfident: true, T0.AddSeconds(t)));
    }

    // Dwell window not yet elapsed → no latch even with enough fixes.
    [Fact]
    public void BeforeDwellElapses_DoesNotLatch()
    {
        var gate = new LowConfidenceArrivalGateSpec(ArrivalRing);

        // 5 fixes packed into 10 s — count is met but the 30 s dwell isn't.
        for (var t = 0; t <= 10; t += 2)
            Assert.False(gate.ShouldLatchApproximate(120, isConfident: false, T0.AddSeconds(t)));
    }

    // Too few fixes (sparse) → no latch even if wall-clock dwell has passed.
    [Fact]
    public void TooFewFixes_DoesNotLatch()
    {
        var gate = new LowConfidenceArrivalGateSpec(ArrivalRing);

        Assert.False(gate.ShouldLatchApproximate(120, false, T0));               // fix 1 at t=0
        Assert.False(gate.ShouldLatchApproximate(120, false, T0.AddSeconds(60))); // fix 2 at t=60 (only 2 fixes)
    }

    // Brief exit from the ring resets the dwell — the cluster must be CONTINUOUSLY inside.
    [Fact]
    public void ExitingRing_ResetsDwell()
    {
        var gate = new LowConfidenceArrivalGateSpec(ArrivalRing);

        // Build up 25 s inside the ring...
        for (var t = 0; t <= 25; t += 5)
            Assert.False(gate.ShouldLatchApproximate(120, false, T0.AddSeconds(t)));

        // ...then one fix outside the ring resets everything...
        Assert.False(gate.ShouldLatchApproximate(450, false, T0.AddSeconds(30)));

        // ...so a single fix back inside is nowhere near the dwell again.
        Assert.False(gate.ShouldLatchApproximate(120, false, T0.AddSeconds(31)));
    }

    // The ring boundary itself counts as inside.
    [Fact]
    public void ExactlyAtRingBoundary_CountsAsInside()
    {
        var gate = new LowConfidenceArrivalGateSpec(ArrivalRing);
        bool latched = false;
        for (var t = 0; t <= 35 && !latched; t += 5)
            latched = gate.ShouldLatchApproximate(ArrivalRing, isConfident: false, T0.AddSeconds(t));
        Assert.True(latched);
    }
}
