using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading.Channels;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Xunit.Abstractions;

namespace Allegiance.Core.Tests.Transport;

[Collection("LiveNetwork")]
public sealed class P1048IngressAndLatencyTests(ITestOutputHelper output)
{
    private sealed class CountingSite(ITransportSite target) : ITransportSite
    {
        internal readonly ConcurrentQueue<ConnectionId> Joins = new();
        internal int Received;
        public void OnPeerJoined(ConnectionId id, string name) { target.OnPeerJoined(id, name); Joins.Enqueue(id); }
        public void OnPeerLeft(ConnectionId id) => target.OnPeerLeft(id);
        public void OnSessionLost() => target.OnSessionLost();
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data)
        { target.OnReceive(from, data); Interlocked.Increment(ref Received); }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task WebSocket_ingress_reuses_event_and_byte_reservations_and_fair_tick_draining(bool decouple)
    {
        long oldBytes = ServerHost.MaxBufferedBytesPerPeer, oldBudget = ServerHost.DrainBudgetEvents;
        int oldEvents = ServerHost.MaxQueuedEventsPerPeer, oldShare = ServerHost.DrainFairShareEvents;
        try
        {
            ServerHost.MaxBufferedBytesPerPeer = 1000; ServerHost.MaxQueuedEventsPerPeer = 4;
            ServerHost.DrainBudgetEvents = 3; ServerHost.DrainFairShareEvents = 1;
            var host = new ServerHost { ReceiveDecouple = decouple }; var site = new CountingSite(host);
            using var transport = new WebSocketServerTransport(site, P1048WebSocketTests.Settings()); host.Transport = transport;
            transport.Host(0, IPAddress.Loopback);
            using var a = await P1048WebSocketTests.Connect(transport.BoundPort);
            using var b = await P1048WebSocketTests.Connect(transport.BoundPort);
            await P1048WebSocketTests.Until(() => site.Joins.Count == 2);
            host.Drain(_ => { }); var ids = site.Joins.ToArray();
            byte[] packet = FedPacket.Pack(Array.Empty<byte[]>());
            for (int i = 0; i < 10; i++) await P1048WebSocketTests.Send(a, packet);
            await P1048WebSocketTests.Send(b, packet);
            await P1048WebSocketTests.Until(() => site.Received == 11);
            Assert.Equal(4, host.QueuedEventsForPeer(ids[0])); Assert.Equal(1, host.QueuedEventsForPeer(ids[1]));
            var received = new List<ConnectionId>();
            Assert.Equal(2, host.Drain(e => { if (e is ReceivedEvt r) received.Add(r.From); }));
            Assert.Equal(ids, received); Assert.Equal(3, host.QueuedEventsForPeer(ids[0]));
            Assert.Equal(0, host.QueuedEventsForPeer(ids[1]));
            host.Drain(_ => { }); host.Drain(_ => { }); host.Drain(_ => { }); Assert.Equal(0, host.QueuedEventsForPeer(ids[0]));
        }
        finally
        {
            ServerHost.MaxBufferedBytesPerPeer = oldBytes; ServerHost.MaxQueuedEventsPerPeer = oldEvents;
            ServerHost.DrainBudgetEvents = oldBudget; ServerHost.DrainFairShareEvents = oldShare;
        }
    }

