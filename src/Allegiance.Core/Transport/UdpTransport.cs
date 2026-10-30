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
/// Socket sends and protocol state are serialized; callbacks execute outside the state lock.
/// </summary>
public sealed partial class UdpTransport : IServerTransport, IDisposable
{
    internal const int Mtu = 1200;
    internal const int Header = 32;
    internal const int RecordHeader = 24; // Maximum encoded record header; common frames use 4–8 bytes.
    private const int Payload = Mtu - Header - RecordHeader;
    internal const uint Magic = 0x32554441; // ADU2, batched data and selective ACK lists
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
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxBytesPerSecond);
        _socket = new Socket(bind.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        if (bind.AddressFamily == AddressFamily.InterNetworkV6) _socket.DualMode = true;
        _socket.ReceiveBufferSize = 4 * 1024 * 1024;
        _socket.SendBufferSize = 1024 * 1024;
        _socket.Blocking = !_host;
        try { _socket.Bind(new IPEndPoint(bind, port)); }
        catch { _socket.Dispose(); _socket = null; throw; }
        _boundPort = (ushort)((IPEndPoint)_socket.LocalEndPoint!).Port;
        if (_host)
        {
            _receiveArgs.SetBuffer(_receive, 0, _receive.Length);
            _receiveArgs.Completed += ReceiveCompleted;
        }
        else _clientTimer = new Timer(ClientTimer);
        _running = true;
        _worker = new Thread(_host ? Run : RunClient) { IsBackground = true, Name = "udp-transport" };
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
            if (_peers.TryGetValue(target, out var p) && !p.Closing)
            {
                Enqueue(p, data, guaranteed, key);
                _enqueueSignals++;
                if (_host) { _readyPeers.Add(p); _wake.Set(); }
                else
                {
                    ServiceClient(Environment.TickCount64);
                    if (_callbacks.Count != 0) _clientTimer!.Change(0, Timeout.Infinite);
                }
            }
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
                    _workerTurns++;
                    long now = Environment.TickCount64;
                    ReceiveAvailable(now);
                    CollectDuePeers(now);
                    _tickPeers.Clear(); _tickPeers.AddRange(_readyPeers); _readyPeers.Clear();
                    foreach (var p in _tickPeers)
                    {
                        if (!_peers.ContainsKey(p.Id)) continue;
                        Tick(p, now);
                        if (_peers.ContainsKey(p.Id)) SchedulePeer(p, now);
                    }
                    if (now - _lastRetiredSweep >= 1000)
                    {
                        _lastRetiredSweep = now; _expiredTokens.Clear();
                        foreach (var entry in _retired) if (entry.Value < now) _expiredTokens.Add(entry.Key);
                        foreach (var token in _expiredTokens) _retired.Remove(token);
                    }
                }
                DrainCallbacks();
                int wait;
                lock (_gate) wait = NextWait(Environment.TickCount64);
                if (_running && !_wake.WaitOne(wait)) Interlocked.Increment(ref _timerWakes);
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
    private readonly object _callbackGate = new();
    private void DrainCallbacks()
    {
        lock (_callbackGate) DrainCallbacksSerial();
    }
    private void DrainCallbacksSerial()
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
        int acks = d[5], offset = Header;
        if (acks > 64) return;
        Span<ulong> ackIds = stackalloc ulong[64];
        for (int i = 0; i < acks; i++)
            if (!ReadVar(d, ref offset, out ackIds[i])) return;
        if (kind != Kind.Data && (d[6] != 0 || offset != d.Length)) return;
        p.LastReceive = now;
        if (_host) _readyPeers.Add(p);
        for (int i = 0; i < acks; i++) Ack(p, ackIds[i], now);
        switch (kind)
        {
            case Kind.Ack: break;
            case Kind.Ping: Control(p, Kind.Pong); break;
            case Kind.Pong: break;
            case Kind.Close: Drop(p, "UDP peer closed"); break;
            case Kind.Data:
                ulong packet = BinaryPrimitives.ReadUInt64LittleEndian(d[24..]);
                if (packet == 0 || d[6] == 0) { Drop(p, "UDP invalid packet"); break; }
                for (int i = 0; i < d[6]; i++)
                {
                    if (!ReadRecord(d, ref offset, out var record)) { Drop(p, "UDP invalid record"); return; }
                    int length = (int)Math.Min(Payload, (long)record.Size - (long)record.Index * Payload);
                    if (length < 0 || offset + length > d.Length) { Drop(p, "UDP invalid fragment length"); return; }
                    ReceiveData(p, record, d.Slice(offset, length), now);
                    if (!_peers.ContainsKey(p.Id)) return;
                    offset += length;
                }
                if (offset != d.Length) { Drop(p, "UDP invalid trailing data"); return; }
                QueueAck(p, packet, now);
                break;
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
            _readyPeers.Add(peer);
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

    private void ReceiveData(Peer p, Record record, ReadOnlySpan<byte> d, long now)
    {
        ulong seq = record.Sequence;
        int key = record.Key, size = record.Size, index = record.Index;
        int count = Math.Max(1, (size + Payload - 1) / Payload);
        int cap = _host ? (int)Math.Min(TcpTransport.ServerInboundMaxFrame, MaxFrame) : MaxFrame;
        if (size < 0 || size > cap || index >= count ||
            d.Length != Math.Min(Payload, size - index * Payload) || (record.Flags & ~3) != 0) { Drop(p, "UDP invalid fragment"); return; }
        if (key == 0 && (record.Flags & 1) == 0) { Drop(p, "UDP invalid ordered flags"); return; }
        bool greet = (record.Flags & 2) != 0;
        if (greet && (!_host || key != 0 || seq != 0 || size > 128)) { Drop(p, "UDP invalid greeting"); return; }
        bool stale = key == 0 ? seq < p.ReceiveOrdered : seq <= p.Delivered.GetValueOrDefault(key);
        if (stale) return;
        if (key == 0 && seq - p.ReceiveOrdered > MaxReorder) { Drop(p, "UDP ordered window exceeded"); return; }
        if (key != 0 && !p.Delivered.ContainsKey(key))
        {
            if (p.Delivered.Count >= MaxKeys) { Drop(p, "UDP receive key cap"); return; }
            p.Delivered[key] = 0;
        }
        if (key != 0 && seq < p.Newest.GetValueOrDefault(key)) return;
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
            assembly = new Assembly(size, count, greet, (record.Flags & 1) != 0, now);
            p.Assemblies.Add(identity, assembly);
            p.ReceiveBytes += size;
        }
        if (assembly.Data.Length != size || assembly.Seen.Length != count || assembly.Greet != greet || assembly.Reliable != ((record.Flags & 1) != 0)) { Drop(p, "UDP inconsistent fragments"); return; }
        if (!assembly.Seen[index])
        {
            d.CopyTo(assembly.Data.AsSpan(index * Payload));
            assembly.Seen[index] = true;
            assembly.Remaining--;
        }
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
            if (now < p.NextClose) return;
            if (p.CloseSent++ < 5) { Control(p, Kind.Close); p.NextClose = now + 20; return; }
            Drop(p, "UDP graceful close"); return;
        }
        if (now - p.LastSend >= KeepAliveInterval.TotalMilliseconds) { Control(p, Kind.Ping); p.LastSend = now; } // A failed send waits a full interval.
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
            if (!sent.Fragments.Any(f => f.Frame is { Reliable: true })) { RemoveFlight(p, sent); continue; }
            if (p.Budget < sent.Length) break;
            bool success = Transmit(p, sent, retry: true);
            sent.LastSent = now; // A transient socket failure also waits for its next RTO.
            sent.Retries++;
            if (success) { _retransmits++; p.Budget -= sent.LastWireLength; }
        }
        // Fill each datagram from the existing fair ordered/stream scheduler.
        // Batch only work already queued; never wait to accumulate another frame.
        for (int i = 0; i < 32 && p.Budget >= Header + 4 && p.InFlight < p.Cwnd; i++)
        {
            int limit = (int)Math.Min(Mtu, Math.Min(p.Budget, p.Cwnd - p.InFlight));
            int length = Header;
            var fragments = new List<Fragment>();
            while (fragments.Count < 255)
            {
                var f = NextFrame(p, limit - length);
                if (f is null) break;
                int fragment = f.NextFragment++;
                fragments.Add(new Fragment(f, fragment));
                length += RecordSize(f, fragment) + Math.Min(Payload, f.Data.Length - fragment * Payload);
                if (fragment == 0) RecordSendWait(now - f.Created);
                if (f.Key == 0 && f.NextFragment == f.Count) p.Ordered.Dequeue();
            }
            if (fragments.Count == 0) break;
            var sent = new Sent(++p.NextPacket, fragments, length, now);
            // A transient send failure retains reliable work for the RTO, with its
            // congestion debt. Frames have already left the pending queues.
            Transmit(p, sent, retry: false);
            p.Budget -= sent.Length;
            p.Flight.Add(sent.Id, sent);
            p.InFlight += sent.Length;
        }
        if (p.PendingAcks.Count != 0 && now >= p.AckDue) Control(p, Kind.Ack);
    }

    private static Frame? NextFrame(Peer p, int space)
    {
        Frame? ordered = p.Ordered.TryPeek(out var head) ? head : null;
        Frame? stream = null;
        if (p.Streams.Count > 0)
        {
            int start = (int)(p.StreamCursor++ % (uint)p.Streams.Count);
            for (int j = 0; j < p.Streams.Count; j++)
            {
                var candidate = p.Streams.Values.ElementAt((start + j) % p.Streams.Count);
                if (candidate.NextFragment < candidate.Count && Fits(candidate)) { stream = candidate; break; }
            }
        }
        if (ordered is not null && !Fits(ordered)) ordered = null;
        Frame? result = p.StreamTurn ? stream ?? ordered : ordered ?? stream;
        p.StreamTurn = !p.StreamTurn;
        return result;
        bool Fits(Frame f) => RecordSize(f, f.NextFragment) + Math.Min(Payload, f.Data.Length - f.NextFragment * Payload) <= space;
    }

    private bool Transmit(Peer p, Sent sent, bool retry)
    {
        int length = Header, records = 0, recordHeaders = 0;
        foreach (var fragment in sent.Fragments)
            if (fragment.Frame is { } f && (!retry || f.Reliable))
            { int h = RecordSize(f, fragment.Index); length += h + Math.Min(Payload, f.Data.Length - fragment.Index * Payload); recordHeaders += h; records++; }
        int allowance = (int)Math.Min(Mtu, Math.Min(p.Budget, retry ? Mtu : p.Cwnd - p.InFlight));
        int acks = 0, ackRoom = allowance - length;
        foreach (ulong id in p.PendingAcks)
        {
            int size = VarSize(id);
            if (acks == 64 || ackRoom < size) break;
            acks++; ackRoom -= size;
        }
        int offset = BeginPacket(p, Kind.Data, acks);
        _send[6] = (byte)records;
        BinaryPrimitives.WriteUInt64LittleEndian(_send.AsSpan(24), sent.Id);
        foreach (var fragment in sent.Fragments)
        {
            if (fragment.Frame is not { } f || retry && !f.Reliable) continue;
            WriteRecord(_send, ref offset, f, fragment.Index);
            int n = Math.Min(Payload, f.Data.Length - fragment.Index * Payload);
            f.Data.AsSpan(fragment.Index * Payload, n).CopyTo(_send.AsSpan(offset));
            offset += n;
        }
        if (!retry) sent.Length = offset;
        sent.LastWireLength = offset;
        bool success = RawSend(_send.AsSpan(0, offset), p.Endpoint);
        if (success) { ConsumeAcks(p, acks); _dataRecords += records; _dataHeaders += Header + recordHeaders; if (retry) _retransmitBytes += offset; }
        return success;
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
        foreach (var fragment in sent.Fragments)
        {
            var f = fragment.Frame;
            if (f is null) continue; // Superseded payload was released; flight still governed congestion.
            if (++f.Acked != f.Count) continue;
            Account(p, f, EgressEvent.Sent);
            if (f.Key != 0 && p.Streams.GetValueOrDefault(f.Key) == f) p.Streams.Remove(f.Key);
            p.Buffered -= f.Data.Length;
        }
    }
    private static void RemoveFlight(Peer p, Sent sent) { p.Flight.Remove(sent.Id); p.InFlight -= sent.Length; }
    private void RetireFrame(Peer p, Frame f)
    {
        foreach (var sent in p.Flight.Values)
            foreach (var fragment in sent.Fragments) if (ReferenceEquals(fragment.Frame, f)) fragment.Frame = null;
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
    private void QueueAck(Peer p, ulong id, long now)
    {
        // ACK only after retaining all fragments in the datagram. Selective IDs
        // cover arbitrarily old retransmits; no finite bitmap can age them out.
        if (p.PendingAcks.Count == 0) p.AckDue = now + 20;
        if (p.PendingAcks.Count < 128) p.PendingAcks.Add(id);
        if (p.PendingAcks.Count >= 64) Control(p, Kind.Ack);
    }
    private int BeginPacket(Peer p, Kind kind, int acks)
    {
        _send.AsSpan(0, Header).Clear();
        WriteMagic(_send, kind);
        _send[5] = (byte)acks;
        p.Token.TryWriteBytes(_send.AsSpan(8, 16));
        int offset = Header;
        foreach (ulong id in p.PendingAcks.Take(acks))
            WriteVar(_send, ref offset, id);
        return offset;
    }
    private void ConsumeAcks(Peer p, int count)
    {
        int offset = Header;
        for (int i = 0; i < count; i++)
        { ReadVar(_send, ref offset, out ulong id); p.PendingAcks.Remove(id); }
        if (p.PendingAcks.Count != 0) p.AckDue = Environment.TickCount64 + 20;
    }
    private void Control(Peer p, Kind kind)
    {
        int acks = Math.Min(64, p.PendingAcks.Count);
        int length = BeginPacket(p, kind, acks);
        if (RawSend(_send.AsSpan(0, length), p.Endpoint)) ConsumeAcks(p, acks);
        else p.AckDue = Environment.TickCount64 + 20;
    }
    private bool RawSend(ReadOnlySpan<byte> data, IPEndPoint endpoint)
    {
        try
        {
            int n = _socket!.SendTo(data, _socket.AddressFamily == AddressFamily.InterNetwork
                ? new IPEndPoint(endpoint.Address.MapToIPv4(), endpoint.Port) : endpoint);
            Interlocked.Add(ref _written, n);
            _packetCounts[data[4]]++;
            _packetBytes[data[4]] += n;
            if (data[4] > (byte)Kind.Accept)
            {
                int offset = Header;
                for (int i = 0; i < data[5]; i++) ReadVar(data, ref offset, out _);
                _ackBytes += offset - Header;
            }
            if (_endpoints.TryGetValue(endpoint, out var peer)) peer.LastSend = Environment.TickCount64;
            return n == data.Length;
        }
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
    { lock (_gate) if (_peers.TryGetValue(id, out var p)) { p.Closing = true; p.CloseDeadline = Environment.TickCount64 + 5000; _readyPeers.Add(p); _wake.Set(); } }
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
        // Lazy deadline entries can temporarily retain a retired peer. Release
        // payload/reassembly storage immediately rather than retaining it in the heap.
        p.Ordered.Clear(); p.Streams.Clear(); p.Flight.Clear(); p.Assemblies.Clear();
        p.FlightScratch.Clear(); p.StreamScratch.Clear(); p.PendingAcks.Clear();
        p.Buffered = p.ReceiveBytes = p.InFlight = 0;
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
                foreach (var f in p.Ordered.Concat(p.Streams.Values).Concat(p.Flight.Values.SelectMany(s => s.Fragments).Select(f => f.Frame).OfType<Frame>()))
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
            if (!_running && _socket is null) return;
            foreach (var p in _peers.Values.ToArray()) Drop(p, "shutdown");
            _running = false;
            _wake.Set();
            _clientTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _socket?.Dispose(); // Interrupt a client's blocking receive before joining.
            IsConnected = false;
            worker = _worker;
        }
        if (worker is not null && worker != Thread.CurrentThread && !Monitor.IsEntered(_callbackGate)) worker.Join();
        lock (_gate) { _socket?.Dispose(); _socket = null; }
        DrainCallbacks();
    }
    private int _disposeStarted;
    public void Dispose() { if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return; Shutdown(); _clientTimer?.Dispose(); _receiveArgs.Dispose(); _wake.Dispose(); _joined.Dispose(); }

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
        public readonly HashSet<ulong> PendingAcks = new();
        public long AckDue, Due, DeadlineVersion;
        public readonly List<Sent> FlightScratch = new();
        public readonly List<Frame> StreamScratch = new();
        public readonly List<(int Key, ulong Seq)> AssemblyScratch = new();
        public ulong NextOrdered, ReceiveOrdered, NextPacket;
        public long Buffered, ReceiveBytes, InFlight;
        public long Created = Environment.TickCount64, LastReceive = Environment.TickCount64, LastSend = Environment.TickCount64, LastPace = Environment.TickCount64, RecoveryUntil, CloseDeadline, NextClose;
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
    private sealed class Fragment(Frame frame, int index)
    {
        public Frame? Frame = frame;
        public readonly int Index = index;
    }
    private sealed class Sent(ulong id, List<Fragment> fragments, int length, long now)
    {
        public readonly ulong Id = id;
        public readonly List<Fragment> Fragments = fragments;
        public int Length = length, LastWireLength;
        public long LastSent = now;
        public int Retries;
    }
}
