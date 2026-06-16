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

    // Builds the tap action that deep-links the user straight to the active-trip (alarm stage)
    // screen. Routed through MainActivity (SingleTop) so the running app is reused, not duplicated.
    private static PendingIntent? BuildAlarmStageContentIntent()
    {
        var context = AndroidApplication.Context;
        var intent = new Intent(context, typeof(global::AlarmaApp.MainActivity));
        intent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        intent.PutExtra(global::AlarmaApp.MainActivity.NavigateExtra, "alarmstage");

        var flags = PendingIntentFlags.UpdateCurrent;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
        {
            flags |= PendingIntentFlags.Immutable;
        }

        return PendingIntent.GetActivity(context, 0, intent, flags);
    }

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
            .SetContentIntent(BuildAlarmStageContentIntent())
            .SetPriority((int)NotificationPriority.High)
            .Build();

        if (notification is null) return;
        manager.Notify(AlertNotificationId, notification);
    }

    public Task UpdateTrackingNotificationAsync(string contentText)
    {
        var manager = AndroidApplication.Context.GetSystemService(Context.NotificationService) as NotificationManager;
        if (manager is null)
        {
            return Task.CompletedTask;
        }

        // The ongoing tracking notification (id + channel owned by LocationTrackingService) is the
        // foreground-service notification; re-posting with the same id updates it in place. The
        // channel is normally created by the service, but ensure it here in case of ordering.
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O
            && manager.GetNotificationChannel(LocationTrackingService.TrackingChannelId) is null)
        {
            var channel = new NotificationChannel(
                LocationTrackingService.TrackingChannelId,
                "Alarma Trip Tracking",
                NotificationImportance.Low)
            {
                Description = "Background location tracking for active trips"
            };
            manager.CreateNotificationChannel(channel);
        }

        var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Notification.Builder(AndroidApplication.Context, LocationTrackingService.TrackingChannelId)
            : new Notification.Builder(AndroidApplication.Context);

        var notification = builder
            .SetContentTitle("Alarma tracking active")
            .SetContentText(contentText)
            .SetSmallIcon(AndroidResource.Drawable.IcDialogMap)
            .SetOngoing(true)
            .SetContentIntent(BuildAlarmStageContentIntent())
            .Build();

        if (notification is null)
        {
            return Task.CompletedTask;
        }

        manager.Notify(LocationTrackingService.TrackingNotificationId, notification);
        return Task.CompletedTask;
    }
}