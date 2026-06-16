using SQLite;

namespace AlarmaApp.Models;

[Table("GeocodeCache")]
public class GeocodeCache
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    // lowercased + alias-expanded search term, used as the lookup key
    [Indexed]
    public string QueryKey { get; set; } = string.Empty;

    // up to 3 GeocodingResults as json
    public string ResultsJson { get; set; } = string.Empty;

    // bumped on every hit/write - drives LRU eviction
    public DateTime LastUsedUtc { get; set; }
}
