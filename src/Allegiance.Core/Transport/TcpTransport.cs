using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Allegiance.Core.Transport;

/// <summary>
/// P605 — how the server drains its per-peer outbound send queues.
/// <see cref="TransportMode.ThreadPerPeer"/> is today's behaviour: each peer's
/// <see cref="PeerSender"/> owns a dedicated <c>peer-send</c> writer thread.
/// <see cref="TransportMode.Pool"/> replaces those per-peer writer threads with
/// a fixed pool of workers that drain hash-partitioned per-peer queues
/// (round-robin, per-peer order preserved), so a 1000+-peer server does not
/// need 1000+ dedicated send threads. Selected at server start via
/// <c>transport.mode</c> config / <c>ALLEGIANCE_TRANSPORT_MODE</c>; default
/// thread-per-peer (today's behaviour, unchanged).
/// </summary>
public enum TransportMode
{
    ThreadPerPeer = 0,
    Pool = 1,
}

/// <summary>
/// P606 — why a peer's connection ended. Previously the transport recorded no
/// reason, so a mass disconnect (the 2026-10-11 390-bot live test: 346 of ~392
/// connections dropped, "SESSION LOST", with no explanation) could not be told
/// apart from a graceful logout. Every disconnect now stamps exactly one of
/// these and it is logged (one line per peer) plus summed per report window.
/// </summary>
public enum DisconnectReason
{
    None = 0,
    /// <summary>The peer's guaranteed backlog sat over capacity for the whole
    /// <see cref="TcpTransport.OverflowTimeout"/> window (nothing droppable
    /// left to shed) — the client genuinely cannot keep up.</summary>
    QueueOverflow,
    /// <summary>A blocking socket write failed or hit the socket's SendTimeout
    /// (dead / black-holed peer).</summary>
    SendError,
    /// <summary>The peer's read loop ended: remote closed (EOF) or a read
    /// error/socket error on the connection.</summary>
    ReadErrorOrEof,
    /// <summary>A malformed frame / wrong frame type / oversize greeting was
    /// received during the join handshake.</summary>
    ProtocolError,
    /// <summary>A peer connected and sent nothing (or a partial frame) within
    /// the greeting timeout.</summary>
    GreetTimeout,
    /// <summary>The server shut down the session (explicit Shutdown).</summary>
    Shutdown,
}

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
/// ## Synchronisation model (P311 revision)
///
/// **Outbound sends are now non-blocking from the caller's perspective.** A
/// per-peer bounded send queue (<see cref="PeerSender"/>) with a dedicated
/// background writer thread sits between the caller and the socket. Every
/// packet the server emits flows through <c>SendPeer → ServerHost.Send →
/// TcpTransport.Send</c> on the single simulation thread; <c>Send</c> only
/// enqueues a fully-framed byte[] and returns immediately — it never performs
/// socket I/O, so one peer whose client has stalled (stopped reading) can no
/// longer block the sim thread and freeze every player (review finding C1).
///
/// The writer thread performs the real blocking <c>Write</c>, serialized per
/// peer so per-peer FIFO order is preserved. As a second layer of defense the
/// underlying <c>TcpClient</c> gets a real <c>SendTimeout</c> (default of 0 =
/// infinite would let even the writer hang forever) and a modest
/// <c>SendBufferSize</c> so a genuinely unresponsive peer's OS send buffer
/// fills quickly and surfaces the backlog. Overflow policy — see
/// <see cref="PeerSender"/>: non-guaranteed frames are dropped oldest-first
/// under pressure (best-effort traffic may be shed), guaranteed traffic is
/// never silently dropped, and a peer whose guaranteed backlog stays over
/// capacity for a sustained interval is disconnected outright rather than
/// letting the queue grow unbounded (trading a freeze for a memory leak).
///
/// Reads remain blocking on background threads (one accept loop on the host;
/// one read loop per peer). Site callbacks happen on those threads; an
/// implementor wanting single-owner dispatch funnels them through a queue,
/// exactly as FedMessaging's <c>m_csMsgList</c> serializer does. The accept
/// loop never performs a blocking read itself: each accepted connection's
/// greeting read runs on its own thread (P312), so a stalled greet can block
/// only that connection — never the next <c>AcceptTcpClient()</c> — and a
/// socket <c>ReceiveTimeout</c> bounds even that read.
/// </summary>
public sealed class TcpTransport : ITransport
{
    private enum FrameType : byte { Greet = 0x01, Data = 0x02 }

    private const int LenWidth = 4;
    private const int HeaderWidth = 1 + LenWidth;
    private const int MaximumFrame = 1 << 24; // sanity cap (~16 MiB)

    // P312: the greeting body is just a player name (a few dozen bytes), so a
    // staller must never be able to claim a 16 MiB greeting and force that
    // allocation before the length is even plausibly backed by real data. This
    // cap is enforced BEFORE any body allocation in ReadFrame (see HandlePeer).
    private const uint MaximumGreeting = 4096;

    // P311: a real send timeout on the socket (layer 2 of defense — even the
    // writer thread's blocking Write gives up rather than hanging forever) and
    // a modest send buffer so a stalled peer's backlog surfaces quickly. The
    // OS-default SendTimeout of 0 (infinite) is precisely the bug this fixes.
    private const int SendTimeoutMs = 10_000;
    private const int SendBufferSize = 8 * 1024;

    // P605 — pool mode: a much tighter per-write socket timeout than the 10 s
    // thread-per-peer default. Each peer's whole-frame write runs on a SHARED
    // worker, so a stalled peer must give up its blocking write almost
    // immediately (and be dropped) rather than tying up the worker (and every
    // neighbour behind it) for up to 10 s. Healthy writes are instantaneous, so
    // this budget never bites a normal peer. Steady-state protection is the
    // not-writable skip in <see cref="PeerSender.TryWriteHead"/>; this bounds
    // only the rare write-already-started-then-stalled transient.
    private const int PoolSendTimeoutMs = 250;

