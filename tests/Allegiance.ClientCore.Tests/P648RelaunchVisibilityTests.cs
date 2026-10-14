using System;
using System.Linq;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P648 — unit tests for the relaunch-visibility fix (invisible + frozen ships
/// at 200v200).
///
/// What was broken: when a bot/player dies and relaunches, the client had
/// latched the ship dead (<c>ShipEntity.IsDead</c>, set by SHIP_DELETE
/// SDR_KILLED) and FM_S_LOADOUT_CHANGE(96) — the server's same-side broadcast
/// of the relaunch's new hull (clash36: "LOADOUT_CHANGE(96) hull 15 -&gt; 195
/// same-side peer(s)") — was a documented UNCONSUMED message (P627). So a
/// same-side peer never learned the relaunch, kept the ship dead (not drawn /
/// not targetable) while its projectiles still rendered ("invisible ship
/// shooting"), and kept the pre-relaunch hull.
///
/// The fix (this task) wires LOADOUT_CHANGE(96) into the client
/// (<see cref="ClientWorld.Apply(Allegiance.Core.Wire.LoadoutChange)"/>) as the
/// native "alive again, with hull H" signal (native appmsg.cpp:1991-2084:
/// ProcessShipLoadout with bFirstOrder = the ship was cached away by
/// SDR_KILLED) and adds a per-viewer placeability diagnostic
/// (<see cref="ClientWorld.ClassifyShipPlaceability"/> + the
/// <see cref="ClientWorld.Diag"/> counters) to measure shots from ships the
/// viewer cannot place (latched dead / never posed / stale pose).
///
/// FAILING-FIRST: before the fix there was no
/// <c>ClientWorld.Apply(LoadoutChange)</c> at all (the message was dropped), so
/// these tests would not even link/pass; the dispatch-coverage registry test
/// (FedMsgDispatchCoverageTests) already pins LOADOUT_CHANGE as consumed.
/// </summary>
public class P648RelaunchVisibilityTests
{
    private static DataHullTypeIGC TestHull() => new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 550f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.5f, 4.5f, 4.5f],
        TurnTorques: [5000f, 5000f, 5000f],
        Thrust: 52500f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 1000f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 801, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 618f, HardpointOffset: 640,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);

    private static Ship MakeShip(short id, Vector3 position) =>
        new(id, TestHull(), position, Vector3.Zero, Orientation.Identity);

    private static Allegiance.Core.Wire.LoadoutChange LC(short sid, short hull, params ExpandedPartData[] parts) => new()
    {
        SidShip = sid,
        HullId = hull,
        Parts = parts,
        Passengers = [],
    };

    // ── Same-side relaunch revive via LOADOUT_CHANGE(96) ─────────────────

    [Fact]
    public void LoadoutChange_RevivesDeadShip_AdoptsNewHullAndParts()
    {
        const short ship = 707;
        var world = new ClientWorld();
        // The side's lifepod is hull 446; the relaunch hull 999 is NOT it, so a
        // change to 999 is a genuine relaunch (revive) — while a change to 446
        // (the death path) must NOT revive (covered by the next test).
        world.LifepodHullResolver = _ => 446;
        world.Apply(new ExportShip { ShipId = ship, HullId = 708, SideId = 910, CharacterName = "relauncher" });
        Assert.False(world.Ships[ship].IsDead);

        world.Apply(new ShipDelete { ShipId = ship, Sdr = ShipDelete.SdrKilled });
        Assert.True(world.Ships[ship].IsDead, "the death must latch IsDead");

        world.Apply(LC(ship, 999, new ExpandedPartData(PartId: 3, Amount: 40, MountId: 0, FractionMounted: 255)));

        var e = world.Ships[ship];
        Assert.False(e.IsDead, "the relaunch LOADOUT_CHANGE must revive a latched-dead ship");
        Assert.False(e.IsGone, "the relaunch also un-hides the ship (IsGone cleared)");
        Assert.Equal((short)999, e.HullId);
        Assert.Single(e.Loadout ?? []);
        Assert.Equal((short)3, e.Loadout![0].PartId);
    }


    /// <summary>The DEATH path fires a LOADOUT_CHANGE to a Lifepod hull
    /// (ChangeHull(lifepod) on the kill seam); that must NOT un-latch the death
    /// (the pod stays dead until a real relaunch). Regression guard for the
    /// P648 gate: only a non-lifepod hull revives.</summary>
    [Fact]
    public void LoadoutChange_ToLifepodHull_DoesNotReviveDeadShip()
    {
        const short ship = 711;
        var world = new ClientWorld();
        world.LifepodHullResolver = _ => 446;   // side's lifepod hull is 446
        world.Apply(new ExportShip { ShipId = ship, HullId = 20, SideId = 910, CharacterName = "dying" });
        world.Apply(new ShipDelete { ShipId = ship, Sdr = ShipDelete.SdrKilled });
        Assert.True(world.Ships[ship].IsDead, "death must latch IsDead");

        // A hull change TO the side's lifepod is the death/respawn, not a relaunch.
        world.Apply(LC(ship, 446));

        Assert.True(world.Ships[ship].IsDead,
            "a LOADOUT_CHANGE to the Lifepod hull is the death path and must NOT revive the ship");
    }

    /// <summary>Summary of the original revive test: a ship the viewer saw DIE is
    /// REVIVED by the relaunch LOADOUT_CHANGE(96) to a NON-lifepod hull, with its
    /// NEW hull adopted (native appmsg.cpp:1991-2084).</summary>

    [Fact]
    public void LoadoutChange_OnLiveShip_DoesNotDisturbReviveState()
    {
        const short ship = 808;
        var world = new ClientWorld();
        world.NowSeconds = () => 1000.0;
        world.Apply(new ExportShip { ShipId = ship, HullId = 10, SideId = 1, CharacterName = "live" });
        world.Apply(new SingleShipUpdate { OidTarget = ship, Ship = ServerShipUpdate.FromModel(ship, MakeShip(ship, Vector3.Zero), 1u) });

        Assert.False(world.Ships[ship].IsDead);
        world.Apply(LC(ship, 555));

        var e = world.Ships[ship];
        Assert.False(e.IsDead, "a live ship's loadout change must not latch it dead");
        Assert.Equal((short)555, e.HullId);
        Assert.Equal(ClientWorld.ShipPlaceability.Live, world.ClassifyShipPlaceability(ship));
    }

    [Fact]
    public void LoadoutChange_ReparentsBoardedChildren()
    {
        const short host = 900;
        const short child = 901;
        var world = new ClientWorld();
        world.Apply(new ExportShip { ShipId = host, HullId = 15, SideId = 2, CharacterName = "host" });
        world.Apply(new ExportShip { ShipId = child, HullId = 445, SideId = 2, CharacterName = "child" });

        world.Apply(new Allegiance.Core.Wire.LoadoutChange
        {
            SidShip = host,
            HullId = 16,
            Parts = [],
            Passengers = [new PassengerData(child, TurretId: 2)],
        });

        Assert.Equal((short)host, world.Ships[child].ParentId);
        Assert.Equal((short)-1, world.Ships[host].ParentId);
    }

    // ── Per-viewer placeability diagnostics (the measurement deliverable) ─

    [Fact]
    public void ClassifyPlaceability_DeadNeverPosedStaleLive()
    {
        var world = new ClientWorld();
        world.NowSeconds = () => 1000.0;

        Assert.Equal(ClientWorld.ShipPlaceability.NeverPosed, world.ClassifyShipPlaceability((short)501));

        world.Apply(new ExportShip { ShipId = 502, HullId = 15, SideId = 1, CharacterName = "np" });
        Assert.Equal(ClientWorld.ShipPlaceability.NeverPosed, world.ClassifyShipPlaceability((short)502));

        world.Apply(new SingleShipUpdate { OidTarget = 502, Ship = ServerShipUpdate.FromModel(502, MakeShip(502, Vector3.Zero), 1u) });
        Assert.Equal(ClientWorld.ShipPlaceability.Live, world.ClassifyShipPlaceability((short)502));

        world.NowSeconds = () => 1002.0;
        Assert.Equal(ClientWorld.ShipPlaceability.Stale, world.ClassifyShipPlaceability((short)502));
        Assert.True(world.PoseAgeSeconds(502) > ClientWorld.StalePoseSeconds);

        world.Apply(new ExportShip { ShipId = 503, HullId = 15, SideId = 1, CharacterName = "corpse" });
        world.Apply(new ShipDelete { ShipId = 503, Sdr = ShipDelete.SdrKilled });
        Assert.Equal(ClientWorld.ShipPlaceability.LatchedDead, world.ClassifyShipPlaceability((short)503));
    }

    [Fact]
    public void Projectile_FromLatchedDeadShooter_IncrementsFromLatchedDead()
    {
        const short shooter = 600;
        var world = new ClientWorld();
        world.Apply(new ExportShip { ShipId = shooter, HullId = 15, SideId = 1, CharacterName = "phantom" });
        world.Apply(new ShipDelete { ShipId = shooter, Sdr = ShipDelete.SdrKilled });
        Assert.Equal(ClientWorld.ShipPlaceability.LatchedDead, world.ClassifyShipPlaceability(shooter));

        world.Apply(Projectile(shooter));
        Assert.Equal(1, world.Diag.FromLatchedDead);
        Assert.Equal(0, world.Diag.FromNeverPosed);
        Assert.Equal(0, world.Diag.FromStale);
    }

    [Fact]
    public void Projectile_FromNeverPosedShooter_IncrementsFromNeverPosed()
    {
        var world = new ClientWorld();
        Assert.Equal(ClientWorld.ShipPlaceability.NeverPosed, world.ClassifyShipPlaceability((short)601));

        world.Apply(Projectile(601));
        Assert.Equal(1, world.Diag.FromNeverPosed);
        Assert.Equal(0, world.Diag.FromLatchedDead);
    }

    private static ProjectileInstance Projectile(short launcher) => new()
    {
        LauncherId = launcher,
        ProjectileTypeId = 1,
        Position = Vector3.Zero,
        Forward = new Vector3(0, 0, 1),
        Velocity = new Vector3(0, 0, 1),
        Lifespan = 5f,
    };
}
