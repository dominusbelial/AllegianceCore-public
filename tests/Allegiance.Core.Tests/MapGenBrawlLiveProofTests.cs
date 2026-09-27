using Allegiance.Core.Model;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
// 'Server'/'Bot' are also namespaces — alias to unambiguous identifiers (the
// same pattern the P397/P398/P399/P400 live proofs use).
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Mg = global::Allegiance.Server.MapGenConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// P401 — the live proof that Brawl (the final, structurally-odd topology) is
/// genuinely playable. Brawl builds ONE shared sector instead of a multi-
/// cluster warp layout (native <c>CmapMakerBrawlIGC</c>,
/// <c>mapmakerigc.cpp:1707-1798</c>), so the whole point is that every side's
/// home station is co-located in the same sector and combat/mining is
/// immediately possible with no travel. Boosts a real server configured for
/// procedural <c>Brawl</c> generation (no map file), connects real clients for
/// TWO different sides over the real TCP transport, confirms each docks at its
/// own correctly-placed home station in the single shared sector, and confirms
/// both ships share that one sector (so opposing sides are instantly in combat
/// range and minable ore is right there). A headless boot test also proves
/// Brawl is config-selectable.
/// </summary>
[Collection("LiveNetwork")]
public class MapGenBrawlLiveProofTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg BrawlConfig(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm
            {
                Name = "Generated Brawl Live",
                Map = "XXV_simple.igc",              // present but IGNORED — MapGen takes the branch
                MapGen = new Mg { Type = "Brawl" },   // ← the P401 generation path under test
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
        SteamAuthTicketLength = 0, SteamID = 72600000000000000ul, DrmHash = "p401brawl",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>⭐ Headless boot proves Brawl is selectable from server config:
    /// one server, two games (Brawl + a plain <c>Map</c>-file regression),
    /// Brawl generating exactly ONE sector with 0 warps, 2 stations and minable
    /// ore through the real <c>ToCoreParams → Generate → Assemble</c> path.</summary>
    [Fact]
    public void Brawl_BootHeadless_ConfigSelectable()
    {
        var games = new (string name, string? type, int teams, int sectors, int stations)[]
        {
            ("Brawl", "Brawl", 2, 1, 2),
            ("PlainFileLoad", null!, 2, 0, 0),   // real map file, MapGen absent — regression
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
                Map = "XXV_simple.igc",               // ignored when MapGen set
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
            var (name, type, _, sectors, stations) = games[i];
            var mission = server.Missions[i];   // boot preserves game order
            if (type is null)
            {
                Assert.Equal(13, mission.Sectors.Count);
                Console.WriteLine($"[boot-regression] game '{name}' loaded file map: {mission.Sectors.Count} sectors");
            }
            else
            {
                Assert.Equal(sectors, mission.Sectors.Count);
                Assert.Equal(stations, mission.Sectors.Sum(s => s.Stations.Count));
                Assert.Equal(0, mission.Sectors.Sum(s => s.Warps.Count));   // single shared sector — no warps
                Assert.Contains(mission.Sectors, s => s.Mineable.Any(a => a.CanMineHe3));
                Console.WriteLine($"[boot] game '{name}' generating map: topology={type}, "
                    + $"{mission.Sectors.Count} sector / {mission.Sectors.Sum(s => s.Warps.Count)} warps / "
                    + $"{mission.Sectors.Sum(s => s.Stations.Count)} stations");
            }
        }
    }

    /// <summary>⭐ THE live proof of Brawl's whole point: two real clients on
    /// two different sides join a generated Brawl map and BOTH ships sit in the
    /// SAME single shared sector — the sector is one map-wide arena, so opposing
    /// sides are instantly in combat range and minable ore is right there (no
    /// travel through warps, because there are none). Each bot then docks at
    /// its OWN side's correctly-placed home station inside that shared sector
    /// (the default spawner seats peer N → side N and P368 resolves its home to
    /// the sector holding that side's owned station — which for Brawl is the one
    /// sector).</summary>
    [Fact]
    public void Brawl_TwoSidesShareOneSector_EachDocksAtOwnHomeStation()
    {
        using var server = new Srv();
        server.Boot(BrawlConfig());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        var mission = server.Missions.Single();
        // The generated mission really is the one-sector Brawl.
        Assert.Single(mission.Sectors);
        Assert.Equal(0, mission.Sectors.Sum(s => s.Warps.Count));
        Assert.Equal(2, mission.Sectors[0].Stations.Count);   // one home per side, co-located

        using var botA = new FltBot();
        botA.Connect(server.BoundPort, "p401_brawl_a");
        botA.SendLogonRequest(Request("p401_brawl_a"));
        PumpUntil(server, () => botA.ShipAssignment is not null, 8000);
        Assert.NotNull(botA.ShipAssignment);

        using var botB = new FltBot();
        botB.Connect(server.BoundPort, "p401_brawl_b");
        botB.SendLogonRequest(Request("p401_brawl_b"));
        PumpUntil(server, () => botB.ShipAssignment is not null, 8000);
        Assert.NotNull(botB.ShipAssignment);

        // Map each bot to its live ship (server.Ships order ≠ join order).
        var liveA = server.Ships.Single(l => l.Ship.Id == botA.Assignments[0].Ship.ShipId);
        var liveB = server.Ships.Single(l => l.Ship.Id == botB.Assignments[0].Ship.ShipId);
        short sideA = liveA.Ship.SideId!.Value;
        short sideB = liveB.Ship.SideId!.Value;
        Assert.NotEqual(sideA, sideB);                       // two DIFFERENT sides
        Assert.Equal(0, sideA);                              // round-robin: peer 0 → side 0
        Assert.Equal(1, sideB);                              // peer 1 → side 1

        // BOTH ships are in the SAME single shared sector — the map-wide arena.
        Assert.Same(liveA.Sector, liveB.Sector);
        Assert.Equal(0, liveA.Sector.Id);
        Assert.Single(mission.Sectors);

        // Combat is IMMEDIATELY possible: the two home stations (owned by the
        // two opposing sides) are co-located in this one sector — no travel.
        var stationA = liveA.Sector.Stations.Single(st => st.SideId == sideA);
        var stationB = liveA.Sector.Stations.Single(st => st.SideId == sideB);
        Assert.Equal(liveA.Sector.Id, stationA.ClusterId);
        Assert.Equal(liveA.Sector.Id, stationB.ClusterId);

        // Mining is IMMEDIATELY possible: the shared sector holds minable ore.
        Assert.Contains(liveA.Sector.Mineable, a => a.CanMineHe3);

        // Each bot docks at ITS OWN home station in the shared sector.
        DockAt(server, liveA.Ship, stationA);
        DockAt(server, liveB.Ship, stationB);

        Assert.Equal(stationA.StationId, liveA.Ship.StationId!.Value);
        Assert.Equal(stationB.StationId, liveB.Ship.StationId!.Value);
        Console.WriteLine("P401-BRAWL-LIVE: sides 0 and 1 both seated in the single shared sector "
            + $"'{liveA.Sector.Name}' (id 0); side {sideA} docked at station {stationA.StationId} "
            + $"'{stationA.Name}', side {sideB} docked at station {stationB.StationId} '{stationB.Name}' "
            + "— combat/mining immediately possible, no warps");
    }

    private static void DockAt(Srv server, Ship ship, DataStationIGC station)
    {
        var pos = new Vector3(station.Position![0], station.Position[1], station.Position[2]);
        var target = pos + new Vector3(60f, 0f, 0f);   // inside DockRadius (200)
        ship.Position = target;
        ship.Velocity = Vector3.Zero;
        PumpUntil(server, () => ship.StationId is not null, 600);
        Assert.NotNull(ship.StationId);   // the move into the dock radius actually docks it
    }
}
