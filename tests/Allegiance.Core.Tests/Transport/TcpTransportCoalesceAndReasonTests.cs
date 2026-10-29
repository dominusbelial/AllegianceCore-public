using System.Buffers.Binary;
using System.Collections;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Allegiance.Core.Transport;

namespace Allegiance.Core.Tests.Transport;

/// <summary>
/// P606 — tests for (a) the latest-wins <b>coalescing</b> send path and
/// (b) <b>disconnect-reason</b> attribution. Parameterized over both send
/// modes (thread-per-peer and pool), since the coalesce/overflow logic lives in
/// the shared <see cref="PeerSender"/> and the reason plumbing is common.
///
/// The coalescing contract:
///  - a non-zero <c>coalesceKey</c> marks a latest-wins stream frame: if the
///    peer's queue already holds a pending frame with the same key, the older
///    one is superseded IN PLACE (only the newest pending entry per key);
///  - ordinary (key 0) frames are never coalesced, never dropped and never
///    reordered relative to each other;
///  - a stalled peer whose queue is dominated by coalescable streaming frames
///    stays small (never blows toward the byte cap), yet is still disconnected
///    once its queue stays undrained for the whole OverflowTimeout (no zombie).
///  - every disconnect records exactly one <see cref="DisconnectReason"/>.
/// </summary>
[Collection("LiveNetwork")]
public abstract class TcpTransportCoalesceAndReasonTestsBase
{
    private readonly TransportMode _mode;
    protected TcpTransportCoalesceAndReasonTestsBase(TransportMode mode) => _mode = mode;

