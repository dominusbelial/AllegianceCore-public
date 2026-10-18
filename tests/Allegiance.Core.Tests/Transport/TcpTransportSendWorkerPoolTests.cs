using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using Allegiance.Core.Transport;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using TrCfg = global::Allegiance.Server.TransportConfig;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests.Transport;

/// <summary>
/// P605 — pool-mode transport tests. In <see cref="TransportMode.Pool"/> the
/// per-peer <c>peer-send</c> writer threads are replaced by a fixed pool of
/// workers that drain hash-partitioned per-peer queues (round-robin, per-peer
/// order preserved). These tests pin the pool-specific guarantees the task
/// demands:
///  (1) per-peer FIFO order is preserved even with CONCURRENT producers
///      (many threads enqueueing to one peer → the single consumer drains the
///      queue in order, so each producer's own subsequence stays ordered);
///  (2) a stalled (non-reading) peer does NOT delay a neighbour that shares
///      its worker — the worker skips a not-writable socket, so a healthy
///      peer's delivery latency stays bounded;
///  (3) overflow → drop → disconnect behaves exactly as in thread-per-peer
///      (bounded queue, stalled peer dropped, no worker leak);
///  (4) join/leave churn leaves no leaked workers or tracked threads — the
///      pool stays at its configured worker count and the per-peer read-loop
///      threads prune back to baseline.
/// </summary>
[Collection("LiveNetwork")]
public class TcpTransportSendWorkerPoolTests
{
    // A small, fixed pool so worker-partition arithmetic in the tests is
    // deterministic (a peer's worker = connectionId % workerCount).
    private const int PoolWorkers = 3;

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

    private static TcpTransport NewHost(Site site) => new(site, TransportMode.Pool, poolWorkers: PoolWorkers);

    private static IDisposable ShortenOverflowTimer()
    {
        var origT = TcpTransport.OverflowTimeout;
        var origN = TcpTransport.NoWriteProgressTimeout;
        var origC = TcpTransport.CapacityBytes;
        TcpTransport.OverflowTimeout = TimeSpan.FromMilliseconds(400);
        TcpTransport.NoWriteProgressTimeout = TimeSpan.FromMilliseconds(400);
        TcpTransport.CapacityBytes = 64 * 1024;
        return new Restorer(() =>
        {
            TcpTransport.OverflowTimeout = origT;
            TcpTransport.NoWriteProgressTimeout = origN;
            TcpTransport.CapacityBytes = origC;
        });
    }

    private sealed class Restorer : IDisposable
    {
        private readonly Action _a;
        public Restorer(Action a) => _a = a;
        public void Dispose() => _a();
    }

    /// <summary>Connect a raw loopback peer, speak the Greet frame, and return
    /// its host-assigned id once <see cref="ITransportSite.OnPeerJoined"/>
    /// fired. Waits for a <em>new</em> join (recording the pre-connect count)
    /// so repeated calls each get their OWN id deterministically — a naive
    /// "wait until Joined.Count>0" returns an earlier peer's id for the 2nd+
    /// connection. <paramref name="tinyReceiveWindow"/> shrinks the client
    /// receive buffer before connecting so a flooded peer's OS send window
    /// fills and the server-side socket becomes not-writable (the "stalled
    /// peer").</summary>
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
            if (DateTime.UtcNow > deadline) throw new TimeoutException("no OnPeerJoined for raw peer");
            Thread.Sleep(5);
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

    /// <summary>Read one Data frame (type + 4-byte len + body) from a raw
    /// client socket, returning the body.</summary>
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

    private static byte[] Frame(byte a, byte b) => new[] { a, b };

