using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

public sealed class P1072BotTransportTests
{
    [Fact]
    public void Defaults_and_cli_environment_worker_roundtrip()
    {
        Assert.Equal("tcp", BotTransportOptions.Read(_ => null).Validate().ProtocolAt(0));
        Assert.Equal("udp", BotTransportOptions.Read(k => k == "ALLEGIANCE_TRANSPORT_PROTOCOL" ? "udp" : null).Validate().ProtocolAt(0));
        var env = new Dictionary<string, string>
        {
            ["ALLEGIANCE_TRANSPORT_PROTOCOL"] = "udp", ["ALLEGIANCE_BOT_TRANSPORT"] = "ws", ["ALLEGIANCE_BOT_WS_URL"] = "ws://127.0.0.1:6080/game",
            ["ALLEGIANCE_BOT_WS_ORIGIN"] = "http://127.0.0.1", ["ALLEGIANCE_BOT_WS_FRACTION"] = "0.5",
        };
        var options = BotTransportOptions.Read(k => env.GetValueOrDefault(k)).Validate();
        var parsed = DriverOptions.Parse(["127.0.0.1", "6073", "--worker", "2/3", .. options.Arguments()]);
        Assert.Equal(options, parsed.Transport);
        Assert.Equal(new WorkerPartition(1, 3), parsed.Worker);
        Assert.Throws<ArgumentException>(() => DriverOptions.Parse(["localhost", "6073", "--logon-file", "fixture.json",
            "--transport", "ws", "--ws-url", "wss://example.invalid/game", "--ws-origin", "https://example.invalid"]));
        Assert.Equal("udp", DriverOptions.Parse(["localhost", "6073", "--transport", "udp"]).Transport!.ProtocolAt(0));
    }

    [Theory]
    [InlineData(0)] [InlineData(.1)] [InlineData(.5)] [InlineData(.73)] [InlineData(1)]
    public void Fraction_is_balanced_and_independent_of_worker_scheduling(double fraction)
    {
        var options = new BotTransportOptions("ws", Fraction: fraction);
        var single = Enumerable.Range(0, 100).Select(i => options.UsesWebSocket(i)).ToArray();
        Assert.Equal((int)Math.Floor(100 * fraction), single.Count(x => x));
        var workers = Enumerable.Range(0, 3).SelectMany(worker => Enumerable.Range(0, 34)
            .Select(local => (Index: local * 3 + worker, Ws: options.UsesWebSocket(local * 3 + worker))))
            .Where(p => p.Index < 100).OrderBy(p => p.Index).Select(p => p.Ws).ToArray();
        Assert.Equal(single, workers);
    }

    [Theory]
    [InlineData("--transport", "quic")]
    [InlineData("--transport", "ws")]
    [InlineData("--ws-fraction", "NaN")]
    [InlineData("--ws-fraction", "-0.1")]
    [InlineData("--ws-fraction", "1.1")]
    public void Invalid_options_fail_before_connect(string option, string value)
        => Assert.Throws<ArgumentException>(() => DriverOptions.Parse(["localhost", "6073", option, value]));

    [Theory]
    [InlineData("ws://192.0.2.1/game", "https://example.invalid")]
    [InlineData("wss://example.invalid/game", "https://example.invalid/path")]
    [InlineData("wss://example.invalid/game", "https://example.invalid/")]
    [InlineData("wss://example.invalid/game", "http://example.invalid")]
    public void Routes_preserve_existing_security_policy(string url, string origin)
        => Assert.Throws<ArgumentException>(() => new BotTransportOptions("ws", url, origin).Validate());
}
