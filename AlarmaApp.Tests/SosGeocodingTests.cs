// =============================================================================
//  SosGeocodingTests.cs
// -----------------------------------------------------------------------------
//  ISOLATED, MOCKED tests for the offline-safe SOS message formatter and the
//  reverse-geocoding address builder.
//
//  WHY THESE ARE SELF-CONTAINED (and use mocks):
//    The production formatter lives in HomeController.BuildSosMessageAsync and the
//    address builder in GeocodingService.ReverseGeocodeAddressAsync — both in the
//    net9.0-android app project, which a plain net9.0 test project cannot
//    reference (cross-target-framework). Per the existing convention in this test
//    project (see AdaptiveAlarmTests.cs), the production decision logic is
//    re-implemented here as small SUTs and the NATIVE geocoder is replaced with a
//    Moq mock of a test-local IPlacemarkSource — the exact shape of
//    Microsoft.Maui.Devices.Sensors.IGeocoding.GetPlacemarksAsync. This lets us
//    rigorously test the C# branching (online address vs. offline coordinate
//    fallback) without any real network or device.
//
//  Production strings mirrored verbatim from HomeController.BuildSosMessageAsync.
// =============================================================================

using System.Globalization;
using Moq;
using Xunit;

namespace AlarmaApp.Tests;

// ── Test doubles mirroring the production surface ────────────────────────────

/// <summary>A minimal stand-in for Microsoft.Maui.Devices.Sensors.Placemark,
/// carrying only the fields ReverseGeocodeAddressAsync actually reads.</summary>
public sealed record TestPlacemark(
    string? SubThoroughfare = null,
    string? Thoroughfare = null,
    string? SubLocality = null,
    string? Locality = null,
    string? AdminArea = null);

/// <summary>Mirrors IGeocoding.GetPlacemarksAsync(lat, lon). This is the seam we
/// mock so no real geocoder/network is touched.</summary>
public interface IPlacemarkSource
{
    Task<IEnumerable<TestPlacemark>?> GetPlacemarksAsync(double latitude, double longitude);
}

public readonly record struct GeoPoint(double Latitude, double Longitude);

/// <summary>Faithful re-implementation of GeocodingService.ReverseGeocodeAddressAsync —
/// same field order, same Take(3), same swallow-and-return-null on failure.</summary>
public sealed class ReverseGeocoder
{
    private readonly IPlacemarkSource _source;
    public ReverseGeocoder(IPlacemarkSource source) => _source = source;

    public async Task<string?> ReverseGeocodeAddressAsync(double latitude, double longitude)
    {
        try
        {
            var placemarks = await _source.GetPlacemarksAsync(latitude, longitude);
            var placemark = placemarks?.FirstOrDefault();
            if (placemark is null) return null;

            var street = JoinNonEmpty(placemark.SubThoroughfare, placemark.Thoroughfare);
            var parts = new[]
            {
                street,
                placemark.SubLocality,
                placemark.Locality,
                placemark.AdminArea
            };

            var address = string.Join(", ",
                parts.Where(p => !string.IsNullOrWhiteSpace(p))
                     .Select(p => p!.Trim())
                     .Take(3));
            return string.IsNullOrWhiteSpace(address) ? null : address;
        }
        catch
        {
            return null;
        }
    }

    private static string? JoinNonEmpty(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a)) return b;
        if (string.IsNullOrWhiteSpace(b)) return a;
        return $"{a.Trim()} {b.Trim()}";
    }
}

/// <summary>Faithful re-implementation of HomeController.BuildSosMessageAsync.</summary>
public sealed class SosMessageFormatter
{
    private readonly ReverseGeocoder _geocoder;
    public SosMessageFormatter(ReverseGeocoder geocoder) => _geocoder = geocoder;

