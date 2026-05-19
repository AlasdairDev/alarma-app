using SQLite;

namespace AlarmaApp.Models;

public class TripHistory
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? EndedAt { get; set; }

    public string? DestinationName { get; set; }

    public double? DestinationLatitude { get; set; }

    public double? DestinationLongitude { get; set; }

    public double DistanceMeters { get; set; }

    public bool OvershootDetected { get; set; }

    public string? Summary { get; set; }
}
