using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Allegiance.Core.Sim;
using Allegiance.Tools.McpSpawner;
using Allegiance.TestInfra;
using ModelContextProtocol.Protocol;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
public sealed class P660ShowcaseLiveTests(ITestOutputHelper output)
{
    // Full MCP preset (15v15), enabled explicitly so routine suites don't run
    // a long background combat scenario. Uses the actual MCP dispatch path.
    [SkippableFact]
    public void Showcase_bots_and_humans_rotate_without_rejoin_and_finale_rearms()
    {
        Skip.If(Environment.GetEnvironmentVariable("ALLEGIANCE_LIVE_P660_SHOWCASE") != "1",
            "Set ALLEGIANCE_LIVE_P660_SHOWCASE=1 for the full 15v15 MCP proof");
        Require.Artwork();
        using var server = new Srv(); server.SpawnInitialMinersAtBoot = false;
        server.Boot(P660NextMatchLiveTests.Config(true, intermission: 12));
        using var humanA = P660NextMatchLiveTests.Connect(server, "P660-showcase-human-A", 66021);
        using var humanB = P660NextMatchLiveTests.Connect(server, "P660-showcase-human-B", 66022);
        using var ticker = new Ticker(server);
        using var manager = new BotManager("127.0.0.1", server.BoundPort);
        Assert.True(manager.ServerReady);
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """{"preset":"showcase","size_scale":1,"target_sector":"Alderan","staging":false,"finale_after_minutes":30}""")!;
        var watch = Stopwatch.StartNew();
        var spawned = McpTools.Handle("spawn_scenario", args, manager);
        Assert.False(spawned.IsError ?? false, JsonSerializer.Serialize(spawned));
        Wait(() => manager.ActiveBots().Count == 30 && manager.ActiveBots().All(b => b.WorldReady));
        var before = manager.ActiveBots().ToDictionary(b => b.Name, b => (b.ShipId, b.Side));
        humanA.SendPlayerReady(true, humanA.World.MyShipId!.Value);
        humanB.SendPlayerReady(true, humanB.World.MyShipId!.Value);
        Wait(() => server.Missions[0].Stage == MissionStage.Started);
        double startClock = server.SimNow;
        ticker.Invoke(() => P660NextMatchLiveTests.End(server));
        Wait(() => P660NextMatchLiveTests.Read(humanA, () => humanA.World.IsIntermission)
            && P660NextMatchLiveTests.Read(humanB, () => humanB.World.IsIntermission));
        Assert.NotNull(humanA.World.GameOverResult);
        Assert.NotNull(humanB.World.GameOverResult);
        output.WriteLine($"intermission timer A={humanA.World.CountdownRemainingSeconds()}s B={humanB.World.CountdownRemainingSeconds()}s");
        Wait(() => server.Missions[0].Stage == MissionStage.Started);
        Wait(() => manager.FinaleStatus().Length == 2);
        var after = manager.ActiveBots();
        Assert.Equal(30, after.Count);
        Assert.All(after, bot =>
        {
            Assert.True(bot.WorldReady);
            Assert.NotEqual(before[bot.Name].ShipId, bot.ShipId);
            Assert.Equal(before[bot.Name].Side, bot.Side);
            Assert.Contains(server.Ships, s => s.Ship.Id == bot.ShipId
                && ReferenceEquals(s.Ship.Mission, server.Missions[0]));
        });
        Assert.True(humanA.Connected && humanB.Connected);
        Wait(() => P660NextMatchLiveTests.Read(humanA, () => humanA.World.GameOverResult is null)
            && P660NextMatchLiveTests.Read(humanB, () => humanB.World.GameOverResult is null));
        var runs = manager.FinaleStatus().Select(r => JsonSerializer.SerializeToElement(r)).ToArray();
        Assert.Equal("next mission", runs[0].GetProperty("state").GetString());
        Assert.Equal(30, runs[0].GetProperty("nextMission").GetProperty("resetBots").GetInt32());
        Assert.Equal(0, runs[0].GetProperty("nextMission").GetProperty("pendingBots").GetInt32());
        Assert.Equal("armed", runs[1].GetProperty("state").GetString());
        Assert.Null(runs[1].GetProperty("triggeredAt").GetString());
        Assert.Equal(0, runs[1].GetProperty("killsLastFiveMinutes").GetInt32());
        Assert.All(runs[1].GetProperty("maturity").EnumerateArray(), m => Assert.Equal(0, m.GetProperty("relaunched").GetInt32()));
        Assert.Equal(0, server.SimPassStageFailures);
        output.WriteLine($"MCP showcase: {after.Count}/30 bots migrated, 2/2 humans connected, "
            + $"sim elapsed={server.SimNow - startClock:F3}s, wall total={watch.Elapsed.TotalSeconds:F3}s, exceptions={server.SimPassStageFailures}");
        output.WriteLine(JsonSerializer.Serialize(runs));
    }

    private static void Wait(Func<bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.Elapsed.TotalSeconds < 90) Thread.Sleep(25);
        Assert.True(done(), "MCP loopback condition timed out");
    }

    private sealed class Ticker : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentQueue<(Action Action, TaskCompletionSource Done)> _actions = new();
        private readonly Thread _thread;
        public Ticker(Srv server)
        {
            _thread = new Thread(() =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    while (_actions.TryDequeue(out var work))
                    {
                        try { work.Action(); work.Done.SetResult(); }
                        catch (Exception ex) { work.Done.SetException(ex); }
                    }
                    server.TickSim(); Thread.Sleep(20);
                }
            }) { IsBackground = true };
            _thread.Start();
        }
        public void Invoke(Action action)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _actions.Enqueue((action, done));
            Assert.True(done.Task.Wait(10_000));
        }
        public void Dispose() { _stop.Cancel(); _thread.Join(5000); _stop.Dispose(); }
    }
}
