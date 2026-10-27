using System.Buffers.Binary;
using System.Text.Json;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;
using Allegiance.Server;

namespace Allegiance.Core.Tests;

public sealed class P832aMissionGoalsTests
{
    // Native Reset: Igc/igc.h:1286-1311; predicates:1511-1554.
    [Fact]
    public void Native_reset_and_compatibility_defaults_have_distinct_explicit_policies()
    {
        var native = MissionGoals.NativeDefaults();
        Assert.True(native.IsConquestGame);
        Assert.Equal(100, native.ConquestPercentage);
        Assert.False(native.IsTerritoryGame);
        Assert.Equal(100, native.TerritoryPercentage);
        var game = new GameConfig();
        Assert.True(game.ResolveGoals().IsNoWinningConditionsGame);
        game.GameLengthSeconds = 60;
        var goals = game.ResolveGoals();
        Assert.True(goals.IsCountdownGame);
        Assert.False(goals.IsNoWinningConditionsGame);
        Assert.False(goals.IsConquestGame);
        Assert.False(goals.IsDeathMatchGame);
        Assert.False(goals.IsProsperityGame);
        Assert.False(goals.IsArtifactsGame);
        Assert.False(goals.IsFlagsGame);
        goals.TeamKills = 1;
        Assert.Equal(0, game.Goals.TeamKills); // resolution detaches config from the match
    }

    [Theory]
    [InlineData("conquest", 100)] [InlineData("territory", 70)]
    [InlineData("kills", 20)] [InlineData("flags", 10)] [InlineData("artifacts", 10)]
    [InlineData("prosperity", 1)] [InlineData("countdown", 1800)]
    public void Presets_select_one_goal(string name, int threshold)
    {
        var goals = MissionGoals.Preset(name);
        Assert.Equal(1, new[] { goals.IsConquestGame, goals.IsTerritoryGame, goals.IsDeathMatchGame,
            goals.IsFlagsGame, goals.IsArtifactsGame, goals.IsProsperityGame, goals.IsCountdownGame }.Count(x => x));
        Assert.Equal(threshold, name switch
        {
            "conquest" => goals.ConquestPercentage, "territory" => goals.TerritoryPercentage,
            "kills" => goals.TeamKills, "flags" => goals.Flags, "artifacts" => goals.Artifacts,
            "prosperity" => goals.TeamMoney, _ => goals.GameLengthSeconds
        });
    }

    [Fact]
    public void Json_and_cli_reach_every_goal_and_preserve_per_game_values()
    {
        var config = JsonSerializer.Deserialize("""
            {"games":[{"name":"A","goals":{"conquestPercentage":73,"territoryPercentage":60,
              "teamKills":21,"flags":4,"artifacts":7,"teamMoney":2.5,"gameLengthSeconds":600}},
              {"name":"B","goals":{"teamKills":3}}]}
            """, ServerJsonContext.Default.ServerConfig)!;
        Assert.Equal(73, config.Games[0].ResolveGoals().ConquestPercentage);
        Assert.Equal(3, config.Games[1].ResolveGoals().TeamKills);
        Assert.Equal(0, config.Games[1].ResolveGoals().ConquestPercentage);
        var roundtrip = JsonSerializer.Deserialize(JsonSerializer.Serialize(config, ServerJsonContext.Default.ServerConfig),
            ServerJsonContext.Default.ServerConfig)!;
        Assert.Equal(config.Games[0].Goals, roundtrip.Games[0].Goals);
        ServerConfigLoader.ApplyGoalEnvironment(config, "");
        Assert.Equal(3, config.Games[1].Goals.TeamKills);
        ServerConfigLoader.ApplyGoalEnvironment(config, """{"artifacts":2}""");
        Assert.All(config.Games, g => Assert.Equal(2, g.Goals.Artifacts));
        Assert.Equal(1, ServerConfigLoader.ApplyCli(config, [("goal", "flags")]));
        Assert.All(config.Games, g => Assert.Equal(10, g.Goals.Flags));
        Assert.Equal(1, ServerConfigLoader.ApplyCli(config, [("goals", """{"teamMoney":3.25}""")]));
        Assert.All(config.Games, g => Assert.Equal(3.25f, g.ResolveGoals().TeamMoney));
        Assert.NotSame(config.Games[0].Goals, config.Games[1].Goals);
    }

