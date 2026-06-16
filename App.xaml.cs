// Security Considerations (OWASP Top 10)
// A04 Insecure Design: LaunchView is pushed as a modal overlay from HomeView.OnAppearing
//   rather than being set as the initial Window.Page. The original approach
//   (Windows[0].Page = _shell after launch completes) caused Android 14+ InputDispatcher
//   to permanently set ActivityRecordInputSink to NO_INPUT_CHANNEL, silently dropping all
//   touch events. Using Shell as the window page from the start keeps the input channel
//   stable for the lifetime of the Activity.
// A05 Security Misconfiguration: LaunchDone is a process-level static flag — it resets on
//   app kill/restart (showing the launch animation again) but persists across Android
//   Activity recreations (rotation, back-stack restore) so the animation is not replayed.

using AlarmaApp.Services;

namespace AlarmaApp;

public partial class App : Application
{
    private readonly AppShell _shell;
    private readonly Views.LaunchView _launchView;

    // Process-lifetime flag: true after the first launch animation completes.
    // Reset to false automatically when the OS kills the process and restarts it.
    internal static bool LaunchDone { get; private set; }

    public App(AppShell shell, Views.LaunchView launchView)
    {
        _shell = shell;
        _launchView = launchView;
        InitializeComponent();

        // Forensic crash recovery: load the Keystore-backed key, then decrypt any previous-session
        // crash into the readable fault report. Fire-and-forget — both steps swallow their own
        // exceptions internally, so this can never fault app startup.
        _ = BlackBoxLogger.InitializeAndReportAsync();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        if (!LaunchDone)
        {
            // Select Home tab before the shell renders so HomeView.OnAppearing
            // fires first and can push the launch modal immediately.
            if (_shell.Items.Count > 0 &&
                _shell.Items[0] is TabBar tabBar &&
                tabBar.Items.Count > 2)
            {
                tabBar.CurrentItem = tabBar.Items[2];
            }

            _launchView.PrepareForAppearance();
            _launchView.Completed -= OnLaunchCompleted;
            _launchView.Completed += OnLaunchCompleted;
        }

        // Shell is always the window page — never swapped.
        // This keeps the Android Activity's input channel stable.
        return new Window(_shell);
    }

    private async void OnLaunchCompleted(object? sender, EventArgs e)
    {
        _launchView.Completed -= OnLaunchCompleted;
        LaunchDone = true;
        await _shell.Navigation.PopModalAsync(animated: false);
    }
}