    /// <summary>(1) Per-peer FIFO order survives CONCURRENT producers. Many
    /// threads enqueue labeled sequences to ONE peer; the pool assigns that
    /// peer exactly one worker, which drains the head frame in order. So each
    /// producer's own subsequence — extracted from the single received stream —
    /// must arrive strictly in sequence, proving no per-peer reordering under
    /// concurrency.</summary>
    [Fact]
    public void Pool_preserves_per_peer_order_under_concurrent_enqueues()
    {
        var site = new Site();
        var host = NewHost(site);
        try
        {
            host.Host(0);
            var (id, client) = ConnectRawPeer(host, site, "CONCUR");
            var ns = client.GetStream();

            const int Producers = 4;
            const int PerProducer = 200;
            var threads = new Thread[Producers];
            for (int t = 0; t < Producers; t++)
            {
                int tt = t;
                threads[t] = new Thread(() =>
                {
                    for (int i = 0; i < PerProducer; i++)
                        host.Send(id, Frame((byte)tt, (byte)i), guaranteed: true);
                });
                threads[t].Start();
            }
            foreach (var th in threads) th.Join();

            // Read all frames back; per producer, seqs must be strictly increasing.
            var highest = new int[Producers];
            Array.Fill(highest, -1);
            int read = 0;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (read < Producers * PerProducer && DateTime.UtcNow < deadline)
            {
                if (ns.DataAvailable)
                {
                    var body = ReadFrame(ns);
                    int prod = body[0];
                    int seq = body[1];
                    Assert.True(seq > highest[prod],
                        $"producer {prod} saw seq {seq} after {highest[prod]} — per-peer order violated under concurrency");
                    highest[prod] = seq;
                    read++;
                }
                else Thread.Sleep(2);
            }
            Assert.Equal(Producers * PerProducer, read);
            foreach (var h in highest)
                Assert.Equal(PerProducer - 1, h);

            client.Dispose();
        }
        finally
        {
            host.Shutdown();
        }
    }

    /// <summary>(2) A stalled, never-reading peer must not delay a HEALTHY peer
    /// that shares its worker. Both are partitioned to the same worker
    /// (connection ids 1 and 4, worker = id % 3); flooding the stalled peer
    /// fills its OS send window so its socket is not writable. A worker that
    /// blocked on the stalled peer (naive sequential write to a full buffer)
    /// would stall the healthy peer too; the pool's skip-not-writable logic
    /// keeps the healthy peer's delivery latency bounded.</summary>
    [Fact]
    [Trait("Tier", "Perf")]
    public void Pool_stalled_peer_does_not_delay_a_neighbor_on_the_same_worker()
    {
        var site = new Site();
        var host = NewHost(site);
        try
        {
            host.Host(0);
            var (stallId, stallClient) = ConnectRawPeer(host, site, "STALL", tinyReceiveWindow: true);
            // Parked peers on the OTHER two workers (ids 2,3 → workers 2,0):
            // they exist so the worker map is realistic, but send/receive nothing.
            ConnectRawPeer(host, site, "PARK2");
            ConnectRawPeer(host, site, "PARK3");
            var (okId, okClient) = ConnectRawPeer(host, site, "OK");   // id 4 → worker 1, SAME as stallId
            Assert.Equal(stallId.Value % 3, okId.Value % 3);

            // Fill the stalled peer's OS send window with guaranteed frames so
            // its server-side socket becomes not-writable. SMALL frames: once the
            // tiny client window + server send buffer fill, the socket stops being
            // writable and the worker skips it (steady state). (A single huge frame
            // here would instead trip the write-already-started transient — which
            // the 250 ms pool budget already bounds, but small frames exercise the
            // steady-state skip that the isolation guarantee is really about.)
            var chunk = new byte[1024];
            for (int i = 0; i < 2000; i++) host.Send(stallId, chunk, guaranteed: true);
            Thread.Sleep(50);   // let the worker settle into not-writable on stallId

            // Measure healthy-peer delivery latency while the stalled peer is saturated.
            var ns = okClient.GetStream();
            long maxUs = 0, sumUs = 0;
            const int N = 300;
            for (int i = 0; i < N; i++)
            {
                var t0 = Stopwatch.GetTimestamp();
                host.Send(okId, Frame(0xFF, (byte)i), guaranteed: true);
                var body = ReadFrame(ns);
                Assert.Equal((byte)i, body[1]);
                long us = (Stopwatch.GetTimestamp() - t0) * 1_000_000 / Stopwatch.Frequency;
                if (us > maxUs) maxUs = us;
                sumUs += us;
            }
            // The healthy peer was served continuously: per-delivery latency stays
            // far below the 10 s SendTimeout a blocked write would incur. A worker
            // that serialized on the stalled peer would blow far past this bound.
            Assert.True(maxUs < 500_000,
                $"healthy neighbor's worst delivery was {maxUs / 1000.0:F0} ms — the stalled peer blocked its worker");
            Console.WriteLine($"POOL-LIVE: stalled-peer isolation: mean {(sumUs / N) / 1000.0:F2} ms, max {maxUs / 1000.0:F2} ms over {N} deliveries");

            stallClient.Dispose();
            okClient.Dispose();
        }
        finally
        {
            host.Shutdown();
        }
    }

