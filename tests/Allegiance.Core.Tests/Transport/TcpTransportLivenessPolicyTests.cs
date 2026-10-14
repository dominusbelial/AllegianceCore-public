using System.Buffers.Binary;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Allegiance.Core.Transport;

namespace Allegiance.Core.Tests.Transport;

/// <summary>
/// P611 — the overflow policy's liveness rule. P606's anti-zombie rule
/// disconnected a peer whose queue was <b>continuously non-empty</b> for the
/// whole OverflowTimeout, on the theory that meant the socket had stopped
/// accepting writes. But under a sustained battle the server enqueues per-tick
/// frames faster than a pool worker fully drains a peer, so a <b>healthy</b>
/// peer's queue is never momentarily empty for 5 s even though bytes flow
/// continuously — hundreds of peers tripped that false "never drained" signal
/// together (the 2026-10-11 15:50 mass disconnect on the 221 server).
///
/// P611 replaces that with a real liveness signal: a peer is disconnected only
/// when the socket has made <b>no write progress</b> (no bytes accepted by the
/// OS) for a timeout. A peer that keeps accepting bytes is never "stuck",
/// however full its queue. The byte-cap rule (over-capacity guaranteed backlog)
/// is kept, and the disconnect reasons are split into distinct values:
/// <see cref="DisconnectReason.QueueOverCapacity"/>,
/// <see cref="DisconnectReason.NoWriteProgress"/> and
/// <see cref="DisconnectReason.SendTimeout"/>.
///
/// Parameterized over both transport modes (thread-per-peer and pool).
/// </summary>
[Collection("LiveNetwork")]
public abstract class TcpTransportLivenessPolicyTestsBase
{
    private readonly TransportMode _mode;
    protected TcpTransportLivenessPolicyTestsBase(TransportMode mode) => _mode = mode;

    private static readonly FieldInfo PeersField =
        typeof(TcpTransport).GetField("_peers", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new Exception("_peers field not found");

    protected sealed class Site : ITransportSite
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

    private TcpTransport NewHost(Site site) => new(site, _mode, poolWorkers: 3);

    protected static (ConnectionId Id, TcpClient Client) ConnectRawPeer(
        TcpTransport host, Site site, string name, int receiveWindowBytes = -1)
    {
        int before;
        lock (site.Joined) before = site.Joined.Count;
        var raw = new TcpClient();
        raw.NoDelay = true;
        if (receiveWindowBytes > 0) raw.ReceiveBufferSize = receiveWindowBytes;
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
            lock (site.Joined) { if (site.Joined.Count > before) return (site.Joined[before], raw); }
            if (DateTime.UtcNow > deadline) throw new TimeoutException("no OnPeerJoined");
            Thread.Sleep(5);
        }
    }

    /// <summary>Set BOTH liveness seams short for a fast deterministic test and
    /// restore them afterwards (friend access via InternalsVisibleTo).</summary>
    private static IDisposable ShortenPolicy()
    {
        var oT = TcpTransport.OverflowTimeout;
        var oN = TcpTransport.NoWriteProgressTimeout;
        var oC = TcpTransport.CapacityBytes;
        TcpTransport.OverflowTimeout = TimeSpan.FromMilliseconds(500);
        TcpTransport.NoWriteProgressTimeout = TimeSpan.FromMilliseconds(500);
        TcpTransport.CapacityBytes = 8L * 1024 * 1024;
        return new Restore(() =>
        {
            TcpTransport.OverflowTimeout = oT;
            TcpTransport.NoWriteProgressTimeout = oN;
            TcpTransport.CapacityBytes = oC;
        });
    }

