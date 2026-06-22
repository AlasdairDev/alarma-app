using AlarmaApp.Models;

namespace AlarmaApp.Services;

// The two decisions that keep the Emergency (Stage 3) wake-up audible in the night-commute cases where it
// could otherwise be silenced. Pulled out as pure predicates so the routing/DND logic is unit-testable
// without an Android AudioManager or NotificationManager in the loop. Both apply to the Emergency stage
// only — Stage 2's earbud routing is deliberately left alone to avoid the old dual-playback annoyance.
public static class EmergencyAudioPolicy
{
    // When earbuds are connected the alarm normally routes to them exclusively, which a paired-but-pocketed
    // earbud can swallow. For the Emergency stage we ALSO drive the phone speaker (STREAM_ALARM) alongside
    // the earbuds, so the wake-up gets out either way. Stage 2 stays earbuds-only.
    public static bool ShouldAlsoDriveSpeaker(AlarmStage stage, bool earphonesConnected) =>
        stage == AlarmStage.Stage3 && earphonesConnected;

    // The full-screen lockout forces InterruptionFilter.All only if we hold notification-policy access.
    // If Do-Not-Disturb is on and we DON'T hold that access, we can't punch through it and the alarm may be
    // muted — so warn the rider (and offer to grant access) rather than letting them sleep through it.
    public static bool ShouldWarnDndMayMuteAlarm(bool dndActive, bool hasNotificationPolicyAccess) =>
        dndActive && !hasNotificationPolicyAccess;
}
