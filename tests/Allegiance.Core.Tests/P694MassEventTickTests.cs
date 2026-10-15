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
public sealed class P694MassEventTickTests(ITestOutputHelper output)
{
    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(195, false, false, false, false)]
    [InlineData(195, true, false, false, false)]
    [InlineData(195, false, true, false, false)]
    [InlineData(195, true, true, false, false)]
    [InlineData(195, false, false, true, false)]
    [InlineData(195, true, false, true, false)]
    [InlineData(195, false, true, true, false)]
    [InlineData(195, true, true, true, false)]
    [InlineData(150, false, true, false, false)]
    [InlineData(150, true, true, false, false)]
    [InlineData(200, false, false, false, true)]
    [InlineData(200, true, false, false, true)]
    [InlineData(200, false, true, false, true)]
    [InlineData(200, true, true, false, true)]
    public void Launch_point_mass_events(int count, bool parallel, bool stationDeath, bool coincident, bool nativeLaunch)
    {
        Require.Artwork();
        ParallelSectorSim.Enabled = parallel;
        ParallelSectorSim.ThreadCount = 4;
        try
        {
            // Station death / next-match launches occur in an already running
            // server. Exercise the numerical helpers long enough for the default
            // tiered JIT to finish background promotion, then time fresh worlds.
            var warmup = Stopwatch.StartNew();
            do { Run(count, stationDeath, coincident, parallel, nativeLaunch, false); }
            while (warmup.Elapsed < TimeSpan.FromSeconds(1));
            for (int sample = 0; sample < 3; sample++) Run(count, stationDeath, coincident, parallel, nativeLaunch, true);
        }
        finally { ParallelSectorSim.Reset(); }
    }

    private void Run(int count, bool stationDeath, bool coincident, bool parallel, bool nativeLaunch, bool measured)
    {
        var console = Console.Out;
        try
        {
            // Boot and warmup are outside the measurement. Do not queue their
            // many megabytes of console output immediately ahead of timed work.
            Console.SetOut(TextWriter.Null);
            RunWorld(count, stationDeath, coincident, parallel, nativeLaunch, measured, console);
        }
        finally { Console.SetOut(console); }
    }

    private void RunWorld(int count, bool stationDeath, bool coincident, bool parallel, bool nativeLaunch, bool measured, TextWriter console)
    {
        using var server = new Srv();
        server.Boot(new Allegiance.Server.ServerConfig
        {
            ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0,
            Sim = new Allegiance.Server.SimConfig { ParallelSectorCells = parallel, ParallelSectorCellThreads = 4 },
            Games = [new Allegiance.Server.GameConfig
            { Name = "P694", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }]
        });
        Assert.Equal(parallel, ParallelSectorSim.Enabled);
        Assert.Equal(4, ParallelSectorSim.ThreadCount);
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
            foreach (var sector in sectors) sector.Update(sector.LastUpdate + .05);
            sw.Stop();
            long tickAllocated = GC.GetTotalAllocatedBytes() - allocated;
            double tickPause = GC.GetTotalPauseDuration().TotalMilliseconds - pause;
            int tickGc0 = GC.CollectionCount(0) - gc0, tickGc1 = GC.CollectionCount(1) - gc1, tickGc2 = GC.CollectionCount(2) - gc2;
            TickProfiler.EndTick(); TickProfiler.ForceReport(); TickProfiler.Shutdown();
            if (measured)
            {
                output.WriteLine($"P694 count={count} parallel={ParallelSectorSim.Enabled} death={stationDeath} coincident={coincident} native={nativeLaunch}: {sw.Elapsed.TotalMilliseconds:F3} ms");
                output.WriteLine($"P694 allocations={tickAllocated} gc={tickGc0}/{tickGc1}/{tickGc2} pauseMs={tickPause:F3}");
                output.WriteLine(File.ReadAllText(csv));
                Assert.True(sw.Elapsed.TotalMilliseconds < 100, $"tick={sw.Elapsed.TotalMilliseconds:F3}ms");
            }
            Assert.All(stations, s => Assert.Equal(stationDeath, s.Dead));
            Assert.All(ships, s => Assert.NotNull(s.Hull));
            if (!stationDeath) Assert.All(ships, s =>
            {
                Assert.False(s.Dead);
                Assert.Equal(1, s.Fraction);
            });
        }
        finally { TickProfiler.Shutdown(); File.Delete(csv); }
    }
}