    /// <summary>Set ONLY the no-write-progress seam short (byte-cap timer left
    /// long so the byte-cap rule cannot interfere with a NoWriteProgress test).</summary>
    private static IDisposable ShortenNoWriteProgressOnly()
    {
        var oN = TcpTransport.NoWriteProgressTimeout;
        var oT = TcpTransport.OverflowTimeout;
        TcpTransport.NoWriteProgressTimeout = TimeSpan.FromMilliseconds(400);
        TcpTransport.OverflowTimeout = TimeSpan.FromSeconds(5);
        return new Restore(() =>
        {
            TcpTransport.NoWriteProgressTimeout = oN;
            TcpTransport.OverflowTimeout = oT;
        });
    }

    /// <summary>Set ONLY the byte-cap timer short (no-write-progress left long
    /// so the liveness rule cannot steal a QueueOverCapacity test).</summary>
    private static IDisposable ShortenOverCapacityOnly()
    {
        var oT = TcpTransport.OverflowTimeout;
        var oN = TcpTransport.NoWriteProgressTimeout;
        var oC = TcpTransport.CapacityBytes;
        TcpTransport.OverflowTimeout = TimeSpan.FromMilliseconds(400);
        TcpTransport.NoWriteProgressTimeout = TimeSpan.FromSeconds(5);
        TcpTransport.CapacityBytes = 64 * 1024;
        return new Restore(() =>
        {
            TcpTransport.OverflowTimeout = oT;
            TcpTransport.NoWriteProgressTimeout = oN;
            TcpTransport.CapacityBytes = oC;
        });
    }

    private sealed class Restore : IDisposable
    {
        private readonly Action _a; public Restore(Action a) => _a = a;
        public void Dispose() => _a();
    }

    /// <summary>P611 test control: a stream that lets a test dictate exactly how
    /// the writer's <c>Write</c> behaves, so the overflow policy can be unit
    /// tested deterministically without OS socket-buffer timing (which on
    /// loopback randomly RSTs a never-read peer, obscuring the policy reason).
    /// Swapped into a PeerSender's <c>_stream</c> via reflection; the real
    /// socket stays open for the read/disposal paths.</summary>
    protected sealed class ControlledStream : NetworkStream
    {
        public enum Behavior { AcceptFast, ThrottleMs, BlockForever, TimeoutAfterMs }
        private readonly Behavior _behavior;
        private readonly int _delayMs;
        private readonly ManualResetEventSlim _release = new(false);
        public int BytesWritten;

        public ControlledStream(Socket socket, Behavior behavior, int delayMs = 0)
            : base(socket, ownsSocket: false)
        {
            _behavior = behavior;
            _delayMs = delayMs;
        }

        /// <summary>Unblock a BlockForever write (called on teardown / dispose).</summary>
        public void Release() => _release.Set();

        public override void Write(byte[] buffer, int offset, int count)
        {
            switch (_behavior)
            {
                case Behavior.AcceptFast:
                    BytesWritten += count;
                    return;
                case Behavior.ThrottleMs:
                    Thread.Sleep(_delayMs);
                    BytesWritten += count;
                    return;
                case Behavior.BlockForever:
                    _release.Wait();
                    return;
                case Behavior.TimeoutAfterMs:
                    Thread.Sleep(_delayMs);
                    throw new SocketException((int)SocketError.TimedOut);
            }
        }

        public override void Flush() { }
        public override void Close() { Release(); base.Close(); }
        protected override void Dispose(bool disposing)
        {
            Release();
            base.Dispose(disposing);
        }
    }

    /// <summary>Swap a PeerSender's writable <c>_stream</c> for the given
    /// controlled stream (P611 test control).</summary>
    protected static object SwapStream(object ps, ControlledStream stream)
    {
        var f = ps.GetType().GetField("_stream", BindingFlags.Instance | BindingFlags.NonPublic)!;
        f.SetValue(ps, stream);
        return ps;
    }

    /// <summary>Fetch a peer's PeerSender object (for the controlled-stream
    /// tests).</summary>
    protected static object PeerSenderFor(TcpTransport host, ConnectionId id)
    {
        var peers = (IDictionary)PeersField.GetValue(host)!;
        return peers[id]!;
    }

