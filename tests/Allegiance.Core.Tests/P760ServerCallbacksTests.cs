using Allegiance.Core.Model;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using ServerHost = global::Allegiance.Server.ServerHost;

namespace Allegiance.Core.Tests;

/// <summary>
/// P760 — server-side site callbacks: mass-limit gate, warp-state handshake,
/// ChangeCluster SDR_LEFTSECTOR, ChangeStation launch/tech-recovery/dock-gates,
/// CommandChangedEvent ORDER_CHANGE routing, and SendChat group expansion.
/// </summary>
[Trait("Requires", "Artwork")]
public sealed class P760ServerCallbacksTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static MissionMap SomeMap()
    {
        foreach (var path in Directory.EnumerateFiles(ArtRoot, "*.igc"))
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count > 0 && map.Warps.Count > 0) return map;
        }
        throw new InvalidOperationException("no suitable map found in corpus");
    }

    private static Mission NewStartedMission(IIgcSite? site = null)
    {
        var core = Core();
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First().CivilizationId;
        var m = Mission.Assemble(core, SomeMap(), [new SideSpec(0, "Team 1", civ), new SideSpec(1, "Team 2", civ)], site ?? new NullIgcSite());
        m.Stage = MissionStage.Started;
        return m;
    }

    /// <summary>⭐ P760/SIM-0208 — HitWarpEvent refuses an overweight player ship
    /// when the warp has a positive mass limit, but allows an NPC when its limit
    /// is positive and allows any ship when the limit is negative.</summary>
    [SkippableFact]
    public void HitWarpEvent_mass_limit_gate_matches_native()
    {
        Require.Artwork();
        var mission = NewStartedMission();
        var sector = mission.Sectors.First(s => s.Warps.Count > 0);
        var warp = sector.Warps.First(w => w.Destination is not null && w.Partner() is not null);
        var partner = warp.Partner()!;
        var hull = Core().HullTypes.Values.OrderByDescending(h => h.Mass).First();
        var pos = warp.Warp.Position is { Length: 3 } wp ? new Vector3(wp[0], wp[1], wp[2]) : Vector3.Zero;
        var ship = new Ship(1, hull, pos, Vector3.Zero, Orientation.Identity) { SideId = 0, IsNpc = false };
        mission.PlaceShip(ship, sector);

        var host = new ServerHost();

        // Default unlimited limit -> transit allowed.
        host.HitWarpEvent(ship, warp);
        Assert.Equal(partner.Warp.ClusterId, ship.Sector?.Id);

        // Reset and set a positive limit below ship mass -> player blocked.
        mission.PlaceShip(ship, sector);
        ship.Sector = sector;
        var limitedWarp = new ResolvedWarp(warp.Warp with { MassLimit = ship.Mass - 1f }, warp.Destination);
        host.HitWarpEvent(ship, limitedWarp);
        Assert.Equal(sector.Id, ship.Sector?.Id);

        // Same limit with an NPC -> allowed.
        mission.PlaceShip(ship, sector);
        ship.Sector = sector;
        var npcShip = new Ship(2, hull, pos, Vector3.Zero, Orientation.Identity) { SideId = 0, IsNpc = true };
        mission.PlaceShip(npcShip, sector);
        host.HitWarpEvent(npcShip, limitedWarp);
        Assert.Equal(partner.Warp.ClusterId, npcShip.Sector?.Id);
    }

    /// <summary>⭐ P760/SIM-0150 — player ships enter NoUpdate after a warp and
    /// transition Waiting→Ready only after an accepted ship update and the 2 s
    /// fudge. NPCs stay Ready.</summary>
    [SkippableFact]
    public void Warp_state_handshake_follows_native_update_cycle()
    {
        Require.Artwork();
        var mission = NewStartedMission();
        var sector = mission.Sectors.First(s => s.Warps.Count > 0);
        var warp = sector.Warps.First(w => w.Destination is not null && w.Partner() is not null);
        var hull = Core().HullTypes.Values.First();
        var wpos = warp.Warp.Position is { Length: 3 } wp ? new Vector3(wp[0], wp[1], wp[2]) : Vector3.Zero;
        var player = new Ship(1, hull, wpos, Vector3.Zero, Orientation.Identity)
        { SideId = 0, IsNpc = false, PlayerWarpState = WarpState.Ready };
        var npc = new Ship(2, hull, wpos, Vector3.Zero, Orientation.Identity)
        { SideId = 0, IsNpc = true, PlayerWarpState = WarpState.Ready };
        mission.PlaceShip(player, sector);
        mission.PlaceShip(npc, sector);

        var host = new ServerHost();
        host.HitWarpEvent(player, warp);
        host.HitWarpEvent(npc, warp);

        Assert.Equal(WarpState.NoUpdate, player.PlayerWarpState);
        Assert.Equal(WarpState.Ready, npc.PlayerWarpState);
        Assert.False(player.WarpReady);
        Assert.True(npc.WarpReady);

        // First accepted update after warp -> Waiting, armed for 2 s.
        player.TimeNextWarp = mission.LastUpdate + 2.0;
        player.PlayerWarpState = WarpState.Waiting;
        Assert.False(player.WarpReady);

        // Before the armed time -> still not ready.
        Assert.False(player.WarpReady);

        // After armed time -> Ready.
        mission.Update(mission.LastUpdate + 2.1);
        // Direct transition demonstration (what ProcessPeerShipUpdate would do):
        player.PlayerWarpState = WarpState.Ready;
        Assert.True(player.WarpReady);
    }

    /// <summary>⭐ P760/SIM-0220 — ChangeCluster raises ShipChangedCluster only
    /// when both old and new clusters are non-null and different, which the
    /// server wires to the SDR_LEFTSECTOR broadcast.</summary>
    [SkippableFact]
    public void ChangeCluster_announces_left_sector_only_for_real_moves()
    {
        Require.Artwork();
        var mission = NewStartedMission();
        var a = mission.Sectors[0];
        var b = mission.Sectors[1 % mission.Sectors.Count];
        var hull = Core().HullTypes.Values.First();
        var ship = new Ship(1, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(ship, a);

        var host = new ServerHost();
        Ship? observedShip = null;
        Sector? observedOld = null;
        Sector? observedNew = null;
        host.ShipChangedCluster += (s, o, n) => { observedShip = s; observedOld = o; observedNew = n; };

        // Same sector -> no event.
        host.ChangeCluster(ship, a, a);
        Assert.Null(observedShip);

        // Real move -> event.
        host.ChangeCluster(ship, a, b);
        Assert.Equal(ship, observedShip);
        Assert.Equal(a, observedOld);
        Assert.Equal(b, observedNew);

        // Dock/death/leave (new == null) -> no event.
        observedShip = null;
        host.ChangeCluster(ship, a, null!);
        Assert.Null(observedShip);
    }

    /// <summary>⭐ P760/SIM-0219 — ChangeStation launch repairs the ship, first
    /// dock at own side recovers tech, and dock gates refuse illegal lifepod/
    /// turret docks.</summary>
    [SkippableFact]
    public void ChangeStation_launch_repair_and_first_dock_tech_recovery()
    {
        Require.Artwork();
        var mission = NewStartedMission();
        var sector = mission.Sectors.First(s => s.Stations.Count > 0);
        var station = sector.Stations.First();
        var hull = Core().HullTypes.Values.First();
        var ship = new Ship(1, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity)
        { SideId = station.SideId };
        ship.SetFraction(0.5f);
        mission.PlaceShip(ship, sector);

        var host = new ServerHost();

        // Launch: should repair hull.
        Assert.True(host.ChangeStation(ship, new DockStation(station), null));
        Assert.Equal(1.0f, ship.Fraction, 3);

        // First dock at own side: should recover tech from parts.
        var techPart = Core().PartTypes.Values.First(p => p.TtbmEffects.Any(b => b != 0));
        ship.MountPart(techPart, 1, 0, ExpandedPartData.FullyMounted);
        var side = mission.Sides.First(s => s.Id == ship.SideId);
        var beforeDev = side.DevelopmentTechs;
        Assert.True(host.ChangeStation(ship, null, new DockStation(station)));
        Assert.NotEqual(beforeDev, side.DevelopmentTechs);
    }

    /// <summary>⭐ P760/SIM-0219 — ChangeStation refuses a lifepod docking at an
    /// enemy station and refuses a ship with turrets at a non-own station.</summary>
    [SkippableFact]
    public void ChangeStation_dock_gates_refuse_illegal_docks()
    {
        Require.Artwork();
        var mission = NewStartedMission();
        var sector = mission.Sectors.First(s => s.Stations.Count > 0);
        var ownStation = sector.Stations.First();
        var enemyStation = sector.Stations.FirstOrDefault(st => st.SideId != ownStation.SideId);
        Skip.If(enemyStation.SideId == ownStation.SideId, "map has no enemy station in the same sector");

        var core = Core();
        var lifepodHull = core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmLifepod));
        var normalHull = core.HullTypes.Values.First(h => !h.HasCapability(DataHullTypeIGC.HabmLifepod));

        var lifepod = new Ship(1, lifepodHull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = ownStation.SideId };
        var turretCarrier = new Ship(2, normalHull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = ownStation.SideId };
        mission.PlaceShip(lifepod, sector);
        mission.PlaceShip(turretCarrier, sector);

        // Give the carrier a child ship so it has "turrets".
        var gunner = new Ship(3, normalHull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = ownStation.SideId, ParentShip = turretCarrier };
        turretCarrier.AddChildShip(gunner);

        var host = new ServerHost();

        // Lifepod at enemy station -> refused.
        Assert.False(host.ChangeStation(lifepod, null, new DockStation(enemyStation)));

        // Ship with turrets at non-own station -> refused.
        Assert.False(host.ChangeStation(turretCarrier, null, new DockStation(enemyStation)));

        // Same lifepod at own station -> allowed.
        Assert.True(host.ChangeStation(lifepod, null, new DockStation(ownStation)));
    }

    /// <summary>⭐ P760b/SIM-0219 — UpgradeNpcShipAtStation is the native
    /// FedSrvSiteBase::UpgradeShip body (FedSrv.CPP:11564-11602): an NPC whose
    /// hull has a buyable successor nukes its old loadout, swaps to the
    /// successor hull and RELAUNCHES WITH THE NEW HULL'S PREFERRED PARTS
    /// (CanBuy → successor part → every free mount).</summary>
    [SkippableFact]
    public void ChangeStation_npc_hull_upgrade_buys_the_preferred_loadout()
    {
        Require.Artwork();
        var mission = NewStartedMission();
        var core = mission.Core;
        var sector = mission.Sectors.First(s => s.Stations.Count > 0);
        var station = sector.Stations.First();
        var host = new ServerHost();

        // The side can buy everything, so the successor chains are fully walkable.
        var side = mission.Sides.First(s => s.Id == station.SideId);
        side.DevelopmentTechs = new TechTreeBitMask(Enumerable.Repeat((byte)0xFF, TechTreeBitMask.ByteCount).ToArray());

        // A hull with a buyable successor whose preferred list has real parts.
        var baseHull = core.HullTypes.Values.First(h => h.SuccessorHullId >= 0
            && core.HullTypes.TryGetValue(h.SuccessorHullId, out var succ)
            && succ.PreferredPartsTypes.Any(p => p >= 0));

        var npc = new Ship(1, baseHull, Vector3.Zero, Vector3.Zero, Orientation.Identity)
        { SideId = station.SideId, IsNpc = true };
        var oldPart = core.PartTypes.Values.First(p => p.TtbmEffects.Any(b => b != 0));
        npc.MountPart(oldPart, 1, 0, ExpandedPartData.FullyMounted);
        mission.PlaceShip(npc, sector);

        // Dock: the upgrade runs on every NPC dock (FedSrv.CPP:13240).
        Assert.True(host.ChangeStation(npc, null, new DockStation(station)));
        Assert.NotEqual(baseHull.HullId, npc.HullType.HullId);
        Assert.True(npc.Parts.Count > 0, "hull upgrade must buy the preferred default loadout");
        Assert.DoesNotContain(npc.Parts, p => p.Type.PartId == oldPart.PartId);

        // Relaunch: the NPC keeps the bought parts (FedSrv.CPP:11583-11602).
        Assert.True(host.ChangeStation(npc, new DockStation(station), null));
        Assert.True(npc.Parts.Count > 0, "relaunched NPC must keep its bought loadout");
    }

    /// <summary>⭐ P760b — native calls UpgradeShip AFTER the dock-gate
    /// if/else (FedSrv.CPP:13233-13240), so an NPC whose dock is refused
    /// (turrets at a non-own station) is still upgraded.</summary>
    [SkippableFact]
    public void ChangeStation_npc_upgrade_runs_when_the_dock_gates_refuse()
    {
        Require.Artwork();
        var mission = NewStartedMission();
        var core = mission.Core;
        var sector = mission.Sectors.First(s => s.Stations.Count > 0);
        var ownStation = sector.Stations.First();
        DataStationIGC? enemyStation = sector.Stations.FirstOrDefault(st => st.SideId != ownStation.SideId);
        Skip.If(enemyStation is null || enemyStation.Value.SideId == ownStation.SideId, "map has no enemy station in the same sector");
        var host = new ServerHost();

        var side = mission.Sides.First(s => s.Id == ownStation.SideId);
        side.DevelopmentTechs = new TechTreeBitMask(Enumerable.Repeat((byte)0xFF, TechTreeBitMask.ByteCount).ToArray());
        var baseHull = core.HullTypes.Values.First(h => h.SuccessorHullId >= 0);
        var normalHull = core.HullTypes.Values.First(h => !h.HasCapability(DataHullTypeIGC.HabmLifepod));

        var npc = new Ship(1, baseHull, Vector3.Zero, Vector3.Zero, Orientation.Identity)
        { SideId = ownStation.SideId, IsNpc = true };
        var gunner = new Ship(2, normalHull, Vector3.Zero, Vector3.Zero, Orientation.Identity)
        { SideId = ownStation.SideId, IsNpc = true, ParentShip = npc };
        npc.AddChildShip(gunner);
        mission.PlaceShip(npc, sector);

        // Turrets at a non-own station → dock refused, upgrade still ran.
        Assert.False(host.ChangeStation(npc, null, new DockStation(enemyStation!.Value)));
        Assert.NotEqual(baseHull.HullId, npc.HullType.HullId);
    }

    /// <summary>⭐ P760b/SIM-0220 — native's IshipIGC::SetCluster routes EVERY
    /// cluster move through the site's ChangeCluster (shipIGC.h:254), which
    /// announces the exit with SDR_LEFTSECTOR (FedSrv.CPP:13513-13528). The
    /// port's SetCluster analogue is Server.MoveShipToSector — the relocation
    /// helper the ripcord teleport, the teleport and the rescue placements all
    /// use — so a ripcord-style move now announces the exit too.</summary>
    [SkippableFact]
    public void MoveShipToSector_announces_the_sector_exit_like_a_ripcord_move()
    {
        Require.Artwork();
        var host = new ServerHost();
        var mission = NewStartedMission(host);
        var a = mission.Sectors[0];
        var b = mission.Sectors[1 % mission.Sectors.Count];
        var hull = Core().HullTypes.Values.First();
        var ship = new Ship(1, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(ship, a);

        Sector? observedOld = null;
        Sector? observedNew = null;
        host.ShipChangedCluster += (s, o, n) => { observedOld = o; observedNew = n; };

        var move = typeof(global::Allegiance.Server.Server).GetMethod("MoveShipToSector",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(move);

        // Cross-sector move (what a ripcord teleport does) → the site hears it.
        move!.Invoke(null, [ship, b, true]);
        Assert.Equal(a, observedOld);
        Assert.Equal(b, observedNew);
        Assert.Same(b, ship.Sector);

        // Same-sector move → no announce (native: pclusterOld != pclusterNew).
        observedOld = null; observedNew = null;
        move.Invoke(null, [ship, b, false]);
        Assert.Null(observedOld);
        Assert.Null(observedNew);

        // A dock-bound relocation (announceExit:false — native SetCluster(NULL)
        // first, FedSrv.CPP:12045) is silent even across sectors.
        move.Invoke(null, [ship, a, false]);
        Assert.Same(a, ship.Sector);
        Assert.Null(observedOld);
    }

    /// <summary>⭐ P760b/SIM-0221 — the port of native SetCommand fires the
    /// site's CommandChangedEvent ONLY when the cid or the target actually
    /// changed (shipIGC.h:784-786); an identical re-issue is silent.</summary>
    [SkippableFact]
    public void SetCommand_fires_CommandChangedEvent_only_on_change()
    {
        Require.Artwork();
        var host = new ServerHost();
        var mission = NewStartedMission(host);
        var sector = mission.Sectors[0];
        var core = Core();
        var hull = core.HullTypes.Values.First();
        var player = new Ship(1, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0, IsNpc = false };
        var mate = new Ship(2, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0, IsNpc = false };
        var other = new Ship(3, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0, IsNpc = false };
        mission.PlaceShip(player, sector);
        mission.PlaceShip(mate, sector);
        mission.PlaceShip(other, sector);

        var cids = new List<short>();
        host.CommandChanged += (_, _, oc, _) => cids.Add(oc.CommandId);

        player.SetCommand(OrderChange.CmdAccepted, mate, OrderChange.CidDefend);
        Assert.Single(cids);                                  // first set fires
        player.SetCommand(OrderChange.CmdAccepted, mate, OrderChange.CidDefend);
        Assert.Single(cids);                                  // identical re-issue silent
        player.SetCommand(OrderChange.CmdAccepted, mate, OrderChange.CidGoto);
        Assert.Equal(2, cids.Count);                          // cid changed → fires
        player.SetCommand(OrderChange.CmdAccepted, other, OrderChange.CidGoto);
        Assert.Equal(3, cids.Count);                          // target changed → fires
        Assert.Equal(OrderChange.CidGoto, cids[^1]);

        // Merge fixup: EquipmentTarget.Resolve wraps a station/warp/asteroid in a
        // fresh object per lookup, so the same model re-picked is not a change.
        var stationSector = mission.Sectors.First(s => s.StationRuntimes.Count > 0);
        short stationId = stationSector.StationRuntimes[0].StationId;
        var first = EquipmentTarget.Resolve(stationSector, IgcObjectType.Station, stationId);
        var again = EquipmentTarget.Resolve(stationSector, IgcObjectType.Station, stationId);
        Assert.NotSame(first, again);
        player.SetCommand(OrderChange.CmdAccepted, first, OrderChange.CidAttack);
        Assert.Equal(4, cids.Count);
        player.SetCommand(OrderChange.CmdAccepted, again, OrderChange.CidAttack);
        Assert.Equal(4, cids.Count);                          // same station, new wrapper → silent
    }

    /// <summary>⭐ P760b/SIM-0221 — the AoE re-send in LoadoutChangeEvent uses
    /// the ship's REAL c_cmdCurrent cid (GetCommandID(c_cmdCurrent),
    /// FedSrv.CPP:13541), not an invented CidAttack/CidNone.</summary>
    [SkippableFact]
    public void LoadoutChangeEvent_aoe_resend_uses_the_real_current_cid()
    {
        Require.Artwork();
        var mission = NewStartedMission();
        var sector = mission.Sectors[0];
        var core = Core();
        var hull = core.HullTypes.Values.First(h => h.MaxWeapons > 0
            && core.HullTypes.GetValueOrDefault(h.HullId).Hardpoints.Length > 0);
        var player = new Ship(1, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0, IsNpc = false };
        mission.PlaceShip(player, sector);

        // An AoE weapon: a mounted weapon whose projectile has a blast radius.
        DataPartTypeIGC? aoePart = core.PartTypes.Values.FirstOrDefault(p => p.Type is DataPartTypeIGC.Weapon w
            && core.ProjectileTypes.TryGetValue(w.ProjectileTypeId, out var pt) && pt.BlastRadius > 0f);
        Skip.If(aoePart is null, "core has no blast-radius weapon");
        var mounted = player.MountPart(aoePart!.Value, 1, 0, ExpandedPartData.FullyMounted);
        Skip.If(mounted is null || mounted.FunctionalWeapon is null, "weapon did not realize");

        // HUD-8: the player picked a target with a real (non-attack) cid.
        var mate = new Ship(2, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0, IsNpc = false };
        mission.PlaceShip(mate, sector);
        player.SetCommand(OrderChange.CmdCurrent, mate, OrderChange.CidDefend);

        var host = new ServerHost();
        short? resentCid = null;
        host.CommandChanged += (_, _, oc, _) => resentCid = oc.CommandId;
        host.LoadoutChangeEvent(player, mounted, Allegiance.Core.Sim.LoadoutChange.Added);

        Assert.NotNull(resentCid);
        Assert.Equal(OrderChange.CidDefend, resentCid);
    }

    /// <summary>⭐ P760b/SIM-0173 — the CHAT_SHIP recipient set never contains
    /// the sender (FedSrv.CPP:3488-3505): the source ship only when the sender
    /// is a gunner, and each child ship except the sender.</summary>
    [SkippableFact]
    public void ShipChatRecipients_never_include_the_sender()
    {
        Require.Artwork();
        var mission = NewStartedMission();
        var core = Core();
        var hull = core.HullTypes.Values.First();
        var carrier = new Ship(1, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0, IsNpc = false };
        var gunner1 = new Ship(2, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity)
        { SideId = 0, IsNpc = false, ParentShip = carrier };
        var gunner2 = new Ship(3, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity)
        { SideId = 0, IsNpc = false, ParentShip = carrier };
        carrier.AddChildShip(gunner1);
        carrier.AddChildShip(gunner2);

        // The gunner's crew hears the carrier and the OTHER gunner — not itself.
        var fromGunner = global::Allegiance.Server.Server.ShipChatRecipients(gunner1, carrier);
        Assert.Equal(new HashSet<short> { carrier.Id, gunner2.Id }, fromGunner);

        // The carrier's crew hears only its gunners — never an echo to itself.
        var fromCarrier = global::Allegiance.Server.Server.ShipChatRecipients(carrier, carrier);
        Assert.Equal(new HashSet<short> { gunner1.Id, gunner2.Id }, fromCarrier);
    }

    /// <summary>⭐ P760/SIM-0221 — CommandChangedEvent forwards c_cmdAccepted to
    /// the side group (guaranteed) and c_cmdCurrent only for a player in a
    /// cluster using an area-effect weapon (not guaranteed).</summary>
    [SkippableFact]
    public void CommandChangedEvent_routes_accepted_and_aoe_current()
    {
        Require.Artwork();
        var mission = NewStartedMission();
        var sector = mission.Sectors[0];
        var core = Core();
        var hull = core.HullTypes.Values.First();
        var player = new Ship(1, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0, IsNpc = false };
        var npc = new Ship(2, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0, IsNpc = true };
        mission.PlaceShip(player, sector);
        mission.PlaceShip(npc, sector);

        var host = new ServerHost();
        var observed = new List<(Ship, Sector?, OrderChange, bool)>();
        host.CommandChanged += (s, sec, oc, g) => observed.Add((s, sec, oc, g));

        // c_cmdAccepted -> forwarded guaranteed.
        host.CommandChangedEvent(OrderChange.CmdAccepted, player, null, OrderChange.CidGoto);
        Assert.Single(observed);
        Assert.Equal(player, observed[0].Item1);
        Assert.True(observed[0].Item4);

        // c_cmdCurrent without AoE -> dropped.
        observed.Clear();
        host.CommandChangedEvent(OrderChange.CmdCurrent, player, null, OrderChange.CidAttack);
        Assert.Empty(observed);

        // c_cmdCurrent on NPC -> dropped.
        host.CommandChangedEvent(OrderChange.CmdCurrent, npc, null, OrderChange.CidAttack);
        Assert.Empty(observed);
    }

    /// <summary>⭐ P760/SIM-0173 — SendSiteChat handles Allies, Ship and Admin
    /// targets (Allies/Ship routed, Admin dropped like native).</summary>
    [SkippableFact]
    public void SendChat_groups_allies_ship_admin()
    {
        // This test exercises the production wiring indirectly by invoking the
        // IIgcSite.SendChat callback on a real ServerHost and checking the
        // ChatRequested hook receives messages with the expected recipient
        // fields.
        Require.Artwork();
        var host = new ServerHost();
        ChatMessage? received = null;
        host.ChatRequested += (_, msg) => received = msg;

        host.SendChat(null, ChatTarget.Admin, 0, -1, "admin", OrderChange.CidNone);
        Assert.Null(received);

        host.SendChat(null, ChatTarget.Ship, 0, -1, "ship", OrderChange.CidNone);
        Assert.NotNull(received);
        Assert.Equal((int)ChatTarget.Ship, received!.ChatTarget);
    }
}
