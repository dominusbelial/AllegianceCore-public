using System.Buffers.Binary;
using System.Collections;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Allegiance.Core.Transport;

namespace Allegiance.Core.Tests.Transport;

/// <summary>
/// P813d — the reliable tier: one-shot messages (chat, kill feed, death, dock,
/// ship delete, game over) must NEVER be shed or coalesced under queue
/// pressure, only delayed — while self-healing streams (ship states) MAY be
/// coalesced/shed. This suite proves the invariant end to end with a stalled
/// peer, in both send modes and with the experimental
/// <see cref="TcpTransport.ReliableOneShot"/> park lane ON and OFF (OFF =
/// today's behaviour, unchanged). The telemetry assertions pin that only the
/// best-effort stream class ever sheds.
///
/// Native backing: every one-shot server message is FM_GUARANTEED (chat
/// FedSrv.CPP:3387, kill feed FedSrv.CPP:11914, ship-delete fsship.cpp:223,
/// dock fsship.cpp:1042, game-over fsmission.cpp:3436-3438); only the position/
/// damage streams are FM_NOT_GUARANTEED (HEAVY/LIGHT_SHIPS_UPDATE
/// FedSrv.CPP:4189/4195).
/// </summary>
[Collection("LiveNetwork")]
public abstract class P813dReliableTierTestsBase
{
    private readonly TransportMode _mode;
    protected P813dReliableTierTestsBase(TransportMode mode) => _mode = mode;

    private static readonly Type PeerSenderType =
        typeof(TcpTransport).GetNestedType("PeerSender", BindingFlags.NonPublic)!;

