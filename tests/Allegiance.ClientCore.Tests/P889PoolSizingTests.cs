using Allegiance.ClientCore.MonoGame.Diagnostics;
using Allegiance.Core.Transport;
using Allegiance.Server;
using Allegiance.SinglePlayer;
using Allegiance.SinglePlayer.Benchmark;
using Allegiance.TestInfra;
using Xunit;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P889 — the one pool-sizing resolver: cores × expected players × host profile
/// → parallel-build sizes and transport mode/workers. Explicit config/env wins;
/// a calibrated per-machine profile sits between the resolver and config.
/// </summary>
public sealed class P889PoolSizingTests
{
    [Theory]
    // Dedicated large: the P887 rig behaviour must hold (cores-1 builders, the
    // pool with about the core count of workers).
    [InlineData(16, 400, HostProfile.Dedicated, true, 15, TransportMode.Pool, 16)]
    [InlineData(24, 394, HostProfile.Dedicated, true, 23, TransportMode.Pool, 24)]
    [InlineData(8, 200, HostProfile.Dedicated, true, 7, TransportMode.Pool, 8)]
    // Dedicated small: no 15-builder/16-sender pool for a 20-player server.
    [InlineData(16, 20, HostProfile.Dedicated, true, 2, TransportMode.Pool, 5)]
    [InlineData(16, 8, HostProfile.Dedicated, false, 1, TransportMode.ThreadPerPeer, 0)]
    [InlineData(4, 10, HostProfile.Dedicated, false, 1, TransportMode.ThreadPerPeer, 0)]
    // Shared skirmish: cores are reserved for the game client and bot driver.
    [InlineData(24, 14, HostProfile.Shared, false, 1, TransportMode.ThreadPerPeer, 0)]
    [InlineData(24, 34, HostProfile.Shared, false, 1, TransportMode.ThreadPerPeer, 0)]
    [InlineData(24, 54, HostProfile.Shared, true, 4, TransportMode.Pool, 14)]
    [InlineData(24, 124, HostProfile.Shared, true, 8, TransportMode.Pool, 18)]
    [InlineData(8, 124, HostProfile.Shared, true, 4, TransportMode.Pool, 5)]
    // Container-limited cores must not oversubscribe.
    [InlineData(2, 400, HostProfile.Dedicated, false, 1, TransportMode.Pool, 2)]
    [InlineData(1, 400, HostProfile.Dedicated, false, 1, TransportMode.Pool, 1)]
    // Player count is clamped, a huge config cannot ask for absurd pools.
    [InlineData(16, 100000, HostProfile.Dedicated, true, 15, TransportMode.Pool, 16)]
    public void Resolver_maps_cores_players_and_profile_to_pool_sizes(int cores, int players,
        HostProfile profile, bool parallel, int buildWorkers, TransportMode mode, int poolWorkers)
    {
        var sizing = PoolSizingResolver.Resolve(cores, players, profile);
        Assert.Equal(profile, sizing.Profile);
        Assert.Equal(parallel, sizing.ParallelBuild);
        Assert.Equal(buildWorkers, sizing.BuildWorkers);
        Assert.Equal(mode, sizing.TransportMode);
        Assert.Equal(poolWorkers, sizing.TransportWorkers);
        Assert.True(sizing.TransportWorkers <= Math.Max(1, cores - sizing.ReservedCores));
        Assert.True(sizing.BuildWorkers <= Math.Max(1, cores - sizing.ReservedCores));
    }

    [Fact]
    public void Shared_profile_reserves_cores_and_dedicated_reserves_none()
    {
        Assert.Equal(0, PoolSizingResolver.Resolve(24, 400, HostProfile.Dedicated).ReservedCores);
        foreach (int cores in new[] { 1, 2, 4, 8, 16, 24, 64 })
        {
            var shared = PoolSizingResolver.Resolve(cores, 400, HostProfile.Shared);
            Assert.True(shared.ReservedCores >= 1);
            Assert.True(shared.ReservedCores < Math.Max(2, cores));
            Assert.True(shared.TransportWorkers <= Math.Max(1, cores - shared.ReservedCores));
        }
    }

    [Fact]
    public void Boot_line_names_the_resolved_sizes_and_the_reason()
    {
        var sizing = PoolSizingResolver.Resolve(16, 20, HostProfile.Dedicated);
        string line = sizing.BootLine();
        Assert.StartsWith("[boot] sizing: profile=dedicated cores=16 players=20 → build=parallel (2 workers), transport=pool (5 workers)", line);
        Assert.Contains("resolver", line);

        var serial = PoolSizingResolver.Resolve(8, 6, HostProfile.Shared);
        Assert.Contains("build=serial (1 worker), transport=thread-per-peer", serial.BootLine());
        Assert.Contains("reserved", serial.BootLine());
    }

