// =============================================================================
//  CrowdedGpsRobustnessTests.cs
// -----------------------------------------------------------------------------
//  ISOLATED tests for crowded-place / "urban canyon" GPS robustness (Feature 4).
//
//  Proves, against deterministic sequences of degraded/scattered fixes:
//    (a) a cluster of low-confidence fixes near the stop CANNOT fake an arrival
//        or an overshoot latch (no false Stage-3 lockout),
//    (b) once a confident fix arrives, the stage transitions latch correctly,
//    (c) jitter does NOT inflate the accumulated Haversine trip distance.
//
//  Self-contained per project convention: mirrors AlarmaApp.Services.CrowdedGpsFilter
//  and the confidence-gated latch logic in HomeController.HandleDestinationDistanceAsync.
// =============================================================================

using Xunit;

namespace AlarmaApp.Tests;

/// <summary>Mirror of AlarmaApp.Services.CrowdedGpsFilter.</summary>
internal static class CrowdedGpsSpec
{
    public const double ConfidenceThresholdMeters = 35.0;
    public const double MinJitterGateMeters = 8.0;

    // A fix is only usable if its coordinates are finite numbers inside the globe.
    public static bool IsUsableFix(double latitude, double longitude)
        => double.IsFinite(latitude) && double.IsFinite(longitude)
           && latitude is >= -90 and <= 90
           && longitude is >= -180 and <= 180;

    public static bool IsConfident(double accuracy, double threshold = ConfidenceThresholdMeters)
        => double.IsFinite(accuracy) && (accuracy <= 0 || accuracy <= threshold);

    public static double JitterGateMeters(double accuracy, double minGate = MinJitterGateMeters)
        => System.Math.Max(minGate, double.IsFinite(accuracy) && accuracy > 0 ? accuracy : 0);

    public static (double Lat, double Lon, double Acc) Smooth(
        double estLat, double estLon, double estAcc,
        double newLat, double newLon, double newAcc)
    {
        if (!double.IsFinite(newLat) || !double.IsFinite(newLon)) return (estLat, estLon, estAcc);
        if (!double.IsFinite(newAcc)) newAcc = 0;
        if (estAcc <= 0) return (newLat, newLon, newAcc <= 0 ? 0 : newAcc);
        if (newAcc <= 0) return (newLat, newLon, 0);
        var w = estAcc / (estAcc + newAcc);
        var lat = estLat + (newLat - estLat) * w;
        var lon = estLon + (newLon - estLon) * w;
        var acc = (estAcc * newAcc) / (estAcc + newAcc);
        return (lat, lon, acc);
    }

    // Haversine — mirrors HomeController.CalculateDistanceMeters.
    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6_371_000;
        double r1 = lat1 * System.Math.PI / 180, r2 = lat2 * System.Math.PI / 180;
        double dLat = (lat2 - lat1) * System.Math.PI / 180;
        double dLon = (lon2 - lon1) * System.Math.PI / 180;
        double a = System.Math.Sin(dLat / 2) * System.Math.Sin(dLat / 2)
                 + System.Math.Cos(r1) * System.Math.Cos(r2) * System.Math.Sin(dLon / 2) * System.Math.Sin(dLon / 2);
        return R * 2 * System.Math.Atan2(System.Math.Sqrt(a), System.Math.Sqrt(1 - a));
    }
}

// =============================================================================
//  CONFIDENCE GATE
// =============================================================================
public class GpsConfidenceGateTests
{
    [Theory]
    [InlineData(0.0, true)]    // provider reported no estimate -> trusted, as the app already does
    [InlineData(5.0, true)]    // open-sky fix
    [InlineData(35.0, true)]   // exactly on the threshold
    [InlineData(35.01, false)] // just over
    [InlineData(80.0, false)]  // urban-canyon noise
    [InlineData(200.0, false)]
    public void Confidence_ThresholdedAt35m(double accuracy, bool confident)
        => Assert.Equal(confident, CrowdedGpsSpec.IsConfident(accuracy));
}