    // P312: the default greeting-read timeout on an accepted socket (10 s). A
    // real client sends its name in microseconds on connect, so this is very
    // generous for legitimate joins; its sole purpose is to make a connection
    // that sends nothing (or a partial frame) give up and be dropped cleanly
    // instead of parking forever on a per-connection greeting thread. Note the
    // timeout is reset to 0 (infinite) right after a successful greeting so an
    // idle-but-healthy data session is never dropped for going quiet.
    private const int ReceiveTimeoutMs = 10_000;

    /// <summary>P311 test seam: how long a peer's guaranteed backlog may sit over
    /// capacity before the peer is disconnected outright. Internal so the test
    /// suite can tighten it for a fast, deterministic overflow test.</summary>
    internal static TimeSpan OverflowTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>P311 test seam: the per-peer queued-byte bound (8 MiB in
    /// production). Internal so tests can shrink it to force an overflow
    /// deterministically on loopback, where OS socket buffers otherwise swallow
    /// a burst.</summary>
    internal static long CapacityBytes { get; set; } = 8L * 1024 * 1024;

    /// <summary>P312 test seam: how long an accepted peer may connect and send
    /// nothing (or a partial frame) before its greeting read gives up and the
    /// connection is dropped. Internal so the test suite can shorten it for a
    /// fast, deterministic greeting-timeout test.</summary>
    internal static TimeSpan GreetTimeout { get; set; } = TimeSpan.FromSeconds(ReceiveTimeoutMs / 1000.0);

    private readonly ITransportSite _site;
    private readonly object _gate = new();
    private readonly Dictionary<ConnectionId, PeerSender> _peers = new();
    private readonly List<Thread> _threads = new();
    // P605 — send-draining mode and (in pool mode) the fixed worker pool.
    // The client half always uses a dedicated writer (a single connection gains
    // nothing from the pool), so _pool is only ever populated on the host.
    private readonly TransportMode _mode;
    private readonly int _poolWorkerCount;
    private SendWorkerPool? _pool;
    // P605 test seam (InternalsVisibleTo): the mode a transport constructed
    // without an explicit mode argument falls back to. Lets the transport test
    // suite run the SAME body under both modes. Default thread-per-peer =
    // today's behaviour.
    internal static TransportMode DefaultMode { get; set; } = TransportMode.ThreadPerPeer;
    // Exactly-once guard so <c>OnPeerLeft</c> fires once even when both the peer's
    // read loop and the overflow/disconnect path (DropPeer) try to reap it.
    private readonly ConcurrentDictionary<ConnectionId, byte> _peerEnded = new();
    // P606 — per-window disconnect counts by reason. Incremented on every peer
    // disconnect (reader thread / sender thread), drained by the server each
    // report window via <see cref="DrainDisconnectCounts"/> so the profiler log
    // shows a "by reason" summary. Thread-safe (disconnects are not on the sim
    // thread).
    private readonly ConcurrentDictionary<DisconnectReason, int> _disconnectCounts = new();
    private TcpListener? _listener;
    private NetworkStream? _clientStream;
    private PeerSender? _clientSender;
    private bool _isHost;
    private volatile bool _running;
    // P320 — set in Shutdown() so the client read-loop's finally knows the
    // close was intentional and must NOT raise OnSessionLost (only an
    // unexpected server drop / network loss is a session loss worth surfacing).
    private volatile bool _intentionalShutdown;
    // P320 — exactly-once guard so OnSessionLost fires once per lost session
    // even though several paths (read-loop finally, DropClient) can unwind.
    private volatile bool _clientSessionLostRaised;
    // P606 — the exception / reason that ended the client's read loop, so a
    // "SESSION LOST" is logged WITH its cause instead of silently.
    private volatile string? _clientReadFailure;
    private ushort _boundPort;
    private int _nextPeerId; // host id fountain (client self is 0); minted via Interlocked.Increment in HandlePeer

    public TcpTransport(ITransportSite site)
        : this(site, DefaultMode, poolWorkers: 0) { }

    /// <summary>
    /// P605 — construct with an explicit send-draining mode and, for pool mode,
    /// the number of pool workers (0 = the default, about the core count).
    /// The server passes its resolved <c>transport.mode</c> here at boot.
    /// </summary>
    public TcpTransport(ITransportSite site, TransportMode mode, int poolWorkers = 0)
    {
        _site = site;
        _mode = mode;
        _poolWorkerCount = poolWorkers > 0
            ? poolWorkers
            : Math.Max(1, Environment.ProcessorCount);
    }

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
        // P605 — pool mode spins up the fixed worker pool up-front; peer
        // senders then register with it instead of spawning per-peer threads.
        if (_mode == TransportMode.Pool)
            _pool = new SendWorkerPool(_poolWorkerCount);
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
        // P311: the client half also gets a real send timeout + a send queue,
        // so a dead server never blocks the client caller either.
        client.SendTimeout = SendTimeoutMs;
        _clientStream = client.GetStream();
        _isHost = false;
        _running = true;
        IsConnected = true;
        LocalId = ConnectionId.Me; // client self id 0, mirroring Messages.cpp:1603
        // Set up the per-connection send queue; the greeting travels through it
        // (first in the queue, so ordering is preserved) instead of a raw write.
        // P606 — the client half stamps a reason on its sender's death, but
        // DropClient needs none (it just tears the socket down so the read loop
        // unwinds and surfaces OnSessionLost with the real reason).
        var sender = new PeerSender(_clientStream, client, _ => DropClient());
        lock (_gate) _clientSender = sender;
        sender.Start();
        sender.Enqueue(BuildFrame(FrameType.Greet, Encoding.UTF8.GetBytes(playerName)), guaranteed: true, coalesceKey: 0);
        Spawn(() =>
        {
            // P320 — the client read loop ending (server closed / socket error /
            // network drop) is the client's ONLY detection of a lost session: it
            // surfaces OnSessionLost (exactly once, unless this was an intentional
            // Shutdown) so the app can stop sending and show a disconnect UI
            // instead of silently "flying" a dead world.
            try { ReadDataLoop(_clientStream!, ConnectionId.Server); }
            finally { RaiseClientSessionLostIfFirst(); }
        }, "client-read");
    }

