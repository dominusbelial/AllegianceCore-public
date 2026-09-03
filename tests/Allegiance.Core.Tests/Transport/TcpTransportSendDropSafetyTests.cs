using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Allegiance.Core.Transport;

namespace Allegiance.Core.Tests.Transport;

/// <summary>
/// Regression tests for the P60 send-race fix: a peer departing mid-tick must
/// never let <see cref="TcpTransport.Send"/> escape onto the sim thread and kill
/// the whole server. These are the surgical, deterministic tests (the approved
/// fallback for a genuinely-timing-dependent race): each one pins the transport
/// into exactly one branch of the race via reflection rather than hoping thread
/// timing lands in the window, so they are stable across CI runs.
///
/// Two branches are pinned, matching the two exceptions the race actually raises:
///  (a) peer already removed from <c>_peers</c> — the old
///      <c>InvalidOperationException</c> ("no such peer") from the host branch;
///  (b) peer still present but its socket throws — the
///      IOException/SocketException/ObjectDisposedException from <c>WriteFrame</c>.
/// Plus: a healthy host send still arrives (fix must not suppress good sends),
/// and the client-role "not connected" path still throws (must stay loud).
/// </summary>
[Collection("LiveNetwork")]
public class TcpTransportSendDropSafetyTests
{
    private static readonly FieldInfo PeersField =
        typeof(TcpTransport).GetField("_peers", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new Exception("TcpTransport._peers field not found — did the transport change?");

    private sealed class Site : ITransportSite
    {
        public readonly List<ConnectionId> Joined = new();
        public readonly List<ConnectionId> Left = new();
        public readonly List<byte[]> Received = new();
        public void OnPeerJoined(ConnectionId id, string name) { lock (Joined) Joined.Add(id); }
        public void OnPeerLeft(ConnectionId id) { lock (Left) Left.Add(id); }
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data) { lock (Received) Received.Add(data.ToArray()); }
    }

    private static Dictionary<ConnectionId, NetworkStream> Peers(TcpTransport t) =>
        (Dictionary<ConnectionId, NetworkStream>)PeersField.GetValue(t)!;

    /// <summary>Connect a raw loopback socket, speak the transport's Greet frame,
    /// and return the host-assigned id once <see cref="ITransportSite.OnPeerJoined"/>
    /// fired, plus the client NetworkStream (kept alive so the test can read a
    /// response back). Returns the pair.</summary>
    private static (ConnectionId Id, NetworkStream ClientNs) ConnectRawPeer(
        TcpTransport host, Site site, string name)
    {
        var raw = new TcpClient();
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
            lock (site.Joined) { if (site.Joined.Count > 0) return (site.Joined[^1], ns); }
            if (DateTime.UtcNow > deadline) throw new TimeoutException("no OnPeerJoined for raw peer");
            Thread.Sleep(5);
        }
    }

    /// <summary>(a) Branch a — the peer's id is no longer in <c>_peers</c> (its read
    /// loop already reaped it) but the caller still has the id. Old behaviour
    /// threw <c>InvalidOperationException("no such peer")</c>, which would have
    /// killed the server; the fix drops the packet silently.</summary>
    [Fact]
    public void Host_send_to_a_reaped_peer_id_does_not_throw()
    {
        var site = new Site();
        var host = new TcpTransport(site);
        try
        {
            host.Host(0);
            var (id, _) = ConnectRawPeer(host, site, "GONE");
            Assert.True(Peers(host).Remove(id), "peer must be present in _peers before the simulated reap");
            host.Send(id, new byte[] { 1, 2, 3 }, guaranteed: true);   // must NOT throw
        }
        finally
        {
            host.Shutdown();
        }
    }

    /// <summary>(b) Branch b — the peer is still in <c>_peers</c> but its socket's
    /// <c>Write</c> throws (dead-but-unreaped). The exception must not propagate;
    /// the packet is dropped the same way as branch a.</summary>
    [Fact]
    public void Host_send_to_a_peer_whose_socket_throws_does_not_propagate()
    {
        var site = new Site();
        var host = new TcpTransport(site);
        NetworkStream clientNs = null!;
        try
        {
            host.Host(0);
            (var id, clientNs) = ConnectRawPeer(host, site, "THROW");
            Assert.Contains(id, Peers(host).Keys);          // still present → hits WriteFrame
            Peers(host)[id] = new ThrowingNetworkStream();  // its Write now throws
            host.Send(id, new byte[] { 1, 2, 3 }, guaranteed: true);   // must NOT throw
        }
        finally
        {
            clientNs?.Dispose();   // close the real client socket → peer thread reaps cleanly
            host.Shutdown();
        }
    }

    /// <summary>A healthy host send still emits its frame — the fix must not
    /// quietly drop legitimate sends, only the doomed ones.</summary>
    [Fact]
    public void Healthy_host_send_still_arrives_on_the_wire()
    {
        var site = new Site();
        var host = new TcpTransport(site);
        try
        {
            host.Host(0);
            var (id, clientNs) = ConnectRawPeer(host, site, "OK");
            var sent = new byte[] { 9, 9, 9 };
            host.Send(id, sent, guaranteed: true);

            // Read the framed response back on the raw socket: 1 type byte + 4 length bytes + body.
            var hdr = new byte[5];
            ReadExactly(clientNs, hdr);
            Assert.Equal(0x02, hdr[0]);                                   // Data frame type
            Assert.Equal((uint)sent.Length, BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(1)));
            var body = new byte[sent.Length];
            ReadExactly(clientNs, body);
            Assert.Equal(sent, body);                                     // exact bytes on the wire
        }
        finally
        {
            host.Shutdown();
        }
    }

    /// <summary>The client-role "not connected" branch must stay loud: a
    /// <see cref="TcpTransport"/> that was never Host()/Join()ed must still throw
    /// when asked to Send — that firing is a real usage bug, not a network race.</summary>
    [Fact]
    public void Client_role_Send_before_join_still_throws()
    {
        var t = new TcpTransport(new Site());   // neither Host nor Join
        Assert.Throws<InvalidOperationException>(
            () => t.Send(ConnectionId.Server, new byte[] { 1 }, guaranteed: true));
    }

    private static void ReadExactly(NetworkStream s, Span<byte> dst)
    {
        int off = 0;
        while (off < dst.Length)
        {
            int n = s.Read(dst.Slice(off, dst.Length - off));
            if (n <= 0) throw new EndOfStreamException("no data");
            off += n;
        }
    }

    /// <summary>A <see cref="NetworkStream"/> whose <c>Write</c> throws exactly
    /// like a dead socket, used to pin branch (b) deterministically. It wraps a
    /// real but never-used connected loopback pair (the ctor requires
    /// <c>Connected</c>); every <c>Write</c> throws <see cref="IOException"/>
    /// regardless of state.</summary>
    private sealed class ThrowingNetworkStream : NetworkStream
    {
        public ThrowingNetworkStream() : base(ConnectedLoopbackPair(), ownsSocket: true) { }

        private static Socket ConnectedLoopbackPair()
        {
            var lst = new TcpListener(IPAddress.Loopback, 0);
            lst.Start();
            var peer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            peer.Connect((IPEndPoint)lst.LocalEndpoint);
            var accepted = lst.AcceptSocket();
            lst.Stop();
            return accepted;   // accepted side: Connected is true; we never use it
        }

        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("socket dead");
        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("socket dead");
    }
}
