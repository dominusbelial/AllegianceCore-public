using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
// 'Server'/'Bot' are also namespaces — alias to unambiguous identifiers (same
// pattern as PaydayTests / DockDockingTests).
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// The P55 deliverable — real mining at the sim and wire level. A ship parked
/// beside a minable asteroid accrues <see cref="Ship.Ore"/> per tick, draining
/// the asteroid's own depletable pool (<see cref="Asteroid.OreRemaining"/>),
/// pausing when either the ship's flat capacity (<see cref="Ship.MineCapacity"/>)
/// or the rock's pool gates the yield, and the ore is wire-visible as the
/// existing ship-fractions byte (<c>ServerShipUpdate</c> byte 45,
/// <c>OreFraction</c>) — the same "rides an existing message" pattern P53 used
/// for docking. Proven three ways, mirroring the P51/P53 suites: the codec
/// round-trips byte 45 (units), a <see cref="RecordingIgcSite"/> mission
/// establishes accrual/depletion/cap/pool + the <c>MineAsteroidEvent</c> seam
/// (units), and a real loopback TCP <see cref="FltBot"/> boot on the demo map
/// (which has <em>zero</em> asteroids — its own boot summary sums
/// <c>Sector.Asteroids</c> to 0) mines a <b>synthetically injected</b> rock and
/// proves the ore reached the wire over real TCP. No Windows VM, no native
/// client, no real-map dependence — the asteroid is constructed directly, like
/// this suite's other synthetic placements.
/// </summary>
[Collection("LiveNetwork")]
public class MiningTests
{
    private const string ArtRoot = "/home/domingo/Development/Artwork";

