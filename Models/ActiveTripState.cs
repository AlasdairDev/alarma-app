namespace AlarmaApp.Models;

// Everything needed to bring a live trip back after the app process is killed mid-commute. The tracking
// service is Sticky so Android restarts it, but the foreground controller is rebuilt from scratch with no
// destination and a fresh alarm state machine — which is how a task-killed phone could resume "tracking"
// yet never fire the wake-up. We write one of these to Preferences as the trip moves and replay it on
// launch so the destination, accumulated distance, and the alarm latches all survive the kill.
public sealed class ActiveTripState
{
    // Bump this if the shape changes so an old blob from a previous app version is ignored rather than
    // half-read into a trip that fires (or skips) alarms wrongly.
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;

    // Re-links the recovered trip back to its existing TripHistory row so the resumed trip keeps writing
    // to the same record instead of orphaning it and starting a duplicate.
    public int TripHistoryId { get; set; }
    public DateTime StartedAtUtc { get; set; }

    public double DestinationLatitude { get; set; }
    public double DestinationLongitude { get; set; }
    public string? DestinationName { get; set; }

    public double TotalDistanceMeters { get; set; }

    // CurrentAlarmStage reflects a dismissal back to None (Slide-to-Stop resets it without ending the
    // trip), while MaxAlarmStageReached remembers the highest rung we ever hit for the history summary.
    public int CurrentAlarmStage { get; set; }
    public int MaxAlarmStageReached { get; set; }

    // The arrival/overshoot latches. Restoring these is the whole point: an arrival or overshoot the rider
    // already saw must NOT re-fire just because the process died and came back.
    public bool HasArrived { get; set; }
    public bool OvershootAlerted { get; set; }
    public bool OvershootConfirmed { get; set; }
}
