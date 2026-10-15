using Allegiance.Core.Wire;
using Allegiance.Core;
using Allegiance.ClientCore.Session;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P671 — the shared client session's native keep-alive. A <see cref="ConnectionSession"/>
/// (used by the bots' live tests, the Explorer, and the MonoGame client alike)
/// must send CS_PING every 6 s while connected, in EVERY state — including a
/// docked / lobby client that flies nothing. That keep-alive is what stops the
/// server's idle timeout from ever tripping on a healthy client. Before P671 a
/// docked/staged client sent nothing, so the server's (then idle-measuring)
/// read deadline dropped it as ReadErrorOrEof after ~30 s.
///
/// This test boots a real in-process server with the DEFAULT 30 s transport
/// settings, seats a real ConnectionSession, and leaves it DOCKED (no flight)
/// for well past 40 s. It must stay connected, never report lost, and its
/// keep-alive thread must have sent a steady stream of pings (~6 s cadence).
/// </summary>
[Collection("LiveNetwork")]
public class P671KeepAliveLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P671 KeepAlive", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 712u, DwCookie = 0x7B1u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 71200000000000000ul, DrmHash = "p671keepalive",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void IdleDockedClient_StaysConnected_PastIdleWindow()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());                       // DEFAULT transport settings: idle + frame = 30 s
        ushort port = server.BoundPort;

        using var peer = new ConnectionSession();
        peer.Connect(port, "keepalive_peer");
        peer.SendLogonRequest(Request("keepalive_peer"));

        // Seat the peer (gets a ship in the starting sector) but do NOT undock
        // and send NO flight controls: it stays docked / lobby — the "silent"
        // state that used to be dropped.
        int seated = PumpUntil(server, () => peer.World.MyShipId is not null, 4000);
        Assert.True(seated < 4000, "peer was never seated");

        // Measure the keep-alive cadence over a ~13 s window: at the 6 s native
        // interval we expect ~2 pings (allow scheduling slop: 1..3).
        long before = peer.KeepAlivePingsSent;
        for (int i = 0; i < 520; i++) { if (i % 4 == 0) server.TickSim(); Thread.Sleep(25); }   // ~13 s
        long after = peer.KeepAlivePingsSent;
        long delta = after - before;
        Assert.True(delta >= 1 && delta <= 3,
            $"keep-alive cadence off: {delta} pings in ~13 s (expect ~2, i.e. the 6 s native interval)");

        // Now idle the client (still docked, no flight) for well past the 30 s
        // idle window — 42 s total. A pre-P671 silent client was dropped here.
        int idleLoops = 42 * 40;                     // 42 s @ 25 ms
        for (int i = 0; i < idleLoops; i++) { if (i % 4 == 0) server.TickSim(); Thread.Sleep(25); }

        Assert.True(peer.Connected, "idle docked client must stay connected past the 30 s idle window");
        Assert.False(peer.Lost, "idle docked client must not report session lost");
        Assert.True(peer.KeepAlivePingsSent >= 6,
            $"keep-alive must have sent >= 6 pings over ~55 s, got {peer.KeepAlivePingsSent}");
    }
}