    private static readonly FieldInfo PeersField =
        typeof(TcpTransport).GetField("_peers", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new Exception("_peers field not found");

    // PeerSender private introspection (tests share the InternalsVisibleTo friend).
    // P634 — the per-peer sender now has TWO lanes; all coalesce-keyed (stream)
    // frames live in _streamQueue, so the coalescing assertions reflect that.
    private static readonly FieldInfo QField = typeof(TcpTransport).GetNestedType(
            "PeerSender", BindingFlags.NonPublic)!.GetField("_streamQueue",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo CkField = typeof(TcpTransport).GetNestedType(
            "PeerSender", BindingFlags.NonPublic)!.GetField("_coalesceNodes",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

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

    private TcpTransport NewHost(Site site) => new(site, _mode, poolWorkers: 3);

    private static (ConnectionId Id, TcpClient Client) ConnectRawPeer(
        TcpTransport host, Site site, string name, bool tinyReceiveWindow = false)
    {
        int before;
        lock (site.Joined) before = site.Joined.Count;
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
            lock (site.Joined) { if (site.Joined.Count > before) return (site.Joined[before], raw); }
            if (DateTime.UtcNow > deadline) throw new TimeoutException("no OnPeerJoined");
            Thread.Sleep(5);
        }
    }

    private static object PeerSenderFor(TcpTransport host, ConnectionId id)
    {
        var peers = (IDictionary)PeersField.GetValue(host)!;
        return peers[id]!;
    }

    /// <summary>Count the peer's pending queue nodes whose CoalesceKey == key,
    /// and the size of the coalesce map. Caller holds the sender queue lock.</summary>
    private static (int PendingForKey, int CoalesceMapSize, object Queue) PeekQueue(object ps)
    {
        // NOTE: casting LinkedList<T> to the non-generic IEnumerable yields the
        // VALUES (QueuedFrame), not the LinkedListNode, so read the key directly.
        var q = (IEnumerable)QField.GetValue(ps)!;
        int cnt = 0;
        foreach (var item in q)
        {
            int ck = (int)item.GetType().GetField("CoalesceKey")!.GetValue(item)!;
            if (ck != 0) cnt++;
        }
        var ckMap = (IDictionary)CkField.GetValue(ps)!;
        return (cnt, ckMap.Count, q);
    }

    private static IDisposable ShortenOverflow()
    {
        var oT = TcpTransport.OverflowTimeout;
        var oN = TcpTransport.NoWriteProgressTimeout;
        var oC = TcpTransport.CapacityBytes;
        TcpTransport.OverflowTimeout = TimeSpan.FromMilliseconds(400);
        TcpTransport.NoWriteProgressTimeout = TimeSpan.FromMilliseconds(400);
        TcpTransport.CapacityBytes = 64 * 1024;
        return new Restore(() => { TcpTransport.OverflowTimeout = oT; TcpTransport.NoWriteProgressTimeout = oN; TcpTransport.CapacityBytes = oC; });
    }

    private sealed class Restore : IDisposable
    {
        private readonly Action _a; public Restore(Action a) => _a = a;
        public void Dispose() => _a();
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

    private static byte[] ReadFrame(NetworkStream ns)
    {
        var hdr = new byte[5];
        ReadExactly(ns, hdr);
        Assert.Equal(0x02, hdr[0]);
        uint len = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(1));
        var body = new byte[len];
        ReadExactly(ns, body);
        return body;
    }

    /// <summary>Latest-wins per key: a peer's queue holds at most ONE pending
    /// frame per coalesce key, regardless of how many were enqueued — the
    /// newest supersedes the older ones IN PLACE, never duplicates. Uses a
    /// large immediate flood to a tiny-window, never-reading peer so the writer
    /// is provably blocked and the coalescing engine is what keeps the queue to
    /// one pending entry (without it, thousands of frames would pile up).</summary>
    [Fact]
    public void Coalesce_keeps_at_most_one_pending_frame_per_key()
    {
        var site = new Site();
        var host = NewHost(site);
        try
        {
            host.Host(0);
            var (id, client) = ConnectRawPeer(host, site, "COALESCE", tinyReceiveWindow: true);
            var ps = PeerSenderFor(host, id);
            using var paused = new PausedSendQueue(ps, TcpTransport.CapacityBytes);

            // Big immediate flood: many MB into a 1 KiB window => the writer
            // blocks early; coalescing must collapse the same-key frames to ONE.
            var frame = new byte[4096];
            for (int i = 0; i < 4000; i++)
                host.Send(id, frame, guaranteed: false, coalesceKey: 42);

            var (pending, mapSize, _) = PeekQueue(ps);
            // At most one pending frame for key 42 (the newest snapshot), however
            // many of the 4000 were enqueued.
            Assert.True(pending <= 1, $"expected <=1 pending key-42 frame, got {pending}");
            Assert.True(mapSize <= 1, $"expected <=1 coalesce-map entry, got {mapSize}");

            client.Dispose();
        }
        finally { host.Shutdown(); }
    }

    /// <summary>Different keys are independent: distinct message kinds never
    /// collapse into one another — three keys flood to exactly three pending
    /// frames (one per key), never merged and never zero.</summary>
    [Fact]
    public void Coalesce_distinct_keys_remain_independent()
    {
        var site = new Site();
        var host = NewHost(site);
        try
        {
            host.Host(0);
            var (id, client) = ConnectRawPeer(host, site, "KEYS", tinyReceiveWindow: true);
            var ps = PeerSenderFor(host, id);
            using var paused = new PausedSendQueue(ps, TcpTransport.CapacityBytes);

            var frame = new byte[4096];
            for (int rep = 0; rep < 2000; rep++)
            {
                host.Send(id, frame, guaranteed: false, coalesceKey: 1);
                host.Send(id, frame, guaranteed: false, coalesceKey: 2);
                host.Send(id, frame, guaranteed: false, coalesceKey: 3);
            }

            var (pending, mapSize, q) = PeekQueue(ps);
            // Distinct keys are independent: they must NOT collapse into a single
            // pending slot (that is the bug coalescing avoids). Assert that at
            // least two distinct coalesce keys are simultaneously pending — which
            // is only possible if different keys coexist instead of merging.
            var distinct = new HashSet<int>();
            foreach (var item in (IEnumerable)q)
            {
                int ck = (int)item.GetType().GetField("CoalesceKey")!.GetValue(item)!;
                if (ck != 0) distinct.Add(ck);
            }
            Assert.True(distinct.Count == 3,
                $"distinct coalesce keys must remain independent (found {distinct.Count}: {string.Join(",", distinct)})");
            Assert.Equal(3, mapSize);
            Assert.Equal(3, pending);

            client.Dispose();
        }
        finally { host.Shutdown(); }
    }

    /// <summary>Ordinary (key 0) guaranteed traffic is NEVER coalesced, dropped
    /// or reordered: a full sequence sent through a (momentarily non-reading)
    /// peer still arrives in exact order, and no element is lost to coalescing.</summary>
    [Fact]
    public void Coalesce_never_touches_guaranteed_ordered_frames()
    {
        var site = new Site();
        var host = NewHost(site);
        try
        {
            host.Host(0);
            var (id, client) = ConnectRawPeer(host, site, "ORDERED");
            var ns = client.GetStream();

            const int n = 40;
            var expected = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                expected[i] = new byte[3] { (byte)(i >> 8), (byte)i, 0xAA };
                host.Send(id, expected[i], guaranteed: true, coalesceKey: 0);
            }

            var deadline = DateTime.UtcNow.AddSeconds(5);
            int got = 0;
            while (got < n && DateTime.UtcNow < deadline)
            {
                if (!ns.DataAvailable) { Thread.Sleep(2); continue; }
                Assert.Equal(expected[got], ReadFrame(ns));
                got++;
            }
            Assert.Equal(n, got);   // every guaranteed frame delivered, in order
            client.Dispose();
        }
        finally { host.Shutdown(); }
    }

    /// <summary>Coalesced frames CAN supersede across enqueues even while some
    /// were already partially consumed: the final received stream for a key is
    /// a prefix where each key's latest content is retained (here we just assert
    /// no crash + at-most-one coalesced slot ever pending, covered above). A
    /// health check: a fast consumer still receives its coalesced frames (no
    /// silent loss when the queue drains between sends).</summary>
    [Fact]
    public void Coalesce_healthy_consumer_still_receives_each_latest_snapshot()
    {
        var site = new Site();
        var host = NewHost(site);
        try
        {
            host.Host(0);
            var (id, client) = ConnectRawPeer(host, site, "HEALTHY");
            var ns = client.GetStream();

            // Healthy consumer: the writer drains between sends, so each key's
            // newest frame is delivered; we just require a well-formed frame each
            // time and no exception.
            for (int i = 0; i < 10; i++)
            {
                var b = new byte[64];
                b[0] = (byte)i;
                host.Send(id, b, guaranteed: false, coalesceKey: 7);
                var body = ReadFrame(ns);
                Assert.Equal((byte)i, body[0]);
            }
            client.Dispose();
        }
        finally { host.Shutdown(); }
    }

    /// <summary>P608 — a coalesce-keyed frame sent GUARANTEED
    /// (<c>SendGuaranteedCoalescing</c>) is (a) still coalesced (at most one
    /// pending copy per key → bounded, P606 intact) and (b) NEVER shed by the
    /// capacity-overflow shedder — the transport realisation of "the owner's
    /// own-ship state is never dropped". Flood a tiny-window, never-reading peer
    /// well past capacity with guaranteed-coalesce frames of one key interleaved
    /// with non-guaranteed frames of another key: the guaranteed frame survives
    /// (exactly one pending) while the non-guaranteed one is the shed candidate
    /// and never crowds the guaranteed copy out.</summary>
    [Fact]
    public void Guaranteed_coalesced_frames_are_never_shed_but_still_bounded()
    {
        var site = new Site();
        var host = NewHost(site);
        try
        {
            host.Host(0);
            var (id, client) = ConnectRawPeer(host, site, "GCOAL", tinyReceiveWindow: true);
            var ps = PeerSenderFor(host, id);

            using var paused = new PausedSendQueue(ps, capacity: 16 * 1024);
            var frame = new byte[8192];
            for (int i = 0; i < 4000; i++)
            {
                host.Send(id, frame, guaranteed: true, coalesceKey: 77);     // own-ship: guaranteed, never shed
                host.Send(id, frame, guaranteed: false, coalesceKey: 78 + i % 4);    // teammate stream: shed first
            }

            var q = (IEnumerable)QField.GetValue(ps)!;
            int g77 = 0, g78 = 0;
            foreach (var item in q)
            {
                int ck = (int)item.GetType().GetField("CoalesceKey")!.GetValue(item)!;
                bool guar = (bool)item.GetType().GetField("Guaranteed")!.GetValue(item)!;
                if (ck == 77) { g77++; Assert.True(guar, "key-77 frames must be guaranteed"); }
                if (ck is >= 78 and <= 81) g78++;
            }
            // Guaranteed stream: coalesced (<=1 pending, bounded) and never shed
            // (present) even though 4000 guaranteed frames were enqueued past capacity.
            Assert.Equal(1, g77);
            // The non-guaranteed stream is the shed candidate and stays at <=1 too.
            Assert.True(g78 <= 1, $"best-effort frames must be shed to fit the byte cap, got {g78}");

            client.Dispose();
        }
        finally { host.Shutdown(); }
    }

    /// <summary>Anti-zombie: a stalled peer whose backlog is entirely COALESCABLE
    /// streaming frames still gets disconnected once its queue stays undrained
    /// for the whole OverflowTimeout — it cannot silently linger forever even
    /// though coalescing keeps its backlog small. And every disconnect carries a
    /// recorded, non-None reason.</summary>
    [Fact]
    public void Stalled_coalesce_only_peer_is_still_disconnected_with_reason()
    {
        using var _ = ShortenOverflow();
        var site = new Site();
        var host = NewHost(site);
        try
        {
            host.Host(0);
            var (id, client) = ConnectRawPeer(host, site, "STUCK", tinyReceiveWindow: true);

            // A large immediate flood fills the kernel buffer so the writer
            // blocks and the undrained clock can accumulate; coalescing keeps
            // the queue small meanwhile (no memory blow-up).
            var frame = new byte[4096];
            for (int i = 0; i < 4000; i++)
                host.Send(id, frame, guaranteed: false, coalesceKey: 99);

            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!site.HasLeft(id, 0) && DateTime.UtcNow < deadline)
            {
                host.Send(id, frame, guaranteed: false, coalesceKey: 99);
                Thread.Sleep(5);
            }
            Assert.True(site.HasLeft(id, 0), "coalesce-only stalled peer must still be disconnected (no zombie)");

            // Some non-None reason is attributed to the disconnect.
            var counts = host.DrainDisconnectCounts();
            int total = counts.Values.Sum();
            Assert.True(total >= 1, $"a disconnect reason must be recorded, got none ({string.Join(",", counts.Select(kv => $"{kv.Key}={kv.Value}"))})");
            Assert.True(!counts.ContainsKey(DisconnectReason.None), "no disconnect may be attributed to None");
            client.Dispose();
        }
        finally { host.Shutdown(); }
    }

