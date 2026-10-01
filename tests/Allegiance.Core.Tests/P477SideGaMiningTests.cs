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
/// P477 — the mining half of the side-global-attribute (GA) wiring: a side's
/// <see cref="GlobalAttributeSet"/> now scales He3 mining through the same
/// cheap <see cref="Ship.GlobalAttributeFactor"/> / <c>Effective…</c>
/// accessors P446/P467 introduced. Every native site is wired:
/// <list type="bullet">
/// <item><c>c_gaMiningRate</c> (17): ore accrued per tick
/// (<c>shipIGC.cpp:2212</c>: <c>dT · GA</c>) — <see cref="Ship.EffectiveMiningRate"/>.</item>
/// <item><c>c_gaMiningCapacity</c> (19): the miner's cargo-hold capacity —
/// the mining gate/clamp, the NPC mine-or-sell decision and the wire ore
/// fraction (<c>shipIGC.cpp:2166,2213</c>, <c>shipIGC.h:331,2249</c>) —
/// <see cref="Ship.EffectiveMineCapacity"/>.</item>
/// <item><c>c_gaMiningYield</c> (18): credits earned per unit sold at a
/// friendly dock (<c>shipIGC.cpp:1988</c>, <c>shipIGC.h:449</c>:
/// <c>m_fOre · value · GA</c>) — <see cref="Ship.EffectiveOreCreditPerUnit"/>.</item>
/// </list>
/// Every scaling assertion compares the GA-boosted outcome to the identity
/// (1.0) baseline so the multiplier is isolated.
/// </summary>
public class P477SideGaMiningTests
{
    private static readonly Lazy<StaticCore> Core = new(() =>
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

    private static readonly Lazy<MissionMap> Map = new(() =>
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

    /// <summary>Build a started single-side mission whose sole side carries the
    /// given GA factors (everything else identity 1.0 — a synthetic Gas isolating
    /// the multiplier under test, same recipe as P446/P467's <c>NewMission</c>).</summary>
    private static Mission NewMission(params (int idx, float val)[] factors)
    {
        var civ = Core.Value.Civilizations.Values.OrderBy(c => c.CivilizationId).First();
        var m = Mission.Assemble(Core.Value, Map.Value, [new SideSpec(0, "Team 1", civ.CivilizationId)], new NullIgcSite());
        m.Stage = MissionStage.Started;
        var arr = new float[GlobalAttributeSet.Count];
        Array.Fill(arr, 1f);
        foreach (var (idx, val) in factors) arr[idx] = val;
        m.Sides[0].GlobalAttributes = new GlobalAttributeSet(arr);
        return m;
    }

    private static DataAsteroidIGC MkAsteroid(short id, Vector3 pos, float oreMax, short radius, short sectorId)
    {
        return new DataAsteroidIGC(
            Signature: 1.0f, Position: [pos.X, pos.Y, pos.Z],
            Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f], Rotation: [0f, 0f, 0f, 1f],
            Ore: oreMax, OreMax: oreMax,
            AabmCapabilities: Asteroid.CapMineHe3,   // He3-capable => mineable + regenerative
            AsteroidId: id, HitPoints: 0f, Radius: radius,
            ModelName: "roid.mdl", TextureName: "", IconName: "",
            ClusterId: sectorId, Name: $"ROID_{id}", Fraction: 0);
    }

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

    /// <summary>Place a huge mineable rock + a ship ON the mission's sole side
    /// (so GA resolves) sitting exactly at the rock (velocity 0 → mines every
    /// tick). Returns the ship.</summary>
    private static (Ship Ship, Asteroid Rock) PlaceMiner(Mission m, Vector3 spot, Sector sector, float oreMax, int shipId)
    {
        var rock = new Asteroid(MkAsteroid((short)(7000 + shipId), spot, oreMax, 60, sector.Id));
        m.PlaceAsteroid(rock, sector);
        var hull = m.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var ship = new Ship((short)shipId, hull, spot, Vector3.Zero, Orientation.Identity)
        { Mission = m, SideId = m.Sides[0].Id };
        m.PlaceShip(ship, sector);
        return (ship, rock);
    }

    // ── index constants + accessor plumbing ────────────────────────────────

    /// <summary>⭐ Side-attribute indices 17/18/19 are the documented native
    /// constants (igc.h:611-613), and a sideless ship reads identity (1.0) for
    /// all three, so the whole mining half is a no-op for the default
    /// (no-side) case.</summary>
    [Fact]
    public void Indices_and_accessors_match_native_and_sideless_is_identity()
    {
        Assert.Equal(17, GlobalAttributeSet.MiningRate);
        Assert.Equal(18, GlobalAttributeSet.MiningYield);
        Assert.Equal(19, GlobalAttributeSet.MiningCapacity);

        var m = NewMission();
        var (spot, sector) = CleanSpot(m);
        var hull = m.Core.HullTypes.Values.OrderBy(h => h.HullId).First();

        // Sideless: identity capacity / rate / yield (150, 100, 5).
        var sideless = new Ship(9100, hull, spot, Vector3.Zero, Orientation.Identity) { Mission = m };
        Assert.Equal(Ship.MineCapacity, sideless.EffectiveMineCapacity(), 5);
        Assert.Equal(Sector.MiningRate, sideless.EffectiveMiningRate(), 5);
        Assert.Equal(Ship.OreCreditPerUnit, sideless.EffectiveOreCreditPerUnit(), 5);
        Assert.Equal(1f, sideless.GlobalAttributeFactor(GlobalAttributeSet.MiningRate), 6);
        Assert.Equal(1f, sideless.GlobalAttributeFactor(GlobalAttributeSet.MiningYield), 6);
        Assert.Equal(1f, sideless.GlobalAttributeFactor(GlobalAttributeSet.MiningCapacity), 6);

        // On the mission's side: factors resolve from the side's set.
        var ga = NewMission((GlobalAttributeSet.MiningRate, 2f), (GlobalAttributeSet.MiningYield, 1.5f),
                            (GlobalAttributeSet.MiningCapacity, 3f));
        var gs = new Ship(9101, hull, spot, Vector3.Zero, Orientation.Identity) { Mission = ga, SideId = ga.Sides[0].Id };
        Assert.Equal(2f, gs.EffectiveMiningRate() / Sector.MiningRate, 5);
        Assert.Equal(1.5f, gs.EffectiveOreCreditPerUnit() / Ship.OreCreditPerUnit, 5);
        Assert.Equal(3f, gs.EffectiveMineCapacity() / Ship.MineCapacity, 5);
    }

    // ── c_gaMiningRate: ore accrued per tick ───────────────────────────────

    /// <summary>⭐ A <c>c_gaMiningRate</c> of 2.0 doubles the ore accrued per
    /// tick (shipIGC.cpp:2212 <c>dT · GA</c>): two updates yield 2× the
    /// identity baseline, far below any capacity cap.</summary>
    [Fact]
    public void MiningRate_GA_scales_ore_accrued_per_tick()
    {
        var mId = NewMission();
        var (s1, sec1) = CleanSpot(mId);
        var (shipId, _) = PlaceMiner(mId, s1, sec1, oreMax: 100000f, shipId: 9201);

        var mGa = NewMission((GlobalAttributeSet.MiningRate, 2f));
        var (s2, sec2) = CleanSpot(mGa);
        var (shipGa, _) = PlaceMiner(mGa, s2, sec2, oreMax: 100000f, shipId: 9301);

        // dt=0.25 => 25 ore/tick identity, 50 ore/tick with rate 2.0. 2 ticks:
        // 50 vs 100 — neither near the 150 cap.
        Pump(mId, 2);
        Pump(mGa, 2);
        Assert.True(Math.Abs(shipId.Ore - 50f) < 0.2f, $"identity ore should be ~50, got {shipId.Ore}");
        Assert.True(Math.Abs(shipGa.Ore - 100f) < 0.2f, $"rate-2.0 ore should be ~100, got {shipGa.Ore}");
        float ratio = shipGa.Ore / shipId.Ore;
        Assert.InRange(ratio, 1.95f, 2.05f);
        Console.WriteLine($"P477-RATE: 2 ticks ore identity={shipId.Ore:F1}, rate2.0={shipGa.Ore:F1}, ratio {ratio:F3} (expect ~2)");
    }

    // ── c_gaMiningCapacity: the cargo-hold capacity clamp ──────────────────

    /// <summary>⭐ A <c>c_gaMiningCapacity</c> of 2.0 doubles the ore a miner's
    /// hold can carry: it saturates at 2×150 = 300 (not the flat 150) and then
    /// holds flat — the native <c>GetOreCapacity() = c_fcidCapacityHe3 ·
    /// GA[c_gaMiningCapacity]</c> clamp (<c>shipIGC.cpp:2213</c>).</summary>
    [Fact]
    public void MiningCapacity_GA_scales_the_capacity_clamp()
    {
        var mId = NewMission();
        var (s1, sec1) = CleanSpot(mId);
        var (shipId, _) = PlaceMiner(mId, s1, sec1, oreMax: 100000f, shipId: 9210);

        var mGa = NewMission((GlobalAttributeSet.MiningCapacity, 2f));
        var (s2, sec2) = CleanSpot(mGa);
        var (shipGa, _) = PlaceMiner(mGa, s2, sec2, oreMax: 100000f, shipId: 9310);

        // 25 ore/tick. Identity fills to 150 at tick 6; rate-unaffected, GA-2.0
        // capacity fills to 300 at tick 12.
        Pump(mId, 8);
        Pump(mGa, 8);
        Assert.Equal(Ship.MineCapacity, shipId.Ore);          // identity: capped at the flat 150
        Assert.True(Math.Abs(shipGa.Ore - 200f) < 0.2f, $"GA-2.0 capacity should hold ~200 after 8 ticks, got {shipGa.Ore}");
        Assert.True(shipGa.Ore > Ship.MineCapacity, "GA-2.0 capacity must exceed the flat 150");

        // Now drive the GA ship to ITS (doubled) cap and confirm it holds.
        Pump(mGa, 6);   // 12 total ticks => 300 = 2×150 exactly
        Assert.True(Math.Abs(shipGa.Ore - 300f) < 0.2f, $"GA-2.0 should cap at 300, got {shipGa.Ore}");
        Pump(mGa, 6);   // hold flat
        Assert.True(Math.Abs(shipGa.Ore - 300f) < 0.2f, $"GA-2.0 ore must hold at 300, got {shipGa.Ore}");
        Console.WriteLine($"P477-CAP: identity capped at {shipId.Ore:F0}; capacity2.0 capped at {shipGa.Ore:F0} (expect 300 = 2×150)");
    }

    // ── c_gaMiningYield: credits per unit sold ──────────────────────────────

    /// <summary>⭐ <c>EffectiveOreCreditPerUnit</c> — the dock-sell rate the port
    /// of <c>m_fOre · value · GA[c_gaMiningYield]</c> uses
    /// (<c>shipIGC.cpp:1988</c>) — scales the flat 5/u base by the side's yield
    /// factor: 1.5 → 7.5, identity → 5.</summary>
    [Fact]
    public void MiningYield_GA_scales_credits_per_unit()
    {
        var m = NewMission((GlobalAttributeSet.MiningYield, 1.5f));
        var (spot, sector) = CleanSpot(m);
        var hull = m.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var gaShip = new Ship(9220, hull, spot, Vector3.Zero, Orientation.Identity) { Mission = m, SideId = m.Sides[0].Id };
        Assert.Equal(Ship.OreCreditPerUnit * 1.5f, gaShip.EffectiveOreCreditPerUnit(), 5);

        // End to end at the value level: 100 ore sold pays 100 × 7.5.
        Assert.Equal(750f, gaShip.EffectiveOreCreditPerUnit() * 100f, 3);
        Console.WriteLine($"P477-YIELD: effective per-unit = {gaShip.EffectiveOreCreditPerUnit():F2} (base 5 × GA1.5) — 100 ore pays {gaShip.EffectiveOreCreditPerUnit() * 100f:F0}");
    }
}

/// <summary>
/// P477 — end-to-end, LIVE proof that completing a REAL development whose GA
/// raises a mining attribute changes real mining: completing dev 145
/// ('GA: Faster He3 1', gas[17]=1.1 — a real PCore001 development that raises
/// <c>c_gaMiningRate</c>) on the miner's side scales the ore a real miner
/// accrues per tick by ~1.1×, over the real sim/flow.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P477E2eMiningRateDevLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int ups = 8) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = ups,
        Games =
        [
            new Gm { Name = "P477 GA Mining E2E", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 919u, DwCookie = 0x919u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8810000000000000000ul, DrmHash = "p477ga",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static void StepMany(Srv server, int n)
    {
        for (int i = 0; i < n; i++) server.TickSim();
    }

    /// <summary>⭐ Complete real dev 145 ('GA: Faster He3 1', gas[17]=1.1) on the
    /// miner's side (civ 1 Dreghklar, base rate 0.65) through the real funding/
    /// completion pipeline, then show a real miner's per-tick ore accrual rises
    /// by ~1.1× — the economic driver that makes mining research worth buying.</summary>
    [Fact]
    public void Completing_dev_145_raises_real_mining_accrual()
    {
        using var server = new Srv();
        Asteroid? rock = null;
        Vector3 apos = new(140f, 60f, -30f);
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var sector = mission.Sectors[0];
            var data = new DataAsteroidIGC(
                Signature: 1.0f, Position: [apos.X, apos.Y, apos.Z],
                Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f], Rotation: [0f, 0f, 0f, 1f],
                Ore: 1_000_000f, OreMax: 1_000_000f,
                AabmCapabilities: Asteroid.CapMineHe3, AsteroidId: 8701,
                HitPoints: 0f, Radius: 60, ModelName: "roid.mdl", TextureName: "",
                IconName: "", ClusterId: sector.Id, Name: "E2E_ROID", Fraction: 0);
            rock = new Asteroid(data);
            mission.PlaceAsteroid(rock, sector);
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(shipId, hull, apos, Vector3.Zero, Orientation.Identity);
            ship.SideId = mission.Sides[0].Id;   // civ 1 Dreghklar (Alpha)
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // bypass the ready-up lobby (P180)
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "ga_miner");
        bot.SendLogonRequest(Request("ga_miner"));
        int seated = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(seated < 4000, "miner bot did not seat");

        var miner = server.Ships.First(s => s.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Ship;
        var mission = miner.Mission!;
        var side = mission.Sides.Single(s => s.Id == miner.SideId!.Value);
        Assert.NotNull(rock);
        // Confirm the miner is parked on the rock and accruing (velocity 0).
        Assert.True(miner.Velocity.LengthSquared() < 1f, "miner must be parked on the rock");
        float baseRateFactor = side.GlobalAttributes.Get(GlobalAttributeSet.MiningRate);
        Assert.InRange(baseRateFactor, 0.60f, 0.70f);   // Dreghklar base 0.65

        // ── baseline window: N ticks of real accrual (per-tick = rate·dt·GA) ──
        const int N = 12;
        miner.Ore = 0f;
        StepMany(server, N);
        float orePre = miner.Ore;
        Assert.True(orePre > 10f, $"baseline miner must accrue ore, got {orePre:F1}");

        // ── complete dev 145 (gas[17] = 1.1) on the miner's side ──
        var dev = mission.Core.Developments[145];
        Assert.Equal("GA: Faster He3 1", dev.Name);
        Assert.InRange(dev.Gas[17], 1.099f, 1.101f);   // the +10% mining rate
        var bucket = side.Buckets.Single(x => x.ItemType == BucketItemType.BitDevelopment
                                              && x.Development is { } dd && dd.DevelopmentId == dev.DevelopmentId);
        server.Ships.First(s => s.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Wallet.Money = 10_000_000;
        bot.ResetDevCompleteds(); bot.ResetSideAttributeChanges();
        bot.SendDonate(bucket.Id, bucket.Price);
        int done = PumpUntil(server, () => server.BucketCompletions.Contains(bucket.Id), 8000);
        Assert.True(done < 8000, $"dev 145 did not complete (invested {bucket.InvestedMoney}/{bucket.Price})");
        float rateFactorAfter = side.GlobalAttributes.Get(GlobalAttributeSet.MiningRate);
        Assert.InRange(rateFactorAfter / baseRateFactor, 1.095f, 1.105f);   // × 1.1 dev on top of the civ base

        // ── post-dev window: same N ticks → accrual rises ~1.1× ──
        miner.Ore = 0f;
        StepMany(server, N);
        float orePost = miner.Ore;
        Assert.True(orePost > orePre, $"post-dev accrual must exceed pre-dev, got {orePost:F1} vs {orePre:F1}");
        float ratio = orePost / orePre;
        Assert.InRange(ratio, 1.05f, 1.18f);
        Console.WriteLine($"P477-E2E: dev 145 '{dev.Name}' (gas[17] {dev.Gas[17]:F3}) complete on side {side.Id}; {N}-tick ore {orePre:F1} -> {orePost:F1}, ratio {ratio:F3} (expect ~1.1; civ base rate {baseRateFactor:F3})");
    }
}
