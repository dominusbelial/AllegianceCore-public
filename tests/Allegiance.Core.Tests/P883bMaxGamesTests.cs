using Allegiance.Server;

namespace Allegiance.Core.Tests;

public sealed class P883bMaxGamesTests
{
    [Theory]
    [InlineData("{}", 99)]
    [InlineData("{\"maxGames\":1}", 1)]
    [InlineData("{\"maxGames\":123}", 123)]
    public void Config_loads_native_default_and_explicit_capacity(string json, int expected)
    {
        string dir = Path.Combine(".scratch", "p883b-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "server.json");
            File.WriteAllText(path, json);
            Assert.Equal(expected, ServerConfigLoader.Load(path, []).MaxGames);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Nonpositive_capacity_is_refused_in_code_and_JSON(int invalid)
    {
        var config = new ServerConfig();
        Assert.Equal(99, config.MaxGames);
        Assert.Contains("maxGames", Assert.Throws<InvalidDataException>(() => config.MaxGames = invalid).Message);
        string dir = Path.Combine(".scratch", "p883b-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "server.json");
            File.WriteAllText(path, $"{{\"maxGames\":{invalid}}}");
            Assert.Contains("maxGames", Assert.Throws<InvalidDataException>(() => ServerConfigLoader.Load(path, [])).Message);
        }
        finally { Directory.Delete(dir, true); }
    }

}
