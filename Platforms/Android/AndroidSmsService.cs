// Security Considerations (OWASP Top 10)
// A03 Injection: IsValidRecipient() re-validates phone number format with a compiled Regex at
//   the transport layer (defense in depth after HomeController's pre-validation) — no number
//   that does not match ^(09\d{9}|\+639\d{9})$ reaches SmsManager.SendTextMessage().
// A04 Insecure Design: The LINQ Where(IsValidRecipient) filter is applied inside the foreach
//   loop so even if a caller bypasses controller validation, no malformed number is sent.
// No message content is logged — GPS coordinates in the SOS body never appear in logcat output.

using AlarmaApp.Services.Interfaces;
using Android.Telephony;

namespace AlarmaApp.Platforms.Android;

public class AndroidSmsService : ISmsService
{
    public Task SendEmergencySmsAsync(string message, IEnumerable<string> recipients)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("SOS message must not be empty.", nameof(message));

        var smsManager = SmsManager.Default ?? throw new InvalidOperationException("SMS manager unavailable.");
        try
        {
            foreach (var recipient in recipients
                         .Where(number => !string.IsNullOrWhiteSpace(number))
                         .Select(number => number.Trim())
                         .Where(IsValidRecipient))
            {
                smsManager.SendTextMessage(recipient, null, message, null, null);
            }

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to send SOS SMS.", ex);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex RecipientRegex =
        new(@"^(09\d{9}|\+639\d{9})$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // Defense-in-depth: re-validate format at the transport layer before handing off to Android.
    private static bool IsValidRecipient(string number) => RecipientRegex.IsMatch(number);
}