    // ── synthetic asteroid builder ─────────────────────────────────────────
    // DataAsteroidIGC is a fixed record; we build a He3-capable rock (bit 0x01,
    // igc.h:761) with a chosen position, ore pool and radius. Up/Forward/Rotation
    // are orientation noise the mining pass never reads (only Position, Radius,
    // Ore, OreMax, AabmCapabilities, AsteroidId, Name); harmless literals.
    private static DataAsteroidIGC MkAsteroid(short id, Vector3 pos, float oreMax, short radius, short sectorId)
    {
        float ore = oreMax;
        return new DataAsteroidIGC(
            Signature: 1.0f,
            Position: [pos.X, pos.Y, pos.Z],
            Up: [0f, 1f, 0f],
            Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f],
            Ore: ore,
            OreMax: oreMax,
            AabmCapabilities: Asteroid.CapMineHe3,   // He3-capable => mineable + regenerative
            AsteroidId: id,
            HitPoints: 0f,
            Radius: radius,
            ModelName: "roid.mdl",
            TextureName: "",
            IconName: "",
            ClusterId: sectorId,
            Name: $"ROID_{id}",
            Fraction: 0);
    }

    // ── representative static core + a real map, for the no-network mission ─
    private static readonly Lazy<StaticCore> RepresentativeCore = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            return StaticCore.Load(file);
        }
        throw new InvalidOperationException("no static core found in corpus");
    });

    private static readonly Lazy<MissionMap> SomeMap = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count == 0) continue;
            return map;
        }
        throw new InvalidOperationException("no non-empty map found in corpus");
    });

    /// <summary>A started mission whose Site is a recording harness.</summary>
    private static Mission NewStartedMission(IIgcSite site, int sides = 2)
    {
        var civs = RepresentativeCore.Value.Civilizations.Values
            .OrderBy(c => c.CivilizationId).Take(sides).ToList();
        IReadOnlyList<SideSpec> specs = [];
        for (int i = 0; i < civs.Count; i++)
            specs = specs.Append(new SideSpec((short)i, $"Team {i + 1}", civs[i].CivilizationId)).ToList();
        var m = Mission.Assemble(RepresentativeCore.Value, SomeMap.Value, specs, site);
        m.Stage = MissionStage.Started;
        return m;
    }

    /// <summary>Pick a sector + position for a synthetic asteroid that is far
    /// (> <paramref name="margin"/>) from every real He3-capable asteroid already
    /// in that sector, so the placed miner unambiguously mines OUR rock and the
    /// assertion "our pool depletes in lockstep with ship.Ore" cannot be skewed
    /// by a coincidental real rock. Tries each sector and a few directions;
    /// throws if the corpus map is too crowded (does not happen in practice).
    /// </summary>
    private static (Vector3 Spot, Sector Sector) CleanSpot(Mission m, float margin = 1200f)
    {
        var dirs = new[]
        {
            new Vector3(1, 0, 0), new Vector3(-1, 0, 0), new Vector3(0, 1, 0),
            new Vector3(0, -1, 0), new Vector3(0, 0, 1), new Vector3(0, 0, -1),
        };
        foreach (var sector in m.Sectors)
        {
            var he3 = sector.Asteroids
                .Where(a => (a.AabmCapabilities & Asteroid.CapMineHe3) != 0 && a.Position is { Length: 3 })
                .Select(a => new Vector3(a.Position[0], a.Position[1], a.Position[2]))
                .ToList();
            Vector3 anchor = he3.Count == 0 ? Vector3.Zero
                : new Vector3(he3.Average(p => p.X), he3.Average(p => p.Y), he3.Average(p => p.Z));
            foreach (var d in dirs)
                foreach (var dist in new[] { 2000f, 4000f, 6500f })
                {
                    var cand = anchor + d * dist;
                    if (he3.All(p => (p - cand).Length() > margin))
                        return (cand, sector);
                }
        }
        throw new InvalidOperationException("corpus map too crowded to place a clean synthetic asteroid");
    }

    private static void Pump(Mission m, int n, double step = 0.25)
    {
        for (int i = 0; i < n; i++) m.Update(m.LastUpdate + step);
    }

    private static int MineEvents(RecordingIgcSite site) =>
        site.Calls.Count(c => c.StartsWith("MineAsteroidEvent("));

    // ── the wire codec (byte 45 = ore fraction) ────────────────────────────
    /// <summary><c>ServerShipUpdate</c> byte 45 — the ore fraction — is filled
    /// from <see cref="Ship.Ore"/>/<see cref="Ship.MineCapacity"/> by
    /// <see cref="ServerShipUpdate.FromModel"/> and reads back through
    /// <see cref="ServerShipUpdate.OreFraction"/>. Zero for an idle ship, rises
    /// with cargo, saturates at the flat capacity.</summary>
    [Fact]
    public void FromModel_fills_byte45_ore_fraction_from_ShipOre()
    {
        var hull = RepresentativeCore.Value.HullTypes.Values.OrderBy(h => h.HullId).First();
        Ship ShipWithOre(float ore) => new(42, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { Ore = ore };

        Assert.Equal(0f, ServerShipUpdate.FromModel(42, ShipWithOre(0f), 7u).OreFraction);
        Assert.Equal(Ship.MineCapacity, 150f);   // the constant the fractions below assume
        float f075 = ServerShipUpdate.FromModel(42, ShipWithOre(Ship.MineCapacity * 0.75f), 7u).OreFraction;
        Assert.True(Math.Abs(f075 - 0.75f) < 0.005f, $"expected ~0.75, got {f075}");
        Assert.Equal(1f, ServerShipUpdate.FromModel(42, ShipWithOre(Ship.MineCapacity), 7u).OreFraction);   // saturated (clamped)
        Assert.Equal(1f, ServerShipUpdate.FromModel(42, ShipWithOre(Ship.MineCapacity * 2f), 7u).OreFraction); // over-cap clamps to 1
    }

    // ── mission-level accrual/depletion/cap/pool (RecordingIgcSite) ───────
    // Deterministic: mission stepped by Mission.TimeIncrement (0.25s), MiningRate
    // 100/s => 25 ore per Update (two 0.125s chunks), regardless of chunking.

    /// <summary>⭐ A ship parked on a large rock accrues Ore across multiple
    /// ticks (linear in dt), and the rock's own pool depletes correspondingly —
    /// the pool shrinks by exactly what the ship gained. The <c>MineAsteroidEvent</c>
    /// seam fires.</summary>
    [Fact]
    public void Near_rock_accrues_Ore_linearly_and_rock_pool_depletes_in_lockstep()
    {
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording);
        var (spot, sector) = CleanSpot(mission);
        var rock = new Asteroid(MkAsteroid(7001, spot, oreMax: 10000f, radius: 60, sectorId: sector.Id));
        mission.PlaceAsteroid(rock, sector);

        var hull = mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var ship = new Ship(9001, hull, spot, Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(ship, sector);

        // One Update = 0.25 s => 25 ore. Three Updates => 75 ore, no cap, pool still vast.
        Pump(mission, 3);
        Assert.True(Math.Abs(ship.Ore - 75f) < 0.1f, $"expected ~75 ore after 3 ticks, got {ship.Ore}");
        Assert.True(rock.OreRemaining < 10000f - 74f, "pool must have drained correspondingly");
        Assert.True(rock.OreRemaining > 10000f - 76f, $"pool drained too much: {rock.OreRemaining}");
        Assert.True(ship.IsMining, "ship must be flagged as mining");

        // The seam recorded at least one MineAsteroidEvent for our rock.
        Assert.True(MineEvents(recording) >= 1, "MineAsteroidEvent seam must fire during accrual");
        // And the recorded "newOre" fraction equals the rock's live ore fraction
        // (asteroidigc.h:249 transmits ore/oreMax).
        Assert.Contains(recording.Calls, c =>
        {
            var frac = RockFraction(c);
            return frac is not null && Math.Abs(frac.Value - rock.OreFraction) < 0.002f;
        });
    }

    private static float? RockFraction(string mineCall)
    {
        // Format "MineAsteroidEvent(Asteroid, <float>)"; the float is the fraction.
        int comma = mineCall.IndexOf(',');
        if (comma < 0 || !mineCall.EndsWith(")")) return null;
        var f = mineCall.Substring(comma + 1).TrimEnd(')').Trim();
        return float.TryParse(f, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>⭐ Mining stops (accrual halts) once the ship's capacity is hit:
    /// <c>Ore</c> reaches exactly <see cref="Ship.MineCapacity"/> and then holds
    /// flat (the pass skips <c>Ore &gt;= capacity</c> ships), and the seam stops
    /// firing — no continuous event stream once full.</summary>
    [Fact]
    public void Cap_hits_and_accrual_halts_until_drained()
    {
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording);
        var (spot, sector) = CleanSpot(mission);
        var rock = new Asteroid(MkAsteroid(7002, spot, oreMax: 10000f, radius: 60, sectorId: sector.Id));
        mission.PlaceAsteroid(rock, sector);

        var hull = mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var ship = new Ship(9002, hull, spot, Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(ship, sector);

        Pump(mission, 6);                                     // 6*25 = 150 = capacity exactly
        Assert.Equal(Ship.MineCapacity, ship.Ore);            // saturated at the flat cap
        int eventsAtCap = MineEvents(recording);
        Assert.True(eventsAtCap >= 1, "must have mined (and reported) on the way to cap");

        Pump(mission, 6);                                     // 6 more ticks
        Assert.Equal(Ship.MineCapacity, ship.Ore);            // unchanged: accrual halted at capacity
        Assert.Equal(eventsAtCap, MineEvents(recording));     // no further MineAsteroidEvent once full

        // The rock was drained by the accumulated ore (its pool gates nothing
        // here — it is huge — so ship.Ore accounts for the full draw, minus a
        // few units of regeneration topping the rock back up over 6 updates).
        Assert.True(Math.Abs((10000f - rock.OreRemaining) - Ship.MineCapacity) < 8f,
            $"net pool draw {10000 - rock.OreRemaining} should be ~150 (the ore mined)");
    }

    /// <summary>⭐ A small rock's pool gates the yield: the miner can never reach
    /// its 150 capacity because the rock runs dry; ore accrual collapses to the
    /// (tiny) regeneration trickle instead of the full 25/Update, and the rock
    /// sits near-empty. This is the "pool hits zero → mining limited" behaviour
    /// (native asteroids regenerate, asteroidigc.h:47-56, so it is held
    /// near-empty, not permanently destroyed).</summary>
    [Fact]
    public void Small_pool_gates_yield_and_accrual_collapses_to_regen_trickle()
    {
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording);
        var (spot, sector) = CleanSpot(mission);
        var rock = new Asteroid(MkAsteroid(7003, spot, oreMax: 80f, radius: 60, sectorId: sector.Id));  // pool << 150 capacity
        mission.PlaceAsteroid(rock, sector);

        var hull = mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var ship = new Ship(9003, hull, spot, Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(ship, sector);

        Pump(mission, 9);   // 9 * 25 = 225 demanded, but only ~80 is in the rock
        Assert.True(ship.Ore >= 60f && ship.Ore < 100f,
            $"pool-limited ore should be well below capacity (<= ~80 + regen), got {ship.Ore} (cap {Ship.MineCapacity})");
        Assert.True(rock.OreRemaining < 5f, $"rock should be held near-empty, got {rock.OreRemaining}");

        // Halting: the accrual between the 9th and 10th update is the regen
        // trickle (< 4), nowhere near the 25/Update an unconstrained miner gets.
        float before = ship.Ore;
        Pump(mission, 1);
        float grown = ship.Ore - before;
        Assert.True(grown >= 0f && grown < 4f, $"post-tap accrual should collapse to the regen trickle, got {grown}");
        Assert.True(ship.Ore < Ship.MineCapacity, "must never reach capacity on a small rock");
    }

    /// <summary>⭐ A ship nowhere near any asteroid is unaffected: it accrues no
    /// ore and never becomes a miner, over multiple ticks. This is the
    /// no-regression-to-normal-flight half of the verification.</summary>
    [Fact]
    public void Ship_far_from_any_asteroid_is_unaffected()
    {
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording);
        var (spot, sector) = CleanSpot(mission);
        // Place a rock far away; the ship sits elsewhere (also far from it and
        // from any real He3 rock), so mining must never trigger.
        var rock = new Asteroid(MkAsteroid(7004, spot + new Vector3(6000f, 0f, 0f), oreMax: 80f, radius: 60, sectorId: sector.Id));
        mission.PlaceAsteroid(rock, sector);

        var hull = mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var ship = new Ship(9004, hull, spot, Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(ship, sector);

        Pump(mission, 8);
        Assert.Equal(0f, ship.Ore);                       // no accrual
        Assert.False(ship.IsMining);                      // never became a miner
        Assert.Equal(0, MineEvents(recording));           // no seam emissions
    }

    // ── loopback wire (real TCP, demo map XXV_simple — zero asteroids) ────

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Mining Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 4242u, DwCookie = 0x42u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 42600000000000000ul, DrmHash = "miningtest",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        Thread.Sleep(60);
        return n;
    }

    /// <summary>⭐ End-to-end over real TCP: a bot's ship spawned atop a
    /// synthetic asteroid (demo map has zero rocks, so this is the only one in
    /// the universe) mines it — server-side <c>Ore</c> grows and the rock's pool
    /// shrinks — and the ore is <b>wire-visible</b>: once we nudge the parked
    /// ship's position a hair (forcing the dirty-position fmid-78 broadcast that
    /// a perfectly-stationary miner would otherwise suppress), the bot decodes a
    /// non-zero <c>OreFraction</c> matching <c>Ship.Ore/MineCapacity</c>.</summary>
    [Fact]
    public void Loopback_synthetic_rock_mines_and_ore_reaches_the_wire()
    {
        using var server = new Srv();
        Asteroid? placedRock = null;
        Vector3 apos = new(120f, 80f, -40f);
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var sector = mission.Sectors[0];
            placedRock = new Asteroid(MkAsteroid(7100, apos, oreMax: 500f, radius: 60, sectorId: sector.Id));
            mission.PlaceAsteroid(placedRock, sector);
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(shipId, hull, apos, Vector3.Zero, Orientation.Identity);
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };

        server.Boot(Config());
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "mine_probe");
        bot.SendLogonRequest(Request("mine_probe"));
        short shipId = bot.ShipAssignment is null
            ? (PumpUntil(server, () => bot.ShipAssignment is not null, 4000), bot.ShipAssignment!.Ship.ShipId).Item2
            : bot.ShipAssignment.Ship.ShipId;

        var live = () => server.Ships.First(s => s.Ship.Id == shipId).Ship;

        // Let it mine for a while (dt = 0.05 s => 5 ore/tick, 20 UPS).
        PumpUntil(server, () => live().Ore >= 20f, 400);
        Assert.True(live().Ore >= 20f, $"ship should have mined >=20 ore, got {live().Ore}");
        Assert.True(live().IsMining, "ship should be flagged mining");

        // The rock's pool depleted correspondingly (started at 500): ore pulled
        // from the rock equals the ship's carried ore (the demo map's rock is
        // the only one, so the whole draw is attributable), within regen drift.
        Assert.NotNull(placedRock);
        Assert.True(Math.Abs((500f - placedRock.OreRemaining) - live().Ore) < 10f,
            $"pool draw {500 - placedRock!.OreRemaining} should equal ship.Ore {live().Ore}");
        Assert.True(live().Ore < placedRock!.OreMax,
            $"mined amount must be within the rock's pool (got {live().Ore})");

        // Wire-visible ore: nudge the parked ship's POSITION (not velocity — the
        // mining gate's <1.0 velocity constraint is untouched) by > DirtyEpsilon
        // so BroadcastShipStates emits one dirty fmid-78 carrying current ore.
        bot.ResetObservations();
        live().Position = apos + new Vector3(2f, 0f, 0f);
        PumpUntil(server, () => bot.Observations.Any(o => o.OreFraction > 0f), 400);
        FltBot.ShipObservation? obs = null;
        foreach (var o in bot.Observations) if (o.OreFraction > 0f) obs = o;
        Assert.True(obs is not null, "wire must carry a nonzero ore fraction");
        float expectedFraction = Math.Clamp(live().Ore / Ship.MineCapacity, 0f, 1f);
        Assert.True(Math.Abs(obs!.Value.OreFraction - expectedFraction) < 0.02f,
            $"wire ore fraction {obs.Value.OreFraction} should track server Ore/MineCapacity {expectedFraction}");
    }

    /// <summary>⭐ No-regression over the wire: on the zero-asteroid demo map with
    /// the default spawn (no injection) a ship accrues no ore server-side, every
    /// decoded single-ship-update carries OreFraction 0, and it keeps broadcasting
    /// normal flying SHIP_STATUS(90)s (StationID NA, State Flying) — ordinary
    /// flight/status broadcasting is completely unaffected.</summary>
    [Fact]
    public void Loopback_default_spawn_is_unaffected_and_statuses_stay_normal()
    {
        using var server = new Srv();
        server.Boot(Config());
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "free_probe");
        bot.SendLogonRequest(Request("free_probe"));
        short shipId = bot.ShipAssignment is null
            ? (PumpUntil(server, () => bot.ShipAssignment is not null, 4000), bot.ShipAssignment!.Ship.ShipId).Item2
            : bot.ShipAssignment.Ship.ShipId;

        // Force the dirty broadcast the same way (and collect the join SET_CLUSTER
        // which also embeds an ore=0 ServerShipUpdate), then assert no ore anywhere.
        var live = server.Ships.First(s => s.Ship.Id == shipId).Ship;
        Assert.Equal(0f, live.Ore);                        // server-side: no accrual on a rock-less map
        live.Position = live.Position + new Vector3(2f, 0f, 0f);
        PumpUntil(server, () => bot.Observations.Count >= 2, 400);
        Assert.All(bot.Observations, o => Assert.Equal(0f, o.OreFraction));
        Assert.Equal(0f, live.Ore);

        // Normal SHIP_STATUS still flows (the periodic 40-tick refresh), flying/free.
        PumpUntil(server, () => bot.ShipStatuses.Count >= 1, 400);
        Assert.All(bot.ShipStatuses,
            s => { Assert.Equal(ShipStatusMessage.StateFlying, s.State); Assert.Equal(-1, s.StationId); });
        Assert.False(live.IsMining);
    }
}
