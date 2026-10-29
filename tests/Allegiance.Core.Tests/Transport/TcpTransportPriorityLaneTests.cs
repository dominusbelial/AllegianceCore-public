using System.Buffers.Binary;
using System.Collections;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Allegiance.Core.Transport;

namespace Allegiance.Core.Tests.Transport;

/// <summary>
/// P634 — the two-lane per-peer send queue. Prior work coalesced the per-tick
/// latest-wins position stream and mixed it with the one-time join/world-sync
/// data (MISSIONDEF, EXPORTs, CLUSTERINFO/graph, SET_CLUSTER — all
/// coalesceKey==0, guaranteed) in ONE shared FIFO. At ~390 connections a
/// joiner's guaranteed world data was starved behind the continuously-produced
/// stream frames (P633 measured ~224/390 bots never receiving their cluster
/// GRAPH/warp exports and never leaving home). P634 splits the sender into:
///   - an ORDERED lane (coalesceKey==0): never coalesced, never dropped, and
///     ALWAYS drained before any stream frame;
///   - a STREAM lane (coalesceKey!=0): latest-wins coalesced, shed under load.
/// These tests pin the priority contract. Parameterized over both send modes
/// (thread-per-peer and pool) because the lane logic lives in the shared
/// <see cref="PeerSender"/>.
/// </summary>
[Collection("LiveNetwork")]
public abstract class TcpTransportPriorityLaneTestsBase
{
    private readonly TransportMode _mode;
    protected TcpTransportPriorityLaneTestsBase(TransportMode mode) => _mode = mode;

    private static readonly Type PeerSenderType =
        typeof(TcpTransport).GetNestedType("PeerSender", BindingFlags.NonPublic)!;

    private static readonly FieldInfo PeersField =
        typeof(TcpTransport).GetField("_peers", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new Exception("_peers field not found");
    private static readonly FieldInfo OrderedQField = PeerSenderType.GetField("_orderedQueue",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo StreamQField = PeerSenderType.GetField("_streamQueue",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
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

    private static (int Ordered, int Stream) LaneCounts(object ps)
    {
        var o = (IEnumerable)OrderedQField.GetValue(ps)!;
        var s = (IEnumerable)StreamQField.GetValue(ps)!;
        return (o.Cast<object>().Count(), s.Cast<object>().Count());
    }

    private static bool NextDrainedIsOrdered(object ps)
        => (bool)NextDrainedIsOrderedMethod.Invoke(ps, null)!;

    /// <summary>
    /// THE P634 failing-first test: a peer whose position-stream lane is
    /// backed up (writer blocked on a full socket) receives a join/world
    /// (ordered, guaranteed) frame AFTER the stream backlog began. The world
    /// frame must be routed to the ORDERED lane and be the next drained frame —
    /// never left waiting behind the stream. Fails on the pre-P634 single-FIFO
    /// sender (no ordered lane exists).
    /// </summary>
    [Fact]
    public void Ordered_world_data_is_next_drained_even_after_a_stream_backlog()
    {
        var site = new Site();
        var host = NewHost(site);
        try
        {
            host.Host(0);
            var (id, client) = ConnectRawPeer(host, site, "LANES", tinyReceiveWindow: true);
            var ps = PeerSenderFor(host, id);
            using var paused = new PausedSendQueue(ps, TcpTransport.CapacityBytes);

            // 1) Back up the position stream under the queue lock, which
            // pauses both real writer modes without assuming socket saturation.
            var stream = new byte[4096];
            for (int i = 0; i < 4000; i++)
                host.Send(id, stream, guaranteed: false, coalesceKey: 42);

            // 2) The join/world burst arrives (ordered, guaranteed, key 0) —
            // enqueued AFTER the stream backlog, the exact P634 starvation
            // scenario. It must be present in the ORDERED lane and be drained
            // before the stream.
            var world = new byte[64];
            Array.Fill(world, (byte)0x77);
            host.Send(id, world, guaranteed: true, coalesceKey: 0);

            var (ordCnt, _) = LaneCounts(ps);
            Assert.True(ordCnt >= 1, $"ordered lane must hold the world data, got {ordCnt}");
            Assert.True(NextDrainedIsOrdered(ps),
                "join/world (ordered) data must be the next drained frame, ahead of the position stream");
            client.Dispose();
        }
        finally { host.Shutdown(); }
    }

    /// <summary>
    /// Join/world frames are NEVER shed for capacity: a peer flooded over the
    /// byte cap with coalesced stream frames still retains every ordered
    /// (guaranteed) frame it was sent, while the stream lane collapses to the
    /// newest per key. Regression guard — the shedder may only ever discard
    /// non-guaranteed stream frames.
    /// </summary>
    [Fact]
    public void Ordered_join_frames_are_never_shed_when_the_stream_overflow_collapses()
    {
        var site = new Site();
        var host = NewHost(site);
        try
        {
            host.Host(0);
            var (id, client) = ConnectRawPeer(host, site, "NOSHED", tinyReceiveWindow: true);
            var ps = PeerSenderFor(host, id);

            // Pause the real queue consumer. Flooding one coalescing key need
            // not fill the socket at all: a fast producer replaces its own work.
            using var paused = new PausedSendQueue(ps, capacity: 16 * 1024);
            var stream = new byte[8192];
            for (int key = 77; key < 81; key++)
                host.Send(id, stream, guaranteed: false, coalesceKey: key);

            const int nOrdered = 50;
            for (int i = 0; i < nOrdered; i++)
            {
                // Fifty frames exceed the 16 KiB soft cap even after every
                // best-effort stream frame is shed; guaranteed data must remain.
                var w = new byte[512];
                w[0] = (byte)i;
                host.Send(id, w, guaranteed: true, coalesceKey: 0);
            }
            var (ordCnt, strCnt) = LaneCounts(ps);
            Assert.Equal(nOrdered, ordCnt);          // every ordered frame retained while the consumer is paused
            Assert.True(strCnt < 4, $"over-capacity stream frames must actually be shed, got {strCnt}");

            // The ordered lane still drains first.
            Assert.True(NextDrainedIsOrdered(ps));
            client.Dispose();
        }
        finally { host.Shutdown(); }
    }
}

/// <summary>P634 priority-lane suite under thread-per-peer mode (explicit; the server default since P887 is pool).</summary>
[Collection("LiveNetwork")]
public class TcpTransportPriorityLaneThreadPerPeerTests : TcpTransportPriorityLaneTestsBase
{
    public TcpTransportPriorityLaneThreadPerPeerTests() : base(TransportMode.ThreadPerPeer) { }
}

/// <summary>P634 priority-lane suite under pool mode.</summary>
[Collection("LiveNetwork")]
public class TcpTransportPriorityLanePoolTests : TcpTransportPriorityLaneTestsBase
{
    public TcpTransportPriorityLanePoolTests() : base(TransportMode.Pool) { }
}
