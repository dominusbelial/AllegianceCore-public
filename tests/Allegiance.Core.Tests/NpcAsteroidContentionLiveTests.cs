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
/// P159 — same-side asteroid contention arbitration, live loopback proof that
/// when two idle <see cref="IsNpc"/> miners on the same
/// <see cref="Ship.SideId"/> pick the <em>identical</em> rock, the port's new
/// per-tick <see cref="Ship.ArbitrateAsteroidContention"/> step
/// (native <c>CshipIGC::PlotShipMove</c> "miners mine" block,
/// <c>Igc/shipIGC.cpp:2088-2204</c>) lets exactly one of them keep mining —
/// the one currently carrying the least <see cref="Ship.Ore"/> — and redirects
/// every other contender to go sell at the nearest friendly
/// <see cref="DataStationTypeIGC.SabmUnload"/> station (their
/// <see cref="Ship.MiningAsteroidTarget"/> cleared,
/// <see cref="Ship.PlanTarget"/> set to the unload), the exact same redirect a
/// full bag triggers. Real-time arbitration, not avoidance-at-selection-time:
/// both ships may still initially pick the same rock
/// (<see cref="Ship.DecideMiningOrSelling"/> has no search-time collision
/// avoidance); fairness is enforced continuously afterwards, and resolves in
/// the same tick the loser's decision runs (the step is per-tick,
/// shipIGC.cpp:2088-2091).
///
/// Two legs:
///   • Least-ore wins — two same-side idle NPCs (different starting ore) in a
///     sector with exactly ONE mineable rock: the poorer one keeps the rock and
///     mines (<see cref="Ship.Ore"/> rises), the richer one is cleared of the
///     rock and redirected to the friendly unload station (not to a second
///     asteroid — there is only one), and its <see cref="Ship.Ore"/> never
///     rises (it never mines).
///   • Tie-break — two same-side idle NPCs with EQUAL starting ore: the native's
///     strict <c>&lt;</c> least-ore replace (shipIGC.cpp:2121) keeps the FIRST
///     contender encountered in sector model order (spawn order here); matched
///     by the port, so the first-spawned keeps the rock and the second is
///     redirected.
///
/// Harness mirrors P156/P158 (booted in-process server + one seated bot, no
/// Windows VM); the mission advances with the server's own deterministic
/// <see cref="Srv.TickSim"/> (one 0.05 s chunk each). NPC spawn positions are
/// fixed points in the map's verified in-bounds region, asserted >140 u from the
/// rock (beyond mining range) and >250 u from the unload station (beyond docking
/// range), so the decision + arbitration, not spawn luck, resolves the contention.
/// </summary>
[Collection("LiveNetwork")]
public sealed class NpcAsteroidContentionLiveTests
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
            new Gm { Name = "P159 Contention Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 612u, DwCookie = 0x612u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 98100000000000006ul, DrmHash = "p159contention",
    };

    private static int Pump(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    // He3-capable rock (bit CapMineHe3, igc.h:761) — the same synthetic builder
    // MiningTests/MiningSellTests/P156 use, so ResolveMining treats it as a real pool.
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
    /// single-sector fixture: one unload-capable friendly station on the NPC
    /// side, exactly ONE He3 rock (XXV_simple has zero map asteroids, so this is
    /// the only mineable anywhere reachable), and a parked peer player ship on
    /// the other side (bootstrap only). Rock/station/peer sit at the P156-
    /// proven MinerDecision coordinates; NPC spawns are computed at runtime via
    /// <see cref="ClearPoint"/>. Disposes the server and the bot.</summary>
    private static (Srv Server, Mission Mission, Sector Sector, Asteroid Rock, DataStationIGC Unload, short NpcSide)
        BootFixture(out FltBot bot)
    {
        var server = new Srv();
        Mission? mission = null;
        Sector? sector = null;
        Asteroid? rock = null;
        DataStationIGC unload = default;
        short npcSide = 0;

        server.ShipSpawner = (_, m, id) =>
        {
            mission = m;
            sector = m.Sectors[0];
            var core = m.Core;
            short peerSide = m.Sides[0].Id;
            npcSide = m.Sides.First(s => s.Id != peerSide).Id;   // the OTHER side — no players

            // Friendly unload-capable station (real type from the core carrying
            // the SabmUnload bit) on the NPC's own side so a displaced miner can dock & sell.
            var unloadType = core.StationTypes.Values.First(t => (t.SabmCapabilities & DataStationTypeIGC.SabmUnload) != 0);
            unload = new DataStationIGC(
                Position: [600f, 880f, 470f], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
                Rotation: [0f, 0f, 0f, 1f], ClusterId: sector.Id, SideId: npcSide, StationId: 5941,
                StationTypeId: (short)unloadType.StationTypeId, BpHull: 1, BpShield: 1, Name: "SELL");
            m.PlaceStation(new Station(unload, unloadType), sector);

            // THE single He3 rock — the only mineable anywhere reachable. Placed
            // 380 u below the unload station so a ship parked at the rock (mining)
            // is well clear of the station's docking radius, and vice versa.
            rock = new Asteroid(MkAsteroid(5951, new Vector3(600f, 500f, 470f), oreMax: 3999f, radius: 60, sector.Id));
            m.PlaceAsteroid(rock, sector);

            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var peer = new Ship(id, hull, new Vector3(0f, 0f, 0f), Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            m.PlaceShip(peer, sector);
            return (peer, sector);
        };

        server.Boot(Config());
        bot = new FltBot();
        var b = bot;                       // local copy — `bot` is an out param, unusable in the lambda below
        b.Connect(server.BoundPort, "p159_contention");
        b.SendLogonRequest(Request("p159_contention"));
        Pump(server, () => mission is not null && b.ShipAssignment is not null, 4000);

        Assert.NotNull(mission); Assert.NotNull(sector); Assert.NotNull(rock);
        return (server, mission!, sector!, rock!, unload, npcSide);
    }

    /// <summary>⭐ Primary leg: two same-side idle NPCs with DIFFERENT starting
    /// ore target the single rock; the lower-ore one keeps mining while the
    /// higher-ore one is cleared of the rock and redirected to the friendly
    /// unload station (its <see cref="Ship.Ore"/> never rises — it never mines).
    /// Proves least-ore-wins plus redirect-to-sell (not to a second rock).</summary>
    [Fact]
    public void Two_same_side_idle_miners_lower_ore_keeps_rock_higher_ore_redirected_to_unload()
    {
        var (srv, mission, sector, rock, unload, npcSide) = BootFixture(out var bot);
        using (srv)
        using (bot)
        {
            Vector3 rp = RockPos(rock);
            Vector3 sp = StationPos(unload);
            // Two idle NPCs, both beyond mining range of the rock (mining
            // threshold ≈ rock radius 60 + hull + padding ≈ ≤115 u) so each MUST
            // travel to mine — proving the arbitration result, not spawn luck.
            // Fixed spawns, each verified far from BOTH the rock (>140 u) and the
            // unload station (>250 u) so neither ship starts in mining or docking
            // range of either destination.
            var spawnPoor = new Vector3(600f, 300f, 470f);
            var spawnRich = new Vector3(250f, 500f, 470f);
            Assert.True(Dist(spawnPoor, rp) > 140f && Dist(spawnRich, rp) > 140f,
                "both NPCs must spawn beyond mining range to prove the decision cycle + arbitration");
            Assert.True(Dist(spawnPoor, sp) > 250f && Dist(spawnRich, sp) > 250f,
                "both NPCs must spawn beyond docking range of the unload station");
            var poor = srv.SpawnNpcShip(mission, sector, npcSide, spawnPoor);
            var rich = srv.SpawnNpcShip(mission, sector, npcSide, spawnRich);
            poor.Ore = 10f;   // lower-ore → the WINNER (least ore keeps the rock)
            rich.Ore = 40f;   // higher-ore → the LOSER (displaced to sell)
            Assert.True(poor.IsNpc && rich.IsNpc);

            // Premise — the search-based decision alone has no contention
            // awareness: both idle, both below capacity, and both would pick the
            // SAME rock (there is only one mineable anywhere reachable).
            Assert.True(poor.Ore < Ship.MineCapacity && rich.Ore < Ship.MineCapacity);
            Assert.Same(rock, poor.FindNearestMinableAsteroidAnyCluster());
            Assert.Same(rock, rich.FindNearestMinableAsteroidAnyCluster());

            // Arbitration resolves in the same tick the loser decides (it is
            // per-tick, shipIGC.cpp:2088-2204), so we pump straight to the settled
            // state rather than expecting a stable both-holding-the-rock window.
            Pump(srv,
                () => poor.MiningAsteroidTarget is not null
                   && rich.MiningAsteroidTarget is null
                   && rich.PlanTarget is { } pt && pt.StationId == unload.StationId, 1200);

            Assert.Same(rock, poor.MiningAsteroidTarget);                       // the WINNER keeps the rock
            Assert.Null(rich.MiningAsteroidTarget);                             // the LOSER loses the rock
            Assert.True(rich.PlanTarget.HasValue, "displaced miner must be redirected to the unload station");
            Assert.Equal(unload.StationId, rich.PlanTarget.Value.StationId);    // … toward the UNLOAD station …
            Assert.Equal(OrderChange.CidGoto, rich.PlanCommandId);              // … the same Goto a full bag uses
            Console.WriteLine($"P159-CONTEND: poor ship {poor.Id} (ore {poor.Ore:0}) keeps rock {rock.Data.AsteroidId}; " +
                              $"rich ship {rich.Id} (ore {rich.Ore:0}) displaced → unload {unload.StationId} '{unload.Name}'");

            // ── Phase 3: the winner actually mines; the loser never does ──────
            float startRichOre = rich.Ore;
            float startPoorOre = poor.Ore;
            bool poorMined = false, poorNearRock = false, richGrew = false;
            int tn = 0;
            while (tn < 25000 && !(poorMined && richGrew))
            {
                srv.TickSim();
                if (poor.Ore > startPoorOre + 0.5f) poorMined = true;       // winner accrues ore
                if (Dist(poor.Position, rp) < 140f) poorNearRock = true;    // winner physically reaches the rock
                if (rich.Ore > startRichOre + 0.5f) richGrew = true;        // loser must never accrue ore
                tn++;
            }
            Assert.True(poorMined, $"the lower-ore winner should actually mine: start {startPoorOre:0}, now {poor.Ore:0}");
            Assert.False(richGrew, $"the displaced loser must never mine (ore must not grow): start {startRichOre:0}, now {rich.Ore:0}");
            Assert.True(poorNearRock, $"winner must have flown to the rock to mine: min-dist-to-rock not <140 observed");

            // The loser flew toward the unload station (distance shrank) unless
            // it already docked (then its ore went to 0 on the sell, also non-mining).
            float distB0 = Dist(spawnRich, sp);
            float distBNow = Dist(rich.Position, sp);
            Assert.True(distBNow < distB0 - 20f || rich.StationId is not null,
                $"loser must head toward the unload station: dist {distB0:F1}→{distBNow:F1}, docked={rich.StationId is not null}");
            Console.WriteLine($"P159-CONTEND-RESULT: winner {poor.Id} mined ore {poor.Ore:F1} (start {startPoorOre:0}); " +
                              $"loser {rich.Id} ore {rich.Ore:F1} (start {startRichOre:0}), dist to unload {distB0:F1}→{distBNow:F1}, " +
                              $"docked={rich.StationId is not null}");
        }
    }

    /// <summary>⭐ Tie-break leg: two same-side idle NPCs with EQUAL starting ore.
    /// Native's strict <c>&lt;</c> least-ore replace (shipIGC.cpp:2121) keeps the
    /// FIRST contender encountered in sector model order; the port matches, so
    /// the first-spawned (earlier in <c>_models</c> insertion order) keeps the
    /// rock and the second is redirected.</summary>
    [Fact]
    public void Equal_ore_two_same_side_idle_miners_first_spawned_keeps_rock_second_redirected()
    {
        var (srv, mission, sector, rock, unload, npcSide) = BootFixture(out var bot);
        using (srv)
        using (bot)
        {
            Vector3 rp = RockPos(rock);
            Vector3 sp = StationPos(unload);
            var spawnFirst = new Vector3(600f, 330f, 470f);
            var spawnSecond = new Vector3(310f, 500f, 470f);
            Assert.True(Dist(spawnFirst, rp) > 140f && Dist(spawnSecond, rp) > 140f);
            Assert.True(Dist(spawnFirst, sp) > 250f && Dist(spawnSecond, sp) > 250f);
            // Spawn order == _models insertion order == GetShipUpdateCandidates order.
            var first = srv.SpawnNpcShip(mission, sector, npcSide, spawnFirst);
            var second = srv.SpawnNpcShip(mission, sector, npcSide, spawnSecond);
            first.Ore = 25f; second.Ore = 25f;   // EQUAL ore → tie must be broken by encounter order
            Assert.True(first.IsNpc && second.IsNpc);
            Assert.Same(rock, first.FindNearestMinableAsteroidAnyCluster());
            Assert.Same(rock, second.FindNearestMinableAsteroidAnyCluster());

            // Arbitration: on the tie, the FIRST-enumerated (first-spawned) keeps the rock.
            Pump(srv,
                () => first.MiningAsteroidTarget is not null
                   && second.MiningAsteroidTarget is null
                   && second.PlanTarget is { } pt && pt.StationId == unload.StationId, 1200);
            Assert.Same(rock, first.MiningAsteroidTarget);                        // first keeps (tie-break: strict <, first-found)
            Assert.Null(second.MiningAsteroidTarget);                             // second loses
            Assert.True(second.PlanTarget.HasValue);
            Assert.Equal(unload.StationId, second.PlanTarget.Value.StationId);    // redirected to unload, not a rock
            Assert.Equal(OrderChange.CidGoto, second.PlanCommandId);
            Console.WriteLine($"P159-TIE: first-spawned {first.Id} keeps rock on the ore tie; " +
                              $"second-spawned {second.Id} displaced → unload {unload.StationId}");
        }
    }
}
