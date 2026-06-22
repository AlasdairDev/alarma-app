// =============================================================================
//  SosDeliveryResultTests.cs
// -----------------------------------------------------------------------------
//  Tests for the SOS delivery-result aggregation: turning the radio's per-message
//  "sent" receipts into a per-contact outcome and the single line the rider reads.
//
//  Self-contained per project convention (see SosGeocodingTests.cs): the real
//  AlarmaApp.Services.SosDeliveryResult and the AndroidSmsService receipt tracker
//  live in the net9.0-android app project, which this net9.0 test project can't
//  reference, so the rules and wording are mirrored here. Keep the two in
//  lock-step — the strings are asserted verbatim against production.
// =============================================================================

using Xunit;

namespace AlarmaApp.Tests;

// ── Mirrors of the production surface ────────────────────────────────────────

internal enum SosStatusSpec { Sent, Failed }

internal readonly record struct SosRecipientSpec(string Number, SosStatusSpec Status);

/// <summary>Mirror of AlarmaApp.Services.SosDeliveryResult (aggregation + rider summary).</summary>
internal sealed class SosDeliverySpec
{
    public IReadOnlyList<SosRecipientSpec> Recipients { get; }
    public bool OpenedMessagingApp { get; }

    public SosDeliverySpec(IReadOnlyList<SosRecipientSpec> recipients, bool openedMessagingApp = false)
    {
        Recipients = recipients;
        OpenedMessagingApp = openedMessagingApp;
    }

    public int Total => Recipients.Count;
    public int SentCount => Recipients.Count(r => r.Status == SosStatusSpec.Sent);
    public int FailedCount => Total - SentCount;
    public bool AllSent => Total > 0 && FailedCount == 0;
    public bool AllFailed => Total > 0 && SentCount == 0;
    public bool PartialSuccess => SentCount > 0 && FailedCount > 0;

    public SosDeliverySpec WithMessagingAppFallback() => new(Recipients, openedMessagingApp: true);

    public string RiderSummary
    {
        get
        {
            if (Total == 0)
                return "No emergency contacts to send the SOS to.";
            if (AllSent)
                return $"SOS sent to {SentCount} of {Total} contact{Plural(Total)}.";
            if (PartialSuccess)
                return OpenedMessagingApp
                    ? $"SOS sent to {SentCount} of {Total} — opening Messages for the rest."
                    : $"SOS sent to {SentCount} of {Total} contact{Plural(Total)}.";
            return OpenedMessagingApp
                ? "SOS couldn't send — opening Messages so you can retry."
                : "SOS failed to send. Open Messages and try again.";
        }
    }

    private static string Plural(int count) => count == 1 ? "" : "s";
}

/// <summary>Mirror of AndroidSmsService.SosSentTracker — the per-part receipt tally.
/// A multipart message must land every part to count as sent; the first failing part
/// fails the whole contact; an unresolved contact at build time is treated as failed.</summary>
internal sealed class SosSentTallySpec
{
    private readonly string[] _recipients;
    private readonly int[] _expected;
    private readonly int[] _ok;
    private readonly bool[] _failed;
    private readonly bool[] _resolved;

    public SosSentTallySpec(params string[] recipients)
    {
        _recipients = recipients;
        var n = recipients.Length;
        _expected = new int[n];
        _ok = new int[n];
        _failed = new bool[n];
        _resolved = new bool[n];
    }

    public void SetExpectedParts(int i, int parts) => _expected[i] = parts;
    public void MarkFailed(int i) { _failed[i] = true; _resolved[i] = true; }
    public void MarkUnconfirmedButSent(int i) => _resolved[i] = true;

    public void OnPartResult(int i, bool ok)
    {
        if (_resolved[i]) return;
        if (!ok) { _failed[i] = true; _resolved[i] = true; }
        else
        {
            _ok[i]++;
            if (_expected[i] > 0 && _ok[i] >= _expected[i]) _resolved[i] = true;
        }
    }

    public SosDeliverySpec BuildResult()
    {
        var list = new List<SosRecipientSpec>(_recipients.Length);
        for (var i = 0; i < _recipients.Length; i++)
        {
            var sent = _resolved[i] && !_failed[i];
            list.Add(new SosRecipientSpec(_recipients[i], sent ? SosStatusSpec.Sent : SosStatusSpec.Failed));
        }
        return new SosDeliverySpec(list);
    }
}

// ── Tests ────────────────────────────────────────────────────────────────────

public class SosDeliveryAggregationTests
{
    private static SosDeliverySpec Result(bool fallback, params SosStatusSpec[] statuses)
    {
        var recipients = statuses
            .Select((s, idx) => new SosRecipientSpec($"0917000000{idx}", s))
            .ToList();
        var r = new SosDeliverySpec(recipients);
        return fallback ? r.WithMessagingAppFallback() : r;
    }

