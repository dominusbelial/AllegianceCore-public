using Allegiance.ClientCore.MonoGame.Flight;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P464 — pure tests for <see cref="TurretGunnerCadence"/>, the send-loop heart
/// shared by the live <c>Game1</c> turret controller and the loopback live test.
/// Pins native's <c>c_dsecUpdateClient</c> = 0.15 s cadence and the
/// ACTIVE-while-firing / INACTIVE-otherwise branch (clintlib.cpp:1870-1905),
/// including the rule that an ACTIVE update (which carries a server-stamped
/// <c>time</c>) is only emitted once a server-clock estimate is available.
/// </summary>
public sealed class TurretGunnerCadenceTests
{
    [Fact]
    public void No_action_until_the_control_cadence_elapses()
    {
        var c = new TurretGunnerCadence();
        Assert.Equal(TurretSendAction.Active, c.Update(1.0, fireHeld: true, clockReady: true));
        // Still inside the 0.15 s window → nothing further.
        Assert.Equal(TurretSendAction.None, c.Update(1.07, fireHeld: true, clockReady: true));
        // Past the cadence → sends again.
        Assert.Equal(TurretSendAction.Active, c.Update(1.16, fireHeld: true, clockReady: true));
    }

    [Fact]
    public void Firing_sends_active_once_the_clock_is_known()
    {
        var c = new TurretGunnerCadence();
        // Clock not ready yet → INACTIVE (safe: no timestamp needed), never ACTIVE.
        Assert.Equal(TurretSendAction.Inactive, c.Update(0.0, fireHeld: true, clockReady: false));
        // Once the clock is ready and the button still held → ACTIVE.
        Assert.Equal(TurretSendAction.Active, c.Update(0.2, fireHeld: true, clockReady: true));
    }

    [Fact]
    public void Not_firing_sends_inactive_even_when_the_clock_is_ready()
    {
        var c = new TurretGunnerCadence();
        Assert.Equal(TurretSendAction.Inactive, c.Update(0.0, fireHeld: false, clockReady: true));
    }

    [Fact]
    public void Holding_fire_sends_active_at_each_cadence_step()
    {
        var c = new TurretGunnerCadence();
        var actions = new List<TurretSendAction>();
        for (double t = 0.0; t <= 1.0; t += 0.031)     // ~32 Hz stepping
            actions.Add(c.Update(t, fireHeld: true, clockReady: true));
        // Roughly ceil(1.0 / 0.15) ≈ 7 emissions, all ACTIVE, never None at the boundary.
        int active = actions.Count(a => a == TurretSendAction.Active);
        Assert.True(active >= 6 && active <= 8, $"expected ~7 ACTIVE emissions, got {active}");
        Assert.All(actions, a => Assert.NotEqual(TurretSendAction.Inactive, a));
    }

    [Fact]
    public void Reset_rearms_so_the_next_board_sends_immediately()
    {
        var c = new TurretGunnerCadence();
        _ = c.Update(5.0, fireHeld: true, clockReady: true);      // next window starts ~5.15
        c.Reset();
        // After a reset the very next tick is eligible again (fresh board).
        Assert.Equal(TurretSendAction.Active, c.Update(5.01, fireHeld: true, clockReady: true));
    }
}
