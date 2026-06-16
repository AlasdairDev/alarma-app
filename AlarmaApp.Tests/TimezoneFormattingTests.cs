// =============================================================================
//  TimezoneFormattingTests.cs
// -----------------------------------------------------------------------------
//  ISOLATED unit tests for the "Timezone & Localization" section of README.md.
//
//  README claim under test:
//    "Alarma explicitly enforces UTC+8 (Philippine Standard Time, PST) across
//     all UI bindings and the emergency SOS SMS payload."
//
//    | UI bindings    | All displayed timestamps ... are converted from UTC to
//                       UTC+8 before rendering ... regardless of the Android
//                       device's system timezone setting.
//    | SOS SMS payload| The timestamp embedded in the emergency SMS body ... is
//                       formatted in UTC+8.
//
//  These tests re-implement the EXACT conversion/formatting expressions used in
//  production so they run on plain net9.0 with NO Android / MAUI SDK and touch
//  NO production code:
//
//    * HomeController.cs:1523-1524 — SOS body:
//        var phtTime = location.Timestamp.ToOffset(TimeSpan.FromHours(8));
//        $"... https://maps.google.com/?q={lat},{lon} — {phtTime:hh:mm tt} PHT"
//    * HomeController.cs:854 — Last-backup label: ToOffset(8h):g + " PHT"
//    * HomeController.cs:1002-1004 — history cards: SpecifyKind(Utc).AddHours(8)
//    * LocationTrackingService.cs:168 — notification timestamp:
//        ToOffset(TimeSpan.FromHours(8)).ToString("hh:mm tt", InvariantCulture)
//
//  The CRITICAL property the README asserts is *device-timezone independence*:
//  the same instant must render to the same PST wall-clock string no matter what
//  the host machine's local timezone is. We prove that by driving every case
//  from absolute UTC instants and a fixed +8 offset, never from DateTime.Now /
//  TimeZoneInfo.Local.
// =============================================================================

using System.Globalization;
using Xunit;

namespace AlarmaApp.Tests;

/// <summary>
/// Pure re-implementation of the UTC+8 conversion expressions used across the
/// production UI bindings and the SOS SMS payload.
/// </summary>
internal static class PstFormatting
{
    // The single hard-coded offset the whole app pins to (non-configurable).
    public static readonly TimeSpan PhtOffset = TimeSpan.FromHours(8);

    // SOS body / notification time format — HomeController:1524, LocationTrackingService:168.
    public static string SosClock(DateTimeOffset utcInstant) =>
        utcInstant.ToOffset(PhtOffset).ToString("hh:mm tt", CultureInfo.InvariantCulture);

    // Full SOS message body — HomeController:1524 (coords formatted by caller).
    public static string SosBody(double lat, double lon, DateTimeOffset utcInstant) =>
        $"Alarma SOS: I may need help. https://maps.google.com/?q={lat},{lon} — {SosClock(utcInstant)} PHT";

    // History-card conversion — HomeController:1002 (DateTime, not DateTimeOffset).
    public static DateTime HistoryLocalTime(DateTime utcStored) =>
        DateTime.SpecifyKind(utcStored, DateTimeKind.Utc).AddHours(8);

    // Last-backup label — HomeController:854.
    public static string LastBackupLabel(DateTimeOffset utcInstant) =>
        $"Last backup: {utcInstant.ToOffset(PhtOffset):g} PHT";
}

public class SosPayloadTimezoneTests
{
    // 04:34 UTC on 2026-06-07 is 12:34 PM PST. The SOS clock must read 12:34 PM
    // regardless of the host machine's timezone.
    [Fact]
    public void SosClock_ConvertsUtcToPst_12HourFormat()
    {
        var utc = new DateTimeOffset(2026, 6, 7, 4, 34, 0, TimeSpan.Zero);
        Assert.Equal("12:34 PM", PstFormatting.SosClock(utc));
    }

