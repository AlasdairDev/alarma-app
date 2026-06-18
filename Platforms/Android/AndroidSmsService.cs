// Sends the actual SOS text messages. This now goes through the cross-platform MAUI SMS API
// (Microsoft.Maui.ApplicationModel.Communication.Sms), which opens the device's own messaging app
// with the recipients and the SOS body (including the live-location link) pre-filled. The rider taps
// send once. That route needs NO restricted SEND_SMS runtime permission, which is exactly why the old
// SmsManager path quietly failed on newer Android — so the SOS reliably reaches the messaging app now.
// Two safety nets remain:
//   - IsValidRecipient() re-checks every number against ^(09\d{9}|\+639\d{9})$ before it's used, so a
//     bad number can never reach the composer even if the controller's own validation were bypassed.
//   - If the platform can't compose an SMS (no SIM, tablet, etc.) we fall back to a native
//     Intent(ActionSendto) that opens the messaging app the same way. The coordinates in that body are
//     never written to any log.

using AlarmaApp.Services;
using AlarmaApp.Services.Interfaces;
using Android.Content;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.Communication;

namespace AlarmaApp.Platforms.Android;

public class AndroidSmsService : ISmsService
{
    public async Task SendEmergencySmsAsync(string message, IEnumerable<string> recipients)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("SOS message must not be empty.", nameof(message));

        var validRecipients = recipients
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Where(IsValidRecipient)
            .ToList();

        if (validRecipients.Count == 0)
            throw new InvalidOperationException("No valid emergency recipients to send the SOS to.");

        try
        {
            // The MAUI SMS API is the supported, permission-free way to send: it hands the message and
            // recipients to the system messaging app pre-filled. Must run on the main thread.
            if (Sms.Default.IsComposeSupported)
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                    Sms.Default.ComposeAsync(new SmsMessage(message, validRecipients)));
                return;
            }
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[AndroidSmsService.ComposeAsync]");
            // Fall through to the native intent below so the SOS can still go out.
        }

        // Composer unsupported or threw — open the native messaging app pre-filled as a last resort.
        LaunchNativeSmsIntent(message, validRecipients);
    }

    private static void LaunchNativeSmsIntent(string message, IList<string> recipients)
    {
        try
        {
            var verifiedEmergencyNumbers = string.Join(";", recipients);
            var uri = global::Android.Net.Uri.Parse("smsto:" + verifiedEmergencyNumbers);
            var smsIntent = new Intent(Intent.ActionSendto, uri);
            smsIntent.PutExtra("sms_body", message);
            smsIntent.AddFlags(ActivityFlags.NewTask);
            global::Android.App.Application.Context.StartActivity(smsIntent);
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[AndroidSmsService.LaunchNativeSmsIntent]");
        }
    }

    private static readonly System.Text.RegularExpressions.Regex RecipientRegex =
        new(@"^(09\d{9}|\+639\d{9})$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // Make sure the number still looks like a real PH mobile number before we hand it to the composer.
    // The controller checked already, but a malformed number must never reach the SMS app.
    private static bool IsValidRecipient(string number) => RecipientRegex.IsMatch(number);
}