    public async Task<string> BuildSosMessageAsync(GeoPoint? location)
    {
        if (location is null)
            return "EMERGENCY! I need help. My live location is currently unavailable — please call me.";

        var lat = location.Value.Latitude.ToString("F6", CultureInfo.InvariantCulture);
        var lon = location.Value.Longitude.ToString("F6", CultureInfo.InvariantCulture);

        var address = await _geocoder.ReverseGeocodeAddressAsync(location.Value.Latitude, location.Value.Longitude);
        if (!string.IsNullOrWhiteSpace(address))
            return $"EMERGENCY! I need help near {address}.";

        return $"EMERGENCY! I need help. My coordinates are Latitude: {lat}, Longitude: {lon}. " +
               "Please search these in a map app.";
    }
}

// ── Tests ────────────────────────────────────────────────────────────────────

public class SosMessageFormatterTests
{
    private static SosMessageFormatter FormatterWith(Mock<IPlacemarkSource> source)
        => new(new ReverseGeocoder(source.Object));

    // TEST A (Online): a successful geocode must format as the human-readable address line.
    [Fact]
    public async Task Online_SuccessfulGeocode_FormatsNearAddress()
    {
        var source = new Mock<IPlacemarkSource>();
        source.Setup(s => s.GetPlacemarksAsync(It.IsAny<double>(), It.IsAny<double>()))
              .ReturnsAsync(new[]
              {
                  new TestPlacemark(
                      SubThoroughfare: "123",
                      Thoroughfare: "Rizal Avenue",
                      SubLocality: "Barangay 659",
                      Locality: "Manila",
                      AdminArea: "Metro Manila")
              });

        var message = await FormatterWith(source)
            .BuildSosMessageAsync(new GeoPoint(14.5995, 120.9842));

        // Street + barangay + city (Take 3); the admin area is intentionally dropped.
        Assert.Equal(
            "EMERGENCY! I need help near 123 Rizal Avenue, Barangay 659, Manila.",
            message);
    }

    // TEST B (Offline): a geocode that yields nothing must fall back to raw coordinates,
    // formatted F6 / InvariantCulture, with NO URL.
    [Fact]
    public async Task Offline_NoPlacemarks_FallsBackToCoordinates()
    {
        var source = new Mock<IPlacemarkSource>();
        source.Setup(s => s.GetPlacemarksAsync(It.IsAny<double>(), It.IsAny<double>()))
              .ReturnsAsync((IEnumerable<TestPlacemark>?)null);

        var message = await FormatterWith(source)
            .BuildSosMessageAsync(new GeoPoint(14.5995, 120.9842));

        Assert.Equal(
            "EMERGENCY! I need help. My coordinates are Latitude: 14.599500, Longitude: 120.984200. " +
            "Please search these in a map app.",
            message);
        Assert.DoesNotContain("http", message, StringComparison.OrdinalIgnoreCase);
    }

    // Offline due to a thrown exception (e.g. no internet) — the swallow-to-null in the geocoder
    // means the formatter still produces the coordinate fallback rather than throwing.
    [Fact]
    public async Task Offline_GeocoderThrows_FallsBackToCoordinates()
    {
        var source = new Mock<IPlacemarkSource>();
        source.Setup(s => s.GetPlacemarksAsync(It.IsAny<double>(), It.IsAny<double>()))
              .ThrowsAsync(new HttpRequestException("no internet"));

        var message = await FormatterWith(source)
            .BuildSosMessageAsync(new GeoPoint(10.3157, 123.8854));

        Assert.StartsWith("EMERGENCY! I need help. My coordinates are Latitude: 10.315700", message);
    }

    // A geocode that returns a placemark with only blank fields collapses to no address,
    // so the formatter must still fall back to coordinates.
    [Fact]
    public async Task Online_BlankPlacemark_FallsBackToCoordinates()
    {
        var source = new Mock<IPlacemarkSource>();
        source.Setup(s => s.GetPlacemarksAsync(It.IsAny<double>(), It.IsAny<double>()))
              .ReturnsAsync(new[] { new TestPlacemark() });

        var message = await FormatterWith(source)
            .BuildSosMessageAsync(new GeoPoint(14.0, 121.0));

        Assert.Contains("My coordinates are", message);
    }