    /// <summary>(3) Overflow → drop → disconnect behaves as in thread-per-peer:
    /// a genuinely stalled (never-reading, tiny-window) peer is flooded past a
    /// tiny capacity; Send stays non-blocking; the peer is disconnected
    /// outright within the overflow timer rather than accumulating an unbounded
    /// queue; and the pool worker count is untouched (no worker leaked).
    /// Post-disconnect sends stay silent (P60).</summary>
    [Fact]
    [Trait("Tier", "Perf")]
    public void Pool_overflow_drops_and_disconnects_a_stalled_peer_without_leaking_workers()
    {
        using var _ = ShortenOverflowTimer();
        var site = new Site();
        var host = NewHost(site);
        try
        {
            host.Host(0);
            int workersBefore = host.PoolWorkerThreadCount();
            Assert.Equal(PoolWorkers, workersBefore);

            var (id, client) = ConnectRawPeer(host, site, "OVERFLOW", tinyReceiveWindow: true);
            var big = new byte[1 << 16];

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 400; i++)
                host.Send(id, big, guaranteed: true);   // never blocks, never throws
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 2000,
                $"Send to a stalled peer took {sw.ElapsedMilliseconds} ms for 400 frames — it must not block");

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!site.HasLeft(id, 0) && DateTime.UtcNow < deadline)
            {
                host.Send(id, big, guaranteed: true);
                Thread.Sleep(10);
            }
            Assert.True(site.HasLeft(id, 0), "stalled pool peer must eventually be disconnected");

            // No worker leaked, and the disconnected peer left a clean slate.
            Assert.Equal(PoolWorkers, host.PoolWorkerThreadCount());
            host.Send(id, big, guaranteed: true);   // post-disconnect stays silent

            client.Dispose();
        }
        finally
        {
            host.Shutdown();
        }
    }

    /// <summary>(4) Join/leave churn leaks nothing: many peers connect and
    /// abruptly drop, repeatedly. The pool stays at exactly its configured
    /// worker count throughout (no worker grows or leaks), and the per-peer
    /// read-loop threads prune back to the accept-loop baseline after every
    /// wave (M9 guarantee holds in pool mode too).</summary>
    [Fact]
    public void Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn()
    {
        var site = new Site();
        var host = NewHost(site);
        try
        {
            host.Host(0);
            int baseline = host.TrackedThreadCount();   // just the accept loop
            Assert.Equal(PoolWorkers, host.PoolWorkerThreadCount());

            var waves = new List<TcpClient>();
            for (int wave = 0; wave < 4; wave++)
            {
                var waveClients = new List<TcpClient>();
                for (int i = 0; i < 8; i++)
                    waveClients.Add(ConnectRawPeer(host, site, $"W{wave}_{i}").Client);

                // Pool workers never change; every peer added one read-loop thread.
                Assert.Equal(PoolWorkers, host.PoolWorkerThreadCount());
                int expected = baseline + waveClients.Count;
                var td = DateTime.UtcNow.AddSeconds(5);
                while (host.TrackedThreadCount() < expected && DateTime.UtcNow < td) Thread.Sleep(5);
                Assert.True(host.TrackedThreadCount() >= expected,
                    $"wave {wave}: expected ~{expected} tracked threads, saw {host.TrackedThreadCount()}");

                // Abrupt drop of the whole wave.
                foreach (var c in waveClients) { try { c.Dispose(); } catch { } }
                var settle = DateTime.UtcNow.AddSeconds(10);
                while (host.TrackedThreadCount() > baseline && DateTime.UtcNow < settle) Thread.Sleep(10);
                Assert.True(host.TrackedThreadCount() <= baseline,
                    $"wave {wave}: tracked threads did not prune back to {baseline} (got {host.TrackedThreadCount()})");
                Assert.Equal(PoolWorkers, host.PoolWorkerThreadCount());
                waves.AddRange(waveClients);
            }

            Assert.Equal(baseline, host.TrackedThreadCount());
            Assert.Equal(PoolWorkers, host.PoolWorkerThreadCount());
            Console.WriteLine($"POOL-LIVE: after 4 churn waves: {host.PoolWorkerThreadCount()} pool workers, " +
                              $"{host.TrackedThreadCount()} tracked threads (baseline {baseline}) — no leaks");
        }
        finally
        {
            host.Shutdown();
        }
    }

    /// <summary>
    /// (5) Config binding end-to-end: boot a real server with
    /// <c>transport.mode=pool</c> (and an explicit worker count) and assert the
    /// server resolves pool mode, the transport is in pool mode, and it spun up
    /// exactly that many workers. Also pins the DEFAULT is thread-per-peer
    /// (today's behaviour) on a default boot — so selecting the pool is an
    /// explicit, labelled opt-in and never changes an unconfigured server.
    /// </summary>
    private static Cfg Cfg(int port = 0, TrCfg? transport = null) => new()
    {
        ArtPath = TestArtRoot.Value,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Transport = transport,
        Games =
        [
            new Gm { Name = "P605 Config Bind", Map = "XXV_simple.igc", Teams = 1, CivIds = [1], SideNames = ["Alpha"] },
        ],
    };

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Default_server_boot_stays_thread_per_peer()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Cfg(transport: null));
        Assert.False(server.Host.Transport!.IsPoolMode);
        Assert.Equal(TransportMode.ThreadPerPeer, server._transportMode);
        Assert.Equal(0, server.Host.Transport.PoolWorkerThreadCount());
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Pool_config_boot_spins_up_exactly_the_requested_workers()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Cfg(transport: new TrCfg { Mode = "pool", PoolWorkers = 3 }));
        Assert.Equal(TransportMode.Pool, server._transportMode);
        Assert.Equal(3, server._transportPoolWorkers);
        Assert.True(server.Host.Transport!.IsPoolMode);
        Assert.Equal(3, server.Host.Transport.PoolWorkerThreadCount());
    }

    /// <summary>Verify the active mode is PRINTED at boot in both variants —
    /// <c>[boot] transport mode = pool (N workers)</c> and
    /// <c>[boot] transport mode = thread-per-peer</c> — so a live server run
    /// (and any measurement) is self-labelling.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Boot_line_prints_the_active_transport_mode()
    {
        Require.Artwork();

        string BootOnce(TrCfg? transport)
        {
            var sw = new StringWriter();
            var orig = Console.Out;
            Console.SetOut(sw);
            try
            {
                using (var server = new Srv()) server.Boot(Cfg(transport: transport));
            }
            finally { Console.SetOut(orig); }
            return sw.ToString();
        }

        Assert.Contains("[boot] transport mode = pool (3 workers)",
            BootOnce(new TrCfg { Mode = "pool", PoolWorkers = 3 }));
        Assert.Contains("[boot] transport mode = thread-per-peer",
            BootOnce(null));
    }
}
