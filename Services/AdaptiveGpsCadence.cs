namespace AlarmaApp.Services;

// The cadence + wake-lock decision for one fix. Pulled out as pure data so the policy can be unit-tested
// off-device while the Android service stays a thin applier.
public readonly record struct GpsCadencePlan(long IntervalMillis, bool HoldWakeLock);

// Picks how often to poll GPS based on how close the rider is to the stop, relative to the speed-scaled
// alarm lead distance. The whole point: a budget phone shouldn't burn battery polling every 2 s for a
// two-hour highway leg when the stop is 40 km away — but the moment the rider is within alarm range we
// snap back to the original 2 s cadence (and hold the wake lock) so the stage timing and the adaptive-lead
// math behave EXACTLY as they did before. Nothing about the near-stop behaviour changes; we only relax the
// far-away polling.
public static class AdaptiveGpsCadence
{
    // The original fixed cadence. We never poll slower than this once inside the approach band, so the
    // worked-example stage boundaries (which were tuned against 2 s fixes) are untouched near the stop.
    public const long BaselineIntervalMillis = 2000;
    public const long MediumIntervalMillis = 5000;
    public const long FarIntervalMillis = 10000;

    // Approach band = the alarm lead distance itself (where Stage 1 arms), so every stage fires at the
    // baseline cadence. Medium band stretches out to a few lead-distances for a gentle ramp.
    public const double ApproachLeadMultiple = 1.0;
    public const double MediumLeadMultiple = 3.0;

    // Absolute floors so a slow/stationary vehicle (tiny lead distance) can't shrink the fast-cadence zone
    // to nothing — we must never under-poll near the stop just because the rolling speed is low.
    public const double ApproachFloorMeters = 800;
    public const double MediumFloorMeters = 2500;

    public static GpsCadencePlan Select(double distanceToStopMeters, double adaptiveLeadDistanceMeters)
    {
        // Unknown distance (no destination yet, or a not-yet-acquired fix): stay on the safe fast default
        // with the wake lock held. Better a little extra battery than a missed fix right after start.
        if (double.IsNaN(distanceToStopMeters) || distanceToStopMeters < 0)
            return new GpsCadencePlan(BaselineIntervalMillis, true);

        var lead = double.IsNaN(adaptiveLeadDistanceMeters) || adaptiveLeadDistanceMeters < 0
            ? 0
            : adaptiveLeadDistanceMeters;

        var approachBand = Math.Max(ApproachFloorMeters, lead * ApproachLeadMultiple);
        var mediumBand = Math.Max(MediumFloorMeters, lead * MediumLeadMultiple);

        if (distanceToStopMeters <= approachBand)
            return new GpsCadencePlan(BaselineIntervalMillis, true);

        if (distanceToStopMeters <= mediumBand)
            return new GpsCadencePlan(MediumIntervalMillis, false);

        return new GpsCadencePlan(FarIntervalMillis, false);
    }
}