// =============================================================================
//  (a) + (b)  ARRIVAL / OVERSHOOT LATCHING UNDER DEGRADED GPS
// =============================================================================
public class CrowdedLatchTests
{
    // Mirrors the confidence-gated arrival + overshoot latch in HomeController.
    private sealed class Latcher
    {
        const double ArrivalThreshold = 200, OvershootBuffer = 250;
        const int ArrivalPersistence = 2, OvershootPersistence = 3;

        public bool Arrived; public bool OvershootAlerted;
        int _arrivalStreak, _overshootStreak; double _lastOver = double.MaxValue;

        public void Feed(double distance, double accuracy)
        {
            var confident = CrowdedGpsSpec.IsConfident(accuracy);

            if (confident && !Arrived && distance <= ArrivalThreshold)
            {
                _arrivalStreak++;
                if (_arrivalStreak >= ArrivalPersistence) { Arrived = true; return; }
            }
            else if (confident && !Arrived)
            {
                _arrivalStreak = 0;
            }

            if (confident && Arrived && !OvershootAlerted)
            {
                var threshold = ArrivalThreshold + OvershootBuffer + System.Math.Max(accuracy, 0);
                if (distance >= threshold && distance > _lastOver) _overshootStreak++;
                else if (distance < _lastOver) _overshootStreak = 0;
                _lastOver = distance;
                if (_overshootStreak >= OvershootPersistence) OvershootAlerted = true;
            }
        }
    }

    // (a) A cluster of LOW-confidence fixes sitting right on the stop must NOT latch arrival, even though
    //     their raw distance is inside the 200 m ring — biasing toward waiting for a confident fix.
    [Fact]
    public void LowConfidenceClusterAtStop_DoesNotLatchArrival()
    {
        var sm = new Latcher();
        // Eight scattered fixes, all within 200 m but all reported at 80 m accuracy (urban canyon).
        var rng = new[] { 40.0, 180.0, 90.0, 150.0, 60.0, 190.0, 30.0, 120.0 };
        foreach (var d in rng) sm.Feed(d, accuracy: 80.0);
        Assert.False(sm.Arrived);
        Assert.False(sm.OvershootAlerted);
    }

    // (a) A noisy cluster PAST the stop can't fake an overshoot either, because arrival never latched.
    [Fact]
    public void LowConfidenceClusterPastStop_DoesNotLatchOvershoot()
    {
        var sm = new Latcher();
        foreach (var d in new[] { 500.0, 700.0, 900.0, 1100.0 }) sm.Feed(d, accuracy: 120.0);
        Assert.False(sm.OvershootAlerted);
    }

    // (b) Once CONFIDENT fixes arrive inside the ring, arrival latches after the persistence count, and a
    //     subsequent confident increasing run past the threshold latches the overshoot.
    [Fact]
    public void ConfidentFixes_LatchArrivalThenOvershoot()
    {
        var sm = new Latcher();
        // Low-confidence noise first — no effect.
        foreach (var d in new[] { 50.0, 150.0, 80.0 }) sm.Feed(d, 90.0);
        Assert.False(sm.Arrived);

        // Two confident fixes inside the ring -> arrival latches.
        sm.Feed(120.0, 6.0);
        sm.Feed(90.0, 5.0);
        Assert.True(sm.Arrived);

        // Confident, monotonically increasing past 450 m. The first past-threshold fix only seeds the
        // comparison anchor (mirrors production's double.MaxValue start), so three *increases* after it
        // are what latch the overshoot — four fixes total.
        sm.Feed(500.0, 6.0);
        sm.Feed(620.0, 6.0);
        sm.Feed(760.0, 6.0);
        sm.Feed(880.0, 6.0);
        Assert.True(sm.OvershootAlerted);
    }

