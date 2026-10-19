using System.IO.Compression;
using Allegiance.ClientCore.Replay;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Wire;
using Allegiance.SinglePlayer;
using Allegiance.SinglePlayer.Benchmark;

namespace Allegiance.ClientCore.Tests;

public sealed class P729BenchmarkTests
{
    [Fact]
    public void Command_validates_workloads_and_preserves_replay_paths()
    {
        var command = BenchmarkCommand.Parse(["--benchmark", "skirmish:large", "--benchmark-seconds", "2", "--benchmark-headless"])!;
        Assert.Equal(SkirmishSize.Large, command.Size);
        Assert.False(command.Options.StartAsSpectator);
        Assert.Equal(729, command.Options.CreateConfig().BenchmarkSeed);
        Assert.Equal(new[] { 25, 3 }, command.Options.CreateConfig().Games[0].CivIds);
        Assert.Equal("m2xspiderw.igc", command.Options.Map);
        Assert.Equal("C:/battle.alr", BenchmarkCommand.Parse(["--benchmark", "replay:C:/battle.alr"])!.ReplayFile);
        Assert.Throws<ArgumentException>(() => BenchmarkCommand.Parse(["--benchmark", "skirmish:4"]));
        Assert.Throws<ArgumentException>(() => BenchmarkCommand.Parse(["--benchmark", "bogus"]));
        Assert.Throws<ArgumentException>(() => BenchmarkCommand.Parse(["--benchmark", "replay", "--benchmark-seconds", "NaN"]));
        Assert.Null(BenchmarkCommand.Parse([]));
    }

    [Fact]
    public void Percentiles_and_median_select_an_actual_middle_run()
    {
        var stats = Distribution.Of(Enumerable.Range(1, 100).Select(i => (double)i).Append(double.NaN));
        Assert.Equal(50, stats.P50); Assert.Equal(95, stats.P95); Assert.Equal(99, stats.P99);
        Assert.Equal(100, stats.Worst); Assert.Equal(50.5, stats.Mean);
        var runs = new[] { 50, 5, 20 }.Select(n => new BenchmarkRun { Repetition = n, ServerTickMs = Distribution.Of([n]) });
        Assert.Equal(20, BenchmarkResult.SelectMedian(runs).Repetition);
        Assert.Equal(0, Distribution.Of([]).Count);
        var medians = BenchmarkResult.MedianOf(new[]
        {
            new BenchmarkRun { FpsAverage = 50, FpsOnePercentLow = 30, FrameMs = Distribution.Of([5]) },
            new BenchmarkRun { FpsAverage = 200, FpsOnePercentLow = 40, FrameMs = Distribution.Of([10]) },
            new BenchmarkRun { FpsAverage = 100, FpsOnePercentLow = 90, FrameMs = Distribution.Of([15]) }
        });
        Assert.Equal(100, medians.FpsAverage); Assert.Equal(40, medians.FpsOnePercentLow);
        Assert.Equal(10, medians.FrameMs!.P99);
    }

    [Fact]
    public void Recommendation_uses_gpu_cpu_split_and_steps_down_once()
    {
        var gpu = new BenchmarkRun { Rendered = true, CpuMs = 4, GpuMs = 25, FpsOnePercentLow = 35 };
        Assert.Equal("GPU-bound", BenchmarkPolicy.Bottleneck(gpu));
        var recommendation = BenchmarkPolicy.Recommend(gpu, SkirmishSize.Large, 3840, 2160);
        Assert.True(recommendation.Fsr); Assert.Equal(2, recommendation.FsrPreset);
        Assert.False(recommendation.HdPbr); Assert.Equal(SkirmishSize.Large, recommendation.BattleSize);
        Assert.Equal(11, recommendation.Reasons.Length);
        Assert.Contains(recommendation.Reasons, r => r.Setting == "Battle size" && r.Reason.Contains("provisional"));
        Assert.All(recommendation.Reasons, r => Assert.False(string.IsNullOrWhiteSpace(r.Reason)));
        var bots = gpu with { CpuMs = 3, GpuMs = 2, ServerTickMs = Distribution.Of([55, 70]) };
        Assert.Equal("CPU-bound by bots", BenchmarkPolicy.Bottleneck(bots));
        Assert.Equal(SkirmishSize.Medium, BenchmarkPolicy.Recommend(bots, SkirmishSize.Large).BattleSize);
        var lower = BenchmarkPolicy.StepDown(recommendation);
        Assert.Equal(3, lower.FsrPreset); Assert.Equal(SkirmishSize.Medium, lower.BattleSize);
        Assert.Contains(lower.Reasons, r => r.Setting == "Resolution / FSR" && r.Value.StartsWith("Performance"));
        Assert.Contains(lower.Reasons, r => r.Setting == "Effects density" && r.Value == "0.25");
        Assert.Contains(lower.Reasons, r => r.Setting == "HD PBR" && r.Value == "Off");
        Assert.False(BenchmarkPolicy.MeetsTarget(gpu));
        Assert.StartsWith("Frame-time spikes", BenchmarkPolicy.Bottleneck(gpu with { CpuMs = 3, GpuMs = 4 }));
        Assert.True(BenchmarkPolicy.MeetsTarget(bots with { FpsOnePercentLow = 61, ServerTickMs = Distribution.Of([12, 20]) }));
        Assert.False(BenchmarkPolicy.MeetsTarget(new() { FpsOnePercentLow = 1000 }));
    }

