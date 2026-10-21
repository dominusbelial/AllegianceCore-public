using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P767 / SIM-0100 — the NPC wingman "getting ammo" resupply latch, the port
/// of native <c>BaseClient::Reload</c>'s wingman branch
/// (<c>clintlib.cpp:3700-3717</c>) plus the three native state rules:
/// clear-on-ammo in <c>CshipIGC::PreplotShipMove</c>
/// (<c>shipIGC.cpp:1713-1720</c>), clear-on-new-plan in
/// <c>SetCommand(c_cmdPlan)</c> (<c>shipIGC.h:755-762</c>), and the resupply
/// goto's afterburner (<c>shipIGC.cpp:2752-2754</c> applied at
/// <c>:2830-2833</c> when the throttle is 1).
///
/// The trigger is gated like native's offline-only branch: native reaches it
/// only when the client runs the sim (<c>!m_fm.IsConnected()</c>,
/// clintlib.cpp:3697); FedSrv inherits <c>IgcSite::Reload</c>'s
/// <c>false</c> default (igc.h:4923), so native multiplayer wingmen never
/// resupply-travel. The port's gate is <c>Mission.NpcWingmanAmmoResupply</c>
/// (default false = multiplayer; SkirmishHost sets it true).
/// </summary>
[Trait("Requires", "Artwork")]
public sealed class P767WingmanAmmoTests
{
    private const short ReloadStationTypeId = 700;
    private const short ReloadStationId = 500;

    private static StaticCore Core()
    {
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
        // A station type carrying c_sabmReload (the FindTarget ability bit,
        // clintlib.cpp:3710) plus the land/unload abilities a dockable base has.
        ((IDictionary<short, DataStationTypeIGC>)core.StationTypes)[ReloadStationTypeId] =
            DataStationTypeIGC.Decode(new byte[DataStationTypeIGC.Size]) with
            {
                StationTypeId = ReloadStationTypeId,
                Name = "Reloader",
                Radius = 50f,
                SabmCapabilities = DataStationTypeIGC.SabmReload | DataStationTypeIGC.SabmLand |
                                   DataStationTypeIGC.SabmUnload,
            };
        return core;
    }

