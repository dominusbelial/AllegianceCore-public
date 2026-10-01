using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P448b — CLIENT-level wiring test: a scripted 20 Hz sequence of real
/// <c>ServerShipUpdate</c>s (sampled from a server-side <see cref="Ship"/> flying a
/// constant turn) is fed into <see cref="ClientWorld"/> through the real message
/// Apply path. The render pose for a remote ship is then queried at mid-interval
/// times via <see cref="ClientWorld.TryGetRenderPose"/> and compared to the true ship
/// pose at that instant. Before this change there is no dead-reckoner at all — the
/// render pose equals the last packet (the stale baseline, which is ~a packet behind
/// and visibly steps) — so this test cannot compile / cannot pass against the unfixed
/// port.
/// </summary>
/// <remarks>
/// What is actually being tested here, beyond <see cref="ShipDeadReckoner"/> itself
/// (P448a, <c>ShipDeadReckonerTests</c>):
/// <list type="bullet">
/// <item>that each arriving update re-seeds the per-ship reckoner at its real local
/// arrival time (the three <c>ShipEntity.Latest</c> sites in <c>ClientWorld</c>),</item>
/// <item>that <c>TryGetRenderPose</c> returns the dead-reckoned pose (forward replay
/// of the real flight model), not the raw last packet,</item>
/// <item>and that an unresolvable-hull ship falls back to the raw pose (no reckoner).</item>
/// </list>
/// </remarks>
public class ClientWorldRemoteDeadReckonTests
{
    private readonly ITestOutputHelper _output;
    public ClientWorldRemoteDeadReckonTests(ITestOutputHelper o) => _output = o;