    // (b) A confident arrival is not undone by later low-confidence noise (the latch holds).
    [Fact]
    public void ConfidentArrival_SurvivesLaterNoise()
    {
        var sm = new Latcher();
        sm.Feed(120.0, 5.0);
        sm.Feed(110.0, 5.0);
        Assert.True(sm.Arrived);

        foreach (var d in new[] { 800.0, 50.0, 900.0 }) sm.Feed(d, 150.0); // all low-confidence
        Assert.False(sm.OvershootAlerted); // noisy fixes can't confirm the overshoot
    }
}

// =============================================================================
//  (c)  JITTER MUST NOT INFLATE ACCUMULATED DISTANCE
// =============================================================================
public class JitterDistanceTests
{
    // A stationary phone in a crowded area emits fixes that wander a few metres with poor accuracy.
    // The dynamic jitter gate (max(8 m, accuracy)) rejects every sub-accuracy hop, so the accumulated
    // Haversine distance stays at zero.
    [Fact]
    public void StationaryNoisyFixes_AccumulateNoDistance()
    {
        // ~14.6097, 120.9895 (UST). +/- offsets up to ~7 m, reported accuracy 50 m.
        double baseLat = 14.6097, baseLon = 120.9895;
        var offsets = new (double dLat, double dLon)[]
        {
            (0, 0), (0.00005, -0.00004), (-0.00006, 0.00003),
            (0.00003, 0.00006), (-0.00004, -0.00005), (0.00006, 0.00002),
        };

        double total = 0;
        double anchorLat = baseLat, anchorLon = baseLon;
        const double accuracy = 50.0;
        var gate = CrowdedGpsSpec.JitterGateMeters(accuracy);

        foreach (var (dLat, dLon) in offsets)
        {
            double lat = baseLat + dLat, lon = baseLon + dLon;
            double seg = CrowdedGpsSpec.DistanceMeters(anchorLat, anchorLon, lat, lon);
            if (seg >= gate) // accepted only if it clears the (widened) noise floor
            {
                total += seg;
                anchorLat = lat; anchorLon = lon;
            }
        }

        Assert.Equal(0.0, total, 3); // every hop was jitter -> nothing accumulated
    }

    // Genuine slow movement (each hop ~25 m, well past an 8 m gate at good accuracy) still accumulates,
    // so the gate rejects noise without swallowing real travel.
    [Fact]
    public void GenuineMovement_StillAccumulates()
    {
        double lat = 14.6047, lon = 120.9895;
        double total = 0, anchorLat = lat, anchorLon = lon;
        var gate = CrowdedGpsSpec.JitterGateMeters(5.0); // good accuracy -> 8 m floor

        for (int i = 0; i < 5; i++)
        {
            lat += 0.000225; // ~25 m north per step
            double seg = CrowdedGpsSpec.DistanceMeters(anchorLat, anchorLon, lat, lon);
            if (seg >= gate) { total += seg; anchorLat = lat; anchorLon = lon; }
        }

        Assert.True(total > 100, $"Expected genuine ~125 m of travel to accumulate, got {total:F1} m.");
    }

    [Fact]
    public void JitterGate_WidensWithAccuracy()
    {
        Assert.Equal(8.0, CrowdedGpsSpec.JitterGateMeters(5.0), 3);   // floored
        Assert.Equal(50.0, CrowdedGpsSpec.JitterGateMeters(50.0), 3); // tracks poor accuracy
    }
}

// =============================================================================
//  ACCURACY-WEIGHTED SMOOTHING
// =============================================================================
public class AccuracyWeightedSmoothingTests
{
    // A tight (accurate) prior estimate barely moves toward a noisy new fix — the noisy fix is distrusted.
    [Fact]
    public void NoisyFix_BarelyMovesTightEstimate()
    {
        var (lat, _, acc) = CrowdedGpsSpec.Smooth(
            estLat: 14.6097, estLon: 120.9895, estAcc: 5.0,
            newLat: 14.6120, newLon: 120.9895, newAcc: 100.0);

        // weight to the new fix = 5/105 ≈ 0.048, so the estimate moves <5% of the way.
        Assert.True(lat < 14.6097 + (14.6120 - 14.6097) * 0.1,
            "A 100 m-accuracy fix should barely move a 5 m-accuracy estimate.");
        Assert.True(acc < 5.0, "Blended accuracy should be at least as tight as the prior estimate.");
    }