    [Fact]
    public void Preferences_round_trip_all_recommended_settings_and_skip_offer()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var settings = BenchmarkPolicy.StepDown(BenchmarkPolicy.Recommend(new() { Rendered = true, CpuMs = 5, GpuMs = 25 }, SkirmishSize.Large));
            var prefs = new BenchmarkPreferences { OfferHandled = true, Applied = settings };
            prefs.Save(path);
            var loaded = BenchmarkPreferences.Load(path);
            Assert.True(loaded.OfferHandled);
            Assert.Equal(settings.BattleSize, loaded.Applied!.BattleSize);
            Assert.Equal(settings.Reasons, loaded.Applied.Reasons);
            Assert.Equal(settings.EffectsDensity, loaded.Applied.EffectsDensity);
            Assert.Equal(settings.ViewDistance, loaded.Applied.ViewDistance);
            var menu = new SkirmishScreenModel(); menu.ApplyDefaults(loaded.Applied);
            Assert.Equal(settings.BattleSize, menu.Options.Size);
            Assert.Equal(settings.ResearchSpeed, menu.Options.CreateConfig().Showcase.ResearchTimeScale);
        }
        finally { File.Delete(path); }
    }

    private static string BattlePath()
    {
        for (var root = new DirectoryInfo(AppContext.BaseDirectory); root is not null; root = root.Parent)
        {
            string path = Path.Combine(root.FullName, "data", "benchmarks", "battle.alr");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("Bundled P729 battle missing");
    }

    [Fact]
    public void Two_real_battle_replays_have_identical_frame_state_hashes_without_network()
    {
        string[] Play()
        {
            using var session = new ConnectionSession();
            var replay = new BattleReplay(BattlePath());
            var hashes = new List<string>();
            Assert.Equal(new byte[] { 0, 1, 2 }, replay.Packets.Where(p => p.ViewSector is not null).Select(p => p.CameraPhase));
            bool enemyInBattle = false, liveProjectile = false;
            while (!replay.Finished)
            {
                replay.Advance(session); hashes.Add(replay.FrameStateHash(session));
                enemyInBattle |= replay.CameraPhase == 1 && replay.ViewedSector is { } sector
                    && session.World.Ships.Values.Any(s => s.SideId == 1 && s.CanDrawInSector(sector));
                liveProjectile |= session.World.Projectiles.Count > 0;
            }
            Assert.True(enemyInBattle, "Recorded observer must actually receive enemy battle poses");
            Assert.True(liveProjectile, "Recorded stream must contain real projectiles");
            Assert.False(session.Connected); Assert.False(session.Lost);
            Assert.True(session.World.Ships.Count >= 9);
            Assert.True(session.DecodedBytes > 1000);
            return hashes.ToArray();
        }
        var first = Play();
        Thread.Sleep(25); // Wall-clock separation must not change frame state.
        Assert.Equal(first, Play());
        Assert.True(first.Distinct().Count() > 100);
    }

    [Fact]
    public void Replay_camera_markers_restore_decoder_sector_and_route_phase()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".alr");
        try
        {
            using (var recording = new WireRecording(path))
            {
                recording.WriteCamera(1050, 0);
                recording.Write(FedPacket.Pack(new Ping { TimeServer = 42 }.Encode()));
                recording.WriteCamera(1052, 1);
            }
            var replay = new BattleReplay(path);
            using var session = new ConnectionSession();
            while (!replay.Finished) replay.Advance(session);
            Assert.Equal((short?)1052, replay.ViewedSector);
            Assert.Equal((short?)1052, session.World.TelemetrySectorOverride);
            Assert.Equal(1, replay.CameraPhase);
            Assert.True(session.DecodedBytes > 0);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Live_reconnect_after_replay_restores_wall_clock_and_ordinary_visibility()
    {
        using var session = new ConnectionSession();
        session.ReplayPacket(FedPacket.Pack(new Ping { TimeServer = 42 }.Encode()), 2000);
        session.World.TelemetrySectorOverride = 1052;
        Assert.Equal(2, session.World.NowSeconds());
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        session.Connect((ushort)((System.Net.IPEndPoint)listener.LocalEndpoint).Port, "p729-live-reconnect");
        Assert.True(session.Connected);
        Assert.Null(session.ReplayMilliseconds);
        Assert.Null(session.World.TelemetrySectorOverride);
        Assert.InRange(Math.Abs(session.World.NowSeconds() * 1000 - Environment.TickCount64), 0, 1000);
    }

    [Fact]
    public void Per_bot_cpu_clock_counts_thread_work_separately_from_elapsed_time()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) return;
        long start = Allegiance.Tools.McpSpawner.ThreadCpuClock.Nanoseconds() ?? throw new Exception("Thread CPU clock unavailable");
        var work = System.Diagnostics.Stopwatch.StartNew();
        while (work.ElapsedMilliseconds < 10) Thread.SpinWait(100);
        long end = Allegiance.Tools.McpSpawner.ThreadCpuClock.Nanoseconds()!.Value;
        Assert.True(end > start);
        Assert.True(end - start <= work.Elapsed.TotalMilliseconds * 1_000_000 + 10_000_000);
    }

    [Fact]
    public void Recording_hook_writes_raw_transport_and_reader_rejects_partial_packets()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".alr");
        try
        {
            var packet = FedPacket.Pack(new Ping { TimeServer = 1234 }.Encode());
            using (var recording = new WireRecording(path))
            using (var session = new ConnectionSession { Recording = recording })
                ((Allegiance.Core.Transport.ITransportSite)session).OnReceive(Allegiance.Core.Transport.ConnectionId.Server, packet);
            var replay = new BattleReplay(path);
            Assert.Equal(packet, Assert.Single(replay.Packets).Payload);
            using (var writer = new BinaryWriter(new GZipStream(File.Create(path), CompressionLevel.Fastest)))
            { writer.Write("P729RPL1"); writer.Write(0L); writer.Write(20); writer.Write(new byte[3]); }
            Assert.Throws<InvalidDataException>(() => new BattleReplay(path));
        }
        finally { File.Delete(path); }
    }
}
