using System.Text.Json;
using AlarmaApp.Models;

namespace AlarmaApp.Services;

// Turns an ActiveTripState into the JSON blob we stash in Preferences and back again. The read side fails
// closed: anything we can't fully trust — malformed JSON, a blob from an older schema, a destination that
// isn't a real on-globe coordinate, a nonsense distance or stage — comes back as null so a corrupt blob
// can never resurrect a ghost trip that fires alarms in the wrong place.
public static class ActiveTripStateCodec
{
    // Matches BackupService's sanity ceiling — no real commute integrates to more than 1,000 km of path.
    private const double MaxDistanceMeters = 1_000_000;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string Serialize(ActiveTripState state) => JsonSerializer.Serialize(state, Options);

    public static ActiveTripState? TryDeserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        ActiveTripState? state;
        try
        {
            state = JsonSerializer.Deserialize<ActiveTripState>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (state is null || state.Schema != ActiveTripState.CurrentSchema)
            return null;

        if (!IsFiniteCoordinate(state.DestinationLatitude, state.DestinationLongitude))
            return null;

        if (double.IsNaN(state.TotalDistanceMeters)
            || state.TotalDistanceMeters < 0
            || state.TotalDistanceMeters > MaxDistanceMeters)
            return null;

        if (state.CurrentAlarmStage is < 0 or > 3 || state.MaxAlarmStageReached is < 0 or > 3)
            return null;

        return state;
    }

    private static bool IsFiniteCoordinate(double lat, double lon) =>
        double.IsFinite(lat) && double.IsFinite(lon)
        && lat is >= -90 and <= 90
        && lon is >= -180 and <= 180;
}
