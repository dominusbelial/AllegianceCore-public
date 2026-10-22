using Allegiance.ClientCore.Flight;
using Allegiance.Core;
using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P781 — the client ripcord state machine, driven through the real wire
/// messages (the same <see cref="ClientWorld.Apply"/> path the HUD-12/P414
/// tests use) plus the pure <see cref="RipcordLeadup"/> timeline. Pins the
/// native rule that the client-side ripcord state is "the model is set"
/// (appmsg.cpp:899-988): set on ACTIVATE, cleared on ABORTED, own SET_CLUSTER,
/// SHIP_RESET, kill, delete and reset — and NOT cleared when the countdown
/// reaches 0 (native pins at 0, shipIGC.cpp:286-294; the port-only 2 s safety
/// grace stands in for the server's silent teleport).
/// </summary>
public class P781RipcordStateTests
{
    private static ServerShipUpdate PoseFor(short id) =>
        ServerShipUpdate.FromValues(
            id, stateM: 0, new ControlData(0f, 0f, 0f, 1f), time: 1u,
            new Vector3(id, 0f, 0f), Orientation.Identity, Vector3.Zero,
            turnRates: new[] { 0f, 0f, 0f },
            hullFrac: 1f, shieldFrac: 1f, ammoFrac: 1f, fuelFrac: 1f,
            energyFrac: 1f, oreFrac: 0f, power: 1f);

