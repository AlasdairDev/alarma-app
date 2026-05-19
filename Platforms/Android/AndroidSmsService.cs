using AlarmaApp.Services.Interfaces;
using Android.Telephony;

namespace AlarmaApp.Platforms.Android;

public class AndroidSmsService : ISmsService
{
    public Task SendEmergencySmsAsync(string message, IEnumerable<string> recipients)
    {
        var smsManager = SmsManager.Default ?? throw new InvalidOperationException("SMS manager unavailable.");
        try
        {
            foreach (var recipient in recipients.Where(number => !string.IsNullOrWhiteSpace(number)))
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
}
