using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text;
using Allegiance.Core.Transport;
using Allegiance.Server;

namespace Allegiance.Core.Tests.Transport;

/// <summary>
/// P645 — transport send-side hardening regression tests.
/// <para>
/// Defect 1 (partial-write safety): the old drain path POPPED the head frame
/// out of the queue and only then did a blocking <c>Write</c>; on a timeout the
/// P619c human branch neither restored the frame nor closed the connection, so
/// a half-sent frame was silently dropped and the next enqueue resumed on the
/// same socket — corrupting TCP framing (the next frame's bytes completed the
/// previous frame's declared body). P645 promotes the head into an in-flight
/// frame + byte offset that is never abandoned until fully sent; a blocking
/// write that times out with unknown progress CLOSES the connection
/// (SendTimeout) instead of continuing on a possibly-corrupt stream.
/// </para>
/// <para>
/// Defect 2 (memory bound): the overflow-disconnect is exempt for humans and
/// the shedder only scans the stream lane, so a "human" flooding guaranteed
/// traffic (e.g. PING replies) grew its ordered queue without bound. P645 adds
/// an ABSOLUTE <see cref="TcpTransport.HardCapBytes"/> ceiling applied to every
/// peer, humans included, disconnecting with the new
/// <see cref="DisconnectReason.QueueHardCap"/> reason.
/// </para>
/// </summary>
[Collection("LiveNetwork")]
public abstract class TcpTransportSendHardeningTestsBase
{
    private readonly TransportMode _mode;
    protected TcpTransportSendHardeningTestsBase(TransportMode mode) => _mode = mode;

    private TcpTransport NewHost(Site site, bool tinyPool = false)
        => new TcpTransport(site, _mode, poolWorkers: tinyPool ? 2 : 0);