    [Fact]
    public void Unrecognized_profile_fails_loudly()
    {
        Assert.Throws<InvalidDataException>(() => PoolSizingResolver.ParseProfile("gpu"));
        Assert.Equal(HostProfile.Dedicated, PoolSizingResolver.ParseProfile("dedicated"));
        Assert.Equal(HostProfile.Shared, PoolSizingResolver.ParseProfile("Shared"));
    }

    [Fact]
    public void Expected_players_uses_the_configured_capacity_unless_declared()
    {
        var config = new ServerConfig
        {
            Games =
            [
                new GameConfig { Teams = 2, MaxPlayersPerTeam = 200 },
                new GameConfig { Teams = 1, MaxPlayersPerTeam = 8 },
            ],
        };
        Assert.Equal(408, PoolSizingResolver.ExpectedPlayersFromConfig(config));
        config.ExpectedPlayers = 20;
        Assert.Equal(20, PoolSizingResolver.ExpectedPlayersFromConfig(config));
        config.ExpectedPlayers = 0;
        Assert.Equal(408, PoolSizingResolver.ExpectedPlayersFromConfig(config));
    }

    [Fact]
    public void Environment_wins_over_config_for_profile_and_players()
    {
        var config = new ServerConfig
        {
            HostProfile = "dedicated",
            ExpectedPlayers = 400,
            Games = [new GameConfig { Teams = 2, MaxPlayersPerTeam = 200 }],
        };
        var environment = new Dictionary<string, string?>
        {
            ["ALLEGIANCE_HOST_PROFILE"] = "shared",
            ["ALLEGIANCE_EXPECTED_PLAYERS"] = "14",
        };
        var sizing = PoolSizingResolver.ResolveConfig(config, name => environment.GetValueOrDefault(name));
        Assert.Equal(HostProfile.Shared, sizing.Profile);
        Assert.Equal(14, sizing.Players);
        Assert.Equal(TransportMode.ThreadPerPeer, sizing.TransportMode);

        environment["ALLEGIANCE_EXPECTED_PLAYERS"] = "not-a-number";
        Assert.Throws<InvalidDataException>(
            () => PoolSizingResolver.ResolveConfig(config, name => environment.GetValueOrDefault(name)));
    }

    [Fact]
    public void Calibration_entry_overrides_the_resolver_on_this_machine_only()
    {
        // Fixed, so a small runner's worker budget cannot clamp the calibrated 6.
        const int Cores = 16;
        var entry = new SizingCalibrationEntry
        {
            Players = 34, Profile = "shared", ParallelBuild = false, BuildWorkers = 1,
            Transport = "pool", PoolWorkers = 6, BotWorkers = 2, BotCoresPerWorker = 3,
            Score = 41.5, Reason = "test", UpdatedUtc = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero),
        };
        var profile = new MachineSizingProfile
        {
            Cores = Cores,
            Version = PoolSizingProfileStore.CurrentVersion,
            Entries = [entry],
        };
        var sizing = PoolSizingResolver.Resolve(Cores, 34, HostProfile.Shared, profile);
        Assert.False(sizing.ParallelBuild);
        Assert.Equal(1, sizing.BuildWorkers);
        Assert.Equal(TransportMode.Pool, sizing.TransportMode);
        Assert.Equal(6, sizing.TransportWorkers);
        Assert.Contains("calibrated on this machine", sizing.Reason);