    private static ClientWorld OwnWorld(short ownId = 5)
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster { SectorId = 1, Ship = PoseFor(ownId) });   // "here is your ship"
        return world;
    }

    /// <summary>A controllable world clock (ClientWorld.NowSeconds is a seam) so
    /// start-time stamps are deterministic across Apply calls. Mutate
    /// <see cref="DoubleRef.Value"/> between Applies.</summary>
    private static ClientWorld ClockWorld(short ownId, out DoubleRef clock)
    {
        var world = OwnWorld(ownId);
        var t = new DoubleRef(100.0);
        world.NowSeconds = () => t.Value;
        clock = t;
        return world;
    }

    private sealed class DoubleRef(double initial) { public double Value = initial; }

    private static RipcordActivate Activate(short shipId, short ot = (short)IgcObjectType.Station, short oid = 100) => new()
    {
        ShipidRipcord = shipId, OtRipcord = ot, OidRipcord = oid, SidRipcord = 2,
    };

    // ── own ship ──────────────────────────────────────────────────────────

    [Fact]
    public void Own_activate_sets_state_and_start()
    {
        var world = OwnWorld();
        world.Apply(Activate(5));
        Assert.True(world.RipcordActive);
        Assert.NotNull(world.RipcordActiveSinceLocalMs);
        Assert.Equal((short)2, world.RipcordTargetSectorId);
    }

    [Fact]
    public void Own_aborted_clears()
    {
        var world = OwnWorld();
        world.Apply(Activate(5));
        world.Apply(new RipcordAborted { ShipidRipcord = 5 });
        Assert.False(world.RipcordActive);
        Assert.Null(world.RipcordActiveSinceLocalMs);
    }

    [Fact]
    public void Own_set_cluster_clears()
    {
        var world = OwnWorld();
        world.Apply(Activate(5));
        world.Apply(new SetCluster { SectorId = 2, Ship = PoseFor(5) });
        Assert.False(world.RipcordActive);
    }

    [Fact]
    public void Own_ship_reset_clears()
    {
        var world = OwnWorld();
        world.Apply(Activate(5));
        world.Apply(new ShipReset { Ship = PoseFor(5), Cookie = 7 });
        Assert.False(world.RipcordActive);
    }

    [Fact]
    public void Retarget_same_anchor_keeps_the_running_countdown()
    {
        // Native appmsg.cpp:940-944 — only a model CHANGE resets the countdown;
        // a repeated ACTIVATE naming the same anchor keeps it running.
        var world = ClockWorld(5, out var clock);
        world.Apply(Activate(5, ot: (short)IgcObjectType.Station, oid: 100));
        long start = world.RipcordActiveSinceLocalMs!.Value;
        clock.Value += 1.0;   // 1 s of countdown elapses before the re-target

        world.Apply(Activate(5, ot: (short)IgcObjectType.Station, oid: 100));
        Assert.True(world.RipcordActive);
        Assert.Equal(start, world.RipcordActiveSinceLocalMs);   // not restarted
    }

    [Fact]
    public void Retarget_to_a_different_anchor_restarts_the_countdown()
    {
        var world = ClockWorld(5, out var clock);
        world.Apply(Activate(5, ot: (short)IgcObjectType.Station, oid: 100));
        long start1 = world.RipcordActiveSinceLocalMs!.Value;
        clock.Value += 1.5;
        world.Apply(Activate(5, ot: (short)IgcObjectType.Probe, oid: 200));
        Assert.True(world.RipcordActive);
        Assert.Equal(start1 + 1500, world.RipcordActiveSinceLocalMs);   // re-stamped
    }

    [Fact]
    public void Fresh_activate_after_a_clear_restarts_the_countdown()
    {
        // SetRipcordModel(NULL) on abort means the next ACTIVATE is always a
        // model change (native clears the model on abort, appmsg.cpp:981).
        var world = ClockWorld(5, out var clock);
        world.Apply(Activate(5));
        long start = world.RipcordActiveSinceLocalMs!.Value;
        world.Apply(new RipcordAborted { ShipidRipcord = 5 });
        clock.Value += 2.0;
        world.Apply(Activate(5));
        Assert.Equal(start + 2000, world.RipcordActiveSinceLocalMs);
    }

    // ── remote ships ──────────────────────────────────────────────────────

    [Fact]
    public void Remote_activate_sets_state_abort_kill_delete_clear_it()
    {
        const short remote = 9;
        var world = OwnWorld();
        world.Apply(new SingleShipUpdate { OidTarget = remote, Ship = PoseFor(remote) });

        world.Apply(Activate(remote));
        Assert.True(world.TryGetRemoteRipcord(remote, out _));

        // Native's remote handler ALWAYS resets the countdown (appmsg.cpp:950-962),
        // so a repeated remote ACTIVATE re-stamps the start.
        world.NowSeconds = () => 100.0;
        world.Apply(Activate(remote));
        Assert.True(world.TryGetRemoteRipcord(remote, out long s1));
        world.NowSeconds = () => 101.0;
        world.Apply(Activate(remote));
        Assert.True(world.TryGetRemoteRipcord(remote, out long s2));
        Assert.Equal(101_000, s2);

        world.Apply(new RipcordAborted { ShipidRipcord = remote });
        Assert.False(world.TryGetRemoteRipcord(remote, out _));

        world.Apply(Activate(remote));
        world.Apply(new KillShip
        {
            ShipId = remote, TypeCredit = (short)IgcObjectType.Ship, IdCredit = 5,
            SideIdKiller = 1, BKillCredit = true, BDeathCredit = true,
        });
        Assert.False(world.TryGetRemoteRipcord(remote, out _));

        world.Apply(Activate(remote));
        world.Apply(new ShipDelete { ShipId = remote, Sdr = ShipDelete.SdrLeftSector });
        Assert.False(world.TryGetRemoteRipcord(remote, out _));
    }

    // ── the pure timeline ─────────────────────────────────────────────────

    [Fact]
    public void IsRipcording_survives_zero_and_expires_at_the_safety_grace()
    {
        long start = 10_000;
        const float dur = 3f;

        // Straight through the countdown and 1 s past zero (native pins at 0).
        Assert.True(RipcordLeadup.IsRipcording(start, start + 0, dur, 1.0));
        Assert.True(RipcordLeadup.IsRipcording(start, start + 3000, dur, 1.0));
        Assert.True(RipcordLeadup.IsRipcording(start, start + 4000, dur, 1.0));
        // The port-only safety grace: expired at duration + 2.1 s.
        Assert.False(RipcordLeadup.IsRipcording(start, start + 5100, dur, 1.0));
        // Exactly at the grace boundary is already out (overrun >= 2.0).
        Assert.False(RipcordLeadup.IsRipcording(start, start + 5000, dur, 1.0));
        // No start / unknown duration.
        Assert.False(RipcordLeadup.IsRipcording(null, start + 100, dur, 1.0));
        Assert.True(RipcordLeadup.IsRipcording(start, start + 60_000, 0f, 1.0));   // unknown duration fails open

        // TiDi: the elapsed wall time is scaled (the shared HUD rule).
        Assert.Equal(dur, RipcordLeadup.TimeLeft(start, start, dur, 0.5));
        Assert.Equal(1f, RipcordLeadup.TimeLeft(start, start + 4000, dur, 0.5));   // 4 s wall at scale 0.5 = 2 s sim
    }

    [Fact]
    public void OverrunSeconds_measures_the_safety_window()
    {
        long start = 10_000;
        Assert.Equal(0f, RipcordLeadup.OverrunSeconds(start, start + 3000, 3f, 1.0), 4);
        Assert.Equal(-1f, RipcordLeadup.OverrunSeconds(start, start + 4000, 3f, 1.0), 4);
        Assert.Equal(-2f, RipcordLeadup.OverrunSeconds(start, start + 5000, 3f, 1.0), 4);   // 2 s PAST the zero
    }

    [Fact]
    public void Countdown_state_survives_the_zero_into_the_grace_window()
    {
        // The HUD rule split: the label is gone at left <= 0, but the state the
        // client flies/sends with is still "ripcording" 1 s past zero, and gone
        // past the grace. (On the unfixed port the HUD's self-expiry cleared the
        // whole state AT 0 — the live-client P781 behaviour this replaces.)
        long start = 10_000;
        const float dur = 3f;
        long atZero = start + (long)(dur * 1000);
        Assert.True(RipcordLeadup.TimeLeft(start, atZero + 500, dur, 1.0) <= 0f);          // label gone
        Assert.True(RipcordLeadup.IsRipcording(start, atZero + 1000, dur, 1.0));           // still flying frozen
        Assert.False(RipcordLeadup.IsRipcording(start, atZero + 2100, dur, 1.0));          // safety expiry
    }
}
