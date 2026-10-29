using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>Live complement of P652's socket-free enemy-miner tests:
/// physically ready opposing miners retain their orders, clear drilling state,
/// and award no ore (Igc/shipIGC.cpp:2189-2203).</summary>
[Collection("LiveNetwork")]
public sealed class NpcCrossSideContentionLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

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
        FedsrvVer = LogonRequest.MsgVer, Time = 612u, DwCookie = uint.MaxValue,
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

        server.SpawnInitialMinersAtBoot = false;   // P382: pristine 3-NPC arena — no initial miner to perturb cross-side arbitration
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
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
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Enemy_ready_contender_blocks_everyone_without_clearing_orders_or_redirecting()
    {
        Require.Artwork();
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
            // P652: park all three inside their native readiness envelope.
            poor.Position = rp + new Vector3(rock.Radius + 0.5f * poor.HullType.Length + 20f, 0, 0);
            rich.Position = rp - new Vector3(rock.Radius + 0.5f * rich.HullType.Length + 20f, 0, 0);
            enemy.Position = rp + new Vector3(0, rock.Radius + 0.5f * enemy.HullType.Length + 20f, 0);
            poor.Velocity = rich.Velocity = enemy.Velocity = Vector3.Zero;
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

            // The native cross-side branch clears mining/wants state, keeping
            // the mine command intact (shipIGC.cpp:2189-2203).
            srv.TickSim();
            foreach (var s in new[] { poor, rich, enemy })
            {
                Assert.Same(rock, s.MiningAsteroidTarget);
                Assert.Null(s.MiningAsteroid);
                Assert.False(s.PlanTarget.HasValue,
                    $"cross-side eviction must not redirect {s.Id} to a station (same-side loser path would)");
            }
            Console.WriteLine("P652-LOCKOUT: ready enemies retain orders with no mining or station redirect");

            // Ready enemies keep the rock locked out across subsequent ticks.
            float sPoor = poor.Ore, sRich = rich.Ore, sEnemy = enemy.Ore;
            bool poorMined = false, richMined = false, enemyMined = false;
            bool enemyRedirected = false;
            int tn = 0;
            while (tn < 6000)
            {
                srv.TickSim();
                if (poor.Ore > sPoor + 0.5f) poorMined = true;
                if (rich.Ore > sRich + 0.5f) richMined = true;
                if (enemy.Ore > sEnemy + 0.5f) enemyMined = true;
                if (enemy.PlanTarget.HasValue) enemyRedirected = true;
                tn++;
            }
            Assert.False(poorMined, $"would-be same-side winner must NOT mine the contested rock under cross-side lockout: start {sPoor:0}, now {poor.Ore:0}");
            Assert.False(richMined, $"rich same-side contender must NOT mine the contested rock: start {sRich:0}, now {rich.Ore:0}");
            Assert.False(enemyMined, $"enemy contender must NOT mine the contested rock: start {sEnemy:0}, now {enemy.Ore:0}");
            Assert.False(enemyRedirected, "cross-side branch must never redirect the enemy to sell");
            Console.WriteLine($"P167-LOCKOUT: poor {poor.Id}(side {poor.SideId}) ore {sPoor:0}→{poor.Ore:F1}, rich {rich.Id}(side {rich.SideId}) ore {sRich:0}→{rich.Ore:F1}, " +
                              $"enemy {enemy.Id}(side {enemy.SideId}) ore {sEnemy:0}→{enemy.Ore:F1} — none mined the contested rock, enemy never redirected");
        }
    }
}
