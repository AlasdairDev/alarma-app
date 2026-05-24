using Android.App;
using Android.Content.PM;
using Android.OS;

namespace AlarmaApp;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    Exported = true,
    ScreenOrientation = ScreenOrientation.Portrait,
    ConfigurationChanges = ConfigChanges.ScreenSize
        | ConfigChanges.Orientation
        | ConfigChanges.UiMode
        | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize
        | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
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
    }

    sealed class SplashExitListener : Java.Lang.Object,
        AndroidX.Core.SplashScreen.SplashScreen.IOnExitAnimationListener
    {
        public void OnSplashScreenExit(AndroidX.Core.SplashScreen.SplashScreenViewProvider p)
            => p.Remove();
    }
}
