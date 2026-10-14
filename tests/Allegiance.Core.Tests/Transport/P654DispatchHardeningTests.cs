using System.Reflection;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Core.Sim;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests.Transport;

/// <summary>
/// P654 — failing-first regression tests for the server-side dispatch
/// hardening (each attack fails, the matching normal action still works):
///   H-10  chat sender spoofing is impossible — the server stamps sidSender
///         (native FedSrv.CPP:5249);
///   H-19  PLAYER_READY relays the sender's OWN ship id (native
///         FedSrv.CPP:7019) — a forged teammate id is not relayed;
///   H-28  a >24-char / control-character CharacterName is refused (native
///         c_cbName=25, Utility.h:60,64);
///   H-14  LOGOFF closes the connection, so a second LOGONREQ cannot re-arm
///         and churn ship ids;
///   H-32  REQPINGDATA only answers a seated peer with the same-mission roster;
///   H-24  the per-peer chat budget bounds relay amplification while the first
///         messages still flow.
/// Crafted packets are injected through the server's private ProcessDatagram
/// (reflection, on the sim thread), attributed to a seated bot's ConnectionId,
/// and outbound observed via _testSendCapture / the receiving bot's decode.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P654DispatchHardeningTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games = [new Gm { Name = "P654", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [1, 2, 3, 4],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 911u, DwCookie = 0xBEEFu,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 72000000000000000ul, DrmHash = "p654",
    };

    private static int Pump(Srv s, Func<bool> done, int max = 5000)
    {
        int n = 0;
        while (!done() && n < max) { s.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>Reflection: invoke the private ProcessDatagram(ConnectionId,
    /// byte[]) on the sim thread (the audit's ServerProbe technique).</summary>
    private static void Inject(Srv s, ConnectionId from, byte[] packet)
    {
        var m = typeof(Srv).GetMethod("ProcessDatagram", BindingFlags.NonPublic | BindingFlags.Instance)!;
        m.Invoke(s, new object[] { from, packet });
    }

    /// <summary>Reflection: the ConnectionId whose ship has the given id.</summary>
    private static ConnectionId ConnOfShip(Srv s, short shipId)
    {
        var f = typeof(Srv).GetField("_peerShips", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var map = (System.Collections.IDictionary)f.GetValue(s)!;
        foreach (System.Collections.DictionaryEntry e in map)
        {
            var live = e.Value!;
            var ship = live.GetType().GetProperty("Ship")!.GetValue(live)!;
            int id = (short)ship!.GetType().GetProperty("Id")!.GetValue(ship)!;
            if (id == shipId) return (ConnectionId)e.Key;
        }
        throw new Exception($"no seated peer with ship {shipId}");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Chat_Relay_StampsSender_NotTheForgedSidSender()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        using var bot0 = new FltBot();
        using var bot1 = new FltBot();
        bot0.Connect(server.BoundPort, "b0");
        bot0.SendLogonRequest(Request("p654_bot0"));
        bot1.Connect(server.BoundPort, "b1");
        bot1.SendLogonRequest(Request("p654_bot1"));
        Pump(server, () => bot0.Assignments.Count >= 1 && bot1.Assignments.Count >= 1);
        short ship0 = bot0.Assignments[0].Ship.ShipId;
        short ship1 = bot1.Assignments[0].Ship.ShipId;
        Assert.NotEqual(ship0, ship1);
        var conn0 = ConnOfShip(server, ship0);

        // Attack (H-10): bot0 sends a chat claiming to be bot1 (forged sidSender).
        var forged = new ChatMessage { Message = "hi", SidSender = ship1, ChatTarget = (int)ChatTarget.Everyone, OidRecipient = 0 };
        Inject(server, conn0, FedPacket.Pack(new[] { forged.Encode() }));
        Pump(server, () => bot1.Chats.Count >= 1);

        // The relayed chat must carry bot0's OWN ship id, not the forged ship1.
        Assert.True(bot1.Chats.Count >= 1, "bot1 should receive the relayed chat");
        short stamped = bot1.Chats[^1].SidSender;
        Assert.Equal(ship0, stamped);
        Assert.NotEqual(ship1, stamped);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void PlayerReady_RelaysSendersOwnShip_NotForged()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        using var bot0 = new FltBot();
        using var bot1 = new FltBot();
        bot0.Connect(server.BoundPort, "b0");
        bot0.SendLogonRequest(Request("p654_rdy0"));
        bot1.Connect(server.BoundPort, "b1");
        bot1.SendLogonRequest(Request("p654_rdy1"));
        Pump(server, () => bot0.Assignments.Count >= 1 && bot1.Assignments.Count >= 1);
        short ship0 = bot0.Assignments[0].Ship.ShipId;
        short ship1 = bot1.Assignments[0].Ship.ShipId;
        var conn0 = ConnOfShip(server, ship0);

        // Capture everything the server sends (the PLAYER_READY relay goes to
        // the sender's own side, which includes the sender itself).
        var all = new List<byte[]>();
        server._testSendCapture = (conn, packet) => { lock (all) all.Add(packet); };

        // Attack (H-19): bot0 marks bot1 ready by forging ShipId = ship1.
        var forged = new PlayerReady { FReady = true, ShipId = ship1 };
        Inject(server, conn0, FedPacket.Pack(new[] { forged.Encode() }));

        bool sawRelay = Pump(server, () =>
        {
            lock (all)
                return all.Any(p => { foreach (var m in FedPacket.Unpack(p)) if (FedMessageHeader.Read(m).Fmid == PlayerReady.Fmid) return true; return false; });
        }) < 5000;

        Assert.True(sawRelay, "PLAYER_READY relay should be emitted to the sender's side");
        var relayed = new List<short>();
        foreach (var p in all.ToArray())
            foreach (var m in FedPacket.Unpack(p))
                if (FedMessageHeader.Read(m).Fmid == PlayerReady.Fmid)
                    relayed.Add(PlayerReady.Decode(m).ShipId);
        Assert.NotEmpty(relayed);
        // The relayed ShipId is bot0's OWN ship — a forged teammate id (ship1)
        // must never be relayed (native TrapHackBoot, FedSrv.CPP:7019).
        Assert.All(relayed, id => Assert.Equal(ship0, id));
        Assert.DoesNotContain(ship1, relayed);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void LogonName_Over24Chars_IsRefused()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "attacker");
        bot.SendLogonRequest(Request(new string('x', 30)));   // 30 chars — over native's 24
        Pump(server, () => bot.Assignments.Count >= 1, 1500);
        // The oversized name must NOT be seated (no ship assigned).
        Assert.Empty(bot.Assignments);
        // Normal action still works: a valid 24-char name logs on fine.
        using var ok = new FltBot();
        ok.Connect(server.BoundPort, "ok");
        ok.SendLogonRequest(Request("p654_ok_name_ok"));
        Pump(server, () => ok.Assignments.Count >= 1);
        Assert.True(ok.Assignments.Count >= 1, "a valid name must still be seated");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Logoff_ClosesConnection_SecondLogonCannotRearmShip()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "looper");
        bot.SendLogonRequest(Request("p654_logoff"));
        Pump(server, () => bot.Assignments.Count >= 1);
        short firstShip = bot.Assignments[0].Ship.ShipId;
        var conn = ConnOfShip(server, firstShip);

        // Graceful LOGOFF (H-14): the connection is closed afterwards.
        var lo = new Logoff();
        Inject(server, conn, FedPacket.Pack(new[] { lo.Encode() }));
        Pump(server, () => !(bool)(typeof(Srv).GetField("_peerShips", BindingFlags.NonPublic | BindingFlags.Instance)!
                          .GetValue(server) as System.Collections.IDictionary)!.Contains(conn), 1500);

        // Attack: the same peer tries to LOGONREQ again on the now-closed
        // connection. It must NOT re-seat a fresh ship (the connection is dead),
        // so no second ship id churns on this connection.
        var again = Request("p654_logoff_again");
        try
        {
            Inject(server, conn, FedPacket.Pack(new[] { again.Encode() }));
        }
        catch (Exception)
        {
            // Injection on a closed peer is fine to no-op (dispatch dropped).
        }
        Pump(server, () => bot.Assignments.Count >= 2, 1500);
        Assert.Single(bot.Assignments);   // no second ship churned
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ReqPingData_AnsweredOnlyForSeatedPeer_ScopedToMission()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "probe");
        bot.SendLogonRequest(Request("p654_ping"));
        Pump(server, () => bot.Assignments.Count >= 1);
        short ship = bot.Assignments[0].Ship.ShipId;
        var conn = ConnOfShip(server, ship);

        // Capture every outbound packet on the whole server.
        var all = new List<byte[]>();
        server._testSendCapture = (c, p) => { lock (all) all.Add(p); };

        // Seated peer requests the ping roster: it gets a (same-mission) roster.
        Inject(server, conn, FedPacket.Pack(new[] { new ReqPingData().Encode() }));
        Pump(server, () => { lock (all) return all.Count > 0; });
        int pingEntries = 0;
        foreach (var p in all.ToArray())
            foreach (var m in FedPacket.Unpack(p))
                if (FedMessageHeader.Read(m).Fmid == PingData.Fmid) pingEntries++;
        Assert.True(pingEntries >= 1, "a seated peer should get a PINGDATA roster");

        // Attack (H-32): a NON-seated peer requests the roster. HandleReqPingData
        // must return early (never seated), so NO PINGDATA is emitted to anyone.
        all.Clear();
        var strayConn = new Allegiance.Core.Transport.ConnectionId(9000);   // never seated
        Inject(server, strayConn, FedPacket.Pack(new[] { new ReqPingData().Encode() }));
        Pump(server, () => { lock (all) return all.Count > 0; });
        int leaked = 0;
        foreach (var p in all.ToArray())
            foreach (var m in FedPacket.Unpack(p))
                if (FedMessageHeader.Read(m).Fmid == PingData.Fmid) leaked++;
        Assert.Equal(0, leaked);   // pre-logon REQPINGDATA must not leak the roster (H-32)
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ChatBudget_BoundsRelayAmplification_WhileFirstMessagesFlow()
    {
        Require.Artwork();
        var savedMax = TcpTransport.MaxConnections;      // unused here, just symmetry
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        using var bot0 = new FltBot();
        using var bot1 = new FltBot();
        bot0.Connect(server.BoundPort, "b0");
        bot0.SendLogonRequest(Request("p654_amt0"));
        bot1.Connect(server.BoundPort, "b1");
        bot1.SendLogonRequest(Request("p654_amt1"));
        Pump(server, () => bot0.Assignments.Count >= 1 && bot1.Assignments.Count >= 1);
        short ship0 = bot0.Assignments[0].Ship.ShipId;
        short ship1 = bot1.Assignments[0].Ship.ShipId;
        var conn0 = ConnOfShip(server, ship0);
        var conn1 = ConnOfShip(server, ship1);

        int chatRelayed = 0;
        server._testSendCapture = (c, p) =>
        {
            if (c != conn1) return;
            foreach (var m in FedPacket.Unpack(p))
                if (FedMessageHeader.Read(m).Fmid == ChatMessage.Fmid) chatRelayed++;
        };

        // Attack (H-24): bot0 floods Everyone chats on a hot loop with no ticks
        // in between (so the lazy budget cannot refill). The per-peer budget
        // (native chat budget, c_chatBudgetMax/cost cited) must bound how many
        // reach bot1 — a flood cannot become an unbounded N-fold fan-out — while
        // the FIRST messages still flow (normal gameplay works).
        var chat = new ChatMessage { Message = "spam", SidSender = ship0, ChatTarget = (int)ChatTarget.Everyone, OidRecipient = 0 };
        var packet = FedPacket.Pack(new[] { chat.Encode() });
        for (int i = 0; i < 200; i++) Inject(server, conn0, packet);

        // Normal action: the first chats flowed (bot1 received some).
        Assert.True(chatRelayed > 0, "the first chats must still be relayed");
        // Amplification is bounded: with a 1000/20 budget, at most 50 of the 200
        // get through in this burst (no refill between injections).
        Assert.True(chatRelayed < 200, $"budget must bound the burst ({chatRelayed} relayed of 200)");
        Assert.True(chatRelayed <= 50, $"burst should be capped at ~50 (got {chatRelayed})");
        TcpTransport.MaxConnections = savedMax;
    }
}