    private static Mission Domain(IIgcSite? site = null, bool gate = true, float stationZ = -1000f)
    {
        var core = Core();
        var cluster = DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = 0 };
        var station = DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
        {
            ClusterId = 0, SideId = 0, StationId = ReloadStationId,
            StationTypeId = ReloadStationTypeId,
            Position = [0f, 0f, stationZ],
            Name = "Reloader",
        };
        var civ = core.Civilizations.Keys.First();
        var m = Mission.Assemble(core, MissionMap.FromLists([cluster], [], [station], []),
            [new SideSpec(0, "Team 1", civ)], site ?? new NullIgcSite());
        m.Stage = MissionStage.Started;
        foreach (var side in m.Sides) side.GlobalAttributes = GlobalAttributeSet.Identity;
        m.NpcWingmanAmmoResupply = gate;
        return m;
    }

    private static DataHullTypeIGC Hull(Mission m, short habm = 0) =>
        m.Core.HullTypes.Values.First() with
        {
            Mass = 100, Thrust = 5000f, Speed = 1000, MaxEnergy = 1000, MaxAmmo = 1000, RechargeRate = 0,
            HitPoints = 1000, Length = 10, HabmCapabilities = habm, RipcordSpeed = 1,
            MaxFixedWeapons = 1, MaxWeapons = 1, CapacityDispenser = 1000,
            Hardpoints = [new(0, 0, "", "", short.MaxValue, true)], ScannerRange = 10000
        };

    private static Ship Wingman(Mission m, short id = 100, short habm = 0, float x = 0f, float z = 0f)
    {
        var s = new Ship(id, Hull(m, habm), new Vector3(x, 0, z), Vector3.Zero, Orientation.Identity)
            { SideId = 0, IsNpc = true, PilotType = Ship.PilotTypeWingman };
        m.PlaceShip(s, m.Sectors[0]);
        return s;
    }

    private static DataPartTypeIGC AmmoPackType(short amount) => new(
        Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "ammo", Description: "",
        GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
        PartId: 910, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
        Type: new DataPartTypeIGC.Pack(PackType: 0, Amount: amount));

    private static Ship Deplete(Ship ship, double now = 1.0)
    {
        ship.Ammo = 0;
        var outcome = ship.TryAutoReloadAmmo(now);   // no pack in the hold → Depleted → the P767 trigger
        Assert.Equal(AutoReloadOutcome.Depleted, outcome);
        return ship;
    }

    private static int AmmoChats(RecordingIgcSite site) =>
        site.Calls.Count(c => c.StartsWith("DroneChatEvent(") && c.Contains("Out of ammo, I'm getting resupplies."));

    [SkippableFact]
    public void GateOn_WingmanRunsOut_PlansGotoToReloadStation_Latches_AndChatsOnce()
    {
        Require.Artwork();
        var site = new RecordingIgcSite();
        var m = Domain(site);
        var ship = Deplete(Wingman(m));

        // The trigger picked the nearest friendly c_sabmReload station
        // (clintlib.cpp:3710-3711) and wrote SetCommand(c_cmdPlan, …, c_cidGoto).
        Assert.True(ship.IsGettingAmmo);
        Assert.Equal(ReloadStationId, ship.PlanTarget?.StationId);
        Assert.Equal(OrderChange.CidGoto, ship.PlanCommandId);

        // Native order: SetCommand FIRST, then SetGettingAmmo(true) then the
        // team chat (clintlib.cpp:3711-3716).
        Assert.Equal(1, AmmoChats(site));
        Assert.Contains("1409", site.Calls.Single(c => c.StartsWith("DroneChatEvent(") && c.Contains("resupplies.")));

        // Exactly once per depletion: a later debounced-past attempt (the P361
        // rate limit) neither re-chats nor re-plans while the latch holds.
        ship.Ammo = 0;
        Assert.Equal(AutoReloadOutcome.NotAttempted, ship.TryAutoReloadAmmo(0.5));   // inside the debounce window
        ship.Ammo = 0;
        Assert.Equal(AutoReloadOutcome.Depleted, ship.TryAutoReloadAmmo(ship.LastUpdate + 10));
        Assert.Equal(1, AmmoChats(site));
        Assert.True(ship.IsGettingAmmo);
    }

    [SkippableFact]
    public void GateOff_MultiplayerDefault_NothingChanges()
    {
        Require.Artwork();
        var site = new RecordingIgcSite();
        var m = Domain(site, gate: false);
        var ship = Deplete(Wingman(m));

        // Native multiplayer: FedSrv inherits IgcSite::Reload's false default
        // (igc.h:4923) — no resupply trip, no chat, order untouched.
        Assert.False(ship.IsGettingAmmo);
        Assert.Null(ship.PlanTarget);
        Assert.Equal(OrderChange.CidNone, ship.PlanCommandId);
        Assert.Equal(0, AmmoChats(site));
        Assert.Equal(0, ship.Ammo);
    }

    [SkippableFact]
    public void WingmanWithAmmoPackInCargo_ReloadsFromCargo_NeverTrips()
    {
        Require.Artwork();
        var site = new RecordingIgcSite();
        var m = Domain(site);
        var ship = Wingman(m);
        ship.Ammo = 0;
        // Mount an ammo pack and swap it into the hold (P764's fixture shape).
        ship.MountPart(AmmoPackType(2000), 2000, 0, 255);
        Assert.True(ship.TrySwapPartWithCargo(EquipmentType.Pack, 0, -1));

        // The cargo drain succeeds → Reloaded (clintlib.cpp:3438-3556) — the
        // wingman branch (nReloads <= 0) is never reached.
        Assert.Equal(AutoReloadOutcome.Reloaded, ship.TryAutoReloadAmmo(1.0));
        Assert.Equal(ship.HullType.MaxAmmo, ship.Ammo);
        Assert.False(ship.IsGettingAmmo);
        Assert.Null(ship.PlanTarget);
        Assert.Equal(0, AmmoChats(site));
    }

    [SkippableFact]
    public void NonWingmanNpc_NeverTriggers()
    {
        Require.Artwork();
        var site = new RecordingIgcSite();
        var m = Domain(site);
        var miner = new Ship(102, Hull(m), new Vector3(0, 0, 0), Vector3.Zero, Orientation.Identity)
            { SideId = 0, IsNpc = true, PilotType = Ship.PilotTypeMiner };
        m.PlaceShip(miner, m.Sectors[0]);

        Deplete(miner);
        // clintlib.cpp:3700 — the branch requires pilot type c_ptWingman.
        Assert.False(miner.IsGettingAmmo);
        Assert.Null(miner.PlanTarget);
        Assert.Equal(0, AmmoChats(site));
    }

    [SkippableFact]
    public void AmmoArrives_PreplotClearsLatchAndPlan()
    {
        Require.Artwork();
        var m = Domain();
        var ship = Deplete(Wingman(m));
        Assert.True(ship.IsGettingAmmo);
        Assert.NotNull(ship.PlanTarget);

        // The resupply lands (dock restock / carrier relaunch refills the
        // pool) → the next undocked pre-plot clears the latch and the plan
        // (shipIGC.cpp:1713-1720: m_bGettingAmmo = false;
        // SetCommand(c_cmdPlan, NULL, c_cidNone)), so the default order
        // resumes. Docked ticks return early in this port, so the latch
        // survives the docked resupply and clears on the first undocked tick.
        ship.Ammo = ship.HullType.MaxAmmo;
        ship.Update(now: 1.0);
        Assert.False(ship.IsGettingAmmo);
        Assert.Null(ship.PlanTarget);
        Assert.Equal(OrderChange.CidNone, ship.PlanCommandId);
    }

    [SkippableFact]
    public void LatchSurvivesWhileStillEmpty_UndockedTick()
    {
        Require.Artwork();
        var m = Domain();
        var ship = Deplete(Wingman(m));
        ship.Update(now: 1.0);   // still at zero ammo: the trip keeps flying
        Assert.True(ship.IsGettingAmmo);
        Assert.NotNull(ship.PlanTarget);
    }

    [SkippableFact]
    public void NewOrderMidTrip_ClearsTheLatch()
    {
        Require.Artwork();
        var m = Domain();
        var ship = Deplete(Wingman(m));
        Assert.True(ship.IsGettingAmmo);

        // A new plan resets the latch like native SetCommand(c_cmdPlan) does
        // for a sub-player pilot (shipIGC.h:755-762) — here via the accepted
        // attack order's plan write.
        var enemy = new Ship(103, Hull(m), new Vector3(100, 0, -100), Vector3.Zero, Orientation.Identity)
            { SideId = 1, IsNpc = true, PilotType = Ship.PilotTypeWingman };
        m.PlaceShip(enemy, m.Sectors[0]);
        ship.SetWingmanAttackOrder(enemy);

        Assert.False(ship.IsGettingAmmo);
        Assert.Equal(enemy, ship.AttackTarget);
    }

    [SkippableFact]
    public void ResupplyGoto_UsesTheAfterburner_AtFullThrottle()
    {
        Require.Artwork();
        var m = Domain(stationZ: -5000f);
        // Aligned dead-on with the station (identity orientation faces −z in
        // this port's frame) and far away: SteerToward answers throttle 1 and
        // native's :2830-2833 application sets the afterburner bit.
        var ship = Deplete(Wingman(m, z: 0f));
        ship.Update(now: 1.0);
        Assert.True(ship.IsGettingAmmo);
        Assert.True((ship.StateM & ShipControlState.AfterburnerButton) != 0,
            $"throttle {ship.Controls.Throttle}, state {ship.StateM}");
    }

    [SkippableFact]
    public void LandOnCarrierWingman_PrefersANearbyFriendlyCarrier()
    {
        Require.Artwork();
        var site = new RecordingIgcSite();
        var m = Domain(site, stationZ: -5000f);
        var carrier = new Ship(104, Hull(m, DataHullTypeIGC.HabmCarrier), new Vector3(0, 0, -30),
            Vector3.Zero, Orientation.Identity) { SideId = 0, IsNpc = true, PilotType = Ship.PilotTypeCheatPlayer };
        m.PlaceShip(carrier, m.Sectors[0]);
        // clintlib.cpp:3706-3709 — with c_habmLandOnCarrier the search widens
        // to ships; common.cpp:541-551 interprets c_sabmReload on OT_ship as
        // c_habmCarrier. The carrier at 30 units beats the station at 5000.
        var ship = Deplete(Wingman(m, habm: DataHullTypeIGC.HabmLandOnCarrier));
        Assert.True(ship.IsGettingAmmo);
        Assert.Null(ship.PlanTarget);                       // the carrier rides its own slot, not the station plan slot
        Assert.Same(carrier, ship.GettingAmmoCarrier);
        Assert.Equal(1, AmmoChats(site));
    }

    [SkippableFact]
    public void AcceptedAttackOrder_SurvivesTheResupplyTrip_AndResumesAfterAmmoArrives()
    {
        Require.Artwork();
        var site = new RecordingIgcSite();
        var m = Domain(site);
        var enemy = new Ship(103, Hull(m), new Vector3(100, 0, -100), Vector3.Zero, Orientation.Identity)
            { SideId = 1, IsNpc = true, PilotType = Ship.PilotTypeWingman };
        m.PlaceShip(enemy, m.Sectors[0]);
        var ship = Wingman(m);
        ship.SetWingmanAttackOrder(enemy);
        Assert.Equal(enemy, ship.AttackTarget);

        // The trigger writes ONLY the plan slot (native SetCommand(c_cmdPlan,
        // pmodel, c_cidGoto), clintlib.cpp:3711-3712): the latch sets and the
        // accepted attack order is still remembered — the PlanTarget setter's
        // accepted-attack forget must not fire here (P767b: the plan writes run
        // under the _changingRetreatPlan guard, the accepted slot is untouched
        // and the post-trip "check for running" logic resumes it,
        // shipIGC.cpp:1936-1939).
        Deplete(ship);
        Assert.True(ship.IsGettingAmmo);
        Assert.Equal(ReloadStationId, ship.PlanTarget?.StationId);
        Assert.Equal(OrderChange.CidGoto, ship.PlanCommandId);
        Assert.Equal(enemy, ship.AttackTarget);          // pre-fix: wiped by the PlanTarget setter

        // The ammo arrives → the clear-on-ammo pre-plot drops the latch and
        // the plan (shipIGC.cpp:1713-1720) → the next tick the accepted
        // attack order comes back as the plan/attack target.
        ship.Ammo = ship.HullType.MaxAmmo;
        ship.Update(now: 1.0);
        Assert.False(ship.IsGettingAmmo);
        Assert.Null(ship.PlanTarget);
        Assert.Equal(OrderChange.CidNone, ship.PlanCommandId);
        Assert.Equal(enemy, ship.AttackTarget);
        Assert.Equal(1, AmmoChats(site));
    }

    [SkippableFact]
    public void CarrierLeg_WritesThePlanSlot_KeepsTheLatch_AndFliesToTheCarrier()
    {
        Require.Artwork();
        var site = new RecordingIgcSite();
        var m = Domain(site, stationZ: -5000f);          // reload station far away — the carrier is the nearest target
        var carrier = new Ship(104, Hull(m, DataHullTypeIGC.HabmCarrier), new Vector3(0, 0, -2000),
            Vector3.Zero, Orientation.Identity) { SideId = 0, IsNpc = true, PilotType = Ship.PilotTypeCheatPlayer };
        m.PlaceShip(carrier, m.Sectors[0]);
        // P767c — an enemy visible in the sector for the whole leg (side
        // knowledge, shipIGC.h:380-388): native's getting-ammo plan is legal,
        // so the !LegalCommand recovery (promotion + PickDefaultOrder,
        // shipIGC.cpp:1934-1945) never runs mid-trip and the wingman never
        // diverts to attack. The trip must survive it.
        var enemy = new Ship(105, Hull(m), new Vector3(500, 0, -500), Vector3.Zero, Orientation.Identity)
            { SideId = 1, IsNpc = true, PilotType = Ship.PilotTypeWingman };
        m.PlaceShip(enemy, m.Sectors[0]);
        enemy.SideVisibility.SetSideVisibility(0, true);
        var ship = Wingman(m, habm: DataHullTypeIGC.HabmLandOnCarrier);
        // An accepted station order — a legal c_cmdAccepted the SIM-0110
        // recovery must NOT promote over the resupply trip (shipIGC.cpp:1936
        // runs only once the trip is over, shipIGC.cpp:1936-1939).
        var acceptedStation = m.Sectors[0].StationRuntimes.Single(s => s.StationId == ReloadStationId);
        ship.AcceptedTarget = acceptedStation.Data;
        ship.AcceptedCommandId = OrderChange.CidGoto;

        Deplete(ship);
        Assert.True(ship.IsGettingAmmo);
        Assert.Same(carrier, ship.GettingAmmoCarrier);
        // P767b: the carrier leg WRITES the plan slot (goto, no station target)
        // and ends running-away, exactly like the native plan write it mirrors.
        Assert.Null(ship.PlanTarget);
        Assert.Equal(OrderChange.CidGoto, ship.PlanCommandId);
        Assert.False(ship.IsRunningAway);

        // Pre-fix: the SIM-0110 recovery copied the legal AcceptedTarget into
        // the empty PlanTarget, whose DronePlanChanged cleared the latch and
        // the carrier on the next tick. Now the trip holds for the whole leg
        // and the autopilot steers toward the carrier.
        for (var i = 0; i < 3; i++)
            ship.Update(now: 1.0 + i);
        Assert.True(ship.IsGettingAmmo);
        Assert.Same(carrier, ship.GettingAmmoCarrier);
        Assert.Null(ship.PlanTarget);
        Assert.Equal(OrderChange.CidGoto, ship.PlanCommandId);
        // P767c — the visible enemy did NOT pull the wingman off the trip: no
        // attack order was taken (the pre-fix default-order block would call
        // DecideWingmanDefaultOrder → SetWingmanAttackOrder, whose
        // DronePlanChanged clears the latch and the carrier).
        Assert.Null(ship.AttackTarget);
        Assert.Same(carrier, ship.GettingAmmoCarrier);
        Assert.True(ship.Controls.Throttle >= 1f, $"throttle {ship.Controls.Throttle}");
        Assert.True((ship.StateM & ShipControlState.AfterburnerButton) != 0);
        Assert.True(ship.Position.Z < -1f, $"position {ship.Position} — not closing on the carrier at z=-2000");
        Assert.Equal(1, AmmoChats(site));
    }

    [SkippableFact]
    public void StationLeg_VisibleEnemy_KeepsTheTrip()
    {
        Require.Artwork();
        var site = new RecordingIgcSite();
        var m = Domain(site);                            // reload station at z=-1000 — the station leg
        var enemy = new Ship(105, Hull(m), new Vector3(100, 0, -100), Vector3.Zero, Orientation.Identity)
            { SideId = 1, IsNpc = true, PilotType = Ship.PilotTypeWingman };
        m.PlaceShip(enemy, m.Sectors[0]);
        enemy.SideVisibility.SetSideVisibility(0, true); // visible the whole flight (shipIGC.h:380-388)

        var ship = Deplete(Wingman(m, z: 0f));
        Assert.True(ship.IsGettingAmmo);
        Assert.Equal(ReloadStationId, ship.PlanTarget?.StationId);

        // Ticking toward the station with an enemy visible: the plan (goto the
        // reload station) is native-legal (shipIGC.cpp:1934-1945 — the
        // !LegalCommand recovery never fires), so no default attack order is
        // taken and the latch holds.
        for (var i = 0; i < 5; i++)
            ship.Update(now: 1.0 + i);
        Assert.True(ship.IsGettingAmmo);
        Assert.Equal(ReloadStationId, ship.PlanTarget?.StationId);
        Assert.Equal(OrderChange.CidGoto, ship.PlanCommandId);
        Assert.Null(ship.AttackTarget);
        Assert.Equal(1, AmmoChats(site));
    }

    [SkippableFact]
    public void StationDock_ResuppliesTheLatchedWingman_AndDoesNotRetrip()
    {
        // P767c — server-level proof through the REAL DockWithStationEvent
        // (not a direct Ammo = assignment): the resupply decision must be made
        // BEFORE the dock's PlanTarget/AcceptedTarget clear, because those
        // writes run DronePlanChanged which clears _isGettingAmmo — the old
        // post-clear gate read the latch after it was already wiped, so a
        // latched wingman undocked with 0 ammo and re-tripped forever.
        Require.Artwork();
        var site = new RecordingIgcSite();
        var m = Domain(site);
        var ship = Deplete(Wingman(m));
        Assert.True(ship.IsGettingAmmo);

        var host = new global::Allegiance.Server.ServerHost();
        var station = m.Sectors[0].StationRuntimes.Single(s => s.StationId == ReloadStationId);
        Assert.True(host.DockWithStationEvent(ship, new DockStation(station.Data)));

        // Docked, refilled to the hull max, and the latch is gone: the dock's
        // plan clear (P386) ran DronePlanChanged — the same shape as native's
        // SetCommand(c_cmdPlan, NULL, c_cidNone) clearing m_bGettingAmmo for a
        // sub-player pilot (shipIGC.h:758-763).
        Assert.NotNull(ship.StationId);
        Assert.Equal(ship.HullType.MaxAmmo, ship.Ammo);
        Assert.False(ship.IsGettingAmmo);
        Assert.Null(ship.PlanTarget);
        Assert.Equal(OrderChange.CidNone, ship.PlanCommandId);

        // Following ticks (undock first, the P381 auto-launch stand-in): full
        // ammo means no Depleted zero-transition — no re-trip, no second chat.
        ship.StationId = null;
        for (var i = 0; i < 5; i++)
            ship.Update(now: 2.0 + i);
        Assert.False(ship.IsGettingAmmo);
        Assert.Equal(ship.HullType.MaxAmmo, ship.Ammo);
        Assert.Null(ship.PlanTarget);
        Assert.Equal(1, AmmoChats(site));
    }

    [SkippableFact]
    public void WingmanWithoutCarrierCapability_IgnoresTheNearbyCarrier()
    {
        Require.Artwork();
        var site = new RecordingIgcSite();
        var m = Domain(site, stationZ: -5000f);
        var carrier = new Ship(104, Hull(m, DataHullTypeIGC.HabmCarrier), new Vector3(0, 0, -30),
            Vector3.Zero, Orientation.Identity) { SideId = 0, IsNpc = true, PilotType = Ship.PilotTypeCheatPlayer };
        m.PlaceShip(carrier, m.Sectors[0]);

        // Without c_habmLandOnCarrier the ship arm of the search does not
        // exist (clintlib.cpp:3706-3709): the wingman targets the (far)
        // station instead of the nearby carrier.
        var ship = Deplete(Wingman(m, z: 0f));
        Assert.True(ship.IsGettingAmmo);
        Assert.Null(ship.GettingAmmoCarrier);
        Assert.Equal(ReloadStationId, ship.PlanTarget?.StationId);
        Assert.Equal(1, AmmoChats(site));
    }
}
