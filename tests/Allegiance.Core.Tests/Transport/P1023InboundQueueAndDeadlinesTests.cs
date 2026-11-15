using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Allegiance.Core;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;
using SrvHost = Allegiance.Server.ServerHost;

namespace Allegiance.Core.Tests.Transport;

/// <summary>
/// P1023 (R1 findings F3 + F6) — the inbound receive queue's per-event
/// accounting and the two connection-phase deadlines:
///   F3 — the queue was bounded in BYTES only, so a 4-byte CRC-only packet
///        reserved 4 B and a zero-length frame reserved nothing: tiny/empty
///        frames queued without limit with no logon (R1_REPORT.md §F3).
///        Fixed here by charging every queued event a fixed per-event overhead
///        (ServerHost.PerEventOverheadBytes), capping un-handled events per peer
///        (ServerHost.MaxQueuedEventsPerPeer) and rejecting a data frame shorter
///        than the packet CRC at the framing layer (TcpTransport), which is what
///        native does by deleting the connection on the failed CRC compare.
///   F6 — the greeting read had no absolute deadline, so a dribbled greeting
///        restarted the per-read socket timeout forever; and a greeted peer that
///        never logs on was kept indefinitely as long as it trickled valid
///        frames (R1_REPORT.md §4 "no pre-logon deadline").
/// </summary>
// Mutates shared statics (ServerHost byte/event bounds, TcpTransport idle
// timeout) and boots real sockets; serialize with the other users of those
// statics exactly as the P646/P315 suites do.
[Collection("LiveNetwork")]
public sealed class P1023InboundQueueTests
{
    private sealed class NullSite : ITransportSite
    {
        public void OnPeerJoined(ConnectionId id, string name) { }
        public void OnPeerLeft(ConnectionId id) { }
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data) { }
    }

    private static readonly byte[] CrcOnly = FedPacket.Pack(Array.Empty<byte[]>());

    /// <summary>F3 boundary — a CRC-only packet stays valid on the wire but now
    /// COSTS queue budget: at a 1000-byte per-peer budget each one reserves
    /// 4 + 96 B, so exactly ten fit and the rest are refused (before the fix all
    /// of them fitted, since each claimed 4 B).</summary>
    [Fact]
    public void Crc_only_packets_cost_queue_budget_instead_of_being_almost_free()
    {
        long budget = SrvHost.MaxBufferedBytesPerPeer;
        SrvHost.MaxBufferedBytesPerPeer = 1000;
        try
        {
            var host = new SrvHost();
            var peer = new ConnectionId(21);
            for (int i = 0; i < 40; i++) ((ITransportSite)host).OnReceive(peer, CrcOnly);

            long charge = CrcOnly.Length + SrvHost.PerEventOverheadBytes;
            int expected = (int)(1000 / charge);
            Assert.True(4 + SrvHost.PerEventOverheadBytes == charge, "a CRC-only packet must be charged its 4 payload bytes plus the overhead");
            Assert.Equal(expected, host.QueuedEventsForPeer(peer));

            int delivered = 0;
            host.Drain(_ => delivered++);
            Assert.Equal(Math.Min(expected, (int)SrvHost.DrainFairShareEvents), delivered);
            // The rest are deferred and still charged (they will drain later).
            Assert.Equal(expected - delivered, host.QueuedEventsForPeer(peer));
        }
        finally { SrvHost.MaxBufferedBytesPerPeer = budget; }
    }

    /// <summary>F3 — a ZERO-LENGTH frame now costs the per-event overhead, so it
    /// is refused by the byte budget like any other event (previously it reserved
    /// 0 bytes and was never refused at all).</summary>
    [Fact]
    public void Zero_length_frames_pay_the_per_event_overhead()
    {
        long budget = SrvHost.MaxBufferedBytesPerPeer;
        SrvHost.MaxBufferedBytesPerPeer = 1000;
        try
        {
            var host = new SrvHost();
            var peer = new ConnectionId(22);
            for (int i = 0; i < 40; i++) ((ITransportSite)host).OnReceive(peer, ReadOnlySpan<byte>.Empty);

            int expected = (int)(1000 / SrvHost.PerEventOverheadBytes);
            Assert.Equal(expected, host.QueuedEventsForPeer(peer));
            Assert.Equal(expected, host.Drain(_ => { }));

            // The refused frames left no accounting behind: drain empties it.
            host.Drain(_ => { });
            Assert.Equal(0, host.QueuedEventsForPeer(peer));
        }
        finally { SrvHost.MaxBufferedBytesPerPeer = budget; }
    }

    /// <summary>F3 boundary — the per-peer EVENT cap is exactly enforced: with a
    /// cap of 4 the first four frames queue and every later one is dropped, even
    /// though the byte budget is untouched (which is the point: the bound must
    /// not depend on frame size).</summary>
    [Fact]
    public void Per_peer_event_cap_drops_the_frame_after_the_boundary()
    {
        int cap = SrvHost.MaxQueuedEventsPerPeer;
        SrvHost.MaxQueuedEventsPerPeer = 4;
        try
        {
            var host = new SrvHost();
            var peer = new ConnectionId(23);
            for (int i = 0; i < 10; i++) ((ITransportSite)host).OnReceive(peer, CrcOnly);
            Assert.Equal(4, host.QueuedEventsForPeer(peer));

            int delivered = 0;
            host.Drain(_ => delivered++);
            Assert.Equal(4, delivered);
            Assert.Equal(0, host.QueuedEventsForPeer(peer));

            // A drained peer is served again (the drop released its charge).
            ((ITransportSite)host).OnReceive(peer, CrcOnly);
            Assert.Equal(1, host.QueuedEventsForPeer(peer));
            host.Drain(_ => { });
            Assert.Equal(0, host.QueuedEventsForPeer(peer));
        }
        finally { SrvHost.MaxQueuedEventsPerPeer = cap; }
    }

    /// <summary>F3 — the per-event charge is RELEASED with the event: once a
    /// peer's queued frames are drained, the same budget accepts them again (a
    /// leak here would shrink a legitimate peer's allowance over time).</summary>
    [Fact]
    public void Drained_events_release_both_their_bytes_and_their_event_slot()
    {
        long budget = SrvHost.MaxBufferedBytesPerPeer;
        SrvHost.MaxBufferedBytesPerPeer = 1000;
        try
        {
            var host = new SrvHost();
            var peer = new ConnectionId(24);
            Assert.Equal(10, SendTen(host, peer));
            host.Drain(_ => { });
            Assert.Equal(0, host.QueuedEventsForPeer(peer));

            // The reservation went back too: ten more fit, not zero.
            Assert.Equal(10, SendTen(host, peer));
            Assert.Equal(10, host.Drain(_ => { }));
            Assert.Equal(0, host.QueuedEventsForPeer(peer));
            Assert.Equal(1, host.BufferedPeerCount);   // one live peer, no leaked entries
            host.RemovePeerAccounting(peer);           // ReleasePeer with the peer
            Assert.Equal(0, host.BufferedPeerCount);
        }
        finally { SrvHost.MaxBufferedBytesPerPeer = budget; }

        static int SendTen(SrvHost host, ConnectionId peer)
        {
            for (int i = 0; i < 10; i++) ((ITransportSite)host).OnReceive(peer, CrcOnly);
            return host.QueuedEventsForPeer(peer);
        }
    }

    /// <summary>F3 — the legitimate case the new limits must never touch: a
    /// 200-player field at tick cadence (2 frames per peer per tick, the shape a
    /// live match produces) drains in full every tick with ZERO deferral, and no
    /// peer's queue ever approaches either bound. Numbers at the end are printed
    /// so the report can quote the headroom.</summary>
    [Fact]
    public void Two_hundred_peers_at_tick_cadence_never_touch_the_new_bounds()
    {
        var host = new SrvHost();
        const int peers = 200, framesPerTick = 2, ticks = 50;
        byte[] frame = FedPacket.Pack(new Ping { TimeClient = 7u }.Encode());
        int delivered = 0, maxQueued = 0;
        for (int t = 0; t < ticks; t++)
        {
            for (int p = 1; p <= peers; p++)
                for (int f = 0; f < framesPerTick; f++)
                    ((ITransportSite)host).OnReceive(new ConnectionId((uint)p), frame);
            delivered += host.Drain(_ => { });
            Assert.Equal(0, host.EventsDeferred);      // nothing carried, nothing dropped
            for (int p = 1; p <= peers; p++)
                maxQueued = Math.Max(maxQueued, host.QueuedEventsForPeer(new ConnectionId((uint)p)));
        }
        Assert.Equal(peers * framesPerTick * ticks, delivered);
        int peakPerTick = peers * framesPerTick;       // 400 events a tick
        Assert.True(peakPerTick < SrvHost.DrainBudgetEvents / 2,
            $"{peakPerTick} events/tick at 200 peers must stay well under the {SrvHost.DrainBudgetEvents}-event drain budget");
        Assert.True(maxQueued < SrvHost.MaxQueuedEventsPerPeer / 100,
            $"a legitimate peer queued {maxQueued} events — {SrvHost.MaxQueuedEventsPerPeer}-event cap, {SrvHost.PerEventOverheadBytes} B/event, {SrvHost.MaxBufferedBytesPerPeer}-byte budget");
        Console.WriteLine($"[p1023-legit] 200 peers x {framesPerTick} frames/tick x {ticks} ticks: delivered={delivered} deferred=0 maxQueuedPerPeer={maxQueued} (cap {SrvHost.MaxQueuedEventsPerPeer})");
    }
}

