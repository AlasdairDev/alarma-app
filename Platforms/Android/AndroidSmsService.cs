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

    // Defense-in-depth: re-validate format at the transport layer before handing off to Android.
    private static bool IsValidRecipient(string number) =>
        System.Text.RegularExpressions.Regex.IsMatch(number, @"^(09\d{9}|\+639\d{9})$");
}
