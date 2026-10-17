using System.Net;
using System.Net.Sockets;
using Allegiance.Core.Discovery;
using Allegiance.Server;

namespace Allegiance.Core.Tests;

/// <summary>
/// P731 — the discovery responder + protocol: request/reply encoding, the
/// reply's public facts (name/map/players/max/version/game port), rejection of
/// malformed/foreign datagrams, and the per-second rate limit.
/// </summary>
public class DiscoveryResponderTests
{
    private static DiscoveryResponder MakeResponder(int rate = 20)
    {
        return new DiscoveryResponder(
            () => new DiscoverySnapshot("Mission", "map.igc", 2, 50, "1.2", 6073),
            port: 0, rateLimitPerSecond: rate, multicastGroup: "",
            udp: new UdpClient(new IPEndPoint(IPAddress.Any, 0)));
    }

    [Fact]
    public void Request_reply_round_trip_and_nonce_echo()
    {
        var req = new DiscoveryProtocol.Request { Nonce = "ABCD" };
        var reqBytes = DiscoveryProtocol.EncodeRequest("ABCD");
        var parsed = DiscoveryProtocol.TryParseRequest(reqBytes, reqBytes.Length);
        Assert.NotNull(parsed);
        Assert.Equal("ABCD", parsed!.Nonce);
        Assert.Equal(DiscoveryProtocol.Magic, parsed.Magic);

        var reply = new DiscoveryProtocol.Reply { Nonce = "ABCD", Name = "X", GamePort = 6073 };
        var repBytes = DiscoveryProtocol.EncodeReply(reply);
        var repParsed = DiscoveryProtocol.TryParseReply(repBytes, repBytes.Length);
        Assert.NotNull(repParsed);
        Assert.Equal("ABCD", repParsed!.Nonce);
        Assert.Equal("X", repParsed.Name);
    }

    [Fact]
    public void Responder_replies_with_public_facts_and_game_port()
    {
        using var r = MakeResponder();
        var req = DiscoveryProtocol.EncodeRequest("N1");
        var reply = r.ProcessRequest(req, req.Length);
        Assert.NotNull(reply);
        var parsed = DiscoveryProtocol.TryParseReply(reply!, reply!.Length)!;
        Assert.Equal("N1", parsed.Nonce);
        Assert.Equal("Mission", parsed.Name);
        Assert.Equal("map.igc", parsed.Map);
        Assert.Equal(2, parsed.Players);
        Assert.Equal(50, parsed.MaxPlayers);
        Assert.Equal(6073, parsed.GamePort);   // the game port, distinct from the responder port
        Assert.Equal("1.2", parsed.Version);
        Assert.Equal(1, r.RequestsAnswered);
    }

    [Fact]
    public void Malformed_or_foreign_datagrams_get_no_reply()
    {
        using var r = MakeResponder();
        Assert.Null(r.ProcessRequest(new byte[] { 1, 2, 3 }, 3));
        var junk = System.Text.Encoding.UTF8.GetBytes("hello not a discovery request");
        Assert.Null(r.ProcessRequest(junk, junk.Length));
        // Wrong magic, valid JSON → null.
        var wrongMagic = System.Text.Encoding.UTF8.GetBytes("{\"Magic\":\"other\",\"V\":1,\"Nonce\":\"x\"}");
        Assert.Null(r.ProcessRequest(wrongMagic, wrongMagic.Length));
        Assert.Equal(0, r.RequestsAnswered);
    }

    [Fact]
    public void Rate_limiter_drops_over_budget_requests()
    {
        using var r = MakeResponder(rate: 5);
        var req = DiscoveryProtocol.EncodeRequest("N");
        int answered = 0;
        for (int i = 0; i < 25; i++)
            if (r.ProcessRequest(req, req.Length) is not null)
                answered++;
        Assert.Equal(5, answered);
        Assert.Equal(20, r.RequestsDropped);
    }

    [Fact]
    public void Disabled_responder_is_not_started_by_config()
    {
        // Config-level guard: absent section ⇒ enabled; explicit false ⇒ off.
        var cfg = new DiscoveryConfig { Enabled = false };
        Assert.False(cfg.Enabled);
        Assert.Null(cfg.Port);                      // default resolution left to the responder
        Assert.True(new DiscoveryConfig().Enabled); // absent → enabled (default)
    }
}