    private sealed class EchoSite : ITransportSite
    {
        internal ITransport? Transport { get; set; }
        internal readonly Channel<byte[]> Received = Channel.CreateBounded<byte[]>(1);
        public void OnPeerJoined(ConnectionId id, string name) { }
        public void OnPeerLeft(ConnectionId id) { }
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data)
        { if (Transport is { } transport) transport.Send(from, data, true); else Assert.True(Received.Writer.TryWrite(data.ToArray())); }
    }

    // Counts actual plaintext socket bytes, below WS framing and above TCP/IP.
    // Counters are reset after HTTP upgrade and warmup, excluding those bytes.
    private sealed class MeteredStream(Stream inner) : Stream
    {
        internal long ReadBytes, WrittenBytes;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count)
        { int n = inner.Read(buffer, offset, count); Interlocked.Add(ref ReadBytes, n); return n; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { int n = await inner.ReadAsync(buffer, cancellationToken); Interlocked.Add(ref ReadBytes, n); return n; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Write(byte[] buffer, int offset, int count)
        { inner.Write(buffer, offset, count); Interlocked.Add(ref WrittenBytes, count); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { await inner.WriteAsync(buffer, cancellationToken); Interlocked.Add(ref WrittenBytes, buffer.Length); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    [Fact]
    public async Task Loopback_WS_and_TCP_round_trip_and_actual_framing_overhead_are_measured()
    {
        const int rounds = 128;
        byte[] packet = FedPacket.Pack([new Ping { TimeClient = 1048 }.Encode()]);
        var wsSite = new EchoSite(); using var ws = new WebSocketServerTransport(wsSite, P1048WebSocketTests.Settings()); wsSite.Transport = ws;
        ws.Host(0, IPAddress.Loopback);
        var tcpSite = new EchoSite(); var clientSite = new EchoSite();
        var tcp = new TcpTransport(tcpSite, TransportMode.Pool, 1); tcpSite.Transport = tcp;
        var tcpClient = new TcpTransport(clientSite);
        try
        {
        tcp.Host(0, IPAddress.Loopback); tcpClient.Join("127.0.0.1", tcp.BoundPort, "latency");
        MeteredStream? meter = null;
        using var handler = new SocketsHttpHandler { UseProxy = false, ConnectCallback = async (context, token) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(IPAddress.Loopback, ws.BoundPort, token);
                meter = new MeteredStream(new NetworkStream(socket, ownsSocket: true)); return meter;
            }
            catch { socket.Dispose(); throw; }
        } };
        using var invoker = new HttpMessageInvoker(handler); using var wsClient = new ClientWebSocket();
        wsClient.Options.SetRequestHeader("Origin", "http://localhost"); wsClient.Options.AddSubProtocol(WebSocketServerTransport.Subprotocol);
        using var deadline = new CancellationTokenSource(10_000);
        await wsClient.ConnectAsync(new Uri($"ws://127.0.0.1:{ws.BoundPort}/game"), invoker, deadline.Token);
        async Task Round(bool websocket)
        {
            if (websocket) { await P1048WebSocketTests.Send(wsClient, packet); Assert.Equal(packet, await P1048WebSocketTests.Read(wsClient)); }
            else { tcpClient.Send(ConnectionId.Server, packet, true); Assert.Equal(packet, await clientSite.Received.Reader.ReadAsync(deadline.Token)); }
        }
        for (int i = 0; i < 16; i++) { await Round(true); await Round(false); }
        long read = meter!.ReadBytes, written = meter.WrittenBytes, tcpBefore = tcp.TotalWrittenBytes;
        var wsTimes = new List<double>(); var tcpTimes = new List<double>();
        for (int i = 0; i < rounds; i++)
        {
            foreach (bool websocket in i % 2 == 0 ? new[] { true, false } : new[] { false, true })
            {
                long start = Stopwatch.GetTimestamp(); await Round(websocket);
                (websocket ? wsTimes : tcpTimes).Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
        }
        double incoming = (meter.ReadBytes - read) / (double)rounds - packet.Length;
        double outgoing = (meter.WrittenBytes - written) / (double)rounds - packet.Length;
        double tcpHeader = (tcp.TotalWrittenBytes - tcpBefore) / (double)rounds - packet.Length;
        Assert.Equal(2, incoming); Assert.Equal(6, outgoing); Assert.Equal(5, tcpHeader);
        output.WriteLine($"Payload={packet.Length} bytes; rounds={rounds}; WS RTT p50={Percentile(wsTimes, .5):F4} ms p95={Percentile(wsTimes, .95):F4} ms; TCP RTT p50={Percentile(tcpTimes, .5):F4} ms p95={Percentile(tcpTimes, .95):F4} ms; measured WS client/server overhead={outgoing}/{incoming} bytes; measured TCP server overhead={tcpHeader} bytes. Plain loopback, sequential echoes, alternating order, 16 warmup rounds; no simulation/tick/TLS/proxy.");
        static double Percentile(List<double> values, double percentile) => values.Order().ElementAt((int)Math.Ceiling(values.Count * percentile) - 1);
        }
        finally { tcpClient.Shutdown(); tcp.Shutdown(); }
    }
}
