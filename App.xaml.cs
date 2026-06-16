// App startup wiring. One hard-won lesson lives here: we show the launch animation as a modal
// overlay pushed from HomeView.OnAppearing instead of swapping Windows[0].Page to the Shell after
// the splash. The swap looked fine but on Android 14+ it left the InputDispatcher stuck on
// NO_INPUT_CHANNEL — every touch was silently dropped and the app looked frozen. Keeping the Shell
// as the window page from the very start keeps the input channel alive for the whole Activity.
// LaunchDone is just a process-level static flag: it resets when the app is actually killed (so the
// animation plays again on a fresh start) but survives Activity recreation like rotation or a
// back-stack restore, so we don't replay the splash on every little lifecycle bump.

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
