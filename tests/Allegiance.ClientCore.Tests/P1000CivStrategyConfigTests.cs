using System.Text.Json;
using Allegiance.Server;

namespace Allegiance.ClientCore.Tests;

public sealed class P1000CivStrategyConfigTests
{
    [Fact]
    public void Absent_flag_is_off_and_generated_json_contract_reads_the_operator_setting()
    {
        var absent = JsonSerializer.Deserialize("{}", ServerJsonContext.Default.ServerConfig)!;
        Assert.False(absent.Bots.CivStrategy);
        var enabled = JsonSerializer.Deserialize("""{"bots":{"civStrategy":true}}""", ServerJsonContext.Default.ServerConfig)!;
        Assert.True(enabled.Bots.CivStrategy);
    }

    [Theory]
    [InlineData("1", true)] [InlineData("true", true)] [InlineData(" ON ", true)]
    [InlineData("0", false)] [InlineData("false", false)] [InlineData("off", false)]
    public void Environment_overrides_json_in_both_directions(string value, bool expected)
    {
        var config = new BotsConfig { CivStrategy = !expected };
        config.ApplyEnvironment(value);
        Assert.Equal(expected, config.CivStrategy);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")]
    public void Missing_environment_preserves_json(string? value)
    {
        var config = new BotsConfig { CivStrategy = true };
        config.ApplyEnvironment(value);
        Assert.True(config.CivStrategy);
    }

    [Fact]
    public void Invalid_override_fails_before_opening_listeners()
        => Assert.Throws<InvalidDataException>(() => new BotsConfig().ApplyEnvironment("maybe"));
}
