using AlarmaApp.Controllers;
using AlarmaApp.Services;
using AlarmaApp.Services.Interfaces;
using AlarmaApp.Platforms.Android;
using Microsoft.Extensions.Logging;

namespace AlarmaApp;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

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

        // Navigation views — transient, new instance per navigation
        builder.Services.AddTransient<Views.SearchView>();
        builder.Services.AddTransient<Views.AlarmStageView>();
        builder.Services.AddTransient<Views.OnboardingView>();
        builder.Services.AddSingleton<Views.LaunchView>();

#if ANDROID
        builder.Services.AddSingleton<ILocationService, AndroidLocationService>();
        builder.Services.AddSingleton<ISmsService, AndroidSmsService>();
        builder.Services.AddSingleton<IAlarmAudioService, AndroidAlarmAudioService>();
        builder.Services.AddSingleton<IAlarmNotificationService, AndroidAlarmNotificationService>();
        builder.Services.AddSingleton<IBiometricAuthService, AndroidBiometricAuthService>();
        builder.Services.AddSingleton<IConnectivityService, AndroidConnectivityService>();
        builder.Services.AddSingleton<IGoogleMapsLauncher, AndroidGoogleMapsLauncher>();
        builder.Services.AddSingleton<IBatteryOptimizationService, AndroidBatteryOptimizationService>();
        builder.Services.AddSingleton<IEarphoneService, AndroidEarphoneService>();
#endif

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
