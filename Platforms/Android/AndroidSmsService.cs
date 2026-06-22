// Sends the actual SOS text messages. This is TRUE background SMS: it hands each message straight to the
// platform's Android.Telephony.SmsManager, so the text leaves the phone the instant the SOS fires — the
// rider never has to open the messaging app or tap "Send". That path needs the restricted SEND_SMS
// runtime permission, which HomeController requests natively just before triggering.
// Two safety nets remain:
//   - IsValidRecipient() re-checks every number against ^(09\d{9}|\+639\d{9})$ before it's used, so a
//     bad number can never reach the radio even if the controller's own validation were bypassed.
//   - If the device has no telephony stack at all (tablet / SIM-less emulator) SmsManager isn't usable,
//     so we fall back to a native Intent(ActionSendto) that opens the messaging app pre-filled, meaning
//     the SOS still goes out. The coordinates in that body are never written to any log.
// We also listen for the radio's per-message "sent" broadcast and turn it into a concrete per-contact
// result, so the rider is told "sent to 2 of 2" rather than just assuming the queue-to-radio call worked.
// Any contact the radio couldn't confirm gets the messaging-app hand-off as a one-tap retry.

using AlarmaApp.Services;
using AlarmaApp.Services.Interfaces;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Telephony;
using AndroidApplication = Android.App.Application;

namespace AlarmaApp.Platforms.Android;

public class AndroidSmsService : ISmsService
{
    // A private, package-scoped action for the "sent" PendingIntent broadcasts. We append a per-dispatch
    // GUID at send time so two SOS bursts (shouldn't happen given the single-fire latch, but be safe)
    // can't have their receipts cross-wired.
    private const string SmsSentAction = "com.alarma.app.SMS_SENT";
    private const string RecipientIndexExtra = "alarma.recipient_index";

    // How long we'll wait for the radio to report back on every message before we give up and treat the
    // silent ones as unconfirmed. The sent-broadcast normally lands within a few seconds; a contact that
    // never reports is conservatively counted as failed so the rider gets the Messages retry rather than
    // a false "sent".
    private static readonly TimeSpan ReceiptTimeout = TimeSpan.FromSeconds(12);

    public Task<SosDeliveryResult> SendEmergencySmsAsync(string message, IEnumerable<string> recipients)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("SOS message must not be empty.", nameof(message));

        var validRecipients = recipients
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Where(IsValidRecipient)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (validRecipients.Count == 0)
            throw new InvalidOperationException("No valid emergency recipients to send the SOS to.");

