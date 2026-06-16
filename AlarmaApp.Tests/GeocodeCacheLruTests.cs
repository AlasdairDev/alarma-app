// =============================================================================
//  GeocodeCacheLruTests.cs
// -----------------------------------------------------------------------------
//  ISOLATED unit tests for the "Offline Geocoding Cache" least-recently-used
//  (LRU) eviction logic referenced by README.md ("Offline-first operation",
//  "Destination search — dual-source geocoding").
//
//  Production sources mirrored here as pure helpers (NO production code
//  referenced, NO Android / MAUI SDK, runs on plain net9.0):
//
//    * DatabaseService.EvictGeocodeCacheOverflowAsync (DatabaseService.cs:178)
//        const int MaxEntries = 100;
//        if (count <= MaxEntries) return;
//        excess = count - MaxEntries;
//        delete the `excess` rows ORDER BY LastUsedUtc ASC  (oldest first)
//    * GeocodingService.SearchAsync cache-key build (GeocodingService.cs:146-147)
//        expanded = ExpandAlias(query.Trim());
//        cacheKey = expanded.ToLowerInvariant();
//    * GeocodingService cache HIT refreshes LRU stamp (GeocodingService.cs:157)
//        cached.LastUsedUtc = DateTime.UtcNow;
//    * GeocodeCache model (Models/GeocodeCache.cs) — LastUsedUtc drives ordering.
//
//  The eviction is re-implemented as an in-memory list sort+take so the exact
//  "oldest-LastUsedUtc-first, keep newest 100" contract is provable without a
//  SQLite connection.
// =============================================================================

using Xunit;

namespace AlarmaApp.Tests;

/// <summary>Minimal stand-in for Models.GeocodeCache (same LRU-relevant fields).</summary>
internal sealed class CacheRow
{
    public string QueryKey { get; init; } = string.Empty;
    public DateTime LastUsedUtc { get; set; }
}

/// <summary>
/// Pure re-implementation of the geocode-cache LRU rules.
/// </summary>
internal static class GeocodeCacheLru
{
    public const int MaxEntries = 100; // DatabaseService.cs:180

    /// <summary>
    /// Mirrors EvictGeocodeCacheOverflowAsync: returns the rows that SURVIVE
    /// after evicting the oldest-LastUsedUtc rows down to MaxEntries. A no-op
    /// when count &lt;= MaxEntries.
    /// </summary>
    public static List<CacheRow> EvictOverflow(IReadOnlyList<CacheRow> rows)
    {
        if (rows.Count <= MaxEntries) return rows.ToList();
        var excess = rows.Count - MaxEntries;
        // Oldest first (ascending LastUsedUtc) are the eviction victims.
        var victims = rows.OrderBy(r => r.LastUsedUtc).Take(excess).ToHashSet();
        return rows.Where(r => !victims.Contains(r)).ToList();
    }

    /// <summary>The keys evicted (oldest-first) for a given overflowing set.</summary>
    public static List<string> EvictedKeys(IReadOnlyList<CacheRow> rows)
    {
        if (rows.Count <= MaxEntries) return new List<string>();
        var excess = rows.Count - MaxEntries;
        return rows.OrderBy(r => r.LastUsedUtc).Take(excess).Select(r => r.QueryKey).ToList();
    }
}

public class GeocodeCacheEvictionTests
{
    private static List<CacheRow> MakeRows(int n)
    {
        // Row i has LastUsedUtc = base + i minutes, so index 0 is the OLDEST.
        var baseTime = new DateTime(2026, 6, 7, 0, 0, 0, DateTimeKind.Utc);
        return Enumerable.Range(0, n)
            .Select(i => new CacheRow { QueryKey = $"key{i:D3}", LastUsedUtc = baseTime.AddMinutes(i) })
            .ToList();
    }

    // At or below the cap, nothing is evicted.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(100)]
    public void AtOrBelowCap_NoEviction(int count)
    {
        var rows = MakeRows(count);
        var survivors = GeocodeCacheLru.EvictOverflow(rows);
        Assert.Equal(count, survivors.Count);
        Assert.Empty(GeocodeCacheLru.EvictedKeys(rows));
    }

    // One over the cap evicts exactly one row — the single oldest (key000).
    [Fact]
    public void OneOverCap_EvictsExactlyOneOldest()
    {
        var rows = MakeRows(101);
        var survivors = GeocodeCacheLru.EvictOverflow(rows);

        Assert.Equal(100, survivors.Count);
        Assert.DoesNotContain(survivors, r => r.QueryKey == "key000");
        Assert.Equal(new[] { "key000" }, GeocodeCacheLru.EvictedKeys(rows));
    }

