// =============================================================================
//  ActiveTripRecoveryTests.cs
// -----------------------------------------------------------------------------
//  Tests for persisting and recovering the live trip across a process kill:
//  the JSON round-trip, the fail-closed validation, and the latch rules that
//  stop a recovered trip from re-firing an alarm the rider already handled.
//
//  Self-contained per project convention (see SosGeocodingTests.cs): the real
//  AlarmaApp.Models.ActiveTripState and AlarmaApp.Services.ActiveTripStateCodec
//  live in the net9.0-android app project this net9.0 test project can't
//  reference, so the model + codec rules are mirrored here. Keep them in
//  lock-step. The restore-without-re-fire predicates mirror the gates in
//  HomeController.HandleDestinationDistanceAsync.
// =============================================================================

using System.Text.Json;
using Xunit;

namespace AlarmaApp.Tests;

// ── Mirror of AlarmaApp.Models.ActiveTripState ───────────────────────────────
internal sealed class ActiveTripStateSpec
{
    public const int CurrentSchema = 1;
    public int Schema { get; set; } = CurrentSchema;
    public int TripHistoryId { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public double DestinationLatitude { get; set; }
    public double DestinationLongitude { get; set; }
    public string? DestinationName { get; set; }
    public double TotalDistanceMeters { get; set; }
    public int CurrentAlarmStage { get; set; }
    public int MaxAlarmStageReached { get; set; }
    public bool HasArrived { get; set; }
    public bool OvershootAlerted { get; set; }
    public bool OvershootConfirmed { get; set; }
}

// ── Mirror of AlarmaApp.Services.ActiveTripStateCodec ────────────────────────
internal static class ActiveTripStateCodecSpec
{
    private const double MaxDistanceMeters = 1_000_000;
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static string Serialize(ActiveTripStateSpec state) => JsonSerializer.Serialize(state, Options);

    public static ActiveTripStateSpec? TryDeserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        ActiveTripStateSpec? state;
        try { state = JsonSerializer.Deserialize<ActiveTripStateSpec>(json, Options); }
        catch (JsonException) { return null; }

        if (state is null || state.Schema != ActiveTripStateSpec.CurrentSchema) return null;
        if (!IsFiniteCoordinate(state.DestinationLatitude, state.DestinationLongitude)) return null;
        if (double.IsNaN(state.TotalDistanceMeters)
            || state.TotalDistanceMeters < 0
            || state.TotalDistanceMeters > MaxDistanceMeters) return null;
        if (state.CurrentAlarmStage is < 0 or > 3 || state.MaxAlarmStageReached is < 0 or > 3) return null;
        return state;
    }

    private static bool IsFiniteCoordinate(double lat, double lon) =>
        double.IsFinite(lat) && double.IsFinite(lon)
        && lat is >= -90 and <= 90 && lon is >= -180 and <= 180;
}

// Mirrors the re-fire gates in HomeController: arrival is gated on !HasArrived, overshoot on
// !OvershootAlerted, and a higher stage only fires when it strictly exceeds the current one.
internal static class RecoveryGate
{
    public static bool WouldArrivalRefire(ActiveTripStateSpec s) => !s.HasArrived;
    public static bool WouldOvershootRefire(ActiveTripStateSpec s) => s.HasArrived && !s.OvershootAlerted;
    public static bool WouldStageRefire(ActiveTripStateSpec s, int stage) => stage > s.CurrentAlarmStage;
}

// ── Tests ────────────────────────────────────────────────────────────────────
public class ActiveTripCodecRoundTripTests
{
    private static ActiveTripStateSpec Sample(int stage, bool arrived = false, double distance = 1234.5) => new()
    {
        TripHistoryId = 42,
        StartedAtUtc = new DateTime(2026, 6, 23, 8, 0, 0, DateTimeKind.Utc),
        DestinationLatitude = 14.5995,
        DestinationLongitude = 120.9842,
        DestinationName = "Lawton Terminal",
        TotalDistanceMeters = distance,
        CurrentAlarmStage = stage,
        MaxAlarmStageReached = stage,
        HasArrived = arrived,
        OvershootAlerted = false,
        OvershootConfirmed = false
    };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RoundTrips_AtEveryStage(int stage)
    {
        var arrived = stage == 3;
        var restored = ActiveTripStateCodecSpec.TryDeserialize(
            ActiveTripStateCodecSpec.Serialize(Sample(stage, arrived)));

        Assert.NotNull(restored);
        Assert.Equal(stage, restored!.CurrentAlarmStage);
        Assert.Equal(arrived, restored.HasArrived);
        Assert.Equal(42, restored.TripHistoryId);
        Assert.Equal("Lawton Terminal", restored.DestinationName);
        Assert.Equal(14.5995, restored.DestinationLatitude, 6);
        Assert.Equal(120.9842, restored.DestinationLongitude, 6);
    }

    [Fact]
    public void Distance_CarriesOver_Exactly()
    {
        var restored = ActiveTripStateCodecSpec.TryDeserialize(
            ActiveTripStateCodecSpec.Serialize(Sample(2, distance: 8675.309)));

        Assert.NotNull(restored);
        Assert.Equal(8675.309, restored!.TotalDistanceMeters, 3);
    }
}

