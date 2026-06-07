using SQLite;
using System.Text.Json.Serialization;

namespace AlarmaApp.Models;

public class SavedRoute
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    // [Column("Name")] keeps the existing DB column name for zero-data-loss migration.
    // [JsonPropertyName("Name")] keeps backup file compatibility with the old field name.
    [Column("Name")]
    [JsonPropertyName("Name")]
    public string DisplayName { get; set; } = string.Empty;

    public string FullAddress { get; set; } = string.Empty;

    [Column("DestinationLatitude")]
    [JsonPropertyName("DestinationLatitude")]
    public double Latitude { get; set; }

    [Column("DestinationLongitude")]
    [JsonPropertyName("DestinationLongitude")]
    public double Longitude { get; set; }

    public DateTime CreatedAt { get; set; }
}
