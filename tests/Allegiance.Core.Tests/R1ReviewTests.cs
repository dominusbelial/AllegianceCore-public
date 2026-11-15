using System.Reflection;
using System.Runtime.ExceptionServices;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>R1 review (server network input + authority) — one failing proof per
/// finding in <c>R1_REPORT.md</c>. Every test drives the production inbound
/// dispatcher (<c>TestProcessDatagram</c>) with the bytes a remote peer would
/// send. Each is skipped until its finding is fixed; the report records how it
/// failed when run unskipped.</summary>
[Trait("Requires", "Artwork")]
[Collection("LiveNetwork")]
public sealed class R1ReviewTests
{
    /// <summary>The P867 dispatch-certification world: two sides, seeded peers
    /// with real ships, every egress frame captured.</summary>
    private sealed class World : IDisposable
    {
        public readonly Srv Server = new();
        public readonly ServerHost Host = new();
        public readonly ServerConfig Config = new()
        {
            StaticCore = "PCore001.igc",
            Games = [new GameConfig { MaxTeamImbalance = 0x7fff, MinimumPlayersPerTeam = 1 }, new GameConfig()],
        };
        public readonly Mission Mission, Other;
        public readonly StaticCore Core;
        public readonly Ship Owner, Mate, Enemy, Lobby, Outsider;
        public readonly List<(ConnectionId Peer, byte[] Msg)> Sent = [];
        public readonly ConnectionId A = new(1), B = new(2), C = new(3), L = new(4), X = new(5);
        public World()
        {
            Require.Artwork();
            Core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
            var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "XXV_simple.igc")));
            Mission = Mission.Assemble(Core, map, [new SideSpec(0, "Alpha", 1), new SideSpec(1, "Bravo", 3)], Host);
            Other = Mission.Assemble(Core, map, [new SideSpec(0, "Other", 1), new SideSpec(1, "Team", 3)], Host);
            Mission.Stage = Other.Stage = MissionStage.NotStarted;
            Host.Transport = new TcpTransport(Host);
            typeof(TcpTransport).GetField("_isHost", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Host.Transport, true);
            Set("_host", Host); Set("_config", Config); Set("_missions", new List<Mission> { Mission, Other });
            var hull = Core.HullTypes.Values.First(h => h.GroupId >= 0 && !h.HasCapability(DataHullTypeIGC.HabmLifepod))
                with { HabmCapabilities = 0 };
            Ship Place(short id, short side, Mission m)
            {
                var ship = new Ship(id, hull, new Vector3(5000 + id, 5000, 5000), Vector3.Zero, Orientation.Identity) { SideId = side };
                m.PlaceShip(ship, m.Sectors[0]); ship.InitClock(10); return ship;
            }
            Owner = Place(100, 0, Mission); Mate = Place(101, 0, Mission); Enemy = Place(102, 1, Mission);
            Lobby = Place(103, -2, Mission); Outsider = Place(104, 0, Other);
            Server.TestSeedPeers([(A, Owner, Owner.Sector!), (B, Mate, Mate.Sector!), (C, Enemy, Enemy.Sector!),
                (L, Lobby, Lobby.Sector!), (X, Outsider, Outsider.Sector!)]);
            Mission.MissionOwnerSideId = 0; Mission.Sides[0].LeaderShipId = Owner.Id; Mission.Sides[1].LeaderShipId = Enemy.Id;
            Server._testSendCapture = (c, p) => Sent.AddRange(FedPacket.Unpack(p).Select(m => (c, m)));
            Set("_simNow", 10d);
        }
        public void Set(string field, object value) => typeof(Srv).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Server, value);
        public T Get<T>(string field) => (T)typeof(Srv).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Server)!;
        public object? Call(string method, params object?[] args)
        {
            try { return typeof(Srv).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Server, args); }
            catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
        }
        public void Send(ConnectionId from, IFedMessage message) => Server.TestProcessDatagram(from, FedPacket.Pack([message.Encode()]));
        /// <summary>One wire packet carrying <paramref name="count"/> copies of a message.</summary>
        public void SendBurst(ConnectionId from, IFedMessage message, int count) =>
            Server.TestProcessDatagram(from, FedPacket.Pack(Enumerable.Repeat(message.Encode(), count)));
        public IEnumerable<(ConnectionId Peer, byte[] Msg)> Messages(ushort fmid) => Sent.Where(s => FedMessageHeader.Read(s.Msg).Fmid == fmid);
        public void Dispose() { Host.Transport!.Shutdown(); Server.Dispose(); }
    }

    /// <summary>A really booted server (P966 harness shape) driven through the
    /// production dispatcher; peers log on and pick sides over the wire.</summary>
    private sealed class Booted : IDisposable
    {
        public readonly Srv Server = new() { SpawnInitialMinersAtBoot = false };
        public readonly List<(int Conn, byte[] Message)> Output = [];
        public Booted(Action<ServerConfig>? configure = null)
        {
            Require.Artwork();
            var cfg = new ServerConfig
            {
                ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
                Games = [new GameConfig { Name = "R1", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], AutoStart = false }],
            };
            configure?.Invoke(cfg);
            Server.Boot(cfg);
            Server._testSendCapture = (c, p) => Output.AddRange(FedPacket.Unpack(p).Select(m => ((int)c.Value, m)));
        }
        public Mission Mission => Server.Missions[0];
        public static LogonRequest Request(string name, string password = "") => new()
        {
            CharacterName = name, ZoneTicket = null, CDKey = "", MissionPassword = password,
            FedsrvVer = LogonRequest.MsgVer, Time = 1u, DwCookie = uint.MaxValue,
            CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0, SteamID = 1ul, DrmHash = "r1",
        };
        public void Send(int conn, IFedMessage message) =>
            Server.TestProcessDatagram(new ConnectionId((uint)conn), FedPacket.Pack([message.Encode()]));
        public List<byte[]> SentTo(int conn, ushort fmid) =>
            Output.Where(o => o.Conn == conn && FedMessageHeader.Read(o.Message).Fmid == fmid).Select(o => o.Message).ToList();
        public LogonAck Logon(int conn, LogonRequest req)
        {
            Send(conn, req);
            return LogonAck.Decode(SentTo(conn, LogonAck.Fmid)[^1]);
        }
        /// <summary>Log on, pick <paramref name="side"/> and tick until seated.</summary>
        public Ship Seat(int conn, string name, short side)
        {
            var ack = Logon(conn, Request(name));
            Assert.True(ack.FValidated, ack.CharNameOrFailureReason);
            Send(conn, new PositionReq { SideId = side });
            for (int i = 0; i < 400 && Server.Ships.All(s => s.Ship.Id != ack.ShipID); i++) Server.TickSim();
            return Server.Ships.First(s => s.Ship.Id == ack.ShipID).Ship;
        }
        public void Dispose() => Server.Dispose();
    }

    private sealed class NullSite : ITransportSite
    {
        public int Joined, Left;
        public void OnPeerJoined(ConnectionId id, string name) => Interlocked.Increment(ref Joined);
        public void OnPeerLeft(ConnectionId id) => Interlocked.Increment(ref Left);
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data) { }
    }

    private static string Tally(IEnumerable<(ConnectionId Peer, byte[] Msg)> sent) =>
        string.Join(", ", sent.GroupBy(m => FedMessageHeader.Read(m.Msg).Fmid).Select(g => $"fmid {g.Key} x{g.Count()}"));

    // ── F1: relays that are not charged to the per-peer message budget ─────

    /// <summary>F1 — one packet of 256 six-byte POSITIONREQs from a single peer
    /// is answered with a whole-mission broadcast per copy. Every other relay in
    /// the dispatcher is charged to the P654 budget (about 50 from a full
    /// budget); this one is not.</summary>
    [SkippableFact(Skip = "R1 finding F1 — pending fix")]
    public void One_packet_of_POSITIONREQs_does_not_broadcast_once_per_copy()
    {
        using var w = new World();
        w.SendBurst(w.L, new PositionReq { SideId = 0 }, 256);
        int relays = w.Messages(JoinSide.Fmid).Count() + w.Messages(PositionReqBroadcast.Fmid).Count();
        Assert.True(relays <= 64 * 4, $"256 POSITIONREQs in one packet produced {relays} whole-mission frames ({Tally(w.Sent)})");
    }

    /// <summary>F1 — the same for SET_WINGID: a side-wide relay per copy.</summary>
    [SkippableFact(Skip = "R1 finding F1 — pending fix")]
    public void One_packet_of_SET_WINGIDs_does_not_broadcast_once_per_copy()
    {
        using var w = new World();
        w.SendBurst(w.B, new SetWingId { ShipID = w.Mate.Id, WingId = 3, BCommanded = false }, 256);
        int relays = w.Messages(SetWingId.Fmid).Count();
        Assert.True(relays <= 64 * 2, $"256 SET_WINGIDs in one packet produced {relays} side-wide frames");
    }

    // ── F2: leaving a side mid-game keeps the ship ─────────────────────────

    /// <summary>F2 — a docked player quits their side and joins the other one.
    /// Native resets the ship on the way out (<c>fsmission.cpp:1139</c> →
    /// <c>shipIGC.h:487-530</c>: no station, no cluster, no parts). The port
    /// leaves it docked where it was, so the player is now an enemy inside the
    /// old side's base and can buy and launch from it.</summary>
    [SkippableFact]
    public void A_player_who_switches_sides_mid_game_is_not_left_docked_in_the_old_sides_base()
    {
        using var w = new Booted();
        w.Seat(1, "ALPHA1", 0); var defector = w.Seat(2, "ALPHA2", 0); w.Seat(3, "BRAVO1", 1);
        w.Mission.Stage = MissionStage.Started;
        w.Mission.Sides[1].AutoAccept = true;
        w.Send(2, new QuitSide { ShipId = defector.Id, Reason = QuitSideReason.Quit });
        w.Send(2, new PositionReq { SideId = 1 });
        Assert.Equal((short)1, defector.SideId);
        var dock = w.Mission.Sectors.SelectMany(s => s.Stations).Where(s => s.StationId == defector.StationId).ToArray();
        Assert.True(dock.Length == 0 || dock[0].SideId == defector.SideId,
            $"ship {defector.Id} is on side {defector.SideId} but still docked at station {defector.StationId}, which belongs to side {(dock.Length > 0 ? dock[0].SideId : (short)-1)}");
    }

    /// <summary>F2 — with defections disabled, QUIT_SIDE followed by POSITIONREQ
    /// still changes sides: the team-lobby exemption is all that is checked
    /// (native also remembers the last side, <c>fsmission.cpp:4583-4585</c>).</summary>
    [SkippableFact]
    public void No_defections_cannot_be_bypassed_by_quitting_to_the_team_lobby_first()
    {
        using var w = new World(); w.Mission.Stage = MissionStage.Started;
        w.Config.Games[0].AllowDefections = false;
        w.Mission.Sides[1].AutoAccept = true;
        w.Send(w.B, new QuitSide { ShipId = w.Mate.Id, Reason = QuitSideReason.Quit });
        w.Send(w.B, new PositionReq { SideId = 1 });
        Assert.NotEqual((short)1, w.Mate.SideId);
    }

    // ── F3: the inbound queue is bounded in bytes, not in events ───────────

    /// <summary>F3 — the per-peer receive budget counts payload bytes only, so
    /// CRC-only packets (4 bytes, valid, zero messages) and zero-length frames
    /// (0 bytes) pile up almost for free; nothing here needs a logon.</summary>
    [Fact(Skip = "R1 finding F3 — pending fix")]
    public void Tiny_frames_from_one_peer_cannot_queue_without_bound()
    {
        var host = new ServerHost();
        var peer = new ConnectionId(7);
        byte[] crcOnly = FedPacket.Pack(Array.Empty<byte[]>());
        for (int i = 0; i < 200_000; i++) ((ITransportSite)host).OnReceive(peer, crcOnly);
        for (int i = 0; i < 200_000; i++) ((ITransportSite)host).OnReceive(peer, ReadOnlySpan<byte>.Empty);
        host.Drain(_ => { });
        Assert.True(host.EventsDeferred < 50_000, $"{host.EventsDeferred} events from one peer are still queued after a drain");
    }

    // ── F4: one failed UDP reply stops the UDP listener ────────────────────

    /// <summary>F4 — a send error other than WouldBlock/NoBufferSpace/ConnectionReset
    /// escapes RawSend and ends the UDP worker for everyone. The return address
    /// the OS refuses is simulated with the inbound-route test seam (port 0).</summary>
    [Fact(Skip = "R1 finding F4 — pending fix")]
    public void A_reply_the_OS_refuses_to_send_does_not_stop_the_UDP_listener()
    {
        var site = new NullSite();
        using var host = new UdpTransport(site); host.Host();
        using var player = new UdpTransport(new NullSite());
        player.Join("127.0.0.1", host.BoundPort, "PLAYER");
        var until = DateTime.UtcNow.AddSeconds(5);
        while (site.Joined == 0 && DateTime.UtcNow < until) Thread.Sleep(10);
        Assert.Equal(1, site.Joined);

        host.InboundRouteForTesting = ep => new System.Net.IPEndPoint(ep.Address, 0);
        using var raw = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
        raw.Connect(System.Net.IPAddress.Loopback, host.BoundPort);
        var hello = new byte[64];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(hello, UdpTransport.Magic); hello[4] = 1;
        raw.Send(hello);
        Thread.Sleep(500);
        host.InboundRouteForTesting = null;

        Assert.Equal(0, site.Left);                       // the connected player is still there
        raw.ReceiveTimeout = 1000;
        raw.Send(hello);
        var reply = new byte[1500];
        Assert.Equal(64, raw.Receive(reply));             // and a new Hello is still answered
    }

    // ── F6: the TCP greeting deadline restarts on every byte ───────────────

    /// <summary>F6 — a peer that sends a greeting header and then one body byte
    /// at a time is never timed out: the only bound is the socket's per-read
    /// timeout.</summary>
    [Fact(Skip = "R1 finding F6 — pending fix")]
    public void A_dribbled_greeting_is_dropped_at_the_greet_timeout()
    {
        var orig = TcpTransport.GreetTimeout;
        TcpTransport.GreetTimeout = TimeSpan.FromMilliseconds(300);
        var host = new TcpTransport(new NullSite());
        try
        {
            host.Host(0);
            using var raw = new System.Net.Sockets.TcpClient { NoDelay = true };
            raw.Connect("127.0.0.1", host.BoundPort);
            var ns = raw.GetStream();
            ns.Write([0x01, 0x00, 0x10, 0x00, 0x00]);     // greet frame, declared length 4096
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool dropped = false;
            try { while (sw.ElapsedMilliseconds < 3000) { ns.Write([(byte)'A']); ns.Flush(); Thread.Sleep(100); } }
            catch (IOException) { dropped = true; }
            Assert.True(dropped, $"still accepting greeting bytes after {sw.ElapsedMilliseconds} ms with a 300 ms greet timeout");
        }
        finally { host.Shutdown(); TcpTransport.GreetTimeout = orig; }
    }

    // ── F7: the game password can be guessed without limit ─────────────────

    /// <summary>F7 — every LOGONREQ on one connection is a fresh password
    /// attempt with an immediate answer; nothing slows, counts or closes.</summary>
    [SkippableFact(Skip = "R1 finding F7 — pending fix")]
    public void Wrong_game_passwords_on_one_connection_are_throttled()
    {
        using var w = new Booted(c => c.Games[0].Password = "s3cret");
        for (int i = 0; i < 2000; i++) w.Send(1, Booted.Request("GUESSER", "guess" + i));
        int answered = w.SentTo(1, DelPositionReq.Fmid).Count;
        Assert.True(answered <= 20, $"{answered} of 2000 wrong passwords were evaluated and answered in one tick");
    }

    // ── F8: a seated connection can resume someone's parked seat ───────────

    /// <summary>F8 — the reconnect resume runs before the one-logon-per-connection
    /// check, so a connection that already has a ship can take over a parked
    /// seat; its first ship stays in the sector with no owner (the P646 orphan).</summary>
    [SkippableFact(Skip = "R1 finding F8 — pending fix")]
    public void A_seated_connection_cannot_resume_a_parked_seat()
    {
        using var w = new Booted(c => c.Reconnect = new ReconnectConfig { Enabled = true, GraceSeconds = 30 });
        var dropper = Booted.Request("DROPPER"); dropper.ResumeToken = 0xABCD1234;
        w.Send(1, dropper); w.Send(1, new PositionReq { SideId = 0 });
        for (int i = 0; i < 400 && w.Server.Ships.Count == 0; i++) w.Server.TickSim();
        var collector = w.Seat(2, "COLLECT", 0);
        w.Mission.Stage = MissionStage.Started;
        typeof(Srv).GetMethod("ReleasePeer", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(w.Server, [new ConnectionId(1)]);          // connection 1 drops; its ship is parked
        Assert.Equal(1, w.Server.ReconnectParkedShipCount);

        w.Send(2, dropper);                                     // the SEATED connection presents name + token
        Assert.Contains(collector.Id, w.Server.Ships.Select(s => s.Ship.Id));
        Assert.Equal(1, w.Server.ReconnectParkedShipCount);
    }
}