    // No fix at all → the location-unavailable line, and the geocoder is never even consulted.
    [Fact]
    public async Task NullLocation_ReturnsUnavailableMessage_AndSkipsGeocoder()
    {
        var source = new Mock<IPlacemarkSource>();

        var message = await FormatterWith(source).BuildSosMessageAsync(null);

        Assert.Equal(
            "EMERGENCY! I need help. My live location is currently unavailable — please call me.",
            message);
        source.Verify(s => s.GetPlacemarksAsync(It.IsAny<double>(), It.IsAny<double>()), Times.Never);
    }

    // The geocoder must be queried with the rider's LIVE coordinates, exactly once.
    [Fact]
    public async Task Online_QueriesGeocoderWithLiveCoordinates_Once()
    {
        var source = new Mock<IPlacemarkSource>();
        source.Setup(s => s.GetPlacemarksAsync(It.IsAny<double>(), It.IsAny<double>()))
              .ReturnsAsync(new[] { new TestPlacemark(Locality: "Cebu City") });

        await FormatterWith(source).BuildSosMessageAsync(new GeoPoint(10.3157, 123.8854));

        source.Verify(s => s.GetPlacemarksAsync(10.3157, 123.8854), Times.Once);
    }

    // Coordinate formatting must be culture-invariant (always a '.' decimal separator), so the
    // message can't break for riders whose device locale uses ',' as the decimal mark.
    [Fact]
    public async Task CoordinateFallback_IsCultureInvariant()
    {
        var original = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE"); // ',' decimals
            var source = new Mock<IPlacemarkSource>();
            source.Setup(s => s.GetPlacemarksAsync(It.IsAny<double>(), It.IsAny<double>()))
                  .ReturnsAsync((IEnumerable<TestPlacemark>?)null);

            var message = await FormatterWith(source)
                .BuildSosMessageAsync(new GeoPoint(14.5995, 120.9842));

            Assert.Contains("Latitude: 14.599500, Longitude: 120.984200", message);
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = original;
        }
    }
}

public class ReverseGeocoderAddressBuildingTests
{
    private static ReverseGeocoder GeocoderReturning(params TestPlacemark[] placemarks)
    {
        var source = new Mock<IPlacemarkSource>();
        source.Setup(s => s.GetPlacemarksAsync(It.IsAny<double>(), It.IsAny<double>()))
              .ReturnsAsync(placemarks);
        return new ReverseGeocoder(source.Object);
    }

    // Full placemark → "house street, barangay, city" (most-specific-first, Take 3 drops admin area).
    [Fact]
    public async Task FullPlacemark_BuildsStreetBarangayCity()
    {
        var address = await GeocoderReturning(new TestPlacemark(
            SubThoroughfare: "10", Thoroughfare: "Acacia St",
            SubLocality: "Phase 2", Locality: "Las Piñas", AdminArea: "Metro Manila"))
            .ReverseGeocodeAddressAsync(14.4, 120.9);

        Assert.Equal("10 Acacia St, Phase 2, Las Piñas", address);
    }

    // House number + street are joined with a space.
    [Fact]
    public async Task SubThoroughfareAndThoroughfare_JoinWithSpace()
    {
        var address = await GeocoderReturning(new TestPlacemark(
            SubThoroughfare: "123", Thoroughfare: "Rizal Ave"))
            .ReverseGeocodeAddressAsync(0, 0);

        Assert.Equal("123 Rizal Ave", address);
    }

    // Only a city present → that's the whole address.
    [Fact]
    public async Task OnlyLocality_ReturnsLocality()
    {
        var address = await GeocoderReturning(new TestPlacemark(Locality: "Davao City"))
            .ReverseGeocodeAddressAsync(0, 0);

        Assert.Equal("Davao City", address);
    }

    // Entirely blank placemark → null (caller falls back to coordinates).
    [Fact]
    public async Task EmptyPlacemark_ReturnsNull()
    {
        var address = await GeocoderReturning(new TestPlacemark())
            .ReverseGeocodeAddressAsync(0, 0);

        Assert.Null(address);
    }

    // No placemarks at all → null.
    [Fact]
    public async Task NoPlacemarks_ReturnsNull()
    {
        var address = await GeocoderReturning(Array.Empty<TestPlacemark>())
            .ReverseGeocodeAddressAsync(0, 0);

        Assert.Null(address);
    }
}