public class ActiveTripCodecValidationTests
{
    private static ActiveTripStateSpec Valid() => new()
    {
        DestinationLatitude = 14.5,
        DestinationLongitude = 121.0,
        TotalDistanceMeters = 100,
        CurrentAlarmStage = 1,
        MaxAlarmStageReached = 1
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOrNull_DoesNotResume(string? json)
    {
        // A clean stop wipes the blob to "" — that must read as "no trip to recover".
        Assert.Null(ActiveTripStateCodecSpec.TryDeserialize(json));
    }

    [Fact]
    public void MalformedJson_ReturnsNull()
    {
        Assert.Null(ActiveTripStateCodecSpec.TryDeserialize("{not valid json"));
    }

    [Fact]
    public void WrongSchema_ReturnsNull()
    {
        var s = Valid();
        s.Schema = 99;
        Assert.Null(ActiveTripStateCodecSpec.TryDeserialize(ActiveTripStateCodecSpec.Serialize(s)));
    }

    [Theory]
    [InlineData(91.0, 121.0)]   // latitude past the pole
    [InlineData(-91.0, 121.0)]
    [InlineData(14.0, 200.0)]   // longitude off the globe
    [InlineData(14.0, -200.0)]
    public void OutOfRangeCoordinates_ReturnNull(double lat, double lon)
    {
        var s = Valid();
        s.DestinationLatitude = lat;
        s.DestinationLongitude = lon;
        Assert.Null(ActiveTripStateCodecSpec.TryDeserialize(ActiveTripStateCodecSpec.Serialize(s)));
    }

    [Theory]
    [InlineData(-1.0)]          // negative distance is impossible
    [InlineData(2_000_000.0)]   // above the 1,000 km sanity ceiling
    public void BadDistance_ReturnsNull(double distance)
    {
        var s = Valid();
        s.TotalDistanceMeters = distance;
        Assert.Null(ActiveTripStateCodecSpec.TryDeserialize(ActiveTripStateCodecSpec.Serialize(s)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void StageOutOfRange_ReturnsNull(int stage)
    {
        var s = Valid();
        s.CurrentAlarmStage = stage;
        Assert.Null(ActiveTripStateCodecSpec.TryDeserialize(ActiveTripStateCodecSpec.Serialize(s)));
    }
}

public class ActiveTripRecoveryReFireTests
{
    private static ActiveTripStateSpec Restore(ActiveTripStateSpec s) =>
        ActiveTripStateCodecSpec.TryDeserialize(ActiveTripStateCodecSpec.Serialize(s))!;

    // Killed while only Stage 1/2 had fired: the next stronger stage SHOULD still be able to fire.
    [Fact]
    public void RestoredMidEscalation_StillAllowsNextStage()
    {
        var s = Restore(new ActiveTripStateSpec
        {
            DestinationLatitude = 14.5, DestinationLongitude = 121.0,
            CurrentAlarmStage = 2, MaxAlarmStageReached = 2, HasArrived = false
        });

        Assert.True(RecoveryGate.WouldStageRefire(s, stage: 3)); // Emergency can still escalate
        Assert.True(RecoveryGate.WouldArrivalRefire(s));         // hasn't arrived yet
    }

    // Killed AFTER arrival (Emergency already fired): arrival must not latch again on recovery.
    [Fact]
    public void RestoredAfterArrival_DoesNotRefireArrival()
    {
        var s = Restore(new ActiveTripStateSpec
        {
            DestinationLatitude = 14.5, DestinationLongitude = 121.0,
            CurrentAlarmStage = 3, MaxAlarmStageReached = 3, HasArrived = true
        });

        Assert.False(RecoveryGate.WouldArrivalRefire(s));
        Assert.False(RecoveryGate.WouldStageRefire(s, stage: 3)); // already at Emergency
    }

    // Dismissed Emergency (Slide-to-Stop sets stage back to None but the arrival latch stays): the
    // Emergency arrival still must not re-fire, because HasArrived survived the dismissal.
    [Fact]
    public void RestoredAfterDismissedEmergency_DoesNotRefireArrival()
    {
        var s = Restore(new ActiveTripStateSpec
        {
            DestinationLatitude = 14.5, DestinationLongitude = 121.0,
            CurrentAlarmStage = 0, MaxAlarmStageReached = 3, HasArrived = true
        });

        Assert.False(RecoveryGate.WouldArrivalRefire(s));
        Assert.Equal(3, s.MaxAlarmStageReached); // history still remembers we hit Emergency
    }

    // Killed after an overshoot latched: overshoot must not re-confirm on recovery.
    [Fact]
    public void RestoredAfterOvershoot_DoesNotRefireOvershoot()
    {
        var s = Restore(new ActiveTripStateSpec
        {
            DestinationLatitude = 14.5, DestinationLongitude = 121.0,
            CurrentAlarmStage = 3, MaxAlarmStageReached = 3,
            HasArrived = true, OvershootAlerted = true, OvershootConfirmed = true
        });

        Assert.False(RecoveryGate.WouldOvershootRefire(s));
        Assert.True(s.OvershootConfirmed);
    }
}
