using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Allegiance.Core.Transport;

namespace Allegiance.Core.Tests.Transport;

[Collection("LiveNetwork")]
public sealed class TcpTransportGracefulCloseTests
{
    private sealed class Site : ITransportSite
    {
        public readonly ConcurrentQueue<ConnectionId> Joined = new();
        public readonly ConcurrentQueue<ConnectionId> Left = new();
        public readonly ConcurrentQueue<byte[]> Received = new();
        public void OnPeerJoined(ConnectionId id, string name) => Joined.Enqueue(id);
        public void OnPeerLeft(ConnectionId id) => Left.Enqueue(id);
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data) => Received.Enqueue(data.ToArray());
    }

    [Theory]
    [InlineData(TransportMode.ThreadPerPeer)]
    [InlineData(TransportMode.Pool)]
    public void Graceful_close_retires_peer_before_drain_and_delivers_final_reply_before_eof(TransportMode mode)
    {
        var site = new Site();
        var host = new TcpTransport(site, mode, poolWorkers: 2);
        try
        {
            host.Host(0);
            using var client = new TcpClient { ReceiveTimeout = 30_000, NoDelay = true };
            client.Connect("127.0.0.1", host.BoundPort);
            var stream = client.GetStream();
            var name = Encoding.UTF8.GetBytes("graceful-close");
            var greet = new byte[5 + name.Length];
            greet[0] = 1;
            BinaryPrimitives.WriteInt32LittleEndian(greet.AsSpan(1), name.Length);
            name.CopyTo(greet.AsSpan(5));
            stream.Write(greet);
            Assert.True(SpinWait.SpinUntil(() => !site.Joined.IsEmpty, TimeSpan.FromSeconds(30)));
            var id = Assert.Single(site.Joined);
            Assert.True(SpinWait.SpinUntil(() => host.LiveConnectionCount() == 1,
                TimeSpan.FromSeconds(30)), "greeting resource accounting must settle");
            var peers = (IDictionary)typeof(TcpTransport).GetField("_peers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            var sender = peers[id]!;
            byte[] reply = [10, 20, 30, 40];
            using (new PausedSendQueue(sender, TcpTransport.CapacityBytes))
            {
                host.Send(id, reply, guaranteed: true);
                host.ClosePeerAfterFlush(id);
                // The send worker cannot have drained the reply while this lock is
                // held. Immediate ClosePeer would erase it here (the LOGOFF race).
                Assert.True(host.IsPeerClosed(id));
                Assert.Empty(site.Left); // physical resource accounting remains charged until drain
                Assert.Equal(1, host.LiveConnectionCount());
                host.Send(id, new byte[] { 99 }, guaranteed: true); // retired peers cannot enqueue again
            }
            var header = new byte[5];
            stream.ReadExactly(header);
            Assert.Equal(2, header[0]);
            Assert.Equal(reply.Length, BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1)));
            var body = new byte[reply.Length];
            stream.ReadExactly(body);
            Assert.Equal(reply, body);
            Assert.Equal(-1, stream.ReadByte()); // reply then EOF, no second send
            Assert.Single(site.Left);
        }
        finally { host.Shutdown(); }
    }
}
