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

[Collection("LiveNetwork")]
[Trait("Tier", "Perf")]
public sealed class P694MassEventTickTests(ITestOutputHelper output)
{
#if DEBUG
    // The unoptimized build pays for non-inlined vector access/arithmetic in
    // the native hull solver. Its sustained regression guard is 2x Release's;
    // production performance must also be verified with -c Release. This is
    // compiler headroom, not an alternate density or simulation workload.
    private const double MedianBudgetMs = 200;
#else
    private const double MedianBudgetMs = 100;
#endif
    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(195, false, false, false)]
    [InlineData(195, true, false, false)]
    [InlineData(195, false, true, false)]
    [InlineData(195, true, true, false)]
    [InlineData(195, false, false, true)]
    [InlineData(195, true, false, true)]
    [InlineData(195, false, true, true)]
    [InlineData(195, true, true, true)]
    [InlineData(150, false, true, false)]
    [InlineData(150, true, true, false)]
    [InlineData(200, false, false, true)]
    [InlineData(200, true, false, true)]
    [InlineData(200, false, true, true)]
    [InlineData(200, true, true, true)]
    public void Launch_point_mass_events(int count, bool stationDeath, bool coincident, bool nativeLaunch)
    {
        Require.Artwork();
        // Station death / next-match launches occur in an already running
            // server. Exercise the numerical helpers long enough for the default
            // tiered JIT to finish background promotion, then time fresh worlds.
            // A single-row invocation also has a cold core importer and JIT
        // queue. One second mostly spent booting fixtures left numerical
        // helpers cold, although the whole theory matrix looked warm.
        // Use a fixed preparation interval and minimum world count, never
        // an observed "fast enough" tick or a failure-triggered retry.
        var warmup = Stopwatch.StartNew();
        int warmupWorlds = 0;
        do
        {
            Run(count, stationDeath, coincident, nativeLaunch, false);
            warmupWorlds++;
        }
        while (warmup.Elapsed < TimeSpan.FromSeconds(5) || warmupWorlds < 10);
        // Five independent worlds: a scheduler/GC outlier must not fail a
        // busy developer machine, but sustained slow ticks still must.
        // The configuration's budget covers BOTH sector updates together,
        // including callbacks and all in-tick allocation/GC. This is not a
        // per-sector budget and no samples are discarded or retried.
        var samples = new double[5];
        for (int sample = 0; sample < samples.Length; sample++)
            samples[sample] = Run(count, stationDeath, coincident, nativeLaunch, true);
        Array.Sort(samples);
        double median = samples[samples.Length / 2];
        output.WriteLine($"P694 median={median:F3}ms samples=[{string.Join(", ", samples.Select(s => $"{s:F3}"))}] budget={MedianBudgetMs}ms");
        Assert.True(median < MedianBudgetMs,
            $"median tick={median:F3}ms (five fresh worlds, two sectors; budget={MedianBudgetMs}ms)");
    }

    // P788c: reuse the exact P694 collision/mass-launch fixtures but measure the
    // complete server pass, with one seeded viewer for EVERY friendly ship.
    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void P788c_full_tick_broadcast_mass_events(bool nativeOrder, bool stationDeath, bool coincident)
    {
        Require.Artwork();
        var warmup = Stopwatch.StartNew();
        int worlds = 0;
        do { Run(195, stationDeath, coincident, !coincident, false, true, nativeOrder); worlds++; }
        while (warmup.Elapsed.TotalSeconds < 5 || worlds < 10);
        var samples = new double[12];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = Run(195, stationDeath, coincident, !coincident, true, true, nativeOrder);
        Array.Sort(samples);
        double p50 = samples[samples.Length / 2], p95 = samples[(int)Math.Ceiling(samples.Length * .95) - 1];
        output.WriteLine($"P788c nativeOrder={nativeOrder} death={stationDeath} coincident={coincident} n={samples.Length} p50={p50:F3} p95={p95:F3} worst={samples[^1]:F3}ms samples=[{string.Join(",", samples.Select(v => v.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)))}]");
        // The original 100 ms limit applies to sector simulation alone. This
        // diagnostic includes the broadcast projection and publishes its paired
        // flag timings; the rig compares full tick p95/worst across matches.
    }

    private double Run(int count, bool stationDeath, bool coincident, bool nativeLaunch, bool measured, bool fullTick = false, bool nativeOrder = false)
    {
        var console = Console.Out;
        try
        {
            // Boot and warmup are outside the measurement. Do not queue their
            // many megabytes of console output immediately ahead of timed work.
            Console.SetOut(TextWriter.Null);
            return RunWorld(count, stationDeath, coincident, nativeLaunch, measured, console, fullTick, nativeOrder);
        }
        finally { Console.SetOut(console); }
    }

    private double RunWorld(int count, bool stationDeath, bool coincident, bool nativeLaunch, bool measured, TextWriter console, bool fullTick, bool nativeOrder)
    {
        using var server = new Srv();
        server.Boot(new Allegiance.Server.ServerConfig
        {
            ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0,
            Broadcast = fullTick ? new Allegiance.Server.BroadcastConfig
            { ShipUpdateModel = "native", ImmutableSnapshot = true, EncodeOnce = true, ParallelBuild = true } : null,
            Sim = new Allegiance.Server.SimConfig { NativeBroadcastOrder = nativeOrder },
            Games = [new Allegiance.Server.GameConfig
            { Name = "P694", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }]
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
        var rng = new Random(694);
        short id = 10000;
        foreach (var sector in sectors)
        {
            foreach (var st in sector.StationRuntimes.ToArray()) sector.RemoveStation(st);
            foreach (var ast in sector.Mineable.ToArray()) sector.RemoveAsteroid(ast);
            foreach (var model in sector.Models.ToArray()) sector.Remove(model);
            short side = (short)Array.IndexOf(sectors, sector);
            var station = new Station(DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
            {
                StationId = (short)(30000 + side), ClusterId = sector.Id, SideId = side,
                StationTypeId = type.StationTypeId, Position = [0, 0, 0], Forward = [0, 0, 1], Up = [0, 1, 0],
                BpHull = 1, BpShield = 0
            }, type);
            m.PlaceStation(station, sector);
            stations.Add(station);
            var hull = sector.CollisionHulls!.Resolve(type.ModelName)!;
            var launchBay = HullLandingBay.ReconstructStation(hull, type.Radius);
            Assert.NotEmpty(launchBay.LaunchSlots);
            var slot = launchBay.LaunchSlots[0];
            var direction = slot.Direction * station.Orientation;
            var centre = slot.Position * station.Orientation + direction * (fighter.Length * .5f + 50 * .85f + 5);
            var velocity = direction * 50;
            if (measured) output.WriteLine($"launch={centre}, velocity={velocity}, hull={fighter.ModelName}, station={type.ModelName}");
            for (int i = 0; i < count + (stationDeath ? 12 : 0); i++)
            {
                bool enemy = i >= count;
                var offset = coincident ? Vector3.Zero : new Vector3((float)rng.NextDouble() * 10 - 5,
                    (float)rng.NextDouble() * 10 - 5, (float)rng.NextDouble() * 10 - 5);
                var orientation = new Orientation();
                orientation.Set(enemy ? -direction : direction, station.Orientation.GetForward());
                var ship = new Ship(id++, enemy ? bomber : fighter, centre + offset,
                    enemy ? -velocity : velocity, orientation) { SideId = enemy ? (short)(1 - side) : side };
                ship.Hull = sector.CollisionHulls.Resolve(ship.HullType.ModelName);
                m.PlaceShip(ship, sector);
                if (nativeLaunch && !enemy) station.Launch(ship, launchBay, sector.LastUpdate, rng);
                ships.Add(ship);
            }
            if (stationDeath)
            {
                var pt = m.Core.ProjectileTypes.Values.First(p => p.Power > 0) with
                { Power = 1, BlastPower = 0, Lifespan = 10, Radius = 1 };
                var mt = m.Core.MissileTypes.Values.First(t => t.HitPoints > 0) with
                { ReadyTime = 10, Lifespan = 10, BlastPower = 0 };
                for (int i = 0; i < count; i++)
                {
                    sector.Add(new Projectile(id++, sector, pt, centre, -velocity, -direction, sector.LastUpdate, ships[^1]));
                    sector.Add(new Missile(id++, sector, mt, centre, velocity, direction, sector.LastUpdate));
                }
                var lethal = m.Core.ProjectileTypes.Values.First(p => p.Power > 0
                    && m.Core.Constants.DamageConstants[p.DamageType][type.DefenseTypeArmor] > 0
                    && m.Core.Constants.DamageConstants[p.DamageType][type.DefenseTypeShield] > 0) with
                { Power = 1e9f, BlastPower = 0, Radius = 1, Lifespan = 10 };
                sector.Add(new Projectile(id++, sector, lethal, new Vector3(0, 0, -4 * type.Radius),
                    new Vector3(0, 0, 50 * type.Radius), new Vector3(0, 0, 1), sector.LastUpdate, ships[^1]));
            }
        }
        if (fullTick)
            server.TestSeedPeers(ships.Where(s => s.SideId == sectors.ToList().IndexOf(s.Sector!))
                .Select(s => (new Allegiance.Core.Transport.ConnectionId((uint)s.Id), s, s.Sector!)).ToList());
        var csv = Path.Combine(Path.GetTempPath(), $"p694-{Guid.NewGuid():N}.csv");
        try
        {
            if (measured)
            {
                // Each sample imported a whole new core/map/server. A live
                // station-death tick does not import those fixtures. Clear their
                // unreachable setup garbage BEFORE timing; in-tick GC remains
                // inside the wall clock and is recorded below.
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                Console.SetOut(console);
            }
            TickProfiler.Configure(true, 3600, csv);
            TickProfiler.EnterTick();
            int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
            long allocated = GC.GetTotalAllocatedBytes();
            double pause = GC.GetTotalPauseDuration().TotalMilliseconds;
            var sw = Stopwatch.StartNew();
            if (fullTick) server.TickSim();
            else foreach (var sector in sectors) sector.Update(sector.LastUpdate + .05);
            sw.Stop();
            if (fullTick)
            {
                server.FlushBroadcastSnapshots();
                Assert.Equal(0, server.SimPassStageFailures);
            }
            long tickAllocated = GC.GetTotalAllocatedBytes() - allocated;
            double tickPause = GC.GetTotalPauseDuration().TotalMilliseconds - pause;
            int tickGc0 = GC.CollectionCount(0) - gc0, tickGc1 = GC.CollectionCount(1) - gc1, tickGc2 = GC.CollectionCount(2) - gc2;
            TickProfiler.EndTick(); TickProfiler.ForceReport(); TickProfiler.Shutdown();
            if (measured)
            {
                output.WriteLine($"P694 count={count} death={stationDeath} coincident={coincident} native={nativeLaunch}: {sw.Elapsed.TotalMilliseconds:F3} ms");
                output.WriteLine($"P694 allocations={tickAllocated} gc={tickGc0}/{tickGc1}/{tickGc2} pauseMs={tickPause:F3}");
                output.WriteLine(File.ReadAllText(csv));
            }
            Assert.All(stations, s => Assert.Equal(stationDeath, s.Dead));
            Assert.All(ships, s => Assert.NotNull(s.Hull));
            if (!stationDeath) Assert.All(ships, s =>
            {
                Assert.False(s.Dead);
                Assert.Equal(1, s.Fraction);
            });
            return sw.Elapsed.TotalMilliseconds;
        }
        finally { TickProfiler.Shutdown(); File.Delete(csv); }
    }
}
