// =============================================================================
//  EmergencyAudioPolicyTests.cs
// -----------------------------------------------------------------------------
//  Tests for the two decisions that keep the Emergency (Stage 3) wake-up audible:
//  also driving the phone speaker when earbuds are connected, and warning the
//  rider when Do-Not-Disturb could mute the alarm.
//
//  Self-contained per project convention (see AdaptiveAlarmTests.cs): the real
//  AlarmaApp.Services.EmergencyAudioPolicy and AlarmaApp.Models.AlarmStage live
//  in the net9.0-android app project, so the predicates and the stage enum are
//  mirrored here. Keep them in lock-step.
// =============================================================================

using Xunit;

namespace AlarmaApp.Tests;

// Mirror of AlarmaApp.Models.AlarmStage (only the values the policy reads). Public so it can appear as a
// [Theory] parameter type on the public test methods below.
public enum AlarmStageSpec { None = 0, Stage1 = 1, Stage2 = 2, Stage3 = 3 }

/// <summary>Mirror of AlarmaApp.Services.EmergencyAudioPolicy.</summary>
internal static class EmergencyAudioPolicySpec
{
    public static bool ShouldAlsoDriveSpeaker(AlarmStageSpec stage, bool earphonesConnected) =>
        stage == AlarmStageSpec.Stage3 && earphonesConnected;

    public static bool ShouldWarnDndMayMuteAlarm(bool dndActive, bool hasNotificationPolicyAccess) =>
        dndActive && !hasNotificationPolicyAccess;
}

public class EmergencyAudioRoutingTests
{
    // Emergency + earbuds connected: drive the speaker too, so a pocketed earbud can't swallow the wake-up.
    [Fact]
    public void Emergency_WithEarphones_AlsoDrivesSpeaker()
    {
        Assert.True(EmergencyAudioPolicySpec.ShouldAlsoDriveSpeaker(AlarmStageSpec.Stage3, earphonesConnected: true));
    }

    // Emergency with no earbuds already plays out the speaker on STREAM_ALARM — no second player needed.
    [Fact]
    public void Emergency_WithoutEarphones_DoesNotNeedSecondPlayer()
    {
        Assert.False(EmergencyAudioPolicySpec.ShouldAlsoDriveSpeaker(AlarmStageSpec.Stage3, earphonesConnected: false));
    }

    // Stage 2 routing is left exactly as it was (earbuds-only) to avoid the prior dual-playback annoyance.
    [Theory]
    [InlineData(AlarmStageSpec.Stage1)]
    [InlineData(AlarmStageSpec.Stage2)]
    public void LowerStages_WithEarphones_StayEarbudsOnly(AlarmStageSpec stage)
    {
        Assert.False(EmergencyAudioPolicySpec.ShouldAlsoDriveSpeaker(stage, earphonesConnected: true));
    }
}

public class DndAlarmWarningTests
{
    // DND on and no policy access to override it → we can't punch through, so warn the rider.
    [Fact]
    public void DndOn_WithoutPolicyAccess_Warns()
    {
        Assert.True(EmergencyAudioPolicySpec.ShouldWarnDndMayMuteAlarm(dndActive: true, hasNotificationPolicyAccess: false));
    }

    // DND on but we DO hold policy access → the lockout will force InterruptionFilter.All, no warning needed.
    [Fact]
    public void DndOn_WithPolicyAccess_DoesNotWarn()
    {
        Assert.False(EmergencyAudioPolicySpec.ShouldWarnDndMayMuteAlarm(dndActive: true, hasNotificationPolicyAccess: true));
    }

    // DND off → nothing to warn about regardless of access.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DndOff_NeverWarns(bool hasAccess)
    {
        Assert.False(EmergencyAudioPolicySpec.ShouldWarnDndMayMuteAlarm(dndActive: false, hasNotificationPolicyAccess: hasAccess));
    }
}
