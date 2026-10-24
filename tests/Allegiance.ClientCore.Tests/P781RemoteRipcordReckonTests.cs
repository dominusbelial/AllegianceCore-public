using Allegiance.ClientCore.Flight;
using Allegiance.Core;
using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P781 — a REMOTE ship with live ripcord state dead-reckons with the native
/// frozen flight model: native sets the client-side ripcord model for any
/// ripcording ship (<c>appmsg.cpp:950-962</c>) and every client-side
/// <c>CshipIGC</c> with a model runs the frozen <c>ExecuteShipMove</c>
/// (pinned turn rates, drag-only speed — shipIGC.cpp:2885-3037). The port's
/// <see cref="ShipDeadReckoner"/> previously replayed the last controls with
/// <c>Ripcording: false</c>, so a remote ripcord kept maneuvering in the
/// extrapolation. Driven both at the reckoner level and through
/// <see cref="ClientWorld.TryGetRenderPose"/> (the renderer's seam), using the
/// same synthetic pinned hull family as <c>ShipDeadReckonerTests</c>.
/// </summary>
public class P781RemoteRipcordReckonTests
{
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

    private static ServerShipUpdate UpdateFor(short id) =>
        ServerShipUpdate.FromValues(
            id, stateM: 0, new ControlData(0f, 0f, 0f, 1f), time: 1u,
            new Vector3(100f, 0f, 0f), Orientation.Identity,
            new Vector3(0f, 0f, -80f),                       // 80 u/s along the nose
            turnRates: new[] { 0.6f, -0.25f, 0.4f },        // the seed's frozen rates
            hullFrac: 1f, shieldFrac: 1f, ammoFrac: 1f, fuelFrac: 1f,
            energyFrac: 1f, oreFrac: 0f, power: 1f);

    private static ShipDeadReckoner SeededReckoner(bool ripcording)
    {
        var r = new ShipDeadReckoner();
        r.Seed(UpdateFor(9), ShipDeadReckoner.HullStats.FromHull(Hull), 0f, arrivalTime: 0.0);
        r.Ripcording = ripcording;
        return r;
    }

    [Fact]
    public void Ripcording_reckoner_keeps_constant_turn_rates_and_drags_only()
    {
        var r = SeededReckoner(ripcording: true);
        r.PoseAt(1.0, out var pos, out var ori);

        // Turn rates were pinned: the orientation integrates the SEED rates for
        // the full 1 s — compare against the shared integrator (SIM-0124).
        var stats = ShipDeadReckoner.HullStats.FromHull(Hull);
        var inputs = new ShipKinematics.Inputs(
            Controls: UpdateFor(9).Controls, StateM: 0,
            Thrust: stats.Thrust, MaxSpeed: stats.MaxSpeed, Mass: stats.Mass,
            SideMultiplier: stats.SideMultiplier, BackMultiplier: stats.BackMultiplier,
            MaxTurnRateYaw: stats.MaxTurnRateYaw, MaxTurnRatePitch: stats.MaxTurnRatePitch, MaxTurnRateRoll: stats.MaxTurnRateRoll,
            TurnTorqueYaw: stats.TurnTorqueYaw, TurnTorquePitch: stats.TurnTorquePitch, TurnTorqueRoll: stats.TurnTorqueRoll,
            AfterburnerMaxThrust: 0f, AfterburnerPower: 0f, Ripcording: true);
        var velocity = new Vector3(0f, 0f, -80f);
        var orientation = Orientation.Identity;
        var rates = new[] { 0.6f, -0.25f, 0.4f };
        var position = new Vector3(100f, 0f, 0f);
        // The reckoner's chunk schedule (native CalculateShip, TimeIncrement 0.25 s):
        // n = 1 + (int)(ΔT / 0.25), each chunk advancing by ΔT·(i/n).
        const float deltaT = 1.0f;
        int n = 1 + (int)(deltaT / Allegiance.Core.Model.Mission.TimeIncrement);
        float elapsed = 0f;
        for (int i = 1; i <= n; i++)
        {
            float nextTime = deltaT * (i / (float)n);
            float dtc = nextTime - elapsed;
            ShipKinematics.Step(in inputs, ref velocity, orientation, rates, dtc);
            position += velocity * dtc;
            elapsed = nextTime;
        }

        Assert.True((pos - position).Length() < 1e-3f,
            $"ripcording extrapolation {pos} != frozen-flight reference {position}");
        Assert.Equal(0.6f, rates[0], 5);   // the reference's rates were never ramped either

        // Drag-only decay: speed = 80·exp(−thrust·1/(mass·maxSpeed)).
        double expected = 80f * Math.Exp(-Hull.Thrust * 1.0 / (Hull.Mass * Hull.Speed));
        Assert.True(Math.Abs(velocity.Length() - expected) / expected < 1e-3);
    }

