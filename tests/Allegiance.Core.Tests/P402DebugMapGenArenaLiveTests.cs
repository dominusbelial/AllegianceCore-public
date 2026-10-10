using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
// 'Server'/'Bot' are also namespaces — alias to unambiguous identifiers (the
// same pattern the P397-P401 live proofs and P393 use).
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Mg = global::Allegiance.Server.MapGenConfig;
using Ls = global::Allegiance.Server.LiveSettingsConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P402 — swap debug mode's hand-built test arena (real m2xspiderw
/// Omega/Plato/Alderan + synthetic injected garrisons + manual warp removal)
/// over to a <c>MapGen</c>-generated arena, deleting the station-injection /
/// warp-removal machinery for that path while keeping everything debug mode
/// needs for unattended autonomous testing (hull-mix ship spawning, docked
/// initial miners, mission auto-start). This task adds a generated-arena branch
/// and the real-map branch is preserved byte-for-byte (regression-proven by the
/// existing P379/P382/P393 live tests, still green).
///
/// The three live proofs here, over a real in-process debug-mode server with
/// <c>MapGen = Brawl</c> (no map file) on the real TCP transport:
/// <list type="number">
/// <item><b>The "economy without real users" proof (required)</b> — boot with
///      ZERO players connected: the mission auto-starts (P387), each side's
///      initial miner spawns DOCKED at its real generated home station (the
///      generic <c>ResolveSideHomeStation</c> path, never a synthetic 9000x
///      garrison), auto-undocks (P381) and acquires a mining target (actually
///      mines) — all unattended, exactly as today's real-map debug mode does,
///      just against the generated arena.</item>
/// <item><b>Two real clients on two different sides (required)</b> — connect
///      four real FltBots so the hull-mix alternation is fully exercised: peers
///      settle sides 0,1,0,1 with hulls Scout,Scout,Fighter,Fighter; each lives
///      in the single shared generated Brawl sector; two of them (one per side)
///      dock at their OWN correctly-placed generated home station — dock/buy
///      works, and the two opponents are in the same sector (Brawl's whole
///      point, no warps).</item>
/// <item><b>Death/respawn resolves via the generic path on the generated
///      arena</b> — a lifepod suicide respawns at the ship's OWN real generated
///      home station (not a synthetic garrison, which does not exist here), the
///      <c>FindFriendlyStation</c> / <c>FindDebugGarrisonStation</c> generated-
///      map fallthrough. Mirrors the P393 real-map proof (which still asserts
///      the synthetic station there) to pin the branch split.</item>
/// </list>
/// </summary>
[Collection("LiveNetwork")]
public sealed class P402DebugMapGenArenaLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // Must match the server impl's 90000s debug-garrison ids.
    private static readonly short OmegaDebugStationId = unchecked((short)90001);
    private static readonly short PlatoDebugStationId = unchecked((short)90002);

    private static Cfg BrawlDebugConfig() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        LiveSettings = new Ls { DebugMode = true },   // P402: debug mode on the GENERATED arena
        Games =
        [
            new Gm
            {
                Name = "P402 Generated Brawl Debug",
                Map = "XXV_simple.igc",               // present but IGNORED — MapGen takes the branch
                MapGen = new Mg { Type = "Brawl" },    // ← the P402 generated arena under test
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
        FedsrvVer = LogonRequest.MsgVer, Time = 951u, DwCookie = 0x402u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 7580000000000000000ul, DrmHash = "p402",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>The P382 initial miners: every IsNpc, PilotTypeMiner ship
    /// currently latched docked (<c>StationId != null</c>) in the mission.</summary>
    private static List<Ship> DockedInitialMiners(Srv server) =>
        server.Missions[0].Sectors
            .SelectMany(s => s.Models.OfType<Ship>())
            .Where(s => s.IsNpc && s.PilotType == Ship.PilotTypeMiner && s.StationId is not null)
            .ToList();

    /// <summary>⭐ REQUIRED observed proof — the "economy without real users":
    /// debug mode on a MapGen-generated Brawl arena, ZERO players connected.
    /// The mission auto-starts (P387), both sides' initial miners spawn DOCKED
    /// at their real generated home stations via the generic resolution path
    /// (never a synthetic 9000x garrison), auto-undock (P381) and acquire a
    /// mining target (mine) — all unattended, the exact behavior Domingo asked
    /// to preserve, now against the generated arena.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void GeneratedBrawlDebugMode_EconomyRunsUnattended_NoPlayers()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(BrawlDebugConfig());
        var mission = server.Missions.Single();

        // The generated arena really is the one-sector Brawl: one shared sector,
        // zero warps, two co-located real home stations (one per side).
        Assert.Single(mission.Sectors);
        Assert.Equal(0, mission.Sectors.Sum(s => s.Warps.Count));
        Assert.Equal(2, mission.Sectors[0].Stations.Count);
        Assert.Contains(mission.Sectors[0].Mineable, a => a.CanMineHe3);

        // P387 — in debug mode the mission auto-started at boot even though no
        // player ever connected (the whole point: unattended autonomous runs).
        Assert.NotEqual(MissionStage.NotStarted, mission.Stage);

        // One docked initial miner per side.
        var miners = DockedInitialMiners(server);
        Assert.Equal(2, miners.Count);
        var side0 = miners.Single(s => s.SideId == 0);
        var side1 = miners.Single(s => s.SideId == 1);

        // Both docked in the SAME single generated sector (Brawl: one arena).
        Assert.Same(side0.Sector, side1.Sector);
        Assert.Equal(0, side0.Sector!.Id);

        // Docked at the side's REAL generated home station — the generic
        // ResolveSideHomeStation path, NOT a synthetic 9000x garrison.
        Assert.NotNull(side0.StationId);
        Assert.NotNull(side1.StationId);
        Assert.NotEqual(OmegaDebugStationId, side0.StationId);
        Assert.NotEqual(PlatoDebugStationId, side1.StationId);
        Assert.NotEqual(OmegaDebugStationId, side1.StationId);
        Assert.NotEqual(PlatoDebugStationId, side0.StationId);

        // The docked station is that side's own generated station, resident in
        // the shared sector (correct owner + correct home).
        var st0 = mission.Sectors.SelectMany(s => s.Stations).Single(st => st.StationId == side0.StationId);
        var st1 = mission.Sectors.SelectMany(s => s.Stations).Single(st => st.StationId == side1.StationId);
        Assert.Equal((short)0, st0.SideId);
        Assert.Equal((short)1, st1.SideId);
        Assert.Equal(side0.Sector!.Id, st0.ClusterId);
        Assert.Equal(side1.Sector!.Id, st1.ClusterId);
        Console.WriteLine($"[P402-LIVE] no players: side 0 miner docked at generated station {st0.StationId} '{st0.Name}', side 1 at {st1.StationId} '{st1.Name}', both in shared sector '{side0.Sector.Name}'.");

        // P381 auto-undock (~10 s cooldown) with no input.
        PumpUntil(server, () => miners.All(m => m.StationId is null), 1200);
        Assert.All(miners, m => Assert.Null(m.StationId));

        // They actually MINE unattended: each acquires a He3 mining target.
        int mt = PumpUntil(server, () => miners.All(m => m.MiningAsteroidTarget is not null), 6000);
        Assert.True(mt < 6000, $"generated-arena miners never acquired a mining target with no player (ticks {mt})");
        foreach (var m in miners)
            Console.WriteLine($"[P402-LIVE] no players: side {m.SideId} miner targeted He3 asteroid {m.MiningAsteroidTarget!.Data.AsteroidId} in shared sector '{m.Sector!.Name}'.");
    }

    /// <summary>⭐ REQUIRED live proof — two (here four, to fully exercise the
    /// hull-mix alternation) real clients on different sides of a generated
    /// Brawl arena. Peers settle sides 0,1,0,1 with hulls Scout,Scout,Fighter,
    /// Fighter (the P379 hull-mix alternation kept verbatim), each in the single
    /// shared generated sector; two representatives (one per side) dock at their
    /// OWN correctly-placed generated home station — dock/buy works and the
    /// opposing sides are in the same sector (no warps).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void GeneratedBrawlDebugMode_TwoSidesDockOwnHomeStation_HullMixAlternates()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(BrawlDebugConfig());
        var mission = server.Missions.Single();
        mission.Stage = MissionStage.Started;   // dock needs Started

        // Expect the P379 closure alternation: peer n → side n%2, hull
        // (n/2)%2==1 → Fighter (else Scout). 4 peers → (s0,S),(s1,S),(s0,F),(s1,F).
        (short side, bool fighter)[] expected = [(0, false), (1, false), (0, true), (1, true)];

        var bots = new List<FltBot>();
        try
        {
            for (int i = 0; i < expected.Length; i++)
            {
                var b = new FltBot();
                b.Connect(server.BoundPort, $"p402_{i}");
                b.SendLogonRequest(Request($"p402_{i}"));
                PumpUntil(server, () => b.ShipAssignment is not null, 8000);
                Assert.NotNull(b.ShipAssignment);
                bots.Add(b);
            }

            // Map each bot to its live ship and verify side + hull mix + sector.
            for (int i = 0; i < expected.Length; i++)
            {
                var l = server.Ships.Single(x => x.Ship.Id == bots[i].Assignments[0].Ship.ShipId);
                Assert.Equal(expected[i].side, l.Ship.SideId!.Value);
                bool fighter = l.Ship.HullType.Name.Contains("Fighter", StringComparison.OrdinalIgnoreCase);
                Assert.Equal(expected[i].fighter, fighter);
                Assert.Equal(0, l.Sector.Id);           // the single shared arena sector
                Assert.Same(mission.Sectors[0], l.Sector);
                Console.WriteLine($"[P402-LIVE] peer {i}: side {l.Ship.SideId}, hull '{l.Ship.HullType.Name}' in sector '{l.Sector.Name}'.");
            }

            // The two differing sides (peer 0 = side 0, peer 1 = side 1) each
            // dock at their OWN generated home station in the shared sector.
            var stA = mission.Sectors[0].Stations.Single(st => st.SideId == 0);
            var stB = mission.Sectors[0].Stations.Single(st => st.SideId == 1);
            var live0 = server.Ships.Single(x => x.Ship.Id == bots[0].Assignments[0].Ship.ShipId);
            var live1 = server.Ships.Single(x => x.Ship.Id == bots[1].Assignments[0].Ship.ShipId);
            DockAt(server, live0.Ship, stA);
            DockAt(server, live1.Ship, stB);

            Assert.Equal(stA.StationId, live0.Ship.StationId!.Value);
            Assert.Equal(stB.StationId, live1.Ship.StationId!.Value);
            Console.WriteLine("P402-BRAWL-DEBUG-LIVE: sides 0 and 1 in the single shared generated sector "
                + $"'{live0.Sector.Name}' (id 0); side 0 docked at generated station {stA.StationId} '{stA.Name}', "
                + $"side 1 docked at generated station {stB.StationId} '{stB.Name}' — combat/mining immediately "
                + "possible, no warps; hull mix Scout/Scout/Fighter/Fighter intact.");
        }
        finally
        {
            foreach (var b in bots) b.Dispose();
        }
    }

    /// <summary>Death/respawn on the generated arena resolves via the GENERIC
    /// path: a dead player respawns at its own real generated home station, never
    /// a synthetic garrison (none exist here). Pins <c>FindFriendlyStation</c> /
    /// <c>FindDebugGarrisonStation</c>'s generated-map fallthrough, mirroring the
    /// P393 real-map proof which asserts the opposite (synthetic picked) there.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void GeneratedBrawlDebugMode_DeathRespawnsAtOwnGeneratedStation()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(BrawlDebugConfig());
        var mission = server.Missions.Single();
        mission.Stage = MissionStage.Started;

        short side0 = mission.Sides[0].Id;
        Assert.Equal((short)0, side0);

        using var bot = new FltBot { AutoSelectSideAfterLogon = false };
        bot.Connect(server.BoundPort, "p402_death");
        bot.SendLogonRequest(Request("p402_death"));
        int ok = PumpUntil(server, () => bot.Ack is { FValidated: true }, 4000);
        Assert.True(ok < 4000, "p402_death: never acked");
        bot.SendPositionReq(side0);
        ok = PumpUntil(server,
            () => bot.ShipAssignment is not null &&
                  server.Ships.Any(x => x.Ship.Id == bot.ShipAssignment!.Ship.ShipId && x.Ship.Sector is not null),
            4000);
        Assert.True(ok < 4000, "p402_death: never seated after explicit side 0");

        var ship = server.Ships.Single(x => x.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Ship;
        Assert.Equal(side0, ship.SideId);

        // Convert to the side's Lifepod hull so the death is a bKilled death
        // (native bLifepod → bKilled, FedSrv.CPP:11776-11780), not an eject.
        var lp = mission.Core.HullTypes[mission.Sides.First(s => s.Id == side0).Civilization.Lifepod];
        ship.ChangeHull(lp);
        bot.SendSuicide();

        int ticks = PumpUntil(server, () =>
            server.Ships.Any(x => x.Ship.Id == ship.Id && x.Ship.StationId is not null), 2000);
        Assert.True(ticks < 2000, "p402_death: suicide on lifepod hull never respawned at a station");
        var respawned = server.Ships.Single(x => x.Ship.Id == ship.Id).Ship;

        // Generic path: respawns at its OWN real generated home station in the
        // shared sector — never a synthetic garrison (there are none in a
        // generated mission).
        Assert.NotNull(respawned.StationId);
        Assert.NotEqual(OmegaDebugStationId, respawned.StationId);
        Assert.NotEqual(PlatoDebugStationId, respawned.StationId);
        Assert.Equal(0, respawned.Sector!.Id);          // the single shared arena sector
        var station = mission.Sectors.SelectMany(s => s.Stations).Single(st => st.StationId == respawned.StationId);
        Assert.Equal(side0, station.SideId);           // its own side's generated station
        Console.WriteLine($"[P402-LIVE] side-0 death on generated arena respawned at real generated station {respawned.StationId} '{station.Name}' in sector '{respawned.Sector.Name}' (generic path, not synthetic).");
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
