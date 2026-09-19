using Allegiance.ClientCore.MonoGame.Audio;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="MissileWarningController"/> — the P264
/// incoming-missile warning loop transition contract: exactly one
/// <see cref="MissileWarningAction.Arm"/> on the not-tracked→tracked edge, a
/// per-frame <see cref="MissileWarningAction.Pitch"/> refresh while armed (the
/// pitch ramp <c>0.75 + bestLock/2</c>, native trekigc.cpp:5065), and exactly one
/// <see cref="MissileWarningAction.Disarm"/> on the tracked→not-tracked edge —
/// never re-fired every frame. No audio device involved: the controller only
/// emits <see cref="MissileWarningCommand"/>s; <see cref="SoundManager"/>
/// performs them.
/// </summary>
public class MissileWarningControllerTests
{
    [Fact]
    public void RisingEdge_ArmsExactlyOnce_ThenPitchRefreshes_WhileTracked()
    {
        var ctl = new MissileWarningController();

        // Clear baseline.
        Assert.Equal(MissileWarningAction.None, ctl.Plan(false, 0f).Action);

        // Rising edge → Arm (once), carrying the current best lock.
        var arm = ctl.Plan(true, 0.5f);
        Assert.Equal(MissileWarningAction.Arm, arm.Action);
        Assert.Equal(0.5f, arm.BestLock, 3);
        Assert.True(ctl.IsArmed);

        // Held armed → per-frame pitch refresh, never a second Arm (not re-fired).
        var held = ctl.Plan(true, 0.75f);
        Assert.Equal(MissileWarningAction.Pitch, held.Action);
        Assert.Equal(0.75f, held.BestLock, 3);
        Assert.Equal(MissileWarningAction.Pitch, ctl.Plan(true, 1.0f).Action);
    }

    [Fact]
    public void FallingEdge_DisarmsExactlyOnce_AndStaysClear()
    {
        var ctl = new MissileWarningController();
        ctl.Plan(true, 1.0f);          // arm
        Assert.True(ctl.IsArmed);

        Assert.Equal(MissileWarningAction.Disarm, ctl.Plan(false, 0f).Action);
        Assert.False(ctl.IsArmed);

        // Once clear, further clears emit nothing (no repeated disarm).
        Assert.Equal(MissileWarningAction.None, ctl.Plan(false, 0f).Action);
    }

    [Fact]
    public void ReinfectAfterDisarm_ArmsAgain()
    {
        var ctl = new MissileWarningController();
        ctl.Plan(true, 1.0f);   // arm (missile A)
        ctl.Plan(false, 0f);    // disarm
        Assert.False(ctl.IsArmed);

        // A new missile targets the player later → arms afresh (edge-triggered).
        Assert.Equal(MissileWarningAction.Arm, ctl.Plan(true, 0.9f).Action);
    }

    [Theory]
    [InlineData(0.0f, 0.750f)]   // minimum lock → lowest pitch
    [InlineData(0.5f, 1.000f)]
    [InlineData(1.0f, 1.250f)]   // full lock → highest pitch
    public void PitchFor_Ramps_075To125_AcrossLockRange(float bestLock, float expectedPitch)
    {
        // Native SetPitch(0.75 + fBestLock/2), trekigc.cpp:5065.
        Assert.Equal(expectedPitch, MissileWarningController.PitchFor(bestLock), 3);
    }

    [Fact]
    public void PitchFor_ClampsOutOfRange()
    {
        Assert.Equal(0.75f, MissileWarningController.PitchFor(-1f), 3);
        Assert.Equal(1.25f, MissileWarningController.PitchFor(2f), 3);
    }
}
