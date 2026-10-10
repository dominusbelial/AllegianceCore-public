using Allegiance.Core.Model;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
// 'Server'/'Bot' are also namespaces — alias to unambiguous identifiers (the
// same pattern WarpTransitServerTests and P398's MapGenRingLiveProofTests use).
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Mg = global::Allegiance.Server.MapGenConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P399 — the live proof that a radial/spoke topology beyond the ring family
/// is genuinely playable. Boosts a real server configured for procedural
/// <c>SnowFlake</c> generation (no map file) and drives a real managed client
/// over the real TCP transport through a real generated warp into a genuine
/// neighbour sector and into a generated home station — the SnowFlake
/// equivalent of P397/P398's live proofs. SnowFlake (2 teams, iMapSize 0)
/// generates 8 sectors (homes 0 and 4, each with a 3-neutral triangle). The
/// traced adjacency (mapmakerigc.cpp:2138-2236) is embedded below and the
/// transit assertion checks the destination is a real SnowFlake neighbour.
/// A second test boots all 4 new topologies (plus a regression file-load game)
/// headless through the real config path.
/// </summary>
[Collection("LiveNetwork")]
public class MapGenRadialLiveProofTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // SnowFlake 2-team/iMapSize-0 traced adjacency (see MapGenRadialFamilyTests).
    private static readonly Dictionary<short, short[]> SnowFlakeNeighbours = new()
    {
        [0] = [1, 2], [1] = [0, 2, 3, 6], [2] = [0, 1, 3, 5], [3] = [1, 2, 7],
        [4] = [5, 6], [5] = [2, 4, 6, 7], [6] = [1, 4, 5, 7], [7] = [3, 5, 6],
    };

    private static Cfg SnowConfig(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm
            {
                Name = "Generated SnowFlake Live",
                Map = "XXV_simple.igc",                 // present but IGNORED — MapGen takes the branch
                MapGen = new Mg { Type = "SnowFlake" },  // ← the P399 generation path under test
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
        SteamAuthTicketLength = 0, SteamID = 72600000000000000ul, DrmHash = "p399snow",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>⭐ Headless boot proves every radial topology is selectable from
    /// server config: one server, six games (the 4 new topologies, PinWheel in
    /// its multi-centre layout, plus a plain <c>Map</c>-file regression game),
    /// each map generated through the real <c>ToCoreParams →
    /// MissionMapGenerator.Generate → Mission.Assemble</c> boot path with the
    /// expected sector count and a real generated home base + ore.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void AllRadialTypes_BootHeadless_AreConfigSelectable()
    {
        Require.Artwork();
        // (name, topology, teams, expectedSectors); teams null => file-load game.
        var games = new (string name, string? type, int teams, int sectors)[]
        {
            ("Star", "Star", 2, 12),
            ("PinWheel", "PinWheel", 2, 3),                 // single-centre (MapSize 0)
            ("PinWheelMulti", "PinWheel", 2, 4),            // multi-centre (MapSize 1)
            ("DiamondRing", "DiamondRing", 3, 9),           // requires >2 teams
            ("SnowFlake", "SnowFlake", 2, 8),
            ("PlainFileLoad", null!, 2, 0),                 // real map file, MapGen absent — regression
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
                Map = "XXV_simple.igc",                      // ignored when MapGen set
                MapGen = g.type is null ? null : new Mg { Type = g.type, MapSize = g.name == "PinWheelMulti" ? 1 : 0 },
                Teams = g.teams,
                CivIds = g.teams == 3 ? [1, 3, 2] : [1, 3],
                SideNames = g.teams == 3 ? ["Alpha", "Bravo", "Charlie"] : ["Alpha", "Bravo"],
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

    /// <summary>⭐ A peer joins the generated SnowFlake map, is spawned inside a
    /// generated warp's collision volume, and is transited to a genuine
    /// SnowFlake neighbour sector (per the traced adjacency) — the generated
    /// radial topology is traversable over the real wire path.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void GeneratedSnowFlake_PeerFliesThroughWarp_ToTracedNeighbour()
    {
        Require.Artwork();
        using var server = new Srv();

        short? spawnSectorId = null;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var warp = mission.Warps.First(w => w.Destination is not null && w.Partner() is not null);
            var sector = mission.Sectors.First(s => s.Warps.Contains(warp));
            spawnSectorId = sector.Id;
            var wpos = new Vector3(warp.Warp.Position![0], warp.Warp.Position[1], warp.Warp.Position[2]);

            float[] f = warp.Warp.Forward is { Length: 3 } v && (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]) > 0f
                ? v : [1f, 0f, 0f];
            var dir = new Vector3(f[0], f[1], f[2]).Normalize();

            float shipRadius = 0.5f * hull.Length;
            float radius = warp.Warp.Radius + shipRadius;
            var spawn = wpos + dir * (0.9f * radius);
            var ship = new Ship(shipId, hull, spawn, -dir * 20f, Orientation.Identity);
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };

        server.Boot(SnowConfig());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        // The generated mission really is an 8-sector SnowFlake.
        Assert.Equal(8, server.Missions.Single().Sectors.Count);
        Assert.Contains(server.Missions.Single().Sectors, s => s.Cluster.HomeSector);

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "p399_warp");
        bot.SendLogonRequest(Request("p399_warp"));

        PumpUntil(server, () => bot.Assignments.Count >= 1 && bot.ShipStatuses.Count >= 1, 8000);
        short initial = bot.Assignments[0].SectorId;
        Assert.InRange(initial, (short)0, (short)7);
        Assert.Equal(spawnSectorId!.Value, initial);          // spawned in a generated sector

        PumpUntil(server,
            () => bot.Assignments.Count >= 2 && bot.Assignments[^1].SectorId != initial,
            8000);
        Assert.True(bot.Assignments.Count >= 2,
            $"never crossed: sectors=[{string.Join(",", bot.Assignments.Select(a => a.SectorId))}]");
        short next = bot.Assignments[^1].SectorId;

        var live = server.Ships.First(l => l.Ship.Id == bot.Assignments[0].Ship.ShipId);
        Assert.Equal(next, live.Sector.Id);
        Assert.Equal(next, live.Ship.Sector?.Id);

        // The destination is a real SnowFlake neighbour of the origin.
        var neigh = SnowFlakeNeighbours[initial];
        Assert.True(neigh.Contains(next),
            $"transited from generated sector {initial} to {next}; expected a SnowFlake neighbour of {initial} "
            + $"(got {string.Join(",", neigh)})");
    }

    /// <summary>⭐ A peer joins the generated SnowFlake map with the default
    /// spawner, which seats it in its side's generated home sector; driving
    /// into the generated home station's dock radius docks it — the generated
    /// SnowFlake base is genuinely dockable.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void GeneratedSnowFlake_DefaultSpawnedPeer_DocksAtGeneratedHomeStation()
    {
        Require.Artwork();
        using var server = new Srv();
        // ShipSpawner NOT overridden — exercise the real default spawner.
        server.Boot(SnowConfig());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "p399_dock");
        bot.SendLogonRequest(Request("p399_dock"));

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
        Console.WriteLine($"P399-SNOWFLAKE-DOCK-LIVE: peer (side {side}) docked at generated home station "
            + $"{homeStation.StationId} '{homeStation.Name}' in sector {live.Sector.Id} '{live.Sector.Name}'");
    }
}
