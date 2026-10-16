using System.Linq;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P711 — the ship low-status / combat feedback alarm cues (SIM-0361..0366,
/// SIM-0368, SIM-0370, SIM-0425, SIM-0467): the four fraction low alarms, the
/// two no-ammo/no-fuel attempt cues, the ripcord-active and mining loops, and
/// the ripcord-denied one-shot. The edge detector is the pure
/// <see cref="ShipStatusAlarms.Classify"/> (native trekigc.cpp:1264-1275); the
/// per-frame driver <see cref="ClientWorld.UpdateOwnStatusAlarms"/> samples the
/// local ship's own streamed ServerShipUpdate and records the one-shot events;
/// the ripcord-denied cue rides the real RIPCORD_DENIED(186) wire; the loops
/// are gated by <see cref="ClientWorld.RipcordActive"/> /
/// <see cref="ClientWorld.MiningAsteroidId"/>.
/// </summary>
public class ShipStatusAlarmSoundTests
{
    private static void SetMySide(ClientWorld world, short side) =>
        world.Apply(new ExportShip { ShipId = -1, SideId = side });

    private static ServerShipUpdate PoseFor(short id, float hull = 1f, float ammo = 1f,
        float fuel = 1f, float energy = 1f, int stateM = 0) =>
        ServerShipUpdate.FromValues(
            id, stateM: stateM, new ControlData(0f, 0f, 0f, 0f), time: 1u,
            Vector3.Zero, Orientation.Identity, Vector3.Zero,
            turnRates: new[] { 0f, 0f, 0f },
            hullFrac: hull, shieldFrac: 1f, ammoFrac: ammo, fuelFrac: fuel,
            energyFrac: energy, oreFrac: 0f, power: 0f);

    /// <summary>Seed MyShipId + the own ship's first pose via SET_CLUSTER, then
    /// drive the per-frame alarm detector once (arming the baseline) and DRAIN
    /// any arming-pass events, so the next sampled state is compared against a
    /// real armed snapshot and only NEW edge events remain in the queue.</summary>
    private static ClientWorld ArmedWorld(short myId, ServerShipUpdate baseline)
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster { SectorId = 1, Ship = baseline, Cookie = 0x1 });
        world.UpdateOwnStatusAlarms();      // arm
        world.TakeSoundEvents();            // discard the arming-pass emissions
        return world;
    }

    // ── ShipStatusAlarms.Classify — pure edge detector ──────────────────────

    [Fact]
    public void HullCrossingBelow_EmitsExactlyHullLow()
    {
        var fired = ShipStatusAlarms.Classify(
            new ShipStatusAlarmState(0.2f, 1f, 1f, 1f, false, false),
            new ShipStatusAlarmState(0.10f, 1f, 1f, 1f, false, false));
        Assert.Equal(ClientSoundEventKind.HullLow, Assert.Single(fired));
    }

    [Fact]
    public void CrossingsAboveThreshold_DoNotRefire()
    {
        // A low-low pair (both below the 0.15 threshold) fires nothing — the
        // crossing only fires once, on the ≥→< edge (native's m_fHullFraction
        // >= threshold gate, trekigc.cpp:1264). Staying low never re-triggers.
        Assert.Empty(ShipStatusAlarms.Classify(
            new ShipStatusAlarmState(0.009f, 1f, 1f, 1f, false, false),
            new ShipStatusAlarmState(0.007f, 1f, 1f, 1f, false, false)));
    }

    [Fact]
    public void AllFourLowAlarms_FireIndependently()
    {
        var prev = new ShipStatusAlarmState(0.3f, 0.3f, 0.3f, 0.07f, false, false);
        var next = new ShipStatusAlarmState(0.1f, 0.1f, 0.1f, 0.03f, false, false);
        var fired = ShipStatusAlarms.Classify(prev, next).ToHashSet();
        Assert.Contains(ClientSoundEventKind.HullLow, fired);
        Assert.Contains(ClientSoundEventKind.AmmoLow, fired);
        Assert.Contains(ClientSoundEventKind.FuelLow, fired);
        Assert.Contains(ClientSoundEventKind.EnergyLow, fired);
    }

    [Fact]
    public void NoAmmo_And_NoFuel_Edge_FireOnFalseToTrue()
    {
        // An unfired state (no intent) → intent with the resource exhausted.
        var fired = ShipStatusAlarms.Classify(
            new ShipStatusAlarmState(1f, 0f, 0f, 1f, false, false),
            new ShipStatusAlarmState(1f, 0f, 0f, 1f, true, true)).ToHashSet();
        Assert.Contains(ClientSoundEventKind.NoAmmo, fired);
        Assert.Contains(ClientSoundEventKind.NoFuel, fired);
    }

    [Fact]
    public void NoAmmo_Persisting_DoesNotRefire()
    {
        // Still exhausting / still trying — the latch gate (trekigc.cpp:1272)
        // means no second cue.
        Assert.Empty(ShipStatusAlarms.Classify(
            new ShipStatusAlarmState(1f, 0f, 1f, 1f, true, false),
            new ShipStatusAlarmState(1f, 0f, 1f, 1f, true, false)));
    }

    // ── ClientWorld.UpdateOwnStatusAlarms — wire-driven driver ───────────────

    [Fact]
    public void OwnShipHullDrop_RecordsHullLowEvent()
    {
        var world = ArmedWorld(7, PoseFor(7, hull: 0.2f));
        // Same ship, hull now 0.10 → the downward crossing fires a one-shot.
        world.Apply(new SingleShipUpdate { OidTarget = 7, Ship = PoseFor(7, hull: 0.10f) });
        world.UpdateOwnStatusAlarms();

        var ev = Assert.Single(world.TakeSoundEvents());
        Assert.Equal(ClientSoundEventKind.HullLow, ev.Kind);
        Assert.Equal(7, ev.Id);
    }

    [Fact]
    public void OwnShipFiringEmptyMagazine_RecordsNoAmmoEvent()
    {
        var world = ArmedWorld(7, PoseFor(7, ammo: 0.001f, stateM: 0));
        // Pilot presses fire (AllWeapons) while ammo exhausted.
        world.Apply(new SingleShipUpdate
        {
            OidTarget = 7,
            Ship = PoseFor(7, ammo: 0.001f, stateM: ShipControlState.AllWeapons),
        });
        world.UpdateOwnStatusAlarms();

        Assert.Equal(ClientSoundEventKind.NoAmmo, Assert.Single(world.TakeSoundEvents()).Kind);
    }

    [Fact]
    public void OwnShipNoStatusChange_RecordsNothing()
    {
        var world = ArmedWorld(7, PoseFor(7, hull: 0.3f));
        world.Apply(new SingleShipUpdate { OidTarget = 7, Ship = PoseFor(7, hull: 0.3f) });
        world.UpdateOwnStatusAlarms();
        Assert.Empty(world.TakeSoundEvents());
    }

    [Fact]
    public void RemoteShipStatusChange_DoesNotTriggerLocalAlarms()
    {
        var world = ArmedWorld(7, PoseFor(7));
        // A remote ship's hull drop must not fire the local alarms (bIsPlayersShip).
        world.Apply(new SingleShipUpdate { OidTarget = 88, Ship = PoseFor(88, hull: 0.05f) });
        world.UpdateOwnStatusAlarms();
        Assert.Empty(world.TakeSoundEvents());
    }

    // ── Ripcord denied (SIM-0425 / SIM-0467) ────────────────────────────────

    [Fact]
    public void RipcordDeniedWire_RecordsSalNoRipcordEvent()
    {
        var world = new ClientWorld();
        SetMySide(world, 2);
        world.Apply(new SetCluster { SectorId = 1, Ship = PoseFor(9), Cookie = 0x1 });

        world.Apply(new RipcordDenied());

        var ev = Assert.Single(world.TakeSoundEvents());
        Assert.Equal(ClientSoundEventKind.RipcordDenied, ev.Kind);
    }

    // ── Loop gates (SIM-0368 ripcord / SIM-0370 mining) ─────────────────────

    [Fact]
    public void RipcordActivateWire_SetsRipcordActive_ForLoopGate()
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster { SectorId = 1, Ship = PoseFor(5), Cookie = 0x1 });
        world.Apply(new RipcordActivate { ShipidRipcord = 5, SidRipcord = 3 });
        Assert.True(world.RipcordActive);
        // The loop gate in Game1 reads RipcordActive to start/stop the loop.
        world.Apply(new RipcordAborted { ShipidRipcord = 5 });
        Assert.False(world.RipcordActive);
    }

    [Fact]
    public void MiningUpdate_SetsMiningAsteroidId_ForLoopGate()
    {
        var world = new ClientWorld();
        world.Apply(new OwnShipMiningUpdate { AsteroidId = 42 });
        Assert.Equal(42, world.MiningAsteroidId);
        world.Apply(new OwnShipMiningUpdate { AsteroidId = -1 });
        Assert.Equal(-1, world.MiningAsteroidId);
    }
}
