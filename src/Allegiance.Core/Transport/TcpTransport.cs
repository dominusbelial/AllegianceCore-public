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
    QueueOverCapacity,
    /// <summary>The peer's socket made NO write progress for the whole
    /// <see cref="TcpTransport.NoWriteProgressTimeout"/> window while its send
    /// queue held data (no bytes accepted by the OS) — the peer is dead/link
    /// dead, however small its (coalesced) backlog is.</summary>
    NoWriteProgress,
    /// <summary>A blocking socket write hit the socket's SendTimeout (a single
    /// write stalled for the full per-write budget).</summary>
    SendTimeout,
    /// <summary>P645 — the peer's queued bytes exceeded the ABSOLUTE hard cap
    /// (<see cref="TcpTransport.HardCapBytes"/>), humans included. Where
    /// <see cref="QueueOverCapacity"/> is the timed soft rule (and exempt for
    /// humans), this is an unconditional memory ceiling applied to EVERY peer —
    /// a guaranteed-traffic flood (e.g. PING replies) can never grow a peer's
    /// queue without limit, human or not.</summary>
    QueueHardCap,
    /// <summary>A blocking socket write failed outright (connection reset /
    /// broken pipe / disposal — not a timeout).</summary>
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

    // P654 (H-02/H-21) — the SANITY cap for the CLIENT half's inbound frames
    // (server -> client): a connected client may legitimately receive a large
    // world/export burst, so it keeps the historical ~16 MiB ceiling.
    private const int MaximumFrame = 1 << 24; // sanity cap (~16 MiB)

    // P654 (H-02) — the SERVER half's inbound DATA-phase frame cap. Client ->
    // server traffic is tiny: the largest legitimate client message is about a
    // 1.1 KB LOGONREQ, so 64 KiB is generous. A pre-auth peer can no longer
    // claim a 16 MiB body up front (the old code allocated `new byte[lenRaw]`
    // before a single body byte arrived -> 512 MiB held with 32 slowloris
    // connections). Bounding each in-progress frame to 64 KiB also bounds the
    // per-frame CRC work to a negligible amount on the sim thread (H-21).
    // Internal/settable so the server binds it from config and tests can
    // shrink it.
    public static uint ServerInboundMaxFrame { get; set; } = 64u * 1024;

    // P654 (H-02) — a read DEADLINE for a frame whose body is in progress (a
    // peer that has sent a header then dribbles body bytes, or stalls) is
    // dropped after this long rather than parking a buffer + thread forever.
    // Applied ONLY once a frame's header has been read (completing its body),
    // never to an idle-but-healthy data session — an idle peer between frames
    // is unaffected. Internal/settable for the suite.
    public static TimeSpan DataFrameReadTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// P671 — the IDLE timeout for a host-side peer's inbound data session: a
    /// peer that sends NO bytes at all for this long (silent link) is dropped
    /// as link-dead. This is the counterpart to <see cref="DataFrameReadTimeout"/>:
    /// that bounds a frame once it has STARTED (header byte seen), while this
    /// bounds the silence BETWEEN frames. Aligned with the native client's
    /// link-dead tolerance <c>g_sDeadTime = 30</c> (clintlib.cpp:2047). A healthy
    /// client never trips it, because the shared client session keep-alive sends
    /// a <c>CS_PING</c> every 6 s (<c>clintlib.cpp:2050-2087 CheckServerLag</c>),
    /// so a well-behaved peer always has traffic well inside this window.
    /// Internal/settable so the server binds it from config and the suite can
    /// shrink it for a fast, deterministic test.
    /// </summary>
    public static TimeSpan DataLinkIdleTimeout { get; set; } = TimeSpan.FromSeconds(30);

    // P654 (H-15) — resource caps on the accept path. Without them a hostile
    // or runaway peer can open unbounded connections (one thread per
    // connection) and consume the process. All internal/settable so the server
    // binds them from config and the suite can shrink them for fast tests.
    //    MaxConnections       — hard ceiling on live connections (greet +
    //                           established). 1024 accommodates the 200v200
    //                           rig comfortably.
    //    MaxConnectionsPerIp   — ceiling on connections from one source address
    //                           (default 64: a sane public-server default; the
    //                           200v200 rig's ~390 bots all come from one MAC
    //                           host, so the rig's recommended config raises
    //                           this — see README).
    //    MaxPendingHandshake   — ceiling on connections still in the greeting
    //                           phase (the pre-OnPeerJoined window).
    public static int MaxConnections { get; set; } = 1024;
    public static int MaxConnectionsPerIp { get; set; } = 64;
    public static int MaxPendingHandshake { get; set; } = 256;

    // P312: the greeting body is just a player name (a few dozen bytes), so a
    // staller must never be able to claim a 16 MiB greeting and force that
    // allocation before the length is even plausibly backed by real data. This
    // cap is enforced BEFORE any body allocation in ReadFrame (see HandlePeer).
    private const uint MaximumGreeting = 4096;

    // P311: a real send timeout on the socket (layer 2 of defense — even the
    // writer thread's blocking Write gives up rather than hanging forever) and
    // a modest send buffer so a stalled peer's backlog surfaces quickly. The
    // OS-default SendTimeout of 0 (infinite) is precisely the bug this fixes.
    // Internal/settable (P645) so the suite can shrink it for a fast,
    // deterministic send-timeout test on the dedicated (blocking) writer.
    internal static int SendTimeoutMs { get; set; } = 10_000;
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

    /// <summary>P611 — the real liveness timeout that replaces the old
    /// "never-drained" anti-zombie rule: a peer whose send queue holds data but
    /// whose socket has accepted NO bytes for this long is dead and is
    /// disconnected (<see cref="DisconnectReason.NoWriteProgress"/>). Matches
    /// the native client's link-dead tolerance of 30 s (clintlib.cpp:2047,
    /// <c>g_sDeadTime = 30</c>). Internal so tests can tighten it for a fast,
    /// deterministic liveness test.</summary>
    internal static TimeSpan NoWriteProgressTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>P619c — the longer liveness grace granted to a connected HUMAN
    /// client (marked via <see cref="SetPeerHuman"/> from the server when a
    /// non-harness character name logs on). A human workstation that hiccups
    /// (OBS recording, heavy compile, 390 bots sharing its cores) must not be
    /// mass-disconnected the way the 2026-10-12 P611 rule cut Domingo's client.
    /// Dead sockets are still dropped after this grace; overflow-disconnect is
    /// disabled for humans (shedding/coalescing already bounds the queue). MCP
    /// bots keep the strict <see cref="NoWriteProgressTimeout"/>. Configurable
    /// via <c>transport.humanGraceSeconds</c> (default 60). Public so the server
    /// binds it from config at boot.</summary>
    public static TimeSpan HumanGraceTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>P311 test seam: the per-peer queued-byte bound (8 MiB in
    /// production). Internal so tests can shrink it to force an overflow
    /// deterministically on loopback, where OS socket buffers otherwise swallow
    /// a burst.</summary>
    internal static long CapacityBytes { get; set; } = 8L * 1024 * 1024;

    /// <summary>P645 — the ABSOLUTE per-peer queued-byte ceiling, humans
    /// included. Where <see cref="CapacityBytes"/> is the soft cap (it triggers
    /// stream-frame shedding and the timed <see cref="DisconnectReason.QueueOverCapacity"/>
    /// rule, which is exempt for humans), this is a hard memory bound: a peer
    /// whose queued bytes exceed it is disconnected outright
    /// (<see cref="DisconnectReason.QueueHardCap"/>) with no exemption, so a
    /// guaranteed-traffic flood (e.g. PING reply amplification) can never grow a
    /// peer's queue without limit. A few × <see cref="CapacityBytes"/> so a
    /// legitimate join/world/export burst (measured at 200v200, P645) never
    /// trips it. Internal so tests can shrink it for a fast, deterministic test.</summary>
    internal static long HardCapBytes { get; set; } = 4L * 1024 * 1024 * 8; // 32 MiB = 4 × CapacityBytes

    /// <summary>P312 test seam: how long an accepted peer may connect and send
    /// nothing (or a partial frame) before its greeting read gives up and the
    /// connection is dropped. Internal so the test suite can shorten it for a
    /// fast, deterministic greeting-timeout test.</summary>
    internal static TimeSpan GreetTimeout { get; set; } = TimeSpan.FromSeconds(ReceiveTimeoutMs / 1000.0);

    private readonly ITransportSite _site;
    private readonly object _gate = new();
    private readonly Dictionary<ConnectionId, PeerSender> _peers = new();
    private readonly List<Thread> _threads = new();
    // P654 (H-15) — accept-path resource accounting, guarded by _gate:
    //   _activeByIp        — every LIVE connection (greet or established) by
    //                        source address, for the per-IP cap.
    //   _pendingGreets     — connections still between accept and the end of
    //                        their greeting (pre-OnPeerJoined), for the
    //                        pending-handshake cap and the total-cap tally.
    //   _peerIpById        — each ESTABLISHED peer's source address, removed
    //                        exactly once when the peer is reaped (so the
    //                        per-IP count stays accurate across the data
    //                        session).
    private readonly Dictionary<IPAddress, int> _activeByIp = new();
    private readonly Dictionary<ConnectionId, IPAddress> _peerIpById = new();
    private int _pendingGreets;
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

    /// <summary>
    /// P619c — mark a connection as a connected HUMAN client (the server calls
    /// this when a non-harness character name — anything not under the MCP
    /// prefix — logs on). Human clients get the longer
    /// <see cref="HumanGraceTimeout"/> liveness grace and are not
    /// overflow-disconnected, so a real player whose machine hiccups is not cut
    /// the way P611 cut Domingo's client on 2026-10-12. Connections never marked
    /// (MCP bots, and every raw/synthetic peer) keep the strict
    /// <see cref="NoWriteProgressTimeout"/> + overflow limits. Idempotent;
    /// unknown/already-gone ids are ignored.
    /// </summary>
    public void SetPeerHuman(ConnectionId id, bool human)
    {
        lock (_gate)
        {
            if (_peers.TryGetValue(id, out var ps)) ps.IsHuman = human;
        }
    }

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
                // P654 (H-15) — resource caps before we commit a thread to this
                // connection: total connections, per-source-IP, and pending
                // (greet-phase) handshakes. A refused connection is closed
                // immediately (never gets a greet thread) and logged once. The
                // per-IP cap is why a single hostile host (or a runaway bot
                // host) can no longer exhaust the process; the 200v200 rig's
                // ~390 bots all come from one MAC host, so its recommended
                // config raises MaxConnectionsPerIp (see README).
                var remote = RemoteIpOf(client);
                bool accepted;
                lock (_gate)
                {
                    int live = _peers.Count + _pendingGreets;
                    int perIp = _activeByIp.TryGetValue(remote, out var c) ? c : 0;
                    if (live >= MaxConnections)
                    {
                        accepted = false;
                        LogCapRefusal(client, $"total connection cap {MaxConnections} reached");
                    }
                    else if (perIp >= MaxConnectionsPerIp)
                    {
                        accepted = false;
                        LogCapRefusal(client, $"per-IP cap {MaxConnectionsPerIp} reached for {remote}");
                    }
                    else if (_pendingGreets >= MaxPendingHandshake)
                    {
                        accepted = false;
                        LogCapRefusal(client, $"pending-handshake cap {MaxPendingHandshake} reached");
                    }
                    else
                    {
                        accepted = true;
                        _pendingGreets++;
                        _activeByIp[remote] = perIp + 1;
                    }
                }
                if (!accepted)
                {
                    try { client.Dispose(); } catch { /* best effort */ }
                    continue;
                }
                // P312: the greeting read now runs on ITS OWN thread per
                // connection (HandlePeer), so AcceptTcpClient() returns
                // immediately. Previously HandlePeer ran inline here and its
                // synchronous greeting read could park this accept thread
                // forever — one connection that connects and sends nothing
                // silently blocked every new join (review finding C2). With the
                // read off the accept loop, a stalled greet blocks only that
                // connection's own thread and the loop keeps accepting.
                var greet = new Thread(() => HandlePeer(client, remote))
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

    private void HandlePeer(TcpClient client, IPAddress remote)
    {
        NetworkStream? stream = null;
        ConnectionId id = default;
        bool established = false;
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
            lock (_gate)
            {
                _peers[id] = sender;
                _peerIpById[id] = remote;   // P654 (H-15) — attribute the IP for the per-IP cap
                established = true;          // this connection survived the greeting
            }
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
            else if (IsSocketTimeout(ex)) gr = DisconnectReason.GreetTimeout;
            else gr = DisconnectReason.ProtocolError;
            if (stream != null) CloseQuiet(stream);
            client.Dispose();
            RecordDisconnectRaw(gr, $"greeting failed ({gr})");
            return;
        }
        finally
        {
            // P654 (H-15) — the greeting phase is over either way: release the
            // pending-handshake slot. A connection that FAILED the greeting is
            // also released from the per-IP tally (it never became a peer); an
            // established peer keeps its per-IP entry until reaped
            // (RaisePeerLeftIfFirst).
            lock (_gate)
            {
                if (_pendingGreets > 0) _pendingGreets--;
                if (!established && _activeByIp.TryGetValue(remote, out var cnt))
                {
                    if (cnt > 1) _activeByIp[remote] = cnt - 1;
                    else _activeByIp.Remove(remote);
                }
            }
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
        // Attribute the reason BEFORE tearing down the socket: Close() wakes the
        // peer's read loop with a socket error, and the read loop would otherwise
        // race to reap this peer with ReadErrorOrEof — hiding the real send-side
        // cause (NoWriteProgress / QueueOverCapacity / SendTimeout). Recording
        // first guarantees the exactly-once attribution is the initiating path's
        // reason.
        RaisePeerLeftIfFirst(id, reason, logLine: true);
        if (ps != null)
        {
            if (_mode == TransportMode.Pool) _pool?.Unassign(ps);
            ps.Close();
        }
    }

    /// <summary>P654 (H-14) — proactively disconnect one host-side peer by id
    /// (e.g. the server after a graceful LOGOFF, which must not leave the socket
    /// open to re-arm LOGONREQ and churn ship ids). Raises
    /// <see cref="ITransportSite.OnPeerLeft"/> exactly once with the given
    /// reason, closes the peer's sender (unblocking its read loop). No-op for a
    /// peer that is already gone / for a non-host transport.</summary>
    public void ClosePeer(ConnectionId id, DisconnectReason reason = DisconnectReason.Shutdown)
    {
        if (!_isHost) return;
        DropPeer(id, reason);
    }

    /// <summary>P654 (H-15) test seam: the number of live connections (greet +
    /// established) currently counted by the accept-path caps.</summary>
    internal int LiveConnectionCount()
    {
        lock (_gate) return _peers.Count + _pendingGreets;
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
            // P654 (H-15) — release the established peer's per-IP accounting so
            // the per-IP tally stays accurate across the data session (this
            // peer's slot was held since accept, or since it became a peer).
            if (_peerIpById.TryGetValue(id, out var ip))
            {
                _peerIpById.Remove(id);
                if (_activeByIp.TryGetValue(ip, out var cnt))
                {
                    if (cnt > 1) _activeByIp[ip] = cnt - 1;
                    else _activeByIp.Remove(ip);
                }
            }
            if (logLine)
                Console.WriteLine($"[conn-drop] peer {id} left reason={reason}");
            _site.OnPeerLeft(id);
        }
    }

    /// <summary>P654 (H-15) — the source IP of an accepted socket, for the
    /// per-IP connection cap. Falls back to IPv4Loopback if the remote endpoint
    /// is unavailable (it never is on an accepted socket).</summary>
    private static IPAddress RemoteIpOf(TcpClient client)
    {
        try
        {
            if (client.Client.RemoteEndPoint is IPEndPoint ep && ep.Address is { } a)
                return a;
        }
        catch { /* socket torn down mid-check */ }
        return IPAddress.Loopback;
    }

    /// <summary>P654 (H-15) — one (rate-friendly) log line for a refused
    /// connection, so an operator sees a cap was enforced without every
    /// refused attempt spamming the log.</summary>
    private static void LogCapRefusal(TcpClient client, string why)
    {
        Console.WriteLine($"[conn] refused connection from {RemoteIpOf(client)}: {why}");
    }

    /// <summary>P606 — record a disconnect that never earned a connection id
    /// (a failed greeting), so it still shows in the by-reason summary.</summary>
    private void RecordDisconnectRaw(DisconnectReason reason, string detail)
    {
        _disconnectCounts.AddOrUpdate(reason, 1, (_, c) => c + 1);
        Console.WriteLine($"[conn-drop] (pre-join) {detail} reason={reason}");
    }

    /// <summary>Does <paramref name="ex"/> (or its inner exception) indicate a
    /// socket timeout (either a receive timeout or a send/SendTimeout)? Both
    /// surface as <see cref="SocketError.TimedOut"/>.</summary>
    private static bool IsSocketTimeout(Exception ex)
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

    /// <summary>
    /// P634 — aggregate per-peer send-backlog sample for the live 200v200
    /// measurement (scope 1: "per-peer queue depth and the age of the oldest
    /// guaranteed frame"). Over all live peers reports the max ordered-lane and
    /// stream-lane pending frame counts, the max and total queued bytes, and the
    /// max age (ms) of the oldest pending guaranteed frame — the join/world
    /// head-of-line delay this task attacks. Cheap (one lock per peer), called
    /// once per profiler window.
    /// </summary>
    public (int Peers, int MaxOrderedPending, int MaxStreamPending,
            long MaxQueuedBytes, long TotalQueuedBytes, long MaxOldestGuaranteedMs)
        SamplePeerQueues()
    {
        PeerSender[] senders;
        lock (_gate) senders = _peers.Values.ToArray();
        if (senders.Length == 0) return (0, 0, 0, 0, 0, 0);
        int maxOrd = 0, maxStr = 0;
        long maxBytes = 0, total = 0, maxAge = 0;
        foreach (var s in senders)
        {
            var (o, p, b) = s.SnapshotQueue();
            if (o > maxOrd) maxOrd = o;
            if (p > maxStr) maxStr = p;
            if (b > maxBytes) maxBytes = b;
            total += b;
            long age = s.OldestGuaranteedAgeMs();
            if (age > maxAge) maxAge = age;
        }
        return (senders.Length, maxOrd, maxStr, maxBytes, total, maxAge);
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
                // P654 (H-02/H-21): the HOST's inbound data frames are capped at
                // ServerInboundMaxFrame (a pre-auth peer can no longer claim a
                // huge body up front), and the body has a read deadline so a
                // header-then-stall connection is dropped instead of parking a
                // buffer + thread. The CLIENT half keeps the larger MaximumFrame
                // (server -> client world/export bursts) and no deadline (it is
                // the trusted server speaking).
                //
                // P671 — split the HOST's read bounds into two distinct
                // timeouts, because the old single deadline (computed here,
                // BEFORE ReadFrame) measured IDLE time, not a stalled in-progress
                // frame — so a client that simply sent nothing for 30 s (a docked
                // or staged bot that never sends CS_PING) was wrongly dropped as
                // ReadErrorOrEof. Now:
                //   * DataLinkIdleTimeout  bounds the SILENCE between frames (no
                //     byte at all) — the link-dead case; a healthy client never
                //     hits it because it pings every 6 s;
                //   * DataFrameReadTimeout  bounds a frame once it has STARTED
                //     (first header byte seen), preserving P654's protection
                //     against header-then-stall attackers.
                uint cap = _isHost ? ServerInboundMaxFrame : MaximumFrame;
                long idleDeadline = _isHost
                    ? Environment.TickCount64 + (long)DataLinkIdleTimeout.TotalMilliseconds : 0;
                long frameTimeoutMs = _isHost ? (long)DataFrameReadTimeout.TotalMilliseconds : 0;
                (byte type, byte[] body) = ReadFrame(s, cap, idleDeadline, frameTimeoutMs);
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

    /// <summary>P654 (H-02) + P671 — assemble one complete data frame. The body
    /// buffer is grown as bytes actually arrive (starting small, doubling up to
    /// the declared length capped at <paramref name="maxBodyLen"/>), instead of
    /// the old code's `new byte[lenRaw]` up front — so a hostile peer that claims
    /// a large body but never sends it never forces the allocation. The read is
    /// bounded by TWO separate deadlines:
    ///   * <paramref name="idleDeadlineMs"/> (may be 0 = none) bounds waiting for
    ///     the frame's FIRST byte — the SILENT/idle link case. It is anchored at
    ///     entry, before the header is read.
    ///   * <paramref name="frameTimeoutMs"/> (may be 0 = none) bounds the rest of
    ///     the frame once it has STARTED (first header byte seen) — the
    ///     header-then-stall slowloris case, preserving P654 (H-02). It is
    ///     anchored AFTER the first byte arrives, so it bounds a STALLED
    ///     in-progress frame, never an idle-but-healthy data session.</summary>
    private static (byte Type, byte[] Body) ReadFrame(NetworkStream s, uint maxBodyLen = MaximumFrame,
                                                      long idleDeadlineMs = 0, long frameTimeoutMs = 0)
    {
        Span<byte> hdr = stackalloc byte[HeaderWidth];
        // P671 — the IDLE deadline bounds only the frame's first byte: a link
        // that sends nothing at all is link-dead. Once a byte has arrived the
        // frame is in progress and the (separate) FRAME deadline takes over.
        int first = ReadSome(s, hdr.Slice(0, 1), idleDeadlineMs);
        if (first <= 0) throw new EndOfStreamException("peer closed before frame");
        // P671 — anchor the frame deadline NOW that the frame has started. A
        // header that stalls, or a body that dribbles, must complete within
        // frameTimeoutMs of the header's first byte.
        long frameDeadline = frameTimeoutMs > 0
            ? Environment.TickCount64 + frameTimeoutMs : 0;
        ReadExactly(s, hdr.Slice(1), frameDeadline);
        byte type = hdr[0];
        uint lenRaw = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(1));
        if (lenRaw > maxBodyLen)
            throw new IOException($"frame length {lenRaw} exceeds safety cap {maxBodyLen}");
        if (lenRaw == 0) return (type, Array.Empty<byte>());
        // Grow the body buffer as bytes arrive: start at 4 KiB and double up to
        // the (capped) declared length, so the allocation tracks real arrival.
        var body = new byte[(int)Math.Min(lenRaw, 4096)];
        int filled = 0;
        while (filled < (int)lenRaw)
        {
            if (filled == body.Length)
            {
                int grow = (int)Math.Min(lenRaw, (long)body.Length * 2);
                Array.Resize(ref body, grow);
            }
            int n = ReadSome(s, body.AsSpan(filled, body.Length - filled), frameDeadline);
            if (n <= 0) throw new EndOfStreamException("peer closed mid-frame");
            filled += n;
        }
        return (type, body);
    }

    /// <summary>P646 — how long a reader parks in <c>Socket.Poll</c> waiting for
    /// readability on a NON-BLOCKING pool socket before re-checking (250 ms).
    /// Long enough that an idle peer does not spin, short enough that shutdown/
    /// closure is noticed promptly. Idle-wakes ≈ 4/s per peer, not ~1000/s of
    /// thrown exceptions (P645's old would-block retry).</summary>
    private const int ReadPollTimeoutMicros = 250_000;
    private static long _wouldBlockCatches;
    private static long _wouldBlockWaits;
    /// <summary>P646 — cumulative would-block IOException catches in the read
    /// path. Should stay ≈ 0 for idle non-blocking pool peers now that the
    /// reader parks in <see cref="System.Net.Sockets.Socket.Poll"/> instead of
    /// faulting ~1 ms apart.</summary>
    public static long WouldBlockCatches => Interlocked.Read(ref _wouldBlockCatches);
    /// <summary>P646 — cumulative times the reader waited in Poll with no data
    /// ready (an idle-wake); shows the efficient-block path is engaged.</summary>
    public static long WouldBlockWaits => Interlocked.Read(ref _wouldBlockWaits);

    private static void ReadExactly(Stream s, Span<byte> dst, long deadlineMs = 0)
    {
        int off = 0;
        while (off < dst.Length)
        {
            int n = ReadSome(s, dst.Slice(off, dst.Length - off), deadlineMs);
            if (n <= 0) throw new EndOfStreamException("peer closed mid-frame");
            off += n;
        }
    }

    /// <summary>P654 (H-02) + P671 — one read of up to <paramref name="dst"/>
    /// bytes with P646's efficient-blocking (Poll-based) handling for
    /// non-blocking pool sockets, plus the data-session read bound. Returns the
    /// bytes read; throws on EOF / socket error. When <paramref name="deadlineMs"/>
    /// is non-zero and elapses, throws an <see cref="IOException"/> so the
    /// stalled/idle connection is dropped. The bound is honored on BOTH socket
    /// kinds:
    ///   * NON-BLOCKING (pool) — P646 parks in <c>Socket.Poll</c> (250 ms) and
    ///     re-checks the deadline each wake, so no CPU is burned and no
    ///     would-block exception is thrown per idle peer per ms;
    ///   * BLOCKING (thread-per-peer) — a plain blocking <c>Read</c> parks the
    ///     thread forever and cannot be interrupted by a wall-clock check, so the
    ///     socket is given a receive timeout equal to the remaining budget and a
    ///     timed-out read is translated into the bound-exceeded error. Only set
    ///     when a deadline is active: the greet handshake (deadline 0) keeps its
    ///     P312 socket <c>ReceiveTimeout</c> undisturbed, and the client half
    ///     (deadline 0) keeps the infinite read it relies on to detect EOF.
    ///     Sends stay non-blocking (P645's partial-write safety is untouched).</summary>
    private static int ReadSome(Stream s, Span<byte> dst, long deadlineMs)
    {
        while (true)
        {
            if (deadlineMs != 0 && Environment.TickCount64 > deadlineMs)
                throw new IOException("peer data-session read bound exceeded (idle or in-progress frame deadline)");
            // P646 — block efficiently on a NON-BLOCKING pool socket instead of
            // throwing+catching a would-block IOException every ~1 ms (P645's
            // Thread.Sleep(1) retry burned CPU and allocated an exception object
            // per idle peer per ms — up to ~390k exceptions/s at 390 idle peers).
            // Poll SelectRead (plus a zero-timeout SelectError probe on a
            // not-ready socket) with a sane timeout before Read, so an idle
            // non-blocking peer parks in Poll and no exception is thrown when no
            // data is pending. RESTRICTED to non-blocking sockets: a BLOCKING
            // socket (the greet handshake, which carries a ReceiveTimeout, and
            // thread-per-peer data reads) must keep using the plain blocking
            // Read, because Socket.Poll bypasses the socket's ReceiveTimeout and
            // would defeat the greeting timeout (a silent connection would never
            // be dropped).
            if (s is NetworkStream ns && ns.Socket is { Blocking: false } sock
                && !sock.Poll(ReadPollTimeoutMicros, SelectMode.SelectRead))
            {
                Interlocked.Increment(ref _wouldBlockWaits);
                if (sock.Poll(0, SelectMode.SelectError))
                    throw new IOException("socket error");
                continue;
            }
            // P671 — a BLOCKING (thread-per-peer) data socket honors the bound by
            // bounding the blocking Read itself. Only applied when a deadline is
            // active; greeting (deadline 0) keeps its P312 socket ReceiveTimeout
            // and the client half (deadline 0) keeps its infinite read.
            if (s is NetworkStream bn && bn.Socket is { Blocking: true } && deadlineMs != 0)
            {
                long remain = deadlineMs - Environment.TickCount64;
                bn.ReadTimeout = remain > 0 ? (int)remain : 1;
            }
            int n;
            try
            {
                n = s.Read(dst);
            }
            catch (Exception ex) when (IsWouldBlock(ex))
            {
                // P645 — a POOL-mode peer's socket is non-blocking (so the
                // shared worker's sends never block), which makes reads throw
                // would-block when no data is pending instead of blocking.
                // Retry rather than treating "no data right now" as a fatal
                // error. Harmless for blocking sockets (would-block never fires).
                Interlocked.Increment(ref _wouldBlockCatches);
                Thread.Sleep(1);
                continue;
            }
            catch (IOException ioex) when (IsSocketTimeout(ioex) && deadlineMs != 0)
            {
                // P671 — a BLOCKING socket's receive timeout fired. Re-check the
                // wall clock: if the bound truly passed, the top of the loop
                // throws the real bound-exceeded error; otherwise (spurious early
                // timeout) retry with a fresh remaining budget.
                if (Environment.TickCount64 > deadlineMs)
                    throw new IOException("peer data-session read bound exceeded (idle or in-progress frame deadline)");
                continue;
            }
            return n;
        }
    }

    /// <summary>P645 — does <paramref name="ex"/> (or its inner exception)
    /// indicate a socket "would block" (a non-blocking socket with nothing to
    /// send/receive right now)? .NET surfaces this as an <c>IOException</c>
    /// wrapping a <see cref="SocketException"/> whose
    /// <see cref="SocketException.SocketErrorCode"/> is WouldBlock (EAGAIN/
    /// EWOULDBLOCK) — distinct from a hard error or a timeout.</summary>
    private static bool IsWouldBlock(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is SocketException se
                && (se.SocketErrorCode == SocketError.WouldBlock || se.SocketErrorCode == SocketError.TryAgain))
                return true;
        return false;
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
        // P634 — TWO per-peer send lanes instead of one shared FIFO. Join and
        // world-sync data (MISSIONDEF, EXPORTs, CLUSTERINFO/graph, SET_CLUSTER,
        // chat, kills — all sent coalesceKey==0 via SendPeer) must never wait
        // behind the per-tick latest-wins position stream: at ~390 connections
        // a joiner's guaranteed world data starved behind the continuously-
        // produced stream frames in the single FIFO (P633 measured ~224/390
        // bots never receiving their cluster GRAPH/warp exports). So:
        //   _orderedQueue — coalesceKey==0 ordered/guaranteed traffic. Never
        //                   coalesced, never dropped, ALWAYS drained first.
        //   _streamQueue  — coalesceKey!=0 latest-wins stream frames (ship
        //                   states, own-ship, stations/asteroids/probes/...).
        //                   Coalesced per key, shed oldest non-guaranteed under
        //                   capacity, drained only when _orderedQueue is empty.
        // This is the transport realisation of "separate lanes" (xorgy's port:
        // reliable bursts must not be delayed behind state; P631's architecture
        // study builds on it), and of native's ordered-vs-streaming send split
        // (Messages.cpp:548-563 guaranteed: DPNSEND_GUARANTEED|COALESCE never
        // times out; best-effort: DPNSEND_NONSEQUENTIAL drops at 500 ms).
        private readonly LinkedList<QueuedFrame> _orderedQueue = new();
        private readonly LinkedList<QueuedFrame> _streamQueue = new();
        private readonly object _sync = new();
        // P606 — latest-wins coalescing over the STREAM lane only:
        // coalesceKey -> the stream-queue node holding that key's newest
        // pending frame. Lets Enqueue supersede an older pending stream frame
        // in place (O(1)) instead of queueing every stale snapshot; removed
        // when the writer pops the node. Ordered frames never enter it.
        private readonly Dictionary<int, LinkedListNode<QueuedFrame>> _coalesceNodes = new();
        private long _queuedBytes;
        private DateTime? _overflowSinceUtc;
        // P645 — partial-write-safe sending: the head frame is promoted OUT of
        // the queue into _inflight (with _inflightOffset bytes already accepted
        // by the OS) and stays there until FULLY sent. A frame is never
        // destroyed before it is wholly on the wire, so a wedged socket can
        // never corrupt TCP framing by resuming mid-frame on a fresh enqueue —
        // a partially-sent frame is either completed (bytes keep flowing) or the
        // connection is closed (unknown progress on a timed-out write), never
        // abandoned-and-continued on the same socket.
        private byte[]? _inflight;
        private int _inflightOffset;
        // P611 — the peer's liveness clock, in ticks: the last time the socket
        // accepted ANY bytes (a send completed partially or fully). Updated by
        // the writer/pool worker on every successful write. A peer whose socket
        // keeps accepting bytes is never "stuck", however full its queue — the
        // liveness rule disconnects ONLY when this clock goes stale (no bytes
        // accepted for NoWriteProgressTimeout) while the queue holds data. This
        // replaces P606's flawed "continuously non-empty queue" proxy, which
        // misclassified a healthy-but-overloaded peer (queue never momentarily
        // empty under sustained load) as dead and mass-disconnected it. Long +
        // Interlocked so the writer thread and the enqueue (sim) thread can
        // update/read it without tearing (DateTime is not volatile-able).
        private long _lastWriteProgressUtcTicks;
        private volatile bool _disconnected;
        private volatile DisconnectReason _reason;
        // P619c — true when this peer is a connected human client (set via
        // TcpTransport.SetPeerHuman by the server at logon). Grants the longer
        // HumanGraceTimeout and disables overflow-disconnect; see Enqueue.
        internal volatile bool IsHuman;
        // P605 — pool mode: the owning worker's Wake callback, pulsed when a
        // frame is enqueued so the pool never busy-waits. Null in
        // thread-per-peer mode, where the dedicated writer is woken by Pulse.
        private Action? _wake;
        // P605 — pool mode: the worker this peer is partitioned to (used to
        // unassign on leave). Null in thread-per-peer mode.
        internal SendWorkerPool.Worker? OwnerWorker;
        // P645 — true when this peer's socket is non-blocking (POOL mode; set in
        // AttachWorker). The pool worker then sends via Socket.Send (partial
        // bytes / would-block) so the shared worker never blocks and a slow
        // human is never cut by a write timeout. The dedicated writer / client
        // half keep a blocking socket and send via _stream.Write (close on
        // timeout with unknown progress).
        private bool _nonBlocking;
        // P645 test seam (InternalsVisibleTo): inject a deterministic fake send
        // sink — the "fake socket/stream" the suite uses to unit-test partial
        // writes, would-block, and timeout behaviour without OS socket timing.
        // When set, SendInflight consults it instead of the real socket/stream.
        internal Func<byte[], int, int, SendOutcome>? SendOverride = null; // set by tests via InternalsVisibleTo/reflection

        public PeerSender(NetworkStream stream, TcpClient tcpClient, Action<DisconnectReason> onDead)
        {
            _stream = stream;
            _tcpClient = tcpClient;
            _onDead = onDead;
            // Liveness baseline: a freshly-created peer has made no writes yet,
            // so its clock starts now. The first enqueue on an empty queue also
            // resets it (see Enqueue), so an idle-then-active peer is never
            // misjudged stale before its writer gets a chance to run.
            _lastWriteProgressUtcTicks = DateTime.UtcNow.Ticks;
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
                // Was the peer's send path fully drained before this enqueue?
                // If so the peer is current (idle or caught up); its writer
                // gets a fresh liveness window rather than being judged stale
                // from an old baseline (an idle-then-active peer must never be
                // dropped for sitting quiet before its next frame arrived).
                // P645 — a peer with a partially-sent INFLIGHT frame is NOT
                // drained, so it is not treated as freshly-idle.
                bool wasEmpty = _orderedQueue.First is null
                                && _streamQueue.First is null
                                && _inflight is null;

                if (coalesceKey != 0)
                {
                    // P634 — STREAM lane (coalesceKey!=0): latest-wins, shed-able.
                    // A slower peer keeps at most the newest pending frame per key.
                    if (_coalesceNodes.TryGetValue(coalesceKey, out var existing))
                    {
                        // Latest-wins: an older pending stream frame for this
                        // same key is superseded in place, so the collapsed
                        // frame keeps its stream-lane position and the byte
                        // accounting stays exact.
                        _queuedBytes -= existing.Value.Frame.Length;
                        existing.Value = new QueuedFrame(frame, guaranteed, coalesceKey, DateTime.UtcNow.Ticks);
                        _queuedBytes += frame.Length;
                    }
                    else
                    {
                        // Shed oldest best-effort STREAM frames while over
                        // capacity (guaranteed traffic is never dropped here).
                        // Streaming ship-state frames are sent non-guaranteed,
                        // so they are the natural shed candidates.
                        while (_queuedBytes + frame.Length > CapacityBytes && DropOldestNonGuaranteed()) { }
                        var node = _streamQueue.AddLast(new QueuedFrame(frame, guaranteed, coalesceKey, DateTime.UtcNow.Ticks));
                        _coalesceNodes[coalesceKey] = node;
                        _queuedBytes += frame.Length;
                    }
                }
                else
                {
                    // P634 — ORDERED lane (coalesceKey==0): join/world/exports/
                    // graph/chat/kills. Never coalesced and never dropped (the
                    // shedder only ever scans the stream lane), and drained
                    // before any stream frame — so world data never starves
                    // behind the position stream. Also shed stream traffic if
                    // over capacity so an ordered burst can still land.
                    while (_queuedBytes + frame.Length > CapacityBytes && DropOldestNonGuaranteed()) { }
                    _orderedQueue.AddLast(new QueuedFrame(frame, guaranteed, coalesceKey, DateTime.UtcNow.Ticks));
                    _queuedBytes += frame.Length;
                }
                Monitor.Pulse(_sync);

                // Overflow policy (P611): the peer is disconnected only when it
                // is genuinely STUCK — never merely because its streaming backlog
                // is large (coalescing keeps that small) and never because its
                // queue fails to momentarily drain under sustained load (that is
                // healthy backpressure, not death). Two signals, each gated on
                // its own timeout so a healthy (if slow) peer is never dropped
                // for a brief burst:
                //   (a) QueueOverCapacity — a guaranteed-only backlog sits over
                //       the byte cap for OverflowTimeout (nothing droppable or
                //       coalescable left to shed): the classic "cannot keep up",
                //       and the memory bound (P606);
                //   (b) NoWriteProgress — the socket has accepted NO bytes for
                //       NoWriteProgressTimeout while the queue holds data: the
                //       peer is dead/link-dead, however small its (coalesced)
                //       backlog is. This is the REAL anti-zombie signal,
                //       measured by actual write progress, not by queue
                //       emptiness. A peer that keeps accepting bytes is never
                //       "stuck", however full its queue — the 2026-10-11 mass
                //       disconnect misread that backpressure as death.
                var now = DateTime.UtcNow;
                if (wasEmpty)
                {
                    Interlocked.Exchange(ref _lastWriteProgressUtcTicks, now.Ticks);
                    _overflowSinceUtc = null;
                }
                else if (_orderedQueue.First is not null || _streamQueue.First is not null || _inflight is not null)
                {
                    _overflowSinceUtc = _queuedBytes > CapacityBytes ? (_overflowSinceUtc ?? now) : null;
                    long lastProg = Interlocked.Read(ref _lastWriteProgressUtcTicks);
                    // P619c — a connected HUMAN client gets a longer no-progress
                    // grace, and its overflow-disconnect is DISABLED: coalescing
                    // (P606 latest-wins) + non-guaranteed shedding already keep a
                    // human renderer's queue bounded, so a laggy-but-alive player
                    // is never cut merely for backpressure — only a genuinely dead
                    // socket (no bytes accepted for the longer human grace) is.
                    // MCP bots and every unmasked peer keep the strict P611 limits.
                    var noProgressTimeout = IsHuman ? HumanGraceTimeout : NoWriteProgressTimeout;
                    bool noProgress = (now - new DateTime(lastProg)) >= noProgressTimeout;
                    bool overCap = !IsHuman && _overflowSinceUtc is { } os && now - os >= OverflowTimeout;
                    if (noProgress)
                    {
                        _disconnected = true;
                        _reason = DisconnectReason.NoWriteProgress;
                        fireDisconnect = true;
                    }
                    else if (overCap)
                    {
                        _disconnected = true;
                        _reason = DisconnectReason.QueueOverCapacity;
                        fireDisconnect = true;
                    }
                }
                // P645 — the ABSOLUTE queued-byte cap, applied to EVERY peer
                // (humans included), independent of the timed soft rules above.
                // The human exemption above covers only the timed
                // QueueOverCapacity rule; this hard memory ceiling has no
                // exemption, so a guaranteed-only flood (e.g. PING reply
                // amplification) that nothing can shed must never be allowed to
                // grow a peer's queue past a bounded size.
                if (_queuedBytes > HardCapBytes)
                {
                    _disconnected = true;
                    _reason = DisconnectReason.QueueHardCap;
                    fireDisconnect = true;
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
        /// woken on every enqueue. No dedicated writer thread is spawned. P645 —
        /// pool peers also flip their socket NON-BLOCKING so the shared worker's
        /// sends report partial progress and "would block" instead of ever
        /// blocking the worker (the non-blocking state only affects sends; the
        /// read loop tolerates WouldBlock — see <see cref="ReadExactly"/>).
        /// The socket must already have had GetStream() called (it has: the
        /// greeting ran before this), else constructing a NetworkStream over a
        /// non-blocking socket throws.</summary>
        public void AttachWorker(Action wake)
        {
            _wake = wake;
            _nonBlocking = true;
            try { _tcpClient.Client.Blocking = false; } catch { /* already gone */ }
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
            lock (_sync)
            {
                if (_disconnected) return DrainResult.Dead;
                if (!PromoteHeadLocked()) return DrainResult.Idle;   // nothing queued/in-flight
                // Skip — not writable now (keeps in-flight). When a deterministic
                // SendOverride fake sink is installed (P645 test seam), the test
                // controls writability through the sink, so don't gate on the
                // real socket.
                if (SendOverride is null && !SocketReady()) return DrainResult.Blocked;
            }
            // Outside the lock: one non-blocking socket send of the in-flight
            // frame's remaining bytes. Reports partial progress / would-block,
            // so a slow peer never ties up the shared worker. Returns:
            //   Wrote   — some or all of the in-flight frame was accepted by the
            //              OS (fully sent => in-flight cleared for the next frame);
            //   Blocked — the OS send buffer is full; the in-flight frame is
            //              PRESERVED and retried on a later pass;
            //   Dead    — socket error / disposal / timed-out-with-unknown-
            //              progress: the connection is closed (see SendInflight).
            return SendInflight();
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

        /// <summary>Remove the oldest non-guaranteed STREAM frame to relieve
        /// pressure. Ordered-lane frames (coalesceKey 0) are never dropped
        /// (they carry join/world/exports that must land), so the shedder only
        /// ever scans the stream lane. Returns false when there is no
        /// non-guaranteed frame to drop.</summary>
        private bool DropOldestNonGuaranteed()
        {
            for (var node = _streamQueue.First; node is not null; node = node.Next)
            {
                if (!node.Value.Guaranteed)
                {
                    _queuedBytes -= node.Value.Frame.Length;
                    if (node.Value.CoalesceKey != 0) _coalesceNodes.Remove(node.Value.CoalesceKey);
                    _streamQueue.Remove(node);
                    return true;
                }
            }
            return false;
        }

        /// <summary>P645 — promote the next frame to drain into the in-flight
        /// buffer (if none is already in flight): the ORDERED lane head first,
        /// then the STREAM lane head — so ordered/world data always precedes
        /// latest-wins position frames on the wire and can never be starved
        /// behind them. Caller must hold <c>_sync</c>. Releases the coalesce-map
        /// entry for a promoted stream frame and maintains the byte + overflow
        /// accounting. A promoted frame is removed from the queue and its bytes
        /// deducted from the accounting up front, but it is NOT discarded — it
        /// lives in <see cref="_inflight"/> until fully sent (see
        /// <see cref="SendInflight"/>). Returns true when there is now an
        /// in-flight frame (just promoted, or already there); false when both
        /// lanes are empty.</summary>
        private bool PromoteHeadLocked()
        {
            if (_inflight != null) return true;
            LinkedListNode<QueuedFrame> node;
            if (_orderedQueue.First is not null) node = _orderedQueue.First!;
            else if (_streamQueue.First is not null) node = _streamQueue.First!;
            else return false;
            node.List!.RemoveFirst();
            // P606 — a coalescable stream frame leaving the queue releases its
            // key so the NEXT enqueue for that key appends fresh instead of
            // pointing at this (now-consumed) node.
            if (node.Value.CoalesceKey != 0) _coalesceNodes.Remove(node.Value.CoalesceKey);
            _inflight = node.Value.Frame;
            _inflightOffset = 0;
            _queuedBytes -= _inflight.Length;
            if (_orderedQueue.First is null && _streamQueue.First is null) _overflowSinceUtc = null;
            else if (_queuedBytes <= CapacityBytes) _overflowSinceUtc = null;
            return true;
        }

        /// <summary>P645 — one send attempt of the in-flight frame's remaining
        /// bytes, advancing <see cref="_inflightOffset"/> by however many the
        /// socket/stream accepted. The in-flight frame is PRESERVED until fully
        /// sent, so a partial write is never abandoned (abandoning one would
        /// corrupt TCP framing when the next frame's bytes complete its declared
        /// body). Returns:
        ///  <c>Blocked</c> — the transport can't take more right now: nothing
        ///      sent, the in-flight frame is kept and retried on a later pass;
        ///  <c>Wrote</c> — some or all bytes went out (in-flight cleared when
        ///      the whole frame is now on the wire);
        ///  <c>Dead</c> — a hard error, or a write that TIMED OUT with unknowable
        ///      progress: in that case the connection is CLOSED (reason
        ///      <see cref="DisconnectReason.SendTimeout"/>) rather than continued
        ///      on a possibly-corrupt stream.
        /// Transmit selection (P645): the POOL worker sends via a NON-BLOCKING
        /// <c>Socket.Send</c> (reports partial bytes / would-block), so a shared
        /// worker is never blocked by a wedged peer and a slow-but-alive HUMAN is
        /// never cut by a write timeout — would-block just waits and the no-
        /// progress grace remains the authority. The DEDICATED writer uses its
        /// blocking <c>_stream.Write</c> (all-or-nothing per call, bounded by the
        /// socket's SendTimeout): on timeout the connection is CLOSED (P645) —
        /// a slow-but-alive human's blocking write simply completes (bytes flow),
        /// so P619c's intent is preserved, and only a genuinely stalled write
        /// (unknown progress) closes the socket. <see cref="SendOverride"/> is a
        /// P645 test seam that injects a deterministic fake send sink (the
        /// "fake socket/stream" the suite uses to unit-test partial writes).
        /// Caller must NOT hold <c>_sync</c>.</summary>
        private DrainResult SendInflight()
        {
            if (SendOverride is { } ov)
            {
                var o = ov(_inflight!, _inflightOffset, _inflight!.Length - _inflightOffset);
                if (o.Timeout) { FailAndClose(DisconnectReason.SendTimeout); return DrainResult.Dead; }
                if (o.Error) { FailAndClose(DisconnectReason.SendError); return DrainResult.Dead; }
                if (o.WouldBlock) return DrainResult.Blocked;
                return AdvanceInflight(o.Bytes);
            }
            if (_nonBlocking)
            {
                var sock = _tcpClient.Client;
                int n;
                try
                {
                    n = sock.Send(_inflight!, _inflightOffset, _inflight!.Length - _inflightOffset, SocketFlags.None);
                }
                catch (SocketException se)
                {
                    if (se.SocketErrorCode == SocketError.WouldBlock || se.SocketErrorCode == SocketError.TryAgain)
                        return DrainResult.Blocked;   // OS buffer full — keep in-flight, retry later
                    bool timeout = se.SocketErrorCode == SocketError.TimedOut;
                    FailAndClose(timeout ? DisconnectReason.SendTimeout : DisconnectReason.SendError);
                    return DrainResult.Dead;
                }
                catch (Exception)
                {
                    FailAndClose(DisconnectReason.SendError);
                    return DrainResult.Dead;
                }
                return AdvanceInflight(n);
            }
            // Dedicated (blocking) writer: _stream.Write transmits the whole
            // remaining frame or throws. Routed through _stream so the existing
            // controlled-stream test seam can drive the write behaviour.
            try
            {
                _stream.Write(_inflight!, _inflightOffset, _inflight!.Length - _inflightOffset);
                return AdvanceInflight(_inflight.Length - _inflightOffset);
            }
            catch (Exception ex) when (IsSocketTimeout(ex))
            {
                FailAndClose(DisconnectReason.SendTimeout);
                return DrainResult.Dead;
            }
            catch (Exception)
            {
                FailAndClose(DisconnectReason.SendError);
                return DrainResult.Dead;
            }
        }

        /// <summary>P645 — fold <paramref name="n"/> accepted bytes into the
        /// in-flight offset, refreshing the liveness clock. Clears the in-flight
        /// frame once the whole frame is on the wire. <c>n &lt;= 0</c> means
        /// nothing was accepted (would-block surfaced as a 0-length result) —
        /// keep the frame, treat as <c>Blocked</c>, never a dead peer.</summary>
        private DrainResult AdvanceInflight(int n)
        {
            if (n <= 0)
            {
                // Nothing accepted this call (some stacks surface would-block as
                // a 0-length result). Keep the in-flight frame; not a dead peer.
                return DrainResult.Blocked;
            }
            // Real write progress: the OS accepted these bytes — refresh the
            // no-progress liveness clock so a slow-but-flowing peer is never cut.
            Interlocked.Exchange(ref _lastWriteProgressUtcTicks, DateTime.UtcNow.Ticks);
            _inflightOffset += n;
            if (_inflightOffset >= _inflight!.Length)
            {
                _inflight = null;
                _inflightOffset = 0;
                return DrainResult.Wrote;   // frame fully on the wire
            }
            return DrainResult.Wrote;       // partial progress — still in flight, next call continues
        }

        /// <summary>P645 — stamp the disconnect reason and tear the peer down
        /// via the owner callback (which closes the stream, unblocking the read
        /// loop so <c>OnPeerLeft</c> fires with this reason). Exactly-once
        /// guarded by <c>_disconnected</c>.</summary>
        private void FailAndClose(DisconnectReason reason)
        {
            if (_disconnected) return;
            _disconnected = true;
            _reason = reason;
            _cts.Cancel();
            _onDead(reason);
        }

        // P634 — instrumentation + test seams (InternalsVisibleTo). Lets the
        // server sample per-peer send-backlog (scope item 1: "per-peer queue
        // depth and the age of the oldest guaranteed frame") and lets the
        // transport suite assert the ordered-before-stream drain priority
        // without racing the live writer.
        internal (int OrderedPending, int StreamPending, long QueuedBytes) SnapshotQueue()
        {
            lock (_sync) return (_orderedQueue.Count, _streamQueue.Count, _queuedBytes);
        }

        /// <summary>True when the NEXT drained frame is ordered (higher-priority
        /// world/join) traffic rather than a stream frame — i.e. the priority
        /// lane is non-empty, so the drain will take it first.</summary>
        internal bool NextDrainedIsOrdered()
        {
            lock (_sync) return _orderedQueue.First is not null;
        }

        /// <summary>Age of the oldest pending guaranteed frame (ordered lane,
        /// plus guaranteed stream frames), in milliseconds — the join/world
        /// data head-of-line delay this task measures. 0 when none pending.</summary>
        internal long OldestGuaranteedAgeMs()
        {
            long now = DateTime.UtcNow.Ticks;
            long oldest = long.MaxValue;
            lock (_sync)
            {
                foreach (var n in _orderedQueue) if (n.EnqueuedUtcTicks < oldest) oldest = n.EnqueuedUtcTicks;
                foreach (var n in _streamQueue) if (n.Guaranteed && n.EnqueuedUtcTicks < oldest) oldest = n.EnqueuedUtcTicks;
            }
            return oldest == long.MaxValue ? 0 : (now - oldest) / TimeSpan.TicksPerMillisecond;
        }

        private void WriterLoop()
        {
            while (true)
            {
                lock (_sync)
                {
                    while (!PromoteHeadLocked()
                           && !_disconnected && !_cts.IsCancellationRequested)
                        Monitor.Wait(_sync);
                    if (_disconnected || _cts.IsCancellationRequested) return;
                }
                // One send of the in-flight frame's remaining bytes. On this
                // dedicated writer's BLOCKING socket, _stream.Write transmits the
                // whole remaining frame (or blocks up to SendTimeout then
                // throws); the P645 in-flight/offset design guarantees a partial
                // send is RESUMED on the next iteration, never abandoned — and a
                // timed-out write whose progress is unknowable CLOSES the
                // connection (reason SendTimeout) rather than continuing on a
                // possibly-corrupt stream. P619c's intent is preserved: a
                // laggy-but-alive human's blocking write simply completes (bytes
                // flow), so it is never cut — only a write that genuinely stalls
                // for the full SendTimeout closes the socket, which is the only
                // safe thing to do once framing may be corrupt.
                var r = SendInflight();
                if (r == DrainResult.Dead) return;
                if (r == DrainResult.Blocked)
                {
                    // Socket full on a blocking writer is unusual (Send blocks
                    // rather than returning would-block); brief backoff, keep
                    // the in-flight frame for the next iteration.
                    Thread.Sleep(1);
                }
                // Wrote: loop to finish the in-flight frame or promote the next.
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
            // P634 — enqueue timestamp (Utc ticks) so the transport can report
            // the age of the oldest pending guaranteed frame (join/world
            // head-of-line delay) for the live measurements.
            public readonly long EnqueuedUtcTicks;
            public QueuedFrame(byte[] frame, bool guaranteed, int coalesceKey, long enqueuedUtcTicks)
            {
                Frame = frame;
                Guaranteed = guaranteed;
                CoalesceKey = coalesceKey;
                EnqueuedUtcTicks = enqueuedUtcTicks;
            }
        }
    }

    /// <summary>P645 — the result of one send-sink (<c>PeerSender.SendOverride</c>)
    /// attempt: how many bytes were accepted (<see cref="Bytes"/>), or a
    /// would-block / timeout / hard-error condition. Mirrors what a real
    /// non-blocking <c>Socket.Send</c> can report, so the suite can unit-test
    /// the drain's partial-write / timeout logic with a deterministic fake.
    /// Top-level (not nested in the private PeerSender) so the test assembly
    /// can name it and build <c>SendOverride</c> funcs.</summary>
    public readonly struct SendOutcome
    {
        public readonly bool WouldBlock;
        public readonly bool Timeout;
        public readonly bool Error;
        public readonly int Bytes;
        public SendOutcome(bool wouldBlock, bool timeout, bool error, int bytes)
        {
            WouldBlock = wouldBlock;
            Timeout = timeout;
            Error = error;
            Bytes = bytes;
        }
        public static SendOutcome Sent(int bytes) => new(false, false, false, bytes);
        public static SendOutcome Blocked() => new(true, false, false, 0);
        public static SendOutcome TimedOut() => new(false, true, false, 0);
        public static SendOutcome Faulted() => new(false, false, true, 0);
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
