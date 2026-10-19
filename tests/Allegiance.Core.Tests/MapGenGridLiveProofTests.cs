using Allegiance.Core.Model;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
// 'Server'/'Bot' are also namespaces — alias to unambiguous identifiers (the
// same pattern WarpTransitServerTests and the P397/P398/P399 live proofs use).
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Mg = global::Allegiance.Server.MapGenConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P400 — the live proof that a grid/geographic-family topology is genuinely
/// playable. Boosts a real server configured for procedural <c>Grid</c>
/// generation (no map file) and drives a real managed client over the real TCP
/// transport through a real generated warp into a genuine Grid neighbour sector
/// and into a generated home station — the Grid equivalent of P397/P398/P399's
/// live proofs. Grid (2 teams, iMapSize 0) generates 10 sectors (homes 0 and
/// 5), each a 4-regular vertex of the traced grid (mapmakerigc.cpp:2364-2414,
/// embedded below). A second test boots all 3 new topologies (plus a
/// regression file-load game) headless through the real config path.
/// </summary>
[Collection("LiveNetwork")]
public class MapGenGridLiveProofTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // Grid 2-team/iMapSize-0 traced adjacency (see MapGenGridFamilyTests).
    private static readonly Dictionary<short, short[]> GridNeighbours = new()
    {
        [0] = [1, 2, 8, 9], [1] = [0, 2, 4, 9], [2] = [0, 1, 3, 8],
        [3] = [2, 4, 5, 7], [4] = [1, 3, 5, 6], [5] = [3, 4, 6, 7],
        [6] = [4, 5, 7, 9], [7] = [3, 5, 6, 8], [8] = [0, 2, 7, 9],
        [9] = [0, 1, 6, 8],
    };

    private static Cfg GridConfig(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm
            {
                Name = "Generated Grid Live",
                Map = "XXV_simple.igc",              // present but IGNORED — MapGen takes the branch
                MapGen = new Mg { Type = "Grid" },     // ← the P400 generation path under test
                Teams = 2,
                CivIds = [1, 3],
                SideNames = ["Alpha", "Bravo"],
            },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 910u, DwCookie = 0xBEEFu,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 72600000000000000ul, DrmHash = "p400grid",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>⭐ Headless boot proves every grid/geographic-family topology is
    /// selectable from server config: one server, four games (the 3 new
    /// topologies plus a plain <c>Map</c>-file regression game), each map
    /// generated through the real <c>ToCoreParams →
    /// MissionMapGenerator.Generate → Mission.Assemble</c> boot path with the
    /// expected sector count and a real generated home base + ore.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void AllGridTypes_BootHeadless_AreConfigSelectable()
    {
        Require.Artwork();
        var games = new (string name, string? type, int teams, int sectors)[]
        {
            ("Grid", "Grid", 2, 10),
            ("EastWest", "EastWest", 2, 26),
            ("SplitBase", "SplitBase", 2, 8),
            ("PlainFileLoad", null!, 2, 0),              // real map file, MapGen absent — regression
        };

        var cfg = new Cfg
        {
            ArtPath = ArtRoot,
            StaticCore = "PCore001.igc",
            Port = 0,
            UpdatesPerSecond = 20,
            Games = games.Select(g => new Gm
            {
                Name = g.name,
                Map = "XXV_simple.igc",                  // ignored when MapGen set
                MapGen = g.type is null ? null : new Mg { Type = g.type },
                Teams = g.teams,
                CivIds = [1, 3],
                SideNames = ["Alpha", "Bravo"],
            }).ToList(),
        };

        using var server = new Srv();
        server.Boot(cfg);
        Assert.Equal(games.Length, server.Missions.Count);

        for (int i = 0; i < games.Length; i++)
        {
            var (name, type, _, sectors) = games[i];
            var mission = server.Missions[i];   // boot preserves game order
            if (type is null)
            {
                Assert.Equal(13, mission.Sectors.Count);
                Console.WriteLine($"[boot-regression] game '{name}' loaded file map: {mission.Sectors.Count} sectors");
            }
            else
            {
                Assert.Equal(sectors, mission.Sectors.Count);
                Assert.Contains(mission.Sectors, s => s.Cluster.HomeSector);
                Assert.Contains(mission.Sectors, s => s.Mineable.Any(a => a.CanMineHe3));
                int stations = mission.Sectors.Sum(s => s.Stations.Count);
                Console.WriteLine($"[boot] game '{name}' generating map: topology={type}, "
                    + $"{mission.Sectors.Count} sectors / {stations} stations");
            }
        }
    }

    /// <summary>⭐ A peer joins the generated Grid map, enters a clear approach to a
    /// generated warp's collision volume, and is transited to a genuine Grid
    /// neighbour sector (per the traced adjacency) — the generated grid-family
    /// topology is traversable over the real wire path.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void GeneratedGrid_PeerFliesThroughWarp_ToTracedNeighbour()
    {
        Require.Artwork();
        using var server = new Srv();

        short? spawnSectorId = null;
        GeneratedWarpEntry? entry = null;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            entry = GeneratedWarpEntry.Find(mission, hull);
            spawnSectorId = entry.Sector.Id;
            var ship = new Ship(shipId, hull, entry.Holding, Vector3.Zero, Orientation.Identity);
            mission.PlaceShip(ship, entry.Sector);
            return (ship, entry.Sector);
        };

        server.Boot(GridConfig());

        // The generated mission really is a 10-sector Grid.
        Assert.Equal(10, server.Missions.Single().Sectors.Count);
        Assert.Contains(server.Missions.Single().Sectors, s => s.Cluster.HomeSector);

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "p400_warp");
        bot.SendLogonRequest(Request("p400_warp"));

        PumpUntil(server, () => server.Ships.Count == 1, 8000);
        var seated = Assert.Single(server.Ships);
        Assert.True(SpinWait.SpinUntil(() => bot.Assignments.Count >= 1
            && bot.ShipStatuses.Any(s => s.ShipId == seated.Ship.Id), TimeSpan.FromSeconds(30)),
            "peer must decode its initial assignment and own status before transit");
        short initial = bot.Assignments[0].SectorId;
        Assert.InRange(initial, (short)0, (short)9);
        Assert.Equal(spawnSectorId!.Value, initial);          // spawned in a generated sector

        // Settle the paused sector/model clocks while parked outside every warp,
        // then enter a checked clear collision volume for exactly one transit tick.
        server.Missions.Single().Stage = MissionStage.Started;
        server.TickSim();
        Assert.Equal(initial, seated.Ship.Sector?.Id);
        Assert.Null(seated.Ship.StationId);
        Assert.False(seated.Ship.Dead);
        entry!.Enter(seated.Ship, server.SimNow);
        server.TickSim();
        short destination = entry.Warp.Destination!.Id;
        Assert.True(seated.Ship.Sector?.Id == destination,
            $"clear warp entry did not transit: warp={entry.Warp.Warp.WarpId}, entry={entry.Entry}, "
            + $"position={seated.Ship.Position}, sector={seated.Ship.Sector?.Id}, expected={destination}");
        Assert.True(SpinWait.SpinUntil(() => bot.Assignments.Count >= 2
            && bot.Assignments[^1].SectorId == destination, TimeSpan.FromSeconds(30)),
            "peer must decode the authoritative generated-warp transit");
        short next = bot.Assignments[^1].SectorId;

        var live = server.Ships.First(l => l.Ship.Id == bot.Assignments[0].Ship.ShipId);
        Assert.Equal(next, live.Sector.Id);
        Assert.Equal(next, live.Ship.Sector?.Id);

        // The destination is a real Grid neighbour of the origin.
        var neigh = GridNeighbours[initial];
        Assert.True(neigh.Contains(next),
            $"transited from generated sector {initial} to {next}; expected a Grid neighbour of {initial} "
            + $"(got {string.Join(",", neigh)})");
    }

    /// <summary>⭐ A peer joins the generated Grid map with the default spawner,
    /// which seats it in its side's generated home sector; driving into the
    /// generated home station's dock radius docks it — the generated Grid base
    /// is genuinely dockable.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void GeneratedGrid_DefaultSpawnedPeer_DocksAtGeneratedHomeStation()
    {
        Require.Artwork();
        using var server = new Srv();
        // ShipSpawner NOT overridden — exercise the real default spawner.
        server.Boot(GridConfig());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "p400_dock");
        bot.SendLogonRequest(Request("p400_dock"));

        PumpUntil(server,
            () => server.Ships.Count > 0
               && server.Ships.First().Ship.SideId is not null
               && server.Ships.First().Ship.Mission is not null,
            8000);
        var live = server.Ships.First();
        var ship = live.Ship;
        short side = ship.SideId!.Value;

        var homeStation = live.Sector.Stations.Single(st => st.SideId == side);
        Assert.NotNull(homeStation.Position);
        var stationPos = new Vector3(homeStation.Position[0], homeStation.Position[1], homeStation.Position[2]);

        Assert.True(live.Sector.Cluster.HomeSector, "peer spawned in a generated home sector");

        var target = stationPos + new Vector3(60f, 0f, 0f);   // inside DockRadius (200)
        ship.Position = target;
        ship.Velocity = Vector3.Zero;
        PumpUntil(server, () => ship.StationId is not null, 400);

        Assert.NotNull(ship.StationId);
        Assert.Equal(homeStation.StationId, ship.StationId!.Value);
        Console.WriteLine($"P400-GRID-DOCK-LIVE: peer (side {side}) docked at generated home station "
            + $"{homeStation.StationId} '{homeStation.Name}' in sector {live.Sector.Id} '{live.Sector.Name}'");
    }
}
