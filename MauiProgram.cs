using AlarmaApp.Controllers;
using AlarmaApp.Services;
using AlarmaApp.Services.Interfaces;
using AlarmaApp.Platforms.Android;
using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;

namespace AlarmaApp;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        // Black-box crash handlers — subscribed before any app code runs so that
        // unhandled exceptions on any thread are captured to the encrypted log.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            BlackBoxLogger.WriteCrashLog(e.ExceptionObject as Exception,
                $"AppDomain.UnhandledException (terminating={e.IsTerminating})");

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            BlackBoxLogger.WriteCrashLog(e.Exception,
                "TaskScheduler.UnobservedTaskException");
            e.SetObserved(); // prevent process termination for unobserved async faults
        };

        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>()
            // Community Toolkit — gives us FileSaver (native "Save As" dialog) for the backup export.
            .UseMauiCommunityToolkit()
            .ConfigureFonts(fonts =>
            {
                // Material Symbols — icon ligatures (search, location_on, etc.) used across the views.
                fonts.AddFont("MaterialSymbolsOutlined.ttf", "MaterialSymbols");
            });

        builder.Services.AddSingleton<DatabaseService>();
        builder.Services.AddSingleton<PreferencesService>();
        builder.Services.AddSingleton<BackupService>();
        builder.Services.AddSingleton<PermissionsService>();
        builder.Services.AddSingleton<GeocodingService>();
        builder.Services.AddSingleton<HomeController>();

        builder.Services.AddSingleton<AppShell>();

        // Tab views — singletons so state is preserved across tab switches
        builder.Services.AddSingleton<Views.HomeView>();
        builder.Services.AddSingleton<Views.HistoryView>();
        builder.Services.AddSingleton<Views.FavoritesView>();
        builder.Services.AddSingleton<Views.EmergencyView>();
        builder.Services.AddSingleton<Views.SettingsView>();

        // Navigation views — transient so each push starts clean instead of leaking the previous
        // trip's search query or destination selection into the next one
        builder.Services.AddTransient<Views.SearchView>();
        builder.Services.AddTransient<Views.AddFavoriteView>();
        builder.Services.AddTransient<Views.AlarmStageView>();
        builder.Services.AddTransient<Views.OnboardingView>();
        builder.Services.AddTransient<Views.PermissionsSetupView>();
        builder.Services.AddSingleton<Views.LaunchView>();

#if ANDROID
        Microsoft.Maui.Handlers.WebViewHandler.Mapper.AppendToMapping("AndroidWebViewConfig", (handler, view) =>
        {
            handler.PlatformView.Settings.JavaScriptEnabled = true;
            handler.PlatformView.Settings.DomStorageEnabled = true;
        });

        builder.Services.AddSingleton<ILocationService, AndroidLocationService>();
        builder.Services.AddSingleton<ISmsService, AndroidSmsService>();
        builder.Services.AddSingleton<IAlarmAudioService, AndroidAlarmAudioService>();
        builder.Services.AddSingleton<IAlarmNotificationService, AndroidAlarmNotificationService>();
        builder.Services.AddSingleton<IConnectivityService, AndroidConnectivityService>();
        builder.Services.AddSingleton<IGoogleMapsLauncher, AndroidGoogleMapsLauncher>();
        builder.Services.AddSingleton<IBatteryOptimizationService, AndroidBatteryOptimizationService>();
        builder.Services.AddSingleton<IEarphoneService, AndroidEarphoneService>();
        builder.Services.AddSingleton<IBluetoothMonitor, AndroidBluetoothMonitor>();
#endif

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
