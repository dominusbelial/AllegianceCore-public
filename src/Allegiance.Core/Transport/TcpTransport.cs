using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Allegiance.Core.Transport;

/// <summary>
/// <see cref="ITransport"/> over a real TCP socket — a length-delimited framing
/// layer on top of the byte stream. Connection ids are minted by the host,
/// mirroring DirectPlay's id assignment (§3.1, §Recommendation-constraint-1 of
/// TRANSPORT_SEAM.md); <see cref="ITransportSite.OnPeerJoined"/> is raised before
/// any <see cref="ITransportSite.OnReceive"/> for a peer.
///
/// ## Framing decision (the crux)
///
/// We add an **outer length prefix** and do not rely on <c>cbmsg</c>. Why:
///
/// - FEDMSG.md §3 says explicitly: *"there is no outer packet length; the
///   socket/transport supplies the packet length."* So the wire itself already
///   cedes the delimiter to the transport.
/// - <c>cbmsg</c> is **per-message**, not per-packet. A packet is a flat
///   concatenation of zero-or-more messages plus a trailing CRC. The bytes do
///   not encode *how many* messages precede the CRC, so without an outer length
///   the receiver cannot know where the packet ends or when the CRC has been
///   reached — there is nothing to validate against until the boundary is known.
///   Relying on <c>cbmsg</c> would require assuming exactly one message per
///   packet and re-parsing message framing inside the transport.
/// - The prefix is **outside the CRC**: the CRC covers only message bytes
///   (FEDMSG.md §3). The prefix is transport plumbing that never reaches the
///   codec, so a frame's body is byte-for-byte the codec's own output and
///   <see cref="Allegiance.Core.Wire.FedPacket.Unpack"/> works on it directly.
///
/// Price: one 5-byte header per frame on the wire that DirectPlay never sent —
/// irrelevant for the bot/server path this exists to serve.
///
/// ## Synchronisation model
///
/// Blocking reads on background threads (one accept loop on the host; one read
/// loop per peer). Site callbacks happen on those threads; an implementor wanting
/// single-owner dispatch funnels them through a queue, exactly as FedMessaging's
/// <c>m_csMsgList</c> serializer does. No async/await is used: a blocking read
/// loop is simpler to make correct for stream reassembly than an async state
/// machine, and although sockets were the one place async was permitted, we
/// chose not to spend it (documented for the reviewer).
/// </summary>
public sealed class TcpTransport : ITransport
{
    private enum FrameType : byte { Greet = 0x01, Data = 0x02 }

    private const int LenWidth = 4;
    private const int HeaderWidth = 1 + LenWidth;
    private const int MaximumFrame = 1 << 24; // sanity cap (~16 MiB)

    private readonly ITransportSite _site;
    private readonly object _writeLock = new();
    private readonly object _gate = new();
    private readonly Dictionary<ConnectionId, NetworkStream> _peers = new();
    private readonly List<Thread> _threads = new();
    private TcpListener? _listener;
    private NetworkStream? _clientStream;
    private bool _isHost;
    private volatile bool _running;
    private ushort _boundPort;
    private int _nextPeerId = 1; // host id fountain (client self is 0)

    public TcpTransport(ITransportSite site) => _site = site;

    public bool IsConnected { get; private set; }
    public ConnectionId LocalId { get; private set; } = ConnectionId.Me;
    public ushort BoundPort => _boundPort;

    public void Host(ushort port = 0)
    {
        if (_running) throw new InvalidOperationException("transport already running");
        _isHost = true;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _boundPort = (ushort)((IPEndPoint)_listener.LocalEndpoint).Port;
        _running = true;
        IsConnected = true;
        Spawn(ReceiveLoopStart, "accept");
    }

    public void Join(string address, ushort port, string playerName)
    {
        if (_running) throw new InvalidOperationException("transport already running");
        var client = new TcpClient();
        client.Connect(address, port);
        // Disable Nagle's algorithm (off by default in .NET's TcpClient). This
        // protocol sends frequent small messages (controls every ~50ms, ship
        // updates every tick) — over loopback the resulting Nagle+delayed-ACK
        // interaction (classically ~40ms per small exchange) is negligible at
        // near-zero RTT, but over any REAL network path it compounds into
        // clearly perceptible input lag and slow joins — confirmed live
        // (2026-09-15) on a real LAN connection with zero artificial latency
        // injected.
        client.NoDelay = true;
        _clientStream = client.GetStream();
        _isHost = false;
        _running = true;
        IsConnected = true;
        LocalId = ConnectionId.Me; // client self id 0, mirroring Messages.cpp:1603
        // publish our name first — the host reads it before handing us an id
        WriteFrame(_clientStream, FrameType.Greet, Encoding.UTF8.GetBytes(playerName));
        Spawn(() => ReadDataLoop(_clientStream!, ConnectionId.Server), "client-read");
    }

