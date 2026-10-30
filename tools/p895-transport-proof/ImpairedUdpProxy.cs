using System.Net;
using System.Net.Sockets;

namespace Allegiance.TransportProof;

/// <summary>Test/proof-only UDP forwarder. Seeded loss, delay, jitter, duplication
/// and reordering act on real datagrams in both directions. No production hook.</summary>
public sealed class ImpairedUdpProxy : IDisposable
{
    private readonly Socket _front = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly Socket _back = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly IPEndPoint _server;
    private readonly Random _rng;
    private readonly double _loss;
    private readonly int _delay, _jitter;
    private readonly bool _duplicate;
    private readonly PriorityQueue<(byte[] Data, bool ToServer), (long Due, long Order)> _pending = new();
    private readonly Thread _thread;
    private IPEndPoint? _client;
    private volatile bool _running = true;
    public volatile bool DropAll;
    public volatile bool DropDownstream;
    public volatile bool DropServerData;
    public volatile bool DropNextServerData;
    public volatile bool DropClientData;
    public volatile bool DropAcks;
    private long _order, _bytes, _dropped, _packets;
    private int _pendingBytes;
    public ushort Port => (ushort)((IPEndPoint)_front.LocalEndPoint!).Port;
    public long Bytes => Interlocked.Read(ref _bytes);
    public long Dropped => Interlocked.Read(ref _dropped);
    public long Packets => Interlocked.Read(ref _packets);
    public ImpairedUdpProxy(ushort serverPort, double loss = .05, int delayMs = 8, int jitterMs = 15, int seed = 895, bool duplicate = true)
    {
        _server = new IPEndPoint(IPAddress.Loopback, serverPort);
        _loss = loss; _delay = delayMs; _jitter = jitterMs; _rng = new Random(seed); _duplicate = duplicate;
        _front.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _back.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _front.Blocking = _back.Blocking = false;
        _thread = new Thread(Run) { IsBackground = true, Name = "p895-udp-proxy" };
        _thread.Start();
    }
    private void Run()
    {
        var buffer = new byte[65536];
        while (_running)
        {
            Read(_front, true, buffer);
            Read(_back, false, buffer);
            long now = Environment.TickCount64;
            while (_pending.TryPeek(out _, out var priority) && priority.Due <= now)
            {
                var item = _pending.Dequeue();
                _pendingBytes -= item.Data.Length;
                var ep = item.ToServer ? _server : _client;
                if (ep is null || DropAll || DropDownstream && !item.ToServer) continue;
                try { Interlocked.Add(ref _bytes, (item.ToServer ? _back : _front).SendTo(item.Data, ep)); }
                catch (SocketException) { }
            }
            Thread.Sleep(2);
        }
    }
    private void Read(Socket socket, bool toServer, byte[] buffer)
    {
        for (int i = 0; i < 256; i++)
        {
            EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
            int n;
            try { n = socket.ReceiveFrom(buffer, ref ep); }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.WouldBlock or SocketError.ConnectionReset) { return; }
            if (toServer) _client = (IPEndPoint)ep;
            Interlocked.Increment(ref _packets);
            bool data = n > 4 && buffer[4] == 5;
            if (data && !toServer && DropNextServerData)
            { DropNextServerData = false; Interlocked.Increment(ref _dropped); continue; }
            if (DropAll || DropDownstream && !toServer || (data && (toServer ? DropClientData : DropServerData)) || (DropAcks && n > 4 && buffer[4] == 6) || _rng.NextDouble() < _loss || _pendingBytes > 4 * 1024 * 1024)
            { Interlocked.Increment(ref _dropped); continue; }
            Schedule(buffer.AsSpan(0, n).ToArray(), toServer);
            if (_duplicate && _rng.Next(20) == 0) Schedule(buffer.AsSpan(0, n).ToArray(), toServer);
        }
    }
    private void Schedule(byte[] data, bool toServer)
    {
        long due = Environment.TickCount64 + Math.Max(0, _delay + (_jitter == 0 ? 0 : _rng.Next(-_jitter, _jitter + 1)));
        _pending.Enqueue((data, toServer), (due, ++_order));
        _pendingBytes += data.Length;
    }
    public void Dispose()
    {
        _running = false;
        _thread.Join();
        _front.Dispose(); _back.Dispose();
    }
}