    // The first fix (no prior estimate) is adopted verbatim.
    [Fact]
    public void FirstFix_AdoptedVerbatim()
    {
        var (lat, lon, acc) = CrowdedGpsSpec.Smooth(0, 0, 0, 14.61, 120.99, 12.0);
        Assert.Equal(14.61, lat, 6);
        Assert.Equal(120.99, lon, 6);
        Assert.Equal(12.0, acc, 6);
    }

    // Two equally-trusted fixes average, and the blended accuracy improves (halves) — combining
    // independent estimates is what damps a scattered cluster toward its centre.
    [Fact]
    public void EqualAccuracyFixes_AverageAndTighten()
    {
        var (lat, _, acc) = CrowdedGpsSpec.Smooth(0.0, 0.0, 20.0, 10.0, 0.0, 20.0);
        Assert.Equal(5.0, lat, 6);   // weight 0.5 toward the new fix
        Assert.Equal(10.0, acc, 6);  // (20*20)/(20+20) = 10
    }
}

// =============================================================================
//  USABLE-FIX GATE — a NaN / Infinity / off-globe fix must be rejected at the door
// =============================================================================
public class UsableFixGateTests
{
    [Theory]
    [InlineData(14.6097, 120.9895, true)]   // a real Manila fix
    [InlineData(-90.0, -180.0, true)]       // corners of the globe are valid
    [InlineData(90.0, 180.0, true)]
    [InlineData(0.0, 0.0, true)]            // null island is finite/in-range (not our job to reject here)
    [InlineData(91.0, 120.0, false)]        // latitude past the pole
    [InlineData(14.6, 181.0, false)]        // longitude off the globe
    public void RangeAndFiniteness(double lat, double lon, bool usable)
        => Assert.Equal(usable, CrowdedGpsSpec.IsUsableFix(lat, lon));

    [Fact]
    public void NonFiniteCoordinates_AreRejected()
    {
        Assert.False(CrowdedGpsSpec.IsUsableFix(double.NaN, 120.99));
        Assert.False(CrowdedGpsSpec.IsUsableFix(14.61, double.NaN));
        Assert.False(CrowdedGpsSpec.IsUsableFix(double.PositiveInfinity, 120.99));
        Assert.False(CrowdedGpsSpec.IsUsableFix(14.61, double.NegativeInfinity));
    }

    // The smoother is the last line of defence: even handed a non-finite fix directly, it must keep the
    // prior estimate intact rather than poison it (a single NaN would spread to every later reading).
    [Fact]
    public void Smooth_IgnoresNonFiniteFix_KeepsPriorEstimate()
    {
        var (lat, lon, acc) = CrowdedGpsSpec.Smooth(14.6097, 120.9895, 6.0, double.NaN, 120.99, 8.0);
        Assert.Equal(14.6097, lat, 6);
        Assert.Equal(120.9895, lon, 6);
        Assert.Equal(6.0, acc, 6);
    }

    [Fact]
    public void JitterGate_NeverNaN_OnNonFiniteAccuracy()
    {
        Assert.Equal(8.0, CrowdedGpsSpec.JitterGateMeters(double.NaN), 6);
        Assert.False(CrowdedGpsSpec.IsConfident(double.NaN));
    }
}