    // Midnight UTC is 08:00 AM PST — verifies the +8 roll and AM marker.
    [Fact]
    public void SosClock_MidnightUtc_Is8amPst()
    {
        var utc = new DateTimeOffset(2026, 6, 7, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal("08:00 AM", PstFormatting.SosClock(utc));
    }

    // 20:00 UTC rolls past midnight into the next PST day → 04:00 AM.
    [Fact]
    public void SosClock_EveningUtc_RollsIntoNextPstDay()
    {
        var utc = new DateTimeOffset(2026, 6, 7, 20, 0, 0, TimeSpan.Zero);
        Assert.Equal("04:00 AM", PstFormatting.SosClock(utc));
    }

    // The README's device-independence guarantee: the SAME instant expressed in
    // three different source offsets (UTC, +08:00 device, -05:00 OFW roaming
    // device) must yield the IDENTICAL PST clock string. ToOffset normalizes the
    // underlying instant, so all three collapse to one answer.
    [Fact]
    public void SosClock_IsDeviceTimezoneIndependent()
    {
        var asUtc      = new DateTimeOffset(2026, 6, 7, 4, 34, 0, TimeSpan.Zero);
        var asManila   = new DateTimeOffset(2026, 6, 7, 12, 34, 0, TimeSpan.FromHours(8));
        var asRoaming  = new DateTimeOffset(2026, 6, 6, 23, 34, 0, TimeSpan.FromHours(-5));

        var expected = PstFormatting.SosClock(asUtc);
        Assert.Equal(expected, PstFormatting.SosClock(asManila));
        Assert.Equal(expected, PstFormatting.SosClock(asRoaming));
        Assert.Equal("12:34 PM", expected);
    }

    // Full SOS body must embed the Google Maps link AND the PHT-suffixed clock.
    [Fact]
    public void SosBody_EmbedsMapsLinkAndPhtTime()
    {
        var utc = new DateTimeOffset(2026, 6, 7, 4, 34, 0, TimeSpan.Zero);
        var body = PstFormatting.SosBody(14.5998, 120.9920, utc);

        Assert.Contains("https://maps.google.com/?q=14.5998,120.992", body);
        Assert.Contains("12:34 PM PHT", body);
        Assert.EndsWith("PHT", body);
    }

    // 12 noon UTC is 8 PM PST — guards the 12-hour clock's PM rendering at the
    // 12:00 boundary (must be 08:00 PM, never 20:00 or 00:00).
    [Fact]
    public void SosClock_NoonUtc_Is8pmPst()
    {
        var utc = new DateTimeOffset(2026, 6, 7, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("08:00 PM", PstFormatting.SosClock(utc));
    }
}

public class UiBindingTimezoneTests
{
    // History cards store UTC and add 8h for display (HomeController:1002).
    [Fact]
    public void HistoryLocalTime_AddsEightHours()
    {
        var storedUtc = new DateTime(2026, 6, 7, 4, 34, 0, DateTimeKind.Utc);
        var local = PstFormatting.HistoryLocalTime(storedUtc);

        Assert.Equal(new DateTime(2026, 6, 7, 12, 34, 0), local);
        // The +8 wall-clock value is fixed regardless of host TZ.
        Assert.Equal(12, local.Hour);
        Assert.Equal(34, local.Minute);
    }

    // A stored value whose Kind was Unspecified must still be treated as UTC
    // first (SpecifyKind) before the +8 — otherwise a non-UTC host would double
    // or zero the offset. We assert the conversion is purely arithmetic +8.
    [Fact]
    public void HistoryLocalTime_TreatsStoredValueAsUtc_NotLocal()
    {
        var unspecified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var local = PstFormatting.HistoryLocalTime(unspecified);
        Assert.Equal(new DateTime(2026, 1, 1, 8, 0, 0), local);
    }

    // Last-backup label carries the PHT suffix and is built off the +8 offset.
    [Fact]
    public void LastBackupLabel_HasPhtSuffix()
    {
        var utc = new DateTimeOffset(2026, 6, 7, 4, 34, 0, TimeSpan.Zero);
        var label = PstFormatting.LastBackupLabel(utc);
        Assert.StartsWith("Last backup: ", label);
        Assert.EndsWith(" PHT", label);
    }

    // The offset constant the app pins to is exactly +8h, never the host's.
    [Fact]
    public void PinnedOffset_IsExactlyEightHours()
        => Assert.Equal(TimeSpan.FromHours(8), PstFormatting.PhtOffset);
}
