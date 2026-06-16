// =============================================================================
//  GeocodingFallbackTests.cs
// -----------------------------------------------------------------------------
//  ISOLATED unit tests for the 3-tier forward-geocoding fallback in
//  GeocodingService (Photon → Nominatim → native platform geocoder), plus the
//  coordinate-label fallback for nameless pins.
//
//  Following the convention in AdaptiveAlarmTests.cs / RouteDeviationTests.cs,
//  the pure decision helpers are re-implemented here and asserted directly. The
//  suite runs on plain net9.0 with no Android / MAUI SDK and references no
//  production code. (The native tier itself — Geocoding.GetLocationsAsync — is a
//  platform call exercised on-device, not in this suite; only the PH-envelope
//  bound and labeling logic around it are unit-tested.)
// =============================================================================

using System.Globalization;
using Xunit;

namespace AlarmaApp.Tests;

internal static class GeocodingFallbackSpec
{
    // PH envelope — mirrors GeocodingService.InPhilippines (bounds native results).
    public static bool InPhilippines(double lat, double lon) =>
        lat >= 4.5 && lat <= 21.1 && lon >= 116.9 && lon <= 126.6;

    // Address-like queries are routed to Nominatim even when Photon already returned hits.
    public static bool IsAddressLike(string q)
    {
        if (string.IsNullOrWhiteSpace(q)) return false;
        if (q.Any(char.IsDigit)) return true;
        return q.Split(' ', System.StringSplitOptions.RemoveEmptyEntries).Length >= 3;
    }

    // Coordinate label for a valid pin we couldn't name.
    public static string CoordinateLabel(double lat, double lon) =>
        $"Near {lat.ToString("F4", CultureInfo.InvariantCulture)}, " +
        $"{lon.ToString("F4", CultureInfo.InvariantCulture)} (Unmapped Area)";
}

// =============================================================================
//  ADDRESS-LIKE DETECTION — gates the eager Nominatim query
// =============================================================================
public class AddressLikeDetectionTests
{
    [Theory]
    [InlineData("10 Acacia St", true)]                       // has a digit
    [InlineData("Phase 2 Block 5", true)]                    // has digits
    [InlineData("santo nino village paranaque", true)]       // 4 tokens
    [InlineData("greenwoods executive village", true)]       // 3 tokens
    [InlineData("moa", false)]                               // single short token
    [InlineData("ust", false)]                               // alias-style single token
    [InlineData("two words", false)]                         // 2 tokens, no digit
    [InlineData("", false)]                                  // empty
    [InlineData("   ", false)]                               // whitespace
    public void IsAddressLike_ClassifiesQueries(string query, bool expected)
        => Assert.Equal(expected, GeocodingFallbackSpec.IsAddressLike(query));
}

// =============================================================================
//  COORDINATE LABEL — nameless pins stay selectable instead of dropped/blank
// =============================================================================
public class CoordinateLabelTests
{
    [Fact]
    public void CoordinateLabel_FormatsToFourDecimals_Invariant()
        => Assert.Equal(
            "Near 14.6097, 120.9895 (Unmapped Area)",
            GeocodingFallbackSpec.CoordinateLabel(14.6097, 120.9895));

    // Rounds to 4 dp regardless of input precision, and is culture-invariant
    // (always a '.' decimal separator, never a ',').
    [Fact]
    public void CoordinateLabel_RoundsAndStaysInvariant()
    {
        var label = GeocodingFallbackSpec.CoordinateLabel(14.605512, 120.989512);
        Assert.Equal("Near 14.6055, 120.9895 (Unmapped Area)", label);
        Assert.DoesNotContain(",,", label);
    }

    [Fact]
    public void CoordinateLabel_IsNeverBlank()
        => Assert.False(string.IsNullOrWhiteSpace(
            GeocodingFallbackSpec.CoordinateLabel(0, 0)));
}

// =============================================================================
//  PH ENVELOPE BOUND — native results outside the Philippines are rejected
// =============================================================================
public class PhilippineEnvelopeTests
{
    [Theory]
    [InlineData(14.5995, 120.9842, true)]   // Manila
    [InlineData(7.1907, 125.4553, true)]    // Davao
    [InlineData(35.6762, 139.6503, false)]  // Tokyo
    [InlineData(1.3521, 103.8198, false)]   // Singapore
    [InlineData(0.0, 0.0, false)]           // null island
    public void InPhilippines_BoundsNativeResults(double lat, double lon, bool expected)
        => Assert.Equal(expected, GeocodingFallbackSpec.InPhilippines(lat, lon));
}
