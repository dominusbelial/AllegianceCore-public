using System.Diagnostics;
using System.Text.Json;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Server;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork"), Trait("Tier", "Live")]
public sealed class P930MatchCapLiveTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void Research_bots_complete_two_capped_matches_and_restart_the_clock()
    {
        Require.Artwork();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "AllegianceCore.slnx"))) root = root.Parent!;
        string directory = Path.Combine(root.FullName, ".scratch/p930");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"cap-loopback-{DateTime.UtcNow:yyyyMMddTHHmmssfff}.log");
        using var log = TextWriter.Synchronized(new StreamWriter(path) { AutoFlush = true });
        var saved = Console.Out; Console.SetOut(log);
        try
        {
            var config = P660NextMatchLiveTests.Config(true, intermission: 5);
            // JSON lets this exact regression run against the unmodified server too.
            config.Showcase = JsonSerializer.Deserialize("""{"techMode":"research","matchCapSeconds":60}""",
                ServerJsonContext.Default.ShowcaseConfig)!;
            config.BenchmarkSeed = 930;
            config.Broadcast = new() { ImmutableSnapshot = true, EncodeOnce = true, ParallelBuild = true, ShipUpdateModel = "native" };
            config.Transport = new() { Mode = "pool" };
            config.Sim.NativeGotoPlan = true;
            config.LiveSettings.TestHarness = null;
            config.Games[0].CivIds = [25, 25];
            config.Games[0].CivSeed = 930;
            config.Games[0].MaxTeamImbalance = 0x7fff;
            using var server = new Srv { SpawnInitialMinersAtBoot = false }; server.Boot(config);
            var gate = new object();
            using var stop = new CancellationTokenSource(); Exception? tickerError = null;
            var ticker = new Thread(() =>
            {
                try { while (!stop.IsCancellationRequested) { lock (gate) server.TickSim(); Thread.Sleep(10); } }
                catch (Exception ex) { tickerError = ex; }
            }) { IsBackground = true };
            ticker.Start();
            try
            {
                using var manager = new BotManager("127.0.0.1", server.BoundPort);
                var result = McpTools.Handle("spawn_scenario", JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    """{"preset":"showcase","count_a":2,"count_b":2,"staging":true,"finale_after_minutes":0}""")!, manager);
                Assert.False(result.IsError ?? false, JsonSerializer.Serialize(result));
                double previousEnd = 0;
                for (int match = 0; match < 2; match++)
                {
                    Mission mission; lock (gate) mission = server.Missions[0];
                    var watch = Stopwatch.StartNew();
                    bool sawStarted = false, sawWireClock = false;
                    while (watch.Elapsed.TotalSeconds < 40)
                    {
                        lock (gate)
                        {
                            sawStarted |= mission.Stage == MissionStage.Started;
                            if (mission.Stage == MissionStage.Over) break;
                        }
                        lock (manager.Probe.Gate)
                            sawWireClock |= manager.Probe.World.MissionGoals.GameLengthSeconds == 60;
                        Assert.Null(tickerError);
                        Thread.Sleep(20);
                    }
                    lock (gate)
                    {
                        output.WriteLine($"match={match + 1} stage={mission.Stage} start={mission.CountdownStart:R} end={server.SimNow:R} wall={watch.Elapsed.TotalSeconds:F2} log={path}");
                        Assert.Equal(MissionStage.Over, mission.Stage);
                        Assert.True(sawStarted && sawWireClock);
                        Assert.True(mission.CountdownStart > previousEnd);
                        Assert.InRange(server.SimNow - mission.CountdownStart, 60, 60.5);
                        previousEnd = server.SimNow;
                    }
                    Assert.Equal(match + 1, File.ReadLines(path).Count(l => l.StartsWith("[finale] cap winner")));
                    Assert.Equal(match + 1, File.ReadLines(path).Count(l => l.StartsWith("[gameover] mission")));
                }
                Assert.Equal(0, server.SimPassStageFailures);
                Assert.Equal(4, manager.ActiveBots().Count);
            }
            finally { stop.Cancel(); Assert.True(ticker.Join(5000)); }
        }
        finally { Console.SetOut(saved); }
    }
}
