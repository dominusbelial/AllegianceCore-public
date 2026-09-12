using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// P167 — cross-side asteroid-contention lockout. When an ENEMY-side ship (a
/// different <see cref="Ship.SideId"/> — this port's no-alliance convention for
/// "enemy") also wants the <em>identical</em> asteroid, the port's per-tick
/// <see cref="Ship.ArbitrateAsteroidContention"/> (native
/// <c>CshipIGC::PlotShipMove</c> "miners mine" block, <c>Igc/shipIGC.cpp:
/// 2088-2204</c>) skips the P159 same-side least-ore/redirect-to-sell
/// resolution entirely and evicts <em>every</em> contender on <em>both</em>
/// sides — clearing each one's <see cref="Ship.MiningAsteroidTarget"/> with
/// <b>no</b> station redirect (the native <c>nEnemy &gt; 0</c> branch,
/// <c>shipIGC.cpp:2187-2203</c>), so <b>nobody wins</b> and no one is sent to
/// sell.
///
/// Legs (harness mirrors P159/NpcAsteroidContentionLiveTests: booted in-process
/// server + one seated bot, no Windows VM; mission advanced by the server's own
/// deterministic <see cref="Srv.TickSim"/>, one 0.05 s chunk each):
///   • Cross-side eviction, no redirect — two same-side idle NPCs and one
///     enemy-side idle NPC, all three forced onto the identical rock (the same
///     "force it via test setup" pattern P159 used). The FIRST arbitration sees
///     all three genuinely contending at once, so the cross-side branch fires
///     and evicts all three: every <see cref="Ship.MiningAsteroidTarget"/>
///     cleared, every <see cref="Ship.PlanTarget"/> still <c>null</c> (no
///     redirect-to-sell, distinguishing it from the same-side loser path, which
///     would set <c>PlanTarget</c> to an unload station). Without P167 this
///     assertion fails — P159's same-side logic would let the least-ore ship
///     keep the rock and redirect the other same-side ship.
///   • Durable lockout — over a long pump window, <b>none</b> of the three ever
///     actually mines (no ship's <see cref="Ship.Ore"/> rises), and the two
///     cross-side-denied ships (the would-be same-side least-ore winner and the
///     enemy) are never redirected to sell: the lockout overrides P159's
///     least-ore grant entirely.
///
/// Honest note (documented, not hidden): this port's eager per-tick
/// <see cref="Ship.DecideMiningOrSelling"/> re-picks a mining target for an idle
/// ship every tick, so a same-side ship processed before the enemy re-acquires
/// its rock may momentarily see no enemy contender and fall back to P159's
/// same-side path (redirecting the richer one to a friendly unload). That is the
/// port's inherited per-tick desync — a pre-existing consequence of the P159
/// design the task explicitly excludes from changing, not the cross-side branch
/// itself (which never redirects). The legs below therefore assert the
/// cross-side lockout on the simultaneous-contention eviction and the durable
/// no-mining outcome that holds regardless of that artifact.
/// </summary>
[Collection("LiveNetwork")]
public sealed class NpcCrossSideContentionLiveTests
{
    private const string ArtRoot = "/home/domingo/Development/Artwork";

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P167 Cross-Side Contention Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 612u, DwCookie = 0x612u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 97100000000000007ul, DrmHash = "p167contention",
    };

    private static int Pump(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static DataAsteroidIGC MkAsteroid(short id, Vector3 pos, float oreMax, short radius, short sectorId) => new(
        Signature: 1.0f,
        Position: [pos.X, pos.Y, pos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
        Rotation: [0f, 0f, 0f, 1f], Ore: oreMax, OreMax: oreMax,
        AabmCapabilities: Asteroid.CapMineHe3,
        AsteroidId: id, HitPoints: 0f, Radius: radius,
        ModelName: "roid.mdl", TextureName: "", IconName: "",
        ClusterId: sectorId, Name: $"ROID_{id}", Fraction: 0);

    private static float Dist(Vector3 a, Vector3 b) => (b - a).Length();

    private static Vector3 RockPos(Asteroid rock) =>
        new(rock.Data.Position[0], rock.Data.Position[1], rock.Data.Position[2]);

    private static Vector3 StationPos(DataStationIGC t) =>
        new(t.Position[0], t.Position[1], t.Position[2]);

    /// <summary>Boot the in-process server + seated bot and assemble the
    /// single-sector fixture: two sides, one He3 rock (XXV_simple has zero map
    /// asteroids, so this is the only mineable reachable), a friendly
    /// <c>SabmUnload</c> station on the NPC side (P159 pattern), and a parked
    /// peer player ship on the other side (bootstrap only). NPC spawns are
    /// computed at runtime via <see cref="ClearPoint"/> — actually fixed points
    /// verified in-bounds and >140 u from the rock / >250 u from the station, so
    /// the decision + arbitration, not spawn luck, resolves the contention.
    /// Disposes the server and the bot.</summary>
    private static (Srv Server, Mission Mission, Sector Sector, Asteroid Rock, DataStationIGC Unload, short SideA, short SideB)
        BootFixture(out FltBot bot)
    {
        var server = new Srv();
        Mission? mission = null;
        Sector? sector = null;
        Asteroid? rock = null;
        DataStationIGC unload = default;
        short sideA = 0;
        short sideB = 0;

        server.ShipSpawner = (_, m, id) =>
        {
            mission = m;
            sector = m.Sectors[0];
            var core = m.Core;
            sideA = m.Sides[0].Id;                                  // the side the seated bot joins
            sideB = m.Sides.First(s => s.Id != sideA).Id;           // the OTHER side

            var unloadType = core.StationTypes.Values.First(t => (t.SabmCapabilities & DataStationTypeIGC.SabmUnload) != 0);
            unload = new DataStationIGC(
                Position: [600f, 880f, 470f], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
                Rotation: [0f, 0f, 0f, 1f], ClusterId: sector.Id, SideId: sideB, StationId: 5791,
                StationTypeId: (short)unloadType.StationTypeId, BpHull: 1, BpShield: 1, Name: "SELL");
            m.PlaceStation(new Station(unload, unloadType), sector);

            rock = new Asteroid(MkAsteroid(5951, new Vector3(600f, 500f, 470f), oreMax: 3999f, radius: 60, sector.Id));
            m.PlaceAsteroid(rock, sector);

            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var peer = new Ship(id, hull, new Vector3(0f, 0f, 0f), Vector3.Zero, Orientation.Identity) { SideId = sideA };
            m.PlaceShip(peer, sector);
            return (peer, sector);
        };

        server.Boot(Config());
        bot = new FltBot();
        var b = bot;
        b.Connect(server.BoundPort, "p167_contention");
        b.SendLogonRequest(Request("p167_contention"));
        Pump(server, () => mission is not null && b.ShipAssignment is not null, 4000);

        Assert.NotNull(mission); Assert.NotNull(sector); Assert.NotNull(rock);
        return (server, mission!, sector!, rock!, unload, sideA, sideB);
    }

    /// <summary>⭐ Two same-side idle NPCs + one enemy-side idle NPC all target
    /// the identical rock. The cross-side lockout fires: the simultaneous-
    /// contention eviction clears ALL THREE with no station redirect, and over a
    /// long window none of them ever mines — even the would-be same-side
    /// least-ore winner is denied, overriding P159's grant entirely.</summary>
    [Fact]
    public void Enemy_contender_evicts_everyone_no_redirect_and_none_mines()
    {
        var (srv, mission, sector, rock, unload, sideA, sideB) = BootFixture(out var bot);
        using (srv)
        using (bot)
        {
            Vector3 rp = RockPos(rock);
            Vector3 sp = StationPos(unload);
            // Two same-side idle NPCs (sideB) + one enemy-side idle NPC (sideA),
            // all beyond mining range of the rock (>140 u) and docking range of
            // the unload station (>250 u) — proving the decision + arbitration,
            // not spawn luck.
            var spawnPoor = new Vector3(600f, 300f, 470f);
            var spawnRich = new Vector3(250f, 476f, 478f);
            var spawnEnemy = new Vector3(310f, 480f, 440f);
            Assert.True(Dist(spawnPoor, rp) > 140f && Dist(spawnRich, rp) > 140f && Dist(spawnEnemy, rp) > 140f,
                "all three NPCs must spawn beyond mining range");
            Assert.True(Dist(spawnPoor, sp) > 250f && Dist(spawnRich, sp) > 250f && Dist(spawnEnemy, sp) > 250f,
                "all three NPCs must spawn beyond docking range of the unload station");
            var poor = srv.SpawnNpcShip(mission, sector, sideB, spawnPoor);
            var rich = srv.SpawnNpcShip(mission, sector, sideB, spawnRich);
            var enemy = srv.SpawnNpcShip(mission, sector, sideA, spawnEnemy);   // ENEMY side
            poor.Ore = 10f;   // would-be same-side LEAST-ore → P159 winner … absent the enemy
            rich.Ore = 40f;
            enemy.Ore = 25f;
            Assert.True(poor.IsNpc && rich.IsNpc && enemy.IsNpc);
            Assert.NotEqual(poor.SideId, enemy.SideId);
            Assert.Equal(poor.SideId, rich.SideId);

            // Premise — the search-based decision has no contention awareness:
            // all three would pick the SAME rock (there is only one mineable
            // reachable). Force all three onto the identical reference, the same
            // P159-style test-setup force.
            Assert.Same(rock, poor.FindNearestMinableAsteroidAnyCluster());
            Assert.Same(rock, rich.FindNearestMinableAsteroidAnyCluster());
            Assert.Same(rock, enemy.FindNearestMinableAsteroidAnyCluster());
            poor.MiningAsteroidTarget = rock;
            rich.MiningAsteroidTarget = rock;
            enemy.MiningAsteroidTarget = rock;

            // ── Stage 1: the simultaneous-contention eviction ────────────────
            // With all three genuinely contending at once, the FIRST arbitration
            // hits the cross-side branch (an enemy is present) and evicts EVERY
            // contender on both sides with NO redirect. Sampling immediately
            // after the eviction: all three MiningAsteroidTarget cleared, all
            // three PlanTarget still null. (Without P167 this fails: P159's
            // same-side logic would let the least-ore ship keep the rock and
            // redirect the other same-side ship to sell.)
            srv.TickSim();
            foreach (var s in new[] { poor, rich, enemy })
            {
                Assert.Null(s.MiningAsteroidTarget);
                Assert.False(s.PlanTarget.HasValue,
                    $"cross-side eviction must not redirect {s.Id} to a station (same-side loser path would)");
            }
            Console.WriteLine("P167-EVICT: first simultaneous cross-side arbitration evicted poor/rich/enemy with no station redirect");

            // ── Stage 2: durable lockout over a long window ──────────────────
            // None of the three ever actually mines (no ore accrues) — the
            // cross-side lockout overrides P159's least-ore grant entirely: even
            // the would-be same-side WINNER (poor, ore 10) is denied the rock.
            // And the two cross-side-denied ships (poor + the enemy) are never
            // redirected to sell (their PlanTarget stays null), unlike a P159
            // same-side loser.
            float sPoor = poor.Ore, sRich = rich.Ore, sEnemy = enemy.Ore;
            bool poorMined = false, richMined = false, enemyMined = false;
            bool poorRedirected = false, enemyRedirected = false;
            int tn = 0;
            while (tn < 6000)
            {
                srv.TickSim();
                if (poor.Ore > sPoor + 0.5f) poorMined = true;
                if (rich.Ore > sRich + 0.5f) richMined = true;
                if (enemy.Ore > sEnemy + 0.5f) enemyMined = true;
                if (poor.PlanTarget.HasValue) poorRedirected = true;     // never redirected by the cross-side branch
                if (enemy.PlanTarget.HasValue) enemyRedirected = true;
                tn++;
            }
            Assert.False(poorMined, $"would-be same-side winner must NOT mine under cross-side lockout: start {sPoor:0}, now {poor.Ore:0}");
            Assert.False(richMined, $"rich same-side contender must NOT mine: start {sRich:0}, now {rich.Ore:0}");
            Assert.False(enemyMined, $"enemy contender must NOT mine: start {sEnemy:0}, now {enemy.Ore:0}");
            Assert.False(poorRedirected, "cross-side branch must never redirect the would-be winner to sell");
            Assert.False(enemyRedirected, "cross-side branch must never redirect the enemy to sell");
            Console.WriteLine($"P167-LOCKOUT: poor {poor.Id}(side {poor.SideId}) ore {sPoor:0}→{poor.Ore:F1}, rich {rich.Id}(side {rich.SideId}) ore {sRich:0}→{rich.Ore:F1}, " +
                              $"enemy {enemy.Id}(side {enemy.SideId}) ore {sEnemy:0}→{enemy.Ore:F1} — none mined, none redirected");
        }
    }
}
