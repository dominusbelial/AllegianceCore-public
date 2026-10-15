using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Session;

/// <summary>
/// A <see cref="TcpTransport"/> client session that drives a <see cref="ClientWorld"/>.
/// Closely modeled on AllegianceCore's own <c>Bot.cs</c>: it connects, sends a
/// LOGONREQ, waits for the ACK, commands controls as a real <c>FM_C_SHIP_UPDATE(24)</c>,
/// and on the receive path decodes inbound messages — feeding every type
/// <see cref="ClientWorld"/> understands (plus <see cref="LogonAck"/> for connect
/// proof) into <see cref="World"/> under a lock. The shared <c>ExportShip.Fmid</c>
/// (26) is branched on its raw <c>objecttype</c> before decoding, like <c>Bot.cs</c>.
///
/// <see cref="World"/> is exposed the same way <c>Bot.cs</c> exposes its
/// <c>IReadOnlyList&lt;T&gt;</c> properties: it is the single live model, its
/// mutations all happen under <see cref="_gate"/> on the receive thread, and a
/// caller reads it (after a bounded wait/pump) on its own thread without holding
/// the lock. This is the client half of the transport, so only
/// <see cref="ITransportSite.OnReceive"/> is raised.
/// </summary>
public sealed class ConnectionSession : ITransportSite, IDisposable
{
    private TcpTransport? _transport;
    private readonly object _gate = new();
    private readonly ClientWorld _world = new();
    private LogonAck? _ack;
    // P473 — every FM_S_BUY_LOADOUT_ACK(5) received, in arrival order: the raw
    // server answer to SendBuyLoadout, mirrored from Bot so the combat/spawn
    // harness paths can compare the granted hull/parts with the requested ones
    // and detect a refusal or partial grant. Locked via _gate like _ack.
    private readonly List<BuyLoadoutAck> _buyLoadoutAcks = new();
    // P508 — every FM_CS_PING(35) echo received, in arrival order: the server's
    // sim-time stamp (timeServer = SimNow·1000, ms) paired with the LOCAL receive
    // time. The native SHIP_UPDATE needs a server-converted time (the server drops
    // any outside ±1.5 s of SimNow), and these echoes feed the client's
    // OwnShipServerClock. Locked via _gate like _ack.
    private readonly List<(uint ServerMs, long LocalMs)> _pingEchoes = new();

    // P633 — cumulative inbound wire bytes delivered to OnReceive (fed + crc +
    // framing overhead). Used by get_perf_stats to report the bot's real decode
    // throughput (MB/s). Atomic so a reader never tears the counter.
    private long _decodedBytes;
    /// <summary>P633 — cumulative inbound bytes this session has decoded since
    /// connect (monotonic; diff over time = decode throughput).</summary>
    public long DecodedBytes => Interlocked.Read(ref _decodedBytes);
    // P627 — the distinct set of inbound fmids the receive dispatch did NOT
    // recognize (fell through to the `default` branch). Guarded by _gate like
    // the rest of the receive path state. Kept as a bounded distinct set (max
    // 65536 distinct fmids) so a client being fed S-messages the real client
    // does not yet consume cannot grow memory unboundedly; used by the
    // P627 dispatch-coverage test to assert a supported S-message is consumed
    // and by the receive audit to list genuinely-unconsumed messages.
    private readonly HashSet<ushort> _unknownFmids = new();
    /// <summary>P627 — every distinct inbound fmid the receive dispatch fell
    /// through on (no <c>case</c> matched), i.e. a message the real client did
    /// not consume. Snapshot-on-read under <see cref="Gate"/>.</summary>
    public IReadOnlyCollection<ushort> UnknownFmids { get { lock (_gate) return _unknownFmids.ToArray(); } }
    /// <summary>P627 — clear the unknown-fmid record (tests feed one message at
    /// a time on a fresh/labeled session and reset before each feed).</summary>
    public void ClearUnknownFmids() { lock (_gate) _unknownFmids.Clear(); }
    // P320 — set (volatile, on the transport receive thread) when the established
    // session to the server is lost; read safely from the render thread every frame.
    private volatile bool _lost;

    // P671 — native keep-alive, ported from BaseClient::CheckServerLag
    // (clintlib.cpp:2050-2087). Every connected client (bot OR MonoGame human)
    // sends a CS_PING every <c>maxLagCheck = 6.0f</c> seconds, in EVERY state
    // (lobby, hangar, docked, flying, pod, dead) — the server echoes each one
    // (Server.Dispatch.cs:38-67). The UNANSWERED-ping session-loss rule mirrors
    // native: when more than <c>g_cPingsTimeout(5)</c> pings have gone unanswered
    // AND at least <c>g_sDeadTime(30)</c> has elapsed since any server message,
    // the session is declared lost (clintlib.cpp:2063-2068).
    private const int KeepAliveIntervalMs = 6000;   // maxLagCheck = 6.0f
    private const int PingsTimeout = 5;             // g_cPingsTimeout
    private const int DeadTimeMs = 30000;           // g_sDeadTime (s -> ms)
    private Thread? _keepAliveThread;
    private readonly ManualResetEventSlim _keepAliveGate = new(false); // set = wake/stop
    private int _keepAliveStop;
    private int _unansweredPings;        // guarded by _gate
    private long _lastServerMessageTick; // Environment.TickCount64, guarded by _gate
    private long _keepAlivePingsSent;    // guarded by _gate

    /// <summary>True while the underlying transport is connected.</summary>
    public bool Connected => _transport?.IsConnected ?? false;

    /// <summary>P671 test seam — total CS_PING keep-alive frames sent by this
    /// session's keep-alive thread since connect. Proves a bot/client session
    /// actually pings at the 6 s cadence without having to sniff the wire.</summary>
    public long KeepAlivePingsSent { get { lock (_gate) return _keepAlivePingsSent; } }

    /// <summary>
    /// P671 — start the native keep-alive thread up-front, in the constructor,
    /// NOT in <see cref="Connect"/>. The MCP harness (ObservedSession /
    /// StallWatchdog) injects a <c>TcpTransport</c> directly into this session's
    /// private <c>_transport</c> and calls <c>Transport.Join(...)</c> — never
    /// this class's <see cref="Connect"/>. Tying the keep-alive to
    /// <see cref="Connect"/> therefore left every MCP-seated bot (idle/weave
    /// BotClass and combat ConnectionSession alike) without a keep-alive, so a
    /// silently-docked bot was dropped by the server's <c>DataLinkIdleTimeout</c>
    /// at ~30 s. The thread is started here, references the current
    /// <c>_transport</c> lazily each tick, and is persistent (it survives a
    /// reconnect and skips ticks while disconnected), so it covers every
    /// connection path.
    /// </summary>
    public ConnectionSession()
    {
        StartKeepAlive();
    }

    /// <summary>
    /// P320 — true once the established TCP session to the server has been lost
    /// (server crash / network drop). Set by <see cref="OnSessionLost"/> on the
    /// transport receive thread; <see cref="System.Threading.Volatile"/> so the
    /// render thread can poll it every frame without a lock. Latched: once true
    /// it stays true for the life of this session.
    /// </summary>
    public bool Lost => _lost;

    /// <summary>
    /// P320 — raised (on the transport receive thread) the first time the
    /// established session to the server is lost. Handlers must NOT touch
    /// MonoGame graphics here (wrong thread); latch a flag and do UI work on the
    /// render loop, or just poll <see cref="Lost"/>.
    /// </summary>
    public event Action? SessionLost;

    /// <summary>The last LOGONACK (or rejection) received; null until one arrives.</summary>
    public LogonAck? Ack { get { lock (_gate) return _ack; } }

    /// <summary>Every <c>FM_S_BUY_LOADOUT_ACK(5)</c> received, in arrival order —
    /// the raw server answer to <see cref="SendBuyLoadout"/> (P473), mirrored
    /// from Bot. Locked snapshot-on-read via <see cref="Gate"/>.</summary>
    public IReadOnlyList<BuyLoadoutAck> BuyLoadoutAcks { get { lock (_gate) return _buyLoadoutAcks.ToArray(); } }

    /// <summary>Clear the accumulated BUY_LOADOUT_ACKs. P473 — a spawn harness
    /// resets right before its own <see cref="SendBuyLoadout"/> so the ONLY ack
    /// in the list is the one answering THAT purchase (a peer also receives a
    /// join-burst BUY_LOADOUT_ACK for its default hull at seat, which must not
    /// be mistaken for the purchase's ack).</summary>
    public void ResetBuyLoadoutAcks() { lock (_gate) _buyLoadoutAcks.Clear(); }

    /// <summary>P508 — every FM_CS_PING(35) echo (server sim-ms, local receive
    /// ms) received since the last call, drained in arrival order for the own-ship
    /// server clock (<see cref="Flight.OwnShipServerClock"/>). Locked snapshot
    /// like <see cref="BuyLoadoutAcks"/>.</summary>
    public IReadOnlyList<(uint ServerMs, long LocalMs)> TakePingEchoes()
    {
        lock (_gate)
        {
            var arr = _pingEchoes.ToArray();
            _pingEchoes.Clear();
            return arr;
        }
    }