    // A large overflow evicts (count - 100) rows, always the oldest block, and
    // always keeps the newest 100.
    [Fact]
    public void LargeOverflow_KeepsNewest100_EvictsOldestBlock()
    {
        var rows = MakeRows(150);
        var survivors = GeocodeCacheLru.EvictOverflow(rows);
        var evicted = GeocodeCacheLru.EvictedKeys(rows);

        Assert.Equal(100, survivors.Count);
        Assert.Equal(50, evicted.Count);

        // The 50 oldest (key000..key049) are gone; key050..key149 survive.
        Assert.Equal("key000", evicted.First());
        Assert.Equal("key049", evicted.Last());
        Assert.Contains(survivors, r => r.QueryKey == "key050");
        Assert.Contains(survivors, r => r.QueryKey == "key149");
    }

    // The defining LRU property: a row that was recently *used* (its LastUsedUtc
    // refreshed on a cache hit) survives even though it was inserted long ago,
    // while a never-touched newer row can be evicted before it. We refresh the
    // oldest row to "now" and confirm it is no longer the victim.
    [Fact]
    public void RecentlyUsedRow_SurvivesOverInsertionOrder()
    {
        var rows = MakeRows(101);          // key000 is oldest, would be evicted
        // Simulate a cache HIT on key000: refresh its stamp to the newest.
        rows[0].LastUsedUtc = new DateTime(2026, 6, 7, 23, 0, 0, DateTimeKind.Utc);

        var evicted = GeocodeCacheLru.EvictedKeys(rows);

        // Now key001 (the next-oldest, never re-used) is the victim, not key000.
        Assert.Equal(new[] { "key001" }, evicted);
        Assert.DoesNotContain("key000", evicted);
    }

    // Eviction count is exactly the overflow amount for several sizes.
    [Theory]
    [InlineData(101, 1)]
    [InlineData(120, 20)]
    [InlineData(200, 100)]
    public void EvictionCount_EqualsOverflow(int count, int expectedEvicted)
        => Assert.Equal(expectedEvicted, GeocodeCacheLru.EvictedKeys(MakeRows(count)).Count);
}

public class GeocodeCacheKeyTests
{
    // Mirrors GeocodingService cache-key build: ExpandAlias(query.Trim()).ToLowerInvariant().
    // The alias map here is the subset asserted, matching GeocodingService.PhAliases.
    private static readonly Dictionary<string, string> Aliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["pup"]  = "Polytechnic University of the Philippines",
            ["bgc"]  = "Bonifacio Global City",
            ["moa"]  = "Mall of Asia",
            ["naia"] = "Ninoy Aquino International Airport",
            ["mrt"]  = "MRT Line 3 Station",
        };

    private static string ExpandAlias(string q)
    {
        if (Aliases.TryGetValue(q, out var full)) return full;
        foreach (var (abbr, expansion) in Aliases)
            if (q.StartsWith(abbr + " ", StringComparison.OrdinalIgnoreCase))
                return expansion + q[abbr.Length..];
        return q;
    }

    private static string CacheKey(string query) => ExpandAlias(query.Trim()).ToLowerInvariant();

    // Exact alias expands then lowercases — the lookup key is the canonical form.
    [Theory]
    [InlineData("pup", "polytechnic university of the philippines")]
    [InlineData("BGC", "bonifacio global city")]
    [InlineData("Moa", "mall of asia")]
    public void ExactAlias_ExpandsAndLowercases(string input, string expectedKey)
        => Assert.Equal(expectedKey, CacheKey(input));

    // Prefix alias keeps the trailing remainder ("bgc starbucks").
    [Fact]
    public void PrefixAlias_PreservesRemainder()
        => Assert.Equal("bonifacio global city starbucks", CacheKey("bgc starbucks"));

    // The key MUST be stable across case + surrounding whitespace so the same
    // logical query is one cache row (not many) — this is what makes the LRU
    // table effective and bounded.
    [Fact]
    public void SameLogicalQuery_ProducesIdenticalKey_AcrossCaseAndWhitespace()
    {
        var a = CacheKey("  NAIA ");
        var b = CacheKey("naia");
        var c = CacheKey("Naia");
        Assert.Equal(a, b);
        Assert.Equal(b, c);
        Assert.Equal("ninoy aquino international airport", a);
    }

    // A non-alias query is passed through verbatim (trimmed + lowercased).
    [Fact]
    public void NonAliasQuery_TrimmedAndLowercasedOnly()
        => Assert.Equal("cebu it park", CacheKey("  Cebu IT Park "));
}
