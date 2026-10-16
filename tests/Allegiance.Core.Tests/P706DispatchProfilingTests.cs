using System.Diagnostics;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Profiling;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>
/// P706 — dispatch-level profiling of the two mass-event ticks that dominate the
/// live rotation hitches. P694 cut detection from 1.3–1.8 s to &lt;100 ms; the
/// residue the live rig still shows (0.3–0.6 s every rotation, master
/// cf5821f6) is in EVENT DISPATCH:
///   * match start — ~105 ships per side relaunch together from their garrisons
///     into a dense, heterogeneous, mutually-overlapping swarm (collision.ship.dispatch);
///   * game over   — a garrison with ~100 nearby ships destroyed by the finale
///     bombers (combat.dispatch).
/// Each scenario builds a fresh world (P694-style), then advances the sector a
/// burst of ticks covering the moment the swarm is densest, and reports the WORST
/// (max) dispatch tick — the analog of the [slowtick] line the live rig logs every
/// rotation. Fresh worlds are warmed up and the guard is a median over five worlds
/// (P694b style), so one scheduler/GC outlier cannot fail a busy machine but a
/// sustained slow dispatch still must.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P706DispatchProfilingTests(ITestOutputHelper output)
{
#if DEBUG
    // The unoptimized Debug build pays for non-inlined vector access/arithmetic
    // in the native hull solver (the P694 comment) — at mass-relaunch density
    // the hull-separation path runs ~4–5× slower than Release (185 ms vs 41 ms
    // on this rig). Debug is only for suite stability; the real ≤50 ms perf
    // gate is enforced in Release. Mirrors P694's own 200 ms Debug budget.
    private const double DispatchBudgetMs = 200;
#else
    private const double DispatchBudgetMs = 50;    // the P706 target in Release
#endif

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData("matchStart", 165)]
    [InlineData("gameOver", 200)]
    public void Dispatch_budget(string scenario, int count)
    {
        Require.Artwork();
        try
        {
            var warmup = Stopwatch.StartNew();
            int warmupWorlds = 0;
            do
            {
                Measure(scenario, count);
                warmupWorlds++;
            }
            while (warmup.Elapsed < TimeSpan.FromSeconds(3) || warmupWorlds < 3);

            var samples = new double[5];
            for (int s = 0; s < samples.Length; s++)
                samples[s] = Measure(scenario, count);
            Array.Sort(samples);
            double median = samples[samples.Length / 2];
            output.WriteLine($"P706 [{scenario}] count={count} worst-dispatch median={median:F3}ms samples=[{string.Join(", ", samples.Select(v => $"{v:F3}"))}] budget={DispatchBudgetMs}ms");
            Assert.True(median < DispatchBudgetMs,
                $"P706 [{scenario}] worst-dispatch median={median:F3}ms >= budget={DispatchBudgetMs}ms (five fresh worlds)");
        }
        finally { ParallelSectorSim.Reset(); }
    }

    private double Measure(string scenario, int count)
    {
        var console = Console.Out;
        try
        {
            Console.SetOut(TextWriter.Null);
            return MeasureWorld(scenario, count);
        }
        finally { Console.SetOut(console); }
    }

    private static Vector3 RandomUnit(Random rng)
    {
        double u = rng.NextDouble() * 2.0 - 1.0;
        double phi = rng.NextDouble() * 2.0 * Math.PI;
        double r = Math.Sqrt(Math.Max(0.0, 1.0 - u * u));
        return new Vector3((float)(r * Math.Cos(phi)), (float)(r * Math.Sin(phi)), (float)u);
    }

    private double MeasureWorld(string scenario, int count)
    {
        using var server = new Srv();
        server.Boot(new Allegiance.Server.ServerConfig
        {
            ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0,
            Sim = new Allegiance.Server.SimConfig { ParallelSectorCells = true, ParallelSectorCellThreads = 4 },
            Games = [new Allegiance.Server.GameConfig
            { Name = "P706", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }]
        });
        var m = server.Missions[0];
        m.Stage = MissionStage.Started;
        m.CrowdingPenaltyEnabled = false;
        var type = m.Core.StationTypes.Values.First(t => t.Name.Contains("Garrison", StringComparison.OrdinalIgnoreCase));
        var fighter = m.Core.HullTypes.Values.Where(h => h.Name.Contains("Fighter", StringComparison.OrdinalIgnoreCase)
            && h.HasCapability(DataHullTypeIGC.HabmFighter) && !h.HasCapability(DataHullTypeIGC.HabmLifepod)
            && !h.HasCapability(DataHullTypeIGC.HabmBuilder) && !h.HasCapability(DataHullTypeIGC.HabmMiner))
            .OrderByDescending(h => h.Length).First();
        var bomber = m.Core.HullTypes.Values.Where(h => h.Name.Contains("Bomber", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(h => h.Length).First();

        var sectors = m.Sectors.Take(2).ToArray();
        var stations = new List<Station>();
        var ships = new List<Ship>();
        var rng = new Random(706);
        short id = 20000;
        int ticks = 40;
        foreach (var sector in sectors)
        {
            foreach (var st in sector.StationRuntimes.ToArray()) sector.RemoveStation(st);
            foreach (var ast in sector.Mineable.ToArray()) sector.RemoveAsteroid(ast);
            foreach (var model in sector.Models.ToArray()) sector.Remove(model);
            short side = (short)Array.IndexOf(sectors, sector);
            var station = new Station(DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
            {
                StationId = (short)(40000 + side), ClusterId = sector.Id, SideId = side,
                StationTypeId = type.StationTypeId, Position = [0, 0, 0], Forward = [0, 0, 1], Up = [0, 1, 0],
                BpHull = 1, BpShield = 0
            }, type);
            m.PlaceStation(station, sector);
            stations.Add(station);
            var hull = sector.CollisionHulls!.Resolve(type.ModelName)!;
            var launchBay = HullLandingBay.ReconstructStation(hull, type.Radius);
            var slot = launchBay.LaunchSlots[0];
            var direction = slot.Direction * station.Orientation;
            var centre = slot.Position * station.Orientation + direction * (fighter.Length * .5f + 50 * .85f + 5);
            // Heterogeneous, mutually-overlapping swarm at the launch mouth —
            // the analog of a full side relaunching at once: every ship has its
            // own heading/speed jitter so the ship-ship pass does real work
            // (identical-velocity launches would trip the equal-velocity gate).
            var fwd = station.Orientation.GetForward();
            // Stress-relaunch: every ship has a distinct random heading AND
            // random attitude (pilots turning as they emerge), packed tight at
            // the launch mouth so the first ticks overlap heavily. Random
            // attitudes defeat the pair-keyed separation cache, so this is the
            // worst case for dispatch (the aligned-emergence case is far
            // cheaper — see P706 report). This is the density that currently
            // busts the 50 ms dispatch budget and the bb-cache fix recovers.
            for (int i = 0; i < count; i++)
            {
                var vel = RandomUnit(rng) * (30f + (float)rng.NextDouble() * 80f);
                var orient = new Orientation();
                orient.Set(vel.Normalize(), fwd);
                var pos = centre + new Vector3((float)rng.NextDouble() * 6 - 3,
                    (float)rng.NextDouble() * 6 - 3, (float)rng.NextDouble() * 6 - 3);
                var ship = new Ship(id++, fighter, pos, vel, orient) { SideId = side };
                ship.Hull = sector.CollisionHulls.Resolve(ship.HullType.ModelName);
                m.PlaceShip(ship, sector);
                ships.Add(ship);
            }
            if (scenario == "gameOver")
            {
                var pt = m.Core.ProjectileTypes.Values.First(p => p.Power > 0) with
                { Power = 1, BlastPower = 0, Lifespan = 999, Radius = 1 };
                var mt = m.Core.MissileTypes.Values.First(t => t.HitPoints > 0) with
                { ReadyTime = 10, Lifespan = 999, BlastPower = 0 };
                for (int i = 0; i < count; i++)
                {
                    sector.Add(new Projectile(id++, sector, pt, centre, -direction * 80f, -direction, sector.LastUpdate, ships[^1]));
                    sector.Add(new Missile(id++, sector, mt, centre, direction * 80f, direction, sector.LastUpdate));
                }
                // The finale: a bomber volley whose blast destroys the garrison
                // AND ~all nearby ships at once — the mass combat.dispatch of
                // game over (each kill is a KillShipEvent + broadcast).
                var lethal = m.Core.ProjectileTypes.Values.First(p => p.Power > 0
                    && m.Core.Constants.DamageConstants[p.DamageType][type.DefenseTypeArmor] > 0
                    && m.Core.Constants.DamageConstants[p.DamageType][type.DefenseTypeShield] > 0) with
                { Power = 1e6f, BlastPower = 1e6f, BlastRadius = 520f, Radius = 1, Lifespan = 10 };
                sector.Add(new Projectile(id++, sector, lethal, new Vector3(0, 0, -4 * type.Radius),
                    new Vector3(0, 0, 50 * type.Radius), new Vector3(0, 0, 1), sector.LastUpdate, ships[^1]));
            }
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        string bucket = scenario == "matchStart" ? "collision.ship.dispatch" : "combat.dispatch";
        TickProfiler.Configure(true, 3600, null);
        double worst = 0;
        double worstSeparate = 0, worstEvent = 0;
        int maxEvents = 0, maxResolved = 0, maxDebounced = 0;
        double now = sectors[0].LastUpdate;
        for (int t = 0; t < ticks; t++)
        {
            now += .05;
            TickProfiler.EnterTick();
            foreach (var sector in sectors) sector.Update(now);
            TickProfiler.EndTick();
            var snap = TickProfiler.Snapshot();
            if (snap.TryGetValue(bucket, out var b) && b.MaxMs > worst) worst = b.MaxMs;
            if (snap.TryGetValue("collision.ship.separate", out var bs) && bs.MaxMs > worstSeparate) worstSeparate = bs.MaxMs;
            if (snap.TryGetValue("collision.ship.event", out var be) && be.MaxMs > worstEvent) worstEvent = be.MaxMs;
            int ev = sectors.Sum(s => s.LastShipShipEvents);
            int res = sectors.Sum(s => s.LastShipShipResolved);
            int deb = sectors.Sum(s => s.LastShipShipDebounced);
            if (ev > maxEvents) maxEvents = ev;
            if (res > maxResolved) maxResolved = res;
            if (deb > maxDebounced) maxDebounced = deb;
            TickProfiler.ForceReport();
        }
        TickProfiler.Shutdown();

        output.WriteLine($"P706 [{scenario}] count={count} worst {bucket} = {worst:F3}ms "
            + $"(separate={worstSeparate:F3}ms event={worstEvent:F3}ms maxEvents={maxEvents} resolved={maxResolved} debounced={maxDebounced}) over {ticks} ticks");
        return worst;
    }
}