/// <summary>P1023 (R1-F3) — a data frame shorter than the 4-byte packet CRC is
/// rejected at the framing layer and the peer is dropped (native's message layer
/// has no length guard: PacketSize() underflows and the failed CRC compare
/// deletes the connection, FedSrv.CPP:4591-4603). A CRC-only packet (exactly 4
/// bytes, zero messages) stays valid and is delivered.</summary>
[Collection("LiveNetwork")]
public abstract class P1023ShortDataFrameTestsBase
{
    private readonly TransportMode _mode;
    protected P1023ShortDataFrameTestsBase(TransportMode mode) => _mode = mode;

    private sealed class Site : ITransportSite
    {
        public readonly List<ConnectionId> Joined = [];
        public readonly List<ConnectionId> Left = [];
        public readonly List<byte[]> Received = [];
        public void OnPeerJoined(ConnectionId id, string name) { lock (Joined) Joined.Add(id); }
        public void OnPeerLeft(ConnectionId id) { lock (Left) Left.Add(id); }
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data) { lock (Received) Received.Add(data.ToArray()); }
    }

    private static TcpClient Connect(ushort port)
    {
        var c = new TcpClient { NoDelay = true };
        c.Connect(IPAddress.Loopback, port);
        return c;
    }

    private static void WriteFrame(TcpClient c, byte type, byte[] body)
    {
        var frame = new byte[1 + 4 + body.Length];
        frame[0] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1), (uint)body.Length);
        body.CopyTo(frame, 5);
        c.GetStream().Write(frame, 0, frame.Length);
        c.GetStream().Flush();
    }

    private (Site Site, TcpTransport Transport, TcpClient Client) Open()
    {
        var site = new Site();
        var transport = new TcpTransport(site, _mode);
        transport.Host(0);
        var client = Connect(transport.BoundPort);
        WriteFrame(client, 0x01, Encoding.ASCII.GetBytes("P1023"));
        Assert.True(SpinWait.SpinUntil(() => site.Joined.Count == 1, 3000), "greeting must join the peer");
        return (site, transport, client);
    }

    /// <summary>The legit boundary: a 4-byte CRC-only packet is a VALID packet
    /// (FedPacket.Unpack accepts it) and is delivered; the peer stays connected.</summary>
    [Fact]
    public void Crc_only_data_frame_is_delivered_and_does_not_drop_the_peer()
    {
        var (site, transport, client) = Open();
        try
        {
            WriteFrame(client, 0x02, FedPacket.Pack(Array.Empty<byte[]>()));
            Assert.True(SpinWait.SpinUntil(() => site.Received.Count == 1, 3000), "a 4-byte CRC-only packet is a valid frame");
            Assert.Equal(FedPacket.CrcWidth, site.Received[0].Length);
            Assert.Empty(site.Left);
        }
        finally { client.Dispose(); transport.Shutdown(); }
    }

    /// <summary>F3 — a zero-length data frame cannot be a packet (no room for the
    /// CRC) and is a protocol error: the peer is dropped, nothing is delivered.</summary>
    [Fact]
    public void Zero_length_data_frame_drops_the_peer_as_a_bad_packet()
    {
        var (site, transport, client) = Open();
        try
        {
            WriteFrame(client, 0x02, []);
            Assert.True(SpinWait.SpinUntil(() => site.Left.Count == 1, 3000), "a zero-length data frame must drop the peer");
            Assert.Empty(site.Received);
            var counts = transport.DrainDisconnectCounts();
            Assert.True(counts.TryGetValue(DisconnectReason.BadCrc, out var n) && n == 1,
                $"the drop must be attributed to BadCrc (native OnBadCRC deletes the connection), got {string.Join(", ", counts.Select(kv => $"{kv.Key}={kv.Value}"))}");
        }
        finally { client.Dispose(); transport.Shutdown(); }
    }

    /// <summary>F3 — the same for a short (non-zero) frame: 1..3 bytes still leave
    /// no room for a CRC.</summary>
    [Fact]
    public void Short_data_frame_below_the_crc_width_drops_the_peer()
    {
        var (site, transport, client) = Open();
        try
        {
            WriteFrame(client, 0x02, [0xAA, 0xBB]);
            Assert.True(SpinWait.SpinUntil(() => site.Left.Count == 1, 3000), "a 2-byte data frame must drop the peer");
            Assert.Empty(site.Received);
        }
        finally { client.Dispose(); transport.Shutdown(); }
    }
}