    [Fact]
    public void Non_ripcording_reckoner_keeps_the_old_behaviour()
    {
        // Without the flag the extrapolation RAMPS the turn rates toward the
        // seed controls (neutral here) — visibly different from frozen flight.
        var frozen = SeededReckoner(ripcording: true);
        var steering = SeededReckoner(ripcording: false);
        frozen.PoseAt(1.0, out var frozenPos, out var frozenOri);
        steering.PoseAt(1.0, out var steerPos, out var steerOri);

        Assert.True((frozenPos - steerPos).Length() > 1f,
            "frozen-flight and last-controls extrapolation must differ for a turning seed");
        Assert.True((frozenOri.GetForward() - steerOri.GetForward()).Length() > 1e-3f,
            "frozen orientation must differ from the ramped one");
    }

    [Fact]
    public void Ripcording_flag_survives_a_reseed()
    {
        // Native keeps the model across ProcessShipUpdate (appmsg.cpp:1154 only
        // sets/clears it from the SSU flag); the port keeps the settable flag
        // across re-seeds too — only a real clear message clears it.
        var r = SeededReckoner(ripcording: true);
        r.Seed(UpdateFor(9), ShipDeadReckoner.HullStats.FromHull(Hull), 0f, arrivalTime: 5.0);
        r.PoseAt(6.0, out var pos, out _);

        float travelled = (pos - new Vector3(100f, 0f, 0f)).Length();
        Assert.True(travelled > 0f);
        Assert.True(r.Ripcording);
    }

    [Fact]
    public void TryGetRenderPose_honours_the_ripcording_parameter()
    {
        // The renderer's seam: ClientWorld.TryGetRenderPose(..., ripcording: true)
        // must produce the frozen-flight extrapolation, not the last-controls one.
        var world = new ClientWorld { ReckonRemotes = true };
        world.RemoteShipHullResolver = (_, _) => (ShipDeadReckoner.HullStats.FromHull(Hull), 0f);
        world.Apply(new SingleShipUpdate { OidTarget = 9, Ship = UpdateFor(9) });

        double now = world.NowSeconds();
        Assert.True(world.TryGetRenderPose(9, now + 1.0, out var ripPos, out var ripOri, ripcording: true));
        // The returned orientation is the reckoner's reusable scratch — copy it
        // before the second query overwrites it (the documented contract).
        var ripForward = ripOri.GetForward();
        Assert.True(world.TryGetRenderPose(9, now + 1.0, out var steerPos, out var steerOri));

        Assert.True((ripPos - steerPos).Length() > 1f,
            "ripcording:true must change the extrapolated render pose");
        Assert.True((ripForward - steerOri.GetForward()).Length() > 1e-3f);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void P788_SingleShipUpdate_captures_the_wire_bIsRipcording_flag(bool ripcording)
    {
        // P788 — consume bIsRipcording as native (appmsg.cpp:1154 SetRipcordModel):
        // the server's authoritative flag is recorded per ship so the remote
        // dead-reckoner's frozen flight is driven from the wire, not guessed.
        var world = new ClientWorld { ReckonRemotes = true };
        world.Apply(new SingleShipUpdate { OidTarget = 9, Ship = UpdateFor(9), BIsRipcording = ripcording });
        Assert.True(world.Ships.TryGetValue(9, out var ent), "SSU must upsert the ship entity");
        Assert.Equal(ripcording, ent.WireRipcording);

        // A later update flips it — the server is the source of truth per send.
        world.Apply(new SingleShipUpdate { OidTarget = 9, Ship = UpdateFor(9), BIsRipcording = !ripcording });
        Assert.Equal(!ripcording, ent.WireRipcording);
    }

    [Fact]
    public void P788_ripcord_flag_is_last_writer_wins_like_native()
    {
        // Native: ACTIVATE sets the ripcord model (appmsg.cpp:957), ABORT clears it
        // (:981), every SSU overwrites it (:1154). A pre-activation SSU false must not
        // suppress a fresh countdown; a later SSU false ends it.
        var world = new ClientWorld { ReckonRemotes = true };
        world.Apply(new SingleShipUpdate { OidTarget = 9, Ship = UpdateFor(9), BIsRipcording = false });
        Assert.True(world.Ships.TryGetValue(9, out var ent));
        Assert.False(ent.WireRipcording);

        world.Apply(new RipcordActivate { ShipidRipcord = 9 });
        Assert.Null(ent.WireRipcording);                       // the countdown decides until the next SSU
        Assert.True(world.TryGetRemoteRipcord(9, out _));

        world.Apply(new SingleShipUpdate { OidTarget = 9, Ship = UpdateFor(9), BIsRipcording = true });
        Assert.True(ent.WireRipcording);

        world.Apply(new RipcordAborted { ShipidRipcord = 9 });
        Assert.Null(ent.WireRipcording);
        Assert.False(world.TryGetRemoteRipcord(9, out _));
    }
}
