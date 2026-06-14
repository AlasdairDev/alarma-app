// queries get Uri.EscapeDataString'd before they go in the URL, cache keys are lowercased.
// base URLs are hardcoded (photon + nominatim) so no SSRF, and the PH bbox keeps results local.
// 15s timeout so a slow/dead network can't hang us. lat/lon are range-checked before we trust them.
// cache lives in the same encrypted db as everything else.
// no keys / creds / PII sent or logged here.

using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlarmaApp.Models;
using Microsoft.Maui.ApplicationModel;

namespace AlarmaApp.Services;

public class GeocodingService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private readonly HttpClient _photonClient;
    private readonly HttpClient _nominatimClient;
    private readonly DatabaseService _db;

    private const double PhLatMin = 4.5;
    private const double PhLatMax = 21.1;
    private const double PhLonMin = 116.9;
    private const double PhLonMax = 126.6;

    // bias Photon ranking toward Metro Manila (most commuters are here)
    private const double PhBiasLat = 14.5995;
    private const double PhBiasLon = 120.9842;

    // PH shorthand we expand before searching (people type "moa" not the full name)
    private static readonly Dictionary<string, string> PhAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["pup"]           = "Polytechnic University of the Philippines",
            ["bgc"]           = "Bonifacio Global City",
            ["fort"]          = "Bonifacio Global City",
            ["fort boni"]     = "Bonifacio Global City",
            ["fort bonifacio"]= "Bonifacio Global City",
            ["moa"]           = "Mall of Asia",
            ["sm moa"]        = "SM Mall of Asia",
            ["naia"]          = "Ninoy Aquino International Airport",
            ["ccp"]           = "Cultural Center of the Philippines",
            ["edsa"]          = "Epifanio de los Santos Avenue",
            ["c5"]            = "C5 Road",
            ["c-5"]           = "C5 Road",
            ["cdo"]           = "Cagayan de Oro",
            ["luneta"]        = "Rizal Park Manila",
            ["araneta"]       = "Araneta City Cubao",
            ["cubao"]         = "Araneta City Cubao",
            ["up diliman"]    = "University of the Philippines Diliman",
            ["up manila"]     = "University of the Philippines Manila",
            ["up los banos"]  = "University of the Philippines Los Banos",
            ["dlsu"]          = "De La Salle University",
            ["la salle"]      = "De La Salle University",
            ["ateneo"]        = "Ateneo de Manila University",
            ["ust"]           = "University of Santo Tomas",
            ["feu"]           = "Far Eastern University",
            ["pgh"]           = "Philippine General Hospital",
            ["pnr"]           = "Philippine National Railways",
            ["lrt"]           = "LRT Station",
            ["lrt1"]          = "LRT Line 1",
            ["lrt2"]          = "LRT Line 2",
            ["mrt"]           = "MRT Line 3 Station",
            ["mrt3"]          = "MRT Line 3",
            ["nlex"]          = "North Luzon Expressway",
            ["slex"]          = "South Luzon Expressway",
            ["skyway"]        = "Metro Manila Skyway",
            ["macapagal"]     = "Diosdado Macapagal Boulevard",
            ["roxas"]         = "Roxas Boulevard",
            ["taft"]          = "Taft Avenue",
            ["aurora"]        = "Aurora Boulevard",
            ["ortigas"]       = "Ortigas Center",
            ["eastwood"]      = "Eastwood City",
            ["mckinley"]      = "McKinley Hill",
            ["market market"] = "Market Market BGC",
            ["greenbelt"]     = "Greenbelt Makati",
            ["glorietta"]     = "Glorietta Makati",
            ["trinoma"]       = "Trinoma Quezon City",
            ["sm north"]      = "SM City North EDSA",
            ["sm north edsa"] = "SM City North EDSA",
        };

    public GeocodingService(DatabaseService db)
    {
        _db = db;
        var version = AppInfo.VersionString;
        var ua = $"AlarmaApp/{version} (offline-first)";

        _photonClient = new HttpClient
        {
            BaseAddress = new Uri("https://photon.komoot.io/"),
            Timeout = RequestTimeout
        };
        _photonClient.DefaultRequestHeaders.UserAgent.ParseAdd(ua);

        _nominatimClient = new HttpClient
        {
            BaseAddress = new Uri("https://nominatim.openstreetmap.org/"),
            Timeout = RequestTimeout
        };
        _nominatimClient.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
        _nominatimClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
    }

    // offline fallback - hardcoded landmarks for when both APIs are down. keyed by expanded alias.
    private static readonly Dictionary<string, GeocodingResult> LocalFallback =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Polytechnic University of the Philippines"] =
                new("Polytechnic University of the Philippines, Manila", 14.5995, 120.9867, "University"),
            ["Bonifacio Global City"] =
                new("Bonifacio Global City, Taguig",    14.5505, 121.0497, "Landmark"),
            ["Mall of Asia"] =
                new("SM Mall of Asia, Pasay",           14.5355, 120.9830, "Shopping Mall"),
            ["SM Mall of Asia"] =
                new("SM Mall of Asia, Pasay",           14.5355, 120.9830, "Shopping Mall"),
            ["Ninoy Aquino International Airport"] =
                new("Ninoy Aquino International Airport, Pasay", 14.5086, 121.0194, "Airport"),
            ["Rizal Park Manila"] =
                new("Rizal Park (Luneta), Manila",      14.5831, 120.9794, "Park"),
            ["University of the Philippines Diliman"] =
                new("University of the Philippines Diliman, Quezon City", 14.6548, 121.0653, "University"),
            ["De La Salle University"] =
                new("De La Salle University, Manila",   14.5647, 120.9932, "University"),
            ["Ateneo de Manila University"] =
                new("Ateneo de Manila University, Quezon City", 14.6401, 121.0773, "University"),
            ["University of Santo Tomas"] =
                new("University of Santo Tomas, Manila", 14.6097, 120.9895, "University"),
        };

    public async Task<IReadOnlyList<GeocodingResult>> SearchAsync(
        string query, CancellationToken cancellationToken)
    {
        var expanded = ExpandAlias(query.Trim());
        var cacheKey = expanded.ToLowerInvariant();

        // ── Cache intercept (offline-first) ───────────────────────────────────
        var cached = await _db.GetGeocodeCacheAsync(cacheKey);
        if (cached is not null)
        {
            var cachedResults = DeserializeCacheResults(cached.ResultsJson);
            if (cachedResults.Count > 0)
            {
                // Refresh LRU timestamp on every hit.
                cached.LastUsedUtc = DateTime.UtcNow;
                await _db.UpsertGeocodeCacheAsync(cached);
                return cachedResults;
            }
        }

        // ── Online path ───────────────────────────────────────────────────────

        // photon first - fuzzier + faster than nominatim
        var photon = await SearchPhotonAsync(expanded, cancellationToken);

        // top up with nominatim if photon's a bit thin (happens on very specific addresses)
        if (photon.Count < 3)
        {
            var nominatim = await SearchNominatimAsync(expanded, cancellationToken);
            var merged = Merge(photon, nominatim, maxCount: 10);

            if (merged.Count > 0)
            {
                await SaveToCacheAsync(cacheKey, merged, cached);
                return merged;
            }

            // both APIs gave us nothing - try the hardcoded table
            if (LocalFallback.TryGetValue(expanded, out var local))
                return [local];

            return merged;
        }

        await SaveToCacheAsync(cacheKey, photon, cached);
        return photon;
    }

    // cache the top 3
    private async Task SaveToCacheAsync(
        string cacheKey,
        IReadOnlyList<GeocodingResult> results,
        GeocodeCache? existing)
    {
        var top3 = results.Take(3).ToList();
        var json = JsonSerializer.Serialize(top3);
        var entry = existing ?? new GeocodeCache { QueryKey = cacheKey };
        entry.ResultsJson = json;
        entry.LastUsedUtc = DateTime.UtcNow;
        await _db.UpsertGeocodeCacheAsync(entry);
    }

    private static List<GeocodingResult> DeserializeCacheResults(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<GeocodingResult>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    // ── Photon (primary) ──────────────────────────────────────────────────────

    private async Task<List<GeocodingResult>> SearchPhotonAsync(
        string query, CancellationToken cancellationToken)
    {
        // bbox=minLon,minLat,maxLon,maxLat — hard-clips to Philippines
        // lat/lon — biases ranking toward Metro Manila
        var bLon0 = PhLonMin.ToString("F1", CultureInfo.InvariantCulture);
        var bLat0 = PhLatMin.ToString("F1", CultureInfo.InvariantCulture);
        var bLon1 = PhLonMax.ToString("F1", CultureInfo.InvariantCulture);
        var bLat1 = PhLatMax.ToString("F1", CultureInfo.InvariantCulture);
        var biasLat = PhBiasLat.ToString("F4", CultureInfo.InvariantCulture);
        var biasLon = PhBiasLon.ToString("F4", CultureInfo.InvariantCulture);

        var uri = $"api/?q={Uri.EscapeDataString(query)}&limit=10&lang=en" +
                  $"&lat={biasLat}&lon={biasLon}" +
                  $"&bbox={bLon0},{bLat0},{bLon1},{bLat1}";

        try
        {
            var resp = await _photonClient
                .GetFromJsonAsync<PhotonResponse>(uri, cancellationToken)
                ?? new PhotonResponse([]);

            var results = new List<GeocodingResult>(resp.Features.Count);
            foreach (var f in resp.Features)
            {
                if (f.Geometry.Coordinates.Length < 2) continue;
                var lon = f.Geometry.Coordinates[0];
                var lat = f.Geometry.Coordinates[1];

                // Accept PH country code, or no code but within PH coordinates
                var code = f.Properties.CountryCode?.ToUpperInvariant();
                if (code is not null && code != "PH") continue;
                if (!InPhilippines(lat, lon)) continue;

                var name = BuildPhotonName(f.Properties);
                if (string.IsNullOrWhiteSpace(name)) continue;

                var ptype = FormatPlaceType(f.Properties.OsmKey,
                                            f.Properties.OsmValue ?? f.Properties.Type);
                results.Add(new GeocodingResult(name, lat, lon, ptype));
            }
            return results;
        }
        catch (OperationCanceledException) { throw; }
        catch { return []; }
    }

    private static string BuildPhotonName(PhotonProperties p)
    {
        var name = p.Name;

        // Street address fallback when there is no place name
        if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(p.Street))
            name = string.IsNullOrWhiteSpace(p.HouseNumber)
                ? p.Street : $"{p.HouseNumber} {p.Street}";

        var city = p.City ?? p.County;

        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(city)
            && !string.Equals(name.Trim(), city, StringComparison.OrdinalIgnoreCase))
            return $"{name.Trim()}, {city.Trim()}";

        if (!string.IsNullOrWhiteSpace(name)) return name.Trim();

        if (!string.IsNullOrWhiteSpace(city))
        {
            var region = p.State ?? p.County;
            if (!string.IsNullOrWhiteSpace(region)
                && !string.Equals(city, region, StringComparison.OrdinalIgnoreCase))
                return $"{city.Trim()}, {region.Trim()}";
            return city.Trim();
        }

        return string.Empty;
    }

    // ── Nominatim (fallback / supplement) ────────────────────────────────────

    private async Task<List<GeocodingResult>> SearchNominatimAsync(
        string query, CancellationToken cancellationToken)
    {
        var uri = $"search?format=jsonv2&limit=8&countrycodes=ph&accept-language=en" +
                  $"&addressdetails=1&namedetails=1&dedupe=1" +
                  $"&viewbox={PhLonMin},{PhLatMax},{PhLonMax},{PhLatMin}" +
                  $"&q={Uri.EscapeDataString(query)}";
        try
        {
            var raw = await _nominatimClient
                .GetFromJsonAsync<List<NominatimResult>>(uri, cancellationToken) ?? [];

            var results = new List<GeocodingResult>(raw.Count);
            foreach (var r in raw)
            {
                if (!TryParseLatitude(r.Latitude, out var lat)
                    || !TryParseLongitude(r.Longitude, out var lon)) continue;
                if (!InPhilippines(lat, lon)) continue;

                results.Add(new GeocodingResult(
                    BuildNominatimName(r), lat, lon,
                    FormatPlaceType(r.OsmClass, r.OsmType)));
            }
            return results;
        }
        catch (OperationCanceledException) { throw; }
        catch { return []; }
    }

    private static string BuildNominatimName(NominatimResult r)
    {
        var addr = result_addr(r);
        var city  = NomCity(addr);

        if (r.NameDetails?.TryGetValue("name:en", out var en) == true
            && !string.IsNullOrWhiteSpace(en))
        {
            var e = en.Trim();
            return string.IsNullOrWhiteSpace(city)
                   || string.Equals(e, city, StringComparison.OrdinalIgnoreCase)
                ? e : $"{e}, {city}";
        }

        if (addr is not null)
        {
            var primary = addr.Amenity ?? addr.Leisure ?? addr.Building ?? addr.Road
                          ?? addr.Suburb ?? addr.Neighbourhood ?? addr.Quarter;
            if (!string.IsNullOrWhiteSpace(primary) && !string.IsNullOrWhiteSpace(city)
                && !string.Equals(primary.Trim(), city, StringComparison.OrdinalIgnoreCase))
                return $"{primary.Trim()}, {city}";

            if (!string.IsNullOrWhiteSpace(city))
            {
                var region = addr.Province ?? addr.State;
                if (!string.IsNullOrWhiteSpace(region)
                    && !string.Equals(city, region, StringComparison.OrdinalIgnoreCase))
                    return $"{city.Trim()}, {region.Trim()}";
                return city.Trim();
            }
        }

        return SplitRaw(r.DisplayName);
    }

    private static NominatimAddress? result_addr(NominatimResult r) => r.Address;
    private static string? NomCity(NominatimAddress? a) =>
        a is null ? null : (a.City ?? a.Town ?? a.Municipality);

    private static string SplitRaw(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        var p = raw.Split(',');
        return p.Length <= 2 ? raw.Trim() : $"{p[0].Trim()}, {p[1].Trim()}";
    }

    // ── Place type label ──────────────────────────────────────────────────────

    private static string? FormatPlaceType(string? osmClass, string? osmType)
    {
        var t = osmType?.ToLowerInvariant().Replace('-', '_');
        var c = osmClass?.ToLowerInvariant();
        return (c, t) switch
        {
            (_, "hospital")          => "Hospital",
            (_, "clinic")            => "Clinic",
            (_, "university")        => "University",
            (_, "college")           => "College",
            (_, "school")            => "School",
            (_, "place_of_worship")  => "Church / Mosque",
            (_, "restaurant")        => "Restaurant",
            (_, "cafe")              => "Café",
            (_, "fast_food")         => "Fast Food",
            (_, "bank")              => "Bank",
            (_, "pharmacy")          => "Pharmacy",
            (_, "fuel")              => "Gas Station",
            (_, "bus_station")       => "Bus Terminal",
            (_, "ferry_terminal")    => "Ferry Terminal",
            (_, "marketplace")       => "Market",
            (_, "mall")              => "Shopping Mall",
            (_, "supermarket")       => "Supermarket",
            (_, "department_store")  => "Department Store",
            (_, "convenience")       => "Convenience Store",
            (_, "city")              => "City",
            (_, "town")              => "Town",
            (_, "municipality")      => "Municipality",
            (_, "suburb")            => "Barangay",
            (_, "neighbourhood")     => "Neighbourhood",
            (_, "village")           => "Village",
            (_, "island")            => "Island",
            (_, "residential")       => "Street",
            ("highway", "primary")
                or ("highway", "secondary")
                or ("highway", "tertiary")   => "Road",
            ("highway", "motorway")
                or ("highway", "trunk")      => "Highway",
            (_, "hotel")             => "Hotel",
            (_, "attraction")        => "Landmark",
            (_, "museum")            => "Museum",
            (_, "park")              => "Park",
            (_, "sports_centre")     => "Sports Center",
            (_, "government")        => "Government Office",
            (_, "cinema")            => "Cinema",
            (_, "theatre")           => "Theater",
            (_, "library")           => "Library",
            (_, "stadium")           => "Stadium",
            (_, _) when t != null    =>
                char.ToUpperInvariant(t[0]) + t[1..].Replace('_', ' '),
            _ => null
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ExpandAlias(string q)
    {
        // Exact match
        if (PhAliases.TryGetValue(q, out var full)) return full;

        // Prefix: "bgc starbucks" → "Bonifacio Global City starbucks"
        foreach (var (abbr, expansion) in PhAliases)
        {
            if (q.StartsWith(abbr + " ", StringComparison.OrdinalIgnoreCase))
                return expansion + q[abbr.Length..];
        }
        return q;
    }

    private static bool InPhilippines(double lat, double lon) =>
        lat >= PhLatMin && lat <= PhLatMax && lon >= PhLonMin && lon <= PhLonMax;

    private static IReadOnlyList<GeocodingResult> Merge(
        List<GeocodingResult> primary,
        List<GeocodingResult> secondary,
        int maxCount)
    {
        var merged = new List<GeocodingResult>(primary);
        foreach (var s in secondary)
        {
            var dupe = merged.Any(p =>
                HaversineM(p.Latitude, p.Longitude, s.Latitude, s.Longitude) < 150);
            if (!dupe) merged.Add(s);
            if (merged.Count >= maxCount) break;
        }
        return merged.Take(maxCount).ToList();
    }

    private static double HaversineM(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6_371_000;
        var φ1 = lat1 * Math.PI / 180;
        var φ2 = lat2 * Math.PI / 180;
        var Δφ = (lat2 - lat1) * Math.PI / 180;
        var Δλ = (lon2 - lon1) * Math.PI / 180;
        var a  = Math.Sin(Δφ / 2) * Math.Sin(Δφ / 2)
               + Math.Cos(φ1) * Math.Cos(φ2) * Math.Sin(Δλ / 2) * Math.Sin(Δλ / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static bool TryParseLatitude(string v, out double r) =>
        double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out r)
        && double.IsFinite(r) && r is >= -90.0 and <= 90.0;

    private static bool TryParseLongitude(string v, out double r) =>
        double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out r)
        && double.IsFinite(r) && r is >= -180.0 and <= 180.0;

    // ── JSON models: Photon ───────────────────────────────────────────────────

    private sealed record PhotonResponse(
        [property: JsonPropertyName("features")] List<PhotonFeature> Features);

    private sealed record PhotonFeature(
        [property: JsonPropertyName("geometry")]   PhotonGeometry   Geometry,
        [property: JsonPropertyName("properties")] PhotonProperties Properties);

    private sealed record PhotonGeometry(
        [property: JsonPropertyName("coordinates")] double[] Coordinates);

    private sealed record PhotonProperties(
        [property: JsonPropertyName("name")]         string? Name,
        [property: JsonPropertyName("country_code")] string? CountryCode,
        [property: JsonPropertyName("city")]         string? City,
        [property: JsonPropertyName("district")]     string? District,
        [property: JsonPropertyName("county")]       string? County,
        [property: JsonPropertyName("state")]        string? State,
        [property: JsonPropertyName("street")]       string? Street,
        [property: JsonPropertyName("housenumber")]  string? HouseNumber,
        [property: JsonPropertyName("postcode")]     string? Postcode,
        [property: JsonPropertyName("osm_key")]      string? OsmKey,
        [property: JsonPropertyName("osm_value")]    string? OsmValue,
        [property: JsonPropertyName("type")]         string? Type);

    // ── JSON models: Nominatim ────────────────────────────────────────────────

    private sealed record NominatimAddress(
        [property: JsonPropertyName("amenity")]       string? Amenity,
        [property: JsonPropertyName("leisure")]       string? Leisure,
        [property: JsonPropertyName("building")]      string? Building,
        [property: JsonPropertyName("road")]          string? Road,
        [property: JsonPropertyName("suburb")]        string? Suburb,
        [property: JsonPropertyName("neighbourhood")] string? Neighbourhood,
        [property: JsonPropertyName("quarter")]       string? Quarter,
        [property: JsonPropertyName("city")]          string? City,
        [property: JsonPropertyName("town")]          string? Town,
        [property: JsonPropertyName("municipality")]  string? Municipality,
        [property: JsonPropertyName("province")]      string? Province,
        [property: JsonPropertyName("state")]         string? State);

    private sealed record NominatimResult(
        [property: JsonPropertyName("display_name")] string                     DisplayName,
        [property: JsonPropertyName("lat")]          string                     Latitude,
        [property: JsonPropertyName("lon")]          string                     Longitude,
        [property: JsonPropertyName("class")]        string?                    OsmClass,
        [property: JsonPropertyName("type")]         string?                    OsmType,
        [property: JsonPropertyName("address")]      NominatimAddress?          Address,
        [property: JsonPropertyName("namedetails")]  Dictionary<string, string>? NameDetails);
}

public record GeocodingResult(string DisplayName, double Latitude, double Longitude, string? PlaceType = null);
