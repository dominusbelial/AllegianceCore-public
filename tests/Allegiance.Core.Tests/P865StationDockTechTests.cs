using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = global::Allegiance.Server.Server;
using Host = global::Allegiance.Server.ServerHost;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// P865 — station/dock/tech/drone parity slices: SIM-0636 (eager
/// <c>UpgradeDrones</c> on tech change/build), SIM-0643 (BuildStation's trailing
/// upgrade), SIM-0662/0666/0667 (the allied-base dock entry, the allied capture
/// exemption, the docked children, the rearm relaunch and the lifepod re-home),
/// SIM-0770 (CreateStockDrone's name+id PLAYERINFO ceremony) and SIM-0780
/// (RefillPart's unconditional successor upgrade).
/// </summary>
[Trait("Requires", "Artwork")]
public sealed class P865StationDockTechTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core() =>
        StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));

    private static Mission Map(IIgcSite? site = null)
    {
        var core = Core();
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "XXV_simple.igc")));
        var mission = Mission.Assemble(core, map,
            [new SideSpec(0, "Alpha", 1), new SideSpec(1, "Bravo", 3)], site ?? new NullIgcSite());
        mission.Stage = MissionStage.Started;
        return mission;
    }

    private static DataStationTypeIGC LandType(StaticCore core) =>
        core.StationTypes.Values.First(st => st.HasCapability(DataStationTypeIGC.SabmLand));

    private static DataStationTypeIGC RestartType(StaticCore core) =>
        core.StationTypes.Values.First(st => st.HasCapability(DataStationTypeIGC.SabmRestart));

    private static Station PlacedStation(Mission mission, Sector sector, short sideId, short stationId)
    {
        var core = mission.Core;
        var station = new Station(
            DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
            {
                StationId = stationId, SideId = sideId, ClusterId = sector.Id,
                Position = [500f, 500f, 500f], Name = $"P865-{sideId}-{stationId}",
                StationTypeId = LandType(core).StationTypeId,
            },
            LandType(core));
        mission.PlaceStation(station, sector);
        return station;
    }

    private static Ship DockedShip(Mission mission, Sector sector, short sideId, short shipId,
        bool npc, DataHullTypeIGC? hull = null, Station? at = null)
    {
        var resolved = hull ?? mission.Core.HullTypes.Values.First(h =>
            h.HasCapability(DataHullTypeIGC.HabmFighter) && !h.HasCapability(DataHullTypeIGC.HabmLifepod));
        var pos = at is { } target
            ? new Vector3(target.Data.Position[0] + Sector.DockRadius - 50f, target.Data.Position[1], target.Data.Position[2])
            : new Vector3(600f, 600f, 600f);
        var ship = new Ship(shipId, resolved, pos, Vector3.Zero, Orientation.Identity)
        { SideId = sideId, IsNpc = npc };
        mission.PlaceShip(ship, sector);
        if (at is { } station) ship.StationId = station.StationId;
        return ship;
    }

    // ── SIM-0636 — UpgradeDrones (FedSrv.CPP:11234-11257) ────────────────────

    /// <summary>⭐ SIM-0636 — native UpgradeDrones iterates the side's stations'
    /// docked ships and upgrades every NON-PLAYER one (FedSrv.CPP:11251-11253);
    /// a docked player ship is never touched. Red on unfixed code: the method
    /// did not exist (drones upgraded only at their next dock).</summary>
    [SkippableFact]
    public void UpgradeDrones_upgrades_docked_npcs_and_skips_player_ships()
    {
        var mission = Map(new Host());
        var core = mission.Core;
        var sector = mission.Sectors[0];
        var station = PlacedStation(mission, sector, 0, 701);
        var side = mission.Sides.First(s => s.Id == 0);

        // Full techs: the successor chains are fully walkable.
        side.DevelopmentTechs = new TechTreeBitMask(Enumerable.Repeat((byte)0xFF, TechTreeBitMask.ByteCount).ToArray());

        var baseHull = core.HullTypes.Values.First(h => h.SuccessorHullId >= 0
            && core.HullTypes.ContainsKey(h.SuccessorHullId));
        var npc = DockedShip(mission, sector, 0, 801, npc: true, hull: baseHull, at: station);
        var player = DockedShip(mission, sector, 0, 802, npc: false, hull: baseHull, at: station);

        var host = new Host();
        host.UpgradeDrones(side);

        Assert.NotEqual(baseHull.HullId, npc.HullType.HullId);          // the NPC was upgraded
        Assert.Equal(baseHull.HullId, player.HullType.HullId);          // the player was NOT
    }

    /// <summary>⭐ SIM-0636 — a drone docked at ANOTHER side's station is not
    /// touched: native iterates pside->GetStations() only (FedSrv.CPP:11239).</summary>
    [SkippableFact]
    public void UpgradeDrones_only_walks_the_owning_sides_stations()
    {
        var mission = Map(new Host());
        var core = mission.Core;
        var sector = mission.Sectors[0];
        var foreign = PlacedStation(mission, sector, 1, 711);
        var side = mission.Sides.First(s => s.Id == 0);
        side.DevelopmentTechs = new TechTreeBitMask(Enumerable.Repeat((byte)0xFF, TechTreeBitMask.ByteCount).ToArray());

        var baseHull = core.HullTypes.Values.First(h => h.SuccessorHullId >= 0
            && core.HullTypes.ContainsKey(h.SuccessorHullId));
        var npc = DockedShip(mission, sector, 0, 811, npc: true, hull: baseHull, at: foreign);

        var host = new Host();
        host.UpgradeDrones(side);

        Assert.Equal(baseHull.HullId, npc.HullType.HullId);
    }

    // ── SIM-0643 — BuildStation's trailing UpgradeDrones (FedSrv.CPP:11507) ──

    /// <summary>⭐ SIM-0643 — native BuildStation calls UpgradeDrones(pside)
    /// right after the station is created (FedSrv.CPP:11507): the fresh base
    /// immediately upgrades the drones docked at the side's stations. Red on
    /// unfixed code: the build ran no drone pass.</summary>
    [SkippableFact]
    public void Station_build_upgrades_docked_drones()
    {
        var host = new Host();
        var mission = Map(host);
        var core = mission.Core;
        var sector = mission.Sectors[0];
        var station = PlacedStation(mission, sector, 0, 721);
        var side = mission.Sides.First(s => s.Id == 0);
        side.DevelopmentTechs = new TechTreeBitMask(Enumerable.Repeat((byte)0xFF, TechTreeBitMask.ByteCount).ToArray());

        var baseHull = core.HullTypes.Values.First(h => h.SuccessorHullId >= 0
            && core.HullTypes.ContainsKey(h.SuccessorHullId));
        var npc = DockedShip(mission, sector, 0, 821, npc: true, hull: baseHull, at: station);

        var buildType = RestartType(core);
        var rock = new Asteroid(new DataAsteroidIGC(1, [900f, 900f, 900f],
            [0, 1, 0], [1, 0, 0], [0, 0, 0, 1], 0, 0, buildType.AabmBuild,
            6401, 0, 60, "roid.mdl", "", "", sector.Id, "P865 build rock", 0));
        mission.PlaceAsteroid(rock, sector);

        host.BuildStation(rock, side, buildType, mission.LastUpdate + 1, new bool[6]);

        Assert.NotEqual(baseHull.HullId, npc.HullType.HullId);          // FedSrv.CPP:11507 ran
        Assert.True(mission.Sectors.SelectMany(s => s.StationRuntimes)
            .Any(st => st.Data.Position[0] == 900f), "the station itself was built");   // the station itself was built
    }

    // ── SIM-0666 — DockWithStationEvent's children (FedSrv.CPP:13172-13178) ──

    /// <summary>⭐ SIM-0666 — a docked ship's children get the station too:
    /// native loops GetChildShips() and SetStation(pstation) each (FedSrv.CPP:
    /// 13172-13178). Red on unfixed code: a boarded child kept StationId NA.</summary>
    [SkippableFact]
    public void Docking_gives_the_station_to_boarded_children()
    {
        var host = new Host();
        var mission = Map(host);
        var sector = mission.Sectors[0];
        var station = PlacedStation(mission, sector, 0, 731);

        var hull = mission.Core.HullTypes.Values.First(h =>
            h.HasCapability(DataHullTypeIGC.HabmFighter) && !h.HasCapability(DataHullTypeIGC.HabmLifepod));
        var parent = DockedShip(mission, sector, 0, 831, npc: false, hull: hull);
        var child = new Ship(832, hull, parent.Position, Vector3.Zero, Orientation.Identity)
        { SideId = 0, ParentShip = parent };
        parent.AddChildShip(child);

        Assert.True(host.DockWithStationEvent(parent, new DockStation(station.Data)));

        Assert.Equal(station.StationId, parent.StationId);
        Assert.Equal(station.StationId, child.StationId);               // FedSrv.CPP:13172-13178
    }

    // ── SIM-0662/0666/0667 — the allied dock entry (shipIGC.cpp:729-731) ─────

    /// <summary>⭐ SIM-0662 — an ALLIED ship passes the sector dock gate into a
    /// foreign (allied) station without capturing it (native
    /// `(pside1 == pside2) || pside1->AlliedSides(...)`, shipIGC.cpp:729-731).
    /// Red on unfixed code: every non-own-side non-capture dock was rejected.</summary>
    [SkippableFact]
    public void Allied_ship_docks_at_an_allied_station_without_capture()
    {
        var mission = Map(new Host());
        var sector = mission.Sectors[0];
        var allied = PlacedStation(mission, sector, 1, 741);
        var ship = DockedShip(mission, sector, 0, 841, npc: false, at: null);
        ship.Position = new Vector3(allied.Data.Position[0] + Sector.DockRadius - 50f,
            allied.Data.Position[1], allied.Data.Position[2]);
        mission.AlliedSidePairs.Add((0, 1));

        for (int i = 0; i < 40 && ship.StationId is null; i++)
            mission.Update(mission.LastUpdate + 1.0);

        Assert.Equal(allied.StationId, ship.StationId);                 // docked at the ALLIED base
        Assert.Equal(1, allied.SideId);                                 // ... without a capture
        Assert.Equal(1, sector.Stations.First(st => st.StationId == allied.StationId).SideId);
    }

    /// <summary>⭐ SIM-0666 (guard) — the same dock WITHOUT the alliance is
    /// still rejected for a non-board hull (the pre-existing cross-side gate,
    /// shipIGC.cpp:845-857).</summary>
    [SkippableFact]
    public void Non_allied_non_board_ship_still_cannot_dock_at_a_foreign_station()
    {
        var mission = Map(new Host());
        var sector = mission.Sectors[0];
        var enemy = PlacedStation(mission, sector, 1, 751);
        var ship = DockedShip(mission, sector, 0, 851, npc: false, at: null);
        ship.Position = new Vector3(enemy.Data.Position[0] + Sector.DockRadius - 50f,
            enemy.Data.Position[1], enemy.Data.Position[2]);

        for (int i = 0; i < 40; i++)
            mission.Update(mission.LastUpdate + 1.0);

        Assert.Null(ship.StationId);
    }

    /// <summary>⭐ SIM-0666 — the capture gate's ALLIED exemption
    /// (FedSrv.CPP:13101-13102): an allied c_habmBoard hull captures ONLY when
    /// bAllowFriendlyFire is on — with FF off it docks like anyone else, with FF
    /// on it captures the allied base regardless of the shields (the FedSrv
    /// capture gate has no shield term; the shield-down gate is the ENEMY
    /// branch's IGC collision gate, shipIGC.cpp:849-851).</summary>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Allied_board_hull_captures_only_with_friendly_fire(bool allowFriendlyFire)
    {
        var mission = Map(new Host());
        mission.AllowFriendlyFire = allowFriendlyFire;
        var sector = mission.Sectors[0];
        var allied = PlacedStation(mission, sector, 1, 761);
        allied.SetConstructionFractions(1.0f);                          // shields FULL — irrelevant to an allied FF capture
        var boardHull = mission.Core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmBoard));
        var ship = DockedShip(mission, sector, 0, 861, npc: false, hull: boardHull, at: null);
        ship.Position = new Vector3(allied.Data.Position[0] + Sector.DockRadius - 50f,
            allied.Data.Position[1], allied.Data.Position[2]);
        mission.AlliedSidePairs.Add((0, 1));

        for (int i = 0; i < 40 && ship.StationId is null; i++)
            mission.Update(mission.LastUpdate + 1.0);

        Assert.Equal(allied.StationId, ship.StationId);                 // the dock happens either way
        short after = sector.Stations.First(st => st.StationId == allied.StationId).SideId;
        if (allowFriendlyFire)
        {
            Assert.Equal(0, after);                                     // captured (FedSrv.CPP:13101-13102)
            Assert.Equal(0, allied.SideId);                             // runtime re-sided too
        }
        else
        {
            Assert.Equal(1, after);                                     // no capture — plain allied dock
        }
    }

    // ── SIM-0770 — CreateStockDrone's name ceremony (FedSrv.CPP:2246-2261) ───

    /// <summary>⭐ SIM-0770 — a drone's PLAYERINFO carries the native
    /// "&lt;droneTypeName&gt;&lt;shipId&gt;" name (FedSrv.CPP:2257-2261); a
    /// builder drone uses the station type's BuilderName (FedSrv.CPP:2252-2255).
    /// Red on unfixed code: the name was the generic "Ship {id}".</summary>
    [SkippableFact]
    public void Drone_playerinfo_carries_the_native_name_id()
    {
        var server = new Srv();
        try
        {
            server.Boot(new Cfg
            {
                ArtPath = ArtRoot, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
                Games = [new Gm { Name = "P865 Drone Name", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
            });
            var mission = server.Missions[0];
            var droneType = mission.Core.DroneTypes.Values.First(dt => dt.PilotType != Ship.PilotTypeBuilder);
            var sector = mission.Sectors[0];

            var drone = server.SpawnNpcShip(mission, sector, mission.Sides[0].Id,
                new Vector3(100f, 100f, 100f), droneType.DroneTypeId);

            var build = (PlayerInfo)typeof(Srv)
                .GetMethod("BuildPlayerInfo", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(server, [drone, mission.Sides[0].Id, null, 0, 0, 0, (short)0])!;
            Assert.Equal($"{droneType.Name}{drone.Id}", build.CharacterName);   // FedSrv.CPP:2257-2261
            Assert.Equal(droneType.DroneTypeId, build.DtidDrone);               // SetDroneTypeID, FedSrv.CPP:2240
        }
        finally { server.Dispose(); }
    }

    // ── SIM-0662/0667 — allied dock end-to-end (full server) ─────────────────

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P865 Allied Dock", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "P865ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 865u, DwCookie = 0x865u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8650000000000000000ul, DrmHash = "p865",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static short Seat(Srv server, FltBot bot, string name)
    {
        bot.Connect(server.BoundPort, name);
        bot.SendLogonRequest(Request(name));
        int seat = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(seat < 4000, $"{name} did not seat");
        return bot.ShipAssignment!.Ship.ShipId;
    }

    /// <summary>Place an ALLIED side's station in the bot's spawn sector so the
    /// spawned player ship starts inside its dock radius.</summary>
    private static Station AlliedHome(Srv server, Sector sector, short alliedSide)
    {
        var mission = server.Missions[0];
        var station = new Station(
            DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
            {
                StationId = 901, SideId = alliedSide, ClusterId = sector.Id,
                Position = [1000f, 1000f, 1000f], Name = "P865 allied garrison",
                StationTypeId = LandType(mission.Core).StationTypeId,
            },
            LandType(mission.Core));
        mission.PlaceStation(station, sector);
        return station;
    }

    /// <summary>Place an ALLIED side's station in the bot's spawn sector. The
    /// <paramref name="capLand"/> variant carries c_sabmCapLand — the pod dock
    /// test needs it (a lifepod hull is not a c_habmFighter, shipIGC.cpp:725-727).</summary>
    private static Station AlliedHome(Srv server, Sector sector, short alliedSide, bool capLand = false)
    {
        var mission = server.Missions[0];
        var type = capLand
            ? mission.Core.StationTypes.Values.First(st => st.HasCapability(DataStationTypeIGC.SabmCapLand))
            : LandType(mission.Core);
        var station = new Station(
            DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
            {
                StationId = 901, SideId = alliedSide, ClusterId = sector.Id,
                Position = [1000f, 1000f, 1000f], Name = "P865 allied garrison",
                StationTypeId = type.StationTypeId,
            },
            type);
        mission.PlaceStation(station, sector);
        return station;
    }

    /// <summary>⭐ SIM-0662/0667 — the allied rearm ENTRY + launch leg: a player
    /// docking at an ALLIED base is restocked (the AlliedRearm body — the
    /// RefillPart/desired-loadout walk, FedSrv.CPP:12662-12733) and IMMEDIATELY
    /// relaunched (BUY_LOADOUT_ACK fLaunch + ship+children SetStation(NULL),
    /// FedSrv.CPP:12776-12810). Red on unfixed code: the dock was rejected
    /// outright and the ship stayed in space, unrearmed.</summary>
    [SkippableFact]
    public void Player_docking_at_an_allied_base_is_rearmed_and_relaunched()
    {
        using var server = new Srv();
        Sector spawnSector = null!;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var sector = mission.Sectors.First(s => s.Stations.Count > 0);
            spawnSector = sector;
            var allied = AlliedHome(server, sector, 1);
            var hull = core.HullTypes.Values.First(h =>
                h.HasCapability(DataHullTypeIGC.HabmFighter) && !h.HasCapability(DataHullTypeIGC.HabmLifepod));
            var ship = new Ship(shipId, hull, new Vector3(3000f, 3000f, 3000f),
                Vector3.Zero, Orientation.Identity) { SideId = 0 };
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;
        var mission = server.Missions[0];
        mission.AlliedSidePairs.Add((0, 1));

        using var bot = new FltBot();
        short shipId = Seat(server, bot, "p865_allied");

        var ship = server.Ships.First(s => s.Ship.Id == shipId).Ship;
        var allied = spawnSector.Stations.First(st => st.StationId == 901);
        ship.Ammo = 0;                                                  // drained pool — the restock must refill it
        ship.Energy = 0;
        ship.Position = DockTestPlacement.Inside(mission, allied, ship.HullType);   // a real bay interior, not the centre

        PumpUntil(server, () => ship.StationId is not null || ship.LastLaunchTime > 0, 600);  // dock → rearm → relaunch
        PumpUntil(server, () => false, 20);

        Assert.Null(ship.StationId);                                    // SetStation(NULL), FedSrv.CPP:12809
        Assert.Equal(ship.HullType.MaxAmmo, ship.Ammo);                 // the AlliedRearm restock ran
        Assert.True(ship.Energy > 0);
        Assert.Equal(1, spawnSector.Stations.First(st => st.StationId == 901).SideId);  // no capture
    }

    /// <summary>⭐ SIM-0667 — the lifepod-at-allied-base redirect
    /// (FedSrv.CPP:13264-13300): the pod does NOT stay at the allied base — it
    /// is re-hulled and re-homed docked at its OWN side's restart station, and
    /// the whole mission gets the SHIP_DELETE(SDR_DOCKED) announcement.</summary>
    [SkippableFact]
    public void Lifepod_at_an_allied_base_is_rehomed_at_the_own_station()
    {
        using var server = new Srv();
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var sector = mission.Sectors.First(s => s.Stations.Count > 0);
            AlliedHome(server, sector, 1);                              // a lifepod hull is a c_habmFighter (P550) → dock gate is c_sabmLand
            var podHull = core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmLifepod));
            var ship = new Ship(shipId, podHull, new Vector3(3000f, 3000f, 3000f),
                Vector3.Zero, Orientation.Identity) { SideId = 0 };
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;
        var mission = server.Missions[0];
        mission.AlliedSidePairs.Add((0, 1));

        using var bot = new FltBot();
        short shipId = Seat(server, bot, "p865_pod");

        var ship = server.Ships.First(s => s.Ship.Id == shipId).Ship;
        short podHullId = ship.HullType.HullId;
        var allied = server.Missions[0].Sectors.SelectMany(s => s.Stations).First(st => st.StationId == 901);
        ship.Position = DockTestPlacement.Inside(mission, allied, ship.HullType);   // a real CAP bay interior (pods are not c_habmFighter)

        PumpUntil(server, () => ship.StationId is not null, 600);       // re-homed (not at the allied base)

        Assert.NotEqual((short)901, ship.StationId);                    // NOT the allied base
        var home = mission.Sectors.SelectMany(s => s.StationRuntimes)
            .First(st => st.StationId == ship.StationId);
        Assert.Equal(0, home.SideId);                                   // its OWN side's station
        Assert.True(home.Type.HasCapability(DataStationTypeIGC.SabmRestart));
        Assert.NotEqual(podHullId, ship.HullType.HullId);               // re-hulled out of the pod (FedSrv.CPP:13285)
    }

    // ── SIM-0636/0638 — the eager UpgradeDrones wiring on tech change ───────

    /// <summary>⭐ SIM-0636/0638 — completing a development runs native
    /// SideDevelopmentTechChange's trailing UpgradeDrones (FedSrv.CPP:11312)
    /// EAGERLY: a drone docked at the side's station is upgraded the moment the
    /// tech lands, not at its next dock. Red on unfixed code: the tech change
    /// ran no drone pass.</summary>
    [SkippableFact]
    public void Completing_a_development_upgrades_docked_drones_eagerly()
    {
        using var server = new Srv();
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;
        var mission = server.Missions[0];
        var side = mission.Sides[0];
        var garrison = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId == side.Id);
        var sector = mission.Sectors.First(s => s.Stations.Contains(garrison));

        // A (development, base hull) pair where the development's effect techs
        // unlock the hull's buyable successor BEYOND the side's current techs.
        var core = mission.Core;
        var before = side.DevelopmentTechs;
        var (dev, baseHull) = core.Developments.Values
            .SelectMany(d => core.HullTypes.Values
                .Where(h => h.SuccessorHullId >= 0 && core.HullTypes.ContainsKey(h.SuccessorHullId))
                .Select(h => (Dev: d, Hull: h))
                .Where(p =>
                {
                    var succ = core.HullTypes[p.Hull.SuccessorHullId];
                    var need = TechTreeBitMask.FromBytes(succ.TtbmRequired);   // the walk buys the SUCCESSOR (station.GetSuccessor)
                    var effects = TechTreeBitMask.FromBytes(p.Dev.TtbmEffects);
                    return need.IsSubsetOf(before.Or(effects))
                        && !need.IsSubsetOf(before)
                        && !effects.IsSubsetOf(before);      // the dev must actually be researchable
                }))
            .First();
        var npc = new Ship(950, baseHull, new Vector3(3000f, 3000f, 3000f), Vector3.Zero, Orientation.Identity)
        { SideId = side.Id, IsNpc = true };
        mission.PlaceShip(npc, sector);
        npc.StationId = garrison.StationId;                             // docked at the side's station

        typeof(Srv).GetMethod("CompleteDevelopment", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(server, [mission, side, dev]);

        Assert.NotEqual(baseHull.HullId, npc.HullType.HullId);          // FedSrv.CPP:11312 ran eagerly
    }

    // ── SIM-0637 — the prosperity gate is native's reachability ─────────────

    /// <summary>⭐ SIM-0637 — native's c_didTeamMoney GameOver (FedSrv.CPP:
    /// 11274-11279) is unconditional in the body but UNREACHABLE outside a
    /// prosperity game: CreateBuckets excludes the c_didTeamMoney development
    /// (sideigc.cpp:186) and EnterGame creates its bucket only for prosperity
    /// games (missionigc.h:769-794). This port seeds the bucket in every
    /// mission, so the ProsperityEnabled gate is the reachability compensation:
    /// completing the development in a NON-prosperity mission must NOT end the
    /// game (mirroring native, where it cannot happen).</summary>
    [SkippableFact]
    public void Non_prosperity_teamMoney_completion_does_not_end_the_game()
    {
        using var server = new Srv();
        server.Boot(Config());                                          // no prosperity goal in this config
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;
        var mission = server.Missions[0];
        Assert.False(mission.ProsperityEnabled);
        var dev = mission.Core.Developments[Mission.TeamMoneyDevelopmentId];

        typeof(Srv).GetMethod("CompleteDevelopment", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(server, [mission, mission.Sides[0], dev]);

        Assert.NotEqual(MissionStage.Over, mission.Stage);
    }

    // ── SIM-0650 — the non-victory destruction announcement ─────────────────

    /// <summary>⭐ SIM-0650 — a station kill that produces NO victory still
    /// announces the destruction to CHAT_EVERYONE: "%s has destroyed %s's %s."
    /// (FedSrv.CPP:12376-12379). Red on unfixed code: the chat never fired.</summary>
    [SkippableFact]
    public void Non_victory_station_kill_announces_the_destruction_to_everyone()
    {
        using var server = new Srv();
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;
        var mission = server.Missions[0];

        using var bot = new FltBot();
        short shipId = Seat(server, bot, "p865_chat");
        bot.ResetChatReceived();

        var victim = new Station(
            DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
            { StationId = 951, SideId = 1, ClusterId = mission.Sectors[0].Id, Position = [500f, 500f, 500f], Name = "P865 victim" },
            LandType(mission.Core));
        mission.PlaceStation(victim, mission.Sectors[0]);
        var killer = new Ship(952, mission.Core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmFighter)),
            new Vector3(600f, 600f, 600f), Vector3.Zero, Orientation.Identity) { SideId = 0, IsNpc = true };
        mission.PlaceShip(killer, mission.Sectors[0]);

        mission.NotifyStationVictory(victim, killer);                   // a kill: no capture, no build
        PumpUntil(server, () => bot.Chats.Count > 0, 200);

        Assert.Contains(bot.Chats, c => c.Message == "Alpha has destroyed Bravo's P865 victim.");   // FedSrv.CPP:12376-12379
        Assert.NotEqual(MissionStage.Over, mission.Stage);              // no victory — the game goes on
    }

    // ── SIM-0780 — RefillPart's unconditional successor upgrade ──────────────

    /// <summary>⭐ SIM-0780 — native RefillPart upgrades a mounted part to its
    /// buyable successor UNCONDITIONALLY: it never charges and never keeps the
    /// base part when the value differential is unaffordable (FedSrv.CPP:
    /// 12592-12620 — the player already owns the tech). Red on unfixed code: an
    /// empty wallet kept the base part at dock.</summary>
    [SkippableFact]
    public void Dock_restock_upgrades_the_successor_even_on_an_empty_wallet()
    {
        Require.Artwork();
        using var server = new Srv();
        DataStationIGC garrison = default;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            garrison = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
            var sector = mission.Sectors.First(s => s.Stations.Contains(garrison));
            var hull = core.HullTypes.Values.First(h => h.CanMountPart(core.PartTypes.Values.First(p => p.SuccessorPartId >= 0), 0));
            var ship = new Ship(shipId, hull,
                new Vector3(garrison.Position[0] + Sector.DockRadius + 50f, garrison.Position[1], garrison.Position[2]),
                Vector3.Zero, Orientation.Identity) { SideId = garrison.SideId };
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "p865_wallet");
        bot.SendLogonRequest(Request("p865_wallet"));
        PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        short shipId = bot.ShipAssignment!.Ship.ShipId;
        var ship = server.Ships.First(s => s.Ship.Id == shipId).Ship;
        var mission = ship.Mission!;
        var core = mission.Core;
        var side = mission.Sides.First(s => s.Id == ship.SideId);

        // A real successor pair the hull can mount; grant the WHOLE chain's
        // required techs — RefillPart's successor walk needs every hop buyable
        // (FedSrv.CPP:12595-12600), not just the leaf.
        var (basePt, leaf) = FindUpgradeChain(mission, side);
        ship.ClearLoadout();
        Assert.NotNull(ship.MountPart(basePt, 1, 0, byte.MaxValue));
        var chainTechs = side.DevelopmentTechs;
        var cursor = basePt;
        var guard = 0;
        while (guard++ < 64)
        {
            chainTechs = chainTechs.Or(TechTreeBitMask.FromBytes(cursor.TtbmRequired));
            if (cursor.SuccessorPartId < 0 || !core.PartTypes.TryGetValue(cursor.SuccessorPartId, out var nxt)) break;
            cursor = nxt;
        }
        side.DevelopmentTechs = chainTechs;

        // Empty wallet: the old port kept the base part; native upgrades anyway.
        server.Ships.First(s => s.Ship.Id == shipId).Wallet.Money = 0;

        ship.Position = DockTestPlacement.Inside(mission, garrison, ship.HullType); // a real bay interior, not the centre
        PumpUntil(server, () => ship.StationId is not null, 600);

        Assert.Equal(leaf.PartId, ship.Parts.Single(p => p.MountId == 0).Type.PartId);  // upgraded, wallet or not
    }

    /// <summary>Discover a PCore001 part chain whose successor only becomes
    /// buyable once the leaf's required techs are granted (same shape as the
    /// P494 helper).</summary>
    private static (DataPartTypeIGC Base, DataPartTypeIGC Leaf) FindUpgradeChain(Mission mission, Side side)
    {
        var core = mission.Core;
        foreach (var basePt in core.PartTypes.Values.Where(p => p.SuccessorPartId >= 0))
        {
            if (SuccessorUpgrade.PartSuccessor(core, basePt, null,
                    side.BuildingTechs.Or(TechTreeBitMask.FromBytes(basePt.TtbmRequired))).PartId != basePt.PartId)
                continue;
            var afterTech = side.BuildingTechs.Or(TechTreeBitMask.FromBytes(basePt.TtbmRequired));
            var leaf = basePt;
            var seen = new HashSet<short> { basePt.PartId };
            short? next = basePt.SuccessorPartId;
            while (next is { } id && core.PartTypes.TryGetValue(id, out var cand) && seen.Add(id))
            {
                afterTech = afterTech.Or(TechTreeBitMask.FromBytes(cand.TtbmRequired));
                leaf = cand;
                next = cand.SuccessorPartId;
            }
            if (leaf.PartId != basePt.PartId) return (basePt, leaf);
        }
        throw new InvalidOperationException("no part-upgrade chain found in PCore001");
    }
}
