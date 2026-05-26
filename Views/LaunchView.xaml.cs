// Security Considerations (OWASP Top 10)
// A05 Security Misconfiguration: _navigated flag prevents double-navigation on rapid tap;
//   PrepareForAppearance() resets it per Activity lifecycle so the singleton never skips
//   the onboarding/auth gate on Android back-stack restoration.
// A04 Insecure Design: No user data is accepted here; splash cannot be used to intercept
//   or bypass downstream permission flows — it only signals Completed to App.xaml.cs.

namespace AlarmaApp.Views;

public partial class LaunchView : ContentPage
{
    public event EventHandler? Completed;

    private bool _navigated;

    public LaunchView()
    {
        InitializeComponent();
        Appearing += OnAppearing;
    }

    // Called by App.CreateWindow before each Window creation so activity
    // recreation (rotation, back-stack restore) always shows a clean entry
    // animation rather than a frozen page caused by _navigated=true persisting
    // in the singleton instance.
    public void PrepareForAppearance()
    {
        _navigated = false;
        RootContent.Opacity = 0;
    }

    private async void OnAppearing(object? sender, EventArgs e)
    {
        // Opacity is already 0 (XAML default + PrepareForAppearance).
        // Starting the animation directly removes the one-frame white flash
        // that occurred when Content.Opacity was set in C# after first render.
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
        await RootContent.FadeTo(0, 220, Easing.CubicIn);
        Completed?.Invoke(this, EventArgs.Empty);
    }
}