    public void Send(ConnectionId target, ReadOnlySpan<byte> data, bool guaranteed)
    {
        NetworkStream s;
        lock (_gate)
        {
            if (_isHost)
            {
                // Host-half race window (P60): a peer's read loop may already have
                // reaped it (removing the id from _peers in its finally) before the
                // sim thread has drained the matching PeerLeftEvt and removed it
                // from the broadcast set. Landing in that window is an expected
                // outcome of a peer departing mid-tick, not a bug — and throwing
                // here escalates one peer's drop into killing the whole server
                // (ServerMain.RunAsync's outer catch prints and exits 1). So drop
                // silently; the peer's own "[conn] peer N left" line is logged once
                // when the event drains, no per-packet spam.
                if (!_peers.TryGetValue(target, out s!))
                    return; // already gone — nothing to send
            }
            else
            {
                s = _clientStream
                    ?? throw new InvalidOperationException("not connected");
            }
        }
        try
        {
            WriteFrame(s, FrameType.Data, data);
        }
        catch (IOException) { }            // dead socket mid-send
        catch (SocketException) { }        //   "       "     "
        catch (ObjectDisposedException) { } // socket disposed by shutdown
    }

    public void Shutdown()
    {
        if (!_running) return;
        _running = false;
        IsConnected = false;
        try { _listener?.Stop(); } catch { /* already stopped */ }

        NetworkStream[] streams;
        Thread[] threads;
        lock (_gate)
        {
            streams = _peers.Values.Where(s => s is not null).Append(_clientStream!)
                              .Where(s => s is not null).Distinct().ToArray();
            _peers.Clear();
            _clientStream = null;
            threads = _threads.ToArray();
        }
        foreach (var s in streams) CloseQuiet(s);
        foreach (var th in threads) th.Join(500);
    }

    // ---- host accept loop ----------------------------------------------
    private void ReceiveLoopStart()
    {
        TcpListener l = _listener!;
        try
        {
            while (_running)
            {
                TcpClient client = l.AcceptTcpClient();
                client.NoDelay = true; // see Join's matching remark — both ends need this
                HandlePeer(client);
            }
        }
        catch (SocketException) { /* listener stopped */ }
        catch (ObjectDisposedException) { /* disposed */ }
    }

    private void HandlePeer(TcpClient client)
    {
        NetworkStream? stream = null;
        ConnectionId id = default;
        try
        {
            stream = client.GetStream();
            (byte type, byte[] body) = ReadFrame(stream);   // peer's greeting
            if (type != (byte)FrameType.Greet) { CloseQuiet(stream); client.Dispose(); return; }
            string name = Encoding.UTF8.GetString(body);
            id = new ConnectionId(unchecked((uint)_nextPeerId++));
            lock (_gate) _peers[id] = stream;
            // Constraint: OnPeerJoined BEFORE the app sees any datagram. The
            // greeting above was consumed internally; OnReceive cannot fire
            // until this call has returned.
            _site.OnPeerJoined(id, name);
        }
        catch (Exception)
        {
            if (stream != null) CloseQuiet(stream);
            client.Dispose();
            return;
        }

        NetworkStream peer = stream!;
        ConnectionId peerId = id;
        Spawn(() =>
        {
            try
            {
                ReadDataLoop(peer, peerId);
            }
            finally
            {
                lock (_gate) _peers.Remove(peerId);
                CloseQuiet(peer); client.Dispose();
                _site.OnPeerLeft(peerId);
            }
        }, $"peer-{peerId}");
    }

    // ---- read loop -------------------------------------------------------
    private void ReadDataLoop(NetworkStream s, ConnectionId from)
    {
        try
        {
            while (_running)
            {
                (byte type, byte[] body) = ReadFrame(s);
                if (type != (byte)FrameType.Data) { CloseQuiet(s); return; }
                _site.OnReceive(from, body);
            }
        }
        catch (EndOfStreamException) { /* remote closed */ }
        catch (IOException) { /* remote closed */ }
        catch (SocketException) { /* closed */ }
        catch (ObjectDisposedException) { /* closed */ }
        catch (ArgumentException) { /* invalid codec body forwarded anyway */ }
    }

    // ---- framing primitives ---------------------------------------------

    private void WriteFrame(NetworkStream s, FrameType type, ReadOnlySpan<byte> body)
    {
        Span<byte> hdr = stackalloc byte[HeaderWidth];
        hdr[0] = (byte)type;
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(1), (uint)body.Length);
        lock (_writeLock)
        {
            s.Write(hdr); s.Write(body);
        }
    }

    private static (byte Type, byte[] Body) ReadFrame(NetworkStream s)
    {
        Span<byte> hdr = stackalloc byte[HeaderWidth];
        ReadExactly(s, hdr);
        byte type = hdr[0];
        uint lenRaw = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(1));
        if (lenRaw > MaximumFrame)
            throw new IOException($"frame length {lenRaw} exceeds safety cap {MaximumFrame}");
        var body = new byte[(int)lenRaw];
        ReadExactly(s, body);
        return (type, body);
    }

    private static void ReadExactly(Stream s, Span<byte> dst)
    {
        int off = 0;
        while (off < dst.Length)
        {
            int n = s.Read(dst.Slice(off, dst.Length - off));
            if (n <= 0) throw new EndOfStreamException("peer closed mid-frame");
            off += n;
        }
    }

    private static void CloseQuiet(Stream s) { try { s.Close(); } catch { } }

    private void Spawn(Action a, string name)
    {
        var t = new Thread(() => a()) { IsBackground = true, Name = name };
        lock (_gate) _threads.Add(t);
        t.Start();
    }
}