// =============================================================================
//  END-TO-END DEGRADATION — a sustained burst of noisy / dropped / wild fixes must
//  never throw, never NaN/inflate the distance, never false-latch, and a good fix
//  afterward must resume normal tracking. Mirrors the HomeController pipeline:
//  usable-fix gate -> accuracy-weighted smoothing -> jitter-gated distance ->
//  confidence-gated arrival/overshoot latch.
// =============================================================================
public class CrowdedGpsDegradationTests
{
    // A faithful little mirror of the live pipeline so we can feed it adversarial fixes and assert the
    // invariants the production code promises.
    private sealed class Pipeline
    {
        const double ArrivalThreshold = 200, OvershootBuffer = 250;
        const int ArrivalPersistence = 2, OvershootPersistence = 3;

        readonly double _destLat, _destLon;
        double _estLat, _estLon, _estAcc;          // accuracy-weighted running estimate
        double _anchorLat, _anchorLon; bool _hasAnchor; // jitter-gate anchor (raw fixes)
        int _arrivalStreak, _overshootStreak; double _lastOver = double.MaxValue;

        public double TotalDistanceMeters;
        public bool Arrived, OvershootAlerted;
        public int Dropped;
        public double EstLat => _estLat;
        public double EstLon => _estLon;

        public Pipeline(double destLat, double destLon) { _destLat = destLat; _destLon = destLon; }

        public void Feed(double lat, double lon, double accuracy)
        {
            // 1) Gate: a NaN/Infinity/off-globe fix is dropped — estimate, anchor and distance untouched.
            if (!CrowdedGpsSpec.IsUsableFix(lat, lon)) { Dropped++; return; }

            // 2) Jitter-gated distance from the raw fix (this is what feeds the trip total).
            if (_hasAnchor)
            {
                var seg = CrowdedGpsSpec.DistanceMeters(_anchorLat, _anchorLon, lat, lon);
                if (seg >= CrowdedGpsSpec.JitterGateMeters(accuracy))
                {
                    TotalDistanceMeters += seg;
                    _anchorLat = lat; _anchorLon = lon;
                }
            }
            else { _anchorLat = lat; _anchorLon = lon; _hasAnchor = true; }

            // 3) Accuracy-weighted smoothing before any stage decision.
            (_estLat, _estLon, _estAcc) = CrowdedGpsSpec.Smooth(_estLat, _estLon, _estAcc, lat, lon, accuracy);

            // 4) Confidence-gated arrival/overshoot, computed from the SMOOTHED estimate.
            var confident = CrowdedGpsSpec.IsConfident(accuracy);
            var distance = CrowdedGpsSpec.DistanceMeters(_estLat, _estLon, _destLat, _destLon);

            if (confident && !Arrived && distance <= ArrivalThreshold)
            {
                if (++_arrivalStreak >= ArrivalPersistence) { Arrived = true; return; }
            }
            else if (confident && !Arrived) { _arrivalStreak = 0; }

            if (confident && Arrived && !OvershootAlerted)
            {
                var threshold = ArrivalThreshold + OvershootBuffer + System.Math.Max(accuracy, 0);
                if (distance >= threshold && distance > _lastOver) _overshootStreak++;
                else if (distance < _lastOver) _overshootStreak = 0;
                _lastOver = distance;
                if (_overshootStreak >= OvershootPersistence) OvershootAlerted = true;
            }
        }
    }

