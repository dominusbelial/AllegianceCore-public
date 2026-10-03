using Allegiance.ClientCore.Flight;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P508 — pure tests for <see cref="OwnShipSimulator"/>, the client's LOCAL
/// own-ship simulation (the native model: the client flies its own ship and the
/// server sanity-checks). Unlike the retired <c>OwnShipPredictor</c> there is NO
/// blend/snap reconciliation against the periodic authoritative update — the sim
/// is the render/send source and is corrected only by the server's real
/// <c>SHIP_RESET</c> (a <see cref="OwnShipSimulator.Seed"/> to the server pose).
/// </summary>
public class OwnShipSimulatorTests
{
    private static readonly DataHullTypeIGC Hull = new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 335f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.566f, 4.344f, 4.976f],
        TurnTorques: [5240f, 5010f, 4860f],
        Thrust: 49600f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 870f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 545, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 511f, HardpointOffset: 581,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);

    private static float FwdAngleDiff(Vector3 fa, Vector3 fb)
    {
        float dot = Math.Clamp(fa.X * fb.X + fa.Y * fb.Y + fa.Z * fb.Z, -1f, 1f);
        return MathF.Acos(dot);   // radians
    }

    private static float Dist(Vector3 a, Vector3 b) => (a - b).Length();

    private static OwnShipSimulator MakeSim(Vector3 pos, Vector3 vel, Orientation ori)
    {
        var sim = new OwnShipSimulator();
        sim.SetHull(ShipDeadReckoner.HullStats.FromHull(Hull), 0f);
        sim.Seed(pos, vel, ori, new float[3]);
        return sim;
    }

    [Fact]
    public void ControlChange_MovesPoseOnSameStep()
    {
        // Warm up at ~60% throttle to a locked cruise.
        var ori = Orientation.Identity;
        var sim = MakeSim(new Vector3(0f, 0f, 4200f), Vector3.Zero, ori);
        var cruise = new ControlData(0f, 0f, 0f, 0.618f);
        for (int i = 0; i < 200; i++) sim.Step(0.0167f, cruise, 0);

        var preForward = sim.Orientation.GetForward();

        // Command HARD RIGHT yaw and step once.
        sim.Step(0.0167f, new ControlData(Yaw: 1f, 0f, 0f, 0.618f), 0);

        // The local sim reacts on the SAME step (no round trip — the whole point).
        Assert.True(FwdAngleDiff(preForward, sim.Orientation.GetForward()) > 1e-3f,
            "a control change must move the local sim pose on the same step");
    }

    [Fact]
    public void Throttle_AdvancesPositionAlongForward()
    {
        var sim = MakeSim(new Vector3(0f, 0f, 0f), Vector3.Zero, Orientation.Identity);
        var fwd = sim.Orientation.GetForward();
        var start = sim.Position;

        // Cruise at ~80% throttle for 2 seconds of frames.
        for (int i = 0; i < 120; i++) sim.Step(0.0167f, new ControlData(0f, 0f, 0f, 0.8f), 0);

        var disp = sim.Position - start;
        Assert.True(disp.Length() > 1f, "cruise throttle must move the ship forward");
        // Direction of travel agrees with the facing (nose-forward, +Z-ish).
        float dot = fwd.X * disp.X + fwd.Y * disp.Y + fwd.Z * disp.Z;
        Assert.True(dot > 0f, "displacement must be along the ship's forward");
    }

    [Fact]
    public void NoHull_StepIsInert()
    {
        var sim = new OwnShipSimulator();
        Assert.False(sim.HaveHull);
        sim.Step(0.05f, ControlData.Neutral, 0);   // no crash
        Assert.Equal(Vector3.Zero, sim.Position);
        Assert.Equal(Vector3.Zero, sim.Velocity);
    }

    [Fact]
    public void Freeze_HoldsPose_UntilResume()
    {
        var sim = MakeSim(new Vector3(100f, 0f, 0f), Vector3.Zero, Orientation.Identity);
        var frozenPos = sim.Position;
        sim.Freeze();
        Assert.True(sim.Frozen);

        // Controls command a turn + thrust, but a frozen sim must not move.
        sim.Step(0.05f, new ControlData(Yaw: 1f, 0f, 0f, 0.8f), 0);
        Assert.True(Dist(sim.Position, frozenPos) < 1e-6f, "frozen sim must hold the server pose");

        sim.Resume();
        Assert.False(sim.Frozen);
        sim.Step(0.05f, new ControlData(Yaw: 1f, 0f, 0f, 0.8f), 0);
        Assert.True(Dist(sim.Position, frozenPos) > 1e-6f, "after Resume the sim must move again");
    }

    [Fact]
    public void Seed_SnapsToServerPose_AndContinues()
    {
        var sim = MakeSim(new Vector3(0f, 0f, 0f), Vector3.Zero, Orientation.Identity);
        sim.Step(0.05f, new ControlData(0f, 0f, 0f, 0.6f), 0);

        // A SHIP_RESET re-seeds the sim to the server's authoritative pose.
        var serverOri = Orientation.Identity;
        serverOri.Yaw(0.5f);
        serverOri.Renormalize();
        sim.Seed(new Vector3(900f, 50f, -300f), new Vector3(40f, 0f, 0f), serverOri, new float[] { 1f, 0f, 0f });

        Assert.True(Dist(sim.Position, new Vector3(900f, 50f, -300f)) < 1e-4f);
        Assert.True(Dist(sim.Velocity, new Vector3(40f, 0f, 0f)) < 1e-3f);
        Assert.True(FwdAngleDiff(sim.Orientation.GetForward(), serverOri.GetForward()) < 1e-3f);
        Assert.False(sim.Frozen, "after a SHIP_RESET seed the sim must resume flying");
        Assert.Equal(1f, sim.TurnRates[0], 3);

        // It continues from the reset pose.
        var before = sim.Position;
        sim.Step(0.025f, new ControlData(0f, 0f, 0f, 0.6f), 0);
        Assert.True(Dist(sim.Position, before) > 1e-4f);
    }

    [Fact]
    public void ReportedState_ReflectsLastSteppedControls()
    {
        var sim = MakeSim(Vector3.Zero, Vector3.Zero, Orientation.Identity);
        var c = new ControlData(Yaw: 0.3f, Pitch: -0.2f, Roll: 0.1f, Throttle: 0.7f);
        sim.Step(0.05f, c, 5);

        // The send path reads Controls/StateM the sim is flying with.
        Assert.Equal(0.3f, sim.Controls.Yaw, 3);
        Assert.Equal(-0.2f, sim.Controls.Pitch, 3);
        Assert.Equal(5, sim.StateM);
        Assert.Equal(3, sim.TurnRates.Length);
        Assert.InRange(sim.Power, 0f, 1f);
    }
}
