using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime;
using System.Runtime.InteropServices;
using Allegiance.Core.Profiling;
using Allegiance.Core.Sim;
using Peer = Allegiance.Core.Tests.BroadcastSnapshotLoopbackTests.Peer;

namespace Allegiance.Core.Tests;

// Opt-in Linux measurement fixture compiled into the Core test project in a scratch
// checkout. See TRANSPORT_ALLOCATION_P1101.md for the build and the sampling window.
[Collection("LiveNetwork")]
public sealed class P1101Measure
{
    [DllImport("libc")]
    private static extern int gettid();

    private sealed class Meter : EventListener
    {
        internal readonly Dictionary<(int Tid, string Type), long> Bytes = [];
        internal long Start, End;

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, EventLevel.Verbose, (EventKeywords)1);
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (e.EventId != 10) return;
            long start = Volatile.Read(ref Start);
            long end = Volatile.Read(ref End);
            if (start == 0 || e.TimeStamp.Ticks < start || (end != 0 && e.TimeStamp.Ticks > end)) return;
            var payload = e.Payload!;
            var names = e.PayloadNames!;
            var key = ((int)e.OSThreadId, (string)payload[names.IndexOf("TypeName")]!);
            long amount = Convert.ToInt64(payload[names.IndexOf("AllocationAmount64")]);
            lock (Bytes) Bytes[key] = Bytes.GetValueOrDefault(key) + amount;
        }
    }

    [Fact]
    public void Sample()
    {
        Assert.True(GCSettings.IsServerGC);
        using var meter = new Meter();
        using var server = new Allegiance.Server.Server { SpawnInitialMinersAtBoot = false };
        var config = UpdateCadenceTests.Config();
        config.Transport = new() { Mode = "pool", PoolWorkers = 4, MaxConnectionsPerIp = 96 };
        config.Broadcast!.ParallelBuild = true;
        server.Boot(config);
        server.TestMission!.Stage = MissionStage.Started;
        var peers = new List<Peer>();
        try
        {
            for (int i = 0; i < 64; i++)
            {
                peers.Add(new Peer(server.BoundPort, i));
                if (i % 8 == 7) server.TickSim();
            }
            var clock = Stopwatch.StartNew();
            while (peers.Any(p => p.OwnId < 0) && clock.Elapsed.TotalSeconds < 20) server.Tick();
            Assert.All(peers, p => Assert.True(p.OwnId >= 0));
            foreach (var peer in server.TestSnapshotPeers())
            {
                peer.Ship.Sector!.Remove(peer.Ship);
                server.TestMission.PlaceShip(peer.Ship, server.TestSectors[0]);
            }

            var ticks = new List<double>(220);
            void Advance()
            {
                long start = Stopwatch.GetTimestamp();
                foreach (var peer in peers)
                    for (int j = 0; j < 4; j++) peer.KeepAlive();
                long simStart = Stopwatch.GetTimestamp();
                server.TickSim();
                ticks.Add(Stopwatch.GetElapsedTime(simStart).TotalMilliseconds);
                var wait = TimeSpan.FromMilliseconds(50) - Stopwatch.GetElapsedTime(start);
                if (wait > TimeSpan.Zero) Thread.Sleep(wait);
            }

            for (int i = 0; i < 20; i++) Advance();
            ticks.Clear();
            int sim = gettid();
            TickProfiler.Configure(true, 3600, null);
            Volatile.Write(ref meter.Start, DateTime.Now.Ticks);
            long initial = GC.GetTotalAllocatedBytes(true);
            clock.Restart();
            for (int i = 0; i < 200; i++) Advance();
            double seconds = clock.Elapsed.TotalSeconds;
            long bytes = GC.GetTotalAllocatedBytes(true) - initial;
            Volatile.Write(ref meter.End, DateTime.Now.Ticks);
            var drain = TickProfiler.Snapshot()["step:drain"];
            Thread.Sleep(500); // allow queued runtime events to reach the listener

            string Owner(int tid)
            {
                if (tid == sim) return "sim";
                try
                {
                    string name = File.ReadAllText($"/proc/self/task/{tid}/comm").Trim();
                    return name.StartsWith("peer-") && name != "peer-send-pool" ? "reader" : name;
                }
                catch (IOException) { return "exited"; }
            }

            using var writer = File.CreateText(Path.Combine(Path.GetTempPath(), "p1101-transport-allocation.tsv"));
            writer.WriteLine($"seconds={seconds:F3} bytes={bytes} drain={drain} tickMean={ticks.Average():F4} tickP95={ticks.Order().ElementAt(189):F4} ticks=200 peers={server.TestSnapshotPeers().Count}");
            lock (meter.Bytes)
            {
                var byOwner = meter.Bytes.GroupBy(row => (Owner(row.Key.Tid), row.Key.Type));
                foreach (var group in byOwner.OrderByDescending(g => g.Sum(row => row.Value)))
                    writer.WriteLine($"{group.Key.Item1}\t{group.Key.Type}\t{group.Sum(row => row.Value)}");
            }
            Assert.Equal(64, server.TestSnapshotPeers().Count);
            Assert.All(peers, p => Assert.Null(p.Error));
        }
        finally
        {
            TickProfiler.Configure(false, 5, null);
            foreach (var peer in peers) peer.Dispose();
        }
    }
}