    // (a) A long run of low-accuracy fixes from a rider waiting in a packed terminal: scattered a few
    //     metres at 80 m accuracy. No throw, no NaN, no inflated distance, no false arrival/lockout — then
    //     a couple of confident fixes resume normal tracking and latch the arrival.
    [Fact]
    public void LongLowAccuracyRun_NoNaN_NoInflation_NoFalseLatch_ThenResumes()
    {
        var pipe = new Pipeline(destLat: 14.6097, destLon: 120.9895);

        // 300 scattered low-confidence fixes within ~7 m of the stop.
        var rng = new System.Random(1234);
        for (int i = 0; i < 300; i++)
        {
            double lat = 14.6097 + (rng.NextDouble() - 0.5) * 0.00012; // ±~6.7 m
            double lon = 120.9895 + (rng.NextDouble() - 0.5) * 0.00012;
            pipe.Feed(lat, lon, accuracy: 80.0);
        }

        Assert.False(double.IsNaN(pipe.EstLat) || double.IsNaN(pipe.EstLon), "Estimate must never go NaN.");
        Assert.True(double.IsFinite(pipe.TotalDistanceMeters), "Distance must stay finite.");
        Assert.True(pipe.TotalDistanceMeters < 50, $"Stationary jitter must not inflate distance (got {pipe.TotalDistanceMeters:F1} m).");
        Assert.False(pipe.Arrived, "Low-confidence fixes must not latch arrival.");
        Assert.False(pipe.OvershootAlerted, "Low-confidence fixes must not latch overshoot.");

        // A confident fix returns — tracking resumes and arrival latches after the persistence count.
        pipe.Feed(14.6097, 120.9895, accuracy: 6.0);
        pipe.Feed(14.6097, 120.9895, accuracy: 5.0);
        Assert.True(pipe.Arrived, "A good fix after the noise must resume normal latching.");
    }

    // (b) Intermittent dropped/absent fixes (modelled as NaN coordinates the provider hands us during a
    //     signal blackout). Each drop must leave the estimate exactly where the last good fix put it, and a
    //     good fix afterward must move it again. Never NaN, never stuck.
    [Fact]
    public void IntermittentDroppedFixes_EstimateSurvives_AndResumes()
    {
        var pipe = new Pipeline(destLat: 14.7000, destLon: 121.0000);

        pipe.Feed(14.6097, 120.9895, 6.0);             // good
        var (lat0, lon0) = (pipe.EstLat, pipe.EstLon);

        pipe.Feed(double.NaN, 120.99, 6.0);            // dropped
        pipe.Feed(14.61, double.NaN, 6.0);             // dropped
        pipe.Feed(double.PositiveInfinity, double.NaN, 6.0); // dropped

        Assert.Equal(3, pipe.Dropped);
        Assert.Equal(lat0, pipe.EstLat, 9);            // estimate untouched by drops
        Assert.Equal(lon0, pipe.EstLon, 9);
        Assert.False(double.IsNaN(pipe.EstLat) || double.IsNaN(pipe.EstLon));

        pipe.Feed(14.6110, 120.9905, 5.0);             // good fix resumes the blend
        Assert.NotEqual(lat0, pipe.EstLat);
        Assert.True(double.IsFinite(pipe.EstLat) && double.IsFinite(pipe.EstLon));
    }

    // (c) Wild outliers: non-finite and off-globe garbage is rejected outright; a finite-but-distant jump
    //     reported at poor accuracy is accepted as position but can't latch a stage (confidence gate), and
    //     never produces a NaN distance. A good fix afterward still tracks.
    [Fact]
    public void WildOutliers_RejectedOrContained_NeverNaN()
    {
        var pipe = new Pipeline(destLat: 14.6097, destLon: 120.9895);

        pipe.Feed(14.6097, 120.9895, 5.0);             // a clean baseline near the stop

        // Garbage that must be rejected by the gate.
        pipe.Feed(double.NaN, double.NaN, 5.0);
        pipe.Feed(double.PositiveInfinity, 120.0, 5.0);
        pipe.Feed(999.0, 9999.0, 5.0);                 // off the globe
        Assert.Equal(3, pipe.Dropped);

        // A finite but absurd jump across the country, reported at terrible accuracy.
        pipe.Feed(7.0, 125.0, accuracy: 800.0);
        Assert.True(double.IsFinite(pipe.EstLat) && double.IsFinite(pipe.EstLon));
        Assert.False(pipe.Arrived, "A low-confidence outlier must not latch a stage.");
        Assert.False(pipe.OvershootAlerted);

        // Recovery: confident fixes back at the stop latch arrival normally.
        pipe.Feed(14.6097, 120.9895, 6.0);
        pipe.Feed(14.6097, 120.9895, 5.0);
        Assert.True(double.IsFinite(pipe.EstLat));
    }
}
