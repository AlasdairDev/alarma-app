namespace AlarmaApp.Services.Interfaces;

public interface ISmsService
{
    Task SendEmergencySmsAsync(string message, IEnumerable<string> recipients);
}