    [Theory]
    [InlineData("conquestPercentage", "101")] [InlineData("territoryPercentage", "-1")]
    [InlineData("teamKills", "-1")] [InlineData("teamKills", "32768")]
    [InlineData("flags", "101")] [InlineData("artifacts", "-1")]
    [InlineData("teamMoney", "9.01")] [InlineData("gameLengthSeconds", "-1")]
    public void Invalid_cli_goals_name_the_bad_field(string field, string value)
    {
        var config = new ServerConfig { Games = [new()] };
        var ex = Assert.Throws<InvalidDataException>(() => ServerConfigLoader.ApplyCli(config,
            [("goals", $"{{\"{field}\":{value}}}")]));
        Assert.Contains(field, ex.Message);
    }

    [Fact]
    public void Invalid_combinations_null_nonfinite_and_conflicting_clocks_fail_clearly()
    {
        Assert.Throws<InvalidDataException>(() => new GameConfig { Goals = null! }.ResolveGoals());
        Assert.Throws<InvalidDataException>(() => new MissionGoals { TeamMoney = float.NaN }.Validate());
        Assert.Throws<InvalidDataException>(() => new GameConfig { GameLengthSeconds = double.PositiveInfinity }.ResolveGoals());
        Assert.Throws<InvalidDataException>(() => new GameConfig { Goals = new() { GameLengthSeconds = 2 }, GameLengthSeconds = 3 }.ResolveGoals());
        Assert.Contains("allowDevelopments", Assert.Throws<InvalidDataException>(() =>
            new GameConfig { AllowDevelopments = false, Goals = new() { TeamMoney = 1 } }.ResolveGoals()).Message);
        Assert.Contains("territory", Assert.Throws<InvalidDataException>(() =>
            new GameConfig { Teams = 2, MapGen = new() { Type = "SingleRing" }, Goals = new() { TerritoryPercentage = 70 } }.ResolveGoals()).Message);
    }

    // Native struct: Igc/igc.h:1158-1179; independent MSVC anchor offsets are
    // recorded in docs/P29_MISSIONDEF_JOINED_MISSION.md. Golden bytes here are
    // literals, not generated using the codec's offsets or its decode method.
    [Fact]
    public void Missiondef_goals_match_native_layout_byte_for_byte()
    {
        var goals = new MissionGoals { ConquestPercentage = 73, TerritoryPercentage = 60,
            TeamKills = 321, Flags = 4, Artifacts = 7, TeamMoney = 2.5f, GameLengthSeconds = 600 };
        var wire = new MissionDef { Goals = goals, RgCivId = [1, 3, 18, 25, 35, 101], ILives = 5 }.Encode();
        Assert.Equal(864, wire.Length);
        Assert.Equal(new byte[] { 0, 0, 32, 64 }, wire[120..124]); // IEEE754 2.5
        Assert.Equal(new byte[] { 0, 0, 22, 68 }, wire[136..140]); // IEEE754 600
        Assert.Equal(new byte[] { 1,0,3,0,18,0,25,0,35,0,101,0,73,0,60,0,100,0,4,0,7,0,65,1 }, wire[172..196]);
        Assert.Equal(5, BinaryPrimitives.ReadInt16LittleEndian(wire.AsSpan(208)));
        Assert.Equal(goals, MissionDef.Decode(wire).Goals);
        // A peer's values also decode independently of our encoder.
        wire[184] = 99; wire[194] = 44; wire[195] = 1;
        Assert.Equal(99, MissionDef.Decode(wire).Goals.ConquestPercentage);
        Assert.Equal(300, MissionDef.Decode(wire).Goals.TeamKills);
    }
    [Fact]
    public void Mission_stage_updates_preserve_detached_native_goals()
    {
        var goals = new MissionGoals { TeamKills = 9, TeamMoney = 2 };
        var world = new Allegiance.ClientCore.State.ClientWorld();
        world.Apply(new MissionDef { Goals = goals });
        goals.TeamKills = 1;
        world.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageStarting });
        Assert.Equal(9, world.MissionGoals.TeamKills);
        Assert.Equal(2, world.MissionGoals.TeamMoney);
        var copy = world.MissionGoals; copy.Flags = 3;
        Assert.Equal(0, world.MissionGoals.Flags);
    }

}
