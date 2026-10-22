using Allegiance.ClientCore.Flight;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P781 — the own ship flies the NATIVE frozen flight model during a ripcord
/// countdown (<see cref="OwnShipSimulator.SetRipcording"/>): turn rates pinned
/// at their activation values (the ramp is guarded by
/// <c>if (!m_pmodelRipcord)</c>, shipIGC.cpp:2885-2910), the orientation still
/// integrating those rates (shipIGC.cpp:2912-2917), engine thrust skipped
/// (shipIGC.cpp:2961-2962) so the speed decays drag-only
/// <c>v ← v·exp(−thrust·dt/(mass·maxSpeed))</c> (shipIGC.cpp:2926-2934), and an
/// active afterburner NOT gated (it folds into drag, shipIGC.cpp:2939-2957, and
/// still accelerates forward). Exactly what <see cref="ShipKinematics.Step"/>
/// already does with <c>Ripcording: true</c> (SIM-0124, ShipRipcordFreezeTests) —
/// these tests pin the <see cref="OwnShipSimulator"/> wiring to it.
/// The hull is the same synthetic pinned fixture
/// <see cref="Allegiance.Core.Tests.ShipKinematicsGoldenTests"/> uses.
/// </summary>
public class P781OwnShipRipcordFlightTests
{
    private static readonly DataHullTypeIGC DemoHull = new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 535f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.666f, 4.444f, 5.022f],
        TurnTorques: [5100f, 4900f, 4700f],
        Thrust: 52800f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 950f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 509, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 515f, HardpointOffset: 563,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);

    private static readonly DataPartTypeIGC AbType = new(
        Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "ab", Description: "",
        GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
        PartId: 9300, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
        Type: new DataPartTypeIGC.Afterburner(
            FuelConsumption: 0f, MaxThrust: 60000f, OnRate: 1e6f, OffRate: 1e6f,
            InteriorSound: 0, ExteriorSound: 0));

    private static readonly float[] SeedRates = [0.8f, -0.3f, 0.5f];
    private const float SeedSpeed = 80f;
    private static readonly ControlData FullStick = new(Yaw: 1f, Pitch: 1f, Roll: 1f, Throttle: 1f);

    private static Orientation Identity => Orientation.Identity;

    private static OwnShipSimulator SeededSim(bool ripcording, float abPower = 0f)
    {
        var sim = new OwnShipSimulator();
        sim.SetHull(ShipDeadReckoner.HullStats.FromHull(DemoHull), afterburnerMaxThrust: abPower > 0 ? 60000f : 0f);
        if (abPower > 0f && AbType.Type is DataPartTypeIGC.Afterburner ab)
            sim.SetAfterburner(ab, mounted: 255,
                fuel: 1000f, mountRate: 1f, power: abPower);
        sim.Seed(new Vector3(0f, 0f, 0f), Identity.GetForward() * SeedSpeed, Identity, (float[])SeedRates.Clone());
        sim.SetRipcording(ripcording);
        return sim;
    }

    [Fact]
    public void Frozen_flight_pins_turn_rates_for_the_whole_countdown()
    {
        var sim = SeededSim(ripcording: true);
        for (int i = 0; i < 40; i++)                       // 2 s of full-stick commands
            sim.Step(0.05f, FullStick, 0);

        float[] rates = sim.TurnRates;
        Assert.Equal(SeedRates[0], rates[0], 5);
        Assert.Equal(SeedRates[1], rates[1], 5);
        Assert.Equal(SeedRates[2], rates[2], 5);
    }

    [Fact]
    public void Frozen_flight_integrates_exactly_like_ShipKinematics_with_Ripcording_true()
    {
        var sim = SeededSim(ripcording: true);
        sim.Step(2f, FullStick, 0);

        // Reference: the shared flight step (SIM-0124) from the same seed, same
        // 0.05 s substep schedule OwnShipSimulator uses.
        var stats = ShipDeadReckoner.HullStats.FromHull(DemoHull);
        var inputs = new ShipKinematics.Inputs(
            Controls: FullStick, StateM: 0,
            Thrust: stats.Thrust, MaxSpeed: stats.MaxSpeed, Mass: stats.Mass,
            SideMultiplier: stats.SideMultiplier, BackMultiplier: stats.BackMultiplier,
            MaxTurnRateYaw: stats.MaxTurnRateYaw, MaxTurnRatePitch: stats.MaxTurnRatePitch, MaxTurnRateRoll: stats.MaxTurnRateRoll,
            TurnTorqueYaw: stats.TurnTorqueYaw, TurnTorquePitch: stats.TurnTorquePitch, TurnTorqueRoll: stats.TurnTorqueRoll,
            AfterburnerMaxThrust: 0f, AfterburnerPower: 0f, Ripcording: true);
        var velocity = Identity.GetForward() * SeedSpeed;
        var orientation = Identity.Copy();
        var rates = (float[])SeedRates.Clone();
        var position = new Vector3(0f, 0f, 0f);
        for (int i = 0; i < 40; i++)
        {
            ShipKinematics.Step(in inputs, ref velocity, orientation, rates, 0.05f);
            position += velocity * 0.05f;
        }

        Assert.True((position - sim.Position).Length() < 1e-3f,
            $"own sim position {sim.Position} != ShipKinematics reference {position}");
        // The orientation is the constant-rate integration of the PINNED seed
        // rates (shipIGC.cpp:2912-2917) — not the full-stick rates.
        var simForward = sim.Orientation.GetForward();
        var simUp = sim.Orientation.GetUp();
        Assert.True((simForward - orientation.GetForward()).Length() < 1e-4f,
            $"own sim forward {simForward} != reference {orientation.GetForward()}");
        Assert.True((simUp - orientation.GetUp()).Length() < 1e-4f,
            $"own sim up {simUp} != reference {orientation.GetUp()}");
        // And it really turned (2 s at ~0.99 rad/s total rate).
        Assert.True(simForward * Identity.GetForward() < 0.9f);
    }

    [Fact]
    public void Frozen_flight_speed_decays_exponentially_toward_zero()
    {
        var sim = SeededSim(ripcording: true);
        sim.Step(2f, FullStick, 0);

        // With no afterburner the engine vector is zero (shipIGC.cpp:2961-2962),
        // so every substep multiplies the velocity by
        // f = exp(−thrust·dt/(mass·maxSpeed)) — 2 s of substeps compounds to
        // exp(−thrust·2/(mass·maxSpeed)).
        double expectedFactor = Math.Exp(-DemoHull.Thrust * 2.0 / (DemoHull.Mass * DemoHull.Speed));
        double expected = SeedSpeed * expectedFactor;
        double actual = sim.Velocity.Length();
        Assert.True(Math.Abs(actual - expected) / expected < 1e-3,
            $"speed {actual} != drag-only decay {expected}");
        // And it DID decay (the ship coasts, it does not hold speed).
        Assert.True(actual < SeedSpeed * 0.5);
    }

    [Fact]
    public void Frozen_flight_afterburner_still_accelerates_forward()
    {
        // Native does NOT gate the afterburner while ripcording — its
        // power·abThrust·backward term is folded into drag
        // (shipIGC.cpp:2939-2957), so the ship speeds UP.
        var sim = SeededSim(ripcording: true, abPower: 1f);
        sim.Step(2f, FullStick, ShipControlState.AfterburnerButton);

        Assert.True(sim.Velocity.Length() > SeedSpeed,
            $"an active afterburner must keep accelerating (got {sim.Velocity.Length()})");
    }

    [Fact]
    public void Disengaging_resumes_the_turn_rate_ramp_toward_the_stick()
    {
        var sim = SeededSim(ripcording: true);
        sim.Step(1f, FullStick, 0);
        float frozenYaw = sim.TurnRates[0];

        sim.SetRipcording(false);
        sim.Step(1f, FullStick, 0);

        // The ramp runs again: the yaw rate moved toward the commanded
        // max (1.0 × 4.666 rad/s), far past its frozen value.
        Assert.True(sim.TurnRates[0] > frozenYaw + 0.5f,
            $"rates must ramp toward the stick after disengage (frozen {frozenYaw}, now {sim.TurnRates[0]})");
    }

    [Fact]
    public void SetHull_preserves_the_ripcord_state()
    {
        var sim = SeededSim(ripcording: true);
        // A mid-countdown hull reconfigure must not silently unfreeze the ship.
        sim.SetHull(ShipDeadReckoner.HullStats.FromHull(DemoHull), 0f);
        sim.Step(0.5f, FullStick, 0);
        Assert.Equal(SeedRates[0], sim.TurnRates[0], 5);   // still pinned
    }
}
