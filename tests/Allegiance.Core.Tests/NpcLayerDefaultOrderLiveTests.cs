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
/// P161 — the idle <see cref="Ship.PilotTypeLayer"/> default order (native
/// <c>PickDefaultOrder</c>'s <c>c_ptLayer</c> case,
/// <c>Igc/shipIGC.h:2417-2434</c>): an idle Layer NPC picks the
/// <b>least-targeted</b> reachable neutral asteroid
/// (<see cref="Ship.FindLeastTargetedAsteroidAnyCluster"/>) and travels to it
/// via the existing autopilot — no new command type, no deployable mechanic.
/// These live loopback tests prove it end to end:
/// <list type="bullet">
/// <item><b>Least-targeted wins</b> — two reachable neutral asteroids A and B
/// in the same sector; the first idle Layer autonomously claims A
/// (first-found, both uncontended), then a second idle Layer, faced with A
/// already claimed by the first Layer's <see cref="Ship.LayerAsteroidTarget"/>,
/// picks the <em>other</em> (less-targeted) rock B — and actually flies there
/// (position measurably closer / within range after ticking).</item>
/// <item><b>Miner/Layer separation</b> — a Miner NPC and a Layer NPC on the
/// same side each drive their own target slot
/// (<see cref="Ship.MiningAsteroidTarget"/> vs
/// <see cref="Ship.LayerAsteroidTarget"/>) without ever touching the other's,
/// so the two decision cycles never interfere.</item>
/// </list>
/// Harness mirrors P160/P159 (booted in-process server + one seated bot, no
/// Windows VM); the mission advances with the server's own deterministic
/// <see cref="Srv.TickSim"/> (one 0.05 s chunk each).
/// </summary>
[Collection("LiveNetwork")]
public sealed class NpcLayerDefaultOrderLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P161 Layer Default Order Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 551u, DwCookie = 0x551u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 44200000000000009ul, DrmHash = "p161layer",
    };

    private static int Pump(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    // Neutral asteroid (He3-capable so a Miner NPC would also accept it — the
    // Layer search deliberately ignores the He3 gate and accepts any asteroid).
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

    /// <summary>Boot the in-process server + seated bot and assemble the
    /// single-sector fixture: a real Layer drone type id from the core
    /// (PilotType==5, e.g. PCore001's '.TowerSC'/'.TowerAC'/'NovaBomb' —
    /// looked up dynamically so the test never hard-codes a core-version id),
    /// TWO well-separated neutral asteroids A and B (XXV_simple has zero map
    /// asteroids, so these are the only rocks anywhere reachable), and a parked
    /// peer player ship on the other side (bootstrap only). Disposes the server
    /// and the bot.</summary>
    private static (Srv Server, Mission Mission, Sector Sector, short LayerDroneId, Asteroid RockA, Asteroid RockB, short NpcSide)
        BootFixture(out FltBot bot)
    {
        var server = new Srv();
        Mission? mission = null;
        Sector? sector = null;
        short layerDroneId = 0;
        Asteroid? rockA = null;
        Asteroid? rockB = null;
        short npcSide = 0;

        server.ShipSpawner = (_, m, id) =>
        {
            mission = m;
            sector = m.Sectors[0];
            var core = m.Core;
            short peerSide = m.Sides[0].Id;
            npcSide = m.Sides.First(s => s.Id != peerSide).Id;   // the OTHER side — no players

            // A REAL Layer drone type straight from the core (PilotType==5).
            var lay = core.DroneTypes.First(kv => kv.Value.PilotType == Ship.PilotTypeLayer);
            layerDroneId = lay.Key;

            // Two neutral asteroids, well separated (~154 u apart centre-to-centre),
            // so arriving near B is unambiguous and neither ship starts beside one.
            rockA = new Asteroid(MkAsteroid(5111, new Vector3(600f, 415f, 458f), oreMax: 499f, radius: 60, sector.Id));
            m.PlaceAsteroid(rockA, sector);
            rockB = new Asteroid(MkAsteroid(5122, new Vector3(566f, 558f, 501f), oreMax: 498f, radius: 60, sector.Id));
            m.PlaceAsteroid(rockB, sector);

            var hull = core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            var peer = new Ship(id, hull, new Vector3(0f, 0f, 0f), Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            m.PlaceShip(peer, sector);
            return (peer, sector);
        };

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        bot = new FltBot();
        var b = bot;                       // local copy — `bot` is an out param, unusable in the lambda below
        b.Connect(server.BoundPort, "p161_layer");
        b.SendLogonRequest(Request("p161_layer"));
        Pump(server, () => mission is not null && b.ShipAssignment is not null, 4000);

        Assert.NotNull(mission); Assert.NotNull(sector); Assert.NotNull(rockA); Assert.NotNull(rockB);
        Assert.True(layerDroneId != 0, "core must expose a Layer pilot-type drone");
        return (server, mission!, sector!, layerDroneId, rockA!, rockB!, npcSide);
    }

    /// <summary>⭐ Primary leg: two same-side idle Layers in a sector with two
    /// neutral asteroids. The first autonomously claims A (both uncontended →
    /// first-found wins); the second, seeing A already claimed by the first
    /// Layer's <see cref="Ship.LayerAsteroidTarget"/>, picks the OTHER rock B
    /// (fewer contenders) — and actually flies to it (position measurably
    /// closer / within range after ticking). Proves least-targeted selection
    /// plus the real autonomous movement, end to end.</summary>
    [Fact]
    public void Second_idle_layer_picks_the_less_targeted_asteroid_and_arrives()
    {
        var (srv, mission, sector, layerDroneId, rockA, rockB, npcSide) = BootFixture(out var bot);
        using (srv)
        using (bot)
        {
            Vector3 ap = RockPos(rockA), bp = RockPos(rockB);

            // The CLAIMING Layer: far from both rocks (so it must travel), and
            // spawned FIRST (earliest in _models → earliest in the search's
            // first-found tie-break, and ticks before any later ship). With both
            // rocks uncontended it picks A (first-found).
            var spawnClaim = new Vector3(310f, 514f, 449f);
            Assert.True(Dist(spawnClaim, ap) > 200f && Dist(spawnClaim, bp) > 200f,
                "claiming layer must spawn well beyond arrival range of BOTH rocks");
            var claim = srv.SpawnNpcShip(mission, sector, npcSide, spawnClaim, layerDroneId);
            Assert.Equal(Ship.PilotTypeLayer, claim.PilotType);   // gate premise: really a Layer

            Pump(srv, () => claim.LayerAsteroidTarget is not null, 700);
            Assert.Same(rockA, claim.LayerAsteroidTarget);   // first Layer: both uncontended → first-found (A) wins
            Console.WriteLine($"P161-CLAIM: Layer {claim.Id} (drone {layerDroneId}) claimed asteroid A {rockA.Data.AsteroidId}");

            // The NEW idle Layer, spawned far from both rocks. Its decision now
            // sees A with one same-side contender (claim's LayerAsteroidTarget)
            // and B with none → it must pick B, the less-targeted rock.
            var spawnNew = new Vector3(358f, 519f, 496f);
            Assert.True(Dist(spawnNew, ap) > 200f && Dist(spawnNew, bp) > 200f,
                "new layer must spawn well beyond arrival range of BOTH rocks");
            var fresh = srv.SpawnNpcShip(mission, sector, npcSide, spawnNew, layerDroneId);
            Assert.Equal(Ship.PilotTypeLayer, fresh.PilotType);

            Pump(srv, () => fresh.LayerAsteroidTarget is not null, 700);
            Assert.Same(rockB, fresh.LayerAsteroidTarget);   // ★ the LESS-targeted rock, not the already-claimed A
            Assert.Null(fresh.MiningAsteroidTarget);         // a Layer never touches the miner's slot
            Assert.Same(rockA, claim.LayerAsteroidTarget);   // the claimer kept its own rock throughout
            Console.WriteLine($"P161-SELECT: new Layer {fresh.Id} chose the less-targeted asteroid B {rockB.Data.AsteroidId} "
                            + $"(A {rockA.Data.AsteroidId} had a contender, B had none)");

            // ── Phase 3: the new Layer actually ARRIVES at B ────────────────
            bool arrived = false;
            int tn = 0;
            while (tn < 25000 && !arrived)
            {
                srv.TickSim();
                if (Dist(fresh.Position, bp) < 170f) arrived = true;   // within arrival band of rock B (radius 60 + settle margin)
                tn++;
            }
            float d0 = Dist(spawnNew, bp);
            float dNow = Dist(fresh.Position, bp);
            Assert.True(arrived, $"new layer must arrive at B: dist-to-B {d0:F1} → {dNow:F1} after {tn} ticks (rock B at {bp})");
            Assert.True(dNow < d0 - 40f, $"must be measurably closer to B than spawn: {d0:F1} → {dNow:F1}");
            Assert.Same(rockB, fresh.LayerAsteroidTarget);   // still committed to B after arrival (no churn / no re-pick)
            Console.WriteLine($"P161-ARRIVE: Layer {fresh.Id} arrived at asteroid B {rockB.Data.AsteroidId}: "
                            + $"dist {d0:F1} → {dNow:F1} (rock B {bp})");
        }
    }

    /// <summary>⭐ Separation leg (small assertion, not a second class): a Miner
    /// NPC and a Layer NPC on the same side each populate ONLY their own target
    /// slot — <see cref="Ship.MiningAsteroidTarget"/> for the Miner,
    /// <see cref="Ship.LayerAsteroidTarget"/> for the Layer — never the other's,
    /// so the two decision cycles run independently without interfering (the
    /// Miner's arbitration and the Layer's least-targeted search count only
    /// their own slot's holders).</summary>
    [Fact]
    public void Miner_and_layer_on_the_same_side_never_interfere()
    {
        var (srv, mission, sector, layerDroneId, rockA, rockB, npcSide) = BootFixture(out var bot);
        using (srv)
        using (bot)
        {
            var miner = srv.SpawnNpcShip(mission, sector, npcSide, new Vector3(402f, 468f, 456f));   // no drone id → Miner (P160 fallback)
            var layer = srv.SpawnNpcShip(mission, sector, npcSide, new Vector3(404f, 508f, 462f), layerDroneId);
            Assert.Equal(Ship.PilotTypeMiner, miner.PilotType);
            Assert.Equal(Ship.PilotTypeLayer, layer.PilotType);

            // Both decide; each must land in its OWN slot.
            Pump(srv, () => miner.MiningAsteroidTarget is not null && layer.LayerAsteroidTarget is not null, 1500);

            Assert.NotNull(miner.MiningAsteroidTarget);   // the Miner's decision cycle ran …
            Assert.Null(miner.LayerAsteroidTarget);       // … and never wrote the Layer's slot
            Assert.NotNull(layer.LayerAsteroidTarget);    // the Layer's decision cycle ran …
            Assert.Null(layer.MiningAsteroidTarget);      // … and never wrote the Miner's slot
            Assert.True(miner.LayerAsteroidTarget is null && layer.MiningAsteroidTarget is null);
            Console.WriteLine($"P161-SEPARATE: Miner {miner.Id} → MiningAsteroidTarget {miner.MiningAsteroidTarget!.Data.AsteroidId}, "
                            + $"Layer {layer.Id} → LayerAsteroidTarget {layer.LayerAsteroidTarget!.Data.AsteroidId}; no cross-slot writes");
        }
    }
}
