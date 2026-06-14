using SQLite;
using System.Text.Json.Serialization;

namespace AlarmaApp.Models;

public class SavedRoute
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    // keep the old "Name" column + json field so existing dbs and backups still load
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
