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

    public int MaxAlarmStageReached { get; set; }

    public int SnoozeCount { get; set; }

    public bool HasAlarmStage => MaxAlarmStageReached > 0;

    public string AlarmStageBadgeText => MaxAlarmStageReached > 0
        ? $"Alarm: Stage {MaxAlarmStageReached}"
        : string.Empty;

    public string DistanceKmText => DistanceMeters >= 1000
        ? $"{DistanceMeters / 1000:F1} km"
        : $"{DistanceMeters:F0} m";

    public string DurationText
    {
        get
        {
            if (!EndedAt.HasValue) return "In progress";
            var minutes = (EndedAt.Value - StartedAt).TotalMinutes;
            return minutes < 1 ? "< 1 min" : $"{minutes:F0} min";
        }
    }
}