    /// <summary>Disconnect-reason attribution under a guaranteed backlog: a
    /// genuinely stalled peer flooded with GUARANTEED (non-coalesced) traffic
    /// is disconnected and a non-None reason is recorded. Depending on TCP
    /// dynamics the kernel may RST the tiny-window socket first (=> ReadErrorOrEof)
    /// or the overflow/stuck policy fires (=> QueueOverflow); either way the
    /// disconnect carries a real, attributed reason — the thing a mass join
    /// burst previously lacked.</summary>
    [Fact]
    public void Guaranteed_flood_disconnect_records_a_reason()
    {
        using var _ = ShortenOverflow();
        var site = new Site();
        var host = NewHost(site);
        try
        {
            host.Host(0);
            var (id, client) = ConnectRawPeer(host, site, "GFLOOD", tinyReceiveWindow: true);

            var frame = new byte[4096];
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!site.HasLeft(id, 0) && DateTime.UtcNow < deadline)
            {
                host.Send(id, frame, guaranteed: true, coalesceKey: 0);   // guaranteed backlog cannot shed/coalesce
                Thread.Sleep(5);
            }
            Assert.True(site.HasLeft(id, 0), "guaranteed-flooded stalled peer must be disconnected");

            var counts = host.DrainDisconnectCounts();
            int total = counts.Values.Sum();
            Assert.True(total >= 1, $"a disconnect reason must be recorded, got none ({string.Join(",", counts.Select(kv => $"{kv.Key}={kv.Value}"))})");
            Assert.True(!counts.ContainsKey(DisconnectReason.None), "no disconnect may be attributed to None");
            client.Dispose();
        }
        finally { host.Shutdown(); }
    }

    /// <summary>Disconnect reasons: a peer whose socket dies / remote closes
    /// (read-loop EOF) is reaped with ReadErrorOrEof.</summary>
    [Fact]
    public void Remote_close_records_read_eof_reason()
    {
        var site = new Site();
        var host = NewHost(site);
        try
        {
            host.Host(0);
            var (id, client) = ConnectRawPeer(host, site, "EOF");
            client.Dispose();   // remote close => the host's read loop unwinds with EOF
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!site.HasLeft(id, 0) && DateTime.UtcNow < deadline) Thread.Sleep(10);
            Assert.True(site.HasLeft(id, 0), "peer must be reaped on remote close");

            var counts = host.DrainDisconnectCounts();
            Assert.True(counts.TryGetValue(DisconnectReason.ReadErrorOrEof, out var n) && n >= 1,
                $"expected ReadErrorOrEof recorded, got {string.Join(",", counts.Select(kv => $"{kv.Key}={kv.Value}"))}");
        }
        finally { host.Shutdown(); }
    }
}

/// <summary>P606 coalesce/reason suite under thread-per-peer mode (explicit; the server default since P887 is pool).</summary>
[Collection("LiveNetwork")]
public class TcpTransportCoalesceAndReasonThreadPerPeerTests : TcpTransportCoalesceAndReasonTestsBase
{
    public TcpTransportCoalesceAndReasonThreadPerPeerTests() : base(TransportMode.ThreadPerPeer) { }
}

/// <summary>P606 coalesce/reason suite under pool mode.</summary>
[Collection("LiveNetwork")]
public class TcpTransportCoalesceAndReasonPoolTests : TcpTransportCoalesceAndReasonTestsBase
{
    public TcpTransportCoalesceAndReasonPoolTests() : base(TransportMode.Pool) { }
}
