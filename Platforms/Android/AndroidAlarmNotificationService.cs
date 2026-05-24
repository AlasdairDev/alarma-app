using AlarmaApp.Services.Interfaces;
using Android.App;
using Android.Content;
using Android.OS;
using AndroidApplication = Android.App.Application;

// Alias to avoid ambiguity between AlarmaApp.Resource and Android.Resource
using AndroidResource = Android.Resource;

namespace AlarmaApp.Platforms.Android;

public class AndroidAlarmNotificationService : IAlarmNotificationService
{
    private const string ChannelId = "alarma_critical_alerts";
    private const int AlertNotificationId = 2001;

    public Task EnsureAlarmChannelAsync()
    {
        var manager = AndroidApplication.Context.GetSystemService(Context.NotificationService) as NotificationManager;
        if (manager is null)
        {
            return Task.CompletedTask;
        }

        var existing = manager.GetNotificationChannel(ChannelId);
        if (existing is not null)
        {
            return Task.CompletedTask;
        }

        var channel = new NotificationChannel(
            ChannelId,
            "Alarma Critical Alerts",
            NotificationImportance.High)
        {
            Description = "Emergency alarms and trip alerts"
        };

        manager.CreateNotificationChannel(channel);
        return Task.CompletedTask;
    }

    public async Task ShowTripAlertAsync(string title, string message)
    {
        var manager = AndroidApplication.Context.GetSystemService(Context.NotificationService) as NotificationManager;
        if (manager is null)
        {
            return;
        }

        await EnsureAlarmChannelAsync();
        var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Notification.Builder(AndroidApplication.Context, ChannelId)
            : new Notification.Builder(AndroidApplication.Context);

        var notification = builder
            .SetContentTitle(title)
            .SetContentText(message)
            .SetSmallIcon(AndroidResource.Drawable.IcDialogAlert)
            .SetAutoCancel(true)
            .SetPriority((int)NotificationPriority.High)
            .Build();

        if (notification is null) return;
        manager.Notify(AlertNotificationId, notification);
    }
}