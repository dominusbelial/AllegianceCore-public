using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Allegiance.Core.Transport;

/// <summary>
/// P895: experimental, bounded UDP delivery. Native intent:
/// _Utility/Messages.cpp:558-563 (guaranteed vs nonsequential, 500 ms expiry).
/// Wire protocol and congestion/security policy are modern port infrastructure.
/// One worker owns socket I/O; callbacks execute outside the state lock.
/// </summary>
public sealed class UdpTransport : IServerTransport, IDisposable
{
    internal const int Mtu = 1200;
    internal const int Header = 52;
    private const int Payload = Mtu - Header;
    private const uint Magic = 0x31554441; // ADU1, protocol version 1
    private const int HandshakeSize = 64;
    private const int MaxFrame = 16 * 1024 * 1024;
    private const int MaxKeys = 256;
    private const int MaxReorder = 4096;
    private enum Kind : byte { Hello = 1, Challenge, Confirm, Accept, Data, Ack, Ping, Pong, Close }
    private readonly ITransportSite _site;
    private readonly object _gate = new();
    private readonly Dictionary<ConnectionId, Peer> _peers = new();
    private readonly Dictionary<IPEndPoint, Peer> _endpoints = new();
    private readonly Dictionary<Guid, long> _retired = new();
    private readonly Dictionary<DisconnectReason, int> _disconnectCounts = new();
    private readonly List<Peer> _tickPeers = new();
    private readonly List<Guid> _expiredTokens = new();
    private long _lastRetiredSweep;
    private readonly Queue<Action> _callbacks = new();
    private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] _receive = new byte[Mtu + 1];
    private readonly byte[] _send = new byte[Mtu];
    private readonly ManualResetEventSlim _joined = new(false);
    private Socket? _socket;
    private Thread? _worker;
    private volatile bool _running;
    private bool _host;
    private bool _established;
    public bool IntentionalShutdown;
    private IPEndPoint? _remote;
    private byte[]? _hello;
    private byte[]? _greeting;
    internal Action? AfterHandshakeForTesting { get; set; }
    private long _lastHandshake;
    private uint _nextId;
    private long _written, _enqueued, _received;
    private ushort _boundPort;
    private double _challengeBudget = 64;
    private long _challengeClock = Environment.TickCount64;

    /// <summary>Bounded handshake before the client may fall back to TCP.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Per-peer absolute payload/reassembly memory ceiling.</summary>
    public int MaxBufferedBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>Pacing ceiling, including retransmissions; congestion control can reduce it.</summary>
    public int MaxBytesPerSecond { get; init; } = 2 * 1024 * 1024;
    public EgressAccounting? Egress { get; init; }
    public ReliableTelemetry? Reliable { get; init; }
    public ServerCounters? ServerCounters { get; init; }
    public bool ReliableOneShot { get; init; }
    public bool IsConnected { get; private set; }
    public ConnectionId LocalId => ConnectionId.Me;
    public ushort BoundPort => _host ? _boundPort : (ushort)0;
    public long TotalWrittenBytes => Interlocked.Read(ref _written);
    public long TotalEnqueuedBytes => Interlocked.Read(ref _enqueued);
    public long TotalReceivedBytes => Interlocked.Read(ref _received);
    public string? ClientReadFailure;
    public UdpTransport(ITransportSite site) => _site = site;

    public void Host(ushort port = 0) => Host(port, null);
    public void Host(ushort port, IPAddress? bindAddress)
    {
        lock (_gate)
        {
            EnsureStopped();
            _host = true;
            Start(bindAddress ?? IPAddress.IPv6Any, port);
            IsConnected = true;
        }
    }

    public void Join(string address, ushort port, string playerName)
    {
        if (Encoding.UTF8.GetByteCount(playerName) > 128) throw new ArgumentException("UDP name exceeds 128 bytes", nameof(playerName));
        var addresses = Dns.GetHostAddresses(address);
        var ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.First();
        lock (_gate)
        {
            EnsureStopped();
            _host = false;
            _joined.Reset();
            _remote = Normalize(new IPEndPoint(ip, port));
            _greeting = Encoding.UTF8.GetBytes(playerName);
            _hello = new byte[HandshakeSize];
            WriteMagic(_hello, Kind.Hello);
            RandomNumberGenerator.Fill(_hello.AsSpan(8, 16));
            _lastHandshake = 0;
            Start(IPAddress.IPv6Any, 0);
        }
        if (!_joined.Wait(HandshakeTimeout))
        {
            Shutdown();
            throw new TimeoutException("UDP address-validation handshake timed out");
        }
        AfterHandshakeForTesting?.Invoke();
        lock (_gate)
        {
            if (!IsConnected) throw new IOException("UDP connection closed during handshake");
            _established = true;
        }
    }

    private void EnsureStopped()
    {
        if (_running || _worker is not null) throw new InvalidOperationException("transport already used; create a new transport for reconnect");
    }
    private void Start(IPAddress bind, ushort port)
    {
        _socket = new Socket(bind.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        if (bind.AddressFamily == AddressFamily.InterNetworkV6) _socket.DualMode = true;
        _socket.ReceiveBufferSize = 4 * 1024 * 1024;
        _socket.SendBufferSize = 1024 * 1024;
        _socket.Blocking = false;
        try { _socket.Bind(new IPEndPoint(bind, port)); }
        catch { _socket.Dispose(); _socket = null; throw; }
        _boundPort = (ushort)((IPEndPoint)_socket.LocalEndPoint!).Port;
        _running = true;
        _worker = new Thread(Run) { IsBackground = true, Name = "udp-transport" };
        _worker.Start();
    }

    public void Send(ConnectionId target, ReadOnlySpan<byte> data, bool guaranteed) => SendCore(target, data, guaranteed, 0);
    public void SendCoalescing(ConnectionId target, ReadOnlySpan<byte> data, int coalesceKey) => SendCore(target, data, false, coalesceKey);
    public void SendGuaranteedCoalescing(ConnectionId target, ReadOnlySpan<byte> data, int coalesceKey) => SendCore(target, data, true, coalesceKey);
    private void SendCore(ConnectionId target, ReadOnlySpan<byte> data, bool guaranteed, int key)
    {
        if (data.Length > MaxFrame) throw new ArgumentOutOfRangeException(nameof(data));
        lock (_gate)
        {
            if (!_running) return;
            if (!_host) target = ConnectionId.Server;
            if (_peers.TryGetValue(target, out var p) && !p.Closing) Enqueue(p, data, guaranteed, key);
        }
    }
    private void Enqueue(Peer p, ReadOnlySpan<byte> data, bool guaranteed, int key, bool greet = false)
    {
        bool reliable = guaranteed || key == 0;
        if (key != 0 && !p.SendSequence.ContainsKey(key) && p.SendSequence.Count >= MaxKeys) { Drop(p, "too many UDP stream keys"); return; }
        if (key != 0 && p.Streams.Remove(key, out var old))
        {
            RetireFrame(p, old);
            Reliable?.RecordCoalesced(old.Guaranteed ? ReliableClass.ReliableStream : ReliableClass.BestEffortStream);
        }
        if (p.Buffered + data.Length > MaxBufferedBytes)
        {
            if (reliable) Drop(p, "UDP reliable queue hard cap");
            else Reliable?.RecordShed(ReliableClass.BestEffortStream);
            return;
        }
        ulong seq = key == 0 ? p.NextOrdered++ : p.SendSequence.GetValueOrDefault(key) + 1;
        if (key != 0) p.SendSequence[key] = seq;
        var frame = new Frame(data.ToArray(), key, seq, reliable, guaranteed, greet);
        p.Buffered += data.Length;
        if (key == 0) p.Ordered.Enqueue(frame); else p.Streams[key] = frame;
        if (key == 0 && guaranteed) Reliable?.RecordOneShotParked(ReliableOneShot);
        Interlocked.Add(ref _enqueued, data.Length);
        Account(p, frame, EgressEvent.Offered);
    }

    private void Run()
    {
        try
        {
            while (_running)
            {
                lock (_gate)
                {
                    long now = Environment.TickCount64;
                    // Bounded receive turns keep a flood from starving sends/timeouts.
                    for (int i = 0; i < 256 && _running; i++)
                    {
                        EndPoint ep = _socket!.AddressFamily == AddressFamily.InterNetworkV6
                            ? new IPEndPoint(IPAddress.IPv6Any, 0) : new IPEndPoint(IPAddress.Any, 0);
                        int count;
                        try { count = _socket.ReceiveFrom(_receive, ref ep); }
                        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.WouldBlock or SocketError.ConnectionReset or SocketError.MessageSize) { break; }
                        Interlocked.Add(ref _received, count);
                        if (count <= Mtu) Receive(_receive.AsSpan(0, count), Normalize((IPEndPoint)ep), now);
                    }
                    if (!_host && !IsConnected && _hello is not null && now - _lastHandshake >= 200)
                    {
                        RawSend(_hello, _remote!);
                        _lastHandshake = now;
                    }
                    _tickPeers.Clear(); _tickPeers.AddRange(_peers.Values);
                    foreach (var p in _tickPeers) Tick(p, now);
                    if (now - _lastRetiredSweep >= 1000)
                    {
                        _lastRetiredSweep = now; _expiredTokens.Clear();
                        foreach (var entry in _retired) if (entry.Value < now) _expiredTokens.Add(entry.Key);
                        foreach (var token in _expiredTokens) _retired.Remove(token);
                    }
                }
                DrainCallbacks();
                // A real blocking wait, including under continuous traffic: no WouldBlock spin.
                Thread.Sleep(2);
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException)
        {
            lock (_gate)
            {
                foreach (var p in _peers.Values.ToArray()) Drop(p, $"UDP socket: {ex.Message}");
                _running = false;
            }
        }
        finally { DrainCallbacks(); }
    }
    private void DrainCallbacks()
    {
        while (true)
        {
            Action callback;
            lock (_gate) { if (!_callbacks.TryDequeue(out callback!)) return; }
            // A malformed application packet can close its peer; never kill the socket worker.
            try { callback(); }
            catch (Exception ex) { Trace.TraceError($"UDP site callback: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    private void Receive(ReadOnlySpan<byte> d, IPEndPoint ep, long now)
    {
        if (d.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(d) != Magic) return;
        var kind = (Kind)d[4];
        if (kind <= Kind.Accept) { Handshake(d, ep, kind, now); return; }
        if (d.Length < Header || !_endpoints.TryGetValue(ep, out var p) || new Guid(d.Slice(8, 16)) != p.Token) return;
        p.LastReceive = now;
        switch (kind)
        {
            case Kind.Ack: Ack(p, BinaryPrimitives.ReadUInt64LittleEndian(d[24..]), now); break;
            case Kind.Ping: Control(p, Kind.Pong); break;
            case Kind.Pong: break;
            case Kind.Close: Drop(p, "UDP peer closed"); break;
            case Kind.Data: ReceiveData(p, d, now); break;
        }
    }
    private void Handshake(ReadOnlySpan<byte> d, IPEndPoint ep, Kind kind, long now)
    {
        if (d.Length != HandshakeSize) return;
        if (_host && kind == Kind.Hello)
        {
            // Global reply budget bounds spoofed tiny-packet CPU/egress; reply is never larger.
            _challengeBudget = Math.Min(64, _challengeBudget + (now - _challengeClock) * .064);
            _challengeClock = now;
            if (_challengeBudget < 1) return;
            _challengeBudget--;
            d.CopyTo(_send);
            _send[4] = (byte)Kind.Challenge;
            long bucket = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 10;
            BinaryPrimitives.WriteInt64LittleEndian(_send.AsSpan(24), bucket);
            Cookie(ep, _send.AsSpan(8, 16), bucket, _send.AsSpan(32, 16));
            RawSend(_send.AsSpan(0, HandshakeSize), ep);
        }
        else if (_host && kind == Kind.Confirm)
        {
            long bucket = BinaryPrimitives.ReadInt64LittleEndian(d[24..]);
            long current = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 10;
            if (bucket < current - 1 || bucket > current) return;
            Span<byte> cookie = stackalloc byte[16];
            Cookie(ep, d.Slice(8, 16), bucket, cookie);
            if (!CryptographicOperations.FixedTimeEquals(cookie, d.Slice(32, 16))) return;
            Guid token = new(cookie);
            if (_retired.ContainsKey(token)) return;
            if (!_endpoints.TryGetValue(ep, out var peer))
            {
                if (_peers.Count >= TcpTransport.MaxConnections || _peers.Values.Count(p => !p.Published) >= TcpTransport.MaxPendingHandshake || _retired.Count >= 4096 ||
                    _peers.Values.Count(p => p.Endpoint.Address.Equals(ep.Address)) >= TcpTransport.MaxConnectionsPerIp) return;
                peer = new Peer(new ConnectionId(++_nextId), ep, token);
                _peers.Add(peer.Id, peer);
                _endpoints.Add(ep, peer);
            }
            if (peer.Token != token) return; // No unauthenticated connection replacement/rebinding.
            d.CopyTo(_send);
            _send[4] = (byte)Kind.Accept;
            RawSend(_send.AsSpan(0, HandshakeSize), ep);
        }
        else if (!_host && !IsConnected && ep.Equals(_remote) && _hello is not null && d.Slice(8, 16).SequenceEqual(_hello.AsSpan(8, 16)))
        {
            if (kind == Kind.Challenge)
            {
                d.CopyTo(_hello);
                _hello[4] = (byte)Kind.Confirm;
                _lastHandshake = 0;
            }
            else if (kind == Kind.Accept && _hello[4] == (byte)Kind.Confirm && d.Slice(32, 16).SequenceEqual(_hello.AsSpan(32, 16)))
            {
                var p = new Peer(ConnectionId.Server, ep, new Guid(d.Slice(32, 16))) { Published = true };
                _peers.Add(p.Id, p);
                _endpoints.Add(ep, p);
                // Reserve ordered frame zero before publishing IsConnected. A
                // constructor-started keepalive may send before Join resumes.
                Enqueue(p, _greeting!, true, 0, greet: true);
                _greeting = null;
                IsConnected = true;
                _hello = null;
                _joined.Set();
            }
        }
    }
    private void Cookie(IPEndPoint ep, ReadOnlySpan<byte> nonce, long bucket, Span<byte> output)
    {
        Span<byte> input = stackalloc byte[42];
        ep.Address.MapToIPv6().TryWriteBytes(input, out _);
        BinaryPrimitives.WriteUInt16LittleEndian(input[16..], (ushort)ep.Port);
        nonce.CopyTo(input[18..]);
        BinaryPrimitives.WriteInt64LittleEndian(input[34..], bucket);
        Span<byte> hash = stackalloc byte[32];
        HMACSHA256.HashData(_secret, input, hash);
        hash[..16].CopyTo(output);
    }

    private void ReceiveData(Peer p, ReadOnlySpan<byte> d, long now)
    {
        ulong packet = BinaryPrimitives.ReadUInt64LittleEndian(d[24..]);
        ulong seq = BinaryPrimitives.ReadUInt64LittleEndian(d[32..]);
        int key = BinaryPrimitives.ReadInt32LittleEndian(d[40..]);
        int size = BinaryPrimitives.ReadInt32LittleEndian(d[44..]);
        int index = BinaryPrimitives.ReadUInt16LittleEndian(d[48..]);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(d[50..]);
        int cap = _host ? (int)Math.Min(TcpTransport.ServerInboundMaxFrame, MaxFrame) : MaxFrame;
        if (size < 0 || size > cap || count != Math.Max(1, (size + Payload - 1) / Payload) || index >= count ||
            d.Length - Header != Math.Min(Payload, size - index * Payload) || (d[5] & ~3) != 0) { Drop(p, "UDP invalid fragment"); return; }
        if (packet == 0 || key == 0 && (d[5] & 1) == 0) { Drop(p, "UDP invalid ordered flags"); return; }
        bool greet = (d[5] & 2) != 0;
        if (greet && (!_host || key != 0 || seq != 0 || size > 128)) { Drop(p, "UDP invalid greeting"); return; }
        bool stale = key == 0 ? seq < p.ReceiveOrdered : seq <= p.Delivered.GetValueOrDefault(key);
        if (stale) { Control(p, Kind.Ack, packet); return; }
        if (key == 0 && seq - p.ReceiveOrdered > MaxReorder) { Drop(p, "UDP ordered window exceeded"); return; }
        if (key != 0 && !p.Delivered.ContainsKey(key))
        {
            if (p.Delivered.Count >= MaxKeys) { Drop(p, "UDP receive key cap"); return; }
            p.Delivered[key] = 0;
        }
        if (key != 0 && seq < p.Newest.GetValueOrDefault(key)) { Control(p, Kind.Ack, packet); return; }
        if (key != 0 && seq > p.Newest.GetValueOrDefault(key))
        {
            p.AssemblyScratch.Clear();
            foreach (var entry in p.Assemblies) if (entry.Key.Key == key) p.AssemblyScratch.Add(entry.Key);
            foreach (var old in p.AssemblyScratch) { p.ReceiveBytes -= p.Assemblies[old].Data.Length; p.Assemblies.Remove(old); }
            p.Newest[key] = seq;
        }
        var identity = (Key: key, Seq: seq);
        if (!p.Assemblies.TryGetValue(identity, out var assembly))
        {
            if (p.ReceiveBytes + size > MaxBufferedBytes || p.Assemblies.Count >= MaxReorder) { Drop(p, "UDP reassembly cap"); return; }
            assembly = new Assembly(size, count, greet, (d[5] & 1) != 0, now);
            p.Assemblies.Add(identity, assembly);
            p.ReceiveBytes += size;
        }
        if (assembly.Data.Length != size || assembly.Seen.Length != count || assembly.Greet != greet || assembly.Reliable != ((d[5] & 1) != 0)) { Drop(p, "UDP inconsistent fragments"); return; }
        if (!assembly.Seen[index])
        {
            d[Header..].CopyTo(assembly.Data.AsSpan(index * Payload));
            assembly.Seen[index] = true;
            assembly.Remaining--;
        }
        // ACK only after retaining the fragment; a reliable fragment is never acknowledged then discarded.
        Control(p, Kind.Ack, packet);
        if (assembly.Remaining != 0) return;
        if (key == 0)
        {
            while (p.Assemblies.TryGetValue((0, p.ReceiveOrdered), out var next) && next.Remaining == 0)
            {
                p.Assemblies.Remove((0, p.ReceiveOrdered++));
                p.ReceiveBytes -= next.Data.Length;
                Deliver(p, next);
            }
        }
        else
        {
            p.Delivered[key] = seq;
            p.Assemblies.Remove(identity);
            p.ReceiveBytes -= assembly.Data.Length;
            Deliver(p, assembly);
        }
    }
    private void Deliver(Peer p, Assembly frame)
    {
        if (_host && !p.Published)
        {
            if (!frame.Greet) { Drop(p, "UDP missing greeting"); return; }
            p.Published = true;
            string name = Encoding.UTF8.GetString(frame.Data);
            _callbacks.Enqueue(() => _site.OnPeerJoined(p.Id, name));
        }
        else if (!frame.Greet)
        {
            byte[] data = frame.Data;
            _callbacks.Enqueue(() => _site.OnReceive(p.Id, data));
        }
    }

    private void Tick(Peer p, long now)
    {
        if (now - p.LastReceive > IdleTimeout.TotalMilliseconds || (!_host && p.Closing && now > p.CloseDeadline) ||
            (_host && !p.Published && now - p.Created > HandshakeTimeout.TotalMilliseconds)) { Drop(p, "UDP receive timeout"); return; }
        if (p.Closing && (p.Ordered.Count == 0 && !p.Streams.Values.Any(f => f.Reliable) && p.Flight.Count == 0 || now > p.CloseDeadline))
        {
            // Close is repeated on subsequent ticks; silence timeout handles loss of all notifications.
            if (p.CloseSent++ < 5) { Control(p, Kind.Close); return; }
            Drop(p, "UDP graceful close"); return;
        }
        if (now - p.LastPing >= KeepAliveInterval.TotalMilliseconds) { Control(p, Kind.Ping); p.LastPing = now; }
        p.AssemblyScratch.Clear();
        foreach (var entry in p.Assemblies) if (!entry.Value.Reliable && now - entry.Value.Created > 500) p.AssemblyScratch.Add(entry.Key);
        foreach (var key in p.AssemblyScratch) { p.ReceiveBytes -= p.Assemblies[key].Data.Length; p.Assemblies.Remove(key); }
        p.StreamScratch.Clear();
        foreach (var f in p.Streams.Values) if (!f.Reliable && now - f.Created > 500) p.StreamScratch.Add(f);
        foreach (var f in p.StreamScratch) { p.Streams.Remove(f.Key); RetireFrame(p, f); Reliable?.RecordShed(ReliableClass.BestEffortStream); }
        double elapsed = Math.Max(0, now - p.LastPace);
        p.LastPace = now;
        double rate = Math.Min(MaxBytesPerSecond / 1000.0, p.Cwnd / Math.Max(10, p.Srtt));
        p.Budget = Math.Min(2 * Mtu, p.Budget + elapsed * rate);
        // Congestion and pacing cover originals AND retransmissions, all data lanes.
        p.FlightScratch.Clear(); p.FlightScratch.AddRange(p.Flight.Values);
        foreach (var sent in p.FlightScratch)
        {
            if (now - sent.LastSent < p.Rto) continue;
            if (now >= p.RecoveryUntil)
            { p.Cwnd = Math.Max(2 * Mtu, p.Cwnd / 2); p.Rto = Math.Min(3000, p.Rto * 2); p.RecoveryUntil = now + (long)p.Rto; }
            if (sent.Frame is null || !sent.Frame.Reliable) { RemoveFlight(p, sent); continue; }
            if (p.Budget < sent.Length) break;
            if (Transmit(p, sent.Frame, sent.Index, sent.Id))
            { sent.LastSent = now; sent.Retries++; p.Budget -= sent.Length; }
        }
        // Alternate lanes at fragment granularity. A bulk ordered snapshot cannot monopolize poses.
        for (int i = 0; i < 32 && p.Budget >= Header && p.InFlight < p.Cwnd; i++)
        {
            Frame? ordered = p.Ordered.TryPeek(out var head) ? head : null;
            Frame? stream = null;
            if (p.Streams.Count > 0)
            {
                int start = (int)(p.StreamCursor++ % (uint)p.Streams.Count);
                for (int j = 0; j < p.Streams.Count; j++)
                {
                    var candidate = p.Streams.Values.ElementAt((start + j) % p.Streams.Count);
                    if (candidate.NextFragment < candidate.Count) { stream = candidate; break; }
                }
            }
            Frame? f = p.StreamTurn && stream is not null ? stream : ordered is { } && ordered.NextFragment < ordered.Count ? ordered : stream;
            p.StreamTurn = !p.StreamTurn;
            if (f is null || f.NextFragment >= f.Count) break;
            int length = Header + Math.Min(Payload, f.Data.Length - f.NextFragment * Payload);
            if (p.Budget < length || p.InFlight + length > p.Cwnd) break;
            ulong id = ++p.NextPacket;
            int fragment = f.NextFragment;
            if (!Transmit(p, f, fragment, id)) break;
            f.NextFragment++;
            if (f.Key == 0 && f.NextFragment == f.Count) p.Ordered.Dequeue();
            p.Budget -= length;
            p.Flight.Add(id, new Sent(id, f, fragment, length, now));
            p.InFlight += length;
        }
    }
    private bool Transmit(Peer p, Frame f, int index, ulong id)
    {
        _send.AsSpan(0, Header).Clear();
        WriteMagic(_send, Kind.Data);
        _send[5] = (byte)((f.Reliable ? 1 : 0) | (f.Greet ? 2 : 0));
        p.Token.TryWriteBytes(_send.AsSpan(8, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(_send.AsSpan(24), id);
        BinaryPrimitives.WriteUInt64LittleEndian(_send.AsSpan(32), f.Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(_send.AsSpan(40), f.Key);
        BinaryPrimitives.WriteInt32LittleEndian(_send.AsSpan(44), f.Data.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(_send.AsSpan(48), (ushort)index);
        BinaryPrimitives.WriteUInt16LittleEndian(_send.AsSpan(50), (ushort)f.Count);
        int n = Math.Min(Payload, f.Data.Length - index * Payload);
        f.Data.AsSpan(index * Payload, n).CopyTo(_send.AsSpan(Header));
        return RawSend(_send.AsSpan(0, Header + n), p.Endpoint);
    }
    private void Ack(Peer p, ulong id, long now)
    {
        if (!p.Flight.TryGetValue(id, out var sent)) return;
        RemoveFlight(p, sent);
        if (sent.Retries == 0)
        {
            double sample = Math.Max(1, now - sent.LastSent);
            p.Variance = .75 * p.Variance + .25 * Math.Abs(p.Srtt - sample);
            p.Srtt = .875 * p.Srtt + .125 * sample;
            p.Rto = Math.Clamp(p.Srtt + 4 * p.Variance, 100, 3000);
        }
        p.Cwnd = Math.Min(256 * Mtu, p.Cwnd + Mtu * sent.Length / p.Cwnd);
        var f = sent.Frame;
        if (f is null) return; // Superseded payload is released; its flight still governs congestion.
        f.Acked++;
        if (f.Acked != f.Count) return;
        Account(p, f, EgressEvent.Sent);
        if (f.Key != 0 && p.Streams.GetValueOrDefault(f.Key) == f) p.Streams.Remove(f.Key);
        p.Buffered -= f.Data.Length;
    }
    private static void RemoveFlight(Peer p, Sent sent) { p.Flight.Remove(sent.Id); p.InFlight -= sent.Length; }
    private void RetireFrame(Peer p, Frame f)
    {
        foreach (var sent in p.Flight.Values) if (ReferenceEquals(sent.Frame, f)) sent.Frame = null;
        p.Buffered -= f.Data.Length;
        Account(p, f, EgressEvent.Coalesced);
    }
    private void Account(Peer p, Frame f, EgressEvent ev)
    {
        if (f.Greet || (Egress is null && ServerCounters is null)) return;
        // Reuse the established FED frame accounting contract, outside UDP wire framing.
        var framed = new byte[f.Data.Length + 5];
        framed[0] = 2;
        BinaryPrimitives.WriteInt32LittleEndian(framed.AsSpan(1), f.Data.Length);
        f.Data.CopyTo(framed, 5);
        Egress?.Record(framed, !p.Human, f.Key != 0, ev);
        if (ev == EgressEvent.Offered) ServerCounters?.RecordFrame(framed, f.Guaranteed);
    }
    private void Control(Peer p, Kind kind, ulong packet = 0)
    {
        _send.AsSpan(0, Header).Clear();
        WriteMagic(_send, kind);
        p.Token.TryWriteBytes(_send.AsSpan(8, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(_send.AsSpan(24), packet);
        RawSend(_send.AsSpan(0, Header), p.Endpoint);
    }
    private bool RawSend(ReadOnlySpan<byte> data, IPEndPoint endpoint)
    {
        try { int n = _socket!.SendTo(data, _socket.AddressFamily == AddressFamily.InterNetwork ? new IPEndPoint(endpoint.Address.MapToIPv4(), endpoint.Port) : endpoint); Interlocked.Add(ref _written, n); return n == data.Length; }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.WouldBlock or SocketError.NoBufferSpaceAvailable or SocketError.ConnectionReset) { return false; }
    }
    private static void WriteMagic(Span<byte> data, Kind kind) { BinaryPrimitives.WriteUInt32LittleEndian(data, Magic); data[4] = (byte)kind; }
    private static IPEndPoint Normalize(IPEndPoint ep) => new(ep.Address.MapToIPv6(), ep.Port);

    public bool IsPeerLoopback(ConnectionId id) { lock (_gate) return _peers.TryGetValue(id, out var p) && (IPAddress.IsLoopback(p.Endpoint.Address) || p.Endpoint.Address.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(p.Endpoint.Address.MapToIPv4())); }
    public void SetPeerHuman(ConnectionId id, bool human) { lock (_gate) if (_peers.TryGetValue(id, out var p)) p.Human = human; }
    public bool IsPeerClosed(ConnectionId id) { lock (_gate) return !_peers.TryGetValue(id, out var p) || p.Closing; }
    public void ClosePeer(ConnectionId id, DisconnectReason reason = DisconnectReason.Shutdown)
    { lock (_gate) if (_peers.TryGetValue(id, out var p)) Drop(p, reason.ToString()); }
    public void ClosePeerAfterFlush(ConnectionId id)
    { lock (_gate) if (_peers.TryGetValue(id, out var p)) { p.Closing = true; p.CloseDeadline = Environment.TickCount64 + 5000; } }
    private void Drop(Peer p, string reason)
    {
        if (!_peers.Remove(p.Id)) return;
        // A one-way failure may leave the other endpoint receiving keepalives.
        // Notify it on the surviving direction so reconnect can retire the old seat.
        if (_socket is not null && reason != "UDP peer closed")
        {
            try { Control(p, Kind.Close); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
        }
        _endpoints.Remove(p.Endpoint);
        var reasonCode = reason.Contains("timeout", StringComparison.Ordinal) ? DisconnectReason.NoWriteProgress :
            reason.Contains("cap", StringComparison.Ordinal) ? DisconnectReason.QueueHardCap :
            reason.Contains("invalid", StringComparison.Ordinal) ? DisconnectReason.ProtocolError : DisconnectReason.ReadErrorOrEof;
        _disconnectCounts[reasonCode] = _disconnectCounts.GetValueOrDefault(reasonCode) + 1;
        _retired[p.Token] = Environment.TickCount64 + 30000;
        if (_host)
        { if (p.Published) _callbacks.Enqueue(() => _site.OnPeerLeft(p.Id)); }
        else
        {
            IsConnected = false;
            ClientReadFailure = reason;
            if (!IntentionalShutdown && _established) _callbacks.Enqueue(_site.OnSessionLost);
        }
    }
    public IReadOnlyDictionary<DisconnectReason, int> DrainDisconnectCounts()
    { lock (_gate) { var result = new Dictionary<DisconnectReason, int>(_disconnectCounts); _disconnectCounts.Clear(); return result; } }
    public (int Peers, int MaxOrderedPending, int MaxStreamPending, long MaxQueuedBytes, long TotalQueuedBytes, long MaxOldestGuaranteedMs) SamplePeerQueues()
    {
        lock (_gate)
        {
            int ordered = 0, streams = 0; long max = 0, total = 0, age = 0;
            foreach (var p in _peers.Values)
            {
                ordered = Math.Max(ordered, p.Ordered.Count); streams = Math.Max(streams, p.Streams.Count);
                max = Math.Max(max, p.Buffered); total += p.Buffered;
                foreach (var f in p.Ordered.Concat(p.Streams.Values).Concat(p.Flight.Values.Select(s => s.Frame).OfType<Frame>()))
                    if (f.Reliable) age = Math.Max(age, Environment.TickCount64 - f.Created);
            }
            return (_peers.Count, ordered, streams, max, total, age);
        }
    }
    public void Shutdown()
    {
        Thread? worker;
        lock (_gate)
        {
            IntentionalShutdown = true;
            foreach (var p in _peers.Values.ToArray()) Drop(p, "shutdown");
            _running = false;
            IsConnected = false;
            worker = _worker;
        }
        if (worker is not null && worker != Thread.CurrentThread) worker.Join();
        lock (_gate) { _socket?.Dispose(); _socket = null; }
        DrainCallbacks();
    }
    public void Dispose() { Shutdown(); _joined.Dispose(); }

    private sealed class Peer(ConnectionId id, IPEndPoint endpoint, Guid token)
    {
        public readonly ConnectionId Id = id;
        public readonly IPEndPoint Endpoint = endpoint;
        public readonly Guid Token = token;
        public readonly Queue<Frame> Ordered = new();
        public readonly Dictionary<int, Frame> Streams = new();
        public readonly Dictionary<int, ulong> SendSequence = new(), Delivered = new(), Newest = new();
        public readonly Dictionary<(int Key, ulong Seq), Assembly> Assemblies = new();
        public readonly Dictionary<ulong, Sent> Flight = new();
        public readonly List<Sent> FlightScratch = new();
        public readonly List<Frame> StreamScratch = new();
        public readonly List<(int Key, ulong Seq)> AssemblyScratch = new();
        public ulong NextOrdered, ReceiveOrdered, NextPacket;
        public long Buffered, ReceiveBytes, InFlight;
        public long Created = Environment.TickCount64, LastReceive = Environment.TickCount64, LastPing, LastPace = Environment.TickCount64, RecoveryUntil, CloseDeadline;
        public double Cwnd = 10 * Mtu, Srtt = 100, Variance = 50, Rto = 300, Budget = 2 * Mtu;
        public bool Published, Human, Closing, StreamTurn;
        public int CloseSent;
        public uint StreamCursor;
    }
    private sealed class Frame(byte[] data, int key, ulong sequence, bool reliable, bool guaranteed, bool greet)
    {
        public readonly byte[] Data = data;
        public readonly int Key = key, Count = Math.Max(1, (data.Length + Payload - 1) / Payload);
        public readonly ulong Sequence = sequence;
        public readonly bool Reliable = reliable, Guaranteed = guaranteed, Greet = greet;
        public readonly long Created = Environment.TickCount64;
        public int NextFragment, Acked;
    }
    private sealed class Assembly(int size, int count, bool greet, bool reliable, long now)
    {
        public readonly byte[] Data = new byte[size];
        public readonly bool[] Seen = new bool[count];
        public readonly bool Greet = greet, Reliable = reliable;
        public readonly long Created = now;
        public int Remaining = count;
    }
    private sealed class Sent(ulong id, Frame frame, int index, int length, long now)
    {
        public readonly ulong Id = id;
        public Frame? Frame = frame;
        public readonly int Index = index, Length = length;
        public long LastSent = now;
        public int Retries;
    }
}
