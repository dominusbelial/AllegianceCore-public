using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

public sealed partial class BotFixRegressionTests
{
    private static void P707Motion(ClientWorld world, short id, Vector3 position, float speed)
    {
        var pose = ServerShipUpdate.FromValues(id, 0, ControlData.Neutral, 0,
            position, Orientation.Identity, new Vector3(speed, 0, 0), [0, 0, 0],
            1, 1, 1, 1, 1, 0, 0);
        world.Apply(new SingleShipUpdate { OidTarget = id, Ship = pose });
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(.5f)]
    public void P707_SubthresholdDriftDoesNotResetMovementClockOrHideIdle(float speed)
    {
        using var cs = Session(true);
        using var probe = Session();
        var bot = Bot(cs);
        Set(bot, "TargetId", (short?)9);
        Set(bot, "LastMoveAt", 0.0);
        var manager = Manager(bot);
        Set(manager, "<Probe>k__BackingField", probe);
        var metric = new IdleTargetMetrics();
        Set(manager, "_idleTargetMetrics", metric);

        // The observer continues receiving tiny position corrections (and may
        // even disagree about speed). The bot's own velocity stays below 1 m/s.
        foreach (double now in new[] { 1.0, 2.1, 4.0, 6.0 })
        {
            P707Motion(cs.World, 1, new Vector3((float)now * .001f, 0, 0), speed);
            P707Motion(probe.World, 1, new Vector3((float)now * .002f, 0, 0), 20);
            Assert.Equal(0f, cs.World.Ships[1].Latest!.Velocity.Length());
            Call("SampleCadence", manager, bot, now);
        }

        Assert.Equal(0.0, Get<double>(bot, "LastMoveAt"));
        Assert.Equal(3, Get<System.Collections.IList>(bot, "Samples").Count);
        Call("SampleIdleTargets", manager, ArrayOf(bot), 6.0);
        Assert.Equal(new IdleTargetReport(1, 1, 0), metric.Snapshot(6));
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(20f)]
    public void P707_OwnVelocityRefreshesMovementClockEvenWithoutPositionChange(float speed)
    {
        using var cs = Session(true);
        using var probe = Session();
        var bot = Bot(cs);
        Set(bot, "LastMoveAt", 0.0);
        var manager = Manager(bot);
        Set(manager, "<Probe>k__BackingField", probe);

        // Observer pose remains unchanged; the owning connection reports motion.
        P707Motion(cs.World, 1, Vector3.Zero, speed);
        Call("SampleCadence", manager, bot, 1.0);
        Call("SampleCadence", manager, bot, 2.0);
        Assert.Equal(2.0, Get<double>(bot, "LastMoveAt"));

        P707Motion(cs.World, 1, Vector3.Zero, 0);
        Call("SampleCadence", manager, bot, 3.0);
        Assert.Equal(2.0, Get<double>(bot, "LastMoveAt"));
    }

    [Trait("Requires", "Artwork")]
    [Fact]
    public void P707_CloseCombatStrafesAfterTwoSecondsDespiteObserverDrift()
    {
        using var reads = new DefenseLiveReads();
        using var cs = Session(true);
        using var probe = Session();
        DefenseEnemy(cs.World);
        P700Move(cs.World, 9, new Vector3(0, 0, -100));
        var bot = Bot(cs);
        P700Weapon(bot);
        Set(bot, "LastMoveAt", 0.0);
        var sender = P700Sender(bot);
        var manager = Manager(bot);
        Set(manager, "<Probe>k__BackingField", probe);

        foreach (double now in new[] { 1.0, 2.1, 2.2, 2.3, 4.0, 6.0 })
        {
            // A newly applied strafe can move the raw position before speed
            // reaches the wire's 1 m/s floor. Keep thrusting through that pulse.
            P707Motion(cs.World, 1, new Vector3((float)now * .001f, 0, 0), .5f);
            P707Motion(probe.World, 1, new Vector3((float)now * .001f, 0, 0), 0);
            Call("SampleCadence", manager, bot, now);
            Call("EngageShipAt", null, bot, cs, (short)9, now, 1, 1, false);
            var controls = P700LastControls(sender).Payload;
            // Preserve native close-band speed matching and firing. The
            // existing showcase liveness policy adds native lateral thrust.
            Assert.Equal(-1f, controls.Controls.Throttle);
            Assert.NotEqual(0, controls.StateM & ShipControlState.AllWeapons);
            Assert.Equal(now >= BotManager.CombatStationarySeconds,
                (controls.StateM & (ShipControlState.LeftButton | ShipControlState.RightButton)) != 0);
        }
    }

    [Trait("Requires", "Artwork")]
    [Fact]
    public void P707_FirstMovingEchoDoesNotCancelTheEscapePulse()
    {
        using var reads = new DefenseLiveReads();
        using var cs = Session(true);
        using var probe = Session();
        DefenseEnemy(cs.World);
        P700Move(cs.World, 9, new Vector3(0, 0, -100));
        var bot = Bot(cs);
        P700Weapon(bot);
        Set(bot, "LastMoveAt", 0.0);
        var sender = P700Sender(bot);
        var manager = Manager(bot);
        Set(manager, "<Probe>k__BackingField", probe);

        P707Motion(cs.World, 1, Vector3.Zero, 0);
        Call("EngageShipAt", null, bot, cs, (short)9, 2.1, 1, 1, false);
        int lateral = ShipControlState.LeftButton | ShipControlState.RightButton;
        Assert.NotEqual(0, P700LastControls(sender).Payload.StateM & lateral);

        // One moving echo can be shorter than the observer's sampling interval.
        // Keep the escape command through that echo, rather than immediately
        // braking back to zero and restarting the two-second wait.
        P707Motion(cs.World, 1, Vector3.Zero, 1);
        Call("SampleCadence", manager, bot, 2.15);
        Call("EngageShipAt", null, bot, cs, (short)9, 2.15, 1, 1, false);
        Assert.NotEqual(0, P700LastControls(sender).Payload.StateM & lateral);

        Call("SampleCadence", manager, bot, 2.7);
        Call("EngageShipAt", null, bot, cs, (short)9, 2.7, 1, 1, false);
        var settled = P700LastControls(sender).Payload;
        Assert.Equal(0, settled.StateM & lateral);
        Assert.Equal(-1f, settled.Controls.Throttle);
        Assert.NotEqual(0, settled.StateM & ShipControlState.AllWeapons);
    }
}
