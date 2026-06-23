namespace AlarmaApp.Services;

// Crowded-place / "urban canyon" GPS robustness (Feature 4). In a dense city core — tall buildings,
// multipath, a packed jeepney terminal — fixes arrive with poor reported accuracy and scatter several
// tens of metres around the rider's true position. Left unfiltered, a cluster of those noisy fixes can
// fake an arrival or an overshoot and trip a false Stage-3 lockout. Three cheap, on-device defences:
//
//   1. Confidence gate — a fix whose accuracy radius is worse than the threshold is "low confidence".
//      The controller still shows it, but refuses to LATCH arrival / overshoot / the Stage-3 wake-up
//      on it. Biasing toward "wait for a confident fix" beats a wrong trigger (the spec's rule).
//   2. Accuracy-weighted smoothing — blend each new fix toward the running estimate, trusting the
//      tighter (smaller-accuracy) of the two more. A scattered low-confidence cluster gets pulled
//      toward its weighted mean instead of yanking the position around.
//   3. Dynamic jitter gate — the minimum segment a fix must move to count toward distance widens with
//      the fix's own accuracy, so a worse fix has to move further before it adds to the trip total.
//      This is what stops jitter from inflating the Haversine path integral.
//
// Pure C# (no Android / MAUI) so CrowdedGpsRobustnessTests can mirror it; keep the two in lock-step.
public static class CrowdedGpsFilter
{
    // Above this reported accuracy radius (metres) a fix is "low confidence" and can't latch a stage.
    // Sits between the typical open-sky fix (a few metres) and the urban-canyon noise floor (50 m+),
    // and below the Release tracking-service accuracy gate (50 m) so genuine fixes still latch.
    public const double DefaultConfidenceThresholdMeters = 35.0;

    // Floor for the per-segment jitter gate — matches HomeController.MinSegmentMeters.
    public const double MinJitterGateMeters = 8.0;

    /// <summary>
    /// A fix is only USABLE if its coordinates are real, finite numbers inside the globe. A degraded or
    /// briefly-absent provider can hand us a NaN/Infinity or an out-of-range lat/lon; if one of those ever
    /// reached the running estimate it would poison every later distance (NaN propagates forever) and leave
    /// the map stuck with no live dot. So the consumer drops an unusable fix and keeps the last known
    /// position instead — graceful degradation, never a crash or a frozen "no location" state.
    /// </summary>
    public static bool IsUsableFix(double latitude, double longitude)
        => double.IsFinite(latitude) && double.IsFinite(longitude)
           && latitude is >= -90 and <= 90
           && longitude is >= -180 and <= 180;

    /// <summary>
    /// True when a fix is trustworthy enough to latch a stage. An accuracy of 0 means the provider
    /// reported none, which the rest of the app already trusts, so it counts as confident. A non-finite
    /// accuracy is never trusted (it can't satisfy the threshold), so it can't latch a stage.
    /// </summary>
    public static bool IsConfident(double accuracyMeters, double thresholdMeters = DefaultConfidenceThresholdMeters)
        => double.IsFinite(accuracyMeters) && (accuracyMeters <= 0 || accuracyMeters <= thresholdMeters);

    /// <summary>
    /// Minimum movement (metres) a segment must clear to count toward accumulated distance: the larger
    /// of the floor and the fix's own accuracy, so a worse fix must move further before it's believed. A
    /// missing/non-finite accuracy falls back to the floor so the gate can never become NaN (which would
    /// reject every segment and silently freeze the trip distance).
    /// </summary>
    public static double JitterGateMeters(double accuracyMeters, double minGateMeters = MinJitterGateMeters)
        => System.Math.Max(minGateMeters, double.IsFinite(accuracyMeters) && accuracyMeters > 0 ? accuracyMeters : 0);

    /// <summary>
    /// Accuracy-weighted blend of a new fix toward the running estimate. The estimate's weight is
    /// proportional to the new fix's accuracy and vice-versa, so the tighter fix dominates; the blended
    /// accuracy shrinks the way two independent estimates combine. With no prior estimate (estAcc &lt;= 0)
    /// the new fix is taken as-is.
    /// </summary>
    public static (double Lat, double Lon, double Acc) Smooth(
        double estLat, double estLon, double estAcc,
        double newLat, double newLon, double newAcc)
    {
        // Defensive: a non-finite incoming fix must never reach the estimate, or it poisons every later
        // reading. Keep the prior estimate unchanged and let the next good fix resume the blend.
        if (!double.IsFinite(newLat) || !double.IsFinite(newLon))
            return (estLat, estLon, estAcc);
        if (!double.IsFinite(newAcc))
            newAcc = 0;

        // No usable prior estimate yet — adopt the incoming fix verbatim.
        if (estAcc <= 0)
            return (newLat, newLon, newAcc <= 0 ? 0 : newAcc);

        // A fix with no accuracy estimate is treated as perfectly trusted (weight all to it), matching
        // how the tracking layer already trusts accuracy==0 readings.
        if (newAcc <= 0)
            return (newLat, newLon, 0);

        // weightNew large when the new fix is tighter than the estimate; small when it's noisier.
        var weightNew = estAcc / (estAcc + newAcc);
        var lat = estLat + (newLat - estLat) * weightNew;
        var lon = estLon + (newLon - estLon) * weightNew;
        // Combined accuracy of two independent estimates: 1/acc = 1/estAcc + 1/newAcc.
        var acc = (estAcc * newAcc) / (estAcc + newAcc);
        return (lat, lon, acc);
    }
}
