using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Maui.ApplicationModel;

namespace AlarmaApp.Services;

public class GeocodingService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private readonly HttpClient _httpClient;

    public GeocodingService()
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://nominatim.openstreetmap.org/"),
            Timeout = RequestTimeout
        };
        var version = AppInfo.VersionString;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"AlarmaApp/{version} (offline-first)");
    }

    public async Task<IReadOnlyList<GeocodingResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var requestUri = $"search?format=jsonv2&limit=5&q={Uri.EscapeDataString(query)}";
        var results = await _httpClient.GetFromJsonAsync<List<NominatimResult>>(requestUri, cancellationToken)
                      ?? new List<NominatimResult>();

        var parsedResults = new List<GeocodingResult>(results.Count);
        foreach (var result in results)
        {
            if (!TryParseCoordinate(result.Latitude, out var latitude)
                || !TryParseCoordinate(result.Longitude, out var longitude))
            {
                continue;
            }

            parsedResults.Add(new GeocodingResult(result.DisplayName, latitude, longitude));
        }

        return parsedResults;
    }

    private static bool TryParseCoordinate(string value, out double result)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result)
            && double.IsFinite(result);
    }

    private sealed record NominatimResult(
        [property: JsonPropertyName("display_name")] string DisplayName,
        [property: JsonPropertyName("lat")] string Latitude,
        [property: JsonPropertyName("lon")] string Longitude);
}

public record GeocodingResult(string DisplayName, double Latitude, double Longitude);
