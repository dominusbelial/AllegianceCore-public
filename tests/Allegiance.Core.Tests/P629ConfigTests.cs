using Allegiance.Server;

namespace Allegiance.Core.Tests;

public sealed class P629ConfigTests
{
    [Theory]
    [InlineData("{}", "native")]
    [InlineData("{\"sim\":{}}", "native")]
    [InlineData("{\"sim\":{\"crowdingPenalty\":\"off\"}}", "off")]
    public void Json_config_defaults_to_native_and_accepts_explicit_off(string json, string expected)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, json);
            Assert.Equal(expected, ServerConfigLoader.Load(path, []).Sim.CrowdingPenalty);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Invalid_mode_fails_boot_before_artwork_or_transport()
    {
        using var server = new Allegiance.Server.Server();
        var error = Assert.Throws<ArgumentException>(() => server.Boot(new() { Sim = new() { CrowdingPenalty = "typo" } }));
        Assert.Contains("sim.crowdingPenalty", error.Message);
    }
}