    /// <summary>The live world model, upserted on the receive thread under
    /// <see cref="_gate"/>. Mutations happen on the transport thread; a caller
    /// should wait for the state it expects (e.g. <see cref="ClientWorld.MyShipId"/>)
    /// rather than read blind.</summary>
    public ClientWorld World => _world;

    /// <summary>The lock the receive thread holds while upserting <see cref="World"/>.
    /// A caller that does more than a single, isolated <c>TryGetValue</c> against one
    /// of <see cref="World"/>'s dictionaries — in particular any <c>foreach</c> over
    /// <see cref="ClientWorld.Ships"/> or its sibling tables — must hold this lock
    /// for the duration, or risk a real <c>System.InvalidOperationException</c>
    /// ("Collection was modified") the moment the receive thread upserts an entry
    /// mid-enumeration. This was latent (a single peer's update cadence rarely won
    /// the race) until a burst of ~20 simultaneous ship joins made it reliably
    /// reproducible live in the MonoGame client.</summary>
    public object Gate => _gate;

    /// <summary>Connect to a hosting server and register our name. Defaults to
    /// loopback (every existing caller's behavior, unchanged) — pass
    /// <paramref name="host"/> to reach a server elsewhere on the network.
    /// Confirmed live (2026-09-15): this previously hardcoded 127.0.0.1
    /// unconditionally, silently ignoring any other host the caller supplied
    /// (Allegiance.ClientCore.MonoGame/Program.cs warned about this rather
    /// than silently dropping it, but no caller had ever lifted the actual
    /// restriction, since this project's testing was LAN-loopback only until
    /// this session's netem-latency experiment needed a real non-loopback
    /// peer).</summary>
    public void Connect(ushort port, string playerName, string host = "127.0.0.1")
    {
        // P321 — a retry must not abandon the previous transport: its socket and
        // receive thread would leak for the process lifetime. Shut it down the
        // same way a normal disconnect does (closes the socket, joins the receive
        // thread, sets _intentionalShutdown so no spurious SessionLost fires),
        // then start from a genuinely clean world so no stale ships/ids from the
        // previous failed/dead connection survive into the new session. Done
        // before the new TcpTransport is created (so its fresh receive thread is
        // the only writer) and under _gate (so a lingering old thread mid-OnReceive
        // drains before Reset clears the tables it might still touch).
        var previous = _transport;
        _transport = null;               // detach first: Shutdown won't self-null, and the new one is fresh
        previous?.Shutdown();

        lock (_gate)
        {
            _ack = null;                 // a fresh session has no ack yet
            _lost = false;               // a new session is not lost (was latched true by a dropped one)
            _world.Reset();
        }

        _transport = new TcpTransport(this);
        _transport.Join(host, port, playerName);
        StartKeepAlive();
    }

    /// <summary>P671 — (re)arm the native keep-alive thread for this session.
    /// Idempotent: resets the unanswered-ping / last-server-message bookkeeping
    /// (so a reconnect starts clean) and starts the thread if it is not already
    /// running (it may have exited after a detected loss). The thread is first
    /// started from the constructor, so this is a no-op on the common path.</summary>
    private void StartKeepAlive()
    {
        lock (_gate)
        {
            _unansweredPings = 0;
            _lastServerMessageTick = Environment.TickCount64;
            _keepAlivePingsSent = 0;
        }
        if (_keepAliveThread != null) return;          // already running
        Volatile.Write(ref _keepAliveStop, 0);
        _keepAliveGate.Reset();
        var t = new Thread(KeepAliveLoop) { IsBackground = true, Name = "session-keepalive" };
        _keepAliveThread = t;
        t.Start();
    }

    /// <summary>
    /// P671 — the native keep-alive + session-loss rule loop, ported from
    /// <c>BaseClient::CheckServerLag</c> (clintlib.cpp:2050-2087): while
    /// connected, send a <c>CS_PING</c> every 6 s in every state, and when more
    /// than 5 pings have gone unanswered for at least 30 s since any server
    /// message, declare the session lost (native <c>g_cPingsTimeout(5)</c> +
    /// <c>g_sDeadTime(30)</c>, clintlib.cpp:2063-2068). The server echoes each
    /// ping (Server.Dispatch.cs:38-67) and any inbound resets the counters, so a
    /// healthy link is never lost. PERSISTENT: it survives a not-yet-connected
    /// window and a reconnect (skipping ticks while disconnected) so it works for
    /// every connection path, including the MCP harness's direct
    /// <c>Transport.Join</c>. Exits only on <see cref="StopKeepAlive"/> (dispose)
    /// or a detected loss; on exit it clears <c>_keepAliveThread</c> so a later
    /// reconnect can re-arm it.
    /// </summary>
    private void KeepAliveLoop()
    {
        try
        {
            while (true)
            {
                if (_keepAliveGate.Wait(KeepAliveIntervalMs)) return;   // stop requested
                if (Volatile.Read(ref _keepAliveStop) != 0) return;
                bool lostNow = false;
                lock (_gate)
                {
                    if (_lost) return;
                    // Skip this tick while not connected (pre-connect window,
                    // between transports during a reconnect, etc.); never exit.
                    if (_transport is { IsConnected: true })
                    {
                        SendPing();
                        _unansweredPings++;
                        _keepAlivePingsSent++;
                        if (_unansweredPings > PingsTimeout
                            && Environment.TickCount64 - _lastServerMessageTick >= DeadTimeMs)
                            lostNow = true;
                    }
                }
                if (lostNow) { RaiseLost(); return; }
            }
        }
        finally
        {
            _keepAliveThread = null;   // allow re-arm after loss / dispose
        }
    }

    /// <summary>Send a LOGONREQ; the ACK (or rejection) is received asynchronously.</summary>
    public void SendLogonRequest(LogonRequest req) =>
        Send(FedPacket.Pack(new[] { req.Encode() }));

    /// <summary>
    /// P508 — send a <c>FM_CS_PING(35)</c> so the server echoes its current sim-time
    /// (<c>timeServer = SimNow·1000</c> ms, <c>FedSrv.CPP:4897-4911</c>). The echoes
    /// feed the client's <see cref="Flight.OwnShipServerClock"/>, which stamps the
    /// native SHIP_UPDATE's server-converted time. Sent at a modest cadence (~1 Hz)
    /// while connected.
    /// </summary>
    public void SendPing()
    {
        var msg = new Ping { TimeClient = 0u, TimeServer = 0u, Fmg = 0 };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>Block until a LOGONACK arrives (or <paramref name="timeoutMs"/>). Returns the ACK.</summary>
    public LogonAck WaitForAck(int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var ack = Ack;
            if (ack is not null) return ack;
            Thread.Sleep(5);
        }
        throw new TimeoutException("timed out waiting for LOGONACK");
    }

