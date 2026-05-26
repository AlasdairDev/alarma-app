// Security Considerations (OWASP Top 10)
// A04 Insecure Design: AlarmStageActivated is subscribed at the Shell singleton level so the
//   full-screen alarm UI surfaces regardless of which tab the user is on when a GPS distance
//   threshold is crossed — no tab switch can silently suppress an in-progress alarm. Without
//   this, the event was only caught while HomeView was visible; any other active tab meant the
//   alarm modal never appeared (audio/notification still fired, but UI was dead).
//   _alarmStageShowing prevents concurrent GoToAsync("alarmstage") calls from stacking
//   duplicate alarm pages on the navigation stack. The Navigated event resets the flag only
//   after the alarmstage route has been popped, not suppressed.
// No user input, secrets, or network calls are handled in this class.

using AlarmaApp.Controllers;
using AlarmaApp.Models;

namespace AlarmaApp;

public partial class AppShell : Shell
{
    private bool _alarmStageShowing;

    public AppShell(HomeController controller)
    {
        InitializeComponent();
        Routing.RegisterRoute("search", typeof(Views.SearchView));
        Routing.RegisterRoute("alarmstage", typeof(Views.AlarmStageView));
        Routing.RegisterRoute("onboarding", typeof(Views.OnboardingView));
        Routing.RegisterRoute("permissions-setup", typeof(Views.PermissionsSetupView));

        controller.AlarmStageActivated += OnAlarmStageActivated;

        // Track flag bidirectionally: set TRUE when navigating TO alarmstage (covers both
        // the automatic OnAlarmStageActivated path AND the manual "View Active Trip" tap),
        // set FALSE when leaving alarmstage. Without the TRUE branch, a manual navigation
        // to alarmstage left _alarmStageShowing=false, allowing OnAlarmStageActivated to
        // push a duplicate AlarmStageView on top of the one already visible.
        Navigated += (_, e) =>
        {
            _alarmStageShowing = e.Current.Location.OriginalString
                .Contains("alarmstage", StringComparison.Ordinal);
        };
    }

    private async void OnAlarmStageActivated(object? sender, AlarmStage stage)
    {
        if (_alarmStageShowing) return;
        _alarmStageShowing = true;
        await GoToAsync("alarmstage", animate: false);
    }
}
