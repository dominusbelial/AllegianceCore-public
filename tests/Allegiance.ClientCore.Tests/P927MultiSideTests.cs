using System.Text.Json;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Wire;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

public sealed class P927MultiSideTests
{
    private static Dictionary<string, JsonElement> Args(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Theory]
    [InlineData("{\"sides\":null}", 3)]
    [InlineData("{\"sides\":[{\"count\":10}]}", 1)]
    [InlineData("{\"sides\":[{\"count\":10},{\"count\":10}]}", 3)]
    [InlineData("{\"sides\":[{\"count\":0},{\"count\":10}]}", 2)]
    [InlineData("{\"sides\":[{\"count\":201},{\"count\":10}]}", 2)]
    [InlineData("{\"sides\":[{\"count\":1.5},{\"count\":10}]}", 2)]
    [InlineData("{\"sides\":[{\"count\":\"10\"},{\"count\":10}]}", 2)]
    [InlineData("{\"sides\":[{\"count\":10,\"tech\":-1},{\"count\":10}]}", 2)]
    [InlineData("{\"sides\":[{\"count\":10,\"fleet\":{}},{\"count\":10}]}", 2)]
    [InlineData("{\"sides\":[{\"count\":10,\"fleet\":[]},{\"count\":10}]}", 2)]
    [InlineData("{\"sides\":[{\"count\":10},{\"count\":10}],\"count_a\":10}", 2)]
    public void Rejects_invalid_and_ambiguous_side_arguments(string json, int teams)
        => Assert.Throws<ArgumentException>(() => ScenarioSideSpec.Read(Args(json), teams));

    [Fact]
    public void Opt_in_has_exact_counts_legacy_absence_and_six_side_limit()
    {
        Assert.Null(ScenarioSideSpec.Read(Args("{\"count_a\":10,\"count_b\":12}"), 2));
        var sides = ScenarioSideSpec.Read(Args("{\"sides\":[{\"count\":10},{\"count\":12,\"tech\":3},{\"count\":14}]}"), 3)!;
        Assert.Equal(new[] { 10, 12, 14 }, sides.Select(s => s.Count));
        Assert.Equal(3, sides[1].Tech);
        var args = Args(JsonSerializer.Serialize(new { sides = Enumerable.Range(0, 6).Select(_ => new { count = 1 }) }));
        Assert.Equal(6, ScenarioSideSpec.Read(args, 6)!.Length);
        args["sides"] = JsonSerializer.SerializeToElement(Enumerable.Range(0, 7).Select(_ => new { count = 1 }));
        Assert.Throws<ArgumentException>(() => ScenarioSideSpec.Read(args, 7));
    }

    private static ClusterGraph Graph => new()
    {
        Clusters = [new(0, true), new(1, false), new(2, true), new(3, true), new(4, true)],
        Warps = [new(0, 1), new(1, 2), new(0, 3)]
    };
    private static MultiSideEnemy.Candidate C(short id, short sector, long kills = 0, int stations = 1,
        bool active = true, bool allied = false) => new(id, active, allied, [sector], kills, stations);

    [Fact]
    public void Enemy_selection_nearest_then_kills_stations_id_drops_inactive_allied_unreachable()
    {
        Assert.Equal((short)2, MultiSideEnemy.Choose(0, 0, Graph, [C(1, 2, 100), C(2, 3)]));
        Assert.Equal((short)2, MultiSideEnemy.Choose(0, 0, Graph, [C(1, 3), C(2, 3, 5)]));
        Assert.Equal((short)2, MultiSideEnemy.Choose(0, 0, Graph, [C(1, 3, 5), C(2, 3, 5, 2)]));
        Assert.Equal((short)1, MultiSideEnemy.Choose(0, 0, Graph, [C(2, 3, 5, 2), C(1, 3, 5, 2)]));
        Assert.Equal((short)1, MultiSideEnemy.Choose(0, 0, Graph, [C(2, 3, 100, active: false), C(1, 2)]));
        Assert.Equal((short)1, MultiSideEnemy.Choose(0, 0, Graph, [C(2, 3, 100, allied: true), C(1, 2)]));
        Assert.Equal((short)1, MultiSideEnemy.Choose(0, 0, Graph, [C(2, 4, 100), C(1, 2)]));
        Assert.Null(MultiSideEnemy.Choose(0, 0, Graph, [C(0, 0), C(2, 4), C(-1, 0, 999)]));
    }

    [Fact]
    public void Two_side_enemy_and_finale_choices_are_pinned()
    {
        Assert.Equal((short)1, MultiSideEnemy.Choose(0, 0, Graph, [C(1, 2)]));
        var sides = new ShowcaseFinale.Maturity[] { new(0, 10, 5, 5), new(1, 10, 5, 5) };
        Assert.Equal(new short[] { 0, 1 }, ShowcaseFinale.TriggerSides(new(Side: "both"), 1800, sides, 5));
        Assert.Equal(new short[] { 0 }, ShowcaseFinale.TriggerSides(new(Side: "leader"), 1800, sides, 5));
        Assert.Equal(new short[] { 1 }, ShowcaseFinale.TriggerSides(new(Side: "1"), 1800, sides, 5));
    }