    private sealed class Site : ITransportSite
    {
        public readonly List<ConnectionId> Joined = new();
        public readonly List<ConnectionId> Left = new();
        public void OnPeerJoined(ConnectionId id, string name) { lock (Joined) Joined.Add(id); }
        public void OnPeerLeft(ConnectionId id) { lock (Left) Left.Add(id); }
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data) { }
        public bool HasLeft(ConnectionId id, int ms)
        {
            lock (Left) { if (Left.Contains(id)) return true; }
            if (ms <= 0) return false;
            var deadline = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(5);
                lock (Left) { if (Left.Contains(id)) return true; }
            }
            return false;
        }
    }

    /// <summary>Connect a raw loopback peer, speak the Greet frame, and return
    /// its host-assigned id plus the client TcpClient. With
    /// <paramref name="tinyReceiveWindow"/> the OS window stays ~1 KiB so the
    /// server-side writer genuinely blocks / would-blocks against a full buffer —
    /// the precondition that makes partial-write and timeout behaviour reachable.</summary>
    private static (ConnectionId Id, TcpClient Client) ConnectRawPeer(
        TcpTransport host, Site site, string name, bool tinyReceiveWindow = false)
    {
        var raw = new TcpClient();
        raw.NoDelay = true;
        if (tinyReceiveWindow) raw.ReceiveBufferSize = 1024;
        raw.Connect("127.0.0.1", host.BoundPort);
        var ns = raw.GetStream();
        var body = Encoding.UTF8.GetBytes(name);
        var greet = new byte[1 + 4 + body.Length];
        greet[0] = 0x01;
        BinaryPrimitives.WriteUInt32LittleEndian(greet.AsSpan(1), (uint)body.Length);
        body.CopyTo(greet.AsSpan(5));
        ns.Write(greet);
        ns.Flush();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            lock (site.Joined) { if (site.Joined.Count > 0) return (site.Joined[^1], raw); }
            if (DateTime.UtcNow > deadline) throw new TimeoutException("no OnPeerJoined for raw peer");
            Thread.Sleep(5);
        }
    }

    /// <summary>Read one complete length-prefixed Data frame from the client
    /// stream and return its body. The test client's socket is blocking, so a
    /// timeout is a real failure (no bytes within ReadTimeoutMs).</summary>
    private static byte[] ReadDataFrame(NetworkStream ns, int readTimeoutMs = 5000)
    {
        ns.ReadTimeout = readTimeoutMs;
        var hdr = new byte[5];
        ReadExactly(ns, hdr);
        if (hdr[0] != 0x02) throw new Exception($"expected Data frame, got type {hdr[0]}");
        int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(1));
        var body = new byte[len];
        ReadExactly(ns, body);
        return body;
    }

    private static void ReadExactly(NetworkStream s, byte[] dst)
    {
        int off = 0;
        while (off < dst.Length)
        {
            int n = s.Read(dst, off, dst.Length - off);
            if (n <= 0) throw new EndOfStreamException("peer closed mid-frame");
            off += n;
        }
    }

    [Fact]
    public void Pooled_data_frames_send_only_valid_bytes_and_own_their_copy()
    {
        var site = new Site();
        var host = NewHost(site);
        try
        {
            host.Host(0);
            var (_, client) = ConnectRawPeer(host, site, "POOL-COPY");
            using (client)
            {
                ConnectionId id;
                lock (site.Joined) id = site.Joined[^1];
                for (int round = 0; round < 4; round++)
                {
                    var expected = new List<byte[]>();
                    foreach (int length in new[] { 1, 7, 37, 129, 7001, 5, 32769 })
                    {
                        var body = Enumerable.Range(0, length).Select(i => (byte)(i + round)).ToArray();
                        expected.Add(body.ToArray());
                        host.Send(id, body, guaranteed: true);
                        Array.Fill(body, (byte)0xEE); // caller storage may change immediately
                    }
                    foreach (var body in expected) Assert.Equal(body, ReadDataFrame(client.GetStream()));
                }
            }
        }
        finally { host.Shutdown(); }
    }

    /// <summary>⭐ A tiny-window, non-reading peer forces partial / would-block
    /// writes; the receiver must still reassemble every frame byte-exact, in
    /// order, with nothing lost. This pins the P645 in-flight/offset machinery:
    /// a partially-sent frame is resumed (never abandoned), so framing stays
    /// intact and not one byte is dropped or reordered.</summary>
    [Fact]
    public void Partial_writes_reassemble_every_frame_byte_exact_in_order()
    {
        var site = new Site();
        var host = NewHost(site);
        try
        {
            host.Host(0);
            var (id, client) = ConnectRawPeer(host, site, "PART", tinyReceiveWindow: true);
            var ns = client.GetStream();

            // 8 x 7 KiB of distinct content, sent with the client NOT yet reading
            // so the writer must dribble them through the tiny window (would-block
            // between partial sends).
            var bodies = new byte[8][];
            for (int i = 0; i < bodies.Length; i++)
                bodies[i] = Enumerable.Range(0, 7000).Select(_ => (byte)(i + 1)).ToArray();
            foreach (var b in bodies) host.Send(id, b, guaranteed: true);

            // Drain slowly and verify byte-exact in-order reassembly.
            foreach (var expected in bodies)
                Assert.Equal(expected, ReadDataFrame(ns));

            client.Dispose();
        }
        finally { host.Shutdown(); }
    }

    /// <summary>⭐ A blocking write that times out with UNKNOWN progress must
    /// CLOSE the connection (reason <see cref="DisconnectReason.SendTimeout"/>)
    /// rather than continue on a possibly-corrupt stream. Only applies to the
    /// dedicated (blocking) writer — in pool mode sends are non-blocking, so
    /// there is no blocking-write timeout at all. On the pre-P645 code a human
    /// timeout dropped the frame and kept writing on the same socket, so this
    /// test FAILS there (no disconnect) and passes on P645.</summary>
    [Fact]
    public void Blocking_write_timeout_closes_the_connection()
    {
        if (_mode == TransportMode.Pool) return;   // pool uses non-blocking sends (no timeout path)
        var oldS = TcpTransport.SendTimeoutMs;
        TcpTransport.SendTimeoutMs = 200;          // fast, deterministic timeout
        var site = new Site();
        var host = NewHost(site);
        try
        {
            host.Host(0);
            var (id, client) = ConnectRawPeer(host, site, "TIMEOUT", tinyReceiveWindow: true);
            host.SetPeerHuman(id, true);           // even a HUMAN is closed on a timed-out blocking write

            var big = new byte[1 << 16];           // 64 KiB guaranteed — far bigger than the tiny window+buffer
            host.Send(id, big, guaranteed: true);  // the dedicated writer blocks in Socket.Send...

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!site.HasLeft(id, 0) && DateTime.UtcNow < deadline) Thread.Sleep(10);
            Assert.True(site.HasLeft(id, 0),
                "a blocking write that times out with unknown progress must close the connection");
            var counts = host.DrainDisconnectCounts();
            Assert.True(counts.TryGetValue(DisconnectReason.SendTimeout, out var n) && n >= 1,
                $"expected SendTimeout, got {string.Join(",", counts.Select(kv => $"{kv.Key}={kv.Value}"))}");
            client.Dispose();
        }
        finally
        {
            host.Shutdown();
            TcpTransport.SendTimeoutMs = oldS;
        }
    }

    /// <summary>⭐ A HUMAN peer whose ordered (guaranteed, non-coalesced) queue
    /// exceeds the ABSOLUTE <see cref="TcpTransport.HardCapBytes"/> ceiling is
    /// disconnected with the new <see cref="DisconnectReason.QueueHardCap"/> —
    /// the human exemption covers only the timed overflow rule, not this hard
    /// memory bound. On pre-P645 code there is no hard cap, so a flooded human
    /// (overflow exempt, long human grace) is never disconnected here — the test
    /// fails there.</summary>
    [Fact]
    public void Human_whose_queue_exceeds_the_hard_cap_is_disconnected_with_QueueHardCap()
    {
        var oC = TcpTransport.CapacityBytes;
        var oH = TcpTransport.HardCapBytes;
        TcpTransport.CapacityBytes = 64 * 1024;
        TcpTransport.HardCapBytes = 512 * 1024;
        var site = new Site();
        var host = NewHost(site);
        try
        {
            host.Host(0);
            var (id, client) = ConnectRawPeer(host, site, "HCAP", tinyReceiveWindow: true);
            host.SetPeerHuman(id, true);   // a human: the timed overflow rule is exempt and the
                                           // human no-progress grace (60 s) is far away, so ONLY
                                           // the absolute hard cap can disconnect this peer.

            var frame = new byte[4096];
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!site.HasLeft(id, 0) && DateTime.UtcNow < deadline)
            {
                host.Send(id, frame, guaranteed: true, coalesceKey: 0);
                Thread.Sleep(2);
            }
            Assert.True(site.HasLeft(id, 0),
                "a human whose ordered queue exceeds the hard cap must be disconnected");
            var counts = host.DrainDisconnectCounts();
            Assert.True(counts.TryGetValue(DisconnectReason.QueueHardCap, out var n) && n >= 1,
                $"expected QueueHardCap, got {string.Join(",", counts.Select(kv => $"{kv.Key}={kv.Value}"))}");
            client.Dispose();
        }
        finally
        {
            host.Shutdown();
            TcpTransport.CapacityBytes = oC;
            TcpTransport.HardCapBytes = oH;
        }
    }

    /// <summary>⭐ A HUMAN peer that is making write progress (its socket keeps
    /// accepting bytes) while staying UNDER the hard cap is NOT disconnected —
    /// P619c's intent is preserved: a slow-but-alive player is never cut, only a
    /// genuinely overflowing or dead one.</summary>
    [Fact]
    public void Slow_but_progressing_human_under_the_cap_is_not_disconnected()
    {
        var oC = TcpTransport.CapacityBytes;
        var oH = TcpTransport.HardCapBytes;
        TcpTransport.CapacityBytes = 64 * 1024;
        TcpTransport.HardCapBytes = 256 * 1024;
        var site = new Site();
        var host = NewHost(site);
        try
        {
            host.Host(0);
            var (id, client) = ConnectRawPeer(host, site, "SLOW");
            host.SetPeerHuman(id, true);
            var ns = client.GetStream();

            // 12 x 16 KiB = 192 KiB of guaranteed ordered traffic, reading each
            // frame back as it arrives (bytes flow, liveness clock stays fresh),
            // so the queue stays far under the 256 KiB hard cap.
            for (int i = 0; i < 12; i++)
            {
                var b = Enumerable.Range(0, 16384).Select(_ => (byte)i).ToArray();
                host.Send(id, b, guaranteed: true);
                Assert.Equal(b, ReadDataFrame(ns));
            }
            Assert.False(site.HasLeft(id, 0),
                "a progressing human under the cap must not be disconnected");
            client.Dispose();
        }
        finally
        {
            host.Shutdown();
            TcpTransport.CapacityBytes = oC;
            TcpTransport.HardCapBytes = oH;
        }
    }
}

