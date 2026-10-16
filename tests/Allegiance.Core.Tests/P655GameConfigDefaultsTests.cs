using Allegiance.Server;

namespace Allegiance.Core.Tests;

public sealed class P655GameConfigDefaultsTests
{
    [Theory]
    [InlineData("{}", 100, 32766)]
    [InlineData("{\"maxPlayersPerTeam\":7}", 7, 32766)]
    [InlineData("{\"maxTeamImbalance\":32767}", 100, 32767)]
    [InlineData("{\"maxPlayersPerTeam\":0,\"maxTeamImbalance\":0}", 0, 0)]
    public void Source_generated_config_preserves_omitted_limits_and_explicit_overrides(string gameJson, int capacity, int imbalance)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"games\":[" + gameJson + "]}");
            var config = ServerConfigLoader.Load(path, []);
            var game = Assert.Single(config.Games);
            Assert.Equal(capacity, game.MaxPlayersPerTeam);
            Assert.Equal(imbalance, game.MaxTeamImbalance);
        }
        finally { File.Delete(path); }
    }
}