    /// <summary>The real underlying socket of a peer's sender, to back a
    /// <see cref="ControlledStream"/>.</summary>
    protected static Socket PeerSocket(object ps)
    {
        var tcpField = ps.GetType().GetField("_tcpClient", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return ((TcpClient)tcpField.GetValue(ps)!).Client;
    }

    /// <summary>P645 — MODE-AWARE deterministic write control. Thread-per-peer
    /// writers transmit via <c>_stream</c>, so the fake
    /// <see cref="ControlledStream"/> is swapped into <c>_stream</c> (unchanged
    /// P611 behaviour). POOL workers transmit via the non-blocking
    /// <c>Socket.Send</c> path, so the same behaviour is instead installed as a
    /// <c>SendOverride</c> fake send-sink on the peer's sender. Both return an
    /// <c>Action</c> that releases a BlockForever write (safe to call on either
    /// path, e.g. teardown).</summary>
    protected Action InstallWriteControl(object ps, ControlledStream.Behavior behavior, int delayMs = 0)
    {
        if (_mode == TransportMode.Pool)
        {
            var gate = new ManualResetEventSlim(false);
            var field = ps.GetType().GetField("SendOverride", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Func<byte[], int, int, TcpTransport.SendOutcome> fn = (_, _, count) =>
            {
                switch (behavior)
                {
                    case ControlledStream.Behavior.AcceptFast:
                        return TcpTransport.SendOutcome.Sent(count);
                    case ControlledStream.Behavior.ThrottleMs:
                        Thread.Sleep(delayMs);
                        return TcpTransport.SendOutcome.Sent(count);
                    case ControlledStream.Behavior.BlockForever:
                        gate.Wait();
                        return TcpTransport.SendOutcome.Sent(count);
                    case ControlledStream.Behavior.TimeoutAfterMs:
                        Thread.Sleep(delayMs);
                        return TcpTransport.SendOutcome.TimedOut();
                    default:
                        return TcpTransport.SendOutcome.Sent(count);
                }
            };
            field.SetValue(ps, fn);
            return gate.Set;
        }
        else
        {
            var ctrl = new ControlledStream(PeerSocket(ps), behavior, delayMs);
            SwapStream(ps, ctrl);
            return ctrl.Release;
        }
    }

    private static void ReadExactly(Stream s, Span<byte> dst)
    {
        int off = 0;
        while (off < dst.Length)
        {
            int n = s.Read(dst.Slice(off, dst.Length - off));
            if (n <= 0) throw new EndOfStreamException("no data");
            off += n;
        }
    }

    /// <summary>Read and discard exactly one framed Data packet, or return false
    /// if none is available right now.</summary>
    protected static bool TryReadOneFrame(NetworkStream ns, byte[] buf)
    {
        if (!ns.DataAvailable) return false;
        ReadExactly(ns, buf.AsSpan(0, 5));
        if (buf[0] != 0x02) return false;
        uint len = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(1));
        if (len > buf.Length - 5) return false;
        ReadExactly(ns, buf.AsSpan(5, (int)len));
        return true;
    }

    /// <summary>
    /// THE core failing-first test. A peer whose socket <b>keeps accepting
    /// bytes</b> but whose queue is <b>never empty</b> is NOT disconnected —
    /// this is the exact case P606's "continuously non-empty for the timeout"
    /// rule misclassified and mass-disconnected on 2026-10-11.
    ///
    /// Construction: the victim reads slowly (one frame per ~25 ms), so its
    /// socket genuinely accepts bytes the whole time (steady write progress),
    /// but its kernel buffer stays full and the producer enqueues far faster
    /// than one frame/25 ms — so the send queue is <b>never momentarily
    /// empty</b>. Today's code sees "never drained for the window" and
    /// disconnects (this test fails). P611 sees write progress every ~25 ms and
    /// keeps the peer (this test passes).
    /// </summary>
    [Fact]
    public void Peer_whose_socket_keeps_accepting_bytes_is_never_disconnected_even_if_queue_never_empties()
    {
        using var _ = ShortenPolicy();
        var site = new Site();
        var host = NewHost(site);
        try
        {
            host.Host(0);
            // Large byte cap so this test isolates LIVENESS (never-empty-but-
            // accepting) from the byte-cap rule: the producer must never trip
            // QueueOverCapacity, only the old never-drained rule (today) or the
            // new no-write-progress rule (fixed).
            var oldC = TcpTransport.CapacityBytes;
            TcpTransport.CapacityBytes = 1L << 30; // 1 GiB
            try
            {
                var (id, client) = ConnectRawPeer(host, site, "DRAIN");

                // Swap in a stream whose Write ACCEPTS bytes, but slowly (one
                // frame / 10 ms) — real, continuous write progress (so the new
                // liveness rule can never fire), while the producer outpaces it
                // so the queue is NEVER momentarily empty. Deterministic model of
                // the 2026-10-11 case: a healthy peer whose queue never drains
                // under sustained load but whose socket keeps accepting bytes.
                var ps = PeerSenderFor(host, id);
                var release = InstallWriteControl(ps, ControlledStream.Behavior.ThrottleMs, delayMs: 10);

                // Producer: outpacing the ~100/s drain keeps the queue non-empty,
                // but far under the (large) byte cap — only liveness can judge it.
                var producerStop = new ManualResetEventSlim(false);
                var frame = new byte[64];
                var producer = new Thread(() =>
                {
                    while (!producerStop.IsSet)
                    {
                        for (int i = 0; i < 200; i++) host.Send(id, frame, guaranteed: false, coalesceKey: 0);
                        Thread.Sleep(1);
                    }
                }) { IsBackground = true };
                producer.Start();

                // Run for well over the (shortened) policy window. The socket accepts
                // bytes throughout, so a healthy peer must NEVER be disconnected.
                Thread.Sleep(TimeSpan.FromMilliseconds(2500));
                producerStop.Set();
                release();

                Assert.False(site.HasLeft(id, 0),
                    "a peer whose socket keeps accepting bytes must NOT be disconnected, even though its queue never empties (P606's never-drained rule falsely dropped these)");
                client.Dispose();
            }
            finally { TcpTransport.CapacityBytes = oldC; }
        }
        finally { host.Shutdown(); }
    }

    /// <summary>Failing-first, the mirror case: a peer whose socket accepts
    /// NOTHING for the (shortened) timeout IS disconnected, and with the
    /// distinct <see cref="DisconnectReason.NoWriteProgress"/> reason — the
    /// anti-zombie intent of the old (b) rule, now grounded in real liveness.</summary>
    [Fact]
    public void Peer_accepting_nothing_for_the_timeout_is_disconnected_with_NoWriteProgress()
    {
        using var _ = ShortenNoWriteProgressOnly();
        var site = new Site();
        var host = NewHost(site);
        try
        {
            host.Host(0);
            var (id, client) = ConnectRawPeer(host, site, "DEAD");

            // Swap in a stream whose Write accepts NOTHING (blocks forever), so
            // the socket provably makes no write progress. Deterministic, and
            // avoids the loopback RST that otherwise obscures the reason.
            var ps = PeerSenderFor(host, id);
            var release = InstallWriteControl(ps, ControlledStream.Behavior.BlockForever);

            // Keep the queue non-empty with coalescable frames (bounded, small
            // backlog so the byte-cap rule never engages — only liveness can
            // fire). NoWriteProgress fires after the (shortened) timeout.
            var frame = new byte[512];
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!site.HasLeft(id, 0) && DateTime.UtcNow < deadline)
            {
                host.Send(id, frame, guaranteed: false, coalesceKey: 99);
                Thread.Sleep(10);
            }
            release();
            Assert.True(site.HasLeft(id, 0), "a peer accepting nothing must eventually be disconnected");

            var counts = host.DrainDisconnectCounts();
            Assert.True(counts.TryGetValue(DisconnectReason.NoWriteProgress, out var n) && n >= 1,
                $"expected NoWriteProgress recorded, got {string.Join(",", counts.Select(kv => $"{kv.Key}={kv.Value}"))}");
            client.Dispose();
        }
        finally { host.Shutdown(); }
    }

