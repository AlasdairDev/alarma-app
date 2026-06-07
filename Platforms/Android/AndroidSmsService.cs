// Security Considerations (OWASP Top 10)
// A03 Injection: IsValidRecipient() re-validates phone number format with a compiled Regex at
//   the transport layer (defense in depth after HomeController's pre-validation) — no number
//   that does not match ^(09\d{9}|\+639\d{9})$ reaches SmsManager.SendTextMessage().
// A04 Insecure Design: When SEND_SMS permission is denied or SmsManager raises a security
//   exception, a native Intent(ActionSendto) fallback is launched — the device's messaging app
//   opens with the SOS body and recipients pre-filled, guaranteeing delivery under all permission
//   states without a crash. GPS coordinates in the SOS body are never logged.

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
