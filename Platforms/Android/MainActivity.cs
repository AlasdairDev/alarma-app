using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Widget;

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
    private const double MinSupportedInches = 5.0;
    private const double MaxSupportedInches = 6.7;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        VerifyFormFactor();
    }

    private void VerifyFormFactor()
    {
        var metrics = Resources?.DisplayMetrics;
        if (metrics is null)
        {
            return;
        }

        var widthInches = metrics.WidthPixels / metrics.Xdpi;
        var heightInches = metrics.HeightPixels / metrics.Ydpi;
        var diagonal = Math.Sqrt(widthInches * widthInches + heightInches * heightInches);
        if (diagonal < MinSupportedInches || diagonal > MaxSupportedInches)
        {
            Toast.MakeText(
                this,
                "Alarma is optimized for 5.0-6.7 inch smartphones in portrait mode.",
                ToastLength.Long)?.Show();
        }
    }
}