    /// <summary>Failing-first: the byte-cap rule still disconnects a peer whose
    /// GUARANTEED (non-droppable, non-coalescable) backlog sits over capacity for
    /// the window — with the distinct <see cref="DisconnectReason.QueueOverCapacity"/>
    /// reason. Bounded memory (P606) is preserved.</summary>
    [Fact]
    public void Byte_cap_overcapacity_disconnect_still_works_with_QueueOverCapacity()
    {
        using var _ = ShortenOverCapacityOnly();
        var site = new Site();
        var host = NewHost(site);
        try
        {
            host.Host(0);
            var (id, client) = ConnectRawPeer(host, site, "CAP");

            // Swap in a stream whose Write accepts bytes only slowly (one frame
            // per 50 ms), so the socket DOES make steady write progress (the
            // NoWriteProgress rule, left at 5 s, can never steal this test) but
            // the producer easily outpaces it — the QUEUE races over the byte cap
            // and stays there, so QueueOverCapacity (400 ms) is the only signal
            // that can fire. No loopback RST to obscure the reason.
            var ps = PeerSenderFor(host, id);
            var release = InstallWriteControl(ps, ControlledStream.Behavior.ThrottleMs, delayMs: 50);

            var frame = new byte[8192];
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!site.HasLeft(id, 0) && DateTime.UtcNow < deadline)
            {
                host.Send(id, frame, guaranteed: true, coalesceKey: 0);   // guaranteed: never shed/coalesce -> over capacity
                Thread.Sleep(2);
            }
            release();
            Assert.True(site.HasLeft(id, 0), "a guaranteed-only over-capacity peer must be disconnected (bounded memory)");

            var counts = host.DrainDisconnectCounts();
            Assert.True(counts.TryGetValue(DisconnectReason.QueueOverCapacity, out var n) && n >= 1,
                $"expected QueueOverCapacity recorded, got {string.Join(",", counts.Select(kv => $"{kv.Key}={kv.Value}"))}");
            client.Dispose();
        }
        finally { host.Shutdown(); }
    }

    /// <summary>P608's guaranteed own-ship stream (one constant coalesce key per
    /// peer) can never accumulate: it is bounded to one pending frame, so it can
    /// never push a peer's queue over capacity by itself, and it is never shed.
    /// Flood a tiny-window, never-reading peer past capacity with guaranteed
    /// own-ship frames of a single key interleaved with non-guaranteed shed
    /// frames of another key: the queue stays bounded and no over-capacity
    /// disconnect fires (liveness is left long so only the byte-cap rule could
    /// fire, and it must not).</summary>
    [Fact]
    public void Guaranteed_own_ship_stream_never_triggers_overcapacity_disconnect()
    {
        using var _ = ShortenOverCapacityOnly();
        var site = new Site();
        var host = NewHost(site);
        try
        {
            host.Host(0);
            var (id, client) = ConnectRawPeer(host, site, "OWSHP", receiveWindowBytes: 65536);

            // Flood well past the tiny capacity. The guaranteed own-ship frame is
            // bounded to one pending copy (coalesced per key) and never shed; the
            // non-guaranteed frames are shed oldest-first, so the non-droppable
            // remainder (one own-ship frame) is always under capacity.
            var ownShip = new byte[8192];
            var shed = new byte[8192];
            for (int i = 0; i < 2000; i++)
            {
                host.Send(id, ownShip, guaranteed: true, coalesceKey: 707);   // own-ship: guaranteed, never shed
                host.Send(id, shed, guaranteed: false, coalesceKey: 708);     // shed candidate
            }
            Thread.Sleep(400);

            Assert.False(site.HasLeft(id, 0),
                "a guaranteed own-ship stream (bounded to one per key) must never push a peer over capacity or disconnect it");
            client.Dispose();
        }
        finally { host.Shutdown(); }
    }

    /// <summary>The three overflow-policy reasons are distinct enum values, so a
    /// window summary can tell an over-capacity peer from a no-progress peer from
    /// a send-timeout peer apart.</summary>
    [Fact]
    public void Overflow_reasons_are_distinct()
    {
        Assert.NotEqual(DisconnectReason.QueueOverCapacity, DisconnectReason.NoWriteProgress);
        Assert.NotEqual(DisconnectReason.NoWriteProgress, DisconnectReason.SendTimeout);
        Assert.NotEqual(DisconnectReason.SendTimeout, DisconnectReason.SendError);
        Assert.NotEqual(DisconnectReason.QueueOverCapacity, DisconnectReason.SendTimeout);
        Assert.NotEqual(DisconnectReason.None, DisconnectReason.NoWriteProgress);
    }
}