    [Fact]
    public void Finale_all_and_numeric_selection_validate_actual_mission_and_living_roster()
    {
        var sides = new ShowcaseFinale.Maturity[] { new(0, 10, 5, 5), new(1, 10, 5, 10), new(2, 10, 5, 8) };
        Assert.Equal(new short[] { 0, 1, 2 }, ShowcaseFinale.TriggerSides(new(Side: "all"), 1800, sides, 5));
        Assert.Equal(new short[] { 2 }, ShowcaseFinale.TriggerSides(new(Side: "2"), 1800, sides, 5));
        Assert.Equal(new short[] { 1 }, ShowcaseFinale.TriggerSides(new(Side: "leader"), 1800, sides, 5));
        Assert.Equal(new short[] { 0, 2 }, ShowcaseFinale.TriggerSides(new(Side: "all"), 1800, [sides[0], sides[2]], 5));
        Assert.Throws<ArgumentException>(() => McpTools.ValidateFinaleSides(new(Side: "both"), [0, 1, 2]));
        Assert.Throws<ArgumentException>(() => McpTools.ValidateFinaleSides(new(Side: "3"), [0, 1, 2]));
        Assert.Equal("all", McpTools.ReadFinaleSettings(Args("{\"sides\":[{},{},{}]}"), true).Side);
        Assert.Equal("both", McpTools.ReadFinaleSettings(Args("{}"), true).Side);
    }

    [Fact]
    public void Strike_enemy_is_latched_between_commits_and_dropped_when_eliminated()
    {
        var tactics = new ResearchTactics();
        var targets = new ResearchTactics.Target[]
        {
            new(11, IgcObjectType.Station, 2, default, false, Side: 1),
            new(22, IgcObjectType.Station, 3, default, false, Side: 2)
        };
        int selections = 0; short choice = 1;
        short? Select() { selections++; return choice; }
        tactics.Tick(0, true, [], targets, selectEnemy: Select);
        choice = 2;
        tactics.Tick(1, true, [], targets, selectEnemy: Select);
        Assert.Equal(1, selections);
        tactics.Tick(2, true, [], [targets[1]], selectEnemy: Select);
        Assert.Equal(2, selections);
    }

    private static WorkerReply Reply(string data) => new(false, JsonDocument.Parse(data).RootElement.Clone());
    private static WorkerReply State(string names) => new(false, JsonSerializer.SerializeToElement(new WorkerState(
        CoordinationState.Empty, JsonDocument.Parse(names).RootElement.Clone())));

    [Fact]
    public void Legacy_merged_results_are_byte_pinned_and_have_no_new_keys()
    {
        var merged = SupervisorResults.MergeScenario(
            [Reply("""{"sideA":{"side":0,"requested":1},"sideB":{"side":1,"requested":1}}""")],
            [Reply("""{"sideA":{"side":0},"sideB":{"side":1},"grants":[{"side":0,"name":"a"},{"side":1,"name":"b"}]}""")],
            [State("""[{"name":"a","status":"active"},{"name":"b","status":"active"}]""")], []);
        Assert.Equal("""{"sideA":{"side":0,"requested":1,"spawned":1,"failed":0,"refilled":0},"sideB":{"side":1,"requested":1,"spawned":1,"failed":0,"refilled":0},"grants":[{"side":0,"name":"a"},{"side":1,"name":"b"}],"failures":[],"warnings":0,"minefields":[],"workers":[],"note":"Parallel worker spawn; counts are connected slots at the supervisor\u0027s final inventory check. Lost or unresponsive workers contribute zero spawned slots."}""", merged.GetRawText());
    }

    [Fact]
    public void Three_side_merge_counts_live_slots_and_dead_worker_failures_per_side()
    {
        var plan = Reply("""{"sides":[{"side":0,"requested":1},{"side":1,"requested":1},{"side":2,"requested":2}]}""");
        var reply = Reply("""{"sides":[{"side":0},{"side":1},{"side":2}],"grants":[{"side":0,"name":"a"},{"side":1,"name":"b"},{"side":2,"name":"c","refilled":true}]}""");
        var merged = SupervisorResults.MergeScenario([plan, plan], [reply, new(true, JsonSerializer.SerializeToElement(new { error = "lost" }))],
            [State("""[{"name":"a","status":"active"},{"name":"c","status":"active"}]"""), new(true, JsonSerializer.SerializeToElement(new { error = "lost" }))], []);
        var rows = merged.GetProperty("sides");
        Assert.False(merged.TryGetProperty("sideA", out _));
        Assert.Equal(1, rows[0].GetProperty("spawned").GetInt32());
        Assert.Equal(0, rows[1].GetProperty("spawned").GetInt32());
        Assert.Equal(4, rows[2].GetProperty("requested").GetInt32());
        Assert.Equal(1, rows[2].GetProperty("spawned").GetInt32());
        Assert.Equal(1, rows[2].GetProperty("refilled").GetInt32());
        Assert.Equal(3, rows[2].GetProperty("failed").GetInt32());
    }
}
