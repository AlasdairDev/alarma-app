// Sends the actual SOS text messages. Two safety nets here:
//   - IsValidRecipient() re-checks every number against ^(09\d{9}|\+639\d{9})$ right before it hits
//     SmsManager.SendTextMessage(). Yes, HomeController already validated it, but we double-check at
//     the transport layer so a bad number can never slip through.
//   - If SEND_SMS is denied or SmsManager throws, we fall back to a native Intent(ActionSendto) that
//     opens the phone's own messaging app with the recipients and SOS text pre-filled. The message
//     still goes out (the user just taps send) instead of the app crashing. The coordinates in that
//     body are never written to any log.

using AlarmaApp.Services;
using AlarmaApp.Services.Interfaces;
using Android.Content;
using Android.Telephony;
using Microsoft.Maui.ApplicationModel;

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

        // Defense-in-depth: check and request SEND_SMS at transport layer.
        var smsStatus = await Permissions.CheckStatusAsync<SmsPermission>();
        if (smsStatus != PermissionStatus.Granted)
            smsStatus = await Permissions.RequestAsync<SmsPermission>();

        if (smsStatus != PermissionStatus.Granted)
        {
            // Graceful Intent fallback: open native SMS app with pre-filled recipients + body.
            LaunchNativeSmsIntent(message, validRecipients);
            return;
        }

        var smsManager = SmsManager.Default ?? throw new InvalidOperationException("SMS manager unavailable.");
        try
        {
            foreach (var recipient in validRecipients)
                smsManager.SendTextMessage(recipient, null, message, null, null);
        }
        catch (Exception ex)
        {
            // Security exception or send failure — fall back to native SMS app.
            LaunchNativeSmsIntent(message, validRecipients);
            throw new InvalidOperationException("SOS SMS send failed — native SMS app launched as fallback.", ex);
        }
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
        catch { }
    }

    private static readonly System.Text.RegularExpressions.Regex RecipientRegex =
        new(@"^(09\d{9}|\+639\d{9})$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // Defense-in-depth: re-validate format at the transport layer before handing off to Android.
    private static bool IsValidRecipient(string number) => RecipientRegex.IsMatch(number);
}