    private static readonly FieldInfo PeersField =
        typeof(TcpTransport).GetField("_peers", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new Exception("_peers field not found");
    private static readonly FieldInfo OrderedQField = PeerSenderType.GetField("_orderedQueue",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo StreamQField = PeerSenderType.GetField("_streamQueue",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo ParkPendingMethod =
        PeerSenderType.GetMethod("ParkPending", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo NextDrainedIsParkMethod =
        PeerSenderType.GetMethod("NextDrainedIsPark", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo NextDrainedIsOrderedMethod =
        PeerSenderType.GetMethod("NextDrainedIsOrdered", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private sealed class Site : ITransportSite
    {
        public readonly List<ConnectionId> Joined = new();
        public readonly List<ConnectionId> Left = new();
        public void OnPeerJoined(ConnectionId id, string name) { lock (Joined) Joined.Add(id); }
        public void OnPeerLeft(ConnectionId id) { lock (Left) Left.Add(id); }
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data) { }
    }

    private TcpTransport NewHost(Site site, bool reliableOneShot, ReliableTelemetry? telemetry = null)
        => new(site, _mode, poolWorkers: 3)
        {
            ReliableOneShot = reliableOneShot,
            Reliable = telemetry,
        };

    private static (ConnectionId Id, TcpClient Client) ConnectRawPeer(
        TcpTransport host, Site site, string name)
    {
        int before;
        lock (site.Joined) before = site.Joined.Count;
        var raw = new TcpClient();
        raw.NoDelay = true;
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

    private static int LaneCount(object ps, FieldInfo f)
        => ((IEnumerable)f.GetValue(ps)!).Cast<object>().Count();

    private static int ParkPending(object ps)
        => (int)ParkPendingMethod.Invoke(ps, null)!;
    private static bool NextDrainedIsPark(object ps)
        => (bool)NextDrainedIsParkMethod.Invoke(ps, null)!;
    private static bool NextDrainedIsOrdered(object ps)
        => (bool)NextDrainedIsOrderedMethod.Invoke(ps, null)!;

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

    /// <summary>
    /// THE P813d failing-first test: a stalled peer (send queue paused past
    /// capacity, writer blocked) is flooded with best-effort stream frames AND
    /// guaranteed one-shots. On release every guaranteed one-shot must arrive in
    /// exact order — never dropped, never reordered, only delayed — while the
    /// stream lane collapses to its coalesced/latest-wins residue. Runs with the
    /// reliable park lane OFF (today's ordered lane) and ON (park lane).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Guaranteed_one_shots_survive_a_stall_and_arrive_in_order(bool reliableOneShot)
    {
        var site = new Site();
        var host = NewHost(site, reliableOneShot);
        try
        {
            host.Host(0);
            var (id, client) = ConnectRawPeer(host, site, "R813D");
            var ns = client.GetStream();
            var ps = PeerSenderFor(host, id);

            // A burst of guaranteed one-shots (small, distinctive bytes) we will
            // verify arrive intact, in order, after the stall.
            const int n = 40;
            var expected = new byte[n][];
            for (int i = 0; i < n; i++)
                expected[i] = new byte[] { (byte)(0xA0 + (i >> 8)), (byte)i, 0x5A };

            // Pause the queue consumer (simulates a peer that cannot keep up)
            // with a modest cap so best-effort stream frames must shed.
            using (var paused = new PausedSendQueue(ps, capacity: 64 * 1024))
            {
                // Flood the stream lane (self-healing, may be coalesced/shed).
                var stream = new byte[4096];
                for (int i = 0; i < 2000; i++)
                    host.Send(id, stream, guaranteed: false, coalesceKey: 55);

                for (int i = 0; i < n; i++)
                    host.Send(id, expected[i], guaranteed: true, coalesceKey: 0);

                // The one-shots are retained (never shed) — in the park lane when the
                // flag is ON, else in the ordinary ordered lane.
                if (reliableOneShot)
                {
                    Assert.Equal(n, ParkPending(ps));
                    Assert.True(NextDrainedIsPark(ps), "park lane must be next drained (highest priority)");
                    Assert.Equal(0, LaneCount(ps, OrderedQField));
                }
                else
                {
                    Assert.True(ParkPending(ps) == 0, "park lane unused when flag is OFF (today's behaviour)");
                    Assert.Equal(n, LaneCount(ps, OrderedQField));
                    Assert.True(NextDrainedIsOrdered(ps));
                }
                // Best-effort stream collapsed to (at most) the newest per key.
                Assert.True(LaneCount(ps, StreamQField) <= 1, "stream must coalesce under the stall");
            }
            // (pause released here)

            // Now the stalled peer drains: every guaranteed one-shot arrives, in
            // exact order, after the stall.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            int got = 0;
            while (got < n && DateTime.UtcNow < deadline)
            {
                if (!ns.DataAvailable) { Thread.Sleep(2); continue; }
                var frame = ReadFrame(ns);
                Assert.True(expected[got].AsSpan().SequenceEqual(frame),
                    $"one-shot {got} mismatch after stall: got [{string.Join(",", frame)}] want [{string.Join(",", expected[got])}]");
                got++;
            }
            Assert.Equal(40, got);   // nothing dropped, nothing reordered
            client.Dispose();
        }
        finally { host.Shutdown(); }
    }

    /// <summary>
    /// The park lane (flag ON) is never shed and never coalesced: flooding a
    /// stalled peer far past capacity with best-effort stream frames sheds the
    /// stream but retains EVERY one-shot parked in the reliable lane.
    /// </summary>
    [Fact]
    public void Reliable_park_lane_never_shed_and_never_coalesced()
    {
        var site = new Site();
        var host = NewHost(site, reliableOneShot: true);
        try
        {
            host.Host(0);
            var (id, client) = ConnectRawPeer(host, site, "RPARK");
            var ps = PeerSenderFor(host, id);

            using var paused = new PausedSendQueue(ps, capacity: 16 * 1024);
            var stream = new byte[8192];
            for (int i = 0; i < 4000; i++)
                host.Send(id, stream, guaranteed: false, coalesceKey: 77 + i % 4); // shed first

            const int n = 50;
            for (int i = 0; i < n; i++)
            {
                var w = new byte[512];
                w[0] = (byte)i;
                host.Send(id, w, guaranteed: true, coalesceKey: 0);   // one-shot -> park lane
            }

            // All 50 parked one-shots survive (never shed), even though the byte
            // cap was blown; the stream lane shed/coalesced down.
            Assert.Equal(n, ParkPending(ps));
            Assert.True(NextDrainedIsPark(ps));
            client.Dispose();
        }
        finally { host.Shutdown(); }
    }

    /// <summary>
    /// Bounded telemetry: the shed counter fires ONLY for the best-effort stream
    /// class; one-shot and reliable-stream (guaranteed) classes never shed, and
    /// the coalesced counter sees only the stream classes. Pins the invariant the
    /// rig relies on when it reads the <c>[reliable]</c> line under 200v200 load.
    /// </summary>
    [Fact]
    public void Telemetry_counts_shed_only_for_best_effort_stream()
    {
        var telemetry = new ReliableTelemetry();
        var site = new Site();
        var host = NewHost(site, reliableOneShot: true, telemetry);
        try
        {
            host.Host(0);
            var (id, client) = ConnectRawPeer(host, site, "RTEL");
            var ps = PeerSenderFor(host, id);

            // Phase A — shed: a small cap forces best-effort stream frames to be
            // dropped (the only class the shedder may touch).
            using (var pausedA = new PausedSendQueue(ps, capacity: 16 * 1024))
            {
                var big = new byte[8192];
                for (int i = 0; i < 4000; i++)
                    host.Send(id, big, guaranteed: false, coalesceKey: 770 + i % 4);
            }
            // Phase B — coalesce: with a cap that holds the single key's frame,
            // repeated same-key best-effort stream enqueues supersede in place.
            using (var pausedB = new PausedSendQueue(ps, capacity: 64 * 1024))
            {
                var big = new byte[8192];
                for (int i = 0; i < 2000; i++)
                    host.Send(id, big, guaranteed: false, coalesceKey: 779);
                // A couple of one-shots (park lane) and a guaranteed stream frame.
                host.Send(id, new byte[] { 1, 2, 3 }, guaranteed: true, coalesceKey: 0);
                host.Send(id, big, guaranteed: true, coalesceKey: 778); // P608 own-ship
            }

            var w = telemetry.DrainWindow();
            Assert.True(w.Shed[(int)ReliableClass.BestEffortStream] > 0,
                $"best-effort stream must shed under pressure (got {w.Shed[(int)ReliableClass.BestEffortStream]})");
            Assert.Equal(0, w.Shed[(int)ReliableClass.OneShot]);
            Assert.Equal(0, w.Shed[(int)ReliableClass.ReliableStream]);
            Assert.Equal(0, w.Shed[(int)ReliableClass.BestEffortOrdered]);
            Assert.True(w.OneShotParked >= 1, "one-shots must be counted as parked");
            Assert.True(w.OneShotParkedLane >= 1, "flag ON routes one-shots to the park lane");
            Assert.True(w.Coalesced[(int)ReliableClass.BestEffortStream] > 0,
                $"coalescing must be recorded for best-effort stream (got {w.Coalesced[(int)ReliableClass.BestEffortStream]})");
            Assert.True(w.Coalesced[(int)ReliableClass.OneShot] == 0, "one-shots are never coalesced");
            client.Dispose();
        }
        finally { host.Shutdown(); }
    }
}

/// <summary>P813d reliable-tier suite under thread-per-peer mode (explicit; the server default since P887 is pool).</summary>
[Collection("LiveNetwork")]
public class P813dReliableTierThreadPerPeerTests : P813dReliableTierTestsBase
{
    public P813dReliableTierThreadPerPeerTests() : base(TransportMode.ThreadPerPeer) { }
}

/// <summary>P813d reliable-tier suite under pool mode.</summary>
[Collection("LiveNetwork")]
public class P813dReliableTierPoolTests : P813dReliableTierTestsBase
{
    public P813dReliableTierPoolTests() : base(TransportMode.Pool) { }
}
