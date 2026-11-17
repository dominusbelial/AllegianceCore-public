using System.Net;
using Allegiance.Core.Transport;

namespace Allegiance.Core.Tests.Transport;

public sealed class P1048TransportMuxTests
{
    private sealed class Sink : ITransportSite
    {
        internal readonly List<ConnectionId> Joined = [];
        internal readonly List<ConnectionId> Left = [];
        internal readonly List<(ConnectionId Id, byte[] Data)> Received = [];
        public void OnPeerJoined(ConnectionId id, string name) => Joined.Add(id);
        public void OnPeerLeft(ConnectionId id) => Left.Add(id);
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data) => Received.Add((from, data.ToArray()));
        public void OnSessionLost() { }
    }
    private sealed class Listener(ITransportSite site, string name, IPAddress address) : IServerTransport
    {
        internal ITransportSite Site => site;
        internal readonly List<(ConnectionId Id, int Key, bool Guaranteed)> Sends = [];
        internal ConnectionId? Closed, Marked;
        internal bool Human;
        internal bool FailHost { get; init; }
        public string ProtocolName => name;
        public bool IsConnected { get; private set; }
        public ConnectionId LocalId => ConnectionId.Me;
        public ushort BoundPort { get; private set; }
        public long TotalWrittenBytes => 0;
        public long TotalEnqueuedBytes => 0;
        public EgressAccounting? Egress => null;
        public ReliableTelemetry? Reliable => null;
        public void Host(ushort port = 0) => Host(port, null);
        public void Host(ushort port, IPAddress? bindAddress)
        { if (FailHost) throw new IOException("bind failed"); IsConnected = true; BoundPort = port == 0 ? (ushort)12345 : port; }
        public void Join(string address, ushort port, string playerName) => throw new NotSupportedException();
        public void Shutdown() => IsConnected = false;
        public void Send(ConnectionId target, ReadOnlySpan<byte> data, bool guaranteed) => Sends.Add((target, 0, guaranteed));
        public void SendCoalescing(ConnectionId target, ReadOnlySpan<byte> data, int coalesceKey) => Sends.Add((target, coalesceKey, false));
        public void SendGuaranteedCoalescing(ConnectionId target, ReadOnlySpan<byte> data, int coalesceKey) => Sends.Add((target, coalesceKey, true));
        public IPAddress? PeerAddress(ConnectionId id) => address;
        public bool IsPeerLoopback(ConnectionId id) => name != "websocket" && IPAddress.IsLoopback(address);
        public bool IsPeerHuman(ConnectionId id) => Human;
        public bool IsPeerClosed(ConnectionId id) => Closed == id;
        public void SetPeerHuman(ConnectionId id, bool human) => Human = human;
        public void MarkPeerAuthenticated(ConnectionId id) => Marked = id;
        public void ClosePeer(ConnectionId id, DisconnectReason reason = DisconnectReason.Shutdown) => Closed = id;
        public void ClosePeerAfterFlush(ConnectionId id) => Closed = id;
        public IReadOnlyDictionary<DisconnectReason, int> DrainDisconnectCounts() => new Dictionary<DisconnectReason, int> { [DisconnectReason.Shutdown] = 1 };
        public (int Peers, int MaxOrderedPending, int MaxStreamPending, long MaxQueuedBytes, long TotalQueuedBytes, long MaxOldestGuaranteedMs) SamplePeerQueues() => (1, 2, 3, 4, 5, 6);
    }

    [Theory]
    [InlineData(1u)] [InlineData(0x80000001u)] [InlineData(uint.MaxValue)]
    public void Three_listeners_can_use_the_same_full_width_inner_ID_without_collision(uint innerId)
    {
        var sink = new Sink(); var mux = new ServerTransportMux(sink);
        Listener[] listeners = [mux.Add(s => new Listener(s, "tcp", IPAddress.Loopback)),
            mux.Add(s => new Listener(s, "udp", IPAddress.Loopback)), mux.Add(s => new Listener(s, "websocket", IPAddress.Parse("198.51.100.9")), 6080)];
        mux.Host(); var inner = new ConnectionId(innerId);
        foreach (var listener in listeners) listener.Site.OnPeerJoined(inner, "peer");
        Assert.Equal(3, sink.Joined.Distinct().Count()); Assert.Equal((ushort)6080, listeners[2].BoundPort);
        for (int i = 0; i < 3; i++)
        {
            var outer = sink.Joined[i]; var listener = listeners[i];
            listener.Site.OnReceive(inner, [(byte)i]); Assert.Equal(outer, sink.Received[i].Id);
            mux.Send(outer, [1], true); mux.SendCoalescing(outer, [2], 7); mux.SendGuaranteedCoalescing(outer, [3], 8);
            Assert.Equal(new[] { (inner, 0, true), (inner, 7, false), (inner, 8, true) }, listener.Sends);
            Assert.Equal(listener.ProtocolName, mux.PeerProtocolName(outer)); Assert.Equal(listener.PeerAddress(inner), mux.PeerAddress(outer));
            Assert.Equal(i != 2, mux.IsPeerLoopback(outer));
            mux.SetPeerHuman(outer, true); Assert.True(mux.IsPeerHuman(outer));
            mux.MarkPeerAuthenticated(outer); Assert.Equal(inner, listener.Marked);
            if (i == 0) mux.ClosePeerAfterFlush(outer); else mux.ClosePeer(outer);
            Assert.Equal(inner, listener.Closed);
        }
        Assert.Equal((3, 2, 3, 4L, 15L, 6L), mux.SamplePeerQueues());
        Assert.Equal(3, mux.DrainDisconnectCounts()[DisconnectReason.Shutdown]);
        listeners[0].Site.OnPeerLeft(inner); Assert.True(mux.IsPeerClosed(sink.Joined[0]));
        Assert.Null(mux.PeerAddress(sink.Joined[0])); Assert.Single(sink.Left);
        mux.Send(sink.Joined[0], [4], true); Assert.Equal(3, listeners[0].Sends.Count);
        listeners[0].Site.OnPeerJoined(inner, "new peer"); Assert.DoesNotContain(sink.Joined[^1], sink.Joined.Take(3));
        mux.Shutdown(); Assert.All(listeners, l => Assert.False(l.IsConnected));
    }

    [Fact]
    public void A_failed_additional_bind_closes_all_started_listeners()
    {
        var mux = new ServerTransportMux(new Sink());
        var first = mux.Add(s => new Listener(s, "tcp", IPAddress.Loopback));
        mux.Add(s => new Listener(s, "websocket", IPAddress.Loopback) { FailHost = true });
        Assert.Throws<IOException>(() => mux.Host()); Assert.False(first.IsConnected); Assert.False(mux.IsConnected);
    }
}