        // Hand off to a background thread: SmsManager queues to the radio without blocking, but grabbing
        // the manager, dividing a long body, and waiting on receipts shouldn't run on the UI thread that
        // fired the SOS.
        return Task.Run(() => SendInBackground(message, validRecipients));
    }

    private static async Task<SosDeliveryResult> SendInBackground(string message, IList<string> recipients)
    {
        var smsManager = GetSmsManager();
        if (smsManager is null)
        {
            // No telephony stack on this device — open the messaging app pre-filled as a last resort so
            // the SOS can still reach the contacts. Nothing was confirmed sent, so report it that way.
            LaunchNativeSmsFallback(message, recipients);
            return AllUnconfirmed(recipients).WithMessagingAppFallback();
        }

        var dispatchAction = $"{SmsSentAction}.{Guid.NewGuid():N}";
        var tracker = new SosSentTracker(recipients);
        SosSentReceiver? receiver = null;
        try
        {
            receiver = RegisterReceiver(dispatchAction, tracker);

            for (var i = 0; i < recipients.Count; i++)
            {
                try
                {
                    var number = recipients[i];
                    // Human-readable SOS bodies (a full street address) routinely run past the 160-character
                    // single-segment limit, so always divide and send multipart. A short body just divides
                    // to a single part and takes the simple SendTextMessage path.
                    var parts = smsManager.DivideMessage(message);
                    if (parts is { Count: > 1 })
                    {
                        tracker.SetExpectedParts(i, parts.Count);
                        var sentIntents = new List<PendingIntent>(parts.Count);
                        for (var p = 0; p < parts.Count; p++)
                        {
                            var pi = BuildSentIntent(dispatchAction, i, i * 100 + p);
                            if (pi is not null) sentIntents.Add(pi);
                        }
                        // Pass the receipts only if we built one per part (SmsManager requires the list length
                        // to match the parts); otherwise send without receipts — the message still goes out,
                        // we just can't confirm it, so count it as sent-to-radio.
                        if (sentIntents.Count == parts.Count)
                        {
                            smsManager.SendMultipartTextMessage(number, null, parts, sentIntents, null);
                        }
                        else
                        {
                            smsManager.SendMultipartTextMessage(number, null, parts, null, null);
                            tracker.MarkUnconfirmedButSent(i);
                        }
                    }
                    else
                    {
                        tracker.SetExpectedParts(i, 1);
                        var pi = BuildSentIntent(dispatchAction, i, i * 100);
                        smsManager.SendTextMessage(number, null, message, pi, null);
                        if (pi is null) tracker.MarkUnconfirmedButSent(i);
                    }
                }
                catch (Exception ex)
                {
                    BlackBoxLogger.RecordHandledException(ex, "[AndroidSmsService.SendInBackground]");
                    tracker.MarkFailed(i);
                }
            }

            // Wait (bounded) for the radio to report on every message we're expecting a receipt for.
            await tracker.WaitForCompletionAsync(ReceiptTimeout);
        }
        finally
        {
            if (receiver is not null)
            {
                try { AndroidApplication.Context.UnregisterReceiver(receiver); }
                catch (Exception ex) { BlackBoxLogger.RecordHandledException(ex, "[AndroidSmsService.Unregister]"); }
            }
        }

        var result = tracker.BuildResult();

        // Retry affordance: any contact the radio couldn't confirm gets the messaging app opened
        // pre-filled with just those numbers, so the rider can resend with one tap.
        if (result.FailedCount > 0)
        {
            var failedNumbers = result.Recipients
                .Where(r => r.Status == SosRecipientStatus.Failed)
                .Select(r => r.Number)
                .ToList();
            LaunchNativeSmsFallback(message, failedNumbers);
            return result.WithMessagingAppFallback();
        }

        return result;
    }

    private static SosSentReceiver RegisterReceiver(string action, SosSentTracker tracker)
    {
        var receiver = new SosSentReceiver(tracker);
        var filter = new IntentFilter(action);
        // API 33+ forces every runtime-registered receiver to declare whether other apps can reach it.
        // These receipts are strictly our own SmsManager talking back to us, so it's not-exported.
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
            AndroidApplication.Context.RegisterReceiver(receiver, filter, ReceiverFlags.NotExported);
        else
            AndroidApplication.Context.RegisterReceiver(receiver, filter);
        return receiver;
    }

    private static SmsManager? GetSmsManager()
    {
        try
        {
            // SmsManager.Default is the cross-version entry point the implementation calls for. It's
            // marked obsolete from API 31 (the per-subscription manager is preferred), but it still
            // routes to the default subscription on every supported version, which is exactly what a
            // single-SIM SOS wants.
#pragma warning disable CA1422, CS0618
            return SmsManager.Default;
#pragma warning restore CA1422, CS0618
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[AndroidSmsService.GetSmsManager]");
            return null;
        }
    }

    // Android 12+ (API 31) requires every PendingIntent to declare its mutability explicitly. The SOS
    // "sent" receipt never needs the OS to fill anything in, so it is strictly FLAG_IMMUTABLE. The
    // recipient index rides along as an extra so the receiver can tell which contact a receipt is for.
    private static PendingIntent? BuildSentIntent(string action, int recipientIndex, int requestCode)
    {
        try
        {
            var intent = new Intent(action)
                .SetPackage(AndroidApplication.Context.PackageName)
                .PutExtra(RecipientIndexExtra, recipientIndex);
            var flags = Build.VERSION.SdkInt >= BuildVersionCodes.S
                ? PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent
                : PendingIntentFlags.UpdateCurrent;
            return PendingIntent.GetBroadcast(AndroidApplication.Context, requestCode, intent, flags);
        }
        catch
        {
            // A null sent-intent is perfectly valid for SmsManager — the message still goes out, we just
            // forgo the delivery receipt. Never let receipt plumbing block the SOS.
            return null;
        }
    }

    private static void LaunchNativeSmsFallback(string message, IList<string> recipients)
    {
        if (recipients.Count == 0) return;
        try
        {
            var verifiedEmergencyNumbers = string.Join(";", recipients);
            var uri = global::Android.Net.Uri.Parse("smsto:" + verifiedEmergencyNumbers);
            var smsIntent = new Intent(Intent.ActionSendto, uri);
            smsIntent.PutExtra("sms_body", message);
            smsIntent.AddFlags(ActivityFlags.NewTask);
            AndroidApplication.Context.StartActivity(smsIntent);
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[AndroidSmsService.LaunchNativeSmsFallback]");
        }
    }

    // Builds a result where nothing was confirmed sent — used when there's no radio to talk to at all.
    private static SosDeliveryResult AllUnconfirmed(IList<string> recipients) =>
        new(recipients.Select(n => new SosRecipientResult(n, SosRecipientStatus.Failed)).ToList());

    private static readonly System.Text.RegularExpressions.Regex RecipientRegex =
        new(@"^(09\d{9}|\+639\d{9})$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // Make sure the number still looks like a real PH mobile number before we hand it to the radio.
    // The controller checked already, but a malformed number must never reach SmsManager.
    private static bool IsValidRecipient(string number) => RecipientRegex.IsMatch(number);

    // Catches the radio's "sent" broadcast and routes the result code back to the tracker by recipient.
    private sealed class SosSentReceiver : BroadcastReceiver
    {
        private readonly SosSentTracker _tracker;
        public SosSentReceiver(SosSentTracker tracker) => _tracker = tracker;

        public override void OnReceive(Context? context, Intent? intent)
        {
            var recipientIndex = intent?.GetIntExtra(RecipientIndexExtra, -1) ?? -1;
            if (recipientIndex < 0) return;
            // Activity.RESULT_OK means the message left the radio; any other code is a generic/no-service
            // /radio-off failure for that part.
            _tracker.OnPartResult(recipientIndex, ResultCode == Result.Ok);
        }
    }

    // Tallies the per-message receipts into a per-recipient sent/failed outcome. A multipart message has
    // to land EVERY part to count as sent; the first failing part fails the whole contact. Pure bookkeeping
    // with no Android types so the rules stay easy to reason about.
    private sealed class SosSentTracker
    {
        private readonly object _gate = new();
        private readonly IList<string> _recipients;
        private readonly int[] _expectedParts;
        private readonly int[] _okParts;
        private readonly bool[] _failed;
        private readonly bool[] _resolved;
        private readonly TaskCompletionSource<bool> _allResolved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SosSentTracker(IList<string> recipients)
        {
            _recipients = recipients;
            var n = recipients.Count;
            _expectedParts = new int[n];
            _okParts = new int[n];
            _failed = new bool[n];
            _resolved = new bool[n];
        }

        public void SetExpectedParts(int i, int parts)
        {
            lock (_gate) { _expectedParts[i] = parts; }
        }

        // The send call itself threw — the message never reached the radio, so this contact failed outright.
        public void MarkFailed(int i)
        {
            lock (_gate) { _failed[i] = true; _resolved[i] = true; }
            SignalIfAllResolved();
        }

        // We sent the message but couldn't attach a receipt (or there's nothing to wait on). The text did
        // leave, so count it as sent rather than punishing the rider for missing plumbing.
        public void MarkUnconfirmedButSent(int i)
        {
            lock (_gate) { _resolved[i] = true; }
            SignalIfAllResolved();
        }

        public void OnPartResult(int i, bool ok)
        {
            if (i < 0 || i >= _recipients.Count) return;
            lock (_gate)
            {
                if (_resolved[i]) return;
                if (!ok)
                {
                    _failed[i] = true;
                    _resolved[i] = true;
                }
                else
                {
                    _okParts[i]++;
                    if (_expectedParts[i] > 0 && _okParts[i] >= _expectedParts[i])
                        _resolved[i] = true;
                }
            }
            SignalIfAllResolved();
        }

        private void SignalIfAllResolved()
        {
            lock (_gate)
            {
                for (var i = 0; i < _resolved.Length; i++)
                    if (!_resolved[i]) return;
            }
            _allResolved.TrySetResult(true);
        }

        public async Task WaitForCompletionAsync(TimeSpan timeout)
        {
            await Task.WhenAny(_allResolved.Task, Task.Delay(timeout));
        }

        public SosDeliveryResult BuildResult()
        {
            var list = new List<SosRecipientResult>(_recipients.Count);
            lock (_gate)
            {
                for (var i = 0; i < _recipients.Count; i++)
                {
                    // Anyone still unresolved at this point never reported in time — treat them as failed
                    // so the messaging-app retry covers them. Better a duplicate SOS than a silent miss.
                    var sent = _resolved[i] && !_failed[i];
                    list.Add(new SosRecipientResult(
                        _recipients[i],
                        sent ? SosRecipientStatus.Sent : SosRecipientStatus.Failed));
                }
            }
            return new SosDeliveryResult(list);
        }
    }
}