    // Same synthetic pinned hull family as P448a's ShipDeadReckonerTests so the
    // flight numbers are deterministic and no artwork corpus is needed.
    private static readonly DataHullTypeIGC Hull = new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 335f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.566f, 4.344f, 4.976f],
        TurnTorques: [5240f, 5010f, 4860f],
        Thrust: 49600f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 870f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 701, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 510f, HardpointOffset: 571,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);

    /// <summary>A real server-side ship mid-flight: a gentle sustained turning climb
    /// at ~60 % throttle in quasi-steady manoeuvre (seed turn rates == commanded, so
    /// there is no from-rest ramp transient) — the same rig P448a used.</summary>
    private static Ship RealTurningShip()
    {
        var o = Orientation.Identity;
        o.Yaw(0.088f);
        o.Pitch(0.481f);
        o.Renormalize();
        var fwd = o.GetForward();
        float cruiseFwd = 0.5f * (1f + 0.6210f) * Hull.Speed;   // throttle-cruise equilibrium
        var s = new Ship(shipId: 9001, Hull, new Vector3(-300f, 410f, 750f), fwd * cruiseFwd, o);
        s.SetTurnRate(0, 0.0550f * Hull.MaxTurnRates[ControlData.AxisYaw]);
        s.SetTurnRate(1, -0.0610f * Hull.MaxTurnRates[ControlData.AxisPitch]);
        s.SetTurnRate(2, 0.0185f * Hull.MaxTurnRates[ControlData.AxisRoll]);
        s.Controls = new ControlData(Yaw: 0.0411f, Pitch: -0.0392f, Roll: 0.0223f, Throttle: 0.6201f);
        s.StateM = 0;
        return s;
    }

    [Fact]
    public void DeadReckonedRemote_MidInterval_TracksTrueShip_BeatsRawBaseline()
    {
        var world = new ClientWorld();
        double T = 0.0;
        world.NowSeconds = () => T;                                          // scripted clock
        world.RemoteShipHullResolver = (_, _) => (ShipDeadReckoner.HullStats.FromHull(Hull), 0f);

        const short shipId = 7001;
        var s = RealTurningShip();
        const double dt = 0.050;                                            // server default 20 upd/s
        const int ticks = 8;

        // Scripted 20 Hz feed: sample the real ship each tick, push each update into
        // the client world at the matching local arrival time, then advance the real
        // ship one tick (what the server simulates per tick). This exercises the
        // re-seed path repeatedly (ClientWorld.Apply(SingleShipUpdate)).
        Vector3 lastPacketPos = default;
        Vector3 trueMid = default;
        for (int k = 0; k < ticks; k++)
        {
            var up = ServerShipUpdate.FromModel(shipId, s, (uint)(520000 + k));   // ship at T=k*dt
            world.Apply(new SingleShipUpdate { OidTarget = shipId, Ship = up });
            T += dt;
            if (k == ticks - 1)
            {
                // Last tick: capture the TRUE pose at the mid-interval of this tick
                // (the instant the next update has not yet arrived) by advancing the
                // real flight model halfway. Also record the raw last-packet pose.
                lastPacketPos = up.Position;
                float half = (float)(dt / 2.0);
                s.ExecuteMove(T - dt, T - dt + half);
                s.Position += s.Velocity * half;
                trueMid = s.Position;
                s.ExecuteMove(T - dt + half, T);
                s.Position += s.Velocity * half;
            }
            else
            {
                s.ExecuteMove(T - dt, T);
                s.Position += s.Velocity * (float)dt;
            }
        }

        // The entity really stamped the local arrival time of the last update.
        Assert.Equal((ticks - 1) * dt, world.Ships[shipId].LatestArrivalTime, 3);

        // Query the render pose at the mid-interval of the last tick.
        double queryNow = (ticks - 1) * dt + dt / 2.0;                       // 0.35 + 0.025 = 0.375
        Assert.True(world.TryGetRenderPose(shipId, queryNow, out var drPos, out _),
            "the remote ship's dead-reckoner must be seeded and return a render pose");

        float drErr = (drPos - trueMid).Length();
        float rawErr = (lastPacketPos - trueMid).Length();                    // raw baseline = last packet

        _output.WriteLine($"DEAD-RECKONED mid-interval ({queryNow:0.000}s) pos={drPos} err={drErr:R} m");
        _output.WriteLine($"TRUE mid-interval                              pos={trueMid}");
        _output.WriteLine($"RAW (last packet)                              err={rawErr:R} m");

        // The dead-reckoned render pose is within a small tolerance of the true ship
        // pose at the mid-interval instant (the operative sub-tick gap).
        Assert.True(drErr < 0.5f,
            $"dead-reckoned mid-interval pose must be within 0.5 m of the true ship, got {drErr:R} m");

        // The raw (pre-change) baseline — render the last packet unchanged — is ~a
        // half-packet behind (≈ speed × gap), so it must be clearly worse.
        Assert.True(rawErr > 0.3f, $"raw last-packet pose should be visibly stale, got {rawErr:R} m");
        Assert.True(rawErr > 5f * drErr,
            $"raw last-packet error ({rawErr:R} m) must dwarf the dead-reckoned error ({drErr:R} m)");
    }

    [Fact]
    public void UnresolvableHull_FallsBackToRaw_NoReckonerSeeded()
    {
        var world = new ClientWorld();
        double T = 0.0;
        world.NowSeconds = () => T;
        world.RemoteShipHullResolver = (_, _) => null;                        // no hull record yet

        const short shipId = 711;
        var s = RealTurningShip();
        world.Apply(new SingleShipUpdate
        {
            OidTarget = shipId,
            Ship = ServerShipUpdate.FromModel(shipId, s, 990001u),
        });

        // The reckoner must not be seeded, so the renderer falls back to the raw
        // pose (TryGetRenderPose returns false) rather than guessing with bad stats.
        Assert.False(world.TryGetRenderPose(shipId, T, out _, out _),
            "an unresolvable-hull remote ship must have no reckoner (renderer uses its raw pose)");

        // Once a hull IS resolvable, a later update re-seeds it.
        world.RemoteShipHullResolver = (_, _) => (ShipDeadReckoner.HullStats.FromHull(Hull), 0f);
        world.Apply(new SingleShipUpdate
        {
            OidTarget = shipId,
            Ship = ServerShipUpdate.FromModel(shipId, s, 990002u),
        });
        Assert.True(world.TryGetRenderPose(shipId, T, out _, out _),
            "once the hull resolves, a later update must seed the dead-reckoner");
    }

    [Fact]
    public void ReseedSequence_AccumulatesCorrectionStats_AndStampsArrival()
    {
        var world = new ClientWorld();
        double T = 0.0;
        world.NowSeconds = () => T;
        world.RemoteShipHullResolver = (_, _) => (ShipDeadReckoner.HullStats.FromHull(Hull), 0f);

        const short shipId = 722;
        var s = RealTurningShip();
        const double dt = 0.050;
        const int ticks = 5;
        for (int k = 0; k < ticks; k++)
        {
            world.Apply(new SingleShipUpdate
            {
                OidTarget = shipId,
                Ship = ServerShipUpdate.FromModel(shipId, s, (uint)(500000 + k)),
            });
            T += dt;
            s.ExecuteMove(T - dt, T);
            s.Position += s.Velocity * (float)dt;
        }

        // Every update stamped its local arrival time; the last one wins.
        Assert.Equal((ticks - 1) * dt, world.Ships[shipId].LatestArrivalTime, 3);

        // Corrections were measured on the 2nd..Nth re-seeds (count = ticks-1).
        var (cnt, mean, max) = world.TakeRemoteCorrectionStats();
        Assert.Equal(ticks - 1, cnt);
        Assert.True(mean >= 0d, "mean correction distance must be non-negative");
        Assert.True(max >= 0f, "max correction distance must be non-negative");

        // A second poll sees only corrections accumulated since the reset (none).
        Assert.Equal(0L, world.TakeRemoteCorrectionStats().Count);
    }
}
