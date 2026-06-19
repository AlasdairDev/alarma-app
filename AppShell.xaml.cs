// We wire AlarmStageActivated up here at the Shell (which is a singleton) on purpose: that way the
// full-screen alarm pops no matter which tab the rider happens to be on when a distance threshold is
// crossed. We learned this the hard way — when the event was only handled inside HomeView, switching
// to any other tab meant the alarm sound and notification still fired but the UI never showed up.
// _alarmStageShowing guards against two GoToAsync("alarmstage") calls racing and stacking duplicate
// alarm pages; the Navigated event only clears it once the alarmstage route is actually popped.
// No user input, secrets, or network calls happen in this class.

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
        Routing.RegisterRoute("add-favorite", typeof(Views.AddFavoriteView));
        Routing.RegisterRoute("alarmstage", typeof(Views.AlarmStageView));
        Routing.RegisterRoute("onboarding", typeof(Views.OnboardingPage));
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
        // Only the Emergency stage takes over the whole screen. Stage 1 (gentle) and Stage 2 (louder)
        // are deliberately non-intrusive — they fire their sound/vibration + a local notification but
        // leave the rider on whatever screen they're on, so the lockout is reserved for the real
        // "you're at your stop / you missed it" moment.
        if (stage < AlarmStage.Stage3) return;
        if (_alarmStageShowing) return;
        _alarmStageShowing = true;
        await GoToAsync("alarmstage", animate: false);
    }
}