    [Fact]
    public void AllSent_ReportsCleanCount()
    {
        var r = Result(fallback: false, SosStatusSpec.Sent, SosStatusSpec.Sent);

        Assert.True(r.AllSent);
        Assert.False(r.PartialSuccess);
        Assert.Equal(2, r.SentCount);
        Assert.Equal(0, r.FailedCount);
        Assert.Equal("SOS sent to 2 of 2 contacts.", r.RiderSummary);
    }

    [Fact]
    public void SingleContactSent_UsesSingularNoun()
    {
        var r = Result(fallback: false, SosStatusSpec.Sent);

        Assert.Equal("SOS sent to 1 of 1 contact.", r.RiderSummary);
    }

    [Fact]
    public void PartialSuccess_WithFallback_PointsToMessagesForTheRest()
    {
        var r = Result(fallback: true, SosStatusSpec.Sent, SosStatusSpec.Failed);

        Assert.True(r.PartialSuccess);
        Assert.Equal(1, r.SentCount);
        Assert.Equal(1, r.FailedCount);
        Assert.Equal("SOS sent to 1 of 2 — opening Messages for the rest.", r.RiderSummary);
    }

    [Fact]
    public void AllFailed_WithFallback_OffersRetry()
    {
        var r = Result(fallback: true, SosStatusSpec.Failed, SosStatusSpec.Failed);

        Assert.True(r.AllFailed);
        Assert.Equal(0, r.SentCount);
        Assert.Equal("SOS couldn't send — opening Messages so you can retry.", r.RiderSummary);
    }

    [Fact]
    public void AllFailed_WithoutFallback_TellsUserToOpenMessages()
    {
        var r = Result(fallback: false, SosStatusSpec.Failed);

        Assert.Equal("SOS failed to send. Open Messages and try again.", r.RiderSummary);
    }

    [Fact]
    public void NoRecipients_ReportsNothingToSend()
    {
        var r = new SosDeliverySpec(new List<SosRecipientSpec>());

        Assert.Equal("No emergency contacts to send the SOS to.", r.RiderSummary);
    }
}

public class SosSentTrackerTests
{
    // A single-part message confirmed OK by the radio counts as sent.
    [Fact]
    public void SinglePart_OkReceipt_IsSent()
    {
        var t = new SosSentTallySpec("09170000000");
        t.SetExpectedParts(0, 1);
        t.OnPartResult(0, ok: true);

        Assert.Equal(SosStatusSpec.Sent, t.BuildResult().Recipients[0].Status);
    }

    // A multipart message must land every part before it counts as sent.
    [Fact]
    public void Multipart_AllPartsOk_IsSent()
    {
        var t = new SosSentTallySpec("09170000000");
        t.SetExpectedParts(0, 3);
        t.OnPartResult(0, true);
        t.OnPartResult(0, true);
        Assert.Equal(SosStatusSpec.Failed, t.BuildResult().Recipients[0].Status); // not all parts yet
        t.OnPartResult(0, true);

        Assert.Equal(SosStatusSpec.Sent, t.BuildResult().Recipients[0].Status);
    }

    // The first failing part fails the whole contact, and a later OK can't revive it.
    [Fact]
    public void Multipart_OneFailingPart_FailsContact()
    {
        var t = new SosSentTallySpec("09170000000");
        t.SetExpectedParts(0, 2);
        t.OnPartResult(0, ok: false);
        t.OnPartResult(0, ok: true);

        Assert.Equal(SosStatusSpec.Failed, t.BuildResult().Recipients[0].Status);
    }

    // A send that threw before reaching the radio is a hard failure.
    [Fact]
    public void ThrownSend_IsFailed()
    {
        var t = new SosSentTallySpec("09170000000");
        t.MarkFailed(0);

        Assert.Equal(SosStatusSpec.Failed, t.BuildResult().Recipients[0].Status);
    }

    // No receipt could be attached, but the text left — counted as sent, not punished.
    [Fact]
    public void UnconfirmedButSent_IsSent()
    {
        var t = new SosSentTallySpec("09170000000");
        t.MarkUnconfirmedButSent(0);

        Assert.Equal(SosStatusSpec.Sent, t.BuildResult().Recipients[0].Status);
    }

    // A contact the radio never reported on (silent at build time) is conservatively failed so the
    // messaging-app retry covers it.
    [Fact]
    public void NeverReported_IsFailed()
    {
        var t = new SosSentTallySpec("09170000000");
        t.SetExpectedParts(0, 1);

        Assert.Equal(SosStatusSpec.Failed, t.BuildResult().Recipients[0].Status);
    }

    // Mixed batch: one confirmed, one failed → aggregates to a partial result.
    [Fact]
    public void MixedBatch_AggregatesToPartial()
    {
        var t = new SosSentTallySpec("09170000000", "09170000001");
        t.SetExpectedParts(0, 1);
        t.SetExpectedParts(1, 1);
        t.OnPartResult(0, true);
        t.OnPartResult(1, false);

        var result = t.BuildResult();
        Assert.True(result.PartialSuccess);
        Assert.Equal(1, result.SentCount);
        Assert.Equal(1, result.FailedCount);
    }
}
