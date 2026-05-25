// Security Considerations (OWASP Top 10)
// A04 Insecure Design: CreateWindow guards against duplicate Completed subscriptions that
//   accumulate across Android Activity recreations, preventing potential event-driven
//   state corruption from replaying the launch flow under high-frequency lifecycle churn.
// A05 Security Misconfiguration: LaunchView.PrepareForAppearance() resets _navigated=false
//   so a singleton page can never silently bypass the auth/onboarding gate on recreation.
// No user input, secrets, or network calls in this class; attack surface is minimal.

namespace AlarmaApp;

public partial class App : Application
{
    private readonly AppShell _shell;
    private readonly Views.LaunchView _launchView;

    public App(AppShell shell, Views.LaunchView launchView)
    {
        _shell = shell;
        _launchView = launchView;
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        // Guard against duplicate event subscriptions that accumulate when
        // Android recreates the Activity (rotation, back-stack restore).
        _launchView.Completed -= OnLaunchCompleted;
        _launchView.Completed += OnLaunchCompleted;

        // Reset animation state each time so the singleton LaunchView never
        // gets stuck in _navigated=true from a prior Activity lifecycle.
        // This is what caused the "History tab pops up the Launch page" bug:
        // the frozen LaunchView was set as the new Window page on recreation.
        _launchView.PrepareForAppearance();

        return new Window(_launchView);
    }

    private void OnLaunchCompleted(object? sender, EventArgs e)
    {
        _launchView.Completed -= OnLaunchCompleted;

        // Pre-select Home tab before making the Shell the Window page.
        // Without this, the Shell briefly shows the first tab (History)
        // while GoToAsync("//home") was awaited on the next frame —
        // which is the visual "History flicker" users reported.
        if (_shell.Items.Count > 0 &&
            _shell.Items[0] is TabBar tabBar &&
            tabBar.Items.Count > 2)
        {
            tabBar.CurrentItem = tabBar.Items[2]; // Home is index 2
        }

        Windows[0].Page = _shell;
    }
}
