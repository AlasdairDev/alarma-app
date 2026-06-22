using AlarmaApp.Services;

namespace AlarmaApp.Services.Interfaces;

public interface ISmsService
{
    // Returns the per-contact delivery outcome so the caller can tell the rider exactly how many of
    // their contacts the SOS actually reached, rather than assuming the queue-to-radio call succeeded.
    Task<SosDeliveryResult> SendEmergencySmsAsync(string message, IEnumerable<string> recipients);
}
