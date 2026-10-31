using System.Diagnostics;
using Allegiance.Core.Profiling;
using Allegiance.Core.Transport;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class P907SchedulingMetricsTests
{
    [Fact]
    public void Histogram_preserves_mean_max_count_and_bounds_p95_without_record_allocations()
    {
        var histogram = new DelayHistogram();
        for (int i = 0; i < 100; i++) histogram.Record(Stopwatch.Frequency / 1000);
        histogram.Drain();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 1; i <= 100; i++) histogram.Record(i * Stopwatch.Frequency / 1000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var window = histogram.Drain();
        Assert.Equal(0, allocated);
        Assert.Equal(100, window.Samples);
        Assert.Equal(50.5, window.MeanMs, 3);
        Assert.Equal(100, window.MaxMs, 3);
        Assert.InRange(window.P95UpperMs, 95, 100);
        Assert.Equal(default, histogram.Drain());
    }

    [Fact]
    public void Probe_does_not_queue_per_tick_and_drains_each_sample_once()
    {
        using var pool = new ThreadPoolTelemetry();
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++) pool.Sample();
        var first = pool.DrainWindow();
        // Do not introduce the same pool-sensitive 100 ms deadline this task
        // diagnoses. A delayed CI pool may take seconds to execute the probe.
        while (first.Delay.Samples == 0 && watch.ElapsedMilliseconds < 5000)
        {
            Thread.Sleep(10);
            first = pool.DrainWindow();
        }
        Assert.Equal(1, first.Delay.Samples);
        Assert.True(first.Threads > 0);
        Assert.True(first.Seconds > 0);
        Assert.True(first.CompletedPerSecond >= 0);
        var second = pool.DrainWindow();
        Assert.InRange(first.Delay.Samples + second.Delay.Samples, 1, 1 + (long)watch.Elapsed.TotalSeconds);
        pool.Dispose();
        Thread.Sleep(1000);
        pool.Sample();
        Assert.Equal(0, pool.DrainWindow().Delay.Samples);
    }

    [SkippableTheory]
    [Trait("Requires", "Artwork")]
    [InlineData("dedicated", true, true)]
    [InlineData("shared", true, true)]
    [InlineData("shared", true, false)]
    [InlineData("dedicated", false, true)]
    public void Production_server_logs_metrics_for_both_host_profiles_only_when_enabled(
        string profile, bool profiler, bool scheduling)
    {
        Require.Artwork();
        var original = Console.Out;
        using var log = new StringWriter();
        try
        {
            Console.SetOut(log);
            using var server = new Srv { SpawnInitialMinersAtBoot = false };
            server.Boot(new ServerConfig
            {
                ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0,
                HostProfile = profile, ExpectedPlayers = 20,
                Profiler = new() { Enabled = profiler, SchedulingMetrics = scheduling, ReportIntervalSeconds = 1 },
                Transport = new() { Protocol = "udp", Mode = "pool" },
                Broadcast = new() { ImmutableSnapshot = true, EncodeOnce = true, ParallelBuild = true, ShipUpdateModel = "native" },
                Sim = new() { NativeGotoPlan = true },
                Games = [new() { Name = "P907", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
            });
            var udp = Assert.IsType<DualTransport>(server.Host.Transport).Udp;
            Assert.Equal(profiler && scheduling, udp.MeasureReceiveDelay);
            using var client = new UdpTransport(new Site());
            client.Join("127.0.0.1", udp.BoundPort, "P907-test");
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 1200) { server.TickSim(); Thread.Sleep(20); }
            string output = log.ToString();
            Assert.Equal(profiler && scheduling, output.Contains("[thread-pool] scope=process"));
            Assert.Equal(profiler && scheduling, output.Contains("[udp-receive]"));
            if (profiler && scheduling)
                Assert.Contains("kernelArrivalToCompletion=unavailable schedulingProxy=thread-pool-probe", output);
            else Assert.Equal(default, udp.DrainReceiveDelay());
        }
        finally { Console.SetOut(original); }
    }

    private sealed class Site : ITransportSite
    {
        public void OnPeerJoined(ConnectionId id, string name) { }
        public void OnPeerLeft(ConnectionId id) { }
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data) { }
    }
}
