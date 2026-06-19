using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AlarmaApp.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace AlarmaApp;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    Exported = true,
    // SingleTop so a notification tap on the already-running app routes through OnNewIntent
    // instead of spawning a second activity instance on top of the live one.
    LaunchMode = LaunchMode.SingleTop,
    ScreenOrientation = ScreenOrientation.Portrait,
    ConfigurationChanges = ConfigChanges.ScreenSize
        | ConfigChanges.Orientation
        | ConfigChanges.UiMode
        | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize
        | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    // Intent extra carrying the Shell route a notification wants to open (e.g. "alarmstage").
    public const string NavigateExtra = "alarma_navigate_to";

    // Hardware SOS panic gesture: three quick Volume-Up presses fire the SOS. Three is enough to be
    // deliberate (you won't hit it just nudging the volume) but still doable one-handed in a panic.
    private const int SosVolumePressCount = 3;
    // …all within this rolling window. 1.5s is snappy enough that ordinary volume adjustments spread out
    // over a song won't ever stack up to a false trigger.
    private static readonly long SosVolumeWindowMs = 1500;
    // Timestamps (monotonic, via SystemClock) of recent Volume-Up presses still inside the window.
    private readonly List<long> _volumeUpPresses = new();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        // MAUI keeps the splash on-screen via setKeepOnScreenCondition (set in base).
        // A second InstallSplashScreen call lets us register an exit-animation listener
        // that removes the splash instantly — preventing the default circle-zoom-out
        // animation that makes the icon's circular mask visible to the user.
        AndroidX.Core.SplashScreen.SplashScreen
            .InstallSplashScreen(this)
            .SetOnExitAnimationListener(new SplashExitListener());

        // Cold start from a notification tap — the launch intent carries the route.
        HandleDeepLinkIntent(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        // Warm tap — the app was already running. Replace the stored intent and route.
        Intent = intent;
        HandleDeepLinkIntent(intent);
    }

    private static void HandleDeepLinkIntent(Intent? intent)
    {
        var route = intent?.GetStringExtra(NavigateExtra);
        if (string.IsNullOrEmpty(route))
        {
            return;
        }

        // Clear it so a later configuration change / relaunch doesn't re-trigger navigation.
        intent!.RemoveExtra(NavigateExtra);
        _ = NavigateWhenReadyAsync(route);
    }

    // On a cold start the Shell isn't constructed the instant the Activity is — poll briefly until
    // it exists, then navigate on the main thread.
    private static async Task NavigateWhenReadyAsync(string route)
    {
        for (var i = 0; i < 50 && Shell.Current is null; i++)
        {
            await Task.Delay(100);
        }

        if (Shell.Current is null)
        {
            return;
        }

        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            try { await Shell.Current.GoToAsync(route, animate: false); }
            catch { }
        });
    }

    // Intercept the physical Volume-Up button to drive the panic SOS. We deliberately DON'T consume the
    // event (we still call base), so the press also does its normal job of changing the media/ring volume —
    // the gesture is invisible until the third rapid press actually fires the SOS.
    public override bool OnKeyDown(Keycode keyCode, KeyEvent? e)
    {
        if (keyCode == Keycode.VolumeUp)
        {
            RegisterVolumeUpPress();
        }

        return base.OnKeyDown(keyCode, e);
    }

    // Record this press, forget any that have aged out of the window, and if enough land in quick
    // succession, fire the SOS and reset the counter so the next burst starts clean.
    private void RegisterVolumeUpPress()
    {
        var now = SystemClock.ElapsedRealtime();
        _volumeUpPresses.Add(now);
        _volumeUpPresses.RemoveAll(t => now - t > SosVolumeWindowMs);

        if (_volumeUpPresses.Count >= SosVolumePressCount)
        {
            _volumeUpPresses.Clear();
            FireHardwareSos();
        }
    }

    // Resolve the shared controller from the DI container and trigger the SOS on the UI thread. If the
    // app isn't fully spun up yet (no service provider), we simply do nothing rather than risk a crash.
    private static void FireHardwareSos()
    {
        var controller = IPlatformApplication.Current?.Services?.GetService<HomeController>();
        if (controller is null)
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(controller.TriggerSosFromHardware);
    }

    sealed class SplashExitListener : Java.Lang.Object,
        AndroidX.Core.SplashScreen.SplashScreen.IOnExitAnimationListener
    {
        public void OnSplashScreenExit(AndroidX.Core.SplashScreen.SplashScreenViewProvider p)
            => p.Remove();
    }
}
