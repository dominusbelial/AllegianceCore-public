using System.Net;
using System.Text;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Xunit;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

using Server = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

/// <summary>
/// P866 — the native server counters and the two connection callbacks whose whole
/// visible body is bookkeeping:
/// <list type="bullet">
/// <item><b>SIM-0583 / OnMessageSent</b> (<c>FedSrv/FedSrv.CPP:4613-4652</c>): every
/// <c>FM_GUARANTEED</c> message bumps <c>cPacketsOut</c>/<c>cBytesOut</c> (plus
/// their per-second twins); the non-guaranteed branch counts nothing.
/// <see cref="ServerCounters"/> is the named port of native's
/// <c>SERVER_COUNTERS</c> (<c>FedSrv/counters.h:10-21</c>), fed from the per-peer
/// send queue where the port hands a frame to the transport.</item>
/// <item><b>SIM-0580 / OnDestroyConnection</b> (<c>FedSrv.CPP:4549-4566</c>): a
/// connection destroyed while a player was still attached bumps
/// <c>cTimeouts</c>; a LOGOFF is not a timeout (native deletes the player inside
/// the LOGOFF handler first, so the callback's player lookup finds nothing).</item>
/// <item><b>SIM-0579 / OnNewConnection</b> (<c>FedSrv.CPP:4536-4546</c>): the whole
/// native body is the "New connection ID: %x IP: %s" debug line. The port logs it
/// when the greeting mints the connection id and records the address
/// (<see cref="TcpTransport.PeerAddress"/>).</item>
/// </list>
/// </summary>
[Collection("LiveNetwork")]
public sealed class P866ConnectionBookkeepingTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games = [new Gm { Name = "P866 Counters", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 866u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 86600000000000000ul, DrmHash = "p866counters",
    };

    private static int PumpUntil(Server server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>Native OnMessageSent's counting rule, at the unit level: a
    /// guaranteed frame's messages each add one packet and their own <c>cbmsg</c>
    /// bytes; a non-guaranteed frame adds nothing; a malformed frame adds nothing.
    /// The per-second twins come from one window drain and are independent of the
    /// cumulative values (native keeps both, FedSrv.CPP:4633-4636).</summary>
    [Fact]
    public void ServerCounters_count_guaranteed_messages_and_timeouts_like_native()
    {
        var counters = new ServerCounters();
        var ping = new Ping { TimeClient = 7, TimeServer = 9 }.Encode();
        // The transport wraps a FedPacket (messages + CRC) in its outer
        // [type][bodyLen] frame; that outer frame is what the send queue sees.
        static byte[] Frame(byte[] packet)
        {
            var frame = new byte[5 + packet.Length];
            frame[0] = 2;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1), (uint)packet.Length);
            packet.CopyTo(frame, 5);
            return frame;
        }
        var single = Frame(FedPacket.Pack([ping]));
        counters.RecordFrame(single, guaranteed: true);
        Assert.Equal(1, counters.CPacketsOut);                       // cPacketsOut += cConnections
        Assert.Equal(ping.Length, counters.CBytesOut);               // cBytesOut += cb * cConnections

        var batch = Frame(FedPacket.Pack([ping, ping, ping]));
        counters.RecordFrame(batch, guaranteed: true);
        Assert.Equal(4, counters.CPacketsOut);
        Assert.Equal(4L * ping.Length, counters.CBytesOut);

        // Native's non-guaranteed branch counts nothing (FedSrv.CPP:4640-4650).
        counters.RecordFrame(single, guaranteed: false);
        Assert.Equal(4, counters.CPacketsOut);
        Assert.Equal(4L * ping.Length, counters.CBytesOut);

        // A malformed/short frame cannot be attributed and is ignored.
        counters.RecordFrame([2, 0], guaranteed: true);
        counters.RecordFrame([], guaranteed: true);
        Assert.Equal(4, counters.CPacketsOut);

        counters.RecordTimeout();
        counters.RecordTimeout();
        Assert.Equal(2, counters.CTimeouts);

        var window = counters.DrainWindow();
        Assert.Equal(2, window.CTimeouts);
        Assert.Equal(4, window.CPacketsOut);
        Assert.Equal(4L * ping.Length, window.CBytesOut);
        Assert.True(window.CTimeoutsPerSecond > 0);
        Assert.True(window.CPacketsOutPerSecond > 0);
        Assert.True(window.CBytesOutPerSecond > 0);
        // Cumulative values never reset; the next window's rates are 0.
        var quiet = counters.DrainWindow();
        Assert.Equal(2, quiet.CTimeouts);
        Assert.Equal(0, quiet.CPacketsOutPerSecond);
    }

    /// <summary>⭐ On a live loopback server: the accept-time IP is recorded and
    /// logged (native OnNewConnection's whole body), a guaranteed join burst bumps
    /// cPacketsOut/cBytesOut, and an UNREQUESTED connection loss bumps cTimeouts —
    /// while a graceful LOGOFF does not.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Live_server_new_connection_ip_egress_counters_and_timeout_counter()
    {
        Require.Artwork();
        using var server = new Server();
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = Allegiance.Core.Sim.MissionStage.Started;
        var transport = server.TestTransport;
        Assert.NotNull(transport);

        var bot = new FltBot();
        var log = new SyncWriter();
        var saved = Console.Out;
        try
        {
            Console.SetOut(TextWriter.Synchronized(log));
            bot.Connect(server.BoundPort, "p866_counters");
            bot.SendLogonRequest(Request("p866_counters"));
            PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        }
        finally { Console.SetOut(saved); }

        Assert.True(bot.ShipAssignment is not null, "the bot must be seated");
        var conn = server.TestSnapshotPeers().Single(p => p.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Conn;

        // SIM-0579 — the accept-time log line (native FedSrv.CPP:4536-4541).
        Assert.Contains("[conn] new connection ID:", log.Text);
        Assert.Contains($"ID: {conn}", log.Text);
        // The recorded source address is exactly what the log printed.
        Assert.Equal(IPAddress.Loopback, transport!.PeerAddress(conn));

        // SIM-0583 — the join burst is guaranteed traffic: both counters grew.
        Assert.True(server.Counters.CPacketsOut > 0, "guaranteed join traffic must bump cPacketsOut");
        Assert.True(server.Counters.CBytesOut > 0, "guaranteed join traffic must bump cBytesOut");
        long packets = server.Counters.CPacketsOut;
        long bytes = server.Counters.CBytesOut;
        PumpUntil(server, () => false, 40);
        Assert.True(server.Counters.CPacketsOut > packets && server.Counters.CBytesOut > bytes,
            "ticking broadcast traffic keeps feeding the native egress counters");

        // SIM-0580 — a dropped (not logged-off) connection bumps cTimeouts once.
        long timeouts = server.Counters.CTimeouts;
        bot.Dispose();
        Assert.True(PumpUntil(server, () => server.Counters.CTimeouts == timeouts + 1, 4000) < 4000,
            "an unrequested connection loss must bump cTimeouts (FedSrv.CPP:4558-4562)");

        Console.WriteLine($"P866-COUNTERS: cPacketsOut={server.Counters.CPacketsOut} cBytesOut={server.Counters.CBytesOut} " +
                          $"cTimeouts={server.Counters.CTimeouts} (peer {conn} from {transport.PeerAddress(conn)})");
    }

    /// <summary>A graceful LOGOFF is NOT a timeout: native deletes the player in
    /// the LOGOFF handler, so OnDestroyConnection's player lookup misses and the
    /// counter block is skipped (FedSrv.CPP:4551-4563, P813c's requested flag).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Graceful_logoff_does_not_bump_the_timeout_counter()
    {
        Require.Artwork();
        using var server = new Server();
        server.Boot(Config());
        var mission = server.TestMission!;
        var ship = new Allegiance.Core.Sim.Ship(9000, mission.Core.HullTypes.Values.First(),
            Allegiance.Core.Collision.Vector3.Zero, Allegiance.Core.Collision.Vector3.Zero,
            Allegiance.Core.Sim.Orientation.Identity) { SideId = mission.Sides[0].Id };
        mission.PlaceShip(ship, mission.Sectors[0]);
        var conn = new ConnectionId(960);
        server.TestSeedPeers([(conn, ship, mission.Sectors[0])]);

        long before = server.Counters.CTimeouts;
        server.TestProcessDatagram(conn, FedPacket.Pack([new Logoff().Encode()]));

        Assert.False(server.TestSnapshotPeers().Any(p => p.Conn == conn), "LOGOFF must release the peer");
        Assert.Equal(before, server.Counters.CTimeouts);
    }

    /// <summary>⭐ SIM-0582 — native <c>FedSrvSiteBase::OnBadCRC</c>
    /// (<c>FedSrv.CPP:4591-4603</c>) logs the corrupt packet (player, sizes, fmid)
    /// and DELETES the connection. Before P866 the port logged the failure and let
    /// the peer keep talking. A raw socket speaks the greeting, then sends a
    /// well-formed frame whose trailing CRC is wrong; the server must log the line
    /// and drop the peer (the socket closes) while the server itself keeps
    /// ticking.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Corrupt_crc_packet_is_logged_and_the_peer_is_dropped()
    {
        Require.Artwork();
        using var server = new Server();
        server.Boot(Config());
        using var client = new System.Net.Sockets.TcpClient();
        client.Connect(IPAddress.Loopback, server.BoundPort);
        client.NoDelay = true;
        var stream = client.GetStream();

        static void WriteFrame(Stream s, byte type, byte[] body)
        {
            var frame = new byte[5 + body.Length];
            frame[0] = type;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1), (uint)body.Length);
            body.CopyTo(frame, 5);
            s.Write(frame);
            s.Flush();
        }
        WriteFrame(stream, 0x01, Encoding.UTF8.GetBytes("p866_badcrc"));   // greeting

        var ping = new Ping { TimeClient = 0xBADC, TimeServer = 0 }.Encode();
        var corrupt = new byte[ping.Length + 4];
        ping.CopyTo(corrupt, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(corrupt.AsSpan(ping.Length), 0xDEADBEEFu);   // wrong CRC

        var log = new SyncWriter();
        var saved = Console.Out;
        bool closed = false;
        var watchdog = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Console.SetOut(TextWriter.Synchronized(log));
            WriteFrame(stream, 0x02, corrupt);
            var scratch = new byte[256];
            while (!closed && watchdog.Elapsed < TimeSpan.FromSeconds(10))
            {
                server.TickSim();
                try
                {
                    if (client.Client.Poll(10_000, System.Net.Sockets.SelectMode.SelectRead))
                    {
                        if (client.Client.Available == 0) closed = true;          // EOF — the server closed
                        else _ = stream.Read(scratch, 0, scratch.Length);        // drain server traffic
                    }
                }
                catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException) { closed = true; }
            }
        }
        finally { Console.SetOut(saved); }

        Assert.True(closed, "the server must drop a peer that sent a bad CRC (FedSrv.CPP:4600-4601)");
        Assert.Contains("corrupt message", log.Text);
        // The simulation keeps running after the drop (one bad peer is not fatal).
        server.TickSim();
    }

    /// <summary>Thread-safe capture sink (the server's own threads keep writing
    /// while the test reads).</summary>
    private sealed class SyncWriter : StringWriter
    {
        private readonly object _gate = new();
        public override void Write(char value) { lock (_gate) base.Write(value); }
        public override void Write(string? value) { lock (_gate) base.Write(value); }
        public override void Write(char[] buffer, int index, int count) { lock (_gate) base.Write(buffer, index, count); }
        public override void WriteLine(string? value) { lock (_gate) base.WriteLine(value); }
        public string Text { get { lock (_gate) return ToString(); } }
    }
}
