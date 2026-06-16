// The splash/launch animation. The _navigated flag stops a rapid double-tap from firing navigation
// twice, and PrepareForAppearance() resets it each Activity lifecycle so this singleton never
// accidentally skips the onboarding/permission gates when Android restores the back stack. It takes
// no user input and can't be used to jump past any later permission step — all it does is raise
// Completed back to App.xaml.cs.

namespace AlarmaApp.Views;

public partial class LaunchView : ContentPage
{
    public event EventHandler? Completed;

    private bool _navigated;

    public LaunchView()
    {
        InitializeComponent();
        // NOTE: do NOT subscribe Appearing here — the event is unreliable when a modal is
        // pushed from within another page's OnAppearing lifecycle callback. Use the virtual
        // OnAppearing override instead, which the MAUI framework always calls.
    }

    // Called by App.CreateWindow before each Window creation so Activity recreation
    // (rotation, back-stack restore) always shows a clean entry animation rather than
    // a frozen page caused by _navigated=true persisting in the singleton instance.
    public void PrepareForAppearance()
    {
        _navigated = false;
        RootContent.Opacity = 0;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // A short delay lets the Android native renderer attach and complete its first
        // draw pass before the opacity animation starts. Without this, FadeTo may fire
        // before the view has a compositor layer and the animation is lost.
        await Task.Delay(60);
        await RootContent.FadeTo(1, 300, Easing.CubicOut);
        await Task.Delay(1200);
        await NavigateForwardAsync();
    }

    private async void OnScreenTapped(object? sender, TappedEventArgs e)
    {
        await NavigateForwardAsync();
    }

    private async Task NavigateForwardAsync()
    {
        if (_navigated) return;
        _navigated = true;
        // Fire while the surface is still visible — fading to 0 before the swap
        // causes Android to hide the window surface, which blocks all touch input
        // on the incoming Shell page.
        Completed?.Invoke(this, EventArgs.Empty);
    }
}