    public void Send(ConnectionId target, ReadOnlySpan<byte> data, bool guaranteed)
        => Send(target, data, guaranteed, coalesceKey: 0);

    /// <summary>
    /// P606 — <see cref="Send"/> with an optional per-peer coalesce key. When
    /// <paramref name="coalesceKey"/> is non-zero the packet is treated as a
    /// <b>latest-wins</b> stream frame: if this peer's queue already holds a
    /// pending frame for the same key (e.g. a previous tick's ship-state batch),
    /// the older one is <em>superseded in place</em> by the new one, so a slow
    /// client's queue keeps at most the newest pending entry per key instead of
    /// accumulating every tick's stale snapshot. Zero = ordinary ordered traffic
    /// (chat, kills, joins, events) which is never coalesced.
    /// </summary>
    public void Send(ConnectionId target, ReadOnlySpan<byte> data, bool guaranteed, int coalesceKey)
    {
        PeerSender? ps;
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
                if (!_peers.TryGetValue(target, out ps!))
                    return; // already gone — nothing to send
            }
            else
            {
                ps = _clientSender
                    ?? throw new InvalidOperationException("not connected");
            }
        }
        // Enqueue-and-return: never blocks on socket I/O. A dead/disconnected
        // peer's sender no-ops its enqueue internally.
        ps.Enqueue(BuildFrame(FrameType.Data, data), guaranteed, coalesceKey);
    }

    /// <summary>
    /// P606 — <see cref="SendCoalescing"/> sends a latest-wins stream frame as
    /// <b>non-guaranteed</b> (best-effort): it is the natural overflow-shed
    /// candidate (native sends HEAVY/LIGHT_SHIPS_UPDATE with FM_NOT_GUARANTEED,
    /// FedSrv.CPP:4189) and it supersedes any older pending frame with the same
    /// key, so a slow client's queue stays small and current instead of
    /// accumulating stale ship-state snapshots.
    /// </summary>
    public void SendCoalescing(ConnectionId target, ReadOnlySpan<byte> data, int coalesceKey)
        => Send(target, data, guaranteed: false, coalesceKey);

    /// <summary>P608 — coalesce-keyed latest-wins frame sent GUARANTEED (never
    /// shed under capacity, but still superseded in place per key so the backlog
    /// stays bounded). See <see cref="ITransport.SendGuaranteedCoalescing"/>.</summary>
    public void SendGuaranteedCoalescing(ConnectionId target, ReadOnlySpan<byte> data, int coalesceKey)
        => Send(target, data, guaranteed: true, coalesceKey);

    public void Shutdown()
    {
        if (!_running) return;
        // P320 — flag the close as intentional so the client read-loop's finally
        // (which unwinds as we close the socket) does NOT raise OnSessionLost: a
        // deliberate Shutdown is not a connection loss.
        _intentionalShutdown = true;
        _running = false;
        IsConnected = false;
        try { _listener?.Stop(); } catch { /* already stopped */ }

        PeerSender[] senders;
        Thread[] threads;
        lock (_gate)
        {
            senders = _peers.Values.Append(_clientSender!)
                              .Where(s => s is not null).Distinct().ToArray();
            _peers.Clear();
            _clientSender = null;
            threads = _threads.ToArray();
        }
        foreach (var s in senders) s.Close();
        foreach (var th in threads) th.Join(500);
        // P605 — stop the worker pool (joins its N threads) so no pooled send
        // thread or queued buffer outlives the transport.
        _pool?.Shutdown();
        _pool = null;
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
                ConfigureAcceptedSocket(client);
                // P312: the greeting read now runs on ITS OWN thread per
                // connection (HandlePeer), so AcceptTcpClient() returns
                // immediately. Previously HandlePeer ran inline here and its
                // synchronous greeting read could park this accept thread
                // forever — one connection that connects and sends nothing
                // silently blocked every new join (review finding C2). With the
                // read off the accept loop, a stalled greet blocks only that
                // connection's own thread and the loop keeps accepting.
                var greet = new Thread(() => HandlePeer(client))
                { IsBackground = true, Name = "greet" };
                greet.Start();
            }
        }
        catch (SocketException) { /* listener stopped */ }
        catch (ObjectDisposedException) { /* disposed */ }
    }

    /// <summary>Configure a newly-accepted peer socket: disable Nagle (both ends
    /// need it), bound the send path (P311: SendTimeout + a modest send buffer so
    /// a stalled consumer surfaces promptly), and — new in P312 — a real
    /// <c>ReceiveTimeout</c> so the greeting read gives up instead of parking
    /// forever if a peer connects and sends nothing (finding C2).</summary>
    private static void ConfigureAcceptedSocket(TcpClient client)
    {
        client.NoDelay = true; // see Join's matching remark — both ends need this
        // P311: bound the accepted peer's send buffer and give its socket
        // a real send timeout so a stalled consumer surfaces promptly.
        client.SendTimeout = SendTimeoutMs;
        client.SendBufferSize = SendBufferSize;
        // P312: bound the greeting read. This is cleared back to 0 (infinite)
        // in HandlePeer right after a successful greeting, so it applies only to
        // the join handshake, never to the peer's data session.
        client.ReceiveTimeout = (int)GreetTimeout.TotalMilliseconds;
    }

    private void HandlePeer(TcpClient client)
    {
        NetworkStream? stream = null;
        ConnectionId id = default;
        try
        {
            stream = client.GetStream();
            // The greeting body is just a player name, so cap it well below the
            // generic MaximumFrame: a staller claiming a huge greeting length
            // it never sends is rejected here (before any big allocation),
            // closing the C2 compounding vector. A length within cap but whose
            // body never arrives is still bounded by the socket's ReceiveTimeout
            // (set in ConfigureAcceptedSocket), so it drops rather than parking.
            (byte type, byte[] body) = ReadFrame(stream, MaximumGreeting);   // peer's greeting
            if (type != (byte)FrameType.Greet)
            {
                // P606 — a connection that speaks a non-greet frame is a
                // protocol violation (not a client we can talk to).
                CloseQuiet(stream); client.Dispose();
                RecordDisconnectRaw(DisconnectReason.ProtocolError, "peer sent non-greet frame");
                return;
            }
            // Greeting done and the peer is legitimate: restore indefinite reads
            // for its data session, so an idle-but-healthy player is never
            // dropped merely for not sending within the greeting timeout.
            client.ReceiveTimeout = 0;
            string name = Encoding.UTF8.GetString(body);
            // HandlePeer now runs on a per-connection thread, so the host id
            // fountain must be minted atomically — two simultaneous greets must
            // never produce the same id. (_nextPeerId starts at 0 and
            // Interlocked.Increment yields 1 for the first peer.)
            id = new ConnectionId(unchecked((uint)Interlocked.Increment(ref _nextPeerId)));
            var sender = new PeerSender(stream, client, r => DropPeer(id, r));
            lock (_gate) _peers[id] = sender;
            StartOrPool(id, sender);
            // Constraint: OnPeerJoined BEFORE the app sees any datagram. The
            // greeting above was consumed internally; OnReceive cannot fire
            // until this call has returned.
            _site.OnPeerJoined(id, name);
        }
        catch (Exception ex)
        {
            // P606 — the greeting read failed: the peer connected but sent
            // nothing (ReceiveTimeout → GreetTimeout), closed early (EOF →
            // ReadErrorOrEof), or sent a bad/oversize frame (ProtocolError).
            DisconnectReason gr;
            if (ex is EndOfStreamException) gr = DisconnectReason.ReadErrorOrEof;
            else if (IsReadTimeout(ex)) gr = DisconnectReason.GreetTimeout;
            else gr = DisconnectReason.ProtocolError;
            if (stream != null) CloseQuiet(stream);
            client.Dispose();
            RecordDisconnectRaw(gr, $"greeting failed ({gr})");
            return;
        }

        NetworkStream peer = stream!;
        ConnectionId peerId = id;
        PeerSender peerSender = _peers[peerId];
        Spawn(() =>
        {
            try
            {
                ReadDataLoop(peer, peerId);
            }
            finally
            {
                // The read loop ending (remote closed / socket error) also reaps
                // the peer; OnPeerLeft fires exactly once thanks to _peerEnded.
                lock (_gate) _peers.Remove(peerId);
                if (_mode == TransportMode.Pool) _pool?.Unassign(peerSender);
                peerSender.Close();
                // P606 — the read loop terminating IS a disconnect; record its
                // reason (a clean server Shutdown is the only non-error close).
                bool intentional = _intentionalShutdown;
                RaisePeerLeftIfFirst(peerId, intentional ? DisconnectReason.Shutdown : DisconnectReason.ReadErrorOrEof,
                                     logLine: !intentional);
            }
        }, $"peer-{peerId}");
    }

    /// <summary>P605 — start a peer's send path according to the transport's
    /// mode: a dedicated <c>peer-send</c> writer thread (thread-per-peer,
    /// today's behaviour) or registration with the fixed worker pool.</summary>
    private void StartOrPool(ConnectionId id, PeerSender sender)
    {
        if (_mode == TransportMode.Pool && _pool is { } pool)
            pool.Assign(id, sender);
        else
            sender.Start();
    }

    /// <summary>A peer's sender hit its overflow/timeout policy or its socket
    /// died: tear the peer down. Closing the stream unblocks the peer's read
    /// loop, and <see cref="ITransportSite.OnPeerLeft"/> fires here (exactly
    /// once) rather than depending on the read loop noticing — so an overflow
    /// disconnect always surfaces to the server even if the read loop is parked
    /// on a stalled socket.</summary>
    private void DropPeer(ConnectionId id, DisconnectReason reason)
    {
        PeerSender? ps = null;
        lock (_gate) { if (_peers.TryGetValue(id, out ps)) _peers.Remove(id); }
        if (ps != null)
        {
            if (_mode == TransportMode.Pool) _pool?.Unassign(ps);
            ps.Close();
        }
        RaisePeerLeftIfFirst(id, reason, logLine: true);
    }

    /// <summary>Raise <see cref="ITransportSite.OnPeerLeft"/> exactly once per
    /// peer, whichever of the read-loop finally / DropPeer gets there first.
    /// The <paramref name="reason"/> is counted into the per-window disconnect
    /// summary (and, unless suppressed for a benign close, logged one line per
    /// peer) the FIRST time this peer is reaped — so exactly one reason is
    /// attributed to each disconnect.</summary>
    private void RaisePeerLeftIfFirst(ConnectionId id, DisconnectReason reason, bool logLine)
    {
        if (_peerEnded.TryAdd(id, 0))
        {
            _disconnectCounts.AddOrUpdate(reason, 1, (_, c) => c + 1);
            if (logLine)
                Console.WriteLine($"[conn-drop] peer {id} left reason={reason}");
            _site.OnPeerLeft(id);
        }
    }

    /// <summary>P606 — record a disconnect that never earned a connection id
    /// (a failed greeting), so it still shows in the by-reason summary.</summary>
    private void RecordDisconnectRaw(DisconnectReason reason, string detail)
    {
        _disconnectCounts.AddOrUpdate(reason, 1, (_, c) => c + 1);
        Console.WriteLine($"[conn-drop] (pre-join) {detail} reason={reason}");
    }

    /// <summary>Does <paramref name="ex"/> (or its inner exception) indicate a
    /// socket receive timeout?</summary>
    private static bool IsReadTimeout(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is SocketException se && se.SocketErrorCode == SocketError.TimedOut)
                return true;
        return false;
    }

    /// <summary>P606 — drain (and clear) the per-window disconnect counts by
    /// reason. Called by the server each report window so the profiler log gets
    /// a "disconnects by reason" line; resets the window counter.</summary>
    public IReadOnlyDictionary<DisconnectReason, int> DrainDisconnectCounts()
    {
        var snap = new Dictionary<DisconnectReason, int>();
        foreach (var kv in _disconnectCounts) snap[kv.Key] = kv.Value;
        _disconnectCounts.Clear();
        return snap;
    }

    /// <summary>P606 test seam: total disconnects recorded since boot (all
    /// reasons, never drained) — lets a test assert a disconnect occurred and
    /// its reason without racing the server's window drain.</summary>
    internal int TotalDisconnects()
    {
        int total = 0;
        foreach (var v in _disconnectCounts.Values) total += v;
        return total;
    }

    /// <summary>The client-half sender died (server closed / send timeout):
    /// close the client stream so the client read loop unwinds.</summary>
    private void DropClient()
    {
        PeerSender? ps;
        lock (_gate) { ps = _clientSender; _clientSender = null; }
        ps?.Close();
    }

    /// <summary>
    /// P320 — the client half's connection to the server dropped unexpectedly
    /// (server closed / socket error / network loss): set <see cref="IsConnected"/>
    /// false and raise <see cref="ITransportSite.OnSessionLost"/> exactly once so
    /// the app can stop sending controls and surface a disconnect UI instead of
    /// silently rendering a frozen world. Suppressed during an intentional
    /// <see cref="Shutdown"/>, which closes the socket by design.
    /// </summary>
    private void RaiseClientSessionLostIfFirst()
    {
        if (_intentionalShutdown) return;
        if (_clientSessionLostRaised) return;
        _clientSessionLostRaised = true;
        IsConnected = false;
        // P606 — log WHY the session was lost (the read-loop's recorded failure)
        // so a client-side "SESSION LOST" is never unexplained again.
        Console.WriteLine($"[client] SESSION LOST reason={_clientReadFailure ?? "read loop ended"}");
        _site.OnSessionLost();
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
        catch (EndOfStreamException) { _clientReadFailure = "server closed connection (EOF)"; }
        catch (IOException) { _clientReadFailure = "read I/O error"; }
        catch (SocketException) { _clientReadFailure = "socket error"; }
        catch (ObjectDisposedException) { _clientReadFailure = "connection disposed"; }
        catch (ArgumentException) { _clientReadFailure = "invalid frame"; }
    }

    // ---- framing primitives ---------------------------------------------

    /// <summary>Assemble one complete wire frame (type byte + 4-byte length +
    /// body) into a single buffer. The frame is handed wholesale to a peer's
    /// send queue so the writer thread can emit it atomically — a frame is
    /// never split across queue items or interleaved with another peer's.</summary>
    private static byte[] BuildFrame(FrameType type, ReadOnlySpan<byte> body)
    {
        var frame = new byte[HeaderWidth + body.Length];
        frame[0] = (byte)type;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1), (uint)body.Length);
        body.CopyTo(frame.AsSpan(HeaderWidth));
        return frame;
    }

    private static (byte Type, byte[] Body) ReadFrame(NetworkStream s, uint maxBodyLen = MaximumFrame)
    {
        Span<byte> hdr = stackalloc byte[HeaderWidth];
        ReadExactly(s, hdr);
        byte type = hdr[0];
        uint lenRaw = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(1));
        if (lenRaw > maxBodyLen)
            throw new IOException($"frame length {lenRaw} exceeds safety cap {maxBodyLen}");
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
        var t = new Thread(() =>
        {
            try
            {
                a();
            }
            finally
            {
                // M9 — prune this thread from _threads when it terminates (its
                // connection/loop ended), so a long-uptime server with
                // connection churn never accumulates an unbounded list of dead
                // Thread objects. Thread.CurrentThread is this spawned thread
                // object; _threads.Add(t) in the outer frame strictly precedes
                // t.Start(), so the add always happens before this remove.
                lock (_gate) _threads.Remove(Thread.CurrentThread);
            }
        })
        { IsBackground = true, Name = name };
        lock (_gate) _threads.Add(t);
        t.Start();
    }

    /// <summary>M9 test seam (InternalsVisibleTo): number of thread references
    /// still tracked in <c>_threads</c>. With the <see cref="Spawn"/>
    /// self-prune this stays bounded by the live thread set (just the accept
    /// loop on a host) rather than growing with every connection.</summary>
    internal int TrackedThreadCount() { lock (_gate) return _threads.Count; }

    /// <summary>P605 test seam (InternalsVisibleTo): the number of send-pool
    /// worker threads this host owns (0 in thread-per-peer mode). Lets a test
    /// assert the pool stays at about the configured worker count regardless
    /// of how many peers connect.</summary>
    internal int PoolWorkerThreadCount() => _pool?.WorkerCount ?? 0;

    /// <summary>P605 test seam: true when this transport runs its sends through
    /// the fixed worker pool rather than per-peer writer threads.</summary>
    internal bool IsPoolMode => _mode == TransportMode.Pool;

    /// <summary>
    /// The per-peer outbound path: a bounded FIFO send queue drained by a
    /// dedicated background writer thread. The caller (<see cref="Send"/> on the
    /// sim thread) only enqueues a fully-framed byte[] and returns immediately.
    ///
    /// **Bounds &amp; overflow policy**
    /// - <see cref="CapacityBytes"/> caps queued bytes. Under pressure the
    ///   oldest <em>non-guaranteed</em> frames are dropped first (best-effort
    ///   traffic may be shed to make room), so normal fills never lose a
    ///   guaranteed message.
    /// - Guaranteed frames are <em>never</em> silently dropped. When only
    ///   guaranteed traffic remains over capacity — i.e. the client genuinely
    ///   cannot keep up and we have nothing droppable left — the backlog is
    ///   allowed to stay over capacity for at most <see cref="OverflowTimeout"/>
    ///   (measured on the enqueue side), after which the peer is disconnected
    ///   outright via <paramref name="onDead"/>. This prevents an unbounded
    ///   queue (a freeze traded for a memory leak): a truly stalled peer is
    ///   dropped, and a healthy one never sustains overflow.
    /// - Second layer: the socket's <c>SendTimeout</c> (set by the owner) makes
    ///   even the writer thread's blocking Write give up on a black-holed path.
    ///
    /// Per-peer FIFO order is preserved because a single writer thread drains
    /// the queue serially.
    /// </summary>
    private sealed class PeerSender
    {
        /// <summary>P605 — outcome of one worker attempt to drain this peer's
        /// head frame. Drives the pool's pump decision.</summary>
        public enum DrainResult { Idle, Wrote, Blocked, Dead }

        private readonly NetworkStream _stream;
        private readonly TcpClient _tcpClient;
        private readonly Action<DisconnectReason> _onDead;
        private readonly CancellationTokenSource _cts = new();
        private readonly LinkedList<QueuedFrame> _queue = new();
        private readonly object _sync = new();
        // P606 — latest-wins coalescing: coalesceKey -> the queue node holding
        // that key's newest pending frame. Lets Enqueue supersede an older
        // pending stream frame in place (O(1)) instead of queueing every stale
        // snapshot; removed when the writer pops the node.
        private readonly Dictionary<int, LinkedListNode<QueuedFrame>> _coalesceNodes = new();
        private long _queuedBytes;
        private DateTime? _overflowSinceUtc;
        // P606 — how long the queue has been continuously non-empty (never
        // fully drained). Cleared whenever the writer empties it; a peer whose
        // queue stays non-empty for the whole OverflowTimeout is stuck (its
        // socket hasn't accepted a write in that long) and is disconnected.
        // This is what keeps coalescing from turning a forever-stalled peer
        // into a silent zombie: coalescing shrinks the backlog, but a peer that
        // never drains is still dead and gets dropped (anti-Zombie fail-safe).
        private DateTime? _undrainedSinceUtc;
        private volatile bool _disconnected;
        private volatile DisconnectReason _reason;
        // P605 — pool mode: the owning worker's Wake callback, pulsed when a
        // frame is enqueued so the pool never busy-waits. Null in
        // thread-per-peer mode, where the dedicated writer is woken by Pulse.
        private Action? _wake;
        // P605 — pool mode: the worker this peer is partitioned to (used to
        // unassign on leave). Null in thread-per-peer mode.
        internal SendWorkerPool.Worker? OwnerWorker;

        public PeerSender(NetworkStream stream, TcpClient tcpClient, Action<DisconnectReason> onDead)
        {
            _stream = stream;
            _tcpClient = tcpClient;
            _onDead = onDead;
        }

        public void Start()
        {
            var t = new Thread(WriterLoop) { IsBackground = true, Name = "peer-send" };
            t.Start();
        }

        /// <summary>Queue a fully-framed datagram. Never blocks; no socket I/O.
        /// When <paramref name="coalesceKey"/> is non-zero, supersedes (in
        /// place) any older pending frame for the same peer+key — latest-wins —
        /// instead of appending a stale duplicate (P606).</summary>
        public void Enqueue(byte[] frame, bool guaranteed, int coalesceKey)
        {
            bool fireDisconnect = false;
            lock (_sync)
            {
                if (_disconnected) return;

                if (coalesceKey != 0 && _coalesceNodes.TryGetValue(coalesceKey, out var existing))
                {
                    // Latest-wins: an older pending frame for this same key is
                    // superseded. Replace in place so the collapsed frame keeps
                    // its queue position (relative order vs guaranteed messages
                    // is preserved) and the byte accounting stays exact.
                    _queuedBytes -= existing.Value.Frame.Length;
                    existing.Value = new QueuedFrame(frame, guaranteed, coalesceKey);
                    _queuedBytes += frame.Length;
                }
                else
                {
                    // Shed oldest best-effort frames while over capacity
                    // (guaranteed traffic is never dropped here). Streaming
                    // ship-state frames are sent non-guaranteed, so they are the
                    // natural shed candidates.
                    while (_queuedBytes + frame.Length > CapacityBytes && DropOldestNonGuaranteed()) { }
                    var node = _queue.AddLast(new QueuedFrame(frame, guaranteed, coalesceKey));
                    if (coalesceKey != 0) _coalesceNodes[coalesceKey] = node;
                    _queuedBytes += frame.Length;
                }
                Monitor.Pulse(_sync);

                // Overflow policy (P606 revision): the peer is disconnected only
                // when it is genuinely STUCK — never merely because its streaming
                // backlog is large (coalescing keeps that small). Two signals,
                // both gated on the whole OverflowTimeout so a healthy (if slow)
                // peer is never dropped for a brief burst:
                //   (a) a guaranteed-only backlog sits over capacity — the classic
                //       "cannot keep up" case (nothing droppable left);
                //   (b) the queue has been continuously non-empty (never fully
                //       drained) the whole window — the socket hasn't accepted a
                //       write in that long, so the peer is dead even though its
                //       (coalesced) backlog is small. Prevents a forever-stalled
                //       peer from lingering as a silent zombie now that
                //       coalescing keeps its queue under the byte cap.
                var now = DateTime.UtcNow;
                if (_queue.First is null)
                {
                    _undrainedSinceUtc = null;
                    _overflowSinceUtc = null;
                }
                else
                {
                    _undrainedSinceUtc ??= now;
                    _overflowSinceUtc = _queuedBytes > CapacityBytes ? (_overflowSinceUtc ?? now) : null;
                    if ((now - _undrainedSinceUtc) >= OverflowTimeout
                        || (_overflowSinceUtc is { } os && now - os >= OverflowTimeout))
                    {
                        _disconnected = true;
                        _reason = DisconnectReason.QueueOverflow;
                        fireDisconnect = true;
                    }
                }
            }

            if (fireDisconnect)
            {
                _cts.Cancel();
                _onDead(_reason);
            }
            else
            {
                // P605 — wake the owning pool worker (pool mode) so it pumps
                // this peer without polling. No-op in thread-per-peer mode
                // (the dedicated writer was already woken by Pulse above).
                _wake?.Invoke();
            }
        }

        /// <summary>P605 — register this peer's queue with a pool worker: from
        /// now on the worker drains it (via <see cref="TryWriteHead"/>) and is
        /// woken on every enqueue. No dedicated writer thread is spawned. Also
        /// tightens the socket write timeout so a stalled peer can't tie up the
        /// shared worker for the thread-per-peer 10 s default.</summary>
        public void AttachWorker(Action wake)
        {
            _wake = wake;
            try { _tcpClient.SendTimeout = PoolSendTimeoutMs; } catch { }
        }

        /// <summary>P605 — detach from the pool worker (peer leaving). Further
        /// enqueues no longer wake anyone; the worker has already forgotten us.</summary>
        public void DetachWorker() => _wake = null;

        /// <summary>P605 — non-blocking drain of this peer's head frame, called
        /// by a pool worker. Returns <see cref=\"DrainResult.Blocked\"/> (and
        /// sends nothing) when the peer's socket is not currently writable, so a
        /// slow/stalled client is skipped and never blocks the worker — other
        /// peers on the same worker keep flowing. Per-peer FIFO order is
        /// preserved because this peer's queue has exactly one consumer (its
        /// assigned worker) and only ever drains the head frame.</summary>
        public DrainResult TryWriteHead()
        {
            byte[] frame;
            lock (_sync)
            {
                if (_disconnected) return DrainResult.Dead;
                if (_queue.First is null) return DrainResult.Idle;
                if (!SocketReady()) return DrainResult.Blocked;   // skip — not writable now
                var node = _queue.First!;
                _queue.RemoveFirst();
                // P606 — a coalescable frame leaving the queue releases its key
                // so the NEXT enqueue for that key appends fresh instead of
                // pointing at this (now-consumed) node.
                if (node.Value.CoalesceKey != 0) _coalesceNodes.Remove(node.Value.CoalesceKey);
                frame = node.Value.Frame;
                _queuedBytes -= frame.Length;
                if (_queue.First is null) { _undrainedSinceUtc = null; _overflowSinceUtc = null; }
                else if (_queuedBytes <= CapacityBytes) _overflowSinceUtc = null;
            }
            // Outside the lock, like WriterLoop: the only blocking socket write.
            // SendTimeout on the socket bounds it even against a black-holed
            // path; because we only write when SocketReady said writable, a
            // healthy (consuming) peer never blocks here for long.
            try
            {
                _stream.Write(frame, 0, frame.Length);
                return DrainResult.Wrote;
            }
            catch (Exception)
            {
                // Socket error / SendTimeout / disposal -> this peer is dead.
                if (!_disconnected)
                {
                    _disconnected = true;
                    _reason = DisconnectReason.SendError;
                    _cts.Cancel();
                    _onDead(DisconnectReason.SendError);
                }
                return DrainResult.Dead;
            }
        }

        /// <summary>P605 — can this peer's socket take a write right now without
        /// blocking? Polls with a zero timeout (select-for-write) so a stalled
        /// client — its OS send buffer full — is reported not-writable and the
        /// worker simply skips it this pass. Poll never blocks.</summary>
        private bool SocketReady()
        {
            try
            {
                var s = _tcpClient.Client;
                return s is { Connected: true } && s.Poll(0, SelectMode.SelectWrite);
            }
            catch (Exception)
            {
                return false;   // socket closed / disposed -> not writable
            }
        }

        /// <summary>Remove the oldest non-guaranteed frame to relieve pressure.
        /// Returns false when there is no non-guaranteed frame to drop.</summary>
        private bool DropOldestNonGuaranteed()
        {
            for (var node = _queue.First; node is not null; node = node.Next)
            {
                if (!node.Value.Guaranteed)
                {
                    _queuedBytes -= node.Value.Frame.Length;
                    if (node.Value.CoalesceKey != 0) _coalesceNodes.Remove(node.Value.CoalesceKey);
                    _queue.Remove(node);
                    return true;
                }
            }
            return false;
        }

        private void WriterLoop()
        {
            try
            {
                while (true)
                {
                    byte[] frame;
                    lock (_sync)
                    {
                        while (_queue.First is null && !_disconnected && !_cts.IsCancellationRequested)
                            Monitor.Wait(_sync);
                        if (_disconnected || _cts.IsCancellationRequested) break;
                        var node = _queue.First!;
                        _queue.RemoveFirst();
                        if (node.Value.CoalesceKey != 0) _coalesceNodes.Remove(node.Value.CoalesceKey);
                        frame = node.Value.Frame;
                        _queuedBytes -= frame.Length;
                        if (_queue.First is null) { _undrainedSinceUtc = null; _overflowSinceUtc = null; }
                        else if (_queuedBytes <= CapacityBytes) _overflowSinceUtc = null;
                    }
                    // The only blocking socket write, on this dedicated thread.
                    // SendTimeout on the socket bounds it even against a
                    // black-holed path.
                    _stream.Write(frame, 0, frame.Length);
                }
            }
            catch (Exception)
            {
                // Socket error / SendTimeout / disposal -> this peer is dead.
                if (!_disconnected)
                {
                    _disconnected = true;
                    _reason = DisconnectReason.SendError;
                    _cts.Cancel();
                    _onDead(DisconnectReason.SendError);
                }
            }
        }

        /// <summary>Cancel the writer and close the underlying stream + socket
        /// (which wakes a blocked Write/Read on the peer). Idempotent.</summary>
        public void Close()
        {
            _cts.Cancel();
            lock (_sync) { _disconnected = true; Monitor.PulseAll(_sync); }
            CloseQuiet(_stream);
            try { _tcpClient.Dispose(); } catch { }
        }

        /// <summary>One element of the peer's send queue: a fully-framed byte[]
        /// plus its delivery contract. <see cref="CoalesceKey"/> is 0 for
        /// ordinary ordered traffic; non-zero marks a latest-wins stream frame
        /// (only the newest pending entry per key is ever retained, P606).</summary>
        private readonly struct QueuedFrame
        {
            public readonly byte[] Frame;
            public readonly bool Guaranteed;
            public readonly int CoalesceKey;
            public QueuedFrame(byte[] frame, bool guaranteed, int coalesceKey)
            {
                Frame = frame;
                Guaranteed = guaranteed;
                CoalesceKey = coalesceKey;
            }
        }
    }

    /// <summary>
    /// P605 — the fixed pool of send workers that replaces per-peer
    /// <c>peer-send</c> writer threads in pool mode. N workers each own a
    /// hash-partitioned subset of the peers (stable hash of the connection id),
    /// so every peer has exactly one consumer → per-peer send order is
    /// preserved. Each worker drains its peers round-robin (visits every
    /// assigned peer every pass, so a busy peer cannot starve its neighbours),
    /// skipping any peer whose socket is not writable — a slow/stalled client
    /// therefore never blocks the worker or the other peers on it. Enqueues
    /// wake the owning worker, so an idle pool sleeps instead of polling.
    /// </summary>
    private sealed class SendWorkerPool
    {
        private readonly Worker[] _workers;

        public SendWorkerPool(int workerCount)
        {
            if (workerCount < 1) workerCount = 1;
            _workers = new Worker[workerCount];
            for (int i = 0; i < workerCount; i++)
            {
                _workers[i] = new Worker();
                _workers[i].Start();
            }
        }

        /// <summary>Number of pool worker threads (test seam).</summary>
        public int WorkerCount => _workers.Length;

        /// <summary>Stable partition: a peer is always drained by the SAME
        /// worker, so its queue never has two consumers (that would break
        /// per-peer order).</summary>
        private Worker WorkerFor(ConnectionId id)
            => _workers[unchecked((int)((uint)id.Value % (uint)_workers.Length))];

        public void Assign(ConnectionId id, PeerSender peer)
        {
            var w = WorkerFor(id);
            peer.AttachWorker(w.Wake);
            w.Assign(peer);
        }

        public void Unassign(PeerSender peer)
        {
            // The peer keeps its own reference to its owner; find it there.
            peer.OwnerWorker?.Unassign(peer);
            peer.DetachWorker();
        }

        public void Shutdown()
        {
            foreach (var w in _workers) w.Stop();
        }

        // internal so sibling-nested PeerSender can hold an OwnerWorker ref to
        // it (a private nested class would be invisible outside SendWorkerPool).
        internal sealed class Worker
        {
            private readonly LinkedList<PeerSender> _peers = new();
            private readonly object _sync = new();
            private Thread? _thread;
            private volatile bool _running = true;

            public void Assign(PeerSender peer)
            {
                peer.OwnerWorker = this;
                lock (_sync) { _peers.AddLast(peer); Monitor.PulseAll(_sync); }
            }

            public void Unassign(PeerSender peer)
            {
                lock (_sync) { _peers.Remove(peer); }
            }

            public void Wake()
            {
                lock (_sync) { Monitor.PulseAll(_sync); }
            }

            public void Start()
            {
                _thread = new Thread(Loop) { IsBackground = true, Name = "peer-send-pool" };
                _thread.Start();
            }

            public void Stop()
            {
                _running = false;
                lock (_sync) { Monitor.PulseAll(_sync); }
                _thread?.Join(500);
            }

            /// <summary>Fair drain: visit every assigned peer each pass. Any
            /// progress (a frame written) keeps the pass pumping immediately;
            /// only when NO peer on this worker could make progress does the
            /// worker sleep — briefly if peers are merely blocked (not
            /// writable), or until a Wake when truly idle. So one stalled peer
            /// among many writable ones costs the others nothing.</summary>
            private void Loop()
            {
                while (_running)
                {
                    PeerSender[] snapshot;
                    lock (_sync)
                    {
                        if (_peers.Count == 0) { Monitor.Wait(_sync, 200); continue; }
                        snapshot = _peers.ToArray();
                    }

                    bool progressed = false;
                    bool anyBlocked = false;
                    foreach (var p in snapshot)
                    {
                        if (!_running) return;
                        var r = p.TryWriteHead();
                        if (r == PeerSender.DrainResult.Wrote) progressed = true;
                        else if (r == PeerSender.DrainResult.Blocked) anyBlocked = true;
                        // Idle: nothing queued. Dead: peer dropped itself.
                    }

                    if (progressed) continue;                       // keep pumping
                    if (anyBlocked) { Thread.Sleep(2); continue; }  // all blocked: re-poll shortly
                    lock (_sync) { Monitor.Wait(_sync, 50); }       // idle: sleep until a Wake
                }
            }
        }
    }
}
