namespace AlarmaApp.Services.Interfaces;

public interface IAlarmNotificationService
{
    Task EnsureAlarmChannelAsync();
    Task ShowTripAlertAsync(string title, string message);

    // Updates the ongoing foreground-tracking notification in place so it mirrors the
    // foreground app's live distance-to-destination state.
    Task UpdateTrackingNotificationAsync(string contentText);
}
