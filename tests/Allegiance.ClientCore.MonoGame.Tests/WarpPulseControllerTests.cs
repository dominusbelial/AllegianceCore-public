using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P585 — pure tests for the opt-in aleph TRANSIT PULSE envelope
/// (<see cref="WarpPulseController"/>): the attack/ease-out curve, the
/// simultaneous-stacking cap, same-warp coalescing, and expiration pruning.
/// Failing-first: these fail against today's code (there was no controller).
/// </summary>
public class WarpPulseControllerTests
{
    [Fact]
    public void Envelope_IsZeroBeforeAndAfter_ThePulseWindow()
    {
        Assert.Equal(0f, WarpPulseController.Envelope(-0.5f));
        Assert.Equal(0f, WarpPulseController.Envelope(WarpPulseController.TotalSeconds));
        Assert.Equal(0f, WarpPulseController.Envelope(WarpPulseController.TotalSeconds + 0.2f));
    }

    [Fact]
    public void Envelope_ReachesOneAtTheAttackKnee()
    {
        // At the end of the linear attack the strength must be ~1 (peak).
        float knee = WarpPulseController.AttackSeconds;
        Assert.InRange(WarpPulseController.Envelope(knee), 0.995f, 1.001f);
    }

    [Fact]
    public void Envelope_AttacksThenEasesOutToZero()
    {
        float a = WarpPulseController.Envelope(WarpPulseController.AttackSeconds * 0.5f); // rising
        float peak = WarpPulseController.Envelope(WarpPulseController.AttackSeconds);
        float mid = WarpPulseController.Envelope(WarpPulseController.AttackSeconds + WarpPulseController.FallSeconds * 0.5f);
        float late = WarpPulseController.Envelope(WarpPulseController.TotalSeconds * 0.925f);
        // Monotonic up to the knee, then monotonic down to zero.
        Assert.True(a > 0f && a < peak, "attack should rise from 0 to 1");
        Assert.True(peak > mid && mid > late && late > 0f, "fall should ease out monotonically to 0");
        // Easy sanity: total stays inside the task's "0.6-1.0 s ease-out" window.
        Assert.InRange(WarpPulseController.TotalSeconds, 0.6f, 1.0f);
    }

    [Fact]
    public void Trigger_Then_StrengthFollowsTheEnvelope()
    {
        var c = new WarpPulseController();
        c.Trigger(7, now: 100.0);
        Assert.InRange(c.Strength(7, 100.0 + WarpPulseController.AttackSeconds), 0.995f, 1.001f);
        Assert.Equal(0f, c.Strength(7, 100.0 + WarpPulseController.TotalSeconds));   // expired
        Assert.False(c.IsActive(7, 100.0 + WarpPulseController.TotalSeconds));
    }

    [Fact]
    public void SimultaneousTriggers_AreCappedAndNewestWins()
    {
        var c = new WarpPulseController();
        // Push MORE than the cap.
        int count = WarpPulseController.MaxSimultaneous + 3;
        for (short i = 0; i < count; i++)
            c.Trigger(i, now: 50.0);
        Assert.Equal(WarpPulseController.MaxSimultaneous, c.ActiveCount);
        // The LAST warp (highest id) must still be present (newest not dropped).
        // Query just past the trigger instant — a just-triggered pulse reads 0 at
        // the exact instant (the attack starts at 0), so use now=50.04.
        Assert.True(c.IsActive((short)(count - 1), 50.04), "the newest transit must never be dropped at the cap");
    }

    [Fact]
    public void RapidSameWarpRetrigger_CoalescesInsteadOfFlickering()
    {
        var c = new WarpPulseController();
        c.Trigger(3, now: 10.0);
        c.Trigger(3, now: 10.0 + 0.05);   // within MinRepeatGapSeconds
        // A coalesced pulse keeps the ORIGINAL start (10.0), so by now=10.086 it
        // has aged 0.086s (strength ≈ 0.72). Had the second trigger RESTARTED it
        // at 10.05 the age would be only 0.036s (strength ≈ 0.30) — a flicker.
        float strength = c.Strength(3, 10.0 + 0.086);
        Assert.True(strength > 0.5f,
            $"coalesced pulse should retain its original start (got {strength}); a restart would read ~0.30");
    }

    [Fact]
    public void Update_PrunesExpiredPulses()
    {
        var c = new WarpPulseController();
        c.Trigger(1, now: 0.0);
        c.Trigger(2, now: 0.0);
        c.Update(now: WarpPulseController.TotalSeconds + 1.0);
        Assert.Equal(0, c.ActiveCount);
        Assert.False(c.IsActive(1, WarpPulseController.TotalSeconds + 1.0));
    }

    [Fact]
    public void UnrelatedWarp_HasNoStrength()
    {
        var c = new WarpPulseController();
        c.Trigger(5, now: 0.0);
        Assert.Equal(0f, c.Strength(99, 0.5));   // an untouched warp never pulses
    }
}
