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
    // A private, package-scoped action for the "sent" PendingIntent. Nothing actually listens for it —
    // it only exists so SmsManager has a valid receipt callback to fire — but it must be a real intent.
    private const string SmsSentAction = "com.alarma.app.SMS_SENT";

    public Task SendEmergencySmsAsync(string message, IEnumerable<string> recipients)
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
        // the manager and dividing a long body shouldn't run on the UI thread that fired the SOS.
        return Task.Run(() => SendInBackground(message, validRecipients));
    }

    private static void SendInBackground(string message, IList<string> recipients)
    {
        var smsManager = GetSmsManager();
        if (smsManager is null)
        {
            // No telephony stack on this device — open the messaging app pre-filled as a last resort so
            // the SOS can still reach the contacts.
            LaunchNativeSmsFallback(message, recipients);
            return;
        }

        var anySent = false;
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
                    var sentIntents = new List<PendingIntent>(parts.Count);
                    for (var p = 0; p < parts.Count; p++)
                    {
                        var pi = BuildImmutableSentIntent(i * 100 + p);
                        if (pi is not null) sentIntents.Add(pi);
                    }
                    // Pass the receipts only if we built one per part (SmsManager requires the list length
                    // to match the parts); otherwise send without receipts — the message still goes out.
                    smsManager.SendMultipartTextMessage(number, null, parts,
                        sentIntents.Count == parts.Count ? sentIntents : null, null);
                }
                else
                {
                    smsManager.SendTextMessage(number, null, message, BuildImmutableSentIntent(i), null);
                }
                anySent = true;
            }
            catch (Exception ex)
            {
                BlackBoxLogger.RecordHandledException(ex, "[AndroidSmsService.SendInBackground]");
            }
        }

        // Every direct send threw (e.g. permission missing at the radio level) — make sure the SOS still
        // has a way out by handing it to the messaging app.
        if (!anySent)
            LaunchNativeSmsFallback(message, recipients);
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
    // "sent" receipt never needs the OS to fill anything in, so it is strictly FLAG_IMMUTABLE.
    private static PendingIntent? BuildImmutableSentIntent(int requestCode)
    {
        try
        {
            var intent = new Intent(SmsSentAction).SetPackage(AndroidApplication.Context.PackageName);
            var flags = Build.VERSION.SdkInt >= BuildVersionCodes.S
                ? PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent
                : PendingIntentFlags.UpdateCurrent;
            return PendingIntent.GetBroadcast(AndroidApplication.Context, requestCode, intent, flags);
        }
        catch
        {
            // A null sent-intent is perfectly valid for SmsManager — the message still goes out, we just
            // forgo the (unused) delivery receipt. Never let receipt plumbing block the SOS.
            return null;
        }
    }

    private static void LaunchNativeSmsFallback(string message, IList<string> recipients)
    {
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

    private static readonly System.Text.RegularExpressions.Regex RecipientRegex =
        new(@"^(09\d{9}|\+639\d{9})$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // Make sure the number still looks like a real PH mobile number before we hand it to the radio.
    // The controller checked already, but a malformed number must never reach SmsManager.
    private static bool IsValidRecipient(string number) => RecipientRegex.IsMatch(number);
}
