using SQLite;

namespace AlarmaApp.Models;

public class SavedRoute
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public double DestinationLatitude { get; set; }

    public double DestinationLongitude { get; set; }

    public string? Notes { get; set; }
}
