namespace AlarmaApp.Services.Interfaces;

public interface IAlarmNotificationService
{
    Task EnsureAlarmChannelAsync();
    Task ShowTripAlertAsync(string title, string message);
}