/// <summary>P611 liveness-policy suite under thread-per-peer (default) mode.</summary>
[Collection("LiveNetwork")]
public class TcpTransportLivenessPolicyThreadPerPeerTests : TcpTransportLivenessPolicyTestsBase
{
    public TcpTransportLivenessPolicyThreadPerPeerTests() : base(TransportMode.ThreadPerPeer) { }

    /// <summary>Failing-first: a blocking write that stalls for the socket's
    /// SendTimeout is attributed to the distinct <see cref="DisconnectReason.SendTimeout"/>
    /// (not the generic SendError). Thread-per-peer only: pool mode refuses to
    /// block on a non-writable socket (SocketReady check) and relies on
    /// NoWriteProgress instead. We shrink the accepted socket's SendTimeout so
    /// the writer's blocking Write times out deterministically in well under the
    /// (default, long) enqueue-side timers, which must not fire first.</summary>
    [Fact]
    public void Blocked_write_hitting_send_timeout_records_SendTimeout_not_SendError()
    {
        var site = new Site();
        var host = new TcpTransport(site, TransportMode.ThreadPerPeer, poolWorkers: 0);
        try
        {
            host.Host(0);
            var (id, client) = ConnectRawPeer(host, site, "TIMEO");

            // Swap in a stream whose Write stalls for 150 ms then throws a socket
            // TimedOut — exercising the writer's catch path, which must attribute
            // the distinct SendTimeout reason (not the generic SendError). The
            // enqueue-side timers are left at their long defaults so they cannot
            // fire first. Thread-per-peer only (pool mode refuses to block on a
            // non-writable socket and relies on NoWriteProgress instead).
            var ps = PeerSenderFor(host, id);
            var ctrl = new ControlledStream(PeerSocket(ps), ControlledStream.Behavior.TimeoutAfterMs, delayMs: 150);
            SwapStream(ps, ctrl);

            var big = new byte[1 << 16];
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!site.HasLeft(id, 0) && DateTime.UtcNow < deadline)
            {
                host.Send(id, big, guaranteed: true, coalesceKey: 0);
                Thread.Sleep(5);
            }
            ctrl.Release();
            Assert.True(site.HasLeft(id, 0), "a peer whose write times out must be disconnected");

            var counts = host.DrainDisconnectCounts();
            Assert.True(counts.TryGetValue(DisconnectReason.SendTimeout, out var n) && n >= 1,
                $"expected SendTimeout recorded, got {string.Join(",", counts.Select(kv => $"{kv.Key}={kv.Value}"))}");
            client.Dispose();
        }
        finally { host.Shutdown(); }
    }
}

/// <summary>P611 liveness-policy suite under pool mode.</summary>
[Collection("LiveNetwork")]
public class TcpTransportLivenessPolicyPoolTests : TcpTransportLivenessPolicyTestsBase
{
    public TcpTransportLivenessPolicyPoolTests() : base(TransportMode.Pool) { }
}
