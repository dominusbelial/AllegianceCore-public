using Allegiance.ClientCore.Flight;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P508 — pure tests for <see cref="ShipUpdateCadence"/>, the 0.15 s
/// (<c>c_dsecUpdateClient</c>) emission cadence of the native <c>SHIP_UPDATE</c>
/// (matching native's <c>BaseClient::SendUpdate</c>, which does NOT send on control
/// changes — <c>smSendUpdateNow</c> is commented out natively).
/// </summary>
public sealed class ShipUpdateCadenceTests
{
    [Fact]
    public void Fires_immediately_after_reset_then_every_cadence()
    {
        var c = new ShipUpdateCadence();
        Assert.True(c.Update(0.0), "after construction/reset the next send fires immediately");
        Assert.False(c.Update(0.099), "inside the 0.15 s window: not due");
        Assert.True(c.Update(0.15), "due again exactly one cadence later");
        Assert.False(c.Update(0.20), "just past the last fire: not due yet");
        Assert.False(c.Update(0.299), "still inside the window");
        Assert.True(c.Update(0.30), "due again two cadences from the first fire");
    }

    [Fact]
    public void Reset_forces_immediate_next_send()
    {
        var c = new ShipUpdateCadence();
        Assert.True(c.Update(5.0));
        c.Reset();                                   // e.g. resuming flight after a pause
        Assert.True(c.Update(5.003), "after Reset the next send fires immediately, not after the stale window");
    }

    [Fact]
    public void Long_pause_sends_immediately_on_resume()
    {
        var c = new ShipUpdateCadence();
        Assert.True(c.Update(1.0));
        Assert.False(c.Update(1.005));
        // A long pause (docked / turret) during which Update is not called; on resume
        // nowSeconds is far past the schedule => the very next call sends immediately.
        Assert.True(c.Update(30.0), "after a long pause the resumed cadence fires immediately");
    }
}