    /// <summary>Command the ship's controls: the four axes and the movement-bit
    /// <paramref name="stateM"/>, encoded as a real <c>FM_C_SHIP_UPDATE(24)</c>.
    /// Mirror of <c>Bot.CommandControls</c>.</summary>
    public void CommandControls(ControlData controls, int stateM = 0)
    {
        var msg = new ShipUpdate
        {
            Time = 0u,              // client's own time — the server reads controls only
            Cookie = (byte)(Ack?.Cookie ?? 0),
            Payload = ClientShipUpdate.FromControls(0u, stateM, controls),
        };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>
    /// P508 — send a NATIVE-style <c>FM_C_SHIP_UPDATE(24)</c> carrying the client's
    /// reported pose + the server-converted time + the current ship cookie,
    /// exactly the payload a P508 client sends every <c>c_dsecUpdateClient</c>
    /// (0.15 s). The server sanity-checks the pose and answers
    /// <c>SHIP_RESET(183)</c> on rejection (P507). <paramref name="time"/> is the
    /// estimated server sim time in ms (<see cref="Flight.OwnShipServerClock"/>)
    /// and <paramref name="cookie"/> is the ship cookie from the last
    /// SET_CLUSTER / SHIP_RESET.
    /// </summary>
    public void SendShipPose(uint time, byte cookie, ClientShipUpdate payload)
    {
        var msg = new ShipUpdate { Time = time, Cookie = cookie, Payload = payload };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>
    /// P612 — spectator free-camera sector switch: send a real
    /// <c>FM_C_VIEW_CLUSTER(104)</c> naming the sector the camera is now viewing.
    /// The server streams that sector's ships to an ADMIN spectator (ignoring
    /// side visibility) and ignores it for non-admins (no information leak).
    /// </summary>
    /// <summary>Native sectormap.cpp:1499-1507, team sector-mark relay.</summary>
    public void SendHighlightCluster(short clusterId, bool highlight) =>
        Send(FedPacket.Pack([new HighlightCluster { ClusterId = clusterId, Highlight = highlight }.Encode()]));

    public void SendViewClusterRequest(short clusterId)
    {
        var msg = new ViewClusterRequest { ClusterId = clusterId, OtTarget = -1, OidTarget = -1 };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>Send an undock announce, encoded as a real <c>FM_C_DOCKED(48)</c> —
    /// — the mirror of <see cref="CommandControls"/> for the only surviving half
    /// of that message. <paramref name="stationId"/> of <c>-1</c> (NA) is the
    /// pure-undock announce the server honours (<c>Messages.h:402-404</c>;
    /// <c>stationID != NA</c> is the obsolete dock-request half, dropped by
    /// Server.cs). Same signature/semantics as <c>Bot.SendDocked</c>.</summary>
    public void SendDocked(short stationId = -1)
    {
        var msg = new Docked { StationId = stationId };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>Send a real <c>FM_C_BUY_LOADOUT(4)</c> — what the native
    /// WinTrek client's Hangar screen "Launch" button sends. Same
    /// signature/semantics as <c>Bot.SendBuyLoadout</c>: <paramref name="parts"/>
    /// defaults to empty (the hull-only shape). <paramref name="fLaunch"/>=true
    /// is the real client's launch request; the server answers with
    /// <c>FM_S_BUY_LOADOUT_ACK(5)</c> and, if the ship was docked, undocks it
    /// (a second SetCluster lands).</summary>
    public void SendBuyLoadout(short hullId, bool fLaunch, IReadOnlyList<ExpandedPartData>? parts = null)
    {
        var msg = new BuyLoadoutRequest { HullId = hullId, FLaunch = fLaunch, Parts = parts ?? [] };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>Send a chat message, encoded as a real <c>FM_CS_CHATMESSAGE(23)</c> —
    /// the mirror of <c>Bot.SendChat</c> (ported like <see cref="SendDocked"/>/
    /// <see cref="SendBuyLoadout"/> were). The sender ship is filled from this
    /// session's own assignment (<c>cd.sidSender</c>, <c>Messages.h:206</c>),
    /// text is encoded as a CB_ZTS blob (<c>clintlib.cpp:2444-2445</c>), and the
    /// supported targets mirror <c>Bot.SendChat</c> — the server relays
    /// Everyone/Team/Individual(+aliases)/AllSector/FriendlySector and drops the
    /// rest, which the server logs. The relayed message (including the sender's
    /// own echo) lands in <see cref="ClientWorld.Events.Chats"/>.</summary>
    public void SendChat(
        string message,
        ChatTarget target = ChatTarget.Everyone,
        short oidRecipient = 0,
        short voiceOver = 0,
        short commandId = 0,
        bool objectModel = false,
        short oidTarget = -1,
        short otTarget = -1)
    {
        var msg = new ChatMessage
        {
            Message = message,
            SidSender = (short)(World.MyShipId ?? -1),
            ChatTarget = (int)target,
            OidRecipient = oidRecipient,
            VoiceOver = voiceOver,
            CommandID = commandId,
            BObjectModel = objectModel,
            OidTarget = oidTarget,
            OtTarget = otTarget,
        };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>Send a real <c>FM_CS_CHATBUOY(108)</c> — the message the native
    /// <c>BaseClient::SendChat</c> actually uses when the chat targets a buoy
    /// object (<c>clintlib.cpp:2460-2469</c>: <c>otTarget == OT_buoy</c> →
    /// CHATBUOY, with the target's <see cref="DataBuoyIGC db"/> exported into
    /// the payload; otherwise CHATMESSAGE). The commander's dispatch to a
    /// docked miner (<c>CommanderResearchAndExpand.cs:1615-1618</c>) is the
    /// canonical real use. Routing semantics match <see cref="SendChat"/>
    /// exactly (the server's <c>SelectChatTargets</c> is shared via
    /// <see cref="IChatRoutingTarget"/>). Same signature as
    /// <c>Allegiance.Bot.Bot.SendChatBuoy</c>; the sender ship is filled from
    /// this session's own assignment. The relayed buoy (excluding the
    /// individual-addressed case, which the server sends only to the addressed
    /// peer, and which like <see cref="SendChat"/> is not echoed back to the
    /// sender) lands in <see cref="ClientWorld.Events.ChatBuoys"/>.</summary>
    public void SendChatBuoy(
        string message,
        ChatTarget target,
        short oidRecipient,
        DataBuoyIGC db,
        short voiceOver = 0,
        short commandId = 0,
        bool objectModel = false)
    {
        var msg = new ChatBuoy
        {
            Message = message,
            Cd = new ChatData
            {
                SidSender = (short)(World.MyShipId ?? -1),
                ChatTarget = (int)target,
                OidRecipient = oidRecipient,
                VoiceOver = voiceOver,
                CommandID = commandId,
                BObjectModel = objectModel,
            },
            Db = db,
        };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>Send a real <c>FM_CS_PLAYER_READY(45)</c> — what the native
    /// WinTrek client's Team Lobby \"JOIN\" control sends (<c>teampane.cpp</c>),
    /// the mirror of <c>Bot.SendPlayerReady</c>. Same signature exactly. The
    /// server is expected to relay it back to everyone on the sender's side,
    /// including the sender itself (<c>Server.cs</c> relays to same-side peers);
    /// the relay lands in <see cref=\"ClientWorld.Events.PlayerReadyEchoes\"/>
    /// and updates <see cref=\"ClientWorld.Roster\"/> (the current-state map),
    /// but the sender never applies its own click locally — it waits for this
    /// relay, exactly like the native client's <c>appmsg.cpp:2297-2312</c>.</summary>
    public void SendPlayerReady(bool fReady, short shipId)
    {
        var msg = new PlayerReady { FReady = fReady, ShipId = shipId };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>P442 — send a real <c>FM_C_BUCKET_DONATE(64)</c>: donate
    /// <paramref name="moneyGiven"/> into the side's bucket
    /// <paramref name="iBucket"/> (P119, Messages.h:488-491). The server
    /// spends from the donor's own wallet (P198) and broadcasts the resulting
    /// <c>MONEY_CHANGE(60)</c> to the whole side — the round-trip that drives
    /// the donor's <see cref="ClientWorld.MyMoney"/> down by exactly the spent
    /// amount. Mirror of <c>Bot.SendDonate</c>.</summary>
    public void SendBucketDonate(short iBucket, int moneyGiven)
    {
        var msg = new BucketDonate { IBucket = iBucket, MoneyGiven = moneyGiven };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>P282 — send a real <c>FM_CS_FORCE_TEAM_READY(47)</c>, the side's
    /// leader forcing their whole side ready (or un-forcing it), the mirror of
    /// <c>Bot.SendForceTeamReady</c>. The server refuses unless the sender is
    /// the named (own) side's current leader (<c>Server.cs</c> FORCE_TEAM_READY
    /// handler), forwards it to the whole mission on success (so the sender
    /// receives its own echo into
    /// <see cref="ClientWorld.ForceReadyBySideId"/>), and then re-runs the
    /// side-readiness recompute, which may fire TEAM_READY and the auto-start.</summary>
    public void SendForceTeamReady(bool fForceReady, short sideId)
    {
        var msg = new ForceTeamReady { SideId = sideId, FForceReady = fForceReady };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>Send a real <c>FM_C_SUICIDE(80)</c> — the client's "I want to
    /// die" request, the mirror of <c>Bot.SendSuicide</c>. The message is
    /// header-only (no fields to encode), so this takes no parameters. The
    /// server's <c>HandleSuicide</c> (<c>Server.cs:3869</c>) re-triggers the
    /// exact combat-kill path a normal death uses (<c>KillShipEvent</c>,
    /// launcher NULL), so this produces the same <c>KILL_SHIP(105)</c> death
    /// broadcast a combat death does — gated server-side on the mission being
    /// <c>STAGE_STARTED</c>, the ship being in a cluster, and not being docked.</summary>
    public void SendSuicide()
    {
        var msg = new Suicide();
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>P481 — send a real <c>FM_CS_LOGOFF(3)</c>, the client's
    /// deliberate \"I want to logoff\" (the server echoes it and cleans the
    /// player up via ReleasePeer, FedSrv.CPP:5787-5802). Header-only like
    /// <see cref=\"SendSuicide\"/>. Used by the game-over Back action's
    /// <c>stage == Over → leave the mission</c> branch (native
    /// <c>StartQuitMission</c>): the client leaves the running game back to the
    /// server browser. Mirror of <c>Bot.SendLogoff</c>.</summary>
    public void SendLogoff()
    {
        Send(FedPacket.Pack(new[] { new Logoff().Encode() }));
    }

    /// <summary>P414 — send a real <c>FM_C_RIPCORD_REQUEST(184)</c> — the client's
    /// "start / re-target / cancel a ripcord countdown" request, the mirror of
    /// <c>Bot.SendRipcordRequest</c> (the P111-P113/P172 server state machine is
    /// <c>Server.RequestRipcordCore</c>, <c>Server.Docking.cs:388</c>). A real
    /// target sector id starts (or re-targets) the countdown toward a valid
    /// anchor there (<c>RIPCORD_ACTIVATE</c>/<c>RIPCORD_DENIED</c>), and
    /// <see cref="RipcordRequest.NA"/> cancels whatever ripcord the ship
    /// currently has active (<c>RIPCORD_ABORTED</c>). Built from the sector the
    /// pilot picked on the expanded starmap (<c>Game1._selectedSectorId</c>);
    /// see the P414 report for the no-selection / cancel semantics.</summary>
    public void SendRipcordRequest(short sidRipcord)
    {
        var msg = new RipcordRequest { SidRipcord = sidRipcord };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>P281 — send a real <c>FM_C_POSITIONREQ(39)</c>, the side-select
    /// confirmation that seats the peer on a side. The server now defers a
    /// joining peer's spawn to a post-connect side-select step (SIDE_ROSTER(211)
    /// then wait); sending POSITIONREQ with the chosen <paramref name="sideId"/>
    /// (or -1/NA for auto-pick) triggers the seat + launch burst that culminates
    /// in SET_CLUSTER (which sets <see cref="ClientWorld.MyShipId"/>).</summary>
    public void SendPositionReq(short sideId)
    {
        var msg = new PositionReq { SideId = sideId };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>Join the target wing (WinTrek.cpp:10659-10662).</summary>
    public void SendWingId(short shipId, short wingId)
    {
        var message = new SetWingId { ShipID = shipId, WingId = wingId };
        Send(FedPacket.Pack([message.Encode()]));
    }

    /// <summary>P281 — send a real <c>FM_C_RANDOMIZE_TEAMS(21)</c>, the mission
    /// owner's "balance the teams" trigger surfaced on the side-select screen.
    /// The server owner-gates it (<c>FedSrv.CPP:6964-6997</c>), so the client
    /// only surfaces the key while it believes it is the mission owner.</summary>
    public void SendRandomizeTeams()
    {
        var msg = new RandomizeTeams();
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>Native lobby controls. Settings change only on the server echo;
    /// the server checks mission ownership / team leadership.</summary>
    public void SendLockLobby(bool locked) => Send(FedPacket.Pack([new LockLobby { FLock = locked }.Encode()]));
    public void SendLockSides(bool locked) => Send(FedPacket.Pack([new LockSides { FLock = locked }.Encode()]));
    public void SendAutoAccept(short sideId, bool enabled) =>
        Send(FedPacket.Pack([new AutoAccept { SideId = sideId, FAutoAccept = enabled }.Encode()]));
    public void SendPositionAck(short shipId, short sideId, bool accepted) =>
        Send(FedPacket.Pack([new PositionAck { ShipId = shipId, SideId = sideId, FAccepted = accepted }.Encode()]));
    public void SendDelPositionReq(short shipId, short sideId, DelPositionReqReason reason) =>
        Send(FedPacket.Pack([new DelPositionReq { ShipId = shipId, SideId = sideId, Reason = reason }.Encode()]));

    /// <summary>Send a real <c>FM_C_BOARD_SHIP(103)</c> — the client's
    /// dual-purpose board/leave request, the mirror of <c>Bot.SendBoard</c>.
    /// A non-NA <paramref name="sidParent"/> boards that same-side docked ship
    /// as a turret gunner (or observer when no turret slot is open); the wire
    /// value NA = -1 leaves whatever ship this player is currently boarded on.
    /// The server (<c>HandleBoardShip</c>, <c>Server.cs:6296</c>) gates on a
    /// started mission and same-side resolution, answers refusals with
    /// <c>BOARD_NACK(178)</c>, and reflects success through the per-tick
    /// <c>SHIP_STATUS</c> refresh (no dedicated success ack). This is the real
    /// entry point the P273 teleport pane uses to board a teammate's turret.</summary>
    public void SendBoardShip(short sidParent)
    {
        var msg = new BoardShip { SidParent = sidParent };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>P416 — promote a boarded turret gunner into the command seat.
    /// Wraps <c>FM_C_PROMOTE(190)</c>, the exact message native's
    /// TK_PromoteTurret1-4 keys send (WinTrek.cpp:9583-9601:
    /// <c>BEGIN_PFM_CREATE(..., C, PROMOTE)</c> with
    /// <c>pfmPromote-&gt;mountidPromoted = (key − TK_PromoteTurret1) + GetMaxFixedWeapons()</c>).
    /// The server's <c>HandlePromote</c> (P140) walks the requesting ship's OWN
    /// boarded children, promotes the one whose <c>TurretId</c> equals
    /// <c>mountidPromoted</c> in place, demotes the old commander into the
    /// vacated mount, and broadcasts <c>PROMOTE(189)</c> sector-wide. A mount
    /// matching no real boarded child is a silent no-op (no ack, no broadcast),
    /// so the caller should only send mounts that exist on the flown hull.
    /// This is the production client-side path that previously had no sender —
    /// the server feature (P140) was only reachable from tests/other clients.</summary>
    public void SendPromote(short mountidPromoted)
    {
        var msg = new PromoteRequest { MountidPromoted = mountidPromoted };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>P366 — command another (typically NPC) ship to dock / launch /
    /// return-to-base. Wraps <c>FM_CS_ORDER_CHANGE(93)</c>, the exact message
    /// native's leader-only drone context action sends (WinTrek.cpp
    /// contextDockDrone, line 3771-3787): <c>command=c_cmdAccepted</c>,
    /// <c>objectType=OT_invalid</c>, <c>objectID=NA</c>, and the action
    /// <c>commandID</c> — <c>c_cidGoto</c> for both dock and launch. The
    /// server-side handler (P148/P150/P152 HandleOrderChange) resolves the
    /// target on the commander's own side and applies the stay-docked / launch /
    /// return-to-base arm according to the bot's docked state, so the client
    /// does not need to choose a slot target — it just names the bot and the
    /// intent. Caller (Game1) gates this on <see cref="State.ClientWorld.IsTeamLeader"/>.</summary>
    public void SendOrderChange(short shipId, short commandId, short command)
    {
        var msg = new OrderChange
        {
            ShipID = shipId,
            ObjectID = -1,                       // NA (WinTrek.cpp:3787 objectID=NA)
            ObjectType = (IgcObjectType)(-1),    // OT_invalid = -1 (Igc/igc.h:116)
            CommandId = commandId,
            Command = command,
        };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>
    /// P420 — send a real <c>FM_C_ORDER_CHANGE(93)</c> naming an explicit
    /// target model, the client mirror of <c>Bot.SendOrderChange(short, short,
    /// short, IgcObjectType, short)</c>. The server's P153/P154 generic
    /// non-NPC fallback resolves the named object cross-side
    /// (<c>FindShipAnySide</c>) and — for the two real player actions —
    /// writes <c>Ship.CommandTarget</c> (<c>command == CmdCurrent</c>) or
    /// <c>Ship.AcceptedShipTarget</c> (<c>command == CmdAccepted</c>). Pass
    /// <see cref="OrderChange.CmdCurrent"/> with <c>objectType = IgcObjectType.Ship</c>
    /// and <c>objectId</c> = an enemy ship id to acquire a real cross-side
    /// missile/gun command target (the lock target <c>Ship.UpdateMissileLock</c>
    /// builds against); pass <c>objectId = NA</c> to clear it. This is the only
    /// client-visible way to point an external-facing peer at a specific ship.
    /// </summary>
    public void SendOrderChange(short shipId, short commandId, short command, IgcObjectType objectType, short objectId)
    {
        var msg = new OrderChange
        {
            ShipID = shipId,
            ObjectID = objectId,
            ObjectType = objectType,
            CommandId = commandId,
            Command = command,
        };
        lock (_gate) _world.Apply(msg);
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>Send a real <c>FM_C_ACTIVE_TURRET_UPDATE(99)</c> — the turret
    /// gunner's live aiming/control frame, the client mirror of the test
    /// harness's <c>WireClient.SendActiveTurret</c>. Carries the gunner's
    /// snapshot orientation + controls; the server (<c>HandleActiveTurretUpdate</c>)
    /// sets the shooting state (<c>weaponsMaskIGC</c>), installs the controls and
    /// catch-up-integrates the orientation onto the gunner's own ship
    /// (<c>shipIGC.cpp:3349-3392</c>). Used by the P273 live proof to show the
    /// turret aims from the gunner's input after boarding.</summary>
    public void SendActiveTurret(uint timeUpdate, ClientActiveTurretUpdate atu)
    {
        var msg = new ActiveTurretUpdate { TimeUpdate = timeUpdate, Atu = atu };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>Send a real <c>FM_C_INACTIVE_TURRET_UPDATE(100)</c> — clears the
    /// gunner's shooting state (<c>SetStateM(0)</c>, <c>fsship.cpp:1223</c>).</summary>
    public void SendInactiveTurret()
    {
        var msg = new InactiveTurretUpdate();
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>Send a real <c>FM_C_FIRE_EXPENDABLE(82)</c> — the client's \"fire my
    /// mounted dispenser\" request, the mirror of <c>Bot.SendFireExpendable</c>. The
    /// single field is the <see cref="EquipmentType"/> of the mounted dispenser to
    /// fire (the server resolves it at mount 0 via <c>GetMountedPart(et, 0)</c>,
    /// <c>FedSrv.CPP:5652</c>). The server's <c>HandleFireExpendable</c>
    /// (<c>Server.cs:6658</c>) deploys the expendable (chaff for a chaff launcher),
    /// decrements its ammo, and broadcasts the
    /// <c>FE(87)+CREATE_CHAFF(109)+MISSILE_SPOOFED(110)+END_SPOOFING(111)</c> flow
    /// sector-wide. Generic over <see cref="EquipmentType"/> (not chaff-specific) so
    /// a near-future mine/probe task can reuse it with a different value.</summary>
    public void SendFireExpendable(EquipmentType et)
    {
        var msg = new FireExpendableRequest { Et = et };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>
    /// P293 — send a real <c>FM_CS_DROP_PART(33)</c> asking the server to drop
    /// the mounted part at <paramref name="et"/>/<paramref name="mount"/> into
    /// space (the client-side mirror of <c>Bot.SendDropPart</c>). The server
    /// removes the part (<c>Ship.TryRemovePart</c>) and — unless the dropped
    /// part is an empty Pack — creates a real part-treasure and broadcasts
    /// <c>FM_S_TREASURE(28)</c> sector-wide (<c>Server.HandleDropPart</c>,
    /// <c>Server.cs:3124,7014</c>), which is the live path that puts a treasure
    /// into <see cref="ClientWorld.Treasures"/> for P293's renderer. <c>shipID</c>
    /// is sent uninitialized (0) exactly like native/the bot; the server fills it
    /// in on the sector-wide echo (<c>FedSrv.CPP:6312</c>).
    /// </summary>
    public void SendDropPart(EquipmentType et, byte mount)
    {
        var msg = new DropPart { Et = et, Mount = mount };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>P641 — guaranteed native in-flight move/swap (clintlib.h:2371-2400).</summary>
    public void SendSwapPart(EquipmentType type, sbyte oldMount, sbyte newMount)
    {
        var msg = new SwapPart { EtOld = type, MountOld = oldMount, MountNew = newMount };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>
    /// P626 — send a real <c>FM_C_VOTE(19)</c> casting <paramref name="agree"/>
    /// on the in-flight ballot <paramref name="ballotId"/> (the server relays
    /// neither BALLOT(17) nor CANCEL_BALLOT(18) up, so VOTE is the client's only
    /// ballot message, <c>Messages.h:185-188</c>; <c>Server.Dispatch.cs</c>
    /// tallies it via <c>PendingBallot.TryCastVote</c>, fsmission.cpp:5847-5865).
    /// The VoteYes/VoteNo keys read <c>ClientWorld.ActiveBallot</c> and call this.
    /// </summary>
    public void SendVote(short ballotId, bool agree)
    {
        var msg = new Vote { BallotId = ballotId, Agree = agree };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
        // Native Vote() pops the answered ballot (clintlib.cpp:2510-2522).
        lock (_gate) _world.DismissBallot(ballotId);
    }

    /// <summary>
    /// P626 — send a real <c>FM_CS_DROP_PART(33)</c> for a CARGO part: the
    /// selected cargo slot <paramref name="mount"/> (a negative mount in
    /// -1..-5), encoded as a high-bit byte per native's signed-char Mount. The
    /// server routes a negative mount to the cargo hold and drops it into space
    /// as a treasure (<c>Server.HandleDropPart</c>, native TK_DropCargo
    /// WinTrek.cpp:9698-9730). The EquipmentType field is unused on the cargo
    /// path (the server resolves the slot by mount), so 0 is sent; the client
    /// does not retain per-slot type metadata in flight.
    /// </summary>
    public void SendDropCargo(sbyte mount)
    {
        var msg = new DropPart { Et = 0, Mount = unchecked((byte)mount) };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>
    /// P626 — send a real <c>FM_CS_RELOAD(129)</c> requesting the server top up
    /// the ship's pools / equipped launchers from its cargo hold. The port's
    /// reload key computes one <see cref="ReloadData"/> per occupied cargo slot
    /// (each a negative mount, amount-transfered = NA = consume the whole item);
    /// the server applies each reloadable entry and skips non-reloadable ones
    /// (P626 skip-and-continue), then re-broadcasts sector-wide. Native's
    /// TK_Reload (WinTrek.cpp:9674-9695 → BaseClient::Reload clintlib.cpp:3425+)
    /// sends exactly this message.
    /// </summary>
    public void SendReload(IReadOnlyList<ReloadData> entries)
    {
        var msg = new Reload { Entries = entries };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>
    /// P226 — send a real <c>FM_CS_FIRE_MISSILE(70)</c> launch request, the
    /// player's "fire my mounted missile launcher" wire message. Native declares
    /// FIRE_MISSILE as a bidirectional <c>CS</c> message (<c>Messages.h:547-557</c>)
    /// and the server receives it (<c>FedSrv.CPP:5413</c>): the high-word
    /// missileFireIGC state bit cannot cross the 16-bit wire <c>stateM</c>
    /// (<c>igc.h:829,2457-2465</c>), so the wire request is the real player-launch
    /// path. The server is authoritative and ignores the client-supplied ids /
    /// missile type / target ("ignore the IDs provided by the client",
    /// <c>FedSrv.CPP:5444</c>), so this sends a minimal syntactically-valid request
    /// (one launch-data entry, <c>TargetId = NA</c>) and lets the server resolve
    /// the mounted launcher + its lock against the ship's own CommandTarget. Held
    /// callers (the <c>Game1</c> Left-Control fire key) re-send while the button is
    /// held; the client waits for the server's load-readiness update.
    /// </summary>
    public void SendFireMissile()
    {
        float lockFraction;
        lock (_gate) lockFraction = _world.MissileLock.LockFraction;
        var msg = new FireMissile
        {
            Lock = lockFraction,    // normalized lock; server applies the type MaxLock
            TimeFired = 0u,                          // server-authoritative time
            MissileTypeId = 0,                       // server overrides from mounted launcher
            LauncherId = 0,                          // server overrides
            ClusterId = 0,                           // server overrides
            TargetType = 0, TargetId = -1,           // NA — server uses CommandTarget
            BDud = false,
            Entries =
            [
                new MissileLaunchData(Allegiance.Core.Collision.Vector3.Zero,
                                      Allegiance.Core.Collision.Vector3.Zero,
                                      Allegiance.Core.Collision.Vector3.Zero,
                                      0),
            ],
        };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    private void Send(byte[] packet)
    {
        // P320 — once the session is lost there is no socket to send to (the
        // transport's Send throws "not connected" after its client sender is
        // reaped). Guard every send so no racing control frame from the render
        // thread can crash after a server drop; sends become silent no-ops.
        if (_lost || _transport?.IsConnected != true) return;
        _transport.Send(ConnectionId.Server, packet, guaranteed: true);
    }

    // ── ITransportSite (client half: only OnReceive is raised) ─────────────
    // P320 — OnPeerJoined/OnPeerLeft are NEVER invoked on the client half (the
    // transport only raises them on the host accept path; the client's only
    // remote endpoint is ConnectionId.Server, the session's own connection, not
    // a peer). The roster/scoreboard get peer presence another way entirely —
    // from the wire (SHIP_STATUS, PLAYERINFO, SIDE_ROSTER, SCORE_ROSTER) applied
    // directly into ClientWorld — so these two remain correct no-ops and nothing
    // is silently missing peer events because of it.
    void ITransportSite.OnPeerJoined(ConnectionId id, string name) { }
    void ITransportSite.OnPeerLeft(ConnectionId id) { }

    /// <summary>
    /// P320 — the established session to the server was lost (server crash /
    /// network drop). Latches <see cref="Lost"/> and raises <see cref="SessionLost"/>
    /// exactly once so <c>Game1</c> can stop sending controls and show the
    /// disconnect screen. Raised on the transport receive thread — handlers must
    /// not touch graphics here (see <see cref="SessionLost"/>'s doc).
    /// </summary>
    void ITransportSite.OnSessionLost()
    {
        RaiseLost();
    }

    /// <summary>P671 — latch <see cref="Lost"/> and raise <see cref="SessionLost"/>
    /// exactly once. Shared by the transport's <see cref="ITransportSite.OnSessionLost"/>
    /// (server death / EOF) and the keep-alive thread's native unanswered-ping
    /// rule (<c>CheckServerLag</c>, clintlib.cpp:2063-2068). Safe to call from
    /// either the transport receive thread or the keep-alive thread; the first
    /// caller wins and subsequent calls are no-ops.</summary>
    private void RaiseLost()
    {
        if (_lost) return;
        _lost = true;
        SessionLost?.Invoke();
    }

    void ITransportSite.OnReceive(ConnectionId from, ReadOnlySpan<byte> data)
    {
        if (_lost) return; // a rejected core cannot be followed by a partial join burst
        // P633 — count every inbound byte for the decode-throughput metric
        // (atomic; the read side diffs over a wall window).
        Interlocked.Add(ref _decodedBytes, data.Length);

        byte[][] messages;
        // P636 — unpack straight from the transport's ReadOnlySpan (no whole-packet
        // copy); the byte[] overload is retained for other callers.
        try { messages = FedPacket.Unpack(data); }
        catch (FedCrcException) { return; }   // a bad frame is not ours to adjudicate

        lock (_gate)
        {
            // P671 — any inbound server traffic proves the link is alive: refresh
            // the last-server-message timestamp and clear the unanswered-ping
            // count (native updates m_timeLastServerMessage / resets
            // m_cUnansweredPings on receipt, appmsg.cpp:25-46, clintlib.cpp:405).
            _lastServerMessageTick = Environment.TickCount64;
            _unansweredPings = 0;

            foreach (var m in messages)
            {
                ushort fmid;
                try { fmid = FedMessageHeader.Read(m).Fmid; }
                catch { continue; }

                switch (fmid)
                {
                    case LogonAck.Fmid:
                        _ack = LogonAck.Decode(m);
                        break;
                    case RankInfo.Fmid:
                        // P639 — FM_S_RANK_INFO(13): the server's rank-name table,
                        // sent once right after LOGONACK (Server.Lobby.cs:3344;
                        // native appmsg.cpp:3372-3382). Store it on the world so
                        // the char sheet / scoreboard / team pane can resolve a
                        // roster Rank id to its display name.
                        try { _world.Apply(RankInfo.Decode(m)); }
                        catch { /* ignore a malformed rank sync */ }
                        break;
                    case Ping.Fmid:
                        // P508 — the server's echo of our FM_CS_PING(35): record its
                        // sim-time stamp (timeServer, ms) + the local receive time for
                        // the own-ship server clock. Decode-tolerant (bad frame → ignore).
                        try
                        {
                            var ping = Ping.Decode(m);
                            _pingEchoes.Add((ping.TimeServer, Environment.TickCount64));
                            _world.Apply(ping);
                        }
                        catch { /* ignore a malformed echo */ }
                        break;
                    case TimescaleUpdate.Fmid:
                        // P612 — the server's sim time scale (sim.timeScale). Stored on
                        // the world so the remote-ship dead-reckoners extrapolate in
                        // scaled sim time (no rubber-banding under slow motion).
                        try { _world.TimeScale = TimescaleUpdate.Decode(m).Scale; }
                        catch { /* ignore a malformed update */ }
                        break;
                    case SetCluster.Fmid:
                        _world.Apply(SetCluster.Decode(m));
                        break;
                    case SingleShipUpdate.Fmid:
                        _world.Apply(SingleShipUpdate.Decode(m));
                        break;
                    case HeavyShipsUpdate.Fmid:
                        // P347 — the periodic cross-side visibility sync; every
                        // cluster ship the viewer's side sees (now incl. other
                        // factions since P346) refreshes pose via this message.
                        _world.Apply(HeavyShipsUpdate.Decode(m));
                        break;
                    case LightShipsUpdate.Fmid:
                        _world.Apply(LightShipsUpdate.Decode(m));
                        break;
                    case ShipStatusMessage.Fmid:
                        _world.Apply(ShipStatusMessage.Decode(m));
                        break;
                    case ExportShip.Fmid:
                        // FM_S_EXPORT(26) is the generic object-export message:
                        // ships (OT_ship=0), clusters (OT_cluster=13), stations
                        // (OT_station=1), warps (OT_warp=7) and asteroids
                        // (OT_asteroid=5) all ride this fmid, distinguished by
                        // the 4-byte `objecttype` at message offset 8. Branch on
                        // ObjectType first (same approach as Allegiance.Bot.Bot.cs),
                        // decode the matching codec, feed it to the world.
                        if (m.Length >= 12)
                        {
                            int ot = BitConverter.ToInt32(m.AsSpan(8, 4));
                            if (ot == ExportShip.OtShip)
                                _world.Apply(ExportShip.Decode(m));
                            else if (ot == ExportCluster.OtCluster)
                                _world.Apply(ExportCluster.Decode(m));
                            else if (ot == ExportStation.OtStation)
                                _world.Apply(ExportStation.Decode(m));
                            else if (ot == ExportWarp.OtWarp)
                                _world.Apply(ExportWarp.Decode(m));
                            else if (ot == ExportAsteroid.OtAsteroid)
                                _world.Apply(ExportAsteroid.Decode(m));
                            else if (ot == ExportProbe.OtProbe)
                                _world.Apply(ExportProbe.Decode(m));
                            else if (ot == ExportMine.OtMine)
                                _world.Apply(ExportMine.Decode(m));
                            else if (ot == ExportBuildingEffect.OtBuildingEffect)
                                _world.Apply(ExportBuildingEffect.Decode(m));
                        }
                        break;
                    case AsteroidsUpdate.Fmid:
                        // P423 — the periodic per-asteroid ore/health sync; the
                        // world stores the live ore + structural fractions.
                        _world.Apply(AsteroidsUpdate.Decode(m));
                        break;
                    case StationsUpdate.Fmid:
                        // P423 — the periodic per-station hull/shield sync; the
                        // world stores the live fractions (station health bar).
                        _world.Apply(StationsUpdate.Decode(m));
                        break;
                    case QueuedCommandUpdate.Fmid:
                        // P424 — the owner-only "you have a pending queued order"
                        // sync; the world remembers it for the ACCEPT/REJECT/CLEAR
                        // keys.
                        _world.Apply(QueuedCommandUpdate.Decode(m));
                        break;

                    case PlayerInfo.Fmid:
                        _world.Apply(PlayerInfo.Decode(m));
                        break;
                    case ClusterInfo.Fmid:
                        _world.Apply(ClusterInfo.Decode(m));
                        break;
                    case DockedNotify.Fmid:
                        _world.Apply(DockedNotify.Decode(m));
                        break;
                    case BuyLoadoutAck.Fmid:
                        // P473 — keep the raw ack for the spawn harness to
                        // compare against the requested loadout, alongside the
                        // world apply (which mutates the client's own ship).
                        var bla = BuyLoadoutAck.Decode(m);
                        _buyLoadoutAcks.Add(bla);
                        _world.Apply(bla);
                        break;
                    case Allegiance.Core.Wire.LoadoutChange.Fmid:
                        // P648 — FM_S_LOADOUT_CHANGE(96): a ship's loadout/hull
                        // changed (same-side broadcast; a post-death relaunch
                        // sends this with the new hull). Previously dropped as
                        // unknown; now applied so a same-side peer revives a
                        // relaunched teammate and adopts its new hull.
                        _world.Apply(Allegiance.Core.Wire.LoadoutChange.Decode(m));
                        break;
                    case Reload.Fmid:
                        // P362 — a successful reload relay (P361 auto-reload, or
                        // the manual RELOAD path): the world counts it per ship so
                        // the weapon-status tracker can show the "Reloading ammo..."
                        // success state even though the ammo refill is synchronous
                        // server-side. Consumer of an already-broadcast message —
                        // no wire-protocol change.
                        _world.Apply(Reload.Decode(m));
                        break;
                    case ChangeAlliances.Fmid:
                        _world.Apply(ChangeAlliances.Decode(m));
                        break;
                    case SwapPart.Fmid:
                        _world.Apply(SwapPart.Decode(m));
                        break;
                    case DropPart.Fmid:
                        _world.Apply(DropPart.Decode(m));
                        break;
                    case ChatMessage.Fmid:
                        _world.Apply(ChatMessage.Decode(m));
                        break;
                    case Ballot.Fmid:
                        // P626 — a new in-flight ballot was announced (P187); the
                        // world latches it as the ActiveBallot for the vote keys.
                        _world.Apply(Ballot.Decode(m));
                        break;
                    case CancelBallot.Fmid:
                        _world.Apply(CancelBallot.Decode(m));
                        break;
                    case PositionReqBroadcast.Fmid:
                        _world.Apply(PositionReqBroadcast.Decode(m));
                        break;
                    case DelPositionReq.Fmid:
                        _world.Apply(DelPositionReq.Decode(m));
                        break;
                    case LockLobby.Fmid:
                        _world.Apply(LockLobby.Decode(m));
                        break;
                    case LockSides.Fmid:
                        _world.Apply(LockSides.Decode(m));
                        break;
                    case AutoAccept.Fmid:
                        _world.Apply(AutoAccept.Decode(m));
                        break;
                    case ChatBuoy.Fmid:
                        _world.Apply(ChatBuoy.Decode(m));
                        break;
                    case Payday.Fmid:
                        _world.Apply(Payday.Decode(m));
                        break;
                    case SetMoney.Fmid:
                        // P442 — absolute per-ship money sync (join + per-teammate).
                        _world.Apply(SetMoney.Decode(m));
                        break;
                    case MoneyChange.Fmid:
                        // P442 — per-ship money delta (bucket spend, purchase,
                        // refund, player donation).
                        _world.Apply(MoneyChange.Decode(m));
                        break;
                    case AutoDonateUpdate.Fmid:
                        // P442 — a player's auto-donate relationship (payday
                        // routing) + any one-time gift.
                        _world.Apply(AutoDonateUpdate.Decode(m));
                        break;
                    case KillShip.Fmid:
                        _world.Apply(KillShip.Decode(m));
                        break;
                    case ShipDelete.Fmid:
                        // P370 — the server's explicit "ship no longer in your
                        // view" notice (docked / warped to another sector /
                        // logged off / died). Decoded and forwarded to the world
                        // so it can stop rendering the departed ship's frozen
                        // pose (and resume it if the ship returns to view).
                        _world.Apply(ShipDelete.Decode(m));
                        break;
                    case PlayerReady.Fmid:
                        _world.Apply(PlayerReady.Decode(m));
                        break;
                    case JoinedMission.Fmid:
                        _world.Apply(JoinedMission.Decode(m));
                        if (_world.JoinError is not null)
                        {
                            _lost = true;
                            SessionLost?.Invoke();
                            return;
                        }
                        break;
                    case ObjectSpotted.Fmid:
                        _world.Apply(ObjectSpotted.Decode(m));
                        break;
                    case SetWingId.Fmid:
                        _world.Apply(SetWingId.Decode(m));
                        break;
                    case OrderChange.Fmid:
                        _world.Apply(OrderChange.Decode(m));
                        break;
                    case MissionDef.Fmid:
                        _world.Apply(MissionDef.Decode(m));
                        break;
                    case MissionStageMessage.Fmid:
                        _world.Apply(MissionStageMessage.Decode(m));
                        break;
                    case SetStartTime.Fmid:
                        _world.Apply(SetStartTime.Decode(m));
                        break;
                    case SetBriefingText.Fmid:
                        // P271 — the server's per-player mission briefing (the
                        // starting cluster name). The world keeps the text so the
                        // MonoGame client can render the dismissible briefing popup.
                        _world.Apply(SetBriefingText.Decode(m));
                        break;
                    case GameOver.Fmid:
                        _world.Apply(GameOver.Decode(m));
                        break;
                    case GameOverPlayers.Fmid:
                        // P583 — the server's per-player endgame roster (score +
                        // kills/deaths/ejections/rescues/baseKills/baseCaptures/
                        // artifacts/flags). The world keeps it so the game-over
                        // screen can render the per-player scoreboard.
                        _world.Apply(GameOverPlayers.Decode(m));
                        break;
                    case SideInactive.Fmid:
                        // P455b — the server marks a side inactive (pre-game
                        // roster shrink P202, or a mid-game elimination
                        // P455/P455b). The world records the per-side active
                        // state (and AET when bChangeAET) so the scoreboard /
                        // team list can mark the eliminated side "destroyed"
                        // exactly as native does.
                        _world.Apply(SideInactive.Decode(m));
                        break;
                    case MissileLockUpdate.Fmid:
                        // P230 — owner-only lock snapshot; the world updates its
                        // MissileLock and edge-posts the partial/full/lost tips.
                        _world.Apply(MissileLockUpdate.Decode(m));
                        break;
                    case ProjectileInstance.Fmid:
                        // P231 — a real fired projectile; the world appends a
                        // transient ProjectileEntity to render as a bolt/streak.
                        _world.Apply(ProjectileInstance.Decode(m));
                        break;
                    case FireMissile.Fmid:
                        // P234 — a real missile launch; the world hydrates the
                        // id-keyed MissileEntity (identity + type + spawn pose).
                        _world.Apply(FireMissile.Decode(m));
                        break;
                    case MissileUpdate.Fmid:
                        // P234 — the per-tick ongoing-position stream; the world
                        // upserts each missile's current position/heading.
                        _world.Apply(MissileUpdate.Decode(m));
                        break;
                    case MissileDestroyed.Fmid:
                        // P234 — a real missile expiry/destruction; the world
                        // removes the entity by id.
                        _world.Apply(MissileDestroyed.Decode(m));
                        break;
                    case ProbeDestroyed.Fmid:
                        // P260 — a real probe destruction/lifespan-expiry; the
                        // world removes the probe by id (ends its ambient loop).
                        _world.Apply(ProbeDestroyed.Decode(m));
                        break;
                    case MineDestroyed.Fmid:
                        // P291 — a real mine detonation/lifespan-expiry; the
                        // world removes the mine by id (ends its decal field).
                        _world.Apply(MineDestroyed.Decode(m));
                        break;
                    case MineStrength.Fmid:
                        // P574 — a real mine's current strength fraction after a
                        // ship hit; the world thins the mine's decal field.
                        _world.Apply(MineStrength.Decode(m));
                        break;
                    case BuildingEffectDestroyed.Fmid:
                        // P294 — a real building-effect interruption (its Builder
                        // died mid-build); the world removes the construction by
                        // the target asteroid id (removes its visual).
                        _world.Apply(BuildingEffectDestroyed.Decode(m));
                        break;
                    case CreateChaff.Fmid:
                        // P292 — a real chaff deploy (the sector-wide CREATE_CHAFF
                        // of the FE+CREATE_CHAFF+SPOOFED+END_SPOOFING flow); the
                        // world appends a transient ChaffEntity to render.
                        _world.Apply(CreateChaff.Decode(m));
                        break;
                    case TreasureSpawn.Fmid:
                        // P293 — a real treasure creation (the DROP_PART part-
                        // treasure and treasure-ack re-spawn broadcasts); the world
                        // upserts a TreasureEntity keyed by its ObjectId to render.
                        _world.Apply(TreasureSpawn.Decode(m));
                        break;
                    case DestroyTreasure.Fmid:
                        // P293 — a real treasure pickup/expiry; the world removes
                        // the TreasureEntity by id (ends its gold gem).
                        _world.Apply(DestroyTreasure.Decode(m));
                        break;
                    case ExplosionEvent.Fmid:
                        // P263 — a real blast; the world appends a transient
                        // ExplosionEntity for the renderer to play (size/duration
                        // from the type preset).
                        _world.Apply(ExplosionEvent.Decode(m));
                        break;
                    case ProjectileHullHit.Fmid:
                        // P364 — a weapon shot connected; the world buffers a
                        // transient HullHitEntity (impact pos/normal/radius) for
                        // the renderer to spawn the impact spark.
                        _world.Apply(ProjectileHullHit.Decode(m));
                        break;
                    case VictimDamaged.Fmid:
                        // P623 — the local ship just took damage from a ship; the
                        // world buffers the attribution (attacker id + damage) for
                        // Game1's TK_TargetHostile tracker. Targeted unicast to
                        // the victim's own peer only.
                        _world.Apply(VictimDamaged.Decode(m));
                        break;
                    case Allegiance.Core.Wire.CollisionEvent.Fmid:
                        // P463c — two bodies collided; the world buffers a
                        // CollisionEntity (party ids, sector, impact, jiggles)
                        // for the audio pass to play the collision sound + jiggle.
                        _world.Apply(Allegiance.Core.Wire.CollisionEvent.Decode(m));
                        break;
                    case OwnShipMiningUpdate.Fmid:
                        // P255 — owner-only mining-target snapshot; the world
                        // records which rock the local ship is drilling so the
                        // renderer can orient its hull toward it.
                        _world.Apply(OwnShipMiningUpdate.Decode(m));
                        break;
                    case MissileTrackWarningUpdate.Fmid:
                        // P264 — owner-only "an enemy missile is tracking YOU"
                        // snapshot (armed bool + best lock fraction); the world
                        // records it so Game1 can drive the warning loop.
                        _world.Apply(MissileTrackWarningUpdate.Decode(m));
                        break;
                    case ScoreRosterDelta.Fmid:
                        _world.Apply(ScoreRosterDelta.Decode(m));
                        break;
                    case ScoreRosterUpdate.Fmid:
                        // P265 — periodic owner-only in-mission scoreboard
                        // snapshot (name/side/kills/deaths); the world keeps the
                        // latest so the MonoGame scoreboard overlay can render it.
                        _world.Apply(ScoreRosterUpdate.Decode(m));
                        break;
                    case LeadIndicatorUpdate.Fmid:
                        // P266 — owner-only predictive lead-aim snapshot (lead
                        // point + range/hit-probability); the world records it so
                        // the MonoGame lead reticle can render at the lead point.
                        _world.Apply(LeadIndicatorUpdate.Decode(m));
                        break;
                    case StationDestroyed.Fmid:
                        // P267 — a station was destroyed (whole-mission notice);
                        // the world records the sound event + removes the station.
                        _world.Apply(StationDestroyed.Decode(m));
                        break;
                    case StationCapture.Fmid:
                        // P267 — a station changed sides (whole-mission notice);
                        // the world records the capture sound event + re-sides it.
                        _world.Apply(StationCapture.Decode(m));
                        break;
                    case DevCompleted.Fmid:
                        // P267 — a development completed (side-group notice);
                        // the world records the completion-chime event.
                        _world.Apply(DevCompleted.Decode(m));
                        break;
                    case SideRoster.Fmid:
                        // P281 — the post-connect side-roster snapshot the server
                        // pushes while the peer sits in side-select.
                        _world.Apply(SideRoster.Decode(m));
                        break;
                    case SetMissionOwner.Fmid:
                        // P281 — whole-mission owner broadcast; the world keeps it
                        // so the side-select randomize-teams control is shown only
                        // to the mission owner.
                        _world.Apply(SetMissionOwner.Decode(m));
                        break;
                    case TeamReady.Fmid:
                        // P282 — whole-mission per-side aggregate ready broadcast.
                        _world.Apply(TeamReady.Decode(m));
                        break;
                    case ForceTeamReady.Fmid:
                        // P282 — whole-mission forward of the leader's force-ready
                        // trigger (incl. the sender's own echo).
                        _world.Apply(ForceTeamReady.Decode(m));
                        break;
                    case SetTeamLeader.Fmid:
                        // P366 — whole-mission leadership assignment/transfer; the
                        // world tracks the per-side leader so the MonoGame client
                        // can gate the leader-only bot dock/launch UI on
                        // ClientWorld.IsTeamLeader.
                        _world.Apply(SetTeamLeader.Decode(m));
                        break;
                    case JoinSide.Fmid:
                        // P438 — whole-mission side-join broadcast (the server
                        // sends it on every TryJoinSide, Server.Lobby.cs
                        // BroadcastJoinSide). The world refreshes the affected
                        // ship's SideId (and MySideId when it is our own ship),
                        // the input to the isEnemy friend/enemy classification.
                        // Previously dropped, so a ship that switched side after
                        // its one-shot EXPORT stayed classified by its old side.
                        _world.Apply(JoinSide.Decode(m));
                        break;
                    case ClusterWarningUpdate.Fmid:
                        // P374 — a side's per-sector threat state changed; the
                        // world stores the level + queues the sound/status cues.
                        _world.Apply(ClusterWarningUpdate.Decode(m));
                        break;
                    case ShipWarningUpdate.Fmid:
                        _world.Apply(ShipWarningUpdate.Decode(m));
                        break;
                    case CreateBuckets.Fmid:
                        // P284 — the join-sync CREATE_BUCKETS preface; the world
                        // seeds the joining side's development-tech mask from it.
                        _world.Apply(CreateBuckets.Decode(m));
                        break;
                    case BucketStatus.Fmid:
                        // P487 — the join-sync / post-donation per-bucket funding
                        // snapshot; the world tracks each side bucket's invested
                        // money + build-time ms for the investment pane.
                        _world.Apply(BucketStatus.Decode(m));
                        break;
                    case SideTechChange.Fmid:
                        // P284 — a development completed; the world updates that
                        // side's development-tech mask (the docked-shop gate).
                        _world.Apply(SideTechChange.Decode(m));
                        break;
                    case SideAttributeChange.Fmid:
                        // P446 — a development applied an attribute delta; the
                        // world stores that side's post-Apply multiplier set.
                        _world.Apply(SideAttributeChange.Decode(m));
                        break;
                    case RipcordActivate.Fmid:
                        // P414 — the server acknowledged a ripcord countdown began
                        // (or was re-targeted) toward a valid anchor in the target
                        // sector; the world starts/refreshes the local ripcord HUD
                        // state and any per-ship active flag for our own ship.
                        _world.Apply(RipcordActivate.Decode(m));
                        break;
                    case RipcordDenied.Fmid:
                        // P414 — the server refused our ripcord request (no valid
                        // anchor in the named sector); requester-only. The world
                        // surfaces it as a system chat line (not silently dropped).
                        _world.Apply(RipcordDenied.Decode(m));
                        break;
                    case RipcordAborted.Fmid:
                        // P414 — a ripcord countdown was cancelled (our cancel
                        // request, or a mid-countdown legality failure); the world
                        // clears the HUD state and queues the "ripcord aborted" cue.
                        _world.Apply(RipcordAborted.Decode(m));
                        break;
                    case ShipReset.Fmid:
                        // P508 — the server rejected this ship's reported pose and
                        // sent an authoritative reset + a NEW cookie; the world
                        // stores the pose and flags the own ship for re-seed.
                        _world.Apply(ShipReset.Decode(m));
                        break;
                    case Eject.Fmid:
                        // P540 — a player in this sector ejected into a drifting
                        // Lifepod hull; the world swaps the hull, sets the pod
                        // pose + spin, and latches the own-ship rescue autopilot.
                        _world.Apply(Eject.Decode(m));
                        break;
                    case PlayerRescued.Fmid:
                        // P627 — FM_S_PLAYER_RESCUED(16): the server tells both the
                        // rescuer and the rescuee that a lifepod was picked up
                        // (native appmsg.cpp:309-340; FedSrv.CPP:13056-13065 sends
                        // it to both parties). The world already had a
                        // ClientWorld.Apply(PlayerRescued) feedback producer (P578);
                        // only the receive dispatch case was missing, so the real
                        // client silently dropped the "you were rescued" line.
                        _world.Apply(PlayerRescued.Decode(m));
                        break;
                    case OwnShipKillBonusUpdate.Fmid:
                        // P627 — FM_S_OWN_SHIP_KILL_BONUS_UPDATE(212): the server's
                        // per-tick, owner-only kill-bonus snapshot (Server.Broadcast.cs
                        // P298). The world already stores _killBonusPercent/_killBonusStreak
                        // and Game1.Rendering/HudFrame render the Kill Bonus gauge;
                        // only the receive dispatch case was missing, so the real
                        // client never saw its live kill bonus.
                        _world.Apply(OwnShipKillBonusUpdate.Decode(m));
                        break;
                    case TeleportAck.Fmid:
                        // P638 — FM_S_TELEPORT_ACK(102): accepted/denied teleport
                        // reply (appmsg.cpp:1856). Previously unconsumed, so the
                        // real client never heard the denial or the arrival jump.
                        _world.Apply(TeleportAck.Decode(m));
                        break;
                    case Promote.Fmid:
                        _world.Apply(Promote.Decode(m));
                        break;
                    case HighlightCluster.Fmid:
                        _world.Apply(HighlightCluster.Decode(m));
                        break;
                    case ViewClusterReply.Fmid:
                        _world.Apply(ViewClusterReply.Decode(m));
                        break;
                    case BoardNack.Fmid:
                        // P638 — FM_S_BOARD_NACK(178): boarding refusal
                        // (appmsg.cpp:1899). Previously dropped silently.
                        _world.Apply(BoardNack.Decode(m));
                        break;
                    case MissileSpoofed.Fmid:
                        // P638 — FM_S_MISSILE_SPOOFED(110): a missile's lock was
                        // broken by chaff (appmsg.cpp:1667). Retarget onto the decoy.
                        _world.Apply(MissileSpoofed.Decode(m));
                        break;
                    case EndSpoofing.Fmid:
                        // P638 — FM_S_END_SPOOFING(111): close the spoof window
                        // (appmsg.cpp:1686).
                        _world.Apply(EndSpoofing.Decode(m));
                        break;
                    case EnterLifepod.Fmid:
                        // P638 — FM_S_ENTER_LIFEPOD(166): forced into a Lifepod
                        // (appmsg.cpp:1945).
                        _world.Apply(EnterLifepod.Decode(m));
                        break;
                    case LeaveShip.Fmid:
                        // P638 — FM_S_LEAVE_SHIP(94): a ship left its boarded
                        // parent (appmsg.cpp:1914).
                        _world.Apply(LeaveShip.Decode(m));
                        break;
                    case AsteroidMined.Fmid:
                        // P638 — FM_S_ASTEROID_MINED(198): authoritative He3-mined
                        // ore sync (appmsg.cpp:1733).
                        _world.Apply(AsteroidMined.Decode(m));
                        break;
                    default:
                        // P627 — no case matched this fmid: the real client does not
                        // consume it. Record it (bounded distinct set) so the
                        // dispatch-coverage test can assert a supported S-message is
                        // consumed ("not unknown fmid") and so the receive audit can
                        // enumerate genuinely-unconsumed server→client messages.
                        _unknownFmids.Add(fmid);
                        break;
                }
            }
        }

        // P636 — after applying the whole batch (outside the gate; the receive
        // thread is the world's sole writer) publish the lock-free decision
        // snapshot so the bot decision loop reads latest state without contending
        // for the gate. Throttled internally to ~25 Hz.
        // P636d — the publish is now LAZY (requested by the decision loop; no-op
        // when nothing is pending or the snapshot is off). Stamp the newest-applied
        // update here for the decode-lag metric before attempting the publish.
        _world.MarkApplied();
        _world.PublishDecisionSnapshot();
    }

    /// <summary>P671 — stop the keep-alive thread (on dispose). Sets the stop
    /// flag and wakes the gate so the loop exits within its 6 s wait; joins
    /// briefly. Does not acquire <c>_gate</c>, so it cannot deadlock against the
    /// loop's own locked iteration. Called ONLY from <see cref=\"Dispose\"/>: the
    /// thread is persistent, so a <see cref=\"Shutdown\"/> (used by a reconnect's
    /// old-transport teardown) must NOT stop it.</summary>
    private void StopKeepAlive()
    {
        Volatile.Write(ref _keepAliveStop, 1);
        _keepAliveGate.Set();
        var t = _keepAliveThread;
        _keepAliveThread = null;
        try { t?.Join(1000); } catch { /* best effort */ }
    }

    /// <summary>Close the client socket. The server sees EOF / a dropped peer.</summary>
    public void Shutdown() => _transport?.Shutdown();

    public void Dispose()
    {
        StopKeepAlive();
        Shutdown();
    }
}
