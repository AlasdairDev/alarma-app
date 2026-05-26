// Security Considerations (OWASP Top 10)
// A07 Identification and Authentication Failures: UpdateAuthStatus() re-reads BiometricManager
//   state on every OnAppearing call — the displayed enrollment status always reflects the current
//   device state, not a cached value that could mislead the user after they enroll or remove a
//   fingerprint in the Android security settings and return to the app.
// A05 Security Misconfiguration: All Picker/Switch/Stepper values write through HomeController
//   property setters that whitelist (AlarmSound) or clamp (AlarmLeadMinutes 1–60) before
//   persisting to Preferences — the Settings UI cannot write arbitrary values to storage.
// No user-typed text fields in this view; all inputs are constrained by the control types used.

using AlarmaApp.Controllers;

namespace AlarmaApp.Views;

public partial class SettingsView : ContentPage
{
    private readonly HomeController _controller;

    public SettingsView(HomeController controller)
    {
        _controller = controller;
        BindingContext = controller;
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        Content.Opacity = 0;
        base.OnAppearing();
        Content.FadeTo(1, 220, Easing.CubicOut);
    }

    private async void OnUpdateContactsClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("//emergency");
    }
}
