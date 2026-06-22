namespace AlarmaApp.Services;

// What actually happened to each emergency contact when an SOS fired. SmsManager queues the text to
// the radio, but "queued" isn't "sent" — the radio reports back per message through a sent-broadcast,
// and that result code is the only thing that tells us a contact really got the SOS. We collect those
// receipts into one of these so the rider sees a concrete "2 of 2" instead of a hopeful "SOS sent".
public enum SosRecipientStatus
{
    Sent,
    Failed
}

public readonly record struct SosRecipientResult(string Number, SosRecipientStatus Status);

// The aggregated outcome of one SOS dispatch, plus the single line the rider reads afterwards. Kept free
// of any Android types on purpose so the decision logic stays unit-testable off-device.
public sealed class SosDeliveryResult
{
    public IReadOnlyList<SosRecipientResult> Recipients { get; }

    // True when at least one contact couldn't be confirmed sent and we handed those numbers to the
    // messaging app pre-filled. That hand-off is the rider's one-tap retry path.
    public bool OpenedMessagingApp { get; }

    public SosDeliveryResult(IReadOnlyList<SosRecipientResult>? recipients, bool openedMessagingApp = false)
    {
        Recipients = recipients ?? Array.Empty<SosRecipientResult>();
        OpenedMessagingApp = openedMessagingApp;
    }

    public int Total => Recipients.Count;
    public int SentCount => Recipients.Count(r => r.Status == SosRecipientStatus.Sent);
    public int FailedCount => Total - SentCount;

    public bool AllSent => Total > 0 && FailedCount == 0;
    public bool AllFailed => Total > 0 && SentCount == 0;
    public bool PartialSuccess => SentCount > 0 && FailedCount > 0;

    // Same result, now flagged as having opened the messaging app for the failed numbers.
    public SosDeliveryResult WithMessagingAppFallback() => new(Recipients, openedMessagingApp: true);

    // The one line surfaced to the rider. Concrete counts so a partial delivery can't masquerade as a
    // clean success, and a pointer to the Messages hand-off whenever a contact didn't make it.
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

            // Nobody confirmed sent.
            return OpenedMessagingApp
                ? "SOS couldn't send — opening Messages so you can retry."
                : "SOS failed to send. Open Messages and try again.";
        }
    }

    private static string Plural(int count) => count == 1 ? "" : "s";
}