        // Different core count or build → stale profile is ignored.
        var otherCores = profile with { Cores = Cores + 1 };
        Assert.Null(otherCores.Match(Cores, HostProfile.Shared, 34));
        var otherBuild = profile with { Version = "old" };
        Assert.Null(otherBuild.Match(Cores, HostProfile.Shared, 34));
        // Different profile or a very different player count → no match.
        Assert.Null(profile.Match(Cores, HostProfile.Dedicated, 34));
        Assert.Null(profile.Match(Cores, HostProfile.Shared, 200));
    }

    [Fact]
    public void Calibration_profile_round_trips_through_json()
    {
        string path = Path.Combine(Path.GetTempPath(), $"p889-sizing-{Guid.NewGuid():N}.json");
        try
        {
            var merged = new MachineSizingProfile().With(new SizingCalibrationEntry
            {
                Players = 14, Profile = "shared", ParallelBuild = false, BuildWorkers = 1,
                Transport = "thread-per-peer", PoolWorkers = 0, BotWorkers = 2, BotCoresPerWorker = 4,
                Score = 50.25, Reason = "measured",
            }, Environment.ProcessorCount, path);
            var loaded = PoolSizingProfileStore.Load(path);
            Assert.NotNull(loaded);
            Assert.Equal(Environment.ProcessorCount, loaded!.Cores);
            Assert.Equal(PoolSizingProfileStore.CurrentVersion, loaded.Version);
            var entry = Assert.Single(loaded.Entries);
            Assert.Equal(14, entry.Players);
            Assert.False(entry.ParallelBuild);
            Assert.Equal("thread-per-peer", entry.Transport);
            Assert.Equal(50.25, entry.Score);
            // Re-calibrating the same size replaces, it does not duplicate.
            merged.With(entry with { Score = 33 }, Environment.ProcessorCount, path);
            Assert.Single(PoolSizingProfileStore.Load(path)!.Entries);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Skirmish_declares_shared_and_its_seat_count()
    {
        foreach (var (size, teamSize) in new[]
        {
            (SkirmishSize.Small, 5), (SkirmishSize.Medium, 15), (SkirmishSize.Large, 30),
            (SkirmishSize.Epic, 60), (SkirmishSize.Grand, 200),
        })
        {
            var options = new SkirmishOptions { Size = size };
            var config = options.CreateConfig();
            Assert.Equal("shared", config.HostProfile);
            Assert.Equal(teamSize * 2 + 4, config.ExpectedPlayers);
            Assert.Equal(config.ExpectedPlayers, options.SeatCount);
            // The resolver therefore sizes a Small skirmish far below the P887
            // dedicated pools.
            var sizing = PoolSizingResolver.ResolveConfig(config, _ => null);
            Assert.Equal(HostProfile.Shared, sizing.Profile);
            Assert.Equal(teamSize * 2 + 4, sizing.Players);
            if (size == SkirmishSize.Small)
            {
                Assert.False(sizing.ParallelBuild);
                Assert.Equal(TransportMode.ThreadPerPeer, sizing.TransportMode);
            }
        }
    }

    [Fact]
    public void Skirmish_sizing_override_reaches_the_server_config()
    {
        var options = new SkirmishOptions
        {
            Size = SkirmishSize.Large,
            Sizing = new SkirmishSizing(ParallelBuild: false, BuildWorkers: 1,
                TransportMode: "pool", PoolWorkers: 3, BotWorkers: 2, BotCoresPerWorker: 5),
        };
        var config = options.CreateConfig();
        Assert.False(config.Broadcast!.ParallelBuild);
        Assert.Equal(1, config.Broadcast.MaxDegreeOfParallelism);
        Assert.Equal("pool", config.Transport!.Mode);
        Assert.Equal(3, config.Transport.PoolWorkers);
        var budget = SkirmishBudget.ForOptions(options, Environment.ProcessorCount);
        Assert.Equal(2, budget.Workers);
        Assert.Equal(5, budget.CoresPerWorker);
        // No override: the resolver's shared budget sizes the bots.
        var resolved = SkirmishBudget.ForOptions(new SkirmishOptions { Size = SkirmishSize.Small }, Environment.ProcessorCount);
        Assert.InRange(resolved.Workers, 1, 4);
        Assert.True(resolved.Workers * resolved.CoresPerWorker <= Environment.ProcessorCount);
    }

    [Fact]
    public void Training_declares_shared_and_its_few_seats()
    {
        var config = TrainingHost.TrainingConfig(TrainingHost.BasicFlight, "art");
        Assert.Equal("shared", config.HostProfile);
        Assert.Equal(2, config.ExpectedPlayers);
        var sizing = PoolSizingResolver.ResolveConfig(config, _ => null);
        Assert.Equal(HostProfile.Shared, sizing.Profile);
        Assert.False(sizing.ParallelBuild);
        Assert.Equal(TransportMode.ThreadPerPeer, sizing.TransportMode);
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Explicit_config_still_wins_over_the_resolver_at_boot()
    {
        Require.Artwork();
        var config = new ServerConfig
        {
            ArtPath = Allegiance.Core.ExternalPaths.Resolve(Allegiance.Core.ExternalResourceKind.Artwork),
            StaticCore = "PCore001.igc",
            Port = 0,
            // Deliberately "large" so the resolver would choose parallel+pool.
            ExpectedPlayers = 400,
            Games = [new GameConfig { Name = "P889", Map = "XXV_simple.igc", Teams = 1, CivIds = [1], SideNames = ["Alpha"] }],
            Broadcast = new() { ParallelBuild = false, MaxDegreeOfParallelism = 2 },
            Transport = new() { Mode = "thread-per-peer" },
        };
        using var server = new Server.Server();
        server.Boot(config);
        Assert.False(server._broadcastParallelBuild);
        Assert.Equal(2, server._broadcastMaxParallelism);
        Assert.Equal(TransportMode.ThreadPerPeer, server._transportMode);
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Default_boot_of_a_small_dedicated_config_does_not_start_a_rig_pool()
    {
        Require.Artwork();
        var config = new ServerConfig
        {
            ArtPath = Allegiance.Core.ExternalPaths.Resolve(Allegiance.Core.ExternalResourceKind.Artwork),
            StaticCore = "PCore001.igc",
            Port = 0,
            ExpectedPlayers = 20,
            Games = [new GameConfig { Name = "P889 small", Map = "XXV_simple.igc", Teams = 1, CivIds = [1], SideNames = ["Alpha"] }],
        };
        using var server = new Server.Server();
        server.Boot(config);
        Assert.Equal(5, server._transportPoolWorkers);
        Assert.True(server._transportPoolWorkers < Environment.ProcessorCount);
        Assert.True(server._broadcastMaxParallelism <= 2);
        Assert.Equal(HostProfile.Dedicated, server._poolSizing.Profile);
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Explicit_max_parallelism_alone_selects_the_parallel_build()
    {
        Require.Artwork();
        var config = new ServerConfig
        {
            ArtPath = Allegiance.Core.ExternalPaths.Resolve(Allegiance.Core.ExternalResourceKind.Artwork),
            StaticCore = "PCore001.igc",
            Port = 0,
            ExpectedPlayers = 8,      // resolver would choose serial
            Games = [new GameConfig { Name = "P889 degree", Map = "XXV_simple.igc", Teams = 1, CivIds = [1], SideNames = ["Alpha"] }],
            Broadcast = new() { MaxDegreeOfParallelism = 3 },
        };
        using var server = new Server.Server();
        server.Boot(config);
        Assert.True(server._broadcastParallelBuild);
        Assert.Equal(3, server._broadcastMaxParallelism);
    }

    [Fact]
    public void Frame_profile_window_emits_the_aix2_format_per_five_seconds()
    {
        long now = 0;
        var clock = new DateTimeOffset(2026, 10, 29, 16, 28, 21, 17, TimeSpan.Zero);
        var window = new FrameProfileWindow(5, () => now, () => clock);
        string? line = null;
        for (int i = 0; i < 300; i++)
        {
            now += StopwatchTicks(0.020);
            line = window.Record(20) ?? line;
        }
        Assert.NotNull(line);
        Assert.StartsWith("2026-10-29T16:28:21.017Z [frameprof] window 5s frames=", line);
        Assert.Contains("frame-wall mean=20.0ms p95=20.3ms", line);
        Assert.Matches(@"frames=\d+ frame-wall mean=\d+\.\dms p95=\d+\.\dms", line);
        Assert.True(FrameProfileWindow.EnabledFromEnvironment(_ => null));
        Assert.False(FrameProfileWindow.EnabledFromEnvironment(_ => "0"));
        Assert.True(FrameProfileWindow.EnabledFromEnvironment(_ => "1"));
    }

    [Fact]
    public void Calibration_command_is_bounded_and_strips_its_flags()
    {
        var args = new[] { "--calibrate", "small", "--calibrate-seconds", "20",
            "--calibrate-candidates", "resolver,serial,p887", "--calibrate-no-save",
            "--calibrate-exit", "--calibrate-output", "out.json", "--window", "1280x720" };
        var command = SizingCalibrationCommand.Parse(args);
        Assert.NotNull(command);
        Assert.Equal(SkirmishSize.Small, command!.Size);
        Assert.Equal(20, command.Seconds);
        Assert.False(command.SaveProfile);
        Assert.True(command.ExitWhenComplete);
        Assert.Equal("out.json", command.Output);
        Assert.Equal(["resolver", "serial", "p887"], command.Candidates);
        Assert.Equal(["--window", "1280x720"], SizingCalibrationCommand.Strip(args));
        Assert.Null(SizingCalibrationCommand.Parse(["--window", "1280x720"]));
        Assert.Equal(SkirmishSize.Medium, SizingCalibrationCommand.Parse(["--calibrate"])!.Size);
        Assert.Throws<ArgumentException>(() => SizingCalibrationCommand.Parse(["--calibrate", "small", "--calibrate-seconds", "2"]));
        Assert.Throws<ArgumentException>(() => SizingCalibrationCommand.Parse(["--calibrate", "small", "--calibrate-seconds", "999"]));
    }

    [Fact]
    public void Calibration_score_rewards_lower_frame_and_tick_and_higher_decision_rate()
    {
        double baseline = SizingCalibrationScore.Cost(16.667, 50, 20);
        Assert.Equal(1, baseline, 6);
        Assert.True(SizingCalibrationScore.Cost(10, 50, 20) < baseline);
        Assert.True(SizingCalibrationScore.Cost(16.667, 25, 20) < baseline);
        Assert.True(SizingCalibrationScore.Cost(16.667, 50, 40) < baseline);
        Assert.True(SizingCalibrationScore.Cost(30, 90, 5) > baseline);
        Assert.True(double.IsFinite(SizingCalibrationScore.Cost(0, 0, 0)));
    }

    private static long StopwatchTicks(double seconds) =>
        (long)Math.Round(seconds * System.Diagnostics.Stopwatch.Frequency);
}
