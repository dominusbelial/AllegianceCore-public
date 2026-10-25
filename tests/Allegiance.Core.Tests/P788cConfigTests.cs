using Allegiance.Server;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

public sealed class P788cConfigTests
{
    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, true)]
    [InlineData(false, "1", true)]
    [InlineData(false, " TRUE ", true)]
    [InlineData(true, "0", false)]
    [InlineData(true, "false", false)]
    public void Native_order_is_opt_in_and_environment_wins(bool configured, string? env, bool expected)
        => Assert.Equal(expected, Srv.ResolveNativeBroadcastOrderFlag(configured, env));

    [Fact]
    public void Invalid_environment_fails_loudly()
        => Assert.Throws<InvalidDataException>(() => Srv.ResolveNativeBroadcastOrderFlag(false, "ture"));

    [Fact]
    public void Source_generated_JSON_binds_native_order_and_default_is_off()
    {
        Assert.False(new ServerConfig().Sim.NativeBroadcastOrder);
        string path = Path.Combine(Path.GetTempPath(), $"p788c-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"sim":{"nativeBroadcastOrder":true}}""");
            Assert.True(ServerConfigLoader.Load(path, []).Sim.NativeBroadcastOrder);
        }
        finally { File.Delete(path); }
    }
}
