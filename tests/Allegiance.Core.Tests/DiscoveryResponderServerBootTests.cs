using System.Net;
using System.Net.Sockets;
using Allegiance.Core.Discovery;
using Allegiance.TestInfra;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.Core.Tests;

/// <summary>
/// P731 — proves the discovery responder is wired into a REAL server boot
/// (Server.Lifecycle.BindHostAndTransport → StartDiscoveryResponder): booting a
/// server on port P must automatically answer a UDP discovery query on P+1 with
/// the live mission name/map/players/game port. Requires the artwork corpus
/// (the server must load a real core + map).
/// </summary>
[Collection("LiveNetwork")]
public class DiscoveryResponderServerBootTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;
    private const int GamePort = 17321;          // fixed, uncommon
    private const int DiscoveryPort = GamePort + 1;

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = GamePort,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Discovery Boot Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Booted_server_answers_unicast_discovery_query_on_game_port_plus_one()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());

        using var probe = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var nonce = DiscoveryProtocol.NewNonce();
        var req = DiscoveryProtocol.EncodeRequest(nonce);
        probe.Send(req, req.Length, new IPEndPoint(IPAddress.Loopback, DiscoveryPort));

        // Wait (bounded) for the reply.
        probe.Client.ReceiveTimeout = 1500;
        DiscoveryProtocol.Reply? reply = null;
        try
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);
            var buf = probe.Receive(ref remote);
            reply = DiscoveryProtocol.TryParseReply(buf, buf.Length);
        }
        catch (SocketException) { /* timeout */ }

        Assert.NotNull(reply);
        Assert.Equal(nonce, reply!.Nonce);
        Assert.Equal("Discovery Boot Test", reply.Name);   // live mission name
        Assert.Equal("XXV_simple.igc", reply.Map);
        Assert.Equal(GamePort, reply.GamePort);            // the TCP game port
        Assert.Equal(DiscoveryPort, reply.ResponderPort);
        Assert.True(reply.MaxPlayers >= 0);
    }
}
