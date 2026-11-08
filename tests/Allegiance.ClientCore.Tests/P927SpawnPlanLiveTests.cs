using System.Text.Json;
using Allegiance.Tools.McpSpawner;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork"), Trait("Tier", "Live")]
public sealed class P927SpawnPlanLiveTests
{
    [SkippableTheory]
    [InlineData(2)] [InlineData(3)] [InlineData(6)]
    public void Plans_validate_real_roster_partition_every_side_and_pin_legacy_two_side_bytes(int teams)
    {
        Require.Artwork();
        var config = P660NextMatchLiveTests.Config(false);
        config.Games[0].Teams = teams;
        config.Games[0].CivSeed = 927;
        config.Games[0].CivIds = Enumerable.Range(0, teams).Select(_ => 25).ToArray();
        config.Games[0].SideNames = Enumerable.Range(0, teams).Select(i => $"Side {i}").ToArray();
        config.Games[0].MapGen = new() { Type = "SingleRing", Seed = 927 };
        config.Games[0].MaxTeamImbalance = short.MaxValue;
        using var server = new Srv { SpawnInitialMinersAtBoot = false }; server.Boot(config);
        using var stop = new CancellationTokenSource(); Exception? error = null;
        var ticker = new Thread(() =>
        {
            try { while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(20); } }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        ticker.Start();
        try
        {
            using var manager = new BotManager("127.0.0.1", server.BoundPort, new(0, 3)) { PlanOnly = true };
            Assert.Equal(teams, manager.ValidSides().Count);
            WorkerReply Call(string json) => WorkerReply.FromTool(McpTools.Handle("spawn_scenario",
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!, manager));
            if (teams == 2)
            {
                string receipt = (string)typeof(Srv).GetMethod("MatchupDetails", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(server, [server.Missions[0]])!;
                Assert.Equal("match=1 seed=927 side0=25:Iron Coalition(none) side1=25:Iron Coalition(none) homeSwap=off homeMatch=1 homes=0,1 home0=0 home1=2 cap=0", receipt); // cap= since P930 (showcase.matchCapSeconds; 0 = no countdown on this fixture)
                var legacy = Call("""{"preset":"showcase","count_a":10,"count_b":11,"finale_after_minutes":0}""");
                Assert.False(legacy.IsError, legacy.Data.ToString());
                Assert.Equal("""{"sideA":{"side":0,"requested":4,"spawned":0,"failed":4,"refilled":0},"sideB":{"side":1,"requested":4,"spawned":0,"failed":4,"refilled":0},"slots":[],"totals":[{"side":0,"requested":10},{"side":1,"requested":11}]}""", legacy.Data.GetRawText());
            }
            var args = JsonSerializer.Serialize(new { preset = "showcase", sides = Enumerable.Range(0, teams).Select(_ => new { count = 10 }), __fleet_id = 927, finale_after_minutes = 0 });
            var plan = Call(args);
            Assert.False(plan.IsError, plan.Data.ToString());
            Assert.Equal(teams, plan.Data.GetProperty("sides").GetArrayLength());
            foreach (var row in plan.Data.GetProperty("sides").EnumerateArray()) Assert.Equal(4, row.GetProperty("requested").GetInt32());
            var slots = plan.Data.GetProperty("slots").Deserialize<FleetSlot[]>()!;
            Assert.Equal(teams * 4, slots.Length);
            Assert.Equal(teams * 4, slots.Select(s => s.Key).Distinct().Count());
            if (teams > 2) Assert.Contains(slots, s => s.Key == "s2:0" && s.Side == 2);
            Assert.Equal(teams == 2, plan.Data.TryGetProperty("sideA", out _));
            var mismatch = Call("""{"preset":"showcase","sides":[{"count":1}]}""");
            Assert.True(mismatch.IsError);
            var invalidFleet = Call(JsonSerializer.Serialize(new { sides = Enumerable.Range(0, teams).Select(_ => new { count = 10, fleet = new[] { new { hull = "Scout", count = 1 } } }) }));
            Assert.True(invalidFleet.IsError);
            Assert.Null(error);
        }
        finally { stop.Cancel(); Assert.True(ticker.Join(5000)); }
    }
    [SkippableFact]
    public async Task Two_real_workers_spawn_and_merge_all_three_factions()
    {
        Require.Artwork();
        var config = P660NextMatchLiveTests.Config(false);
        config.Games[0].Teams = 3; config.Games[0].CivIds = [18, 4, 5];
        config.Games[0].SideNames = ["Gigacorp", "Phoenix", "Omicron"];
        config.Games[0].MapGen = new() { Type = "DiamondRing", Seed = 927 };
        config.Games[0].MaxTeamImbalance = short.MaxValue;
        using var server = new Srv { SpawnInitialMinersAtBoot = false }; server.Boot(config);
        using var stop = new CancellationTokenSource(); Exception? error = null;
        var ticker = new Thread(() =>
        {
            try { while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(20); } }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        ticker.Start();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            using var supervisor = await WorkerSupervisor.StartAsync("127.0.0.1", server.BoundPort, 2, timeout.Token, recovery: false);
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"preset":"showcase","sides":[{"count":2},{"count":2},{"count":2}],"finale_after_minutes":0}""")!;
            var reply = WorkerReply.FromTool(await supervisor.HandleAsync("spawn_scenario", args, timeout.Token));
            Assert.False(reply.IsError, reply.Data.ToString());
            var sides = reply.Data.GetProperty("sides").EnumerateArray().ToArray();
            Assert.Equal(3, sides.Length);
            foreach (var side in sides)
            {
                Assert.Equal(2, side.GetProperty("requested").GetInt32());
                Assert.Equal(2, side.GetProperty("spawned").GetInt32());
                Assert.Equal(0, side.GetProperty("failed").GetInt32());
            }
            Assert.Equal(6, reply.Data.GetProperty("grants").GetArrayLength());
            Assert.False(reply.Data.TryGetProperty("sideA", out _));
            Assert.True(supervisor.Healthy); Assert.Null(error);
        }
        finally { stop.Cancel(); Assert.True(ticker.Join(5000)); }
    }

}
