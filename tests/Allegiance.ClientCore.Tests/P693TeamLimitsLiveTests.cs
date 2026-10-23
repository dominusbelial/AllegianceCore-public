using System.Diagnostics;
using System.Text.Json;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
public sealed class P693TeamLimitsLiveTests(ITestOutputHelper output)
{
    // The MCP clients use the normal wire protocol. No rig/remote server and no
    // balance override: harness grants money/tech, but rank balance still applies.
    private static Srv Boot(int? capacity = null)
    {
        Require.Artwork();
        var config = P660NextMatchLiveTests.Config(false);
        if (capacity is { } cap) config.Games[0].MaxPlayersPerTeam = cap;
        var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(config);
        server.Missions[0].Stage = MissionStage.Started;
        return server;
    }

    private static void Run(Srv server, Action work)
    {
        var task = Task.Run(work);
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed.TotalSeconds < 240)
        {
            server.TickSim();
            Thread.Sleep(5);
        }
        task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }

    [SkippableTheory]
    [InlineData("combat")]
    [InlineData("idle")]
    [InlineData("weave")]
    public void Batched_bots_consume_native_balance_refusal_and_choose_an_eligible_side(string behavior)
    {
        using var server = Boot();
        Run(server, () =>
        {
            using var manager = new BotManager("127.0.0.1", server.BoundPort);
            short requested = manager.ProbeSide;
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                $$"""{"hull":"Adv Scout","side":{{requested}},"behavior":"{{behavior}}"}""")!;
            for (int i = 0; i < 8; i++)
            {
                var result = McpTools.Handle("spawn_bot", args, manager);
                Assert.False(result.IsError ?? false, JsonSerializer.Serialize(result));
                var bots = manager.ActiveBots();
                Assert.Equal(i + 1, bots.Count);
                if (i == 0) Assert.NotEqual(requested, bots.Single().Side);
            }
            var final = manager.ActiveBots();
            Assert.Equal(4, final.Count(b => b.Side == requested));
            Assert.Equal(4, final.Count(b => b.Side != requested));
            output.WriteLine($"{behavior}: 8/8 same-side requests seated as 4/4, plus probe on {requested}; default rank balance");
        });
        Assert.Equal(0, server.SimPassStageFailures);
    }

    [SkippableTheory]
    [InlineData("combat")]
    [InlineData("idle")]
    public void Full_team_falls_back_and_all_full_reports_the_native_reason_promptly(string behavior)
    {
        using var server = Boot(capacity: 1);
        Run(server, () =>
        {
            using var manager = new BotManager("127.0.0.1", server.BoundPort);
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                $$"""{"hull":"Adv Scout","side":{{manager.ProbeSide}},"behavior":"{{behavior}}"}""")!;
            var first = McpTools.Handle("spawn_bot", args, manager);
            Assert.False(first.IsError ?? false, JsonSerializer.Serialize(first));
            Assert.NotEqual(manager.ProbeSide, manager.ActiveBots().Single().Side);
            var watch = Stopwatch.StartNew();
            var refused = McpTools.Handle("spawn_bot", args, manager);
            Assert.True(refused.IsError ?? false);
            string error = JsonSerializer.Serialize(refused);
            Assert.Contains("TeamFull", error);
            Assert.DoesNotContain("never seated", error);
            Assert.True(watch.Elapsed.TotalSeconds < 10, error);
            Assert.Single(manager.ActiveBots());
            output.WriteLine($"{behavior}: fallback seated; all-full refusal in {watch.Elapsed.TotalSeconds:F3}s");
        });
    }

    [SkippableFact]
    public void Showcase_30v30_completes_with_native_defaults_on_a_started_mission()
    {
        using var server = Boot();
        Assert.Equal(100, server.Config.Games[0].MaxPlayersPerTeam);
        Assert.Equal(0x7ffe, server.Config.Games[0].MaxTeamImbalance);
        Run(server, () =>
        {
            using var manager = new BotManager("127.0.0.1", server.BoundPort);
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                // P797 — no finale: the finale planner needs an OBSERVED enemy
                // home station (BotManager.Finale.cs:83), and a fresh side no
                // longer starts knowing every station (native exports stations
                // per side, fsmission.cpp:4215-4222). This test's subject is the
                // 30v30 spawn / team-capacity behaviour, not the finale; P798
                // restores the finale once the bots scout the enemy home.
                """{"preset":"showcase","size_scale":2,"target_sector":"Alderan","staging":false,"finale_after_minutes":0}""")!;
            var watch = Stopwatch.StartNew();
            var result = McpTools.Handle("spawn_scenario", args, manager);
            Assert.False(result.IsError ?? false, JsonSerializer.Serialize(result));
            var bots = manager.ActiveBots();
            Assert.Equal(60, bots.Count);
            Assert.All(bots, bot => Assert.True(bot.WorldReady));
            Assert.Equal(30, bots.Count(bot => bot.Side == 0));
            Assert.Equal(30, bots.Count(bot => bot.Side == 1));
            output.WriteLine($"MCP showcase: {bots.Count}/60 bots ready, 30/30 per side plus probe; native capacity=100 imbalance=32766; wall={watch.Elapsed.TotalSeconds:F3}s");
        });
        Assert.Equal(0, server.SimPassStageFailures);
    }
}