/// <summary>P645 send-hardening suite under thread-per-peer (default) mode.</summary>
[Collection("LiveNetwork")]
public class TcpTransportSendHardeningThreadPerPeerTests : TcpTransportSendHardeningTestsBase
{
    public TcpTransportSendHardeningThreadPerPeerTests() : base(TransportMode.ThreadPerPeer) { }
}

/// <summary>P645 send-hardening suite under pool (fixed send-worker pool) mode.</summary>
[Collection("LiveNetwork")]
public class TcpTransportSendHardeningPoolTests : TcpTransportSendHardeningTestsBase
{
    public TcpTransportSendHardeningPoolTests() : base(TransportMode.Pool) { }
}

/// <summary>
/// P645 — PING reply throttle (scope 3). The native client pings at most once
/// per ~6 s (clintlib.cpp:2052-2085 CheckServerLag, maxLagCheck=6.0f), so a
/// 1 s minimum reply interval is huge headroom for legit traffic; anything
/// faster is a flood whose excess PINGs are DROPPED and counted, not answered,
/// so a PING storm cannot amplify into an unbounded guaranteed-reply queue.
/// Pure unit test (no sockets) against <see cref="ServerHost.ShouldAnswerPing"/>.
/// </summary>
public sealed class TcpTransportPingThrottleTests
{
    [Fact]
    public void Ping_flood_over_the_reply_interval_is_dropped_and_counted()
    {
        var old = ServerHost.MinPingReplyIntervalMs;
        try
        {
            // 60 s window: every rapid call falls inside it, so only the first
            // is answered and the other 49 are dropped + counted.
            ServerHost.MinPingReplyIntervalMs = 60_000;
            var host = new ServerHost();
            var from = new ConnectionId(7);
            for (int i = 0; i < 50; i++)
                host.ShouldAnswerPing(from);

            Assert.Equal(1, (int)host.PingsAnswered);    // only the first answered
            Assert.Equal(49, (int)host.PingsThrottled);  // the rest dropped + counted
        }
        finally { ServerHost.MinPingReplyIntervalMs = old; }
    }
}
