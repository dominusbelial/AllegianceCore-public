using System.Buffers.Binary;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Profiling;
using Allegiance.Core.Sim;
using Allegiance.Server;
using Allegiance.TestInfra;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class P1094CollisionFleetTests(ITestOutputHelper output)
{
    internal static ServerConfig Config()
    {
        var config = UpdateCadenceTests.Config(native: true);
        config.BindAddress = System.Net.IPAddress.Loopback.ToString();
        config.Transport = new() { Mode = "pool", PoolWorkers = 16 };
        config.Games[0].Map = "";
        config.Games[0].MapGen = new() { Type = "SingleRing", Seed = 1094 };
        config.Broadcast!.ParallelBuild = true;
        config.Broadcast.MaxDegreeOfParallelism = 15;
        return config;
    }

    // Place pairs independently so the number of contacts is a fixture input.
    // Re-stage the approach before each measured tick, after the previous bounce.
    internal static void Approach(Ship[] ships, int pairs)
    {
        for (int i = 0; i < ships.Length; i++)
        {
            var ship = ships[i];
            float direction = i % 2 == 0 ? 1 : -1;
            float x = (i / 2 % 20) * 200 + (i < pairs * 2 ? ship.HullType.Length * .2f : 60) * direction;
            ship.Position = new(x, 10000 + (i / 40) * 200, 0);
            ship.Velocity = new(-5 * direction, 0, 0);
            ship.SetFraction(1);
        }
    }

    [SkippableFact, Trait("Requires", "Artwork"), Trait("Tier", "Perf")]
    public void Crowded_fleet_update_profile()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false, SimulationRandom = new Random(1094) };
        server.Boot(Config());
        var peers = UpdateCadenceTests.Seed(server, 400, 400);
        var mission = server.TestMission!;
        mission.Stage = MissionStage.Started;
        var ships = peers.Select(p => p.Ship).ToArray();
        var hull = mission.Core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmFighter));
        for (int i = 0; i < ships.Length; i++)
        {
            ships[i].ChangeHull(hull);
            ships[i].SideId = (short)(i % 2);
        }
        server.TestCollectBroadcast(1, true, "shipStates"); // establish real teammate exports
        var sector = peers[0].Sector;
        foreach (int pairs in new[] { 1, 64 })
        {
            for (int tick = 0; tick < 20; tick++) Update(pairs);
            TickProfiler.Configure(true, 3600, null);
            try
            {
                const int ticks = 100;
                long start = GC.GetTotalAllocatedBytes(true);
                var counts = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
                int resolved = 0;
                for (int tick = 0; tick < ticks; tick++)
                {
                    TickProfiler.EnterTick();
                    Update(pairs);
                    TickProfiler.EndTick();
                    resolved += sector.LastShipShipResolved;
                }
                var bytes = GC.GetTotalAllocatedBytes(true) - start;
                var gc = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - counts[i]);
                output.WriteLine($"P1094 pairs={pairs} resolved={resolved} ticks={ticks} serverGC={System.Runtime.GCSettings.IsServerGC} KiB/tick={bytes / 1024d / ticks:F2} gc={string.Join('/', gc)}");
                foreach (var (name, value) in TickProfiler.Snapshot())
                    if (name is "step:update" or "sim.collision" or "collision.ship.event")
                        output.WriteLine($"P1094 {name} mean={value.MeanMs:F3} p95={value.P95Ms:F3} max={value.MaxMs:F3} n={value.Count}");
                Assert.Equal(pairs * ticks, resolved);
                Assert.All(ships, ship => Assert.False(ship.Dead));
            }
            finally { TickProfiler.Shutdown(); }
        }

        // Outside every timed window, verify the publisher actually consumed
        // the expected reliable fan-out. Counting resolutions alone misses it.
        var delivered = peers.ToDictionary(p => p.Conn, _ => 0);
        server.TestPublicationCapture = (conn, packet, key, guaranteed) =>
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) != Allegiance.Core.Wire.CollisionEvent.Fmid) return;
            Assert.Equal(0, key);
            Assert.True(guaranteed);
            delivered[conn]++;
        };
        Update(64);
        server.TestPublicationCapture = null;
        Assert.Equal(0, server.BroadcastPublishFailures);
        Assert.All(delivered.Values, count => Assert.Equal(64, count));

        void Update(int pairs)
        {
            Approach(ships, pairs);
            server.BeginBroadcastSnapshot();
            using (TickProfiler.Enter("step:update")) mission.Update(mission.LastUpdate + .05);
            server.CompleteBroadcastSnapshot();
            server.FlushBroadcastSnapshots();
        }
    }
}
