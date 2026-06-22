namespace AlarmaApp.Services;

// A bounded last-resort latch for the Emergency wake-up in a dense terminal / urban canyon. The confidence
// gate (CrowdedGpsFilter.IsConfident) correctly refuses to latch arrival on a low-confidence fix — but in a
// packed terminal EVERY final-approach fix can stay low-confidence, so the normal arrival latch would never
// fire and the rider could sail past their stop with no Emergency alarm. This gate fills that gap WITHOUT
// weakening the no-false-arrival guarantee: it only fires once the SMOOTHED best estimate has sat inside the
// arrival ring continuously — across several fixes AND for a dwell window — while only low-confidence fixes
// arrive. The instant the estimate leaves the ring, the dwell resets, so it can never latch away from the
// destination. When it does fire, the caller flags the arrival as "approximate".
//
// Pure C# (no Android / MAUI) so it can be unit-tested off-device; keep the mirror in lock-step.
public sealed class LowConfidenceArrivalGate
{
    // Conservative on purpose: a full half-minute of being inside the ring, across several fixes, before we
    // resort to an approximate latch. Better the rider waits a bit longer than fire at the wrong place.
    public const double DefaultDwellSeconds = 30.0;
    public const int DefaultMinFixes = 3;

    private readonly double _arrivalRingMeters;
    private readonly double _dwellSeconds;
    private readonly int _minFixes;

    private DateTime? _since;
    private int _fixesInsideRing;

    public LowConfidenceArrivalGate(
        double arrivalRingMeters,
        double dwellSeconds = DefaultDwellSeconds,
        int minFixes = DefaultMinFixes)
    {
        _arrivalRingMeters = arrivalRingMeters;
        _dwellSeconds = dwellSeconds;
        _minFixes = minFixes;
    }

    public void Reset()
    {
        _since = null;
        _fixesInsideRing = 0;
    }

    // Call on every fix while arrival hasn't latched yet. Returns true exactly when the bounded approximate
    // latch should fire. Confident fixes are deliberately ignored here — they take the normal arrival path —
    // but they don't reset the dwell, so a mostly-low-confidence cluster still accumulates.
    public bool ShouldLatchApproximate(double smoothedDistanceToStopMeters, bool isConfident, DateTime fixTimeUtc)
    {
        // Best estimate is outside the ring: this is the hard guard against latching away from the stop.
        if (!(smoothedDistanceToStopMeters <= _arrivalRingMeters))
        {
            Reset();
            return false;
        }

        // A confident fix inside the ring is the normal latch's job, not ours.
        if (isConfident)
            return false;

        _since ??= fixTimeUtc;
        _fixesInsideRing++;
        var dwell = (fixTimeUtc - _since.Value).TotalSeconds;
        return _fixesInsideRing >= _minFixes && dwell >= _dwellSeconds;
    }
}
