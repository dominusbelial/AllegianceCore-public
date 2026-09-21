using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P320 — end-to-end session-loss detection against a REAL in-process server:
/// connect a real <see cref="ConnectionSession"/> client, let it be seated, then
/// kill the server (dispose it). The transport's client read loop must detect the
/// dropped connection and raise <see cref="ConnectionSession.SessionLost"/> /
/// latch <see cref="ConnectionSession.Lost"/> (review H-4 — before P320 the
/// client went totally silent forever). Also proves a control send after loss is
/// a safe no-op, not a throw (there is no socket to send to).
/// </summary>
[Collection("LiveNetwork")]
public class SessionLossLiveTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Session Loss", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 712u, DwCookie = 0x7B1u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 71000000000000000ul, DrmHash = "sessionlosstest",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static void Eventually(Func<bool> cond, int ms = 5000, string what = "")
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > ms)
                throw new TimeoutException($"timed out waiting for {what}");
            Thread.Sleep(5);
        }
    }

    [Fact]
    public void EstablishedSession_ServerKilled_RaisesLost_EventAndControlsGoSilent()
    {
        using var server = new Srv();
        server.Boot(Config());
        ushort port = server.BoundPort;

        using var peer = new ConnectionSession();
        peer.Connect(port, "loss_peer");
        peer.SendLogonRequest(Request("loss_peer"));

        int joined = PumpUntil(server, () => peer.World.MyShipId is not null, 4000);
        Assert.True(joined < 4000, "peer was never assigned a ship");
        Assert.False(peer.Lost, "not lost while the server is healthy");

        bool eventFired = false;
        peer.SessionLost += () => eventFired = true;

        // Kill the server: dispose closes its transport, which closes the peer's
        // socket, which unwinds the peer's client read loop -> OnSessionLost.
        server.Dispose();

        Eventually(() => peer.Lost, what: "ConnectionSession.Lost after server death");
        Assert.True(eventFired, "SessionLost event must fire on the transport thread");

        // A control send after loss is a silent no-op, not a crash ("not connected"
        // throw) — the exact race the pre-fix client could hit on the render thread.
        peer.CommandControls(new ControlData(0f, 0f, 0f, 1f), ShipControlState.ForwardButton);
        Assert.False(peer.Connected, "Connected must be false after the server dies");
    }
}