/// <summary>P1023 — the short-frame rejection under thread-per-peer mode.</summary>
[Collection("LiveNetwork")]
public sealed class P1023ShortDataFrameThreadPerPeerTests : P1023ShortDataFrameTestsBase
{
    public P1023ShortDataFrameThreadPerPeerTests() : base(TransportMode.ThreadPerPeer) { }
}

/// <summary>P1023 — the short-frame rejection under the rig-default pool mode.</summary>
[Collection("LiveNetwork")]
public sealed class P1023ShortDataFramePoolTests : P1023ShortDataFrameTestsBase
{
    public P1023ShortDataFramePoolTests() : base(TransportMode.Pool) { }
}

/// <summary>P1023 (R1-F6 lead) — a greeted connection that never logs on is kept
/// as long as it keeps sending valid frames (the data-link idle bound only sees
/// silence), so a dedicated pre-logon deadline is required; and once it exists, a
/// peer that DOES log on is never cut by it. Boots a real server on an ephemeral
/// port and drives it with <c>TickSim</c>.</summary>
[Trait("Requires", "Artwork")]
[Collection("LiveNetwork")]
public sealed class P1023PreLogonTimeoutTests
{
    private sealed class Booted : IDisposable
    {
        public readonly Srv Server = new() { SpawnInitialMinersAtBoot = false };
        public readonly ushort Port;
        public Booted(int preLogonTimeoutSeconds)
        {
            Require.Artwork();
            var cfg = new ServerConfig
            {
                ArtPath = TestArtRoot.Value,
                StaticCore = "PCore001.igc",
                Port = 0,
                UpdatesPerSecond = 20,
                Transport = new TransportConfig { PreLogonTimeoutSeconds = preLogonTimeoutSeconds },
                Games = [new GameConfig { Name = "P1023", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], AutoStart = false }],
            };
            Server.Boot(cfg);
            Port = Server.BoundPort;
        }
        public SrvHost Host => (SrvHost)typeof(Srv).GetField("_host", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Server)!;
        public TcpTransport Transport => (TcpTransport)Host.Transport!;
        public void Dispose() => Server.Dispose();
    }

    private static TcpClient Greet(ushort port, string name)
    {
        var c = new TcpClient { NoDelay = true };
        c.Connect(IPAddress.Loopback, port);
        var body = Encoding.UTF8.GetBytes(name);
        var frame = new byte[5 + body.Length];
        frame[0] = 0x01;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1), (uint)body.Length);
        body.CopyTo(frame, 5);
        c.GetStream().Write(frame, 0, frame.Length);
        c.GetStream().Flush();
        return c;
    }

    /// <summary>Read one framed packet from the server (5-byte header + body);
    /// null on timeout/close. Used to prove a pre-logon peer is still SERVED (a
    /// PING echo comes back), not merely still listed.</summary>
    private static byte[]? ReadPacket(TcpClient c, int timeoutMs)
    {
        try
        {
            c.ReceiveTimeout = timeoutMs;
            var ns = c.GetStream();
            var hdr = new byte[5];
            for (int off = 0; off < 5;)
            {
                int n = ns.Read(hdr, off, 5 - off);
                if (n <= 0) return null;
                off += n;
            }
            int len = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(1));
            if (len < 0 || len > 65536) return null;
            var body = new byte[len];
            for (int off = 0; off < len;)
            {
                int n = ns.Read(body, off, len - off);
                if (n <= 0) return null;
                off += n;
            }
            return body;
        }
        catch (IOException) { return null; }
    }

    private static void WriteData(TcpClient c, byte[] packet)
    {
        var frame = new byte[5 + packet.Length];
        frame[0] = 0x02;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1), (uint)packet.Length);
        packet.CopyTo(frame, 5);
        try { c.GetStream().Write(frame, 0, frame.Length); c.GetStream().Flush(); } catch (IOException) { }
    }

    /// <summary>R1_F6 lead — with the pre-logon deadline OFF (0), a greeted peer
    /// that sends one valid frame every 100 ms is still connected after ~1.2 s
    /// even though the data-link idle bound is 200 ms: every frame restarts it,
    /// and nothing else in the port requires a logon. This is why the lead is
    /// real and why only a dedicated deadline (native has none) can end it.</summary>
    [SkippableFact]
    public void Greeted_peer_that_never_logs_on_survives_indefinitely_without_the_deadline()
    {
        var idle = TcpTransport.DataLinkIdleTimeout;
        using var booted = new Booted(preLogonTimeoutSeconds: 0);
        try
        {
            // Bind the idle bound AFTER boot (the boot binding writes the config
            // default, 30 s): a 200 ms idle window makes "every frame restarts the
            // bound" observable inside the test's 1.2 s.
            TcpTransport.DataLinkIdleTimeout = TimeSpan.FromMilliseconds(200);
            using var client = Greet(booted.Port, "SQUATTER");
            // A CRC-only packet is a VALID frame (no messages, no reply): the
            // trickle that restarts the idle bound without producing leftovers the
            // final round-trip check could accidentally read.
            var trickle = FedPacket.Pack(Array.Empty<byte[]>());
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1200)
            {
                WriteData(client, trickle);
                booted.Server.TickSim();
                Thread.Sleep(100);
            }
            Assert.Equal(1, booted.Server.PreLogonPendingCount);      // still a pre-logon peer
            Assert.Equal(1, booted.Transport.LiveConnectionCount());  // ...and still connected
            Assert.True(booted.Transport.DrainDisconnectCounts().Count == 0, "no connection was dropped by any deadline");

            // ...and still SERVED: one PING round-trips, i.e. the trickle really
            // kept the 200 ms idle bound from ever expiring.
            WriteData(client, FedPacket.Pack(new Ping { TimeClient = 1u }.Encode()));
            byte[]? reply = null;
            var until = System.Diagnostics.Stopwatch.StartNew();
            while (reply is null && until.ElapsedMilliseconds < 3000)
            {
                booted.Server.TickSim();      // the dispatcher answers on the tick thread
                reply = ReadPacket(client, 200);
                Thread.Sleep(20);
            }
            Assert.NotNull(reply);
            Assert.Equal(Ping.Fmid, FedMessageHeader.Read(reply!).Fmid);
        }
        finally { TcpTransport.DataLinkIdleTimeout = idle; }
    }

    /// <summary>F6 lead fix — with the pre-logon deadline ON (1 s here), the same
    /// trickling peer is dropped once its logon window elapses, with the distinct
    /// PreLogonTimeout reason.</summary>
    [SkippableFact]
    public void Greeted_peer_that_never_logs_on_is_dropped_at_the_pre_logon_deadline()
    {
        var idle = TcpTransport.DataLinkIdleTimeout;
        using var booted = new Booted(preLogonTimeoutSeconds: 1);
        try
        {
            TcpTransport.DataLinkIdleTimeout = TimeSpan.FromMilliseconds(200);
            using var client = Greet(booted.Port, "SQUATTER");
            var packet = FedPacket.Pack(new Ping { TimeClient = 1u }.Encode());
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // Tick from the start: the peer only enters the pre-logon set when its
            // PeerJoinedEvt drains. Run the full window (or until the drop lands).
            while (sw.ElapsedMilliseconds < 2500)
            {
                WriteData(client, packet);
                booted.Server.TickSim();
                if (booted.Server.PreLogonPendingCount == 0 && booted.Transport.LiveConnectionCount() == 0) break;
                Thread.Sleep(50);
            }
            Assert.Equal(0, booted.Server.PreLogonPendingCount);
            Assert.True(SpinWait.SpinUntil(() => booted.Transport.LiveConnectionCount() == 0, 2000),
                "the connection must be closed, not merely forgotten");
            Assert.True(booted.Transport.DrainDisconnectCounts()
                .TryGetValue(DisconnectReason.PreLogonTimeout, out var n) && n == 1,
                "the drop must be attributed to the pre-logon deadline");
        }
        finally { TcpTransport.DataLinkIdleTimeout = idle; }
    }

    /// <summary>F6 lead fix, legit side — a peer that logs on immediately is
    /// removed from the pre-logon set and is NOT dropped, even though the (1 s)
    /// deadline elapses during the test.</summary>
    [SkippableFact]
    public void Peer_that_logs_on_is_never_dropped_by_the_pre_logon_deadline()
    {
        using var booted = new Booted(preLogonTimeoutSeconds: 1);
        using var client = Greet(booted.Port, "PLAYER");
        var logon = new LogonRequest
        {
            CharacterName = "PLAYER", ZoneTicket = null, CDKey = "", MissionPassword = "",
            FedsrvVer = LogonRequest.MsgVer, Time = 1u, DwCookie = uint.MaxValue,
            CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0, SteamID = 1ul, DrmHash = "p1023",
        };
        WriteData(client, FedPacket.Pack(logon.Encode()));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 2200)
        {
            booted.Server.TickSim();
            Thread.Sleep(50);
        }
        Assert.Equal(1, booted.Transport.LiveConnectionCount());   // still connected
        Assert.Equal(0, booted.Server.PreLogonPendingCount);       // its logon cleared the clock
        Assert.False(booted.Transport.DrainDisconnectCounts()
            .ContainsKey(DisconnectReason.PreLogonTimeout), "a logged-on peer must never hit the pre-logon deadline");
    }
}
