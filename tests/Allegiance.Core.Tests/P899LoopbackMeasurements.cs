using System.Diagnostics;
using System.Runtime;
using System.Text.Json;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Server;
using Allegiance.TestInfra;

using Peer = Allegiance.Core.Tests.BroadcastSnapshotLoopbackTests.Peer;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class P899LoopbackMeasurements
{
    // Paced real TCP workload, baseline settings; output is evidence, not a
    // machine-dependent performance assertion. Run before and after a change.
    [SkippableFact, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    public void P899_two_hundred_clients_measure_tick_allocation_and_reuse()
    {
        Require.Artwork();
        using var server = new Server.Server { SpawnInitialMinersAtBoot = false };
        int placed = 0;
        server.ShipSpawner = (core, mission, id) =>
        {
            var hull = core.HullTypes.Values.Where(h => h.Length < 100).OrderByDescending(h => h.ScannerRange).First();
            var ship = new Ship(id, hull, new Vector3(placed % 25 * 300, 10000 + placed / 25 * 300, 0),
                Vector3.Zero, Orientation.Identity) { SideId = (short)(placed++ % 2) };
            mission.PlaceShip(ship, mission.Sectors[0]);
            return (ship, mission.Sectors[0]);
        };
        var config = UpdateCadenceTests.Config(native: true);
        config.Broadcast!.ImmutableSnapshot = config.Broadcast.EncodeOnce = true;
        config.Broadcast.ParallelBuild = true;
        config.Broadcast.MaxDegreeOfParallelism = 4;
        config.Sim.NativeGotoPlan = true;
        config.Transport = new() { Mode = "pool", PoolWorkers = 4, MaxConnections = 216, MaxConnectionsPerIp = 216 };
        server.Boot(config);
        server.TestMission!.Stage = MissionStage.Started;
        var peers = new List<Peer>();
        try
        {
            for (int i = 0; i < 200; i++)
            {
                peers.Add(new Peer(server.BoundPort, i, keepAlive: true));
                if (i % 16 == 15) server.TickSim();
            }
            var join = Stopwatch.StartNew();
            while ((server.Ships.Count != 200 || peers.Any(p => p.OwnId < 0)) && join.Elapsed.TotalSeconds < 30)
            { server.TickSim(); Thread.Sleep(10); }
            Assert.Equal(200, server.Ships.Count);
            Assert.All(peers, p => Assert.True(p.OwnId >= 0));
            void Advance()
            {
                foreach (var peer in server.Ships) peer.Ship.Position += new Vector3(1, 0, 0);
                server.TickSim();
            }
            void PacedTick()
            {
                long start = Stopwatch.GetTimestamp();
                Advance();
                double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (ms < 50) Thread.Sleep(TimeSpan.FromMilliseconds(50 - ms));
            }
            for (int i = 0; i < 40; i++) PacedTick();
            server.FlushBroadcastSnapshots();
            var times = new double[200];
            long allocated = GC.GetTotalAllocatedBytes(true);
            long publishAllocated = server.TestPublisher!.PublishAllocatedBytes;
            long containers = server.TestPublisher.SnapshotContainersCreated;
            for (int i = 0; i < times.Length; i++)
            {
                long start = Stopwatch.GetTimestamp();
                Advance();
                times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (times[i] < 50) Thread.Sleep(TimeSpan.FromMilliseconds(50 - times[i]));
            }
            server.FlushBroadcastSnapshots();
            long totalAllocated = GC.GetTotalAllocatedBytes(true) - allocated;
            server.TestSendSnapshotBarrier(899001);
            Assert.True(SpinWait.SpinUntil(() => peers.All(p => p.LastMarker == 899001), TimeSpan.FromSeconds(15)));
            Assert.All(peers, p => Assert.Null(p.Error));
            Assert.Equal(0, server.TestPublisher.EncodeFailures);
            Assert.Equal(containers, server.TestPublisher.SnapshotContainersCreated);
            var result = new
            {
                serverGc = GCSettings.IsServerGC, clients = 200, ticks = times.Length, meanMs = times.Average(), p95Ms = times.Order().ElementAt(189),
                allocatedBytesPerTick = totalAllocated / times.Length,
                publishAllocatedBytesPerTick = (server.TestPublisher.PublishAllocatedBytes - publishAllocated) / times.Length,
                containers, server.TestPublisher.EncodeFailures,
                framesReceived = peers.Sum(p => p.Frames),
            };
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "p899-loopback.json"),
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { foreach (var peer in peers) peer.Dispose(); }
    }
}
