using System.Text.Json;
using Allegiance.Core.Model;
using Allegiance.Server;
using Allegiance.SinglePlayer;

namespace Allegiance.ClientCore.Tests;

public sealed class P832gGoalRotationTests
{
    [Fact]
    public void Cycle_covers_every_goal_combo_and_wraps_without_mutating_fixed_config()
    {
        var rotation = new GoalRotationConfig();
        var game = new GameConfig();
        rotation.Validate(game);
        Assert.Equal(Enumerable.Range(0, 8).Concat([0, 1]), Enumerable.Range(0, 10).Select(i => rotation.Select(i, 832)));
        var entries = rotation.Entries.Select(e => e.Goals).ToArray();
        Assert.Contains(entries, g => g.IsConquestGame);
        Assert.Contains(entries, g => g.IsTerritoryGame);
        Assert.Contains(entries, g => g.IsDeathMatchGame);
        Assert.Contains(entries, g => g.IsProsperityGame);
        Assert.Contains(entries, g => g.IsCountdownGame);
        Assert.Contains(entries, g => g.IsFlagsGame && g.IsArtifactsGame);
        Assert.True(game.Goals.IsNoWinningConditionsGame);
    }

    [Fact]
    public void Random_replays_match_seed_and_previous_index_without_consecutive_repeats()
    {
        var rotation = new GoalRotationConfig { Mode = "random" };
        int[] Trace()
        {
            var seeds = new Random(832);
            int previous = -1;
            return Enumerable.Range(0, 100).Select(match => previous = rotation.Select(match, seeds.Next(), previous)).ToArray();
        }
        var trace = Trace();
        Assert.Equal(trace, Trace());
        Assert.Equal(8, trace.Distinct().Count());
        Assert.All(trace.Zip(trace.Skip(1)), pair => Assert.NotEqual(pair.First, pair.Second));
        for (int previous = -1; previous < 8; previous++)
            Assert.Equal(rotation.Select(0, 1234, previous), rotation.Select(99, 1234, previous));
    }

    [Fact]
    public void Source_generated_json_preserves_defaults_and_skirmish_opt_in_is_explicit()
    {
        var config = JsonSerializer.Deserialize("""{"showcase":{"goalRotation":{}}}""", ServerJsonContext.Default.ServerConfig)!;
        Assert.Equal(8, config.Showcase.GoalRotation!.Entries.Length);
        var roundtrip = JsonSerializer.Deserialize(JsonSerializer.Serialize(config, ServerJsonContext.Default.ServerConfig), ServerJsonContext.Default.ServerConfig)!;
        Assert.Equal("cycle", roundtrip.Showcase.GoalRotation!.Mode);
        Assert.Null(new SkirmishOptions().CreateConfig().Showcase.GoalRotation);
        var options = new SkirmishOptions { Seed = 832, GoalRotation = new() };
        var skirmish = options.CreateConfig();
        Assert.Same(options.GoalRotation, skirmish.Showcase.GoalRotation);
        Assert.Equal(832, skirmish.Games[0].CivSeed);
        Assert.Null(new SkirmishOptions { Seed = 832 }.CreateConfig().Games[0].CivSeed);
    }

    [Fact]
    public void Invalid_entries_and_incompatible_goals_fail_before_boot()
    {
        void Reject(GoalRotationConfig rotation, GameConfig? game = null) => Assert.Throws<InvalidDataException>(() =>
            new ServerConfig { Showcase = new() { GoalRotation = rotation }, Games = [game ?? new()] }.ResolveShowcaseMode());
        Reject(new() { Mode = "typo" });
        Reject(new() { Entries = [] });
        Reject(new() { Entries = null! });
        Reject(new() { Entries = [null!, new()] });
        Reject(new(), new() { Goals = new() { Flags = 1 } });
        Reject(new(), new() { GameLengthSeconds = 100 });
        Reject(new(), new() { AllowDevelopments = false });
        Reject(new(), new() { Teams = 2, MapGen = new() { Type = "Brawl" } });
        GoalRotationEntry Entry(string name, short count) => new() { Name = name, Goals = new() { Flags = count } };
        Reject(new() { Entries = [Entry("a", 1), Entry("a", 2)] });
        Reject(new() { Entries = [Entry("a", 1), Entry("b", 1)] });
        Reject(new() { Entries = [Entry("a", 0), Entry("b", 1)] });
        Reject(new() { Entries = [Entry("a", -1), Entry("b", 1)] });
        Reject(new() { Entries = [Entry("bad name", 1), Entry("b", 2)] });
        Reject(new() { Entries = [new() { Name = "a", Goals = null! }, Entry("b", 2)] });
    }
}
