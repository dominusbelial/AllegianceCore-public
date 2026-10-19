using Allegiance.Core.Model;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
// 'Server'/'Bot' are also namespaces — alias to unambiguous identifiers (the
// same pattern WarpTransitServerTests and P397's MapGenLiveProofTests use).
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Mg = global::Allegiance.Server.MapGenConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P398 — the live proof that a ring-family topology beyond SingleRing is
/// genuinely playable, not just structurally correct in isolation. Boosts a
/// real server configured for procedural <c>HiLo</c> generation (no map file),
/// and drives a real managed client over the real TCP transport through a real
/// generated warp (ServerHost.HitWarpEvent) into a genuine neighbour sector and
/// into a generated home station — the HiLo equivalent of P397's SingleRing
/// live proofs. HiLo (2 teams, iMapSize 0) generates 9 sectors: 2 home, 6
/// neutral in the low ring, plus a central (other) sector id 8. The traced
/// adjacency (mapmakerigc.cpp:1328-1369) is embedded below and the transit
/// assertion checks the destination is a real HiLo neighbour.
/// </summary>
[Collection("LiveNetwork")]
public class MapGenRingLiveProofTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // HiLo 2-team/iMapSize-0 traced adjacency (see MapGenRingFamilyTests).
    private static readonly Dictionary<short, short[]> HiLoNeighbours = new()
    {
        [0] = [1, 2, 5, 7], [1] = [0, 4, 8], [2] = [0, 3], [3] = [2, 4],
        [4] = [1, 3, 5, 6], [5] = [0, 4, 8], [6] = [4, 7], [7] = [0, 6], [8] = [1, 5],
    };

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm
            {
                Name = "Generated HiLo Live",
                Map = "XXV_simple.igc",                 // present but IGNORED — MapGen takes the branch
                MapGen = new Mg { Type = "HiLo" },       // ← the P398 generation path under test
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
        FedsrvVer = LogonRequest.MsgVer, Time = 911u, DwCookie = 0xBEEFu,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 72600000000000000ul, DrmHash = "p398hilo",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>⭐ Headless boot proves every ring-family topology is selectable
    /// from server config: one server, seven games (one per
    /// <c>MapGenConfig.Type</c>), each map generated through the real
    /// <c>ToCoreParams → MissionMapGenerator.Generate → Mission.Assemble</c> boot
    /// path with the expected sector count and a real generated home base + ore.
    /// Also re-proves the plain file-load branch is untouched (game 8 loads
    /// <c>Map</c> from a real file, not MapGen).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void AllRingTypes_BootHeadless_AreConfigSelectable()
    {
        Require.Artwork();
        // (name, topology, expectedSectors)
        var games = new (string name, string type, int sectors)[]
        {
            ("SingleRing", "SingleRing", 4),
            ("DoubleRing", "DoubleRing", 4),
            ("BigRing", "BigRing", 6),
            ("HiLo", "HiLo", 9),
            ("HiHigher", "HiHigher", 13),
            ("InsideOut", "InsideOut", 13),
            ("LargeSplit", "LargeSplit", 15),
            ("PlainFileLoad", null!, 0),   // real map file, MapGen absent — regression
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
                Map = g.type is null ? "XXV_simple.igc" : "XXV_simple.igc", // ignored when MapGen set
                MapGen = g.type is null ? null : new Mg { Type = g.type },
                Teams = 2,
                CivIds = [1, 3],
                SideNames = ["Alpha", "Bravo"],
            }).ToList(),
        };

        using var server = new Srv();
        server.Boot(cfg);
        Assert.Equal(games.Length, server.Missions.Count);

        for (int i = 0; i < games.Length; i++)
        {
            var (name, type, sectors) = games[i];
            var mission = server.Missions[i];   // boot preserves game order
            if (type is null)
            {
                // Plain file-load path: XXV_simple.igc — same byte-for-byte load as ever.
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

    /// <summary>⭐ A peer joins the generated HiLo map, is spawned inside the
    /// first generated warp's collision volume, and is transited to a genuine
    /// HiLo neighbour sector (per the traced adjacency) — the generated
    /// non-trivial topology is traversable over the real wire path.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void GeneratedHiLo_PeerFliesThroughWarp_ToTracedNeighbour()
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

        server.Boot(Config());

        // The generated mission really is a 9-sector HiLo.
        Assert.Equal(9, server.Missions.Single().Sectors.Count);
        Assert.Contains(server.Missions.Single().Sectors, s => s.Cluster.HomeSector);

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "p398_warp");
        bot.SendLogonRequest(Request("p398_warp"));

        PumpUntil(server, () => server.Ships.Count == 1, 8000);
        var seated = Assert.Single(server.Ships);
        Assert.True(SpinWait.SpinUntil(() => bot.Assignments.Count >= 1
            && bot.ShipStatuses.Any(s => s.ShipId == seated.Ship.Id), TimeSpan.FromSeconds(30)),
            "peer must decode its initial assignment and own status before transit");
        short initial = bot.Assignments[0].SectorId;
        Assert.InRange(initial, (short)0, (short)8);
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

        // The destination is a real HiLo neighbour of the origin (traced graph).
        var neigh = HiLoNeighbours[initial];
        Assert.True(neigh.Contains(next),
            $"transited from generated sector {initial} to {next}; expected a HiLo neighbour of {initial} "
            + $"(got {string.Join(",", neigh)})");
    }

    /// <summary>⭐ A peer joins the generated HiLo map with the default spawner,
    /// which seats it in its side's generated home sector; driving into the
    /// generated home station's dock radius docks it — the generated HiLo base
    /// is genuinely dockable.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void GeneratedHiLo_DefaultSpawnedPeer_DocksAtGeneratedHomeStation()
    {
        Require.Artwork();
        using var server = new Srv();
        // ShipSpawner NOT overridden — exercise the real default spawner.
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "p398_dock");
        bot.SendLogonRequest(Request("p398_dock"));

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
        Console.WriteLine($"P398-HILO-DOCK-LIVE: peer (side {side}) docked at generated home station "
            + $"{homeStation.StationId} '{homeStation.Name}' in sector {live.Sector.Id} '{live.Sector.Name}'");
    }
}
