using System.Diagnostics;
using System.Runtime.InteropServices;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
// Disambiguate the LoadoutChange KIND enum (Sim) from the Allegiance.Core.Wire.LoadoutChange
// codec — both are in scope here; the enum is the natural meaning in the
// gameplay broadcast handler, the wire codec is reached as Allegiance.Core.Wire.LoadoutChange.
using LoadoutChange = Allegiance.Core.Sim.LoadoutChange;

namespace Allegiance.Server;

/// <summary>
/// The headless server — the "Main()" this port was pointed at. It boots a
/// static core, instantiates every configured game world as a live
/// <see cref="Mission"/>, drives them at a fixed cadence, and accepts TCP
/// clients through the logon handshake. Roughly the section 1..10 boot path of
/// BOOT_PATH.md minus the dropped pieces (see README "What the server does and
/// does not do yet").
/// </summary>
/// <remarks>
/// <para><b>Clock:</b> two clocks, deliberately kept apart. The <em>simulation</em>
/// clock <c>_simNow</c> advances by exactly <c>period = 1/ups</c> per tick —
/// deterministic and strictly monotonic, so it can never trip
/// <c>Mission.Update</c>'s backwards-time or &gt;=600s guards. The <em>cadence</em>
/// clock is a <see cref="Stopwatch"/> (monotonic, not wall time) that measures
/// the real cost of a pass to decide how long to sleep and whether to log an
/// overrun. Wall-clock jumps therefore cannot corrupt the simulation or the
/// cadence accounting.</para>
///
/// <para><b>Teardown:</b> reverse of boot. Stop the loop, stop the transport
/// (which joins its background accept/read threads — no orphans), then release
/// the one site. Signal-driven via <see cref="RunAsync"/>.</para>
/// </remarks>
public sealed class Server : IDisposable
{
    private ServerConfig _config = null!;
    private ServerHost _host = null!;
    private List<Mission> _missions = null!;
    private RankInfo _rankTable = new();   // P130: boot-loaded standalone rank table (from ranks.txt)
    private double _period;
    private double _simNow;

    private long _loopIterations;
    private long _overruns;
    private long _connectionsJoined;
    private int _liveShipCount;
    private readonly List<short> _bucketCompletions = new();
    private readonly object _statsLock = new();

    // Source of randomness for the eject branch's random fallback heading
    // (Vector::RandomDirection, vector.cpp:37-49). Shared, never seeded by the
    // caller — non-deterministic directions are fine (the native is too).
    private static readonly Random _rand = new();

    // Ships assigned to peers on validated logon (step 3). This map is owned
    // exclusively by the simulation thread (written in ProcessDatagram, which
    // Drain runs on the sim thread, and read in the broadcast step of SimPass),
    // so it needs no locking of its own.
    private readonly Dictionary<ConnectionId, LiveShip> _peerShips = new();
    private int _nextShipId = 8096;

    // Each connected peer's own character name, remembered past the logon
    // handshake. `req.CharacterName` (LogonRequest) is otherwise a local
    // variable consumed inline at the logon handler and then discarded, and
    // neither LiveShip nor Ship carries a name field — yet ExportShip's
    // CharacterName (the field that becomes the client-side ship's name) is
    // required when broadcasting a teammate's ship export. Populated at the
    // same point `_peerShips` is (validated logon), removed on disconnect.
    // Sim-thread only, like its sibling maps. P62.
    private readonly Dictionary<ConnectionId, string> _peerNames = new();

    // Each connected peer's own ready flag (P180), set from the FReady field
    // of the PLAYER_READY(45) messages it sends and consumed by the per-side
    // aggregate-readiness recompute (the port of native's per-CFSPlayer
    // `GetReady()` consulted by CheckForSideAllReady, fsmission.cpp:5513-5522).
    // Stored as a ConnectionId-keyed map — the same storage shape as
    // `_peerNames`/`_peerShips` for per-peer transient state — rather than a
    // field on Ship, because readiness is a lobby-era concept that must outlive
    // the peer's ship placement and is orthogonal to the sim's Ship model.
    // Present only for peers that have sent a PLAYER_READY; absence is treated
    // as not-ready. Sim-thread only, like the sibling maps. P180.
    private readonly Dictionary<ConnectionId, bool> _playerReady = new();

    // P184 — pending side-join requests awaiting leader approval. Keyed by the
    // REQUESTER's ConnectionId (the same per-connection storage shape as
    // `_playerReady`/`_peerShips`), with the requested {ShipId, SideId} in the
    // value. A side with no leader is auto-accepted immediately and never
    // recorded here; only requests to an already-led side land here (native's
    // flat per-mission `m_listJoinReq`, fsmission.cpp:4860-4869, simplified to
    // at most one pending request per ship). A connection's ship lives in
    // exactly one mission, so scoping is implicit — validated at consume time
    // by confirming the target ship is on the acker/leader's mission. Sim-thread
    // only, like the sibling maps.
    private readonly Dictionary<ConnectionId, (short ShipId, short SideId)> _pendingJoinRequests = new();

    // P187 — pending ballots, one list per mission (the port of native
    // CFSMission::m_ballots, fsmission.cpp:1199-1235). Keyed per mission so a
    // BALLOT/VOTE in mission A can never bleed into mission B (the same
    // multi-mission scoping rationale as _pendingJoinRequests). Sim-thread
    // only: created in the ChatMessage handler and the AcceptDraw pass, voted
    // in the VOTE handler, and driven to resolution in SimPass.
    private readonly Dictionary<Mission, List<PendingBallot>> _ballots = new();

    // P187 — the next ballot id to assign (the port of native
    // Ballot::s_ballotIDNext, fsmission.cpp:5878). A single server-wide
    // counter, so ballot ids are globally unique across missions exactly as
    // native's file-static counter makes them.
    private uint _nextBallotId;

    // P188 — the double-seconds sim target at which each mission currently in
    // the STAGE_STARTING countdown crosses into STAGE_STARTED. Keyed per
    // mission (the same multi-mission scoping rationale as _ballots): two
    // concurrent missions can each be counting down independently. Absent for
    // missions not counting down. Written in StartCountdown (sim thread), read
    // + removed in SimPass's AdvanceStartCountdowns.
    private readonly Dictionary<Mission, double> _startCountdownDeadline = new();

    // P188 — the visible pre-launch countdown length, the port of native's
    // c_fMissionBriefingCountdown constant (src/Igc/igc.h:1005). Verified in
    // the live native source this session: `const float c_fMissionBriefingCountdown
    // = 15.0f; // seconds`. Used for BOTH the manual START_GAME trigger (the
    // faithful AdminGame.cpp:286 usage) and the ready-up auto-start now that it
    // routes through the real countdown stage (native's own start always passes
    // through STAGE_STARTING — StartGame does StartCountdown, fsmission.cpp:2231-2239).
    private const float BriefingCountdownSeconds = 15.0f;

    // One-shot tracking for the P62 teammate-ship EXPORT broadcast: set once
    // the first time a (recipient, subject-ship) pair's real ExportShip has
    // been sent. Deliberately a HashSet (no cadence/time component) rather
    // than the time-windowed dictionaries beside it — a second CreateObject
    // for an id the client already owns could misbehave, so a repeat send is
    // wrong here, not merely wasteful. Sim-thread only, like the siblings.
    private readonly HashSet<(ConnectionId Recipient, short ShipId)> _teammateExportSent = new();

    // One-shot tracking for the P63 teammate-ship PLAYERINFO(10) broadcast:
    // set once the first time a (recipient, subject-ship) pair's real
    // PlayerInfo has been sent. Kept separate from `_teammateExportSent` —
    // these are two different messages with two different one-time gates, even
    // though they fire back-to-back for a given pair (EXPORT then PLAYERINFO).
    // Same reasoning as `_teammateExportSent`: a second PLAYERINFO would hit
    // CreateObject again on the same id, so a repeat is wrong, not merely
    // wasteful. Sim-thread only, like the siblings.
    private readonly HashSet<(ConnectionId Recipient, short ShipId)> _teammatePlayerInfoSent = new();

    // Loadout-dirty tracking for the teammate ExportShip(26) PartData[] tail:
    // the snapshot of PartData last sent to a given (recipient, subject-ship)
    // pair, so BroadcastShipStates can detect when a teammate's mounted parts
    // changed since the last EXPORT and resend a fresh one — unlike
    // `_teammateExportSent`/`_teammatePlayerInfoSent` this is NOT one-shot:
    // a teammate can re-buy a loadout any number of times after the initial
    // export. Sim-thread only, like the siblings above.
    private readonly Dictionary<(ConnectionId Recipient, short ShipId), IReadOnlyList<PartData>> _teammateLoadoutSent = new();

    // Last SHIP_STATUS(90) sector reported per peer, so the sector-change hook
    // in BroadcastShipStates resends only when a ship actually crossed a warp.
    // Written/read only on the sim thread (like _peerShips), no locking needed.
    private readonly Dictionary<ConnectionId, short> _lastStatusSector = new();
    // Last SHIP_STATUS(90) send tick per peer, for the periodic refresh in
    // BroadcastShipStates. Sim-thread only.
    private readonly Dictionary<ConnectionId, long> _lastStatusTick = new();

    // Last CLUSTERINFO(200) send tick per peer, for the periodic refresh in
    // BroadcastShipStates -- see ClusterInfoRefreshTicks for why this exists.
    // Sim-thread only.
    private readonly Dictionary<ConnectionId, long> _lastClusterInfoTick = new();

    // Last same-side teammate SHIP_STATUS(90) send tick per (recipient,
    // subject-ship) pair, for the periodic teammate broadcast in
    // BroadcastShipStates -- see TeammateStatusRefreshTicks for the cadence.
    // Sim-thread only, like the siblings above.
    private readonly Dictionary<(ConnectionId, short), long> _lastTeammateStatusTick = new();

    // P200 — last successful full-roster REQPINGDATA(195) request, in sim
    // seconds (_simNow), per requester connection. Implements native's
    // 5-second debounce (messagecore.h:217-223, CFMConnection::PingQuery): a
    // request less than 5s after the previous full send answers with only the
    // unchanged-window marker. Sim-thread only, like the siblings above.
    private readonly Dictionary<ConnectionId, double> _lastPingQuery = new();

    // Mission-wide CLUSTERINFO payload, encoded once (identical for every
    // peer, doesn't change during a mission) rather than re-encoded on every
    // periodic resend.
    private byte[]? _clusterInfoBytes;

    // Optional live-wire capture (--capture path). Null when off, so the only
    // overhead when disabled is a null check — capture must never change
    // behaviour or timing. Serialized across the transport (receive) and sim
    // (send) threads with a dedicated lock so a frame is never interleaved.
    private FileStream? _captureStream;
    private readonly object _captureLock = new();

    // Optional lobby heartbeat client (P176). Non-null only when config.Lobby is set;
    // constructed at the end of Boot, torn down in Shutdown (which fires its best-effort
    // deregister DELETE). Fully opt-out: null when disabled, so the default server makes
    // no HTTP calls at all. Runs entirely off the sim thread and reads only the
    // cross-thread-safe LivePlayerCount snapshot — never Ships/_peerShips.
    private LobbyHeartbeat? _heartbeat;

    /// <summary>
    /// Raised once per notable gameplay moment (dock, undock, mine-start,
    /// mine-sell, payday, warp crossing) on the simulation thread. The payload
    /// is the same human-readable line already written to the console at the
    /// originating site (via <see cref="ServerHost.Notable"/>, which the sim
    /// feeds through <see cref="Allegiance.Core.Sim.IIgcSite.ReportNotable(string)"/>),
    /// never reformatted — so an observer (e.g. the vizfight spectator event
    /// feed) sees exactly what the server log shows. Raised inside the tick on
    /// the sim thread (like the <see cref="ServerHost.ShipKilled"/> hook), so a
    /// subscriber can reach the peer map without a lock. Null-safe: no
    /// subscribers, nothing happens.
    /// </summary>
    public event Action<string>? Notable;

    /// <summary>The position-change threshold (world units) that marks a ship
    /// dirty and worth a SINGLE_SHIP_UPDATE broadcast after a tick.</summary>
    private const float DirtyEpsilon = 1e-3f;

    /// <summary>How far past <see cref="Sector.DockRadius"/> an undocking ship
    /// gets repositioned, so it clears the sphere test
    /// (<see cref="Sector"/>'s dock-collision pass) before the next tick can
    /// re-latch it. Arbitrary but generous relative to <c>DirtyEpsilon</c>-scale
    /// position noise; see the undock handler for the full reasoning.</summary>
    private const float LaunchClearMargin = 50f;

    /// <summary>Outward speed (world units/sec) given to a ship on undock —
    /// the native reference's own default <c>c_fExitStationSpeed</c>
    /// (<c>missionigc.cpp:2689</c>), reused verbatim. Ensures fast initial
    /// separation from the station; <see cref="Sector.LaunchCooldownSeconds"/>
    /// (not this alone) is what actually guarantees no re-dock — see the
    /// undock handler's own P75 comment for why both are needed.</summary>
    private const float ExitStationSpeed = 50f;

    /// <summary>A peer-owned ship: the <see cref="Ship"/>, the
    /// <see cref="Sector"/> it lives in, its last-broadcast position
    /// (the dirtiness watermark for the SINGLE_SHIP_UPDATE broadcast), and
    /// (P193) the peer's in-memory, connection-lifetime-only
    /// <see cref="PlayerScore"/>. A fresh <see cref="PlayerScore"/> is
    /// allocated when the <c>LiveShip</c> is created at logon and discarded
    /// with it on disconnect — the record's lifecycle <em>is</em> the
    /// PlayerScore's lifetime, so non-persistence is structural rather than an
    /// extra bookkeeping rule.</summary>
    internal sealed record LiveShip(Ship Ship, Sector Sector, Vector3 LastBroadcastPos, PlayerScore PlayerScore, PlayerWallet Wallet);

    /// <summary>
    /// Optional seam for tests (and future launch logic): replace the default
    /// ship-on-logon allocation with a caller-supplied one. Receives the core,
    /// the mission to place into and the freshly-allocated ship id; must
    /// <see cref="Mission.PlaceShip"/> the ship itself and return it plus the
    /// sector it was placed in. The default implementation picks a hull from
    /// the core, staggers the spawn by peer count, and places into the first
    /// sector. Internal so the demo behavior stays the default; tests inject a
    /// known hull + spawn so the independent trajectory reference shares them.
    /// </summary>
    internal Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)>? ShipSpawner;

    /// <summary>The game worlds, in config order.</summary>
    public IReadOnlyList<Mission> Missions => _missions ?? throw new InvalidOperationException("Boot() first");

    /// <summary>Total tick-loop passes so far (one pass ticks every mission once).</summary>
    public long LoopIterations { get { lock (_statsLock) return _loopIterations; } }

    /// <summary>How many real-time pass costs exceeded the nominal period.</summary>
    public long Overruns { get { lock (_statsLock) return _overruns; } }

    /// <summary>Peers whose greet was processed (the join was logged) since boot.</summary>
    public long ConnectionsJoined { get { lock (_statsLock) return _connectionsJoined; } }

    /// <summary>
    /// Snapshot of the current live player count (= the size of the sim-thread-owned
    /// <c>_peerShips</c> map, captured once per tick under <c>_statsLock</c>). Exists so
    /// an off-sim-thread consumer (the lobby heartbeat) can read the player count
    /// without enumerating <c>Ships</c>/<c>_peerShips</c> directly — that collection is
    /// mutated every tick on the sim thread and is not safe to touch from another
    /// thread (see <see cref="Ships"/>). P176.
    /// </summary>
    public int LivePlayerCount { get { lock (_statsLock) return _liveShipCount; } }

    /// <summary>Bucket ids that have become fully funded AND fully
    /// time-invested (the P119 completion-detection seam), in completion order,
    /// guarded by <c>_statsLock</c>. Detection-only — nothing is built.</summary>
    public IReadOnlyList<short> BucketCompletions { get { lock (_statsLock) return _bucketCompletions.ToArray(); } }

    /// <summary>Simulation time advanced so far, seconds.</summary>
    public double SimNow { get { lock (_statsLock) return _simNow; } }

    public ServerConfig Config => _config;

    private double Period => _period;

    /// <summary>The bound port once the transport is hosted.</summary>
    public ushort BoundPort => _host?.BoundPort ?? 0;

    /// <summary>The transport-site host (gateway seam). Internal: tests drive a
    /// client transport against <see cref="BoundPort"/>, they do not need the host.</summary>
    internal ServerHost Host => _host;

    /// <summary>Peer-owned live ships (sim-thread owned). Tests resolve which
    /// ship belongs to which bot by <see cref="SingleShipUpdate.OidTarget"/> /
    /// <c>SET_CLUSTER.Ship.ShipId</c> and read its position/controls directly.</summary>
    internal IReadOnlyCollection<LiveShip> Ships => _peerShips.Values;

    // ── boot ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Load the config file, then <see cref="Boot(ServerConfig)"/>.
    /// </summary>
    public void Boot(string configPath, IReadOnlyList<(string Key, string Value)> cliOverrides) =>
        Boot(ServerConfigLoader.Load(configPath, cliOverrides));

    /// <summary>
    /// Boot in the strict order dictated by the reference engine (MISSION_INIT.md
    /// §4): static core first, then sides / sectors, then the tick. Stubbed step
    /// 5 logs that cores.txt/ranks.txt/maps.txt are intentionally not loaded.
    /// </summary>
    public void Boot(ServerConfig config)
    {
        _config = config;
        _period = _config.UpdatesPerSecond > 0 ? 1.0 / _config.UpdatesPerSecond : 0.05;
        _simNow = 0.0;

        ValidatePaths();
        var core = LoadCore();                       // checklist item 4
        StubFileLists();                             // checklist item 5 (stub — cores/maps; ranks now real, see below)
        _rankTable = LoadRankTable();                // P130: real rank list from <artPath>/ranks.txt
        BindHostAndTransport();                      // checklist items 3 & 6
        OpenCaptureIfEnabled();                      // optional --capture (off by default)
        var missions = InstantiateGames(core);       // checklist item 8
        _missions = missions;
        StartLobbyHeartbeat(core);                   // opt-in; no-op when config.Lobby is null
        PrintReady();
    }

    /// <summary>
    /// Construct the lobby heartbeat client when config.Lobby is set (fully opt-in),
    /// storing it for teardown in <see cref="Shutdown"/>. The payload's
    /// MissionName/CoreFile/Version are resolved once here:
    /// <list type="bullet">
    ///   <item><b>MissionName</b> — the FIRST configured game's <c>Name</c> (empty string
    ///        when <c>Games</c> is empty). The Lobby's RegisterRequest carries one singular
    ///        MissionName/CoreFile pair, but this port supports multiple concurrent missions
    ///        per process (<c>ServerConfig.Games</c> is a list) — so only the first is
    ///        reported, and the live PlayerCount aggregates across all missions via
    ///        <see cref="LivePlayerCount"/>. Documented simplification.</item>
    ///   <item><b>CoreFile</b> — the server-wide <c>StaticCore</c> (same for every mission).</item>
    ///   <item><b>Version</b> — the static core's version word.</item>
    /// </list>
    /// There is no MaxPlayers/player-cap concept in ServerConfig today, so the client
    /// sends 0 (the directory treats it as unknown/unbounded); no config field is
    /// invented for it here.
    /// </summary>
    private void StartLobbyHeartbeat(StaticCore core)
    {
        if (_config.Lobby is null)
            return;

        var firstGame = _config.Games.FirstOrDefault();
        _heartbeat = new LobbyHeartbeat(
            _config.Lobby,
            () => LivePlayerCount,
            firstGame?.Name ?? "",
            _config.StaticCore,
            core.Version.ToString(),
            _config.Port);
        Console.WriteLine($"[lobby] heartbeat enabled -> {_config.Lobby.Url}, every {_config.Lobby.HeartbeatIntervalSeconds}s");
    }

    private void ValidatePaths()
    {
        if (string.IsNullOrWhiteSpace(_config.ArtPath) || !Directory.Exists(_config.ArtPath))
            throw new InvalidDataException($"artPath '{_config.ArtPath}' does not exist (a real corpus root is required)");
    }

    private StaticCore LoadCore()
    {
        var corePath = Path.Combine(_config.ArtPath, _config.StaticCore);
        if (!File.Exists(corePath))
            throw new InvalidDataException($"static core '{corePath}' not found under artPath");
        var file = IgcReader.ParseFile(corePath);
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException($"'{corePath}' is not a static core (wrapper {file.Wrapper})");
        var core = StaticCore.Load(file);
        Console.WriteLine($"[boot] static core '{_config.StaticCore}' loaded: version={core.Version}, " +
                          $"{core.Civilizations.Count} civilizations, {core.HullTypes.Count} hulls, " +
                          $"{core.StationTypes.Count} station types");

        // The config indexes civilizations by id; surface the mapping so a bad
        // id in a game definition is obviously wrong in the logs.
        foreach (var civ in core.Civilizations.OrderBy(kv => kv.Key))
            Console.WriteLine($"[boot]   civ {civ.Key,4}  '{civ.Value.Name}'");
        return core;
    }

    /// <summary>BOOT_PATH.md step 5 — cores.txt/maps.txt are stubbed (dropped)
    /// and we say so rather than silently ignoring. ranks.txt is NOT stubbed
    /// anymore (P130 actually loads it — see <see cref="LoadRankTable"/>), so
    /// it is named out of this note; cores.txt/maps.txt remain deferred.</summary>
    private static void StubFileLists() =>
        Console.WriteLine("[boot] (stub) cores.txt / maps.txt NOT loaded — download list out of scope (rank list IS loaded from ranks.txt, see above)");

    /// <summary>
    /// P130 — load the real standalone rank table from
    /// <c>&lt;artPath&gt;/ranks.txt</c>, using the same configured-artwork-root
    /// path-resolution convention the static core, mission maps, and P116's
    /// <c>.cvh</c> corpus use (single configured <c>ArtPath</c>, files flat at
    /// that root — cf. <c>Server.cs:251,344,356</c>). Faithfully ports native's
    /// standalone <c>LoadRankInfo</c> <c>#else</c> branch
    /// (<c>FedSrv.CPP:125-148</c>): count header then <c>&lt;rank&gt;,&lt;name&gt;</c>
    /// lines, every entry <c>civ=-1</c>/<c>requiredRanking=0</c>. Missing file →
    /// empty table (native's <c>g.cRankInfo = 0</c>, FedSrv.CPP:145-146).
    /// </summary>
    private RankInfo LoadRankTable()
    {
        var path = Path.Combine(_config.ArtPath, "ranks.txt");
        var table = RankInfo.FromRanksFile(path);
        Console.WriteLine($"[boot] rank list loaded: {table.Ranks.Length} ranks from '{path}' (standalone: civ=-1, requiredRanking=0)");
        return table;
    }

    private void BindHostAndTransport()
    {
        _host = new ServerHost();
        _host.ShipKilled = BroadcastKillShip;   // wire the site's KillShipEvent to a broadcast
        _host.ShipWarped = OnShipWarped;        // wire the site's HitWarpEvent transit to per-peer routing
        _host.Payday = OnPayday;                // wire the site's PaydayEvent to the per-side wire payout
        _host.BucketCompletedHook = OnBucketCompleted;  // P119: wire the site's BucketCompleted to the completion recorder (detection only)
        _host.Mine = OnMine;                    // wire the site's MineAsteroidEvent to the host logger
        _host.ShipDocked = OnShipDocked;        // wire the site's DockWithStationEvent to the per-peer docked notify
        _host.ProjectileCreated = BroadcastProjectileInstance;   // P88: wire the site's ProjectileCreatedEvent to the wire broadcast
        _host.MissileCreated = BroadcastFireMissile;            // P90: wire the site's MissileCreatedEvent to the wire broadcast
        _host.MissileDestroyed = BroadcastMissileDestroyed;     // P90: wire the site's MissileDestroyedEvent to the wire broadcast
        _host.LoadoutChange = BroadcastLoadoutChange;          // P173: wire the site's LoadoutChangeEvent to the wire broadcast
        _host.MineCreated = BroadcastExportMine;                // P94: wire the site's MineCreatedEvent to the wire broadcast
        _host.MineDestroyed = BroadcastMineDestroyed;           // P94: wire the site's MineDestroyedEvent to the wire broadcast
        _host.ProbeCreated = BroadcastExportProbe;             // P99: wire the site's ProbeCreatedEvent to the wire broadcast
        _host.ProbeDestroyed = BroadcastProbeDestroyed;        // P99: wire the site's ProbeDestroyedEvent to the wire broadcast
        _host.WarpBomb = BroadcastWarpBomb;                   // P101: wire the site's WarpBombEvent to the WHOLE-mission wire broadcast
        _host.AsteroidKilled = BroadcastAsteroidDestroyed;     // P102: wire the site's KillAsteroidEvent to the WHOLE-mission wire broadcast
        _host.StationKilled = BroadcastStationDestroyed;       // P125: wire the site's KillStationEvent to VacateStation + WHOLE-mission STATION_DESTROYED(59)
        _host.StationCaptured = BroadcastStationCapture;       // P126: wire the site's CaptureStationEvent to the WHOLE-mission STATION_CAPTURE(58) broadcast
        _host.VictoryByStationsComputedHook = OnVictoryByStationsComputed;  // P191: wire the site's post-capture/kill recompute to GAME_STATE(155) + P178 GameOver
        _host.Notable = OnNotable;              // wire the site's ReportNotable to the public spectator event
        _host.RequestRipcordReceived = OnNpcRequestRipcord;  // P172: wire the site's IIgcSite.RequestRipcord (NPC) to the ripcord state machine
        var transport = new TcpTransport(_host);      // site set; assign the back-ref next
        _host.Transport = transport;
        transport.Host((ushort)_config.Port);         // reproduce: bind the logon transport
        Console.WriteLine($"[boot] transport listening on 0.0.0.0:{_host.BoundPort} (all interfaces)");
    }

    /// <summary>Open the capture file (truncating any prior capture) when
    /// <c>--capture</c> was given; otherwise leave <see cref="_captureStream"/>
    /// null. No other behaviour depends on capture, so when off there is nothing
    /// to undo and no timing impact.</summary>
    private void OpenCaptureIfEnabled()
    {
        var path = _config.CapturePath;
        if (string.IsNullOrEmpty(path)) return;
        _captureStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        Console.WriteLine($"[boot] capturing packets to '{path}' (pipe through tools/fedmsgdump)");
    }

    /// <summary>Central send path: optional capture then the actual send. Every
    /// packet the server emits flows through here so capture sees all of TX.</summary>
    private void SendPeer(ConnectionId target, byte[] packet)
    {
        lock (_captureLock)
            _captureStream?.Write(CaptureStream.EncodeFrame(sent: true, packet));
        _host.Send(target, packet);   // ServerHost.Send forwards as guaranteed: true
    }

    /// <summary>Capture one received packet before it is processed. Called from
    /// the transport event drain (the sim thread) so it is naturally serialized
    /// with <see cref="SendPeer"/>'s lock-guarded writes.</summary>
    private void CaptureReceived(byte[] packet)
    {
        lock (_captureLock)
            _captureStream?.Write(CaptureStream.EncodeFrame(sent: false, packet));
    }

    private List<Mission> InstantiateGames(StaticCore core)
    {
        var missions = new List<Mission>();

        // P116 — the production landing-bay resolver/cache, keyed by hull
        // ModelName and shared by every mission this server hosts. Its loader
        // locates each carrier's .cvh under the configured art path (the same
        // asset-root convention the static core and maps use; all corpus .cvh
        // files sit flat at that root), parses it once and caches the
        // reconstructed bay geometry, so the per-tick carrier-landing pass
        // (Sector.ResolveCarrierLandings) never reparses frames.
        var landingBays = new HullLandingBayCache(name =>
        {
            var p = Path.Combine(_config.ArtPath, name + ".cvh");
            return File.Exists(p) ? CvhReader.Parse(File.ReadAllText(p)) : null;
        });

        if (_config.Games.Count == 0)
        {
            Console.WriteLine("[boot] (config) no games — booting a bare transport, nothing to tick");
            return missions;
        }

        foreach (var g in _config.Games)
        {
            var mapPath = Path.Combine(_config.ArtPath, g.Map);
            if (!File.Exists(mapPath)) throw new InvalidDataException($"map '{mapPath}' not found under artPath");
            var mapFile = IgcReader.ParseFile(mapPath);
            if (mapFile.Wrapper != IgcWrapper.DataSize)
                throw new InvalidDataException($"'{g.Map}' is not a map (wrapper {mapFile.Wrapper})");
            var map = MissionMap.Load(mapFile);

            if (g.Teams < 1)
                throw new InvalidDataException($"game '{g.Name}': teams must be >= 1, got {g.Teams}");
            if (g.CivIds.Length < g.Teams)
                throw new InvalidDataException($"game '{g.Name}': {g.Teams} teams but {g.CivIds.Length} civ ids");
            if (g.SideNames.Length < g.Teams)
                throw new InvalidDataException($"game '{g.Name}': {g.Teams} teams but {g.SideNames.Length} side names");

            var sides = new SideSpec[g.Teams];
            for (int t = 0; t < g.Teams; t++)
                sides[t] = new SideSpec((short)t, g.SideNames[t], (short)g.CivIds[t]);

            var mission = Mission.Assemble(core, map, sides, _host);  // site bound; core resolved
            // P180 — a real team-lobby/pre-game phase: the mission is born
            // NotStarted (the stage's genuine default) and only transitions to
            // Started when every side with at least one connected player has
            // readied (see the ready-up recompute + auto-start in the
            // PLAYER_READY handler). Previously this line force-set Started at
            // boot, so the pre-game phase was skipped entirely and the mission
            // was always already running by the time anyone joined. Before it
            // was possible to gate on NotStarted, several seams assumed
            // Started-from-boot: the join-sync ENTER_GAME send is now guarded
            // (Server.cs, live.Ship.Mission.Stage == Started) and the control
            // SHIP_UPDATE path ignores input while NotStarted. Payday is
            // scheduled lazily off the sim clock (not boot), so the payday
            // continuity the task asked to verify is unaffected — confirmed:
            // nothing else in the boot/instantiate sequence reads Stage.
            mission.Stage = MissionStage.NotStarted;                // pre-game hold until every side is ready
            // P116 — attach the landing-bay resolver to every sector so the
            // per-tick carrier-landing pass has real .cvh geometry to test.
            foreach (var sec in mission.Sectors) sec.LandingBays = landingBays;
            missions.Add(mission);
            Console.WriteLine($"[boot] game '{g.Name}' ready: map={g.Map}, {mission.Sectors.Count} sectors, " +
                              $"{mission.Sides.Count} sides [{string.Join(", ", mission.Sides.Select(s => s.Name ?? $"side{s.Id}"))}], " +
                              $"{mission.Warps.Count} warps, {mission.Sectors.Sum(s => s.Stations.Count)} stations, " +
                              $"{mission.Sectors.Sum(s => s.Asteroids.Count)} asteroids" +
                              (mission.DanglingWarps.Count > 0 ? $", {mission.DanglingWarps.Count} DANGLING warps" : ""));
        }
        return missions;
    }

    private void PrintReady() =>
        Console.WriteLine($"[boot] server ready: {_missions.Count} mission(s), {_config.UpdatesPerSecond} upd/s " +
                          $"(period {_period * 1000:0.###} ms). Ctrl-C to stop.");

    // ── the tick loop ───────────────────────────────────────────────────────

    /// <summary>
    /// One pass: pump transport events, advance the simulation clock by exactly
    /// one period and tick every mission, then sleep the real remainder to hold
    /// cadence. Measures the real pass cost with a Stopwatch and logs a tick
    /// that overran the period (never silently skips or accumulates — the sim
    /// clock is independent of real wall time).
    /// </summary>
    public void Tick()
    {
        var sw = Stopwatch.StartNew();
        SimPass();
        sw.Stop();
        SyncCadence(sw.Elapsed.TotalSeconds);
    }

    /// <summary>The simulation half of a tick, with no cadence sleep and no
    /// overrun accounting: pump + advance sim + tick every mission. Deterministic
    /// and fast, so tests can drive many ticks without wall-clock dependency.</summary>
    public void TickSim() => SimPass();

    /// <summary>Pump transport, advance sim by exactly one period, tick every mission.</summary>
    private void SimPass()
    {
        _host.Drain(HandleTransportEvent);
        lock (_statsLock) _simNow += _period;
        double now;
        lock (_statsLock) now = _simNow;
        foreach (var m in _missions) m.Update(now);
        // P188 — advance any STAGE_STARTING start-countdowns: a mission whose
        // recorded target start time has passed crosses into STAGE_STARTED and
        // broadcasts ENTER_GAME (the deferred form of P180's auto-start /
        // START_GAME's manual trigger). Runs right after the mission tick so the
        // transition lands the same tick its deadline elapses.
        AdvanceStartCountdowns(now);
        // P187 — resolve pending ballots this tick (the port of native
        // CFSMission::Update's ballot loop, fsmission.cpp:2585-2592): any
        // ballot whose expiration passed or whose votes are all in is tallied
        // and resolved (pass/fail), its type effect applied, then removed.
        // Runs on the sim thread right after the mission tick so a ballot
        // created during this tick's transport drain is resolved this same
        // tick if it already has every vote (e.g. an initiator-only side).
        foreach (var m in _missions) ResolveBallots(m, now);
        lock (_statsLock) { _loopIterations++; _liveShipCount = _peerShips.Count; }
        // P112 — broadcast ripcord aborts raised during this tick's per-tick
        // revalidation pass (Sector.ResolveRipcordCountdowns). The sim cleared
        // each ripper's ripcord state; here the host emits RIPCORD_ABORTED(187)
        // to everyone flying in the ripper's own sector, reusing P111's
        // SendRipcordSectorWide helper (FedSrv.CPP:13427-13434, the native's
        // GetGroupSectorFlying broadcast). Runs on the sim thread, so it may
        // send without locking, exactly like the request handler.
        foreach (var m in _missions)
            foreach (var sector in m.Sectors)
                foreach (var rip in sector.DrainRipcordAborts())
                    if (rip.Sector is { } ripSec)
                        SendRipcordSectorWide(ripSec.Id,
                            new RipcordAborted { ShipidRipcord = rip.Id }.Encode());
        // P113 — perform the ripcord teleports whose countdowns reached zero
        // this tick (Sector.ResolveRipcordCountdowns). Server.PerformRipcordTeleport
        // is the port of UseRipcord (FedSrv.CPP:13440-13500): it relocates the
        // ripper (station/ship anchor), pays the energy cost, broadcasts
        // RIPCORD_ABORTED on energy-insufficiency, and clears the ripcord state.
        // Runs on the sim thread before the broadcast below so the position
        // delta is picked up by the same tick's SINGLE_SHIP_UPDATE.
        foreach (var m in _missions)
            foreach (var sector in m.Sectors)
                foreach (var rip in sector.DrainRipcordTeleports())
                    PerformRipcordTeleport(rip);
        // P115 — perform the rescues this tick's proximity pass found
        // (Sector.ResolveRescueEvents). Server.PerformRescue is the port of
        // RescueShipEvent (FedSrv.CPP:13033-13074): it sends PLAYER_RESCUED(16)
        // to both ships' connections, then places the rescued ship at the
        // nearest friendly station. Runs on the sim thread before the broadcast
        // below so the docked rescuee's state is picked up by the same tick's
        // SINGLE_SHIP_UPDATE / SHIP_STATUS.
        foreach (var m in _missions)
            foreach (var sector in m.Sectors)
                foreach (var (rescuee, rescuer) in sector.DrainRescues())
                    PerformRescue(rescuee, rescuer);
        // P117 — perform the real relaunch this tick's carrier-landing pass
        // found (Sector.ResolveCarrierLandings). Server.PerformRelaunch is the
        // port of LandOnCarrierEvent (FedSrv.CPP:12808-13031): restore the
        // lander's ship-level energy/ammo pools, reposition it at a real
        // launch slot (or the fallback backward offset when the carrier has no
        // launch frames), and broadcast RELAUNCH_SHIP(192) sector-wide. Runs
        // on the sim thread before the broadcast below so the lander's new
        // position/attitude is picked up by the same tick's
        // SINGLE_SHIP_UPDATE.
        foreach (var m in _missions)
            foreach (var sector in m.Sectors)
                foreach (var (carrier, lander) in sector.DrainCarrierLandings())
                    PerformRelaunch(lander, carrier);
        // P123 — perform the cash-treasure pickups this tick's proximity pass
        // found (Sector.ResolveTreasurePickups). Server.PerformTreasurePickup
        // is the port of the c_tcCash branch of HitTreasureEvent followed by
        // KillTreasureEvent (FedSrv.CPP:13589-13616, 12493-12509): credit the
        // picking ship's side (pooled Side.Money), broadcast MONEY_CHANGE
        // side-wide, then destroy the treasure + broadcast DESTROY_TREASURE
        // whole-mission. Runs on the sim thread before the broadcast below.
        foreach (var m in _missions)
            foreach (var sector in m.Sectors)
                foreach (var (ship, treasure) in sector.DrainTreasurePickups())
                    PerformTreasurePickup(ship, treasure);
        // P164 — broadcast the building-effect interruptions this tick's pass
        // found (Sector.ResolveBuildingEffects). Server.BroadcastBuildingEffectDestroyed
        // is the port of KillBuildingEffectEvent's wire half
        // (FedSrv.CPP:12484-12492): the target asteroid's id goes out
        // whole-mission (GetGroupRealSides) to every peer, mirroring the
        // DESTROY_TREASURE broadcast exactly. Runs on the sim thread before the
        // broadcast below.
        foreach (var m in _missions)
            foreach (var sector in m.Sectors)
                foreach (var astId in sector.DrainBuildingEffectInterruptions())
                    BroadcastBuildingEffectDestroyed(astId);
        // Step 3: after the mission tick, broadcast every peer ship that moved.
        // Runs on the sim thread, so "only Mission.Update moves ships" holds —
        // this is purely an observation send of the authoritative state.
        BroadcastShipStates((uint)TickIndex);
        // P133 — periodic per-side STATIONS_UPDATE(57): for each side with
        // seated peers, batch every station that side currently sees (P132's
        // live per-tick sweep) into one message and send it to that side's
        // connections only. Runs every tick, the same per-tick pass shape the
        // port's convention settles for the now-unblocked PROBES_UPDATE(92) /
        // ASTEROIDS_UPDATE(179) siblings.
        BroadcastStationsUpdate();
        // P134 — periodic per-side ASTEROIDS_UPDATE(179): for each side with
        // seated peers, batch every asteroid that side currently sees (P132's
        // live sweep, extended in P134 to score asteroids as targets) into one
        // message and send it to that side's connections only. The per-side
        // sibling of STATIONS_UPDATE above, same every-tick cadence.
        BroadcastAsteroidsUpdate();
        // P135 — periodic per-side PROBES_UPDATE(92): for each side with
        // seated peers, batch every probe that side currently sees (P132's
        // live sweep already scores probes — they are Models with a SideId,
        // unlike the asteroids P134 had to add) into one message and send it to
        // that side's connections only. The third and last of the three
        // periodic per-side syncs P131/P132 unblocked, closing the cluster.
        BroadcastProbesUpdate();
        // P139 — periodic PER-PLAYER LIGHT/HEAVY_SHIPS_UPDATE (175/176): the
        // consumer of P138's per-cluster candidate list. For each seated peer,
        // resolve their real command target (Ship.CommandTarget) — a ship target
        // fills the FIRST heavy slot and drives the bpTargetHull/bpTargetShield
        // scalars — then broadcast every remaining candidate in the peer's
        // cluster (minus their own ship) as heavy records to that peer's OWN
        // connection only. Per-player, not per-side: this family's send target
        // is each individual connection (contrast the STATIONS/ASTEROIDS/PROBES
        // per-side broadcasts above). Runs every tick, the port's established
        // per-tick cadence; see BroadcastShipsUpdate for the heavy/light split
        // decision.
        BroadcastShipsUpdate((uint)TickIndex);
    }

    /// <summary>Current completed-tick count, read under the stats lock.</summary>
    private long TickIndex { get { lock (_statsLock) return _loopIterations; } }

    /// <summary>Period between periodic own-ship SHIP_STATUS refreshes, in
    /// sim ticks. The client's strategy wires its MessageReceiver to the
    /// connection only on attach (<c>StrategyBase.cs:90</c>), which races the
    /// join burst — so the single join-burst status (and, today, the dormant
    /// sector-change resend) can arrive before the strategy is listening.
    /// Refreshting every interval guarantees a status reaches a freshly-attached
    /// strategy within one interval, matching the real FedSrv's frequent
    /// ShipStatus emission (<c>fsship.cpp</c> ShipStatusSpotted/Hidden). Same-
    /// sector repeats are harmless: ScoutExploreMap's handler skips them once
    /// it's moving/sweping (ScoutExploreMap.cs:141-146).</summary>
    private const int StatusRefreshTicks = 40;   // 2 s at 20 upd/s

    /// <summary>Period between periodic same-side TEAMMATE SHIP_STATUS(90)
    /// refreshes, in sim ticks. The reference sends every player a SHIP_STATUS
    /// for every ship in the mission (<c>fsmission.cpp:4303-4323</c>, no
    /// self-filter); this server throttles to one per (recipient,subject) pair
    /// per interval, enough for a commander's
    /// <c>UndockMinersAndStartMining</c> (which drives off its own ~5 s ping
    /// cadence) to learn a teammate is docked within a few seconds. ~5 s at
    /// 20 upd/s.</summary>
    private const int TeammateStatusRefreshTicks = 100;

    /// <summary>For every peer ship that changed position since its last
    /// broadcast, send that peer a <c>FM_S_SINGLE_SHIP_UPDATE(78)</c> carrying the raw
    /// authoritative position/velocity/orientation, timestamped with the tick
    /// index (the uint <c>Time</c> slot). No broadcast when a ship has not
    /// moved (zero thrust ⇒ silence), matching the C++ dirty-state update.
    /// Also re-sends <c>FM_S_SHIP_STATUS(90)</c> for a peer's own ship on a
    /// sector change (immediate) or periodic refresh (see
    /// <see cref="StatusRefreshTicks"/>), and — P58/P62 — periodically re-sends
    /// <c>FM_S_SHIP_STATUS(90)</c> about each same-side peer ship to that peer
    /// (see <see cref="TeammateStatusRefreshTicks"/>), preceded once per
    /// (recipient, subject) pair by a real <c>FM_S_EXPORT(26)</c> so the
    /// teammate ship object exists client-side at all, so a peer's client
    /// builds a full picture of its own side, matching the reference's
    /// per-player status send (<c>fsmission.cpp:4303-4323</c>).</summary>
    private void BroadcastShipStates(uint tickIndex)
    {
        // One snapshot reused for both the own-ship walk and the (P58)
        // same-side teammate walk below, so neither mutates mid-iteration.
        var peers = _peerShips.ToArray();
        foreach (var kv in peers)
        {
            var live = kv.Value;
            var ship = live.Ship;
            if ((ship.Position - live.LastBroadcastPos).Length() >= DirtyEpsilon)
            {
                var msg = new SingleShipUpdate
                {
                    OtTarget = 0,                          // OT_ship
                    OidTarget = ship.Id,
                    Ship = ServerShipUpdate.FromModel(ship.Id, ship, tickIndex),
                    BIsRipcording = false,
                };
                SendPeer(kv.Key, FedPacket.Pack(new[] { msg.Encode() }));
                _peerShips[kv.Key] = live with { LastBroadcastPos = ship.Position };
            }

            // FM_S_SHIP_STATUS(90) sector-change + periodic refresh hook. The
            // strategy's sweep chain is driven by these status frames; resend
            // when the ship crosses a sector (ShipWarped, appmsg.cpp:2723-2729)
            // and periodically so a post-attach strategy receives one (see
            // StatusRefreshTicks). `_lastStatusSector`/`_lastStatusTick` are
            // sim-thread only, same as the rest of this broadcast.
            short sentSector = _lastStatusSector.TryGetValue(kv.Key, out var s0) ? s0 : (short)-2;
            long lastTick = _lastStatusTick.TryGetValue(kv.Key, out var t0) ? t0 : -1;
            bool sectorChanged = live.Sector.Id != sentSector;
            bool refreshDue = lastTick < 0 || (long)tickIndex - lastTick >= StatusRefreshTicks;
            if (sectorChanged || refreshDue)
            {
                SendPeer(kv.Key, FedPacket.Pack(new[] { MakeOwnShipStatus(ship.Id, live).Encode() }));
                Console.WriteLine($"[shipstatus] peer {kv.Key} " +
                    (sectorChanged ? $"sector change {sentSector} -> {live.Sector.Id}" : $"refresh (sector {live.Sector.Id})"));
                _lastStatusSector[kv.Key] = live.Sector.Id;
                _lastStatusTick[kv.Key] = tickIndex;
            }

            // P48: CLUSTERINFO(200) periodic refresh -- the exact same reason
            // SHIP_STATUS needs one, per this method's own doc comment above:
            // StrategyBase.cs only wires up MessageReceiver.OnAppMessage
            // inside Attach(), which itself only runs after ship assignment
            // -- so the join burst's one-time CLUSTERINFO send (this file,
            // the peer-join handler) is *always* processed by the native
            // client's own internal (pre-managed-subscription) message pump
            // and never reaches the managed handler. Confirmed directly:
            // an unconditional raw-fmid diagnostic across a whole 20s trial
            // showed OnAppMessage only ever receiving fmid 78/90 (the two
            // periodically-resent messages) -- never once 200. A longer
            // interval than StatusRefreshTicks is fine (CLUSTERINFO is
            // static mission data, 4204 B vs SHIP_STATUS's ~24 B, and only
            // needs to land once after the managed subscription exists).
            long lastClusterInfoTick = _lastClusterInfoTick.TryGetValue(kv.Key, out var ci0) ? ci0 : -1;
            if (lastClusterInfoTick < 0 || (long)tickIndex - lastClusterInfoTick >= ClusterInfoRefreshTicks)
            {
                if (_clusterInfoBytes is { } bytes)
                {
                    SendPeer(kv.Key, FedPacket.Pack(new[] { bytes }));
                    _lastClusterInfoTick[kv.Key] = tickIndex;
                }
            }

            // P58: same-side TEAMMATE status broadcast. The reference's
            // per-player status send loops EVERY ship in the mission with NO
            // self-filter (CFSMission, fsmission.cpp:4303-4323), so its client
            // builds a full picture of every ship it knows about; this server
            // previously only ever sent each peer ITS OWN ship's status, so a
            // commander client could never learn a docked miner teammate even
            // existed — the exact gap CommanderResearchAndExpand.
            // UndockMinersAndStartMining hits today. Scope: same-side only (a
            // deliberate cut vs the reference's unrestricted visibility),
            // throttled per (recipient,subject) pair. The peer's own ship is
            // skipped (handled above). Reuses MakeOwnShipStatus unchanged —
            // it already accepts an arbitrary (shipId, live) pair, not just the
            // receiver's own.
            if (live.Ship.SideId is { } recipientSide)
            {
                foreach (var tv in peers)
                {
                    if (ReferenceEquals(tv.Value.Ship, ship)) continue;   // own ship, handled above
                    if (tv.Value.Ship.SideId != recipientSide) continue;  // same-side only
                    short teammateId = tv.Value.Ship.Id;

                    // P62: one-shot real ExportShip(26) about this teammate's
                    // ship, sent to the recipient BEFORE any SHIP_STATUS about
                    // the same subject. This is what makes the client's object
                    // model learn a teammate ship exists at all: FM_S_EXPORT is
                    // the object-CREATION message (appmsg.cpp:80-105 →
                    // CreateObject(OT_ship) news a CshipIGC), whereas GetShip()
                    // (SHIP_STATUS's lookup) can only update an object the
                    // client already has. Until this export, every already-
                    // connected peer is only ever told about its OWN ship
                    // (the server's single join-burst export, Server.cs:~812),
                    // so a teammate's P58 SHIP_STATUS had nothing to attach to.
                    //
                    // ShipId here is the REAL teammate id (NOT the -1/NA that
                    // the own-ship export uses as its "this is my own dummy"
                    // trick) so a genuinely new, separate client-side object
                    // is created. baseObjectID=-1 stays unchanged (docked
                    // carriage — which FM_S_DOCKED already handles — is
                    // explicitly out of scope here). Parts carries whatever
                    // the teammate has mounted AT THIS MOMENT (P61/ApplyLoadout's
                    // authoritative Ship.Parts) — see PartData's own doc comment
                    // for why the real client's ReInitialize (shipIGC.cpp:150-160)
                    // can realize a teammate's already-mounted loadout from this
                    // same EXPORT, closing the gap `BuyLoadoutAck` (owner-only,
                    // Server.cs ApplyLoadout) leaves for everyone else.
                    if (!_teammateExportSent.Contains((kv.Key, teammateId)) &&
                        _peerNames.TryGetValue(tv.Key, out var peerName))
                    {
                        var initialParts = SnapshotParts(tv.Value.Ship);
                        var teamExport = new ExportShip
                        {
                            ObjectType = ExportShip.OtShip,
                            CharacterName = peerName,
                            HullId = tv.Value.Ship.HullType.HullId,
                            ShipId = teammateId,                    // real id — creates a new client-side object
                            SideId = recipientSide,                 // the teammate's side (== recipient's, same-side only)
                            PilotType = ExportShip.PilotTypePlayer,
                            Parts = initialParts,
                        };
                        SendPeer(kv.Key, FedPacket.Pack(new[] { teamExport.Encode() }));
                        Console.WriteLine($"[teamexport] peer {kv.Key} <- ship {teammateId} '{peerName}' hull {teamExport.HullId} nParts {initialParts.Count}");
                        _teammateExportSent.Add((kv.Key, teammateId));
                        _teammateLoadoutSent[(kv.Key, teammateId)] = initialParts;
                    }

                    // P63: one-shot real PLAYERINFO(10) about this same teammate's
                    // ship — sent immediately after that pair's EXPORT (P62) and
                    // BEFORE any SHIP_STATUS about the same subject, extending P62's
                    // gating chain rather than building a competing one. Rationale
                    // (native source, not guessed): a ship's "private data" — the
                    // pointer IshipIGCWrapper::GetPlayerInfo()
                    // (AllegianceInterop/igcWrapper.cpp:1980-1982) dereferences — is
                    // set in EXACTLY one place, the FM_S_PLAYERINFO(10) handler's
                    // pPlayerInfo->SetShip(pship) (appmsg.cpp:2279), via
                    // pship->SetPrivateData((DWORD)this) (clintlib.h:311-314). Without
                    // this message a same-side ship gets an EXPORT object (P62) but
                    // never a PlayerInfo, so GetPlayerInfo() returns a wrapper built
                    // around a null/garbage native pointer, and
                    // UndockMinersAndStartMining's p.GetPlayerInfo().LastSeenState()
                    // (CommanderResearchAndExpand.cs:1558) dereferences it: an
                    // AccessViolationException, silently swallowed by OnAppMessage's
                    // blanket SEH handler (NativeClient.cpp:29-45) — no C# exception,
                    // pump keeps working, diagnostic never prints.
                    //
                    // Ordering: placed after the EXPORT (P62) above and before the
                    // STATUS below. When PLAYERINFO's handler runs, CreateObject
                    // (appmsg.cpp:2264) FAILS because the id exists (from EXPORT) and
                    // it takes the else branch — GetShip(shipID) (appmsg.cpp:2276),
                    // resolving the REAL ship EXPORT created, and calling
                    // SetShip on it (:2279). If PLAYERINFO came first it would create
                    // its own separate throwaway ship and attach to THAT — silently
                    // wrong, hence worth getting right, not something a test catches
                    // by accident. Fields match the existing own-player send
                    // (Server.cs:946-954) exactly except ShipId = the teammate's real
                    // id (NOT the -1/NA own-ship trick — that NA is a deliberate,
                    // documented dummy adoption specific to the self-referential own
                    // ship; there is no dummy for a teammate, so a real, separate
                    // object must be created). Money stays 0 (codec docstring: the
                    // native client asserts GetMoney()==0 in AddPlayerToSide,
                    // clintlib.cpp:4109). ISide = the teammate's own SideId (==
                    // recipient's, same-side only); CharacterName from `_peerNames`.
                    if (_teammateExportSent.Contains((kv.Key, teammateId)) &&
                        !_teammatePlayerInfoSent.Contains((kv.Key, teammateId)) &&
                        _peerNames.TryGetValue(tv.Key, out var teammateName))
                    {
                        var teamInfo = new PlayerInfo
                        {
                            CharacterName = teammateName,
                            Money = 0,                       // flat 0 — satisfies AddPlayerToSide's assert
                            ISide = recipientSide,           // the teammate's own side (== recipient's, same-side only)
                            ShipId = teammateId,             // REAL id — the ship the EXPORT created
                            PilotType = PlayerInfo.PilotTypePlayer,
                        };
                        SendPeer(kv.Key, FedPacket.Pack(new[] { teamInfo.Encode() }));
                        Console.WriteLine($"[teamplayerinfo] peer {kv.Key} <- ship {teammateId} '{teammateName}' side {recipientSide}");
                        _teammatePlayerInfoSent.Add((kv.Key, teammateId));
                    }

                    // P58 teammate STATUS broadcast — gated so it only proceeds
                    // once THIS pair's EXPORT has been sent (this tick or an
                    // earlier one): a status for an object the client doesn't
                    // have yet is a no-op at best, so a brand-new pair's status
                    // must never race ahead of its own export within a pass.
                    // If the name lookup above failed (so no export was/will be
                    // sent), the status is skipped too, keeping the two in step.
                    if (_teammateExportSent.Contains((kv.Key, teammateId)))
                    {
                        long lastTeammateTick = _lastTeammateStatusTick.TryGetValue((kv.Key, teammateId), out var tt0) ? tt0 : -1;
                        if (lastTeammateTick < 0 || (long)tickIndex - lastTeammateTick >= TeammateStatusRefreshTicks)
                        {
                            SendPeer(kv.Key, FedPacket.Pack(new[] { MakeOwnShipStatus(teammateId, tv.Value).Encode() }));
                            Console.WriteLine($"[teamshipstatus] peer {kv.Key} <- ship {teammateId} sector {tv.Value.Sector.Id}" +
                                (tv.Value.Ship.StationId is not null ? $" DOCKED@{tv.Value.Ship.StationId}" : " flying"));
                            _lastTeammateStatusTick[(kv.Key, teammateId)] = tickIndex;
                        }

                        // P85: teammate POSITION broadcast — the actual visibility
                        // gap this whole block left open. EXPORT/PLAYERINFO/STATUS
                        // above let a teammate's ship exist and report its coarse
                        // structural state (sector/dock), but SINGLE_SHIP_UPDATE (the
                        // message that actually carries position/velocity/orientation,
                        // same as the own-ship send at the top of this method) was
                        // never sent for anyone but a peer's own ship — confirmed live
                        // (AllegianceClientCore P3): a teammate's ship was visible and
                        // correctly statused, but never observed to move. Reuse the
                        // identical dirty-position gate the own-ship send above uses —
                        // `tv.Value`'s own `LastBroadcastPos`, read from this tick's
                        // frozen `peers` snapshot, which is safe regardless of
                        // iteration order since nothing this tick has mutated `peers`
                        // itself — so a teammate's position is broadcast to a given
                        // recipient exactly as often as it's broadcast to its own
                        // peer, no more.
                        if ((tv.Value.Ship.Position - tv.Value.LastBroadcastPos).Length() >= DirtyEpsilon)
                        {
                            var teamMove = new SingleShipUpdate
                            {
                                OtTarget = 0,                          // OT_ship
                                OidTarget = teammateId,
                                Ship = ServerShipUpdate.FromModel(teammateId, tv.Value.Ship, tickIndex),
                                BIsRipcording = false,
                            };
                            SendPeer(kv.Key, FedPacket.Pack(new[] { teamMove.Encode() }));
                        }

                        // Teammate LOADOUT re-export: the initial EXPORT above only
                        // captures whatever was mounted at export time; a teammate
                        // who buys a new loadout afterward (ApplyLoadout mutating
                        // this same Ship.Parts) would otherwise never be seen again
                        // by an already-exported recipient — BuyLoadoutAck is
                        // owner-only (SendPeer(from) in ApplyLoadout), so re-sending
                        // this same EXPORT is the only wire path a non-owning peer
                        // has to learn the change (see ExportShip's own doc comment:
                        // CshipIGC::ReInitialize handles a re-send for an id the
                        // client already owns exactly like a fresh creation). Gated
                        // on real content change, not a tick cadence — a loadout
                        // changes rarely, so there is no periodic-refresh case to
                        // cover here the way SHIP_STATUS/position need one.
                        var currentParts = SnapshotParts(tv.Value.Ship);
                        if (!PartsEqual(currentParts, _teammateLoadoutSent[(kv.Key, teammateId)]) &&
                            _peerNames.TryGetValue(tv.Key, out var currentPeerName))
                        {
                            var loadoutExport = new ExportShip
                            {
                                ObjectType = ExportShip.OtShip,
                                CharacterName = currentPeerName,
                                HullId = tv.Value.Ship.HullType.HullId,
                                ShipId = teammateId,
                                SideId = recipientSide,
                                PilotType = ExportShip.PilotTypePlayer,
                                Parts = currentParts,
                            };
                            SendPeer(kv.Key, FedPacket.Pack(new[] { loadoutExport.Encode() }));
                            Console.WriteLine($"[teamloadout] peer {kv.Key} <- ship {teammateId} loadout changed, nParts {currentParts.Count}");
                            _teammateLoadoutSent[(kv.Key, teammateId)] = currentParts;
                        }
                    }
                }
            }
        }
    }

    /// <summary>10 s at 20 upd/s -- see the CLUSTERINFO refresh block in
    /// <see cref="BroadcastShipStates"/> for why this exists.</summary>
    private const int ClusterInfoRefreshTicks = 200;

    /// <summary>Build a peer's own-ship SHIP_STATUS(90) from its live record:
    /// docked (carrying the real StationId + State=c_ssDocked) when the ship is
    /// docked, else flying free with StationID/ParentID NA; detected/unknown
    /// fixed per the simplification documented at the join-burst send site.
    /// P108: a <b>boarded</b> ship (ParentShip set) overrides with
    /// State=c_ssTurret/c_ssObserver, ParentID = the carrier's id, and
    /// HullID = NA (riding inside another hull — the native
    /// <c>pss->SetHullID(NA)</c>, FedSrv.CPP:2454). Because every status path
    /// (own-ship periodic refresh, sector change, and the P58 teammate walk)
    /// funnels through this one method, a board/leave transition is reflected
    /// on the wire by the same per-tick mechanism those paths already use — no
    /// new success-ack message is needed (the task's confirmed scoping win).</summary>
    private static ShipStatusMessage MakeOwnShipStatus(short shipId, LiveShip live)
    {
        var ship = live.Ship;
        bool docked = ship.StationId is not null;
        if (ship.ParentShip is { } parent)
        {
            // Boarded — riding inside the carrier's hull (occupancy only; the
            // turret aim/fire binding is a later P109 task).
            return new()
            {
                ShipId = shipId,
                HullId = -1,                                      // NA — riding another hull (pss->SetHullID(NA), FedSrv.CPP:2454)
                SectorId = live.Sector.Id,
                StationId = docked ? ship.StationId!.Value : (short)-1,
                ParentId = parent.Id,                             // the carrier (pss->SetParentID(carrier), FedSrv.CPP:2453)
                State = ship.TurretId is null
                    ? ShipStatusMessage.StateObserver            // c_ssObserver (igc.h:4393)
                    : ShipStatusMessage.StateTurret,             // c_ssTurret   (igc.h:4395)
                Unknown = false,
                Detected = true,
                StateTime = 0,
            };
        }
        return new()
        {
            ShipId = shipId,
            HullId = ship.HullType.HullId,
            SectorId = live.Sector.Id,
            StationId = docked ? ship.StationId!.Value : (short)-1,  // NA when flying free
            ParentId = -1,                          // NA — no docked/turreted children (ShipStatusDocked sets ParentID=NA, fsship.cpp:376)
            State = docked ? ShipStatusMessage.StateDocked : ShipStatusMessage.StateFlying,  // c_ssDocked/c_ssFlying (igc.h:4390,4394)
            Unknown = false,                 // no scanning system yet
            Detected = true,                 // no scanning system yet
            StateTime = 0,                   // sim clock unused (no dTime source here)
        };
    }

    /// <summary>The teammate ExportShip PartData[] tail as it is right now,
    /// mount order — see <see cref="PartData"/>'s own doc comment for why this
    /// drops <c>MountedPart.FractionMounted</c> (a requester-only, BUY_LOADOUT-
    /// specific concern; an already-mounted part observed via EXPORT has none).</summary>
    private static IReadOnlyList<PartData> SnapshotParts(Ship ship) =>
        ship.Parts.Select(p => new PartData(p.PartId, p.Amount, p.MountId)).ToArray();

    /// <summary>Order-and-value equality for two PartData snapshots — used to
    /// gate the teammate loadout re-export on a real content change, not a
    /// reference/identity difference (<see cref="SnapshotParts"/> always
    /// allocates a fresh array).</summary>
    private static bool PartsEqual(IReadOnlyList<PartData> a, IReadOnlyList<PartData> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>Shared by Tick() and (optionally) a caller wishing to hold real
    /// cadence on top of its own N sim advances. Sleeps the remainder of the
    /// period, or logs an overrun if the pass cost already exceeded it.</summary>
    private void SyncCadence(double cost)
    {
        double budget = _period - cost;
        if (budget > 0)
        {
            Thread.Sleep(TimeSpan.FromSeconds(budget));
        }
        else
        {
            lock (_statsLock) _overruns++;
            Console.WriteLine($"[overrun] tick cost {cost * 1000:0.##} ms exceeded period {_period * 1000:0.##} ms " +
                              $"by {(cost - _period) * 1000:0.##} ms (sim continues at full rate)");
        }
    }

    /// <summary>Standard loop body: tick until cancelled.</summary>
    public void Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Tick();
            if (ct.IsCancellationRequested) break; // observe cancellation promptly after sleeping
        }
    }

    /// <summary>Run the loop for a bounded duration; returns total ticks.</summary>
    public long RunFor(TimeSpan duration, CancellationToken external = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(external);
        cts.CancelAfter(duration);
        Loop(cts.Token);
        return LoopIterations;
    }

    // ── event processing (simulation thread) ───────────────────────────────

    private void HandleTransportEvent(ITransportEvent evt)
    {
        switch (evt)
        {
            case PeerJoinedEvt j:
                Console.WriteLine($"[conn] peer {j.Id} '{j.Name}' joined");
                lock (_statsLock) _connectionsJoined++;
                break;
            case PeerLeftEvt l:
                // A disconnected peer can no longer command its ship, so it is
                // released from the broadcast set. P128: its ship is then
                // retired from the sector (SDR_LOGGEDOFF + removed) so it no
                // longer keeps flying under Mission.Update — the same cleanup
                // the graceful LOGOFF path runs through this same ReleasePeer.
                ReleasePeer(l.Id);
                Console.WriteLine($"[conn] peer {l.Id} left");
                break;
            case SessionLostEvt:
                Console.WriteLine("[conn] session lost");
                break;
            case ReceivedEvt r:
                CaptureReceived(r.Data);
                ProcessDatagram(r.From, r.Data);
                break;
        }
    }

    /// <summary>The connection-cleanup path for a departed peer — reused by
    /// both an abrupt transport drop (<see cref="PeerLeftEvt"/>) and a
    /// graceful <c>FM_CS_LOGOFF</c> (P127). Releasing the peer means dropping
    /// it from the broadcast/command sets (<c>_peerShips</c>,
    /// <c>_peerNames</c>) <b>and</b> (P128) retiring the ship it left behind:
    /// broadcasting <c>SHIP_DELETE(20, SDR_LOGGEDOFF)</c> to its sector peers
    /// and removing it from the sector so it no longer flies under
    /// <c>Mission.Update</c> — the port's analogue of native's
    /// <c>CFSShip::~CFSShip</c> → <c>AnnounceExit(NULL, SDR_LOGGEDOFF)</c> +
    /// <c>m_pShip->Terminate()</c> (fsship.cpp:58-68). Callers that fired
    /// because of LOGOFF also hold the original frame for its guaranteed echo
    /// before releasing. Idempotent: a second call (LOGOFF, then the
    /// transport's own OnPeerLeft) is a no-op.</summary>
    private void ReleasePeer(ConnectionId id)
    {
        if (!_peerShips.Remove(id, out var live))
        {
            // Idempotent second release (LOGOFF followed by the transport's own
            // OnPeerLeft) — the peer is already gone and its ship already retired.
            _peerNames.Remove(id);
            _playerReady.Remove(id);
            return;
        }

        RetireShipOnDisconnect(live);

        // P178 — win-by-elimination (native fsmission.cpp:5371-5427 →
        // CheckForVictoryByInactiveSides). A peer just left (its ship retired),
        // which is the one moment a side can go empty of players; re-evaluate
        // whether this mission has reached its endgame state. Only on this real
        // departure path — never on the idempotent early-return above.
        if (live.Ship.Mission is { } mission && live.Ship.SideId is { })
        {
            CheckForVictoryByInactiveSides(mission);

            // P183 — the RemovePlayerFromSide port's leader-succession
            // (fsmission.cpp:848-1099): if the departing ship was its side's
            // leader, promote a successor (or clear the role), broadcasting
            // SET_TEAM_LEADER / SET_MISSION_OWNER as needed. Runs after the
            // victory check and before the name/ready cleanup, mirroring native
            // RemovePlayerFromMission calling RemovePlayerFromSide first.
            HandleLeaderSuccession(live.Ship);

            // P184 — a departing player's pending side-join request(s), if any,
            // must be cleared and each broadcast as DELPOSITIONREQ(DPR_Rejected)
            // (native RemovePlayerFromMission → RemoveJoinRequest(pfsPlayer, NULL),
            // fsmission.cpp:460-525 / 4938-4960). Usually at most one per ship in
            // this port.
            foreach (var pend in _pendingJoinRequests
                         .Where(kv => kv.Value.ShipId == live.Ship.Id).ToList())
            {
                _pendingJoinRequests.Remove(pend.Key);
                BroadcastDelPositionReq(mission, pend.Value.ShipId, pend.Value.SideId, DelPositionReqReason.Rejected);
            }

            // P183 — broadcast QUIT_MISSION(124) to the whole mission. This
            // disconnect path previously sent nothing telling remaining peers
            // "ship X quit the mission" — a real, live gap now closed.
            BroadcastQuitMission(mission, live.Ship.Id, QuitSideReason.LinkDead);
        }

        _peerNames.Remove(id);                     // P62: release the remembered name with the peer
        _playerReady.Remove(id);                   // P180: release the peer's lobby-ready flag with it
    }

    /// <summary>
    /// P128 — retire the zombie ship a departing peer leaves in the sector.
    /// The port's analogue of native's <c>CFSShip::~CFSShip</c>
    /// (fsship.cpp:58-68), which on destruction announces
    /// <c>SHIP_DELETE(NULL, SDR_LOGGEDOFF)</c> (<i>"client is gone, so waste
    /// their ship and everything about it"</i>, Messages.h:191) and then
    /// <c>m_pShip->Terminate()</c>. Three steps:
    /// <list type="number">
    /// <item><b>Detach any boarded children</b> — native Terminate "blows away
    /// the children" by setting each <c>SetParentShip(NULL)</c>
    /// (shipIGC.cpp:243-250), which ejects the child as an independent ship,
    /// not destroys it (shipIGC.cpp:3671-3700). This port's existing detach
    /// helper is <see cref="PerformLeave"/> — valid here because a boarded
    /// carrier is always docked (board gates on carrier <c>StationId != null</c>,
    /// Server.cs:2660-2662), so its docked gate passes; the same reuse the
    /// teleport bNewHull boot makes (Server.cs:3981-3982).</item>
    /// <item><b>Broadcast <c>SHIP_DELETE(20, SDR_LOGGEDOFF)</c></b> to the peers
    /// who can see the ship — the same sector-scoped audience the kill path
    /// uses for SDR_KILLED (P103; native instead addresses the whole mission
    /// group, fsship.cpp:213-219 — noted, matching this port's SHIP_DELETE
    /// convention).</item>
    /// <item><b>Remove the ship from its sector</b> via the same
    /// <c>_models.Remove</c> the death contract uses (Sector.cs:1082/1499), so
    /// it stops flying under Mission.Update — reusing the mechanism, not a
    /// second one.</item>
    /// </list>
    /// Runs on the server thread (called from <see cref="ReleasePeer"/>), same
    /// as the kill broadcasts. A ship with no sector (e.g. a peer that never
    /// got a validated logon) has nothing to retire.</summary>
    private void RetireShipOnDisconnect(LiveShip live)
    {
        var ship = live.Ship;
        if (ship.Sector is not { } sector)
        {
            Console.WriteLine($"[logoff] ship {ship.Id} has no sector — nothing to retire");
            return;
        }

        // 1. Native "blow away the children": detach every boarded child
        //    (shipIGC.cpp:243-250) via the port's existing detach helper.
        foreach (var kid in ship.ChildShips.ToArray())
            PerformLeave(kid, ship);

        // 2. Broadcast SHIP_DELETE(20, SDR_LOGGEDOFF) to the sector audience.
        //    The departing peer is already out of _peerShips, so it is not a
        //    target — consistent with the kill path's audience construction.
        var targets = _peerShips
            .Where(kv => kv.Value.Ship.Sector == ship.Sector)
            .Select(kv => kv.Key)
            .ToArray();
        if (targets.Length > 0)
        {
            var delete = ShipDelete.ForLoggedOffShip(ship.Id);
            byte[] packet = FedPacket.Pack(new[] { delete.Encode() });
            foreach (var t in targets)
                SendPeer(t, packet);
        }
        Console.WriteLine($"[logoff] ship {ship.Id} — SHIP_DELETE(20) SDR_LOGGEDOFF -> {targets.Length} peer(s)");

        // 3. Remove the ship from its sector (the same _models.Remove the
        //    death contract uses, Sector.cs:1082/1499) so Mission.Update stops
        //    flying it. Nulling Sector mirrors native Terminate's
        //    GetCluster() → NULL.
        sector.Remove(ship);
        ship.Sector = null;
    }

    /// <summary>
    /// P178 — win-by-elimination check, the port of native
    /// <c>CFSMission::CheckForVictoryByInactiveSides</c>
    /// (<c>FedSrv/fsmission.cpp:3767-3788</c>), invoked from the
    /// &quot;a side just went empty of players&quot; site (<c>fsmission.cpp:5371-5427</c>).
    /// This port's hook for that site is <see cref="ReleasePeer"/>: a peer's
    /// departure is the one moment a side can lose its last player.
    /// <para>
    /// For every side of <paramref name="mission"/> that still has at least one
    /// seated player — a player-piloted ship, which is exactly what
    /// <c>_peerShips</c> holds (NPCs never appear there; there is no ghost
    /// concept, mirroring <c>HasPlayers</c>' <c>ISPLAYER &amp;&amp; !IsGhost</c>,
    /// <c>fsmission.cpp:1422-1431</c>) — count the qualifying sides. More than
    /// one still having players: no decision yet, game continues. Exactly one:
    /// that side wins. Zero: a draw (&quot;All sides were destroyed&quot;).
    /// Scoped to THIS mission only (a server hosts multiple missions, cf.
    /// <c>ServerConfig.Games</c> being a list): only peers whose ship belongs
    /// to <paramref name="mission"/> are counted, and only those peers are
    /// recipients of the <c>GAME_OVER</c>(62) broadcast.</para>
    /// <para>
    /// Never runs when <paramref name="mission"/> is already
    /// <see cref="MissionStage.Over"/> (idempotency: an ended mission must not
    /// re-broadcast on a later disconnect). On a decision it flips
    /// <c>Stage</c> to <see cref="MissionStage.Over"/> — which immediately arms
    /// the scattered <c>mission.Stage != MissionStage.Started</c> gates (e.g.
    /// <see cref="HandleSuicide"/> refuses when not <c>Started</c>) — then builds
    /// and broadcasts <see cref="Wire.GameOver"/> to every peer still connected
    /// to that mission. The reason string and winner follow native
    /// (<c>fsmission.cpp:5400-5418</c>); the allied-name-list branch
    /// (<c>:5398-5418</c>) is N/A (no alliance concept in this port).</para>
    /// </summary>
    private void CheckForVictoryByInactiveSides(Mission mission)
    {
        if (mission.Stage == MissionStage.Over)
            return;                                  // idempotent: already decided

        // Post-departure tally (the departing peer is already out of _peerShips).
        var playerSideIds = new HashSet<short>();
        foreach (var kv in _peerShips)
            if (ReferenceEquals(kv.Value.Ship.Mission, mission) && kv.Value.Ship.SideId is { } sid)
                playerSideIds.Add(sid);

        if (playerSideIds.Count > 1)
            return;                                  // ≥2 sides alive — game continues

        // Exactly one side with players → it wins; zero → draw.
        short winnerId = GameOver.NA;
        string reason = "All sides were destroyed";
        if (playerSideIds.Count == 1)
        {
            winnerId = playerSideIds.First();
            var winnerSide = mission.Sides.FirstOrDefault(s => s.Id == winnerId);
            string winnerName = winnerSide?.Name ?? $"side{winnerId}";
            reason = $"{winnerName} won by outlasting all other sides.";
        }

        // Decide the outcome first so the Stage==Over gates are live for any
        // subsequent gameplay message before we broadcast the end.
        DeclareGameOver(mission, winnerId, reason);
    }

    /// <summary>
    /// Shared end-of-mission declaration, used by <b>both</b> the P178
    /// win-by-elimination path (<see cref="CheckForVictoryByInactiveSides"/>) and
    /// the P191 conquest/territory wins
    /// (<see cref="OnVictoryByStationsComputed"/>): flip the mission to
    /// <see cref="MissionStage.Over"/> (arming the scattered
    /// <c>mission.Stage != MissionStage.Started</c> gates, e.g.
    /// <see cref="HandleSuicide"/>), then build and broadcast
    /// <see cref="Wire.GameOver"/> to every peer still connected to that mission
    /// (whole-mission audience scoped to THIS mission — only peers whose ship
    /// belongs to it are recipients, cf. <c>ServerConfig.Games</c> being a list).
    /// Reusing this single path is the explicit P191 instruction — the
    /// conquest/territory wins must not invent a second game-over mechanism.
    /// </summary>
    private void DeclareGameOver(Mission mission, short winnerId, string reason)
    {
        mission.Stage = MissionStage.Over;

        var gameOver = GameOver.ForMission(mission, winnerId, reason);
        byte[] packet = FedPacket.Pack(new[] { gameOver.Encode() });

        // Whole-mission audience, scoped to THIS mission: every peer whose ship
        // belongs to it (the mission-scoped sibling of the whole-mission idiom
        // BroadcastAsteroidDestroyed/BroadcastStationDestroyed use, Server.cs).
        var targets = _peerShips
            .Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission))
            .Select(kv => kv.Key)
            .ToArray();

        Console.WriteLine($"[gameover] mission #{_missions.IndexOf(mission)} -> Stage Over, " +
                          $"iSideWinner={(winnerId == GameOver.NA ? "NA(draw)" : winnerId.ToString())}, " +
                          $"{targets.Length} peer(s)");
        foreach (var t in targets)
            SendPeer(t, packet);

        // P194 — GAME_OVER_PLAYERS(63): per-player endgame broadcast, an
        // addition fired at the exact same shared hook, not a replacement. It
        // rides the same whole-mission audience and reports P193's PlayerScore
        // for every currently-connected peer on a REAL side.
        BroadcastGameOverPlayers(mission);
    }

    /// <summary>
    /// P194 — broadcast <see cref="Wire.GameOverPlayers"/>(63) to every peer
    /// still connected to <paramref name="mission"/> (the same whole-mission
    /// audience <see cref="DeclareGameOver"/> already addresses GAME_OVER(62)
    /// to), carrying each connected player's name/side/P193 <c>PlayerScore</c>.
    /// This is the port of native's per-player endgame loop
    /// (<c>FedSrv/fsmission.cpp:3094-3141</c>, inside
    /// <c>CFSMission::QueueGameoverMessage</c>, itself invoked from
    /// <c>ProcessGameOver</c>).
    /// <para><b>Player filters (native's real ones, ported).</b> Native includes
    /// only players who have actually played (<c>GetTimePlayed() &gt; 0</c>) and
    /// are on a real side (<c>GetSide()-&gt;GetObjectID() != SIDE_TEAMLOBBY</c>,
    /// fsmission.cpp:3103). This port has no time-played counter at all (nothing
    /// in P193's <c>PlayerScore</c> tracks play time), so the "has actually
    /// played" filter collapses to "is a seated connection" — the task's allowed
    /// fallback when no equivalent concept exists. The real-side filter is
    /// ported: a peer whose ship has <c>SideId == null</c> is the closest
    /// analogue of native's team-lobby membership (this port has no explicit
    /// lobby side), and is excluded exactly as native excludes
    /// <c>SIDE_TEAMLOBBY</c>.</para>
    /// <para><b>Simplification, stated plainly:</b> native's second loop over
    /// <c>m_oldPlayers</c> (recently-disconnected-but-still-tracked players,
    /// fsmission.cpp:3115-3128) is out of scope — this port has no
    /// "keep a disconnected player's stats around briefly" mechanism, so a
    /// disconnected player simply isn't in the list here, whereas native would
    /// still include them if they'd played and disconnected recently.</para>
    /// <para><b>Chunking:</b> native splits the list into ≤50-player messages
    /// (fsmission.cpp:3131-3141); this port sends one message (judged unneeded
    /// at this port's scale — missions seat a handful of peers).</para>
    /// </summary>
    private void BroadcastGameOverPlayers(Mission mission)
    {
        var entries = new List<GameOverPlayerEntry>();
        foreach (var kv in _peerShips)
        {
            var live = kv.Value;
            if (!ReferenceEquals(live.Ship.Mission, mission))
                continue;                              // scope to THIS mission
            if (live.Ship.SideId is not { } sid)
                continue;                              // no real side → the team-lobby analogue
            string name = _peerNames.TryGetValue(kv.Key, out var n) ? n : $"Ship {live.Ship.Id}";
            entries.Add(new GameOverPlayerEntry(
                CharacterName: name,
                SideId: sid,
                Rank: live.PlayerScore.Rank,
                Score: live.PlayerScore.Score));
        }

        var players = new GameOverPlayers { Entries = entries.ToArray() };
        byte[] packet = FedPacket.Pack(new[] { players.Encode() });

        var targets = _peerShips
            .Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission))
            .Select(kv => kv.Key)
            .ToArray();

        Console.WriteLine($"[gameover-players] mission #{_missions.IndexOf(mission)} -> {entries.Count} player(s) to {targets.Length} peer(s)");
        foreach (var t in targets)
            SendPeer(t, packet);
    }

    /// <summary>Unpack, validate and answer one inbound packet. At the headless
    /// milestone the only message a client is expected to send is the logon
    /// request; anything else is reported and ignored (no crash).</summary>
    private void ProcessDatagram(ConnectionId from, byte[] data)
    {
        byte[][] messages;
        try { messages = FedPacket.Unpack(data); }
        catch (FedCrcException e) { Console.WriteLine($"[wire] crc/ framing failure from {from}: {e.Message}"); return; }

        foreach (var m in messages)
        {
            ushort fmid;
            try { fmid = FedMessageHeader.Read(m).Fmid; }
            catch (Exception e) { Console.WriteLine($"[wire] bad message header from {from}: {e.Message}"); continue; }

            if (fmid == Ping.Fmid)   // FM_CS_PING (Messages.h:280-284)
            {
                // P188 — a client keepalive/latency probe: stamp the server
                // time into the message and echo it back to the SAME connection
                // it came from (not broadcast), exactly native FM_CS_PING
                // (FedSrv.CPP:4897-4911: stamp timeServer then ForwardMessage to
                // the sender's own connection). No ship/mission/side context is
                // needed at all, so this is handled at the very top of the
                // dispatch, before any mission lookup — it works regardless of
                // game state (even with no mission assigned).
                try
                {
                    var ping = Ping.Decode(m);
                    ping.TimeServer = (uint)(_simNow * 1000.0);
                    SendPeer(from, FedPacket.Pack(new[] { ping.Encode() }));
                    Console.WriteLine($"[ping] {from} <- echo (client {ping.TimeClient}, server {ping.TimeServer})");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad PING from {from}: {e.Message}");
                }
                continue;
            }

            if (fmid == LogonRequest.Fmid)
            {
                LogonRequest req;
                try { req = LogonRequest.Decode(m); }
                catch (Exception e) { Console.WriteLine($"[wire] bad LOGONREQ from {from}: {e.Message}"); continue; }

                var validated = req.VersionMatches;
                // Step 3: a validated logon gets a ship. Allocate an id, create
                // and place the Ship via the spawner (default or injected), then
                // answer the handshake with that ship id.
                short shipId = (short)(_nextShipId++);
                LiveShip live = default!;
                if (validated && _missions.Count > 0)
                {
                    live = AssignShip(req, shipId);
                    Console.WriteLine($"[launch] peer {from} '{req.CharacterName}' got ship {shipId} in sector '{live.Sector.Name}' " +
                                      $"({live.Ship.Position})");
                }

                var ack = new LogonAck
                {
                    CharNameOrFailureReason = validated ? $"Welcome {req.CharacterName}" : "wrong client version",
                    TimeServer = (uint)req.Time + 1,
                    ShipID = validated ? shipId : (short)0,
                    FValidated = validated,
                    FRetry = !validated,
                    Cookie = (byte)(req.DwCookie & 0xFF),
                };
                SendPeer(from, FedPacket.Pack(new[] { ack.Encode() }));
                Console.WriteLine($"[logon] {from} '{req.CharacterName}' validated={validated} -> " +
                                  $"'{ack.CharNameOrFailureReason}' (server time {ack.TimeServer}, ship {ack.ShipID})");

                // Step 3: the real assignment message — SET_CLUSTER(7) tells the
                // client its sector and full ServerSingleShipUpdate.
                if (validated)
                {
                    _peerShips[from] = live with { LastBroadcastPos = live.Ship.Position };
                    _peerNames[from] = req.CharacterName ?? "";   // P62: remember past logon for teammate EXPORT name

                    // FM_S_MISSIONDEF(38) then FM_S_JOINED_MISSION(95) FIRST,
                    // before any object-creation/launch message. Reason (traced
                    // in P29): the JOINED_MISSION handler
                    // (appmsg.cpp:2150-2215) is the client's static-core load
                    // step — it calls ResetStaticData
                    // (appmsg.cpp:2162-2163 → clintlib.cpp:4193-4224), which
                    // runs FlushGameState() (destroys every ship, incl. any
                    // EXPORTed one, and the current mission) then
                    // CreateMission() + Initialize() (fresh no-hull dummy) and
                    // LoadIGCStaticCore() — the import that hydrates the
                    // mission's hull/part/station registries. So any EXPORT/
                    // SET_CLUSTER/BUY_LOADOUT_ACK sent BEFORE these two would
                    // be destroyed/replaced by the join; they MUST come after.
                    //
                    // The two fields that matter are driven by the server's own
                    // config + loaded core (no hardcoded values):
                    //   - misparms.szIGCStaticFile is the extensionless BASE
                    //     name of the static core ("PCore001"), because the
                    //     client's LoadIGCStaticCore calls UTL::getFile(name,
                    //     ".igc", ...) which APPENDS ".igc" (missionigc.cpp:
                    //     2410, _Utility/Utility.cpp:105-123). So the wire value
                    //     must NOT include the extension or the client looks for
                    //     "PCore001.igc.igc" (empirically caught by the P29
                    //     probe: filename=[C:\Source\git\Artwork\PCore001.igc.igc],
                    //     fopen=null → version gate bails, client disconnects).
                    //     Passing the extensionless base is what makes the
                    //     lstrcmp at appmsg.cpp:2162 trigger ResetStaticData at
                    //     all, pointing it at the real file. server.json:3 keeps
                    //     the full name "PCore001.igc" for OUR own LoadCore;
                    //     only the wire value is stripped.
                    //   - misparms.verIGCcore must equal the version word at
                    //     offset 0 of that .igc file (= the StaticCore the
                    //     server loaded), else ResetStaticData bails
                    //     (clintlib.cpp:4212-4218) and the client disconnects.
                    // Everything else in the large MissionParams/MISSIONDEF is
                    // simulation policy this headless server doesn't implement;
                    // grep shows the join flow reads only those two (plus the
                    // dwCookie identity / shipID tagging).
                    SendPeer(from, FedPacket.Pack(new[]
                    {
                        new MissionDef
                        {
                            SzIGCStaticFile = Path.GetFileNameWithoutExtension(_config.StaticCore),
                            VerIgcCore = _missions[0].Core.Version,
                            DwCookie = 0,
                            ISideMissionOwner = 0,
                            Stage = 0,
                        }.Encode(),
                        new JoinedMission
                        {
                            ShipId = shipId,
                            DTime = 0,
                            Cookie = 0,
                            DwCookie = 0,
                        }.Encode(),
                    }));
                    Console.WriteLine($"[join] peer {from} '{req.CharacterName}' -> MISSIONDEF core '{Path.GetFileNameWithoutExtension(_config.StaticCore)}' v{_missions[0].Core.Version} + JOINED_MISSION ship {shipId} (static core load + hull-table hydration)");

                    // FM_S_EXPORT(26)/OT_cluster(13) cluster exports used to
                    // live here (right after JOINED_MISSION). Moved to AFTER
                    // PLAYERINFO/SET_CLUSTER's side-assignment dance below —
                    // see the comment down there for why: BaseClient::
                    // OnQuitSide (clintlib.cpp:3781-3783,
                    // m_pCoreIGC->ResetMission()) unconditionally nukes every
                    // registered cluster, and it fires from PLAYERINFO's own
                    // AddPlayerToSide call. Exporting clusters here just meant
                    // they got wiped moments later.

                    // FM_S_MISSIONDEF(38) again, AFTER the join reset, BEFORE
                    // the ship EXPORT. Reason (P31 empirical, probe-verified;
                    // reordered for the P33 native-side fix — cluster EXPORTs
                    // no longer need to precede this send: P31's original
                    // "civs not resolved immediately after JOINED_MISSION"
                    // finding does not reproduce empirically against the
                    // current static-core load path, confirmed directly via
                    // native P33dbg instrumentation across multiple live
                    // trials sending MISSIONDEF(38) right after JOINED_MISSION
                    // with no exports in between — UpdateSides ran clean every
                    // time): JOINED_MISSION's ResetStaticData
                    // (appmsg.cpp:2162-2163 → clintlib.cpp:4193-4224) DESTROYS
                    // the client's m_pCoreIGC and rebuilds a FRESH mission from
                    // the static core — which has NO sides (the static core is
                    // civ/hull records, not side records). The client's
                    // MISSIONDEF ELSE-branch (m_pMissionInfo != NULL,
                    // appmsg.cpp:2419-2425) runs UpdateSides here and creates
                    // the real sides AND, via CmissionIGC::AddSide
                    // (missionigc.cpp:3612), the SIDE_TEAMLOBBY side.
                    //
                    // Moved to BEFORE the ship EXPORT (was after, through P32):
                    // the ship EXPORT's ShipId is now NA (see the comment on
                    // that send below), which makes the native client's
                    // CshipIGC::Initialize dedup path match the client's own
                    // dummy ship and run CshipIGC::ReInitialize on it
                    // (shipIGC.cpp:65-163) directly — including its hull
                    // branch, which unconditionally reads
                    // pmission->GetSide(dataShip->sideID)->GetColor()
                    // (shipIGC.cpp:124-149) with NO null check. With sides
                    // sent after the ship EXPORT (the old order), that side
                    // lookup returns NULL and this is a genuine, deterministic
                    // null-pointer access violation — confirmed directly via
                    // native P33dbg instrumentation: the ship EXPORT's
                    // CreateObject call never returns (crashes inside
                    // ReInitialize, silently swallowed by
                    // NativeClient::OnAppMessage's blanket SEH handler), which
                    // is also *why* the cluster list was observed wiped by the
                    // time SET_CLUSTER ran (heap corruption from the crash).
                    // Sending sides first removes the NULL entirely.
                    var mdSides = _missions[0].Sides;
                    SendPeer(from, FedPacket.Pack(new[]
                    {
                        new MissionDef
                        {
                            SzIGCStaticFile = Path.GetFileNameWithoutExtension(_config.StaticCore),
                            VerIgcCore = _missions[0].Core.Version,
                            DwCookie = 0,
                            NTems = (short)mdSides.Count,
                            RgCivId = mdSides.Select(s => s.Civilization.CivilizationId).ToArray(),
                        }.Encode(),
                    }));
                    Console.WriteLine($"[sides] peer {from} '{req.CharacterName}' -> MISSIONDEF(38) nTeams {mdSides.Count} civIds [{string.Join(", ", mdSides.Select(s => s.Civilization.CivilizationId))}] (create SIDE_TEAMLOBBY + real sides via UpdateSides)");

                    // FM_S_EXPORT(26) first: this is the real object-creation
                    // message the C++ client's dispatcher handles
                    // (appmsg.cpp:80-105 → m_pCoreIGC->CreateObject → for
                    // OT_ship=0 news a CshipIGC and AddShip-s it, so
                    // GetShip()/GetBaseHullType()/GetPosition() read from a
                    // non-null native backing — the T14 fix). SET_CLUSTER
                    // below updates that object; without this preceding
                    // creation message the client's ship wrapper has a null
                    // native backing and GetPosition() throws NRE. object
                    // creation must precede any per-tick state update
                    // referencing the id, hence the order.
                    //
                    // Own ship only — deliberately NOT broadcast to observers;
                    // mutual multi-bot visibility is out of scope here (same
                    // judgment call P19/P22 made for SingleShipUpdate
                    // broadening).
                    //
                    // nParts=0 simplification (unchanged from P26): the
                    // DataShipIGC is written with no mounted-part state and no
                    // PartData[] tail. Real per-hardpoint export would need
                    // Ship's mounted parts serialized in the exact PartData
                    // (partID/amount/mountID, 5 bytes each) format — real but
                    // separable; this task sends the object-creation header
                    // only. FM_S_EXPORT replaces the inert NEW_SHIP_INSTANCE
                    // (31) send from P26: that message has no case on the real
                    // client's dispatch switch (appmsg.cpp) so it did nothing,
                    // though its codec+tests remain as a faithful-but-inert
                    // port (fmid 31 is a real protocol message; our client
                    // just never dispatches it).
                    if (live.Ship.SideId is { } sideId)
                    {
                        // ShipId = NA (-1), NOT the real assigned shipId. This is the
                        // fix for the movement blocker traced directly (native-side,
                        // read/log evidence, not guessed): the client's OWN placeholder
                        // ship (BaseClient::CreateDummyShip, clintlib.cpp:3859-3882) is
                        // created with DataShipIGC.shipID = NA, and BaseClient::m_ship
                        // is never reassigned anywhere for the rest of the session
                        // (only two "m_ship =" sites exist in clintlib at all: NULL-init
                        // and this dummy creation). CshipIGC::Initialize
                        // (shipIGC.cpp:165-180) reconciles an incoming EXPORT with an
                        // EXISTING object purely by dataShip->shipID equality
                        // (pMission->GetShip(dataShip->shipID)). Sending the real,
                        // globally-unique shipId here (matching JoinedMission/LogonAck)
                        // makes that lookup MISS the dummy and creates an ORPHANED
                        // second ship object instead — hulled, but never the object
                        // m_ship points to. m_ship stays permanently hull-less, so when
                        // SET_CLUSTER runs, appmsg.cpp:1028
                        // (pship->GetThingSite()->SetTrailColor(...)) dereferences a
                        // NULL ThingSite (hull-less ships never LoadCVH,
                        // modelIGC.cpp:105-119) — a real access violation, confirmed
                        // directly in the P31d native debug log (pkt#7 = SET_CLUSTER,
                        // immediately followed by "NativeClient::ExceptionFilter()
                        // caught AV as expected"). NativeClient::OnAppMessage's blanket
                        // per-message SEH handler (NativeClient.cpp:29-45) silently
                        // swallows it, aborting SET_CLUSTER BEFORE
                        // pship->SetCluster(pcluster) (appmsg.cpp:1087) ever runs — so
                        // flyingF() (clintlib.h:1198) never becomes true and
                        // BaseClient::SendUpdate (clintlib.cpp:1815-1819) never fires
                        // FM_C_SHIP_UPDATE. The real, original FedSrv.CPP:907 creates
                        // its own internal placeholder ship the identical way
                        // (dataship.shipID = NA), corroborating NA as the intended wire
                        // value for a player's own ship at creation time.
                        //
                        // PLAYERINFO's ShipId (PlayerInfo.cs) deliberately keeps the
                        // REAL id, unchanged — its native handler (appmsg.cpp:2249)
                        // hardcodes DataShipIGC.hullID = NA on every call, so if it also
                        // matched the dummy by id it would immediately strip the hull
                        // this EXPORT just gave it via ReInitialize's FreeThingSite()
                        // path (shipIGC.cpp:135-138). Leaving it mismatched makes
                        // PLAYERINFO create a separate, harmless, unused bookkeeping
                        // ship instead (same as happens today), while ReInitialize
                        // (shipIGC.cpp:120-125) still applies this EXPORT's own SideId
                        // to m_ship directly, so side comes along for free.
                        const short OwnShipExportId = -1; // NA
                        var exportMsg = new ExportShip
                        {
                            ObjectType = ExportShip.OtShip,
                            CharacterName = req.CharacterName ?? "",
                            HullId = live.Ship.HullType.HullId,
                            ShipId = OwnShipExportId,
                            SideId = sideId,
                            PilotType = ExportShip.PilotTypePlayer,
                        };
                        SendPeer(from, FedPacket.Pack(new[] { exportMsg.Encode() }));
                        Console.WriteLine($"[create] peer {from} '{req.CharacterName}' -> EXPORT hull {exportMsg.HullId} ship NA(real={shipId}) side {sideId}");
                    }
                    else
                    {
                        // A ship with no SideId by SET_CLUSTER time is a real
                        // finding (every live-spawn path seats a side; see
                        // SpawnDefaultShip). Log it and skip the creation
                        // message for this peer — never fabricate side 0
                        // (guessing would put the bot on a fake side).
                        // SET_CLUSTER still goes out as before; without EXPORT
                        // the client wrapper stays null-backed, matching the
                        // T14 state, but that is the honest consequence of an
                        // unseated side.
                        Console.WriteLine($"[create] {from} '{req.CharacterName}': ship {shipId} has NO SideId — skipping EXPORT (finding, not guessed)");
                    }

                    // FM_S_PLAYERINFO(10): the missing side-carrier (T17 →
                    // P31). The client's SET_CLUSTER handler
                    // (appmsg.cpp:1034) reads
                    // GetShip()->GetSide()->GetColor(); T17 proved
                    // m_ship->GetSide() was NULL because the direct-connect
                    // client's ship never got a side assigned (JOINED_MISSION
                    // does NOT call AddPlayerToSide). This message's handler
                    // (appmsg.cpp:2217-2294) is the only dispatched path that
                    // reaches AddPlayerToSide (clintlib.cpp:4101 →
                    // pship->SetSide(pside) at :4121) — the call that
                    // actually sets m_ship's side. Placement reasoning:
                    //   • AFTER JOINED_MISSION (its ResetStaticData →
                    //     FlushGameState at appmsg.cpp:2162-2163 /
                    //     clintlib.cpp:4198 would destroy any ship/side the
                    //     PLAYERINFO handler built).
                    //   • AFTER the ship EXPORT (P33 update: sides are now
                    //     sent BEFORE the ship EXPORT, see above — the ship
                    //     EXPORT's ShipId is NA, matching the client's own
                    //     dummy, so it's the dummy that ends up with the real
                    //     hull+side via ReInitialize). PLAYERINFO's own
                    //     ShipId stays the real, non-NA id on purpose, so its
                    //     CreateObject(OT_ship,&ds,...) at :2264 does NOT match
                    //     the dummy (the real id was never assigned to it) and
                    //     takes the "create a new, separate, mostly-empty
                    //     bookkeeping ship" path instead of the GetShip(shipID)
                    //     else-branch (:2276) — harmless, since nothing reads
                    //     that object; m_ship (the dummy) is already correctly
                    //     hulled+sided by the ship EXPORT alone by this point.
                    //   • BEFORE SET_CLUSTER, whose handler is what crashed
                    //     for want of a Side.
                    // One message with the real iSide is expected to be enough
                    // (code-derived, not yet live-confirmed: AddPlayerToMission,
                    // called at :2284 before AddPlayerToSide at :2288, runs
                    // SetSideID(SIDE_TEAMLOBBY) at clintlib.cpp:4085, so the
                    // AddPlayerToSide assert at :4107 sees SideID()==-2 and
                    // should pass — no two-message dance needed). Whether that
                    // assert holds on the live client is P31b's empirical job.
                    SendPeer(from, FedPacket.Pack(new[]
                    {
                        new PlayerInfo
                        {
                            CharacterName = req.CharacterName ?? "",
                            Money = 0,
                            ISide = live.Ship.SideId ?? -1,
                            ShipId = shipId,
                            PilotType = PlayerInfo.PilotTypePlayer,
                        }.Encode(),
                    }));
                    Console.WriteLine($"[playerinfo] peer {from} '{req.CharacterName}' -> PLAYERINFO(10) ship {shipId} iSide {live.Ship.SideId?.ToString() ?? "(none)"} (side assignment for the direct-connect ship)");

                    // FM_S_SET_MONEY(115): tell the joining/syncing player the
                    // side's money (P118). Native fires this inside
                    // CFSMission::SendMissionInfo (fsmission.cpp:4270-4299),
                    // part of AddPlayerToSide's "tell the joiner about everyone
                    // else" sync, one SET_MONEY per teammate with that
                    // teammate's per-player wallet. This port tracks ONE pooled
                    // number per side (Model/Side.cs:52 — no per-player
                    // wallets), so the honest port is a single message carrying
                    // the side's shared treasury, addressed with the joining
                    // player's own shipID (the only real ship in this model).
                    // Placed right after PLAYERINFO, the point where the player
                    // is definitively seated on a side.
                    if (live.Ship.SideId is { } setMoneySideId)
                    {
                        var smSide = _missions[0].Sides.FirstOrDefault(s => s.Id == setMoneySideId);
                        if (smSide is not null)
                        {
                            SendPeer(from, FedPacket.Pack(new[]
                            {
                                new SetMoney { Money = (int)smSide.Money, ShipId = shipId }.Encode(),
                            }));
                            Console.WriteLine($"[setmoney] peer {from} '{req.CharacterName}' -> SET_MONEY(115) money={smSide.Money} shipID={shipId}");

                            // FM_S_CREATE_BUCKETS(191) + one BUCKET_STATUS(65)
                            // per bucket (P119): the bucket-catalog preface of the
                            // same SendMissionInfo join-sync burst that carries
                            // SET_MONEY — native builds CREATE_BUCKETS at
                            // fsmission.cpp:4255-4258 (from
                            // GetDevelopmentTechs()/GetInitialTechs()), then
                            // sends one BUCKET_STATUS per bucket :4260-4273. The
                            // two TechTreeBitMask fields carry the side's real
                            // development techs (P189: the port now has live
                            // development-tech state on Side.DevelopmentTechs,
                            // seeded from the civ base techs) — both the
                            // developments mask and the initial mask (the
                            // latter from the native UpdateInitialTechs()
                            // equivalence, sideigc.h:219-222, which sets initial
                            // == development techs). Sent to the joining peer,
                            // exactly like SET_MONEY.
                            var joinSideTechs = smSide.DevelopmentTechs;
                            SendPeer(from, FedPacket.Pack(new[]
                            {
                                new CreateBuckets
                                {
                                    TtbmDevelopments = joinSideTechs.ToBytes(),
                                    TtbmInitial = joinSideTechs.ToBytes(),
                                }.Encode(),
                            }));
                            Console.WriteLine($"[createbuckets] peer {from} '{req.CharacterName}' -> CREATE_BUCKETS(191) ttbmDevelopments={joinSideTechs.PopCount} bits ttbmInitial={joinSideTechs.PopCount} bits");
                            foreach (var bkt in smSide.Buckets)
                            {
                                SendPeer(from, FedPacket.Pack(new[]
                                {
                                    new BucketStatus
                                    {
                                        TimeTotal = (uint)bkt.InvestedMilliseconds,
                                        SideId = smSide.Id,
                                        MoneyTotal = (int)bkt.InvestedMoney,
                                        IBucket = bkt.Id,
                                    }.Encode(),
                                }));
                            }
                            if (smSide.Buckets.Count > 0)
                                Console.WriteLine($"[bucketstatus] peer {from} '{req.CharacterName}' -> BUCKET_STATUS(65) x{smSide.Buckets.Count} for side {smSide.Id}");

                            // FM_S_SET_BRIEFING_TEXT(9): tell the joining player
                            // their starting location so the client can display the
                            // mission briefing (P121). Native fires this in the same
                            // join-sync burst (fsmission.cpp:722-732), one message to
                            // the joining player's OWN connection, branching on a
                            // mission-authored custom story text (m_strStoryText →
                            // fGenerateCivBriefing=false) vs. just the starting
                            // cluster's name (fGenerateCivBriefing=true). This port
                            // ALWAYS takes the generate-civ-briefing branch: it
                            // decodes no mission-level custom story-text field
                            // (confirmed — no StoryText/Story in MissionDef), so
                            // there is no m_strStoryText to send verbatim (see
                            // SetBriefingText doc). Text = the cluster name of the
                            // joining side's first-found friendly station, reusing
                            // P105's FindFriendlyStation stand-in for the native
                            // nearest-station/GetBase lookup (Server.cs:4750-4759).
                            // Sent to the joining peer only, exactly like SET_MONEY.
                            var friendlyStation = FindFriendlyStation(live.Ship);
                            if (friendlyStation is { } briefStation)
                            {
                                var clusterName = _missions[0].Sectors
                                    .FirstOrDefault(s => s.Id == briefStation.ClusterId)?.Name;
                                if (clusterName is not null)
                                {
                                    SendPeer(from, FedPacket.Pack(new[]
                                    {
                                        new SetBriefingText { Text = clusterName, FGenerateCivBriefing = true }.Encode(),
                                    }));
                                    Console.WriteLine($"[briefing] peer {from} '{req.CharacterName}' -> SET_BRIEFING_TEXT(9) generateCivBriefing=true text='{clusterName}' (friendly station {briefStation.StationId} side {briefStation.SideId})");
                                }
                            }
                            else
                            {
                                // No friendly station anywhere (P105 helper's
                                // documented null return, Server.cs:4704) — there is
                                // no meaningful starting location to name. Native's
                                // GetBase(pside) always resolves a mission base, so
                                // this does not arise in a real mission; here it is
                                // logged and the message skipped rather than
                                // fabricating text.
                                Console.WriteLine($"[briefing] peer {from} '{req.CharacterName}' -> no friendly station for side {setMoneySideId} — SET_BRIEFING_TEXT skipped");
                            }
                        }
                    }

                    // FM_S_RANK_INFO(13): tell the joining player the server's
                    // rank-name table (P130). Native builds this right after
                    // LOGONACK on the valid-connect path, from the boot-time
                    // LoadRankInfo table (FedSrv.CPP:998-1001; LoadRankInfo at
                    // :103-150, run unconditionally at :9347). Static boot-time
                    // config, not live game state, and not side-scoped — so it
                    // sits outside the side block, sent ONCE to the joining
                    // peer's own connection only, exactly like TREASURE_SETS.
                    // The table is the real ranks.txt payload (loaded at boot,
                    // every entry civ=-1 / requiredRanking=0 — standalone).
                    SendPeer(from, FedPacket.Pack(new[] { _rankTable.Encode() }));
                    Console.WriteLine($"[rankinfo] peer {from} '{req.CharacterName}' -> RANK_INFO(13) {_rankTable.Ranks.Length} rank(s) (civ=-1, requiredRanking=0, standalone)");

                    // FM_S_TREASURE_SETS(89): tell the joining player the
                    // mission's treasure-set catalog (P123). Native defines the
                    // message (Messages.h:624-628) as an FM_VAR_ITEM array of
                    // TreasureSet with the chance blob to ignore; modern FedSrv
                    // never emits it (legacy; Wopr/WinTrek parse it as NYI — see
                    // the P123 report's spawn-trigger finding), but it is the
                    // join-time catalogue sync, so this port sends the real
                    // already-decoded StaticCore.TreasureSets catalog at
                    // join-sync, per the message's own FM_VAR_ITEM shape with
                    // the per-entry chance blob omitted. Mission-wide (not
                    // side-specific), so it sits outside the side block, sent to
                    // the joining peer only, exactly like SET_MONEY.
                    var treasureSets = _missions[0].Core.TreasureSets;
                    if (treasureSets.Count > 0)
                    {
                        var tsMsg = TreasureSets.FromCatalog(treasureSets.Values);
                        SendPeer(from, FedPacket.Pack(new[] { tsMsg.Encode() }));
                        Console.WriteLine($"[treasuresets] peer {from} '{req.CharacterName}' -> TREASURE_SETS(89) {tsMsg.Sets.Length} set(s)");
                    }
                    else
                    {
                        Console.WriteLine($"[treasuresets] peer {from} '{req.CharacterName}' -> core has no treasure sets — TREASURE_SETS(89) skipped");
                    }

                    // FM_S_CLUSTERINFO(200): the map-overview message the
                    // reference server sends in SendMissionInfo IMMEDIATELY
                    // BEFORE the cluster EXPORT loop (fsmission.cpp:4124-4166,
                    // then :4170). It is what feeds the native Wopr client's
                    // GameInfo.Clusters (StrategyBase.cs:172:
                    // GameInfo.Clusters = message.Clusters) and therefore
                    // GetUnexploredClusters() (GameInfo.cs:69-92), which is
                    // currently always empty because this message was never
                    // sent — so ScoutExploreMap's smart branches (nearest-
                    // unexplored-warp/cluster, ScoutExploreMap.cs:577-650)
                    // never engaged and every bot fell back to the random-warp
                    // pick (:653-662). Placed here, right after PLAYERINFO and
                    // right before the cluster EXPORT burst, reproduces the
                    // reference order exactly (CLUSTERINFO before EXPORT) with
                    // no impact on the P33/P35 export-ordering invariants that
                    // this block documents: CLUSTERINFO is pure passive data
                    // the client stores (GameInfo.Clusters), it touches neither
                    // the mission side-membership path (PLAYERINFO) nor the
                    // m_pCoreIGC cluster pool that SET_CLUSTER resolves — those
                    // still come from the EXPORT burst below, so ORDERING of
                    // THAT burst is untouched. Built from the same Sectors the
                    // EXPORT burst iterates; dangling warp destinations are
                    // skipped and logged, never fabricated (ResolvedWarp.cs:28-32).
                    // P48: cached once (mission-wide, never changes) rather than
                    // re-encoded per peer/per resend -- also reused by
                    // BroadcastShipStates' periodic refresh below.
                    var clusterInfo = ClusterInfo.FromSectors(_missions[0].Sectors);
                    _clusterInfoBytes ??= clusterInfo.Encode();
                    SendPeer(from, FedPacket.Pack(new[] { _clusterInfoBytes }));
                    _lastClusterInfoTick[from] = TickIndex;
                    Console.WriteLine($"[clusterinfo] peer {from} '{req.CharacterName}' -> CLUSTERINFO(200) {clusterInfo.Clusters.Count} cluster(s), {clusterInfo.Warps.Count} warp(s)");

                    // FM_S_EXPORT(26)/OT_cluster(13): convey the MAP's sectors
                    // to the client. Placement (P33, moved here from right
                    // after JOINED_MISSION): the PLAYERINFO send just above
                    // moves the local player off SIDE_TEAMLOBBY onto its real
                    // side. On the native client that runs
                    // BaseClient::AddPlayerToSide (clintlib.cpp:4101), which —
                    // because this is the player's OWN PlayerInfo
                    // (pPlayerInfo == m_pPlayerInfo) — calls
                    // BaseClient::OnQuitSide() first (clintlib.cpp:4112-4115,
                    // matching the "leaving SIDE_TEAMLOBBY" half of the
                    // transition even on the very first join). OnQuitSide
                    // unconditionally nukes every registered cluster
                    // (clintlib.cpp:3781-3783: SetViewCluster(NULL);
                    // m_pCoreIGC->ResetMission(), which iterates
                    // GetClusters() and Terminate()s every one). Exporting
                    // clusters BEFORE PLAYERINFO (the original placement)
                    // meant they were all silently wiped moments later —
                    // confirmed directly via native P33dbg instrumentation
                    // (a live cluster-count probe read 13 right after the
                    // exports, then 0 by the time SET_CLUSTER ran) — which is
                    // what made m_pCoreIGC->GetCluster(sectorID)
                    // (appmsg.cpp:1006) return NULL and crash the very next
                    // line (pcluster->GetLastUpdate(), appmsg.cpp:1041, no
                    // null check) before pship->SetCluster(pcluster) could
                    // ever run — the actual, final blocker behind flyingF()
                    // (clintlib.h:1198) never becoming true. Sending the
                    // exports after PLAYERINFO's OnQuitSide instead means
                    // there is nothing left to wipe. We export ALL sectors
                    // (the reference approach, a trivial loop) rather than
                    // just the peer's starting sector, so the client can
                    // resolve any cluster id a later message references and
                    // there is no "which one" selection logic to get wrong;
                    // ~92 bytes × N sectors is negligible locally. Must land
                    // BEFORE SET_CLUSTER, below.
                    var clusterExports = _missions[0].Sectors
                        .Select(s => ExportCluster.FromSector(s).Encode())
                        .ToArray();
                    SendPeer(from, FedPacket.Pack(clusterExports));
                    Console.WriteLine($"[export] peer {from} '{req.CharacterName}' -> {clusterExports.Length} cluster(s): " +
                                      string.Join(", ", _missions[0].Sectors.Select(s => $"{s.Id}:'{s.Name}'{(s.Cluster.HomeSector ? "*" : "")}")));

                    // Same peer/session, immediately after the cluster exports,
                    // per sector, in the reference server's own order
                    // (fsmission.cpp:4170-4222): cluster → warps → stations.
                    // This is what finally gives the native client's
                    // FindTarget (src/Igc/common.cpp:470, station search at
                    // :500-501, adjacent-cluster warp walk at :677-707)
                    // non-empty GetWarps()/GetStations() to search, closing the
                    // P34 finding that FindTarget always returned NULL because
                    // the client's clusters had empty lists. Asteroids ARE
                    // exported (P64) — the map is m2xspiderw.igc, 134 real
                    // rocks — since the client's UndockMinersAndStartMining
                    // (CommanderResearchAndExpand.cs:1563-1568) searches
                    // GetAsteroids() for He3-named rocks. Treasure, mines and
                    // probes are intentionally skipped (none exist in this
                    // map / this codebase's scope), matching the task.
                    //
                    // Unconditional — no SeenBySide()/fog-of-war gate, exactly
                    // as P30 reasoned for clusters (Server.cs:742-747): this
                    // codebase has no scouting/visibility system, so export
                    // every warp, station and asteroid of every sector to every
                    // peer. Ships' resolved destination sectors come for free:
                    // the warp's own ClusterId addresses its sector, and all
                    // sectors are already exported above.
                    foreach (var sector in _missions[0].Sectors)
                    {
                        foreach (var resolved in sector.Warps)
                            SendPeer(from, FedPacket.Pack(new[] { ExportWarp.FromWarp(resolved).Encode() }));
                        foreach (var station in sector.Stations)
                            SendPeer(from, FedPacket.Pack(new[] { ExportStation.FromStation(station).Encode() }));
                        foreach (var asteroid in sector.Asteroids)
                            SendPeer(from, FedPacket.Pack(new[] { ExportAsteroid.FromAsteroid(asteroid).Encode() }));
                    }
                    Console.WriteLine($"[export] peer {from} '{req.CharacterName}' -> {_missions[0].Warps.Count} warp(s), {_missions[0].Sectors.Sum(s => s.Stations.Count)} station(s), {_missions[0].Sectors.Sum(s => s.Asteroids.Count)} asteroid(s) (OT_warp 7 / OT_station 1 / OT_asteroid 5)");

                    var set = new SetCluster
                    {
                        SectorId = live.Sector.Id,
                        Ship = ServerShipUpdate.FromModel(shipId, live.Ship, (uint)TickIndex),
                        Cookie = (byte)ack.Cookie,
                    };
                    SendPeer(from, FedPacket.Pack(new[] { set.Encode() }));

                    // FM_S_BUY_LOADOUT_ACK(5): after the client has been told
                    // its object (EXPORT 26) and its launch (SET_CLUSTER 7),
                    // adopt the real hull onto the client's own ship pointer.
                    // The C++ client handler (appmsg.cpp:3180-3183) calls
                    // m_ship->ProcessShipLoadout(...), which for a no-hull
                    // dummy (m_myHullType.GetHullType()==NULL) fires the
                    // "hull changed" branch (shipIGC.h:888-897) and calls
                    // SetBaseHullType(GetMyMission()->GetHullType(hullID)),
                    // making GetBaseHullType()/GetPosition() stop throwing NRE
                    // — the transition the real server achieves when a player
                    // buys a loadout (FedSrv.CPP:902-928 ProcessConnect starts
                    // every fresh player on a hullID=NA dummy too; the buy is
                    // the step that gives it a hull).
                    //
                    // nParts=0 simplification (unchanged from P26/P27): the
                    // ShipLoadout carries only the 2-byte hullID, no
                    // ExpandedPartData[] tail. Real per-part purchase is the
                    // deferred BUY_LOADOUT/economy machinery.
                    //
                    // fBoughtEverything=false, fLaunch=false: we assign a hull
                    // but buy no parts (honest), and SET_CLUSTER has already
                    // launched the ship before this ACK, so no further launch
                    // messages will follow (claiming fLaunch would be false).
                    // fLaunch=false also releases any client-side lockdown via
                    // EndLockDown (appmsg.cpp:3180-3181); for a direct-connect
                    // bot IsLockedDown() is false anyway, making it a no-op.
                    SendPeer(from, FedPacket.Pack(new[]
                    {
                        new BuyLoadoutAck
                        {
                            HullId = live.Ship.HullType.HullId,
                            FBoughtEverything = false,
                            FLaunch = false,
                        }.Encode(),
                    }));

                    // FM_S_MISSION_STAGE(37): the message this whole join burst
                    // was missing (P37, 2026-08-31). The native client's entire
                    // per-tick simulation -- CmissionIGC::Update
                    // (missionigc.cpp:3133-3134) and the per-cluster ship
                    // plot/execute loop (clusterIGC.cpp:103-231, PlotShipMove/
                    // ExecuteShipMove) -- is a no-op until the client's own core
                    // mission stage is STAGE_STARTED/STAGE_STARTING. The client's
                    // only path there outside the lobby (which a direct-connect
                    // client never sees) is this message
                    // (appmsg.cpp:2314-2349): case STAGE_STARTED calls
                    // SetInProgress(true) + m_pCoreIGC->SetMissionStage(STAGE_STARTED),
                    // unblocking both gates. This server already tracks
                    // Mission.Stage = Started internally (see the mission
                    // construction above) but never told the client -- confirmed
                    // via a live capture showing zero fmid=37 frames and three
                    // native probes (GotoPlan::Execute/ExecuteShipMove/
                    // CclusterIGC::Update) that never fired even with a correct
                    // ship position and a resolved goto target. Sent last, after
                    // everything the simulation needs (mission, sides, ship,
                    // clusters, warps, stations, launch) already exists.
                    SendPeer(from, FedPacket.Pack(new[]
                    {
                        new MissionStageMessage { Stage = MissionStageMessage.StageStarted }.Encode(),
                    }));
                    Console.WriteLine($"[stage] peer {from} '{req.CharacterName}' -> MISSION_STAGE(37) Started");

                    // FM_S_SHIP_STATUS(90): the message ScoutExploreMap's whole
                    // autonomous sweep/exploration is gated on (P39 finding —
                    // <c>src/Wopr/Wopr/Strategies/ScoutExploreMap.cs:85-221</c>).
                    // The client only acts on a status for a ship it has a
                    // PLAYERINFO record for (appmsg.cpp:2719, FindPlayer gate),
                    // which this server sends only for a peer's OWN ship — so
                    // this is correctly scoped to each peer's own ship. Sent
                    // after MISSION_STAGE so the client's mission is running and
                    // its ship is in a sector (the handler returns early if
                    // GetCluster()==null, ScoutExploreMap.cs:96-97), which this
                    // is: SET_CLUSTER already launched it into live.Sector.
                    //
                    // Simplifications (this repo's honesty-about-simplifications
                    // style): no scouting/visibility system exists, so
                    // Unknown=false and Detected=true always — there is no
                    // scanning state to make them meaningful yet (matching the
                    // simplified server's "no detection system" stance). State
                    // is c_ssFlying with StationID/ParentID NA because this
                    // server has no docking — ships are born flying in a sector.
                    SendPeer(from, FedPacket.Pack(new[]
                    {
                        MakeOwnShipStatus(shipId, live).Encode(),
                    }));
                    Console.WriteLine($"[shipstatus] peer {from} '{req.CharacterName}' -> SHIP_STATUS(90) ship {shipId} sector {live.Sector.Id}");
                    _lastStatusSector[from] = live.Sector.Id;
                    _lastStatusTick[from] = TickIndex;

                    // FM_S_GAIN_FLAG(156) join-sync (P201): the port of native
                    // fsmission.cpp:740-762 ("tell them about the players who
                    // have flags"), which runs right after SendMissionInfo and
                    // before the late-joiner/ENTER_GAME handling. A newly-joined
                    // player is told about every currently flag-carrying ship via
                    // one GAIN_FLAG per carrier — sideidFlag = the carried flag's
                    // side, shipidRecipient = the carrier's ship id,
                    // bIsTreasureDocked = false — sent to the joining player's
                    // OWN connection only (g.fm.SendMessages(pfsPlayer->GetConnection())).
                    int nFlagSync = 0;
                    foreach (var fk in _peerShips)
                    {
                        if (!ReferenceEquals(fk.Value.Ship.Mission, live.Ship.Mission)) continue;
                        if (fk.Value.Ship.Flag is not { } carrierFlag) continue;
                        SendPeer(from, FedPacket.Pack(new[]
                        {
                            new GainFlag
                            {
                                SideidFlag = carrierFlag,
                                ShipidRecipient = fk.Value.Ship.Id,
                                BIsTreasureDocked = false,
                                ParttypeidDocked = 0,
                            }.Encode(),
                        }));
                        nFlagSync++;
                    }
                    if (nFlagSync > 0)
                        Console.WriteLine($"[gainflag] peer {from} '{req.CharacterName}' -> GAIN_FLAG(156) join-sync x{nFlagSync} for flag-carrying ship(s)");

                    // FM_S_ENTER_GAME(128): the final "you're ready" signal at the
                    // real end of the join-sync burst (P141). Native fires it inside
                    // the mission-join path gated on STAGE_STARTED == GetStage()
                    // (fsmission.cpp:799-805), flushed to the joining player's OWN
                    // connection only (g.fm.SendMessages(pfsPlayer->GetConnection(),
                    // FM_GUARANTEED, FM_FLUSH), fsmission.cpp:830). Its companion
                    // GetPlayerScoreObject()->Connect (fsmission.cpp:801) is N/A
                    // here (no score system). P180 makes the gate meaningful: now
                    // that the mission is born NotStarted (not force-started at
                    // boot), this join-sync send is sent ONLY when the mission is
                    // already Started (a late joiner arriving after auto-start) —
                    // when still NotStarted it is suppressed here, because the
                    // ready-up auto-start transition broadcasts ENTER_GAME to the
                    // whole mission once the game actually starts (the same
                    // whole-mission audience native's StartGame uses,
                    // fsmission.cpp:2267-2269). This guard only ever suppresses
                    // when the mission is NOT yet started — never when it already
                    // is, so a peer joining a running mission still gets its own
                    // immediate ENTER_GAME through this path.
                    if (live.Ship.Mission is { Stage: MissionStage.Started })
                    {
                        SendPeer(from, FedPacket.Pack(new[] { new EnterGame().Encode() }));
                        Console.WriteLine($"[entergame] peer {from} '{req.CharacterName}' -> ENTER_GAME(128) (join-sync complete — player ready)");
                    }
                    else
                    {
                        Console.WriteLine($"[entergame] peer {from} '{req.CharacterName}' -> ENTER_GAME(128) deferred (mission NotStarted; sent on ready-up auto-start)");
                    }
                }
            }
            else if (fmid == ShipUpdate.Fmid)
            {
                // Step 3: inbound control input. Decode and, for the peer's own
                // ship, ONLY set the ship's Controls/StateM. Nothing moves here —
                // the ship's Motion lives in Mission.Update (the authoritative
                // tick). Running on the sim thread (Drain), so setting controls
                // is race-free w.r.t. the tick loop, and the payload's
                // position/velocity/orientation echoes are ignored as authority.
                try
                {
                    var u = ShipUpdate.Decode(m);
                    if (_peerShips.TryGetValue(from, out var live))
                    {
                        // P180 — a genuine pre-game hold: while the mission is
                        // still NotStarted (awaiting ready-up), a ship must not
                        // be able to fly, so inbound control input is ignored
                        // (report and drop, matching this file's other
                        // not-Started refusals like HandleSuicide). This is a
                        // real, necessary gate — the control path previously had
                        // zero Stage check unlike the 10+ other actions — a ship
                        // must not move while the mission hasn't started.
                        if (live.Ship.Mission is not { Stage: MissionStage.Started })
                        {
                            Console.WriteLine($"[ctrl] {from} ship {live.Ship.Id}: mission {(live.Ship.Mission is null ? "none" : $"stage {live.Ship.Mission.Stage}")} != STAGE_STARTED — SHIP_UPDATE ignored (pre-game hold)");
                        }
                        else if (live.Ship.ParentShip is not null)
                        {
                            Console.WriteLine($"[ctrl] {from} ship {live.Ship.Id}: boarded gunner — SHIP_UPDATE ignored, use ACTIVE_TURRET_UPDATE (FedSrv.CPP:4859)");
                        }
                        else
                        {
                            live.Ship.Controls = u.Payload.Controls;
                            live.Ship.StateM = u.Payload.StateM;
                            var c = u.Payload.Controls;
                            Console.WriteLine($"[ctrl] {from} ship {live.Ship.Id} controls=(yaw {c.Yaw:F3} pitch {c.Pitch:F3} " +
                                              $"roll {c.Roll:F3} thr {c.Throttle:F3}) state=0x{u.Payload.StateM:X}");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[ctrl] {from}: ship update for a peer with no ship — ignored");
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad SHIP_UPDATE from {from}: {e.Message}");
                }
            }
            else if (fmid == ActiveTurretUpdate.Fmid)   // FM_C_ACTIVE_TURRET_UPDATE (Messages.h:673-676)
            {
                // P109 — a turret gunner actively firing the turret, sent
                // INSTEAD of SHIP_UPDATE(24) while it controls the turret
                // (clintlib.cpp:1884-1893). Routes to the real
                // ProcessShipUpdate(const ClientActiveTurretUpdate&) port
                // (shipIGC.cpp:3349-3392) on the gunner's own ship, gated on the
                // sender actually being a boarded gunner (FedSrv.CPP:4873-4875).
                try
                {
                    var u = ActiveTurretUpdate.Decode(m);
                    if (_peerShips.TryGetValue(from, out var live))
                        HandleActiveTurretUpdate(live.Ship, u.Atu);
                    else
                        Console.WriteLine($"[turret] {from}: no ship — ACTIVE_TURRET_UPDATE ignored");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad ACTIVE_TURRET_UPDATE from {from}: {e.Message}");
                }
            }
            else if (fmid == InactiveTurretUpdate.Fmid)  // FM_C_INACTIVE_TURRET_UPDATE (Messages.h:678-679)
            {
                // P109 — the gunner stops actively controlling/firing the turret
                // (sent when StateM drops weaponsMaskIGC, clintlib.cpp:1894-1899).
                // Clears the same "shooting" state bit ACTIVE_TURRET_UPDATE set.
                try
                {
                    _ = InactiveTurretUpdate.Decode(m);   // no fields; decode validates cbmsg=4
                    if (_peerShips.TryGetValue(from, out var live))
                    {
                        var gunner = live.Ship;
                        if (gunner.Sector is not null && gunner.ParentShip is not null && gunner.TurretId is not null)
                        {
                            // Native server-side stop-shooting: SetInactiveTurretUpdate
                            // → GetIGCShip()->SetStateM(0) (fsship.cpp:1223) — a full
                            // assignment to 0, clearing the weaponsMaskIGC "shooting"
                            // bit (and every other state bit) exactly as native does.
                            gunner.StateM = 0;
                            Console.WriteLine($"[turret] ship {gunner.Id} INACTIVE_TURRET_UPDATE — SetStateM(0) (stop shooting, fsship.cpp:1223)");
                        }
                        else
                        {
                            Console.WriteLine($"[turret] {from}: INACTIVE_TURRET_UPDATE from a non-gunner — ignored (FedSrv.CPP:4887-4889)");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[turret] {from}: no ship — INACTIVE_TURRET_UPDATE ignored");
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad INACTIVE_TURRET_UPDATE from {from}: {e.Message}");
                }
            }
            else if (fmid == ChatMessage.Fmid)
            {
                // Step: relay an inbound FM_CS_CHATMESSAGE(23). Decode, resolve
                // the sender's ship (and thus its SideId — P21 gave ships a real
                // SideId), then forward the ORIGINAL bytes to the peers that the
                // target selects. Only two targets are wired: Everyone (every
                // connected peer, mirroring BroadcastShipStates' loop over
                // _peerShips) and Team (peers whose SideId matches the sender's,
                // reusing the side field rather than reinventing comparison).
                // Any other ChatTarget is reported and dropped — not broadcast
                // as a leniency fallback (that would be a real behavior bug) and
                // not thrown (an unimplemented target from a client must not
                // crash the server). Matches README's "report and ignore" rule.
                try
                {
                    var chat = ChatMessage.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var senderLive))
                    {
                        Console.WriteLine($"[chat] {from}: chat from a peer with no ship — ignored");
                    }
                    else
                    {
                        // P187 — ballot chat commands: "#resign"/"#draw"/"#mutiny"
                        // are intercepted here (mirroring native FedSrv.CPP:5170-5200)
                        // and REPLACE the normal chat forward when matched — the
                        // command text is never relayed as chat. A non-matching
                        // message falls through to the ordinary relay below.
                        if (!TryHandleBallotCommand(from, senderLive.Ship, chat.Message))
                        {
                            var targets = SelectChatTargets(from, chat, senderLive.Ship.SideId);
                            Console.WriteLine($"[chat] {from} ship {senderLive.Ship.Id} side {sideStr(senderLive.Ship.SideId)} '{chat.Message}' " +
                                              $"target {chat.ChatTarget} -> {targets.Length} peer(s)");
                            if (targets.Length > 0)
                            {
                                byte[] packet = FedPacket.Pack(new[] { m });
                                foreach (var t in targets) SendPeer(t, packet);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad CHATMESSAGE from {from}: {e.Message}");
                }
            }
            else if (fmid == Vote.Fmid)
            {
                // Step: P187 — record a client's vote on a pending ballot
                // (FM_C_VOTE, Messages.h:185-188), the port of native
                // CFSMission::TallyVote (fsmission.cpp:1207-1219). Decode, find
                // the named ballot in the SENDER'S OWN mission, and cast the
                // vote if the sender is an eligible voter. Any refusal (ballot
                // missing/already resolved, or the sender not eligible) is
                // logged and ignored — never fatal, never creates a ballot.
                try
                {
                    var vote = Vote.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var voterLive))
                    {
                        Console.WriteLine($"[vote] {from}: VOTE from a peer with no ship — ignored");
                    }
                    else if (voterLive.Ship.Mission is not { } voteMission)
                    {
                        Console.WriteLine($"[vote] ship {voterLive.Ship.Id}: VOTE for a ship with no mission — ignored");
                    }
                    else
                    {
                        var list = BallotsOf(voteMission);
                        var ballot = list.FirstOrDefault(b => b.Id == vote.BallotId);
                        if (ballot is null)
                        {
                            Console.WriteLine($"[vote] ship {voterLive.Ship.Id}: VOTE for unknown/resolved ballot {vote.BallotId} — ignored");
                        }
                        else
                        {
                            short voterSide = voterLive.Ship.SideId ?? -1;
                            bool cast = ballot.TryCastVote(voterLive.Ship.Id, voterSide, vote.Agree);
                            Console.WriteLine($"[vote] ship {voterLive.Ship.Id} ballot {ballot.Id} ({ballot.Type}) " +
                                              $"{(vote.Agree ? "for" : "against")} side {voterSide} -> {(cast ? "recorded" : "REFUSED (not eligible / already voted)")}");
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad VOTE from {from}: {e.Message}");
                }
            }
            else if (fmid == ChatBuoy.Fmid)
            {
                // Step: relay an inbound FM_CS_CHATBUOY(108) — the message
                // BaseClient::SendChat actually uses for a chat targeting a buoy
                // (clintlib.cpp:2432-2444: otTarget == OT_buoy → CHATBUOY, else
                // CHATMESSAGE). The commander's real dispatch to a docked miner
                // (CommanderResearchAndExpand.cs:1610,1615-1618 passes
                // otTarget = buoy.GetObjectType(), oidTarget = buoy.GetObjectID())
                // therefore arrives HERE, not as CHATMESSAGE(23) — previously
                // logged "[wire] unsupported fmid 108 — ignored" and silently
                // dropped. Same routing as CHATMESSAGE: decode, resolve the
                // sender's ship/SideId, then call the exact same
                // SelectChatTargets method (its parameter is now the shared
                // IChatRoutingTarget, so no duplicated switch), and forward the
                // ORIGINAL bytes to whoever it selects (verbatim relay, matching
                // CS_PING's echo and ChatMessage's own relay, Server.cs:1281).
                try
                {
                    var buoy = ChatBuoy.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var senderLive))
                    {
                        Console.WriteLine($"[chatbuoy] {from}: chat from a peer with no ship — ignored");
                    }
                    else
                    {
                        var targets = SelectChatTargets(from, buoy, senderLive.Ship.SideId);
                        Console.WriteLine($"[chatbuoy] {from} ship {senderLive.Ship.Id} side {sideStr(senderLive.Ship.SideId)} " +
                                          $"'{buoy.Message}' target {buoy.ChatTarget} buoy {buoy.Db.BuoyId} -> {targets.Length} peer(s)");
                        if (targets.Length > 0)
                        {
                            byte[] packet = FedPacket.Pack(new[] { m });
                            foreach (var t in targets) SendPeer(t, packet);
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad CHATBUOY from {from}: {e.Message}");
                }
            }
            else if (fmid == HighlightCluster.Fmid)
            {
                // Step: relay an inbound FM_CS_HIGHLIGHT_CLUSTER(199) — the
                // commander marking a sector on the map for their own teammates
                // (Xynth #208). Pure relay (FedSrv.CPP:7369-7375, the whole
                // handler): decode, resolve the sender's ship (and thus its
                // SideId — the ship's real side, P21), then forward the ORIGINAL
                // bytes to every peer on that same side. This mirrors the native
                // body verbatim —
                //   g.fm.ForwardMessage(CFSSide::FromIGC(pfsPlayer->GetSide())->GetGroup(), pfmHC, FM_GUARANTEED);
                // (FedSrv.CPP:7374) — the sender's own side GROUP. No validation,
                // no cluster-existence check, no sender-side check, no
                // STAGE_STARTED gate in the native case (Messages.h:1033-1036
                // declares only clusterID + highlight), so this port adds none.
                // Reuses the exact relay shape of the CHATBUOY/CHATMESSAGE
                // branches: resolve SideId → FedPacket.Pack(new[]{ m }) verbatim
                // → SendPeer to the matching-side peers. The
                // _peerShips.Where(SideId == sender's) predicate is the SAME one
                // the Team chat target uses (SelectChatTargets, Server.cs:2370-2374),
                // so no new relay infrastructure is introduced.
                try
                {
                    var hl = HighlightCluster.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var senderLive))
                    {
                        Console.WriteLine($"[highlight] {from}: highlight from a peer with no ship — ignored");
                    }
                    else
                    {
                        var targets = _peerShips
                            .Where(kv => kv.Value.Ship.SideId == senderLive.Ship.SideId)
                            .Select(kv => kv.Key)
                            .ToArray();
                        Console.WriteLine($"[highlight] {from} ship {senderLive.Ship.Id} side {sideStr(senderLive.Ship.SideId)} " +
                                          $"cluster {hl.ClusterId} highlight {hl.Highlight} -> {targets.Length} same-side peer(s)");
                        if (targets.Length > 0)
                        {
                            byte[] packet = FedPacket.Pack(new[] { m });
                            foreach (var t in targets) SendPeer(t, packet);
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad HIGHLIGHT_CLUSTER from {from}: {e.Message}");
                }
            }
            else if (fmid == Docked.Fmid)
            {
                // Step: handle an inbound FM_C_DOCKED(48). The native handler
                // (FedSrv.CPP:7031-7135) is genuinely dual-purpose — read the
                // whole block — and this branch now mirrors both halves:
                //  • stationID == NA  → the pure-undock announce, already
                //    ported (PerformUndock) and preserved exactly as-is.
                //  • stationID != NA  → the teleport/relocation dock request
                //    (P110): a displaced ship lands at a specific friendly
                //    restart-capable station, acknowledged with
                //    FM_S_TELEPORT_ACK(102). Native gating (all in
                //    FedSrv.CPP:7040-7077): only a ship the server believes is
                //    already docked somewhere (GetStation()!=NULL, :7040) and
                //    that is not a boarded child (pshipParent==NULL, :7045)
                //    participates; a non-NA request names a station that must
                //    be real, on the player's side or an ally's, and
                //    c_sabmRestart-capable — else the ACK answers stationID=NA
                //    (rejected, :7068) and the ship stays put. The old
                //    "obsolete half dropped" handling is gone: this is exactly
                //    the legit-request direction Messages.h:402-404 calls
                //    obsolete *as a general client-dock trigger*, but the
                //    server still answers a real one (the "port the responder,
                //    defer the trigger" discipline).
                try
                {
                    var docked = Docked.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var undockLive))
                    {
                        Console.WriteLine($"[undock] {from}: dock message from a peer with no ship — ignored");
                    }
                    else if (undockLive.Ship.StationId is null)
                    {
                        Console.WriteLine($"[undock] {from} ship {undockLive.Ship.Id}: not docked server-side — dock message ignored (gate: GetStation()!=NULL, FedSrv.CPP:7040)");
                    }
                    else if (docked.StationId != -1)
                    {
                        // P110 — the teleport/relocation dock request.
                        PerformTeleport(from, undockLive, docked.StationId);
                    }
                    else
                    {
                        PerformUndock(from, undockLive);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad DOCKED from {from}: {e.Message}");
                }
            }
            else if (fmid == 35) // FM_CS_PING (Messages.h:280-283)
            {
                // Native's own comment is the entire spec: "just send it back
                // to client as soon as you get it." No new codec needed --
                // the client only cares that it gets a well-formed echo of
                // the exact message it sent, so re-pack the raw bytes
                // unmodified rather than decoding/re-encoding timeClient/
                // timeServer/fmg (whose exact native byte widths aren't
                // worth guessing at when a verbatim echo already satisfies
                // the spec precisely). This is CommanderResearchAndExpand's
                // own ~5s heartbeat (its AttachMessages comment: "drive
                // infrequent updates") -- UndockMinersAndStartMining and
                // QueueTechForAvailableMoney are only ever called from this
                // message's client-side handler, so without this echo those
                // never fire at all, regardless of anything else being
                // correct server-side.
                SendPeer(from, FedPacket.Pack(new[] { m }));
            }
            else if (fmid == BuyLoadoutRequest.Fmid)   // FM_C_BUY_LOADOUT (Messages.h:65-68)
            {
                // The client's requested loadout (hull + parts) — this message
                // was not received at all before P61. Decode, resolve against
                // the peer's mission core, apply to the ship's generalized
                // loadout, and answer with a real FM_S_BUY_LOADOUT_ACK carrying
                // the actual mounted-part tail (nParts = real count, not 0).
                try
                {
                    var buy = BuyLoadoutRequest.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var buyLive))
                    {
                        Console.WriteLine($"[loadout] {from}: BUY_LOADOUT from a peer with no ship — ignored");
                    }
                    else
                    {
                        ApplyLoadout(from, buyLive, buy);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad BUY_LOADOUT from {from}: {e.Message}");
                }
            }
            else if (fmid == PositionReq.Fmid)   // FM_C_POSITIONREQ (Messages.h:320-323)
            {
                // P184 — a client's request to be seated on a side. Port of
                // FedSrv.CPP:6658-6707 → RequestPosition (fsmission.cpp:4729-4774).
                // Validate the side exists (refuse with DELPOSITIONREQ(DPR_SideGone)
                // to the requester ONLY); then auto-accept when the target side has
                // no leader (a leaderless side is empty in this port — TryJoinSide
                // seats the first player as leader), otherwise broadcast
                // POSITIONREQ(40) whole-mission and record the pending request for
                // the leader to approve. CheckPositionRequest's ~10 native gates are
                // all inapplicable here (no defections/bans/squads/invites/rank/
                // balance/server-pause — see the P184 doc); only "side still exists"
                // is reachable, and the handler's existence check covers it before
                // this branch is ever reached. bRejoin / AUTO_ACCEPT / PickNewSide
                // (iSide==NA) are out of scope per the task.
                try
                {
                    var pr = PositionReq.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var reqLive))
                    {
                        Console.WriteLine($"[posreq] {from}: POSITIONREQ from a peer with no ship — ignored");
                    }
                    else if (reqLive.Ship.Mission is not Mission mission)
                    {
                        Console.WriteLine($"[posreq] {from} ship {reqLive.Ship.Id}: no mission — POSITIONREQ ignored");
                    }
                    else
                    {
                        var targetSide = mission.Sides.FirstOrDefault(s => s.Id == pr.SideId);
                        if (targetSide is null)
                        {
                            // Side no longer exists: deny to the requester only
                            // (FedSrv.CPP:6692-6704, not whole-mission).
                            var deny = new DelPositionReq { ShipId = reqLive.Ship.Id, SideId = pr.SideId, Reason = DelPositionReqReason.SideGone };
                            SendPeer(from, FedPacket.Pack(new[] { deny.Encode() }));
                            Console.WriteLine($"[posreq] ship {reqLive.Ship.Id} -> side {pr.SideId} DENIED (DPR_SideGone) to requester only");
                        }
                        else if (mission.LockSides)
                        {
                            // P186 — the mission owner locked side changes:
                            // refuse any side-switch POSITIONREQ with
                            // DELPOSITIONREQ(DPR_SidesLocked) to the requester
                            // only, mirroring native CheckPositionRequest's
                            // bLockSides gate (fsmission.cpp:4590). This side has
                            // no SIDE_TEAMLOBBY (this port seats players directly
                            // via config), so every POSITIONREQ is a real side
                            // switch and is refused while LockSides is on. Placed
                            // before the auto-accept/leader branches because native
                            // runs CheckPositionRequest before RequestPosition's
                            // accept logic — the lock wins over both.
                            var deny = new DelPositionReq { ShipId = reqLive.Ship.Id, SideId = pr.SideId, Reason = DelPositionReqReason.SidesLocked };
                            SendPeer(from, FedPacket.Pack(new[] { deny.Encode() }));
                            Console.WriteLine($"[posreq] ship {reqLive.Ship.Id} -> side {pr.SideId} DENIED (DPR_SidesLocked) to requester only — sides locked");
                        }
                        else if (targetSide.LeaderShipId is null || targetSide.AutoAccept)
                        {
                            // No leader on the target side yet (hence empty) → auto-
                            // accept immediately (native RequestPosition's leaderless
                            // fast path, fsmission.cpp:4757; this port folds
                            // fAutoAcceptLeaders to always-on for leaderless sides —
                            // the config isn't ported). P186: an already-led side
                            // with Side.AutoAccept set also auto-accepts here
                            // (native RequestPosition's rgfAutoAccept[sideID] branch,
                            // fsmission.cpp:4759), so a request to it seats the
                            // requester instead of going pending. TryJoinSide seats
                            // and announces; the accept broadcasts cover the
                            // POSITIONREQ(40) notice.
                            Console.WriteLine($"[posreq] ship {reqLive.Ship.Id} -> side {pr.SideId}: {(targetSide.LeaderShipId is null ? "no leader" : "auto-accept")} — auto-accept via TryJoinSide");
                            TryJoinSide(reqLive.Ship, pr.SideId);
                        }
                        else
                        {
                            // Side has a leader: broadcast the pending request and
                            // record it for the leader to approve.
                            BroadcastPositionReq(mission, reqLive.Ship.Id, pr.SideId);
                            _pendingJoinRequests[from] = (reqLive.Ship.Id, pr.SideId);
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad POSITIONREQ from {from}: {e.Message}");
                }
            }
            else if (fmid == PositionAck.Fmid)   // FM_C_POSITIONACK (Messages.h:331-336)
            {
                // P184 — the team leader accepts or rejects a pending side-join
                // request. Port of FedSrv.CPP:6709-6747. Gates (defensive re-checks,
                // never trusting the client's belief it's still leader): the sender
                // must be seated on the named side AND be that side's current leader;
                // the target ship must still exist in this mission (else silently
                // ignore — "player already went away"). On accept: seat the requester
                // via TryJoinSide after removing the pending request. On reject:
                // remove the pending request and broadcast DELPOSITIONREQ(DPR_Rejected)
                // whole-mission. Silent no-op if there was no matching pending request.
                try
                {
                    var pa = PositionAck.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var ackLive))
                    {
                        Console.WriteLine($"[posack] {from}: POSITIONACK from a peer with no ship — ignored");
                    }
                    else if (ackLive.Ship.Mission is not Mission mission)
                    {
                        Console.WriteLine($"[posack] {from} ship {ackLive.Ship.Id}: no mission — POSITIONACK ignored");
                    }
                    else if (ackLive.Ship.SideId != pa.SideId)
                    {
                        Console.WriteLine($"[posack] {from} ship {ackLive.Ship.Id}: ACK side {pa.SideId} != own side {ackLive.Ship.SideId} — refused");
                    }
                    else
                    {
                        var targetSide = mission.Sides.FirstOrDefault(s => s.Id == pa.SideId);
                        if (targetSide is null || targetSide.LeaderShipId != ackLive.Ship.Id)
                        {
                            Console.WriteLine($"[posack] {from} ship {ackLive.Ship.Id}: not {pa.SideId}'s current leader — ACK refused");
                        }
                        else
                        {
                            var targetConn = FindConnectionOfShip(mission, pa.ShipId);
                            if (targetConn is null)
                            {
                                // Target ship already went away — silently ignore.
                                Console.WriteLine($"[posack] ship {pa.ShipId} not in mission — ACK ignored (player already went away)");
                            }
                            else if (pa.FAccepted)
                            {
                                if (RemovePendingJoinRequest(targetConn.Value, pa.SideId))
                                {
                                    // Still exists + request still there → seat them.
                                    var targetShip = _peerShips[targetConn.Value].Ship;
                                    TryJoinSide(targetShip, pa.SideId);
                                }
                                else
                                {
                                    Console.WriteLine($"[posack] ship {pa.ShipId}: no matching pending request — accept no-op");
                                }
                            }
                            else
                            {
                                if (RemovePendingJoinRequest(targetConn.Value, pa.SideId))
                                {
                                    BroadcastDelPositionReq(mission, pa.ShipId, pa.SideId, DelPositionReqReason.Rejected);
                                }
                                else
                                {
                                    Console.WriteLine($"[posack] ship {pa.ShipId}: no matching pending request — reject no-op");
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad POSITIONACK from {from}: {e.Message}");
                }
            }
            else if (fmid == DelPositionReq.Fmid)   // FM_CS_DELPOSITIONREQ (Messages.h:366-370)
            {
                // P184 — the second, genuinely client-sendable entry point for
                // withdrawing a pending request (not just a server broadcast).
                // Port of FedSrv.CPP:7484-7516: if shipID names the sender's own
                // ship, the reason MUST be DPR_Canceled (self-cancel); otherwise
                // the sender MUST be the named side's leader and the reason MUST
                // be DPR_Rejected (leader-initiated reject). On either valid case,
                // relay the EXACT received message whole-mission (native's plain
                // ForwardMessage) and remove the matching pending request WITHOUT an
                // additional broadcast (the relay already told everyone).
                try
                {
                    var del = DelPositionReq.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var delLive))
                    {
                        Console.WriteLine($"[delreq] {from}: DELPOSITIONREQ from a peer with no ship — ignored");
                    }
                    else if (delLive.Ship.Mission is not Mission mission)
                    {
                        Console.WriteLine($"[delreq] {from} ship {delLive.Ship.Id}: no mission — DELPOSITIONREQ ignored");
                    }
                    else
                    {
                        var targetSide = mission.Sides.FirstOrDefault(s => s.Id == del.SideId);
                        if (targetSide is null)
                        {
                            Console.WriteLine($"[delreq] ship {del.ShipId} side {del.SideId}: side does not exist — ignored");
                        }
                        else
                        {
                            bool selfCancel = del.ShipId == delLive.Ship.Id;
                            bool valid = selfCancel
                                ? del.Reason == DelPositionReqReason.Canceled          // own ship → must cancel
                                : (del.Reason == DelPositionReqReason.Rejected          // someone else → leader reject
                                   && targetSide.LeaderShipId == delLive.Ship.Id);
                            if (!valid)
                            {
                                Console.WriteLine($"[delreq] ship {del.ShipId} side {del.SideId} reason {del.Reason} from {delLive.Ship.Id}: invalid combo — ignored");
                            }
                            else
                            {
                                // Relay the exact bytes whole-mission, then drop the
                                // matching request without a second broadcast.
                                SendToMissionPeers(mission, FedPacket.Pack(new[] { m }));
                                var targetConn = selfCancel ? (ConnectionId?)from : FindConnectionOfShip(mission, del.ShipId);
                                if (targetConn is not null)
                                    RemovePendingJoinRequest(targetConn.Value, del.SideId);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad DELPOSITIONREQ from {from}: {e.Message}");
                }
            }
            else if (fmid == ForceTeamReady.Fmid)   // FM_CS_FORCE_TEAM_READY (Messages.h:392-397)
            {
                // P184 — the side's team leader forces the whole side ready (or
                // un-forces it). Port of FedSrv.CPP:6987-7015 (shared block with
                // PLAYER_READY, the fIsForce branch): refuse unless the sender is
                // the named side's current leader (and names their own side). On
                // success: relay the exact message whole-mission, set Side.ForceReady,
                // and re-run the readiness computation — which, per the amended
                // RecomputeSideReady, now reports ready unconditionally (subject to
                // the >=1 connected-player floor), broadcasting TEAM_READY and
                // triggering auto-start if the mission is now fully ready.
                try
                {
                    var ft = ForceTeamReady.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var frLive))
                    {
                        Console.WriteLine($"[forceready] {from}: FORCE_TEAM_READY from a peer with no ship — ignored");
                    }
                    else if (frLive.Ship.Mission is not Mission mission)
                    {
                        Console.WriteLine($"[forceready] {from} ship {frLive.Ship.Id}: no mission — FORCE_TEAM_READY ignored");
                    }
                    else if (frLive.Ship.SideId is not { } frSideId || ft.SideId != frSideId)
                    {
                        Console.WriteLine($"[forceready] {from} ship {frLive.Ship.Id}: names side {ft.SideId}, own side {frLive.Ship.SideId} — ignored (must name own side)");
                    }
                    else
                    {
                        var frSide = mission.Sides.FirstOrDefault(s => s.Id == frSideId);
                        if (frSide is null || frSide.LeaderShipId != frLive.Ship.Id)
                        {
                            Console.WriteLine($"[forceready] {from} ship {frLive.Ship.Id}: not side {frSideId}'s current leader — FORCE_TEAM_READY refused");
                        }
                        else
                        {
                            // Forward-then-apply (native: ForwardMessage then SetForceReady).
                            SendToMissionPeers(mission, FedPacket.Pack(new[] { m }));
                            frSide.ForceReady = ft.FForceReady;
                            RecomputeSideReady(frLive.Ship);
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad FORCE_TEAM_READY from {from}: {e.Message}");
                }
            }
            else if (fmid == StartGame.Fmid)   // FM_C_START_GAME (Messages.h:868-869)
            {
                // P188 — the mission owner manually triggers the start
                // countdown, a genuine alternative to the ready-up auto-start.
                // Port of FedSrv.CPP:7597-7621: refuse unless the sender is the
                // mission owner (P183's owner concept — the owning side's
                // leader, the same IsMissionOwnerPlayer gate P186's LOCK_LOBBY
                // uses), the mission is STAGE_NOTSTARTED, and every occupied
                // side is ready. Native's bot-authentication special-case (bots
                // may start regardless of ownership / with zero countdown) is
                // OUT OF SCOPE — no such concept exists in this port. Native's
                // full FAllReady (team-balance/rank logic needing
                // nMaxPlayersPerTeam/a rank system) is likewise replaced by
                // this port's own already-simplified readiness check — the
                // same IsAllOccupiedSidesReady P180's auto-start uses (a real,
                // deliberate simplification, stated in the P188 writeup). On
                // success: call StartCountdown with the real native constant
                // c_fMissionBriefingCountdown = 15s (igc.h:1005).
                try
                {
                    _ = StartGame.Decode(m);   // zero-field marker; decode validates cbmsg=4
                    if (!_peerShips.TryGetValue(from, out var sgLive))
                    {
                        Console.WriteLine($"[startgame] {from}: START_GAME from a peer with no ship — ignored");
                    }
                    else if (sgLive.Ship.Mission is not Mission mission)
                    {
                        Console.WriteLine($"[startgame] {from} ship {sgLive.Ship.Id}: no mission — START_GAME ignored");
                    }
                    else if (!IsMissionOwnerPlayer(sgLive.Ship, mission))
                    {
                        Console.WriteLine($"[startgame] {from} ship {sgLive.Ship.Id}: not the mission owner — START_GAME refused");
                    }
                    else if (mission.Stage != MissionStage.NotStarted)
                    {
                        Console.WriteLine($"[startgame] {from} ship {sgLive.Ship.Id}: mission stage {mission.Stage} != STAGE_NOTSTARTED — START_GAME refused");
                    }
                    else if (!IsAllOccupiedSidesReady(mission))
                    {
                        Console.WriteLine($"[startgame] {from} ship {sgLive.Ship.Id}: not all occupied sides ready — START_GAME refused");
                    }
                    else
                    {
                        StartCountdown(mission, BriefingCountdownSeconds);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad START_GAME from {from}: {e.Message}");
                }
            }
            else if (fmid == RandomizeTeams.Fmid)   // FM_C_RANDOMIZE_TEAMS (Messages.h:201)
            {
                // P195 — the mission owner's "balance the teams" trigger (not
                // actually random). Port of FedSrv.CPP:6964-6997: refuse unless
                // the sender is the mission owner (the same IsMissionOwnerPlayer
                // gate P186's LOCK_LOBBY/START_GAME use) and the mission is
                // STAGE_NOTSTARTED, then balance the sides. The real dispatch
                // branches on iMaxImbalance==0x7ffe (balance) vs flush — the
                // flush branch (dump everyone to a neutral lobby, no
                // reassignment) is OUT OF SCOPE: this port has no SIDE_TEAMLOBBY
                // and no MissionParams field, and "balance" is what the message
                // name promises, so Stage-1 always balances (a deliberate,
                // permanent simplification — see docs/RANDOMIZE_TEAMS_P195.md).
                // LockSides is deliberately BYPASSED (not a refusal): native's
                // RandomizeSides does not consult bLockSides (SetLockSides is
                // even commented out there, fsmission.cpp:5258-5259, "Randomize
                // should not touch sides!"), and this is a privileged
                // owner-only, NotStarted-only mass reassignment, not the
                // ordinary player-initiated side-switch the lock guards.
                try
                {
                    _ = RandomizeTeams.Decode(m);   // zero-field marker; decode validates cbmsg=4
                    if (!_peerShips.TryGetValue(from, out var rtLive))
                    {
                        Console.WriteLine($"[randomizeteams] {from}: RANDOMIZE_TEAMS from a peer with no ship — ignored");
                    }
                    else if (rtLive.Ship.Mission is not Mission mission)
                    {
                        Console.WriteLine($"[randomizeteams] {from} ship {rtLive.Ship.Id}: no mission — RANDOMIZE_TEAMS ignored");
                    }
                    else if (!IsMissionOwnerPlayer(rtLive.Ship, mission))
                    {
                        Console.WriteLine($"[randomizeteams] {from} ship {rtLive.Ship.Id}: not the mission owner — RANDOMIZE_TEAMS refused");
                    }
                    else if (mission.Stage != MissionStage.NotStarted)
                    {
                        Console.WriteLine($"[randomizeteams] {from} ship {rtLive.Ship.Id}: mission stage {mission.Stage} != STAGE_NOTSTARTED — RANDOMIZE_TEAMS refused");
                    }
                    else
                    {
                        RebalanceSides(mission);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad RANDOMIZE_TEAMS from {from}: {e.Message}");
                }
            }
            else if (fmid == AutoDonate.Fmid)   // FM_C_AUTODONATE (Messages.h:738-741)
            {
                // P197 — the per-player auto-donate request: a two-mode dispatch
                // (set-own-target-with-optional-gift, or the leader-only
                // amount==-1 toggle of a subordinate's donation) plus the
                // donation-confidence leadership check. Port of
                // FedSrv.CPP:5516-5658 case FM_C_AUTODONATE.
                try
                {
                    var autoDonate = AutoDonate.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var adLive))
                    {
                        Console.WriteLine($"[autodonate] {from}: AUTODONATE from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandleAutoDonate(from, adLive, autoDonate);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad AUTODONATE from {from}: {e.Message}");
                }
            }
            else if (fmid == RequestMoney.Fmid)   // FM_CS_REQUEST_MONEY (Messages.h:934-938)
            {
                // P199 — a player who wants to buy something but lacks the funds
                // asks whoever actually holds the team's money to send it. Port of
                // FedSrv.CPP:5361-5406 case FM_CS_REQUEST_MONEY. This handler
                // NEVER transfers money — it only FORWARDS the request (with
                // shipidRequest filled in) to exactly one recipient: the sender's
                // auto-donate target if that player's wallet holds >= amount,
                // otherwise the richest OTHER teammate; silently dropped if no
                // recipient resolves.
                try
                {
                    var requestMoney = RequestMoney.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var rmLive))
                    {
                        Console.WriteLine($"[requestmoney] {from}: REQUEST_MONEY from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandleRequestMoney(from, rmLive, requestMoney);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad REQUEST_MONEY from {from}: {e.Message}");
                }
            }
            else if (fmid == PlayerDonate.Fmid)   // FM_C_PLAYER_DONATE (Messages.h:527-530)
            {
                // P203 — a manual, direct player-to-player gift. Port of
                // FedSrv.CPP:7213-7262 case FM_C_PLAYER_DONATE: gated on
                // STAGE_STARTED; a same-side or allied-side (P190 AllyGroup)
                // recipient with sufficient sender-wallet funds is debited +
                // credited with MONEY_CHANGE(60) to the sender's side group
                // always (and the recipient's side group when the gift crossed
                // sides); any refusal bounces back a no-op MONEY_CHANGE to the
                // sender alone with no wallet movement.
                try
                {
                    var playerDonate = PlayerDonate.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var pdLive))
                    {
                        Console.WriteLine($"[playerdonate] {from}: PLAYER_DONATE from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandlePlayerDonate(from, pdLive, playerDonate);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad PLAYER_DONATE from {from}: {e.Message}");
                }
            }
            else if (fmid == LockLobby.Fmid)   // FM_CS_LOCK_LOBBY (Messages.h:372-374)
            {
                // P186 — the mission owner locks/unlocks the lobby. Port of
                // FedSrv.CPP:6938-6951: refuse unless the sender is the mission
                // owner (the owning side's leader, per the P183 owner concept
                // `Mission.MissionOwnerSideId`). On success relay the exact
                // message whole-mission and set Mission.LockLobby. Honesty note:
                // the flag has no real gating effect in this port — native's
                // consequence (refusing brand-new lobby joiners, FedSrv.CPP:733,
                // DPR_LobbyLocked) belongs to the legacy lobby/rejoin machinery
                // (OldPlayerLink/bAllowJoiners/nTeams) this port has ruled N/A:
                // players are seated directly via config, not a "first join"
                // flow, so there is no hook point to attach the lock to. Built
                // for wire completeness and any future client UI only.
                try
                {
                    var ll = LockLobby.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var llLive))
                    {
                        Console.WriteLine($"[locklobby] {from}: LOCK_LOBBY from a peer with no ship — ignored");
                    }
                    else if (llLive.Ship.Mission is not Mission mission)
                    {
                        Console.WriteLine($"[locklobby] {from} ship {llLive.Ship.Id}: no mission — LOCK_LOBBY ignored");
                    }
                    else if (!IsMissionOwnerPlayer(llLive.Ship, mission))
                    {
                        Console.WriteLine($"[locklobby] {from} ship {llLive.Ship.Id}: not the mission owner — LOCK_LOBBY refused");
                    }
                    else
                    {
                        // Forward-then-apply (native: ForwardMessage then SetLockLobby).
                        SendToMissionPeers(mission, FedPacket.Pack(new[] { m }));
                        mission.LockLobby = ll.FLock;
                        Console.WriteLine($"[locklobby] ship {llLive.Ship.Id}: LOCK_LOBBY fLock={ll.FLock} whole-mission, Mission.LockLobby={mission.LockLobby}");
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad LOCK_LOBBY from {from}: {e.Message}");
                }
            }
            else if (fmid == LockSides.Fmid)   // FM_CS_LOCK_SIDES (Messages.h:376-378)
            {
                // P186 — the mission owner locks/unlocks team changes. Port of
                // FedSrv.CPP:6953-6962: refuse unless the sender is the mission
                // owner. On success relay the exact message whole-mission and set
                // Mission.LockSides. Unlike LOCK_LOBBY this one HAS a real,
                // reachable hook: the POSITIONREQ side-switch handler refuses a
                // request with DELPOSITIONREQ(DPR_SidesLocked) while LockSides is
                // on, mirroring native CheckPositionRequest's bLockSides gate
                // (fsmission.cpp:4590).
                try
                {
                    var ls = LockSides.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var lsLive))
                    {
                        Console.WriteLine($"[locksides] {from}: LOCK_SIDES from a peer with no ship — ignored");
                    }
                    else if (lsLive.Ship.Mission is not Mission mission)
                    {
                        Console.WriteLine($"[locksides] {from} ship {lsLive.Ship.Id}: no mission — LOCK_SIDES ignored");
                    }
                    else if (!IsMissionOwnerPlayer(lsLive.Ship, mission))
                    {
                        Console.WriteLine($"[locksides] {from} ship {lsLive.Ship.Id}: not the mission owner — LOCK_SIDES refused");
                    }
                    else
                    {
                        // Forward-then-apply (native: ForwardMessage then SetLockSides).
                        SendToMissionPeers(mission, FedPacket.Pack(new[] { m }));
                        mission.LockSides = ls.FLock;
                        Console.WriteLine($"[locksides] ship {lsLive.Ship.Id}: LOCK_SIDES fLock={ls.FLock} whole-mission, Mission.LockSides={mission.LockSides}");
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad LOCK_SIDES from {from}: {e.Message}");
                }
            }
            else if (fmid == SideInactive.Fmid)   // FM_CS_SIDE_INACTIVE (Messages.h:532-536)
            {
                // P202 — pre-game roster shrinking: the mission owner marks an
                // *empty* side inactive/active before the mission starts, to play a
                // smaller-than-map-default team count. Port of FedSrv.CPP:7379-7425
                // (the FM_CS_SIDE_INACTIVE dispatch), read in full this session.
                // SCOPE BOUNDARY: this is ONLY use #1 of native's SIDE_INACTIVE
                // machinery — the in-game forced-elimination flow (DeactivateSide,
                // fsmission.cpp:5340-5380) is out of scope (superseded by P187's
                // resign substitution), and P178's CheckForVictoryByInactiveSides
                // keeps detecting side-emptiness by connected-peer presence and does
                // NOT consume the new Side.Active field.
                try
                {
                    var si = SideInactive.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var siLive))
                    {
                        Console.WriteLine($"[sideinactive] {from}: SIDE_INACTIVE from a peer with no ship — ignored");
                    }
                    else if (siLive.Ship.Mission is not Mission mission)
                    {
                        Console.WriteLine($"[sideinactive] {from} ship {siLive.Ship.Id}: no mission — SIDE_INACTIVE ignored");
                    }
                    else if (!IsMissionOwnerPlayer(siLive.Ship, mission))
                    {
                        Console.WriteLine($"[sideinactive] {from} ship {siLive.Ship.Id}: not the mission owner — SIDE_INACTIVE refused");
                    }
                    else if (mission.Stage != MissionStage.NotStarted)
                    {
                        Console.WriteLine($"[sideinactive] ship {siLive.Ship.Id}: mission stage {mission.Stage} != NotStarted — SIDE_INACTIVE refused");
                    }
                    else
                    {
                        var side = mission.Sides.FirstOrDefault(s => s.Id == si.SideId);
                        if (side is null)
                        {
                            Console.WriteLine($"[sideinactive] ship {siLive.Ship.Id}: side {si.SideId} not in this mission — SIDE_INACTIVE ignored");
                        }
                        else
                        {
                            // The native bObjectModelCreated gate (FedSrv.CPP:7381,
                            // "!pfsMission->GetMissionDef()->misparms.bObjectModelCreated")
                            // is N/A here and deliberately skipped: native builds the
                            // sim object model on a later lobby-driven "generate"
                            // trigger, so there is a live window where that flag is
                            // false; this port's Mission.Assemble builds the object
                            // model once at boot, so the check has no reachable
                            // false-case in this architecture. Stated, not fabricated
                            // (no bObjectModelCreated-equivalent flag is invented).

                            // Only change the active flag if the side is currently
                            // empty; otherwise silently revert the requested value to
                            // the side's real current state (native's defensive
                            // re-check, FedSrv.CPP:7387-7390 — not a refusal).
                            bool hasPlayers = _peerShips.Any(kv =>
                                ReferenceEquals(kv.Value.Ship.Mission, mission) &&
                                kv.Value.Ship.SideId == si.SideId);
                            if (!hasPlayers)
                                side.Active = si.BActive;
                            else
                                si.BActive = side.Active;

                            // Keep the mission-wide AllowEmptyTeams (AET) flag in
                            // sync: AET is ON iff at least one side is inactive
                            // (FedSrv.CPP:7395-7414).
                            bool aet = mission.AllowEmptyTeams;
                            bool allActive = true;
                            foreach (var s in mission.Sides)
                                if (!s.Active) allActive = false;

                            bool mustChangeAet = (allActive && aet) || (!allActive && !aet);
                            if (mustChangeAet)
                            {
                                mission.AllowEmptyTeams = !aet;
                                si.BChangeAet = true;
                                si.BAet = mission.AllowEmptyTeams;
                            }
                            else
                                si.BChangeAet = false;

                            // Forward the (possibly-corrected) message whole-mission
                            // (native ForwardMessage(pfsMission->GetGroupMission(), ...,
                            // FM_GUARANTEED), FedSrv.CPP:7416).
                            int n = SendToMissionPeers(mission, FedPacket.Pack(new[] { si.Encode() }));
                            Console.WriteLine($"[sideinactive] ship {siLive.Ship.Id}: SIDE_INACTIVE side {si.SideId} bActive={si.BActive} bChangeAET={si.BChangeAet} bAET={si.BAet} -> {n} peer(s), Mission.AllowEmptyTeams={mission.AllowEmptyTeams}");

                            // #ALLY — clear this side's alliance membership too
                            // (UpdateAlliances(sid, NA), FedSrv.CPP:7424-7425).
                            UpdateAlliances(mission, si.SideId, -1);
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad SIDE_INACTIVE from {from}: {e.Message}");
                }
            }
            else if (fmid == AutoAccept.Fmid)   // FM_CS_AUTO_ACCEPT (Messages.h:413-416)
            {
                // P186 — the side's leader toggles auto-accept. Port of
                // FedSrv.CPP:6908-6936. Gate defensively: recompute the sender's
                // OWN side server-side (don't trust the message's iSide for the
                // gate), require the sender be that side's current leader, and
                // require the named iSide to BE that own side (native's
                // TrapHackBoot(iSide == sid)). The iSide == NA mission-owner-level
                // auto-accept-of-leaders variant is out of scope — log and ignore
                // (no such mission-wide concept exists in this port). On success:
                // relay whole-mission, set Side.AutoAccept, and when turning ON,
                // sweep P184's pending-request map for that side and seat each
                // requester via TryJoinSide (fsmission.cpp:4974-5011 SetAutoAccept).
                try
                {
                    var aa = AutoAccept.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var aaLive))
                    {
                        Console.WriteLine($"[autoaccept] {from}: AUTO_ACCEPT from a peer with no ship — ignored");
                    }
                    else if (aaLive.Ship.Mission is not Mission mission)
                    {
                        Console.WriteLine($"[autoaccept] {from} ship {aaLive.Ship.Id}: no mission — AUTO_ACCEPT ignored");
                    }
                    else if (aaLive.Ship.SideId is not { } aaSideId)
                    {
                        Console.WriteLine($"[autoaccept] {from} ship {aaLive.Ship.Id}: no side (NA) — AUTO_ACCEPT ignored (no mission-owner auto-accept-of-leaders in this port)");
                    }
                    else if (aa.SideId != aaSideId)
                    {
                        Console.WriteLine($"[autoaccept] {from} ship {aaLive.Ship.Id}: names side {aa.SideId}, own side {aaSideId} — ignored (must name own side)");
                    }
                    else
                    {
                        var aaSide = mission.Sides.FirstOrDefault(s => s.Id == aaSideId);
                        if (aaSide is null || aaSide.LeaderShipId != aaLive.Ship.Id)
                        {
                            Console.WriteLine($"[autoaccept] {from} ship {aaLive.Ship.Id}: not side {aaSideId}'s current leader — AUTO_ACCEPT refused");
                        }
                        else
                        {
                            // Forward-then-apply (native: ForwardMessage then SetAutoAccept).
                            SendToMissionPeers(mission, FedPacket.Pack(new[] { m }));
                            aaSide.AutoAccept = aa.FAutoAccept;
                            Console.WriteLine($"[autoaccept] ship {aaLive.Ship.Id}: AUTO_ACCEPT side {aaSideId} fAutoAccept={aa.FAutoAccept} whole-mission, Side.AutoAccept={aaSide.AutoAccept}");
                            if (aa.FAutoAccept)
                            {
                                SweepAutoAcceptPending(mission, aaSideId);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad AUTO_ACCEPT from {from}: {e.Message}");
                }
            }
            else if (fmid == ChangeTeamCiv.Fmid)   // FM_CS_CHANGE_TEAM_CIV (Messages.h:407-411)
            {
                // P186 — the side's leader changes their side's civilization.
                // Port of FedSrv.CPP:7344-7367 (native changes the leader's OWN
                // side, pside = pfsPlayer->GetSide(), so the named iSide must be
                // the sender's own side): refuse unless the sender is that side's
                // current leader AND the mission is STAGE_NOTSTARTED. On success:
                // relay whole-mission, resolve the target civilization from
                // Mission.Core (refuse — log and ignore — if civID is absent, the
                // native TrapHackBoot(pciv)), and set the side's civilization. The
                // random (random-faction) field is out of scope — ignored, always
                // a direct civ change. The per-player score-reset loop of native
                // SetSideCiv (fsmission.cpp:5274-5283) is out of scope (no
                // player-score-persistence system, the same gap that excluded
                // GAME_OVER_PLAYERS from [[allegiance-win-condition-plan]]).
                try
                {
                    var cc = ChangeTeamCiv.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var ccLive))
                    {
                        Console.WriteLine($"[changeciv] {from}: CHANGE_TEAM_CIV from a peer with no ship — ignored");
                    }
                    else if (ccLive.Ship.Mission is not Mission mission)
                    {
                        Console.WriteLine($"[changeciv] {from} ship {ccLive.Ship.Id}: no mission — CHANGE_TEAM_CIV ignored");
                    }
                    else if (ccLive.Ship.SideId is not { } ccSideId || cc.SideId != ccSideId)
                    {
                        Console.WriteLine($"[changeciv] {from} ship {ccLive.Ship.Id}: names side {cc.SideId}, own side {ccLive.Ship.SideId} — ignored (must change own side's civ)");
                    }
                    else
                    {
                        var ccSide = mission.Sides.FirstOrDefault(s => s.Id == ccSideId);
                        if (ccSide is null || ccSide.LeaderShipId != ccLive.Ship.Id)
                        {
                            Console.WriteLine($"[changeciv] {from} ship {ccLive.Ship.Id}: not side {ccSideId}'s current leader — CHANGE_TEAM_CIV refused");
                        }
                        else if (mission.Stage != MissionStage.NotStarted)
                        {
                            Console.WriteLine($"[changeciv] {from} ship {ccLive.Ship.Id}: mission stage {mission.Stage} != STAGE_NOTSTARTED — CHANGE_TEAM_CIV refused");
                        }
                        else if (!mission.Core.Civilizations.TryGetValue(cc.CivId, out var targetCiv))
                        {
                            Console.WriteLine($"[changeciv] {from} ship {ccLive.Ship.Id}: civilization {cc.CivId} not in core — CHANGE_TEAM_CIV ignored");
                        }
                        else
                        {
                            // Forward-then-apply (native: ForwardMessage then SetSideCiv).
                            SendToMissionPeers(mission, FedPacket.Pack(new[] { m }));
                            short oldCivId = ccSide.Civilization.CivilizationId;
                            ccSide.Civilization = targetCiv;
                            Console.WriteLine($"[changeciv] ship {ccLive.Ship.Id}: side {ccSideId} civ {oldCivId} -> {targetCiv.CivilizationId} whole-mission");
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad CHANGE_TEAM_CIV from {from}: {e.Message}");
                }
            }
            else if (fmid == ChangeAlliance.Fmid)   // FM_C_CHANGE_ALLIANCE (Messages.h:1017-1020)
            {
                // P190 — the mission owner changes an alliance while the mission
                // is still STAGE_NOTSTARTED. Port of FedSrv.CPP:7469-7481.
                // DIVERGENCE FROM NATIVE (deliberate, stated in the P190 report):
                // native performs NO owner check at all — it only gates on
                // Stage == NOTSTARTED and trusts the client UI to expose the
                // control only to the owner (the message's own comment says
                // "sent by game owner"). This port's established convention
                // (P184's DELPOSITIONREQ precedent, P186's IsMissionOwnerPlayer
                // gate) is to correct known native gaps rather than replicate
                // them, so CHANGE_ALLIANCE is additionally gated on
                // IsMissionOwnerPlayer here. Also refuse out-of-range sideID /
                // sideAlly (native silently drops them inside UpdateAlliances,
                // fsmission.cpp:5642-5647; here we log-and-ignore up front).
                try
                {
                    var ca = ChangeAlliance.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var caLive))
                    {
                        Console.WriteLine($"[alliances] {from}: CHANGE_ALLIANCE from a peer with no ship — ignored");
                    }
                    else if (caLive.Ship.Mission is not Mission mission)
                    {
                        Console.WriteLine($"[alliances] {from} ship {caLive.Ship.Id}: no mission — CHANGE_ALLIANCE ignored");
                    }
                    else if (!IsMissionOwnerPlayer(caLive.Ship, mission))
                    {
                        Console.WriteLine($"[alliances] {from} ship {caLive.Ship.Id}: not the mission owner — CHANGE_ALLIANCE refused (divergence: native has no owner gate)");
                    }
                    else if (mission.Stage != MissionStage.NotStarted)
                    {
                        Console.WriteLine($"[alliances] {from} ship {caLive.Ship.Id}: mission stage {mission.Stage} != STAGE_NOTSTARTED — CHANGE_ALLIANCE refused");
                    }
                    else if (!IsValidAllianceId(ca.SideId, mission.Sides.Count) || !IsValidAllianceId(ca.SideAlly, mission.Sides.Count))
                    {
                        Console.WriteLine($"[alliances] {from} ship {caLive.Ship.Id}: sideID={ca.SideId} or sideAlly={ca.SideAlly} out of range (must be NA or [0,{mission.Sides.Count})) — CHANGE_ALLIANCE ignored (native fsmission.cpp:5642-5647 silently returns)");
                    }
                    else
                    {
                        UpdateAlliances(mission, ca.SideId, ca.SideAlly);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad CHANGE_ALLIANCE from {from}: {e.Message}");
                }
            }
            else if (fmid == PlayerReady.Fmid)   // FM_CS_PLAYER_READY (Messages.h:380-384)
            {
                // P84: "Sent to everyone on team" (the native message's own
                // header comment) -- the client's own handler
                // (appmsg.cpp:2297-2312) only updates PlayerInfo.fReady and
                // fires the roster-changed sink when this message is
                // RECEIVED; a click on the Team Lobby's own JOIN control
                // never applies locally, it waits for this relay. Before this,
                // the server logged "unsupported fmid 45" and the click
                // appeared completely dead, live-confirmed on the real
                // client (P84). Relay the original bytes to every peer on the
                // sender's side, including the sender itself (its own client
                // is waiting on the same relay to update its own roster
                // entry) -- same shape as ChatTarget.Team in SelectChatTargets.
                try
                {
                    var ready = PlayerReady.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var readyLive))
                    {
                        Console.WriteLine($"[ready] {from}: PLAYER_READY from a peer with no ship — ignored");
                    }
                    else
                    {
                        var targets = _peerShips
                            .Where(kv => kv.Value.Ship.SideId == readyLive.Ship.SideId)
                            .Select(kv => kv.Key)
                            .ToArray();
                        byte[] packet = FedPacket.Pack(new[] { m });
                        foreach (var t in targets) SendPeer(t, packet);
                        Console.WriteLine($"[ready] {from} ship {ready.ShipId} side {sideStr(readyLive.Ship.SideId)} fReady={ready.FReady} -> {targets.Length} peer(s)");

                        // P180 — the real trigger point. This port's analogue of
                        // native's `PlayerReadyChange` (fsmission.cpp:5485-5489):
                        // a player's own ready flag just changed, so remember it
                        // and recompute the side's aggregate readiness, which may
                        // fire a TEAM_READY broadcast and, in turn, the ready-up
                        // auto-start. Runs AFTER the existing relay (the real
                        // client still needs the raw relay for its own roster
                        // UI — the message is sent to everyone on the team).
                        _playerReady[from] = ready.FReady;
                        RecomputeSideReady(readyLive.Ship);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad PLAYER_READY from {from}: {e.Message}");
                }
            }
            else if (fmid == Suicide.Fmid)   // FM_C_SUICIDE (Messages.h:590-591)
            {
                // The client's suicide request (P103). Decode is trivially empty
                // (header-only message); the real work is the gated death in
                // HandleSuicide. A malformed frame is reported and dropped like
                // every other inbound handler — never crashes the server.
                try
                {
                    Suicide.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var live))
                    {
                        Console.WriteLine($"[suicide] {from}: SUICIDE from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandleSuicide(from, live);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad SUICIDE from {from}: {e.Message}");
                }
            }
            else if (fmid == Logoff.Fmid)   // FM_CS_LOGOFF (Messages.h)
            {
                // P127 — the client's graceful logoff request. LOGOFF is a
                // zero-field CS message: the server echoes the exact same
                // (empty) message shape back to the same connection
                // (native FedSrv.CPP:5787-5802 mirrors it with
                // ForwardMessage(..., FM_GUARANTEED)), then releases the peer
                // through the EXACT same cleanup path an abrupt TCP drop uses
                // (PeerLeftEvt → ReleasePeer) — reusing it, not building a
                // second mechanism. Native's `delete pfsPlayer` deletes the
                // player object immediately; ReleasePeer is the port's
                // immediate effect: dropping the peer from _peerShips/_peerNames
                // AND (P128) retiring its ship (SHIP_DELETE SDR_LOGGEDOFF +
                // removed from the sector), so no zombie keeps flying. If the
                // client then closes its socket the transport's OnPeerLeft
                // fires and ReleasePeer runs again — an idempotent no-op.
                // No ship gate here: unlike SUICIDE, LOGOFF acts on the
                // connection itself, not the ship, so it holds even for a peer
                // that never validated/logged on.
                try
                {
                    Logoff.Decode(m);
                    SendPeer(from, FedPacket.Pack(new[] { m }));   // guaranteed echo back
                    ReleasePeer(from);
                    Console.WriteLine($"[logoff] peer {from} logged off (echoed, released)");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad LOGOFF from {from}: {e.Message}");
                }
            }
            else if (fmid == ViewClusterRequest.Fmid)   // FM_C_VIEW_CLUSTER (Messages.h)
            {
                // The docked-spectator/map-view request (P106): a docked player
                // previews another cluster. Gate and reply mirror the native
                // handler (FedSrv.CPP:6420-6454) — only reply when docked
                // (GetStation()!=NULL), only to the requesting peer. The real
                // work (cluster/target resolution, visibility gate, reply) is
                // in HandleViewCluster. A malformed frame is reported and
                // dropped like every other inbound handler.
                try
                {
                    var req = ViewClusterRequest.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var reqLive))
                    {
                        Console.WriteLine($"[viewcluster] {from}: VIEW_CLUSTER from a peer with no ship — ignored");
                    }
                    else if (reqLive.Ship.StationId is null)
                    {
                        Console.WriteLine($"[viewcluster] {from} ship {reqLive.Ship.Id}: not docked — ignored (gate: GetStation()!=NULL, FedSrv.CPP:6421)");
                    }
                    else
                    {
                        HandleViewCluster(from, reqLive, req);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad VIEW_CLUSTER from {from}: {e.Message}");
                }
            }
            else if (fmid == BoardShip.Fmid)   // FM_C_BOARD_SHIP (Messages.h:686-688)
            {
                // The client's board/leave request (P108): dual-purpose per
                // native (FedSrv.CPP:6385-6418) — sidParent == NA leaves the
                // current parent, any other value boards that ship. Only a
                // FAILURE sends a BOARD_NACK to the requester alone; success
                // rides the existing SHIP_STATUS periodic refresh (via
                // MakeOwnShipStatus). The real work (turret-slot search,
                // observer gate, money refund, parent/child/turret state
                // transition) is in HandleBoardShip. A malformed frame is
                // reported and dropped like every other inbound handler.
                try
                {
                    var board = BoardShip.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var boardLive))
                    {
                        Console.WriteLine($"[boardship] {from}: BOARD_SHIP from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandleBoardShip(from, boardLive, board);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad BOARD_SHIP from {from}: {e.Message}");
                }
            }
            else if (fmid == PromoteRequest.Fmid)   // FM_C_PROMOTE (Messages.h:979-981)
            {
                // The commanding ship's request to promote a boarded turret
                // gunner into its own seat (P140). Port of FedSrv.CPP:6353-6382
                // case FM_C_PROMOTE: stage gate, then walk the requesting
                // ship's OWN boarded children for the one at the requested
                // turret mount, promote it, and broadcast PROMOTE(189) sector-
                // wide. A mount matching no real child is a silent no-op.
                try
                {
                    var promoteReq = PromoteRequest.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var promoteLive))
                    {
                        Console.WriteLine($"[promote] {from}: PROMOTE from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandlePromote(from, promoteLive, promoteReq);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad PROMOTE from {from}: {e.Message}");
                }
            }
            else if (fmid == SwapPart.Fmid)   // FM_CS_SWAP_PART (Messages.h:273-278)
            {
                // A flying, unboarded ship's request to move the mounted part
                // at (etOld, mountOld) to mountNew, swapping it with a different
                // same-type part already there if one is present (P143). Port of
                // FedSrv.CPP:6459-6501 case FM_CS_SWAP_PART: stage gate +
                // unboarded gate, then the swap-or-move (Ship.TrySwapPart),
                // then re-broadcast SWAP_PART(34) sector-wide with shipID
                // filled in. A source mount holding no part is a silent no-op.
                try
                {
                    var swapReq = SwapPart.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var swapLive))
                    {
                        Console.WriteLine($"[swappart] {from}: SWAP_PART from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandleSwapPart(from, swapLive, swapReq);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad SWAP_PART from {from}: {e.Message}");
                }
            }
            else if (fmid == Reload.Fmid)   // FM_CS_RELOAD (Messages.h:843-846)
            {
                // A flying, unboarded ship's request to reload its cargo hold
                // into its live pools / equipped launchers (P192). Port of
                // FedSrv.CPP:6504-6653 case FM_CS_RELOAD: stage gate + unboarded
                // gate + the per-entry pack/launcher transfer loop
                // (Ship.TryApplyReloadEntry), then re-broadcast RELOAD(129)
                // sector-wide with shipID filled in.
                try
                {
                    var reloadReq = Reload.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var reloadLive))
                    {
                        Console.WriteLine($"[reload] {from}: RELOAD from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandleReload(from, reloadLive, reloadReq);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad RELOAD from {from}: {e.Message}");
                }
            }
            else if (fmid == DropPart.Fmid)   // FM_CS_DROP_PART (Messages.h:266-270)
            {
                // A flying, unboarded ship's request to drop the mounted part at
                // (et, mount) into space, leaving a real part-type treasure (P144).
                // Port of FedSrv.CPP:6296-6330 case FM_CS_DROP_PART: stage gate +
                // unboarded gate, then the removal (Ship.TryRemovePart), then
                // re-broadcast DROP_PART(33) sector-wide with shipID filled in,
                // then — unless the dropped part is an empty (Amount==0) Pack —
                // create the treasure and send FM_S_TREASURE(28) sector-wide.
                // A source mount holding no part is a silent no-op.
                try
                {
                    var dropReq = DropPart.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var dropLive))
                    {
                        Console.WriteLine($"[droppart] {from}: DROP_PART from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandleDropPart(from, dropLive, dropReq);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad DROP_PART from {from}: {e.Message}");
                }
            }
            else if (fmid == SetWingId.Fmid)   // FM_CS_SET_WINGID (Messages.h:705-708)
            {
                // A player tagging a ship with a wing-group id, relayed side-wide
                // (P146). Port of FedSrv.CPP:5312-5344 case FM_CS_SET_WINGID:
                // mission-not-null gate ONLY (native has no STAGE_STARTED gate),
                // then self-targeting-only (shipID must be the sender's own ship
                // — the leader-retags-another-ship branch is unreachable without
                // a team-leader system), a wingID range check, bCommanded forced
                // false, and a side-wide broadcast + server-side WingId mutation.
                try
                {
                    var swReq = SetWingId.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var swLive))
                    {
                        Console.WriteLine($"[setwingid] {from}: SET_WINGID from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandleSetWingId(from, swLive, swReq);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad SET_WINGID from {from}: {e.Message}");
                }
            }
            else if (fmid == TreasureAck.Fmid)   // FM_C_TREASURE_ACK (Messages.h:877-879)
            {
                // The client's mount decision on an offered part-treasure pickup
                // (P145). Port of FedSrv.CPP:6094-6152 case FM_C_TREASURE_ACK:
                // stage gate + pending-offer gate, then a real mount merges the
                // part into an existing same-type part at that slot (or mounts it
                // fresh) and broadcasts ADD_PART(164) sector-wide; the c_mountNA
                // sentinel declines and re-creates the treasure at the ship's
                // current position; either way the pending offer is cleared.
                try
                {
                    var ack = TreasureAck.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var ackLive))
                    {
                        Console.WriteLine($"[treasureack] {from}: TREASURE_ACK from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandleTreasureAck(from, ackLive, ack);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad TREASURE_ACK from {from}: {e.Message}");
                }
            }
            else if (fmid == RipcordRequest.Fmid)   // FM_C_RIPCORD_REQUEST (Messages.h:951-953)
            {
                // The client's ripcord request (P111): start, re-target, or
                // cancel a ripcord countdown toward a chosen sector. Gates and
                // state machine in HandleRipcordRequest / RequestRipcord —
                // the port of FedSrv.CPP:7715-7728 and FedSrv.CPP:13319-13378.
                try
                {
                    var req = RipcordRequest.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var ripLive))
                    {
                        Console.WriteLine($"[ripcord] {from}: RIPCORD_REQUEST from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandleRipcordRequest(from, ripLive, req);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad RIPCORD_REQUEST from {from}: {e.Message}");
                }
            }
            else if (fmid == BucketDonate.Fmid)   // FM_C_BUCKET_DONATE (Messages.h:488-491)
            {
                // A client donating money into one of its side's construction
                // buckets (P119). Port of FedSrv.CPP:7137-7211
                // case FM_C_BUCKET_DONATE: gates on STAGE_STARTED, resolves the
                // side's bucket by id, validates the pooled treasury covers the
                // donation (the honest single-pool equivalent of the native's
                // per-player pfsPlayer->GetMoney() >= moneyGiven, FedSrv.CPP:7148
                // — this port has no per-player wallets, only Side.Money), caps
                // the investment at what the bucket still needs (refunding the
                // excess by simply never deducting it), then broadcasts
                // BUCKET_STATUS to the whole side.
                try
                {
                    var donate = BucketDonate.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var donateLive))
                    {
                        Console.WriteLine($"[bucketdonate] {from}: BUCKET_DONATE from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandleBucketDonate(from, donateLive, donate);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad BUCKET_DONATE from {from}: {e.Message}");
                }
            }
            else if (fmid == OrderChange.Fmid)   // FM_CS_ORDER_CHANGE (Messages.h:648-654)
            {
                // P148 — Stage 2 "stay docked" slice of the ORDER_CHANGE(93)
                // drone-command handler. Port of FedSrv.CPP:7264-7299:
                // STAGE_STARTED gate, target resolved by shipID on the commander's
                // own side (falling back to the sender's own ship), and — only
                // when the target IsNpc AND docked AND commandID==c_cidDoNothing —
                // sets Ship.StayDocked = true. Every other branch (launch,
                // return-to-base, generic SetCommand fallback) is explicitly out
                // of scope this task and no-ops with a log.
                try
                {
                    var ocReq = OrderChange.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var ocLive))
                    {
                        Console.WriteLine($"[orderchange] {from}: ORDER_CHANGE from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandleOrderChange(from, ocLive, ocReq);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad ORDER_CHANGE from {from}: {e.Message}");
                }
            }
            else if (fmid == FireExpendableRequest.Fmid)   // FM_C_FIRE_EXPENDABLE (Messages.h:592-596)
            {
                // A flying, unboarded ship firing its mounted chaff dispenser
                // (P182). Port of FedSrv.CPP:5609-5750 case FM_C_FIRE_EXPENDABLE:
                // stage gate + unboarded gate, then resolve the mounted dispenser
                // at mount 0 of the requested equipment type, refuse on Amount==0,
                // deploy chaff + run the missile-retarget loop (Ship.DeployChaff),
                // decrement the dispenser's ammo, and broadcast FE(87) +
                // CREATE_CHAFF(109) + MISSILE_SPOOFED(110)×n + END_SPOOFING(111)
                // sector-wide. The mine/probe branch of native is explicitly out
                // of scope (separate, already-solved DeployMine/DeployProbe).
                try
                {
                    var feReq = FireExpendableRequest.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var feLive))
                    {
                        Console.WriteLine($"[fireexpendable] {from}: FIRE_EXPENDABLE from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandleFireExpendable(from, feLive, feReq);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad FIRE_EXPENDABLE from {from}: {e.Message}");
                }
            }
            else if (fmid == ReqPingData.Fmid)   // FM_C_REQPINGDATA (Messages.h:1012-1013)
            {
                // P200 — the client's "send me every connected player's
                // latency roster" trigger (the w0dk4 player-pings feature).
                // Port of FedSrv.CPP:7743-7808 case FM_C_REQPINGDATA. Unlike
                // almost every other C message, native answers only the
                // REQUESTER (every SendMessages targets
                // pfsPlayer->GetConnection()), and it iterates EVERY
                // connection on the whole server with a live player — NOT
                // scoped to a mission or side. The 5-second debounce is
                // native CFMConnection::PingQuery (messagecore.h:217-223).
                try
                {
                    _ = ReqPingData.Decode(m);   // zero-field trigger; decode validates cbmsg=4
                    HandleReqPingData(from);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad REQPINGDATA from {from}: {e.Message}");
                }
            }
            else
            {
                Console.WriteLine($"[wire] from {from}: unsupported fmid {fmid} — ignored at headless milestone");
            }

            static string sideStr(short? s) => s is { } v ? v.ToString() : "(none)";
        }
    }

    /// <summary>
    /// Handle a client <c>FM_C_SUICIDE(80)</c> for a peer that has a ship. This
    /// is the P103 port of <c>FedSrv.CPP:6335-6350</c> <c>case
    /// FM_C_SUICIDE</c>:
    /// <list type="bullet">
    /// <item><b>Gate: mission started.</b> The native refuses unless
    /// <c>pfsMission-&gt;GetStage() == STAGE_STARTED</c>
    /// (<c>FedSrv.CPP:6337</c>). We keep it — <see cref="Ship.Mission"/>
    /// must exist and be at <see cref="MissionStage.Started"/>.</item>
    /// <item><b>Gate: the ship is actually flying.</b> The native refuses
    /// unless <c>GetCluster() != NULL</c> (<c>FedSrv.CPP:6338</c>) — i.e. the
    /// ship is in a sector, not parked. In this port a placed-but-docked ship
    /// retains <see cref="Ship.Sector"/> (the dock seam sets
    /// <see cref="Ship.StationId"/> but does not clear Sector), so
    /// "has a cluster" faithfully means placed <b>and</b> not docked:
    /// <c>Sector != null &amp;&amp; StationId == null</c>. Docked/off-sector
    /// ships are a no-op — no kill, no broadcast — matching the native gate.</item>
    /// <item><b>Deliberate relaxation (documented, not a silent drop): the
    /// child-ship / cheat gate collapses.</b> Native also requires
    /// <c>GetParentShip() != NULL</c> (a boarded/turret gunner) <em>or</em>
    /// <c>CanCheat()</c> (admin). This port has <em>neither</em> — no
    /// boarding/child-ship concept (Phase 3 cluster E) and no admin/cheat
    /// concept at all. Following the P100 precedent (a gate collapses to
    /// always-true when its own prerequisite doesn't exist yet), any placed
    /// ship may suicide. Revisit only when boarding lands.</item>
    /// <item><b>Kill path reused, not rebuilt.</b> Triggering uses the exact
    /// seam a combat death's lethal branch raises
    /// (<c>Ship.ReceiveDamage</c> → <c>Mission.Site.KillShipEvent</c>,
    /// <c>Ship.cs:650</c>) — a direct <c>KillShipEvent</c> with a NULL
    /// launcher, precisely what the native suicide does (launcher NULL,
    /// amount 1.0). No parallel kill mechanism is introduced; the server's
    /// <see cref=\"ServerHost.ShipKilled\"/> wiring therefore broadcasts the
    /// usual <c>KILL_SHIP(105)</c> and the new <c>SHIP_DELETE(20)</c>
    /// alongside. As in native, the suicide itself does not drain the hull —
    /// the death notice is what a suicide produces.</item>
    /// </list>
    /// Runs on the sim thread (the dispatch is drained inside
    /// <see cref=\"Tick\"/>, like every other inbound handler), so it may call
    /// the kill seam and touch <c>_peerShips</c> without extra locking.
    /// </summary>
    private void HandleSuicide(ConnectionId from, LiveShip live)
    {
        var ship = live.Ship;
        var mission = ship.Mission;
        if (mission is null || mission.Stage != MissionStage.Started)
        {
            Console.WriteLine($"[suicide] {from} ship {ship.Id}: " +
                              $"{(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — refused");
            return;
        }
        if (ship.Sector is null || ship.StationId is not null)
        {
            Console.WriteLine($"[suicide] {from} ship {ship.Id}: " +
                              $"{(ship.Sector is null ? "no cluster" : "docked")} — refused (gate: GetCluster(), FedSrv.CPP:6338)");
            return;
        }
        // Relaxed child/cheat gate (see doc comment): ANY placed ship may suicide.
        Console.WriteLine($"[suicide] {from} ship {ship.Id} — re-triggering the combat kill path (KillShipEvent, launcher NULL)");
        mission.Site.KillShipEvent(mission.LastUpdate, ship, null, 1.0f, ship.Position, Vector3.Zero);
    }

    /// <summary>
    /// P180 — recompute one side's aggregate readiness after a player's own
    /// ready flag changed and, if it actually flipped, broadcast the change and
    /// (possibly) auto-start the mission. The port of native's
    /// <c>CheckForSideAllReady</c> (<c>fsmission.cpp:5492-5529</c>), reached
    /// from the <c>PLAYER_READY</c> relay handler as the analogue of native's
    /// <c>PlayerReadyChange</c> (<c>fsmission.cpp:5485-5489</c>).
    /// <para>The real aggregation rules (<c>fsmission.cpp:5501-5525</c>): a side
    /// with fewer than <c>nMinPlayersPerTeam</c> connected players is forced
    /// not-ready; a force-readied side is ready regardless; otherwise every
    /// connected player-piloted ship on the side must be individually ready.
    /// This port has <b>no</b> <c>nMinPlayersPerTeam</c> equivalent (confirmed
    /// absent from <c>GameConfig</c>/<c>ServerConfig</c>), so the readiness
    /// floor is <b>at least one connected player</b>, and there is no force-ready
    /// (that is the leader-only <c>FORCE_TEAM_READY</c>, out of scope). Each
    /// <c>_peerShips</c> entry is a player-piloted ship by construction (one
    /// owned ship per connected peer), so "every connected player on the side
    /// ready" reduces to: side has ≥1 peer AND every such peer's recorded
    /// ready flag is true.</para>
    /// <para><b>Fix (post-landing review):</b> scoped to <c>ship.Mission</c> —
    /// a server can host multiple concurrent missions
    /// (<c>ServerConfig.Games</c> is a list), and side ids are small integers
    /// assigned per-mission, not globally unique, so an unscoped scan would
    /// incorrectly fold another mission's same-numbered side into this one's
    /// readiness. Matches the per-mission scoping idiom P178's
    /// <c>CheckForVictoryByInactiveSides</c> already established.</para></summary>
    private void RecomputeSideReady(Ship ship)
    {
        if (ship.SideId is not { } sideId)
        {
            Console.WriteLine($"[ready] ship {ship.Id} has no side — side-readiness recompute skipped");
            return;
        }
        if (ship.Mission is not Mission mission)
            return;

        var side = mission.Sides.FirstOrDefault(s => s.Id == sideId);
        if (side is null)
        {
            Console.WriteLine($"[ready] side {sideId} (for ship {ship.Id}) not found in mission — recompute aborted");
            return;
        }

        var peersOnSide = _peerShips
            .Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission) && kv.Value.Ship.SideId == sideId)
            .Select(kv => kv.Key)
            .ToArray();

        bool newReady;
        if (peersOnSide.Length == 0)
        {
            // Floor: <1 connected player → forced not-ready (this port's stand-in
            // for native's nMinPlayersPerTeam, fsmission.cpp:5501-5503).
            newReady = false;
        }
        else if (side.ForceReady)
        {
            // P184 — the team leader forced this side ready (FORCE_TEAM_READY(47)).
            // Bypass the per-player check entirely, exactly native
            // CheckForSideAllReady's force-ready branch (fsmission.cpp:5508-5509),
            // still subject to the >=1 connected-player floor above.
            newReady = true;
        }
        else
        {
            newReady = peersOnSide.All(k => _playerReady.TryGetValue(k, out var r) && r);
        }

        if (side.Ready != newReady)
        {
            side.Ready = newReady;
            BroadcastTeamReady(mission, sideId, newReady);

            // After any aggregate change, check whether the whole mission can now
            // start (Domingo's substituted auto-start policy — native leaves the
            // actual start decision to an external admin layer this port lacks).
            TryAutoStart(mission);
        }
    }

    /// <summary>P180 — broadcast <c>TEAM_READY(46)</c> for a side whose aggregate
    /// ready flag just changed, to the WHOLE mission (every connected peer *on
    /// that mission*, regardless of side) — the same whole-mission
    /// <c>_peerShips</c> sweep idiom the <c>GAME_OVER</c>/<c>DESTROY_TREASURE</c>
    /// broadcasts use, matching native's <c>GetGroupMission()</c> audience in
    /// <c>SetReady</c> (<c>fsmission.cpp:5455-5462</c>). Only ever called when
    /// the flag actually flipped (the caller guards on <c>side.Ready !=
    /// newReady</c>), so no duplicate spam on every recompute — exactly
    /// native's <c>if (fReady != GetReady(iSide))</c> (<c>fsmission.cpp:5450</c>).
    /// <para><b>Fix (post-landing review):</b> takes <paramref name="mission"/>
    /// and filters to peers on that mission — a server can host multiple
    /// concurrent missions, and this message must not reach peers on an
    /// unrelated mission (their client would misinterpret an unrelated
    /// mission's side id as their own). Matches P178's established per-mission
    /// broadcast scoping.</para></summary>
    private void BroadcastTeamReady(Mission mission, short sideId, bool fReady)
    {
        var msg = new TeamReady { SideId = sideId, FReady = fReady };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        int n = 0;
        foreach (var kv in _peerShips)
        {
            if (!ReferenceEquals(kv.Value.Ship.Mission, mission)) continue;   // whole-mission, not whole-server
            SendPeer(kv.Key, packet);
            n++;
        }
        Console.WriteLine($"[ready] side {sideId} aggregate readiness {fReady} -> TEAM_READY(46) whole-mission to {n} peer(s)");
    }

    /// <summary>P180/P188 — the ready-up auto-start. If the mission is still
    /// <see cref="MissionStage.NotStarted"/> and every side that currently has
    /// at least one connected player is aggregate-ready, begin the real start
    /// countdown (P188): the mission enters <see cref="MissionStage.Starting"/>,
    /// a <c>SET_START_TIME(14)</c> countdown announcement is broadcast, and
    /// once the countdown elapses on a later tick it crosses into
    /// <see cref="MissionStage.Started"/> with the <c>ENTER_GAME(128)</c>
    /// broadcast (AdvanceStartCountdowns → CompleteStartCountdown →
    /// BroadcastEnterGameToMission). This is Domingo's deliberate substitute for
    /// native's external admin/Zone start trigger (<c>AdminGame.cpp</c>), which
    /// this port has ruled N/A — not a literal port of native, per the task's
    /// explicit decision. Native's own start always passes through
    /// <c>STAGE_STARTING</c> (<c>StartGame</c> → <c>StartCountdown</c>,
    /// fsmission.cpp:2231-2239), so routing the auto-start through the countdown
    /// is the faithful form; the P188 writeup records why this stage upgrades
    /// P180's originally-instant auto-start.
    /// <para><b>Fix (post-landing review):</b> the occupied-side check below
    /// is scoped to <paramref name="mission"/> — side ids are per-mission, not
    /// globally unique, so an unscoped scan could see another mission's
    /// same-numbered side's peers and wrongly conclude this mission's side is
    /// "occupied" (or wrongly skip starting because of an unrelated mission's
    /// unready side of the same id).</para></summary>
    private void TryAutoStart(Mission mission)
    {
        if (mission.Stage != MissionStage.NotStarted)
            return;   // already started (or over) — nothing to do
        if (!IsAllOccupiedSidesReady(mission))
            return;   // at least one occupied side is not yet ready

        // P188 — route the ready-up auto-start through the real countdown
        // stage instead of jumping straight to Started. The ENTER_GAME
        // broadcast now happens once the countdown elapses
        // (AdvanceStartCountdowns → CompleteStartCountdown →
        // BroadcastEnterGameToMission), not here; StartCountdown emits the
        // SET_START_TIME countdown announcement and sets Stage = Starting.
        Console.WriteLine("[ready] ALL occupied sides ready — mission auto-start begins countdown");
        StartCountdown(mission, BriefingCountdownSeconds);
    }

    /// <summary>P180/P188 — is every side of <paramref name="mission"/> that
    /// currently has at least one connected player aggregate-ready? The port's
    /// own simplified readiness gate, shared by the ready-up auto-start
    /// (<see cref="TryAutoStart"/>) and the manual <c>START_GAME</c> trigger —
    /// a deliberate stand-in for native's full <c>FAllReady</c> (whose
    /// team-balance/rank logic needs <c>nMaxPlayersPerTeam</c>/a rank system
    /// this port lacks). A side with no players is irrelevant to the decision.</summary>
    private bool IsAllOccupiedSidesReady(Mission mission)
    {
        foreach (var side in mission.Sides)
        {
            bool hasPlayers = _peerShips.Any(kv => ReferenceEquals(kv.Value.Ship.Mission, mission) && kv.Value.Ship.SideId == side.Id);
            if (hasPlayers && !side.Ready)
                return false;   // at least one occupied side is not yet ready
        }
        return true;
    }

    /// <summary>P188 — begin (or reset) the start countdown for a mission, the
    /// port of native <c>StartCountdown</c>'s wire+stage effect (fsmission.cpp:
    /// 1926-1938). Only valid from <see cref="MissionStage.NotStarted"/>: set
    /// <c>Stage = Starting</c>, record the double-seconds sim target
    /// (<c>now + countdownSeconds</c>), and broadcast <c>SET_START_TIME(14)</c>
    /// whole-mission. Native's remaining <c>StartCountdown</c> side-effects
    /// (<c>GenerateMission</c>, briefing-text broadcast, fsmission.cpp:1940-1980)
    /// are OUT OF SCOPE — this port's bucket/mission generation already happens
    /// once at <c>Mission.Assemble</c> and briefing text is a separate feature.
    /// The eventual <c>Starting → Started</c> transition + <c>ENTER_GAME</c>
    /// broadcast is driven by <see cref="AdvanceStartCountdowns"/> on a later
    /// tick once the target time passes.</summary>
    private void StartCountdown(Mission mission, float countdownSeconds)
    {
        if (mission.Stage != MissionStage.NotStarted)
            return;   // native allows re-starting from STAGE_STARTING too, but
                      // this port has no re-trigger path — refuse

        mission.Stage = MissionStage.Starting;
        double target = _simNow + countdownSeconds;
        _startCountdownDeadline[mission] = target;

        var msg = new SetStartTime { TimeStart = (uint)(target * 1000.0) };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        int n = SendToMissionPeers(mission, packet);
        Console.WriteLine($"[start] mission #{_missions.IndexOf(mission)} countdown begun ({countdownSeconds}s) -> STAGE_STARTING, SET_START_TIME(14) whole-mission to {n} peer(s)");
    }

    /// <summary>P188 — the per-tick countdown driver: any mission in
    /// <see cref="MissionStage.Starting"/> whose recorded target start time
    /// has passed crosses into <see cref="MissionStage.Started"/>, reusing the
    /// existing P180 <c>ENTER_GAME</c> broadcast (<see cref="BroadcastEnterGameToMission"/>)
    /// — the exact same Started-transition path the auto-start used to trigger
    /// directly, now deferred by the countdown. Runs on the sim thread right
    /// after the mission tick in <see cref="SimPass"/>.</summary>
    private void AdvanceStartCountdowns(double now)
    {
        foreach (var m in _missions)
        {
            if (m.Stage != MissionStage.Starting) continue;
            if (!_startCountdownDeadline.TryGetValue(m, out var target)) continue;
            if (now < target) continue;
            CompleteStartCountdown(m);
        }
    }

    private void CompleteStartCountdown(Mission mission)
    {
        mission.Stage = MissionStage.Started;
        _startCountdownDeadline.Remove(mission);
        Console.WriteLine($"[start] mission #{_missions.IndexOf(mission)} countdown elapsed -> STAGE_STARTED, broadcasting ENTER_GAME");
        BroadcastEnterGameToMission(mission);
    }

    /// <summary>P180 — broadcast <c>ENTER_GAME(128)</c> to every connected peer on
    /// the mission at the ready-up auto-start transition — the same whole-mission
    /// <c>_peerShips</c> sweep the other broadcasts use, matching native
    /// <c>StartGame</c>'s <c>SendMessages(GetGroupRealSides(), ...)</c> audience
    /// (<c>fsmission.cpp:2267-2269</c>). A <b>new</b> broadcast call, distinct
    /// from the per-joiner join-sync send (which is suppressed while the mission
    /// is NotStarted so this transition broadcast is the one that delivers
    /// ENTER_GAME to peers who joined before the start).
    /// <para><b>Fix (post-landing review):</b> takes <paramref name="mission"/>
    /// and filters to peers on that mission — a peer on an unrelated,
    /// still-<c>NotStarted</c> mission must not receive an ENTER_GAME that
    /// doesn't apply to them. Matches P178's established per-mission broadcast
    /// scoping.</para></summary>
    private void BroadcastEnterGameToMission(Mission mission)
    {
        var packet = FedPacket.Pack(new[] { new EnterGame().Encode() });
        int n = 0;
        foreach (var kv in _peerShips)
        {
            if (!ReferenceEquals(kv.Value.Ship.Mission, mission)) continue;   // whole-mission, not whole-server
            SendPeer(kv.Key, packet);
            n++;
        }
        Console.WriteLine($"[entergame] mission start -> ENTER_GAME(128) whole-mission to {n} peer(s)");
    }

    /// <summary>P183 — send a pre-packed packet to every currently connected
    /// peer <em>on the given mission</em> (the same whole-mission
    /// <c>_peerShips</c> sweep idiom <see cref="BroadcastTeamReady"/> uses,
    /// matching native's <c>GetGroupMission()</c> audience) and return how many
    /// received it. Shared by all five leadership/join/quit broadcasts so the
    /// multi-mission scoping (the P180/P178 fixed bug — never regress an
    /// unscoped sweep) lives in one place.</summary>
    private int SendToMissionPeers(Mission mission, byte[] packet)
    {
        int n = 0;
        foreach (var kv in _peerShips)
        {
            if (!ReferenceEquals(kv.Value.Ship.Mission, mission)) continue;   // whole-mission, not whole-server
            SendPeer(kv.Key, packet);
            n++;
        }
        return n;
    }

    /// <summary>P183 — broadcast <c>JOIN_SIDE(125)</c> announcing that
    /// <paramref name="shipId"/> joined <paramref name="sideId"/>, to the whole
    /// mission (native <c>AddPlayerToSide</c>'s always-sent announcement,
    /// fsmission.cpp:621-624).</summary>
    private void BroadcastJoinSide(Mission mission, short shipId, short sideId)
    {
        var packet = FedPacket.Pack(new[] { new JoinSide { ShipId = shipId, SideId = sideId }.Encode() });
        int n = SendToMissionPeers(mission, packet);
        Console.WriteLine($"[join] ship {shipId} -> side {sideId}: JOIN_SIDE(125) whole-mission to {n} peer(s)");
    }

    /// <summary>P183 — broadcast <c>SET_TEAM_LEADER(121)</c> announcing
    /// <paramref name="shipId"/> is now <paramref name="sideId"/>'s team
    /// leader, to the whole mission (native fsmission.cpp:626-635 / 1082-1087).
    /// Also re-announces a side's existing leader when that side's leader is
    /// promoted to mission owner (native fsmission.cpp:1083-1086).</summary>
    private void BroadcastSetTeamLeader(Mission mission, short sideId, short shipId)
    {
        var packet = FedPacket.Pack(new[] { new SetTeamLeader { SideId = sideId, ShipId = shipId }.Encode() });
        int n = SendToMissionPeers(mission, packet);
        Console.WriteLine($"[lead] side {sideId} new team leader ship {shipId}: SET_TEAM_LEADER(121) whole-mission to {n} peer(s)");
    }

    /// <summary>P183 — broadcast <c>SET_MISSION_OWNER(123)</c> announcing
    /// <paramref name="shipId"/> (the leader of <paramref name="sideId"/>) is
    /// now the mission owner, to the whole mission (native fsmission.cpp:
    /// 637-643 / 1088-1094). A mission owner is always also that side's leader.</summary>
    private void BroadcastSetMissionOwner(Mission mission, short sideId, short shipId)
    {
        var packet = FedPacket.Pack(new[] { new SetMissionOwner { SideId = sideId, ShipId = shipId }.Encode() });
        int n = SendToMissionPeers(mission, packet);
        Console.WriteLine($"[own] side {sideId} new mission owner ship {shipId}: SET_MISSION_OWNER(123) whole-mission to {n} peer(s)");
    }

    /// <summary>P183 — broadcast <c>QUIT_MISSION(124)</c> announcing that
    /// <paramref name="shipId"/> left the mission entirely, to the whole
    /// mission. Emitted on the disconnect path (native
    /// <c>RemovePlayerFromMission</c>, fsmission.cpp:482-488).</summary>
    private void BroadcastQuitMission(Mission mission, short shipId, QuitSideReason reason)
    {
        var packet = FedPacket.Pack(new[] { new QuitMission { ShipId = shipId, Reason = reason }.Encode() });
        int n = SendToMissionPeers(mission, packet);
        Console.WriteLine($"[quit] ship {shipId} left mission (reason {reason}): QUIT_MISSION(124) whole-mission to {n} peer(s)");
    }

    /// <summary>P183 — broadcast <c>QUIT_SIDE(126)</c> announcing that
    /// <paramref name="shipId"/> left a side while staying in the mission, to
    /// the whole mission (native <c>RemovePlayerFromSide</c>, fsmission.cpp:
    /// 1130-1137). Emitted on the quit-half of a same-mission side switch.</summary>
    private void BroadcastQuitSide(Mission mission, short shipId, QuitSideReason reason)
    {
        var packet = FedPacket.Pack(new[] { new QuitSide { ShipId = shipId, Reason = reason }.Encode() });
        int n = SendToMissionPeers(mission, packet);
        Console.WriteLine($"[quitside] ship {shipId} left side (reason {reason}): QUIT_SIDE(126) whole-mission to {n} peer(s)");
    }

    // ═══════════════════════════ P187 — ballot / voting system ═══════════════════════════
    // Port of native CFSMission::m_ballots + the four Ballot subclasses
    // (fsmission.h:85-180, fsmission.cpp:5817-6194) and the #chat command
    // triggers (FedSrv.CPP:5170-5200). State lives per-mission on the Server
    // (the P184 pending-join-request convention), driven to resolution from
    // SimPass. See docs/BALLOT_SYSTEM_P187.md.

    /// <summary>Get (creating if absent) the pending-ballot list for a mission.</summary>
    private List<PendingBallot> BallotsOf(Mission mission)
    {
        if (!_ballots.TryGetValue(mission, out var list))
            _ballots[mission] = list = new List<PendingBallot>();
        return list;
    }

    /// <summary>Whether a ballot of the given type is already in flight for the
    /// mission — the port of native CFSMission::HasBallots (fsmission.cpp:
    /// 1225-1235): only one in-flight ballot of each type at a time.</summary>
    private bool HasBallotOfType(Mission mission, BallotType type) =>
        BallotsOf(mission).Any(b => b.Type == type);

    /// <summary>Native's ballot-command match: cbMessage &gt; strlen(prefix) and
    /// the text starts with the prefix, case-insensitively (FedSrv.CPP:5170-5200
    /// uses <c>_strnicmp</c> over the first strlen(prefix) bytes). On a .NET
    /// string (no trailing NUL) that is Length &gt;= prefix.Length and a
    /// case-insensitive StartsWith.</summary>
    private static bool IsBallotCommand(string message, string prefix) =>
        message.Length >= prefix.Length &&
        message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Handle a chat line that may be a ballot command. Returns
    /// <c>true</c> iff the line WAS a ballot command — in which case it is NOT
    /// forwarded as chat, mirroring native setting <c>bForward = false</c>
    /// (FedSrv.CPP:5195-5199) whether the ballot was created or refused.
    /// Gates (native FedSrv.CPP:5164-5168): mission must be Started and the
    /// sender seated on a real side.</summary>
    private bool TryHandleBallotCommand(ConnectionId from, Ship ship, string message)
    {
        if (ship.Mission is not { } mission) return false;
        if (mission.Stage != MissionStage.Started) return false;
        if (ship.SideId is not { } sideId) return false;
        var side = mission.Sides.FirstOrDefault(s => s.Id == sideId);
        if (side is null) return false;

        if (IsBallotCommand(message, "#resign"))
        {
            if (!HasBallotOfType(mission, BallotType.Resign))
                CreateBallotFromChat(from, ship, BallotType.Resign);
            else
                SendBallotReply(from, ship.Id, "Resign vote already in progress...");
            return true;
        }

        if (IsBallotCommand(message, "#draw"))
        {
            if (!HasBallotOfType(mission, BallotType.OfferDraw))
                CreateBallotFromChat(from, ship, BallotType.OfferDraw);
            else
                SendBallotReply(from, ship.Id, "Draw vote already in progress...");
            return true;
        }

        if (IsBallotCommand(message, "#mutiny"))
        {
            // "leader cant mutiny" (FedSrv.CPP:5186): a side's leader mutinying
            // against themselves is silently refused — no ballot, no reply.
            if (side.LeaderShipId != ship.Id)
            {
                if (!HasBallotOfType(mission, BallotType.Mutiny))
                    CreateBallotFromChat(from, ship, BallotType.Mutiny);
                else
                    SendBallotReply(from, ship.Id, "Mutiny vote already in progress...");
            }
            return true;
        }

        return false;
    }

    /// <summary>Private chat reply to one sender — the port of native
    /// SendChatf(NULL, CHAT_INDIVIDUAL_NOFILTER, shipID, NA, text)
    /// (FedSrv.CPP:5180-5197), sent only to the offending connection.</summary>
    private void SendBallotReply(ConnectionId to, short shipId, string text)
    {
        var msg = new ChatMessage
        {
            Message = text,
            SidSender = shipId,
            ChatTarget = (int)ChatTarget.IndividualNoFilter,
            OidRecipient = shipId,
        };
        SendPeer(to, FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>Create a player-initiated ballot (#resign/#draw/#mutiny) scoped
    /// to the proposer's own side — the port of the three native constructors'
    /// Ballot::Init(pfsInitiator, ...) (fsmission.cpp:5899-5940). Eligible
    /// voters are every seated player on the proposer's side; the proposer's
    /// own vote is recorded in favor after the BALLOT is announced.</summary>
    private void CreateBallotFromChat(ConnectionId from, Ship ship, BallotType type)
    {
        Mission mission = ship.Mission!;
        short sideId = ship.SideId!.Value;
        string name = _peerNames.TryGetValue(from, out var n) ? n : $"Ship {ship.Id}";

        var (proposal, text) = type switch
        {
            BallotType.Mutiny => ($"{name}'s proposal to mutiny", $"{name} has proposed to mutiny.  "),
            BallotType.Resign => ($"{name}'s proposal to resign", $"{name} has proposed resigning.  "),
            _                  => ($"{name}'s proposal to offer a draw", $"{name} has proposed offering a draw.  "),
        };

        var ballot = new PendingBallot
        {
            Id = _nextBallotId++,
            Type = type,
            InitiatorShipId = ship.Id,
            VotingSideId = sideId,
            Proposal = proposal,
            BallotText = text,
            Expiration = SimNow + 30.0,   // native c_fVoteDuration = 30.0f (fsmission.cpp:5894)
        };

        // Eligible: every seated player on the proposer's side (native iterates
        // the side's ships, fsmission.cpp:5907-5920).
        foreach (var kv in _peerShips)
        {
            if (!ReferenceEquals(kv.Value.Ship.Mission, mission)) continue;
            if (kv.Value.Ship.SideId != sideId) continue;
            ballot.AddEligibleVoter(kv.Value.Ship.Id, sideId);
        }

        BallotsOf(mission).Add(ballot);
        BroadcastBallot(mission, ballot);

        // The proposer votes in favor (native CastVote(pfsInitiator, true), fsmission.cpp:5939).
        ballot.TryCastVote(ship.Id, sideId, true);
    }

    /// <summary>Create the mission-wide AcceptDraw ballot after an OfferDraw
    /// passes — the port of native AcceptDrawBallot::Init(CFSSide*, ...)
    /// (fsmission.cpp:5990-6037). Eligible voters are every seated player on the
    /// OTHER sides (the offering side is excluded — it already agreed); the
    /// BALLOT is broadcast to the whole mission (GetGroupRealSides).</summary>
    private void CreateAcceptDrawBallot(Mission mission, short offeringSideId)
    {
        var offeringSide = mission.Sides.FirstOrDefault(s => s.Id == offeringSideId);
        string sideName = offeringSide?.Name ?? $"side{offeringSideId}";

        var ballot = new PendingBallot
        {
            Id = _nextBallotId++,
            Type = BallotType.AcceptDraw,
            InitiatorShipId = -1,
            VotingSideId = -1,                // mission-wide
            ExcludedSideId = offeringSideId,  // the offering side doesn't vote on acceptance
            Proposal = $"{sideName}'s offer of a draw",
            BallotText = $"{sideName} has offered a draw.  ",
            Expiration = SimNow + 30.0,
        };

        foreach (var kv in _peerShips)
        {
            if (!ReferenceEquals(kv.Value.Ship.Mission, mission)) continue;
            if (kv.Value.Ship.SideId is not { } sid) continue;
            if (sid == offeringSideId) continue;   // native: sideIDPlayer != sideIDInitiator
            ballot.AddEligibleVoter(kv.Value.Ship.Id, sid);
        }

        BallotsOf(mission).Add(ballot);
        BroadcastBallot(mission, ballot);
    }

    /// <summary>Broadcast the BALLOT(17) announcement of a new ballot. A
    /// player-initiated ballot goes to the voting side's peers only (native
    /// sends to the side's group, fsmission.cpp:5937); the mission-wide
    /// AcceptDraw goes to the whole mission (GetGroupRealSides,
    /// fsmission.cpp:5997).</summary>
    private void BroadcastBallot(Mission mission, PendingBallot ballot)
    {
        var msg = new Ballot
        {
            BallotText = ballot.BallotText,
            OtInitiator = ballot.IsMissionWide ? (short)IgcObjectType.Side : (short)IgcObjectType.Ship,
            OidInitiator = ballot.IsMissionWide ? ballot.ExcludedSideId : ballot.InitiatorShipId,
            BallotId = (short)ballot.Id,
            TimeExpiration = (uint)(ballot.Expiration * 1000.0),  // double-seconds → native ms Time
            HideToLeader = ballot.Type == BallotType.Mutiny,      // KGJV #110 (fsmission.h:174): only mutiny hides the proposer
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        if (ballot.IsMissionWide)
        {
            int n = SendToMissionPeers(mission, packet);
            Console.WriteLine($"[ballot] #{ballot.Id} {ballot.Type} whole-mission -> {n} peer(s)");
        }
        else
        {
            int n = 0;
            foreach (var kv in _peerShips)
            {
                if (!ReferenceEquals(kv.Value.Ship.Mission, mission)) continue;
                if (kv.Value.Ship.SideId != ballot.VotingSideId) continue;
                SendPeer(kv.Key, packet);
                n++;
            }
            Console.WriteLine($"[ballot] #{ballot.Id} {ballot.Type} side {ballot.VotingSideId} -> {n} peer(s)");
        }
    }

    /// <summary>Advance every pending ballot of a mission one resolution step —
    /// the port of native CFSMission::Update's ballot loop (fsmission.cpp:
    /// 2585-2592). A ballot that should resolve now (expiration passed OR every
    /// vote in) is tallied, its pass/fail announced, its type effect applied on
    /// a pass, then dropped. A cancelled ballot is dropped unresolved.</summary>
    private void ResolveBallots(Mission mission, double now)
    {
        var list = BallotsOf(mission);
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var ballot = list[i];
            if (ballot.Canceled)
            {
                list.RemoveAt(i);
                continue;
            }
            if (!ballot.ShouldResolve(now)) continue;

            bool passed = ballot.HasPassed();
            AnnounceBallotResult(mission, ballot, passed);
            if (passed)
                ApplyBallotEffect(mission, ballot);
            list.RemoveAt(i);
        }
    }

    /// <summary>Apply the OnPassed side effect of a passing ballot — the port of
    /// the four native OnPassed methods (fsmission.cpp:6100-6194). No
    /// CANCEL_BALLOT is emitted on resolution: the live native server never
    /// sends it (see the CancelBallot codec / the report).</summary>
    private void ApplyBallotEffect(Mission mission, PendingBallot ballot)
    {
        switch (ballot.Type)
        {
            case BallotType.Mutiny:    ApplyMutiny(mission, ballot);    break;
            case BallotType.Resign:    ApplyResign(mission, ballot);    break;
            case BallotType.OfferDraw: ApplyOfferDraw(mission, ballot); break;
            case BallotType.AcceptDraw:ApplyAcceptDraw(mission, ballot);break;
        }
    }

    /// <summary>Mutiny passing transfers the side's leadership to the ballot's
    /// initiator — re-validated: the initiator must still be on that side when
    /// the vote closes (leadership could have changed mid-vote). Port of native
    /// MutinyBallot::OnPassed (fsmission.cpp:6117-6168), reusing the P183
    /// <c>Side.LeaderShipId</c> mutation + <c>BroadcastSetTeamLeader</c>. The
    /// native donation-transfer/wing bookkeeping is N/A here (no donations,
    /// no squads in this port).</summary>
    private void ApplyMutiny(Mission mission, PendingBallot ballot)
    {
        if (mission.Stage != MissionStage.Started) return;
        short sideId = ballot.VotingSideId;
        var side = mission.Sides.FirstOrDefault(s => s.Id == sideId);
        if (side is null) return;

        // The initiator must still be on the side when the vote closes (native
        // re-checks pfssNewLeader->GetSide() == m_pside).
        var initiator = _peerShips.Values
            .Select(l => l.Ship)
            .FirstOrDefault(s => s.Id == ballot.InitiatorShipId
                              && ReferenceEquals(s.Mission, mission)
                              && s.SideId == sideId);
        if (initiator is null) return;

        if (side.LeaderShipId == initiator.Id) return;   // already leader — nothing to do

        // P197 — the deposed leader's wallet passes to the new leader as a
        // one-time gift, and the deposed leader's future donations point at the
        // new leader — the port of native MutinyBallot::OnPassed
        // (fsmission.cpp:6121-6136). ORDER MATTERS (and is read from the live
        // function): clear the new leader's own donate target silently first
        // (bSend=false), capture the old leader's wallet, then re-point the OLD
        // leader at the new leader with that wallet as the gift (bSend=true →
        // AUTODONATE(116) side-wide). Native retargets ONLY the deposed leader
        // here — it does NOT loop over the side re-pointing every other donor
        // onto the new leader (that retarget-everyone loop only runs when a
        // player LEAVES a side, fsmission.cpp:1106-1115, not on mutiny). This
        // is a deliberate divergence from the task brief's "retarget every
        // prior donor" phrasing, which does not match the verified native code.
        var oldLeaderShip = LeaderShip(mission, sideId);
        var oldLeaderLive = oldLeaderShip is null ? null : LiveOf(oldLeaderShip);
        var newLeaderLive = LiveOf(initiator);
        if (oldLeaderLive is not null && newLeaderLive is not null)
        {
            SetAutoDonate(newLeaderLive, null, 0, bSend: false);   // clear the new leader's own target, silent
            int money = oldLeaderLive.Wallet.Money;
            SetAutoDonate(oldLeaderLive, initiator, money, bSend: true);  // gift + re-point the old leader
            // The old leader's client still shows his old balance (the gift rides
            // the AUTODONATE amount, which the client applies to the recipient,
            // not to the payer) — native compensates with a MONEY_CHANGE(-money)
            // sent to HIM alone (fsmission.cpp:6131-6136).
            if (ConnOf(oldLeaderShip!) is { } oldConn)
                SendPeer(oldConn, FedPacket.Pack(new[] { new MoneyChange { DMoney = -money, SidTo = oldLeaderShip!.Id, SidFrom = Na }.Encode() }));
        }

        side.LeaderShipId = initiator.Id;
        BroadcastSetTeamLeader(mission, sideId, initiator.Id);
    }

    // ════════════════════ P197 — per-player money & auto-donate ════════════════════

    /// <summary>Resolve a <see cref="Ship"/> to its live peer record (the
    /// reverse of <c>_peerShips</c>' forward lookup), or <c>null</c> when the
    /// ship isn't a live player ship (an NPC or disconnected peer — the same
    /// <c>CFSShip::IsPlayer()</c> guard native applies before treating a
    /// donation target as a player).</summary>
    private LiveShip? LiveOf(Ship ship)
    {
        foreach (var kv in _peerShips)
            if (ReferenceEquals(kv.Value.Ship, ship)) return kv.Value;
        return null;
    }

    /// <summary>Resolve a <see cref="Ship"/> to the <see cref="ConnectionId"/>
    /// of the peer piloting it, or <c>null</c> when not a live player ship.</summary>
    private ConnectionId? ConnOf(Ship ship)
    {
        foreach (var kv in _peerShips)
            if (ReferenceEquals(kv.Value.Ship, ship)) return kv.Key;
        return null;
    }

    /// <summary>The <see cref="Side.LeaderShipId"/> owner as a live
    /// <see cref="Ship"/> on <paramref name="mission"/>/<paramref name="sideId"/>,
    /// or <c>null</c> when the side is leaderless or its leader isn't live.</summary>
    private Ship? LeaderShip(Mission mission, short sideId)
    {
        var side = mission.Sides.FirstOrDefault(s => s.Id == sideId);
        if (side is null || side.LeaderShipId is not { } lid) return null;
        foreach (var l in _peerShips.Values)
            if (ReferenceEquals(l.Ship.Mission, mission) && l.Ship.SideId == sideId && l.Ship.Id == lid)
                return l.Ship;
        return null;
    }

    /// <summary>Port of <c>CFSPlayer::SetAutoDonate</c>
    /// (<c>FedSrv/fsship.cpp:1293-1352</c>) — the real cycle-resolution
    /// algorithm. Sets <paramref name="donor"/>'s
    /// <see cref="PlayerWallet.AutoDonateTarget"/> to <paramref name="target"/>
    /// (or clears it when <paramref name="target"/> is null), applying native's
    /// chain-redirection and mutual-donation-unset rules, optionally transferring
    /// a one-time <paramref name="amount"/> gift, and (when <paramref name="bSend"/>)
    /// broadcasting <c>AUTODONATE(116)</c> to the donor's side group. Every
    /// branch is read from the live native function; <c>bSend=false</c> is the
    /// silent variant native uses for intermediate clears and leadership plumbing
    /// (no immediate broadcast; native's explicit side-group flush after a
    /// <c>bSend=false</c> batch is reproduced by the caller passing <c>bSend:true</c>
    /// for each member of that batch, giving the identical wire effect).</summary>
    private void SetAutoDonate(LiveShip donor, Ship? target, int amount, bool bSend)
    {
        Ship? resolved = null;
        if (target is not null)
        {
            var targetLive = LiveOf(target);
            var targetDonatesTo = targetLive?.Wallet.AutoDonateTarget;   // who THEY currently donate to
            if (targetDonatesTo is null)
            {
                // they donate to nobody → target them directly
                resolved = target;
            }
            else
            {
                // they were already donating to someone else — resolve the chain
                var targetNew = LiveOf(targetDonatesTo);
                if (targetNew is not null && ReferenceEquals(targetNew.Ship, donor.Ship))
                {
                    // They're already donating to US — stop that first (silent,
                    // no messages), then donate to them anyway
                    // (fsship.cpp:1313-1321).
                    if (targetLive is not null) targetLive.Wallet.AutoDonateTarget = null;
                    resolved = target;
                }
                else
                {
                    // donate to whoever THEY were donating to (chain resolution,
                    // fsship.cpp:1322-1326).
                    resolved = targetDonatesTo;
                }
            }

            // An optional one-time up-front gift when establishing a NEW target
            // (amount == 0 means no gift; separate from the AUTODONATE(112)
            // amount==-1 toggle sentinel handled in the dispatch).
            if (amount != 0)
            {
                var receiver = LiveOf(resolved!);
                if (receiver is not null)
                {
                    receiver.Wallet.Money += amount;
                    donor.Wallet.Money -= amount;    // fsship.cpp:1336-1338
                }
            }
        }
        // else: clearing the target entirely — no gift (native's if(amount!=0)
        // sits inside the if(pplayer) block, so a clear never transfers money).

        donor.Wallet.AutoDonateTarget = resolved;

        if (bSend)
            BroadcastAutoDonate(donor.Ship, donor.Ship.Id, resolved?.Id ?? Na, amount);
    }

    /// <summary>Broadcast <c>AUTODONATE(116)</c> to the donor's <em>side</em>
    /// group (native <c>CFSSide::FromIGC(GetSide())->GetGroup()</c>,
    /// fsship.cpp:1350-1352) — not the whole mission.</summary>
    private void BroadcastAutoDonate(Ship donor, short sidDonateBy, short sidDonateTo, int amount)
    {
        if (donor.Mission is not Mission mission || donor.SideId is not { } sideId) return;
        var msg = new AutoDonateUpdate { SidDonateBy = sidDonateBy, SidDonateTo = sidDonateTo, Amount = amount };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        var targets = _peerShips
            .Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission) && kv.Value.Ship.SideId == sideId)
            .Select(kv => kv.Key)
            .ToArray();
        foreach (var t in targets) SendPeer(t, packet);
        Console.WriteLine($"[autodonate] ship {sidDonateBy} side {sideId} -> AUTODONATE(116) target={sidDonateTo} amount={amount} to {targets.Length} peer(s)");
    }

    /// <summary>Resolve a wire <c>sidDonateTo</c> (as sent in
    /// <c>AUTODONATE(112)</c>) to a live <em>same-side</em> player ship, or
    /// <c>null</c> when it is <c>NA</c>, the actor's own ship, or not a live
    /// same-side player (native FedSrv.CPP:5535-5544: <c>GetShipFromID</c> +
    /// <c>IsPlayer()</c> + <c>pside == GetSide()</c>).</summary>
    private Ship? ResolveDonateTarget(LiveShip actor, short sidDonateTo)
    {
        if (sidDonateTo == Na || sidDonateTo == actor.Ship.Id) return null;
        foreach (var l in _peerShips.Values)
            if (l.Ship.Id == sidDonateTo
                && ReferenceEquals(l.Ship.Mission, actor.Ship.Mission)
                && l.Ship.SideId == actor.Ship.SideId)
                return l.Ship;
        return null;
    }

    /// <summary>Port of <c>PickNewLeader</c> (<c>FedSrv.CPP:13981-14024</c>): among
    /// the side's ships, count who each one's donation points at, ignoring any
    /// donation that points at the current <paramref name="ignoreLeader"/>; return
    /// the ship with the most such votes, but only if that count strictly exceeds
    /// <paramref name="maxVotes"/> (the leader's own current vote count). Keeps the
    /// first ship to reach each new maximum, matching native's <c>&gt;</c> comparison
    /// (ties resolve to whichever is encountered first).</summary>
    private Ship? PickNewLeader(IEnumerable<Ship> sideShips, Ship ignoreLeader, int maxVotes)
    {
        Ship? pshipMax = null;
        var votes = new Dictionary<Ship, int>();
        foreach (var s in sideShips)
        {
            var t = LiveOf(s)?.Wallet.AutoDonateTarget;
            if (t is null) continue;
            if (ReferenceEquals(t, ignoreLeader)) continue;   // skip donations pointing at the current leader
            votes.TryGetValue(t, out var c);
            c++;
            votes[t] = c;
            if (c > maxVotes) { maxVotes = c; pshipMax = t; }
        }
        return pshipMax;
    }

    /// <summary>
    /// P197 — the <c>FM_C_AUTODONATE(112)</c> dispatch, port of
    /// <c>FedSrv.CPP:5516-5658</c> <c>case FM_C_AUTODONATE</c>: a two-mode
    /// request (set-own-target-with-optional-gift, or the leader-only
    /// <c>amount==-1</c> toggle of a subordinate's donation) followed by the
    /// donation-confidence leadership check. Runs on the sim thread, so no extra
    /// locking is needed.
    /// </summary>
    private void HandleAutoDonate(ConnectionId from, LiveShip live, AutoDonate req)
    {
        var ship = live.Ship;
        if (ship.Mission is not Mission mission) return;
        if (mission.Stage != MissionStage.Started)
        {
            Console.WriteLine($"[autodonate] {from} ship {ship.Id}: stage {mission.Stage} != STAGE_STARTED — AUTODONATE(112) refused (FedSrv.CPP:5517-5518)");
            return;
        }
        if (ship.SideId is not { } sideId) return;

        // Can't donate more than you have (only relevant to the gift path; the
        // amount==-1 toggle sentinel is never below a non-negative Money).
        if (live.Wallet.Money < req.Amount)
            req.Amount = live.Wallet.Money;

        // amount == -1 is the LEADER-ONLY "toggle someone else's donation" mode.
        if (req.Amount == -1 && !IsSideLeader(mission, ship))
        {
            Console.WriteLine($"[autodonate] {from} ship {ship.Id}: AUTODONATE amount=-1 toggle from a non-leader — refused (FedSrv.CPP:5531-5532)");
            return;
        }

        Ship? pfsDonate = ResolveDonateTarget(live, req.SidDonateTo);

        LiveShip? changed;
        if (req.Amount == -1)
        {
            if (pfsDonate is null)
            {
                Console.WriteLine($"[autodonate] {from} ship {ship.Id}: toggle target {req.SidDonateTo} not a live same-side player — ignored (FedSrv.CPP:5548)");
                return;
            }
            var donateLive = LiveOf(pfsDonate)!;
            // toggle: if the subordinate currently donates to someone, stop them;
            // otherwise set them to donate to the leader (the sender).
            if (donateLive.Wallet.AutoDonateTarget is not null)
                SetAutoDonate(donateLive, null, 0, bSend: true);
            else
                SetAutoDonate(donateLive, ship, 0, bSend: true);
            changed = donateLive;
        }
        else
        {
            SetAutoDonate(live, pfsDonate, req.Amount, bSend: true);
            changed = live;
        }

        // Donation-confidence leadership check (native FedSrv.CPP:5560-5658):
        // if the changed player now donates somewhere other than the current
        // leader, and more side members donate to that other player than to the
        // leader, leadership passes to the player with the most donations.
        if (changed is null || changed.Ship.Mission is not Mission cm) return;
        if (changed.Ship.SideId is not { } csid) return;
        var cside = cm.Sides.FirstOrDefault(s => s.Id == csid);
        if (cside is null || cside.LeaderShipId is null) return;
        var leader = LeaderShip(cm, csid);
        if (leader is null || ReferenceEquals(changed.Wallet.AutoDonateTarget, leader)) return;

        int cLeader = 0, cOther = 0;
        foreach (var l in _peerShips.Values)
        {
            if (!ReferenceEquals(l.Ship.Mission, cm) || l.Ship.SideId != csid) continue;
            var t = l.Wallet.AutoDonateTarget;
            if (t is null) continue;
            if (ReferenceEquals(t, leader)) cLeader++;
            else cOther++;
        }
        if (cLeader < cOther)
        {
            var sideShips = _peerShips.Values
                .Where(l => ReferenceEquals(l.Ship.Mission, cm) && l.Ship.SideId == csid)
                .Select(l => l.Ship)
                .ToList();
            var newLeader = PickNewLeader(sideShips, leader, cLeader);
            if (newLeader is not null && cside.LeaderShipId != newLeader.Id)
            {
                cside.LeaderShipId = newLeader.Id;
                BroadcastSetTeamLeader(cm, csid, newLeader.Id);
                Console.WriteLine($"[autodonate] donation-confidence: side {csid} leadership passed to ship {newLeader.Id}");
            }
        }
    }

    /// <summary>
    /// P199 — the <c>FM_CS_REQUEST_MONEY(182)</c> dispatch, port of
    /// <c>FedSrv.CPP:5361-5406</c> <c>case FM_CS_REQUEST_MONEY</c>: a player asks
    /// whoever actually holds the team's money to send them some. Crucially this
    /// handler <b>never transfers money</b> — it only <em>forwards</em> the request
    /// (with <c>shipidRequest</c> filled in) to exactly one recipient connection:
    /// the sender's own auto-donate target when that player's wallet holds at
    /// least <paramref name="req"/>.Amount, otherwise the richest <em>other</em>
    /// teammate on the sender's side (native's <c>&gt;</c> comparison from
    /// <c>moneyMax = amount - 1</c> ⇒ the first ship to reach each new maximum
    /// wins — ties resolve to whichever is encountered first). If no recipient
    /// resolves (empty/solo side, nobody with funds) the request is silently
    /// dropped — no reply, no error. The resolved recipient is expected to answer
    /// with its own follow-up (e.g. an <c>AUTODONATE(112)</c> gift). Runs on the
    /// sim thread, so no extra locking is needed.
    /// </summary>
    private void HandleRequestMoney(ConnectionId from, LiveShip live, RequestMoney req)
    {
        var ship = live.Ship;
        if (ship.Mission is not Mission mission) return;
        if (mission.Stage != MissionStage.Started)
        {
            Console.WriteLine($"[requestmoney] {from} ship {ship.Id}: stage {mission.Stage} != STAGE_STARTED — REQUEST_MONEY refused (FedSrv.CPP:5363-5364)");
            return;
        }
        if (ship.SideId is not { } sideId) return;

        // Native uses TrapHackBoot(req.Amount > 0) here (FedSrv.CPP:5368), which
        // would DISCONNECT a client lying about a non-positive amount; per this
        // port's standing convention that harsh trap is softened to log-and-ignore.
        if (req.Amount <= 0)
        {
            Console.WriteLine($"[requestmoney] {from} ship {ship.Id}: amount {req.Amount} <= 0 — REQUEST_MONEY ignored (no forward)");
            return;
        }

        // Native gates REQUEST_MONEY behind CFSPlayer::DecrementChatBudget — the
        // chat-spam rate limiter (FedSrv.CPP:5370). This port has NO chat
        // budget/rate-limit mechanism at all (nothing equivalent exists for
        // CHATMESSAGE; the standing pooled-design port never introduced one), so
        // the gate is DELIBERATELY SKIPPED here — the omission is logged, not
        // fabricated. See docs/REQUEST_MONEY_P199.md.

        Ship? pshipAD = live.Wallet.AutoDonateTarget;
        var adLive = pshipAD is null ? null : LiveOf(pshipAD);
        if (adLive is null || adLive.Wallet.Money < req.Amount)
        {
            // Not auto-donating to anyone holding enough — find the richest
            // teammate instead, EXCLUDING the sender (FedSrv.CPP:5378-5395).
            int moneyMax = req.Amount - 1;
            Ship? richest = null;
            foreach (var l in _peerShips.Values)
            {
                if (!ReferenceEquals(l.Ship.Mission, mission) || l.Ship.SideId != sideId) continue;
                if (ReferenceEquals(l.Ship, ship)) continue;   // exclude the sender
                if (l.Wallet.Money > moneyMax)
                {
                    moneyMax = l.Wallet.Money;
                    richest = l.Ship;
                }
            }
            pshipAD = richest;
        }

        if (pshipAD is null)
        {
            // Side empty / nobody has any money / solo — silently dropped
            // (FedSrv.CPP:5398 has no else: no reply, no error).
            Console.WriteLine($"[requestmoney] {from} ship {ship.Id}: no recipient resolves — REQUEST_MONEY silently dropped");
            return;
        }

        // Fill in the requester's ship id and forward to the single recipient.
        var forward = new RequestMoney { Amount = req.Amount, ShipIdRequest = ship.Id, HidFor = req.HidFor };
        if (ConnOf(pshipAD) is { } conn)
        {
            SendPeer(conn, FedPacket.Pack(new[] { forward.Encode() }));
            Console.WriteLine($"[requestmoney] {from} ship {ship.Id}: REQUEST_MONEY({req.Amount}, hidFor {req.HidFor}) forwarded to ship {pshipAD.Id}");
        }
    }

    /// <summary>
    /// P203 — the <c>FM_C_PLAYER_DONATE(67)</c> dispatch, port of
    /// <c>FedSrv.CPP:7213-7262</c> <c>case FM_C_PLAYER_DONATE</c>: a manual,
    /// direct player-to-player gift, deliberately independent of
    /// <c>AUTODONATE(112)</c>'s standing relationship. Runs on the sim thread,
    /// so no extra locking is needed.
    /// </summary>
    private void HandlePlayerDonate(ConnectionId from, LiveShip live, PlayerDonate req)
    {
        var ship = live.Ship;
        if (ship.Mission is not Mission mission) return;
        if (mission.Stage != MissionStage.Started)
        {
            Console.WriteLine($"[playerdonate] {from} ship {ship.Id}: stage {mission.Stage} != STAGE_STARTED — PLAYER_DONATE refused (FedSrv.CPP:7215-7216)");
            return;
        }
        if (ship.SideId is not { } donorSideId)
        {
            Console.WriteLine($"[playerdonate] {from} ship {ship.Id}: no side yet — PLAYER_DONATE ignored");
            return;
        }

        // Softened TrapHackBoot(moneyGiven > 0) (FedSrv.CPP:7220) — log-and-ignore,
        // no forward at all (no bounce-back).
        if (req.MoneyGiven <= 0)
        {
            Console.WriteLine($"[playerdonate] {from} ship {ship.Id}: moneyGiven {req.MoneyGiven} <= 0 — PLAYER_DONATE ignored (no forward)");
            return;
        }
        // Softened TrapHackBoot(sender money >= moneyGiven) (FedSrv.CPP:7221) — the
        // initial hard gate; log-and-ignore, no forward.
        if (live.Wallet.Money < req.MoneyGiven)
        {
            Console.WriteLine($"[playerdonate] {from} ship {ship.Id}: wallet {live.Wallet.Money} < given {req.MoneyGiven} — PLAYER_DONATE ignored (no forward)");
            return;
        }

        // Resolve the recipient ship by its wire id (native CFSShip::GetShipFromID,
        // FedSrv.CPP:7223; this port's global id resolver FindShipAnySide).
        Ship? pfssTo = FindShipAnySide(req.ShipID, mission);

        var mc = new MoneyChange { DMoney = req.MoneyGiven };

        bool sameSide = pfssTo is not null && ship.SideId == pfssTo.SideId;
        bool bAllied = pfssTo is not null && AlliedSides(mission, donorSideId, pfssTo.SideId);
        // The SECOND (redundant-looking) money check — native's defensive re-test
        // against a race where the sender's wallet changed between the TrapHackBoot
        // gate and this point (FedSrv.CPP:7234). Port both faithfully.
        if (pfssTo is not null && (sameSide || bAllied) && live.Wallet.Money >= req.MoneyGiven)
        {
            // Real transfer (FedSrv.CPP:7236-7240).
            mc.SidTo = req.ShipID;
            mc.SidFrom = ship.Id;
            live.Wallet.Money -= req.MoneyGiven;
            if (LiveOf(pfssTo) is { } toLive)
                toLive.Wallet.Money += req.MoneyGiven;
            // else: an NPC target has no per-player wallet to credit (the port's
            // LiveOf returns null for non-players); the sender is still debited and
            // the broadcasts still fire, matching native's "transfer happened"
            // semantics for an ineligible-but-same-side/allied recipient.

            Console.WriteLine($"[playerdonate] {from} ship {ship.Id} -> ship {req.ShipID}: MONEY_CHANGE dMoney={req.MoneyGiven} sidTo={req.ShipID} sidFrom={ship.Id} (real transfer)");

            // Cross-side/allied: ALSO tell the recipient's side group. Native fires
            // this inside `if (bAllied)` (FedSrv.CPP:7242-7243). Because native
            // IsideIGC::AlliedSides returns TRUE for same-side too, the literal code
            // sends to the recipient's group even on a same-side gift — but there
            // that group is the SAME as the sender's group, i.e. a redundant
            // duplicate send of the identical message to the identical peer set. The
            // author's intent (and this brief) is a second, distinct audience only
            // when the gift CROSSED sides; this port sends the recipient's side
            // group only in that case, collapsing the same-side duplicate (identical
            // net wire effect). Documented in docs/PLAYER_DONATE_P203.md.
            if (bAllied && pfssTo.SideId != donorSideId)
                SendMoneyChangeToSideGroup(mission, pfssTo.SideId!.Value, mc);

            // Always tell the sender's own side group (native prcp, FedSrv.CPP:7245).
            SendMoneyChangeToSideGroup(mission, donorSideId, mc);
        }
        else
        {
            // No-op bounce-back: bad target, or the sender's money changed since the
            // hard gate (FedSrv.CPP:7247-7256). No wallets move; the echo informs the
            // sender alone. sidTo = the sender's own ship, sidFrom = NA.
            mc.SidTo = ship.Id;
            mc.SidFrom = Na;
            SendPeer(from, FedPacket.Pack(new[] { mc.Encode() }));
            Console.WriteLine($"[playerdonate] {from} ship {ship.Id}: target {req.ShipID} ineligible — no-op MONEY_CHANGE echo to sender alone (sidFrom=NA)");
        }
    }

    /// <summary>Native <c>IsideIGC::AlliedSides</c> (<c>igc.h:4297-4304</c>),
    /// adapted to the port's <see cref="Side.AllyGroup"/> model (P190): two
    /// sides are allied iff they are the same side (ids equal), or both carry
    /// the same non-null alliance-group value. Mirrors native exactly — same
    /// side → true; either side absent → false; donor's group NA (null) →
    /// false; otherwise equal group values.</summary>
    private static bool AlliedSides(Mission mission, short? a, short? b)
    {
        if (a == b) return true;                      // side1==side2
        if (a is null || b is null) return false;     // NULL guards
        var sa = mission.Sides.FirstOrDefault(s => s.Id == a.Value);
        var sb = mission.Sides.FirstOrDefault(s => s.Id == b.Value);
        if (sa is null || sb is null) return false;
        if (sa.AllyGroup is not { } ga) return false; // side1->GetAllies()==NA
        return ga == sb.AllyGroup;
    }

    /// <summary>Broadcast a <c>MONEY_CHANGE(60)</c> to every peer seated on the
    /// given mission/side — the port of native <c>g.fm.SendMessages(
    /// CFSSide::FromIGC(...)-&gt;GetGroup(), ...)</c>. Scoped by BOTH mission and
    /// side id (side ids repeat across world missions), the same mission+side
    /// filter every side-group broadcast in this arc uses.</summary>
    private void SendMoneyChangeToSideGroup(Mission mission, short sideId, MoneyChange mc)
    {
        var targets = _peerShips
            .Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission) && kv.Value.Ship.SideId == sideId)
            .Select(kv => kv.Key)
            .ToArray();
        if (targets.Length == 0) return;
        byte[] packet = FedPacket.Pack(new[] { mc.Encode() });
        foreach (var t in targets) SendPeer(t, packet);
        Console.WriteLine($"[moneychange] mission side {sideId} -> MONEY_CHANGE(60) dMoney={mc.DMoney} sidTo={mc.SidTo} sidFrom={mc.SidFrom} to {targets.Length} peer(s)");
    }

    /// <summary>P197 — send <c>AUTODONATE(116)</c> + <c>SET_MONEY(115)</c> together,
    /// once per <em>other</em> player on a side, to a joining peer — the port of the
    /// native <c>SendMissionInfo</c> money/autodonate loop
    /// (<c>fsmission.cpp:4276-4302</c>), reached from the join path so a newly
    /// seated player learns every teammate's donate relationship and wallet.</summary>
    private void SyncMoneyStateToJoiner(Mission mission, short sideId, ConnectionId? joinerConn)
    {
        if (joinerConn is not { } conn) return;
        var side = mission.Sides.FirstOrDefault(s => s.Id == sideId);
        if (side is null) return;
        foreach (var kv in _peerShips)
        {
            var live = kv.Value;
            if (!ReferenceEquals(live.Ship.Mission, mission) || live.Ship.SideId != sideId) continue;
            if (joinerConn == kv.Key) continue;   // don't tell the joiner about themself
            var ad = new AutoDonateUpdate
            {
                SidDonateBy = live.Ship.Id,
                SidDonateTo = live.Wallet.AutoDonateTarget?.Id ?? Na,
                Amount = 0,
            };
            var sm = new SetMoney { Money = live.Wallet.Money, ShipId = live.Ship.Id };
            SendPeer(conn, FedPacket.Pack(new[] { ad.Encode(), sm.Encode() }));
        }
        Console.WriteLine($"[syncmoney] side {sideId} -> AUTODONATE(116)+SET_MONEY(115) per teammate to joining peer");
    }

    /// <summary>P197 — the leader-change donation retarget (native
    /// fsmission.cpp:1106-1115, the <c>else</c> branch of the leadership
    /// succession block in the player-left-side path): first, if the new leader
    /// was itself auto-donating to the departing player, clear that relationship
    /// (avoiding a self-referential state); then every player who was
    /// auto-donating to the departing player is retargeted to the new leader.
    /// Runs with <c>bSend:true</c> for each member so every changed relationship
    /// is broadcast as <c>AUTODONATE(116)</c> to the side group (native gathers
    /// the same batch with <c>bSend=false</c> and flushes it once to the side
    /// group — identical net wire effect).</summary>
    private void RetargetDonorsOffLeaver(Mission mission, short sideId, Ship departing, Ship newLeader)
    {
        var newLeaderLive = LiveOf(newLeader);
        if (newLeaderLive is not null && ReferenceEquals(newLeaderLive.Wallet.AutoDonateTarget, departing))
            SetAutoDonate(newLeaderLive, null, 0, bSend: true);

        foreach (var kv in _peerShips)
        {
            var l = kv.Value;
            if (!ReferenceEquals(l.Ship.Mission, mission) || l.Ship.SideId != sideId) continue;
            if (ReferenceEquals(l.Ship, newLeader)) continue;   // native excludes the new leader's ship
            if (ReferenceEquals(l.Wallet.AutoDonateTarget, departing))
                SetAutoDonate(l, newLeader, 0, bSend: true);
        }
    }

    /// <summary>Resign passing — the stated P187 substitution for native
    /// ResignBallot::OnPassed → DeactivateSide (fsmission.cpp:6170-6180), which
    /// is OUT OF SCOPE (this port has no side deactivation). Instead every
    /// remaining player on the resigning side is withdrawn from the mission,
    /// reusing the P183 BroadcastQuitMission broadcast and the empty-side
    /// leader/owner release: each leaver's ship is removed from its sector (it
    /// stops flying), its side cleared, QUIT_MISSION broadcast whole-mission,
    /// and the peer dropped from the roster; the side's leadership and — if it
    /// owned the mission — the mission-owner role are released exactly as an
    /// emptying side does. This is a deliberate, honest approximation of "this
    /// side is out of the fight". The QUIT_MISSION reason is reused as
    /// <see cref="QuitSideReason.LinkDead"/> because native has no vote-based
    /// resign signal at all (it deactivates, emitting nothing).</summary>
    private void ApplyResign(Mission mission, PendingBallot ballot)
    {
        if (mission.Stage != MissionStage.Started) return;
        short sideId = ballot.VotingSideId;
        var side = mission.Sides.FirstOrDefault(s => s.Id == sideId);
        if (side is null) return;

        // Snapshot the leavers first (removing them below mutates _peerShips).
        var leavers = _peerShips
            .Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission) && kv.Value.Ship.SideId == sideId)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var conn in leavers)
        {
            if (!_peerShips.TryGetValue(conn, out var live)) continue;
            if (live.Ship.Sector is { } sec) sec.Remove(live.Ship);
            live.Ship.Sector = null;
            live.Ship.SideId = null;
            BroadcastQuitMission(mission, live.Ship.Id, QuitSideReason.LinkDead);
            _peerShips.Remove(conn);
            _peerNames.Remove(conn);
            _playerReady.Remove(conn);
        }

        // The side is now empty of players: clear its leader; if it owned the
        // mission, migrate the owner role to another occupied side's leader (or
        // null if none) — the same resolution HandleLeaderSuccession reaches for
        // an emptying side.
        side.LeaderShipId = null;
        if (mission.MissionOwnerSideId != sideId) return;
        mission.MissionOwnerSideId = null;
        foreach (var other in mission.Sides)
        {
            if (other.Id == sideId) continue;
            if (other.LeaderShipId is not { } otherLeaderId) continue;
            bool occupied = _peerShips.Any(kv => ReferenceEquals(kv.Value.Ship.Mission, mission) && kv.Value.Ship.SideId == other.Id);
            if (!occupied) continue;
            mission.MissionOwnerSideId = other.Id;
            BroadcastSetMissionOwner(mission, other.Id, otherLeaderId);
            return;
        }
    }

    /// <summary>An OfferDraw passing announces the offer and immediately creates
    /// the AcceptDraw ballot scoped to the OTHER side(s) (native OfferDrawBallot::
    /// OnPassed, fsmission.cpp:6182-6188).</summary>
    private void ApplyOfferDraw(Mission mission, PendingBallot ballot)
    {
        if (mission.Stage != MissionStage.Started) return;
        short offeringSide = ballot.VotingSideId;
        SendChatAnnouncement(mission, ballot, "A draw is being offered to the other teams.");
        CreateAcceptDrawBallot(mission, offeringSide);
    }

    /// <summary>An AcceptDraw passing ends the game as a draw — native
    /// AcceptDrawBallot::OnPassed → GameOver(NULL, "The game was declared a
    /// draw") (fsmission.cpp:6190-6194). Reuses P178's real draw outcome: the
    /// same Stage→Over flip + whole-mission GAME_OVER(62) broadcast with a NA
    /// winner that CheckForVictoryByInactiveSides builds for its zero-sides case
    /// (Server.cs:1273-1310), not a second game-over path.</summary>
    private void ApplyAcceptDraw(Mission mission, PendingBallot ballot) => DeclareDraw(mission);

    /// <summary>Flip a mission to Over as a DRAW and broadcast GAME_OVER(62) to
    /// the whole mission — the reused P178 path (GameOver.ForMission(mission,
    /// NA, reason), the same outcome CheckForVictoryByInactiveSides produces
    /// when no side remains).</summary>
    private void DeclareDraw(Mission mission)
    {
        if (mission.Stage == MissionStage.Over) return;   // idempotent
        mission.Stage = MissionStage.Over;
        var gameOver = GameOver.ForMission(mission, GameOver.NA, "The game was declared a draw");
        byte[] packet = FedPacket.Pack(new[] { gameOver.Encode() });
        var targets = _peerShips
            .Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission))
            .Select(kv => kv.Key)
            .ToArray();
        Console.WriteLine($"[gameover] mission #{_missions.IndexOf(mission)} -> Stage Over (draw via AcceptDraw ballot), {targets.Length} peer(s)");
        foreach (var t in targets) SendPeer(t, packet);
    }

    /// <summary>Announce a ballot's outcome — the port of native Ballot::OnPassed
    /// / OnFailed (fsmission.cpp:6045-6055): "&lt;proposal&gt; has passed /
    /// been defeated. &lt;tally&gt;", sent to the ballot's audience.</summary>
    private void AnnounceBallotResult(Mission mission, PendingBallot ballot, bool passed)
    {
        string verdict = passed ? "has passed." : "has been defeated.";
        SendChatAnnouncement(mission, ballot, $"{ballot.Proposal} {verdict}  {TallyString(ballot)}");
    }

    /// <summary>Send a chat announcement to a ballot's audience — CHAT_TEAM for
    /// a side-scoped ballot (native m_chattarget = CHAT_TEAM, fsmission.cpp:
    /// 5905) and CHAT_EVERYONE for the mission-wide AcceptDraw.</summary>
    private void SendChatAnnouncement(Mission mission, PendingBallot ballot, string text)
    {
        var msg = new ChatMessage
        {
            Message = text,
            ChatTarget = (int)(ballot.IsMissionWide ? ChatTarget.Everyone : ChatTarget.Team),
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        if (ballot.IsMissionWide)
        {
            int n = SendToMissionPeers(mission, packet);
            Console.WriteLine($"[ballot] #{ballot.Id} announcement whole-mission -> {n} peer(s)");
        }
        else
        {
            int n = 0;
            foreach (var kv in _peerShips)
            {
                if (!ReferenceEquals(kv.Value.Ship.Mission, mission)) continue;
                if (kv.Value.Ship.SideId != ballot.VotingSideId) continue;
                SendPeer(kv.Key, packet);
                n++;
            }
            Console.WriteLine($"[ballot] #{ballot.Id} announcement side {ballot.VotingSideId} -> {n} peer(s)");
        }
    }

    /// <summary>The port of native Ballot::GetTallyString (fsmission.cpp:
    /// 5970-6007): a per-side summary of in-favor/opposed/abstained counts.
    /// Compact form "(sideName: N for, M against, K abstained)".</summary>
    private string TallyString(PendingBallot ballot)
    {
        var parts = new List<string>();
        for (int s = 0; s < PendingBallot.SideSlots; s++)
        {
            if (ballot.Abstaining[s] + ballot.InFavor[s] + ballot.Opposed[s] == 0) continue;
            var bits = new List<string>();
            if (ballot.InFavor[s] > 0) bits.Add($"{ballot.InFavor[s]} for");
            if (ballot.Opposed[s] > 0) bits.Add($"{ballot.Opposed[s]} against");
            if (ballot.Abstaining[s] > 0) bits.Add($"{ballot.Abstaining[s]} abstained");
            parts.Add($"({string.Join(", ", bits)})");
        }
        return string.Join(", ", parts);
    }

    /// <summary>P184 — broadcast <c>POSITIONREQ(40)</c> announcing a pending
    /// side-join request (that needs leader approval), to the whole mission
    /// (native <c>RequestPosition</c>'s broadcast, fsmission.cpp:4765-4769).</summary>
    private void BroadcastPositionReq(Mission mission, short shipId, short sideId)
    {
        var packet = FedPacket.Pack(new[] { new PositionReqBroadcast { ShipId = shipId, SideId = sideId }.Encode() });
        int n = SendToMissionPeers(mission, packet);
        Console.WriteLine($"[posreq] ship {shipId} -> side {sideId}: POSITIONREQ(40) whole-mission to {n} peer(s)");
    }

    /// <summary>P184 — broadcast <c>DELPOSITIONREQ(42)</c> announcing that a
    /// pending side-join request is gone (rejected/canceled/dropped), to the
    /// whole mission. The port of native <c>NotifyPlayerBoot</c>'s whole-mission
    /// <c>DELPOSITIONREQ(DPR_Rejected)</c> (fsmission.cpp:4881-4891), generalised
    /// to carry whatever reason the caller needs.</summary>
    private void BroadcastDelPositionReq(Mission mission, short shipId, short sideId, DelPositionReqReason reason)
    {
        var packet = FedPacket.Pack(new[] { new DelPositionReq { ShipId = shipId, SideId = sideId, Reason = reason }.Encode() });
        int n = SendToMissionPeers(mission, packet);
        Console.WriteLine($"[delreq] ship {shipId} side {sideId} reason {reason}: DELPOSITIONREQ(42) whole-mission to {n} peer(s)");
    }

    /// <summary>P184 — the <see cref="ConnectionId"/> of the connected peer whose
    /// ship is <paramref name="shipId"/> <em>on</em> <paramref name="mission"/>,
    /// or <c>null</c> if no such connected ship exists. Used to resolve a
    /// ship-id from a <c>POSITIONACK</c>/<c>DELPOSITIONREQ</c> to the pending-
    /// request key (ship ids are per-mission, so the mission filter is the
    /// multi-mission scoping guard).</summary>
    private ConnectionId? FindConnectionOfShip(Mission mission, short shipId)
    {
        foreach (var kv in _peerShips)
            if (kv.Value.Ship.Id == shipId && ReferenceEquals(kv.Value.Ship.Mission, mission))
                return kv.Key;
        return null;
    }

    /// <summary>P184 — remove the pending join request (if any) that
    /// <paramref name="requesterConn"/>'s ship has on <paramref name="sideId"/>.
    /// Returns whether one was present and removed.</summary>
    private bool RemovePendingJoinRequest(ConnectionId requesterConn, short sideId)
    {
        if (_pendingJoinRequests.TryGetValue(requesterConn, out var pend) && pend.SideId == sideId)
        {
            _pendingJoinRequests.Remove(requesterConn);
            return true;
        }
        return false;
    }

    /// <summary>P186 — is <paramref name="ship"/> the current <b>mission owner</b>
    /// of <paramref name="mission"/>? The port of native <c>CFSMission::GetOwner()</c>
    /// (the mission-owner side's leader; <c>fedmsgs</c>'s LOCK_LOBBY/LOCK_SIDES gate,
    /// <c>FedSrv.CPP:6940,6955</c>). True iff the ship is on the owning side
    /// (<c>Mission.MissionOwnerSideId</c>, the P183 concept) AND is that side's
    /// current leader. A mission with no owner, or a side leader on a non-owning
    /// side, is not the owner.</summary>
    private bool IsMissionOwnerPlayer(Ship ship, Mission mission)
    {
        if (mission.MissionOwnerSideId is not { } ownerSideId) return false;
        if (ship.SideId != ownerSideId) return false;
        var ownerSide = mission.Sides.FirstOrDefault(s => s.Id == ownerSideId);
        return ownerSide is not null && ownerSide.LeaderShipId == ship.Id;
    }

    /// <summary>P195 — is <paramref name="ship"/> the current <b>leader</b> of
    /// the real side it is seated on in <paramref name="mission"/>? Used to
    /// filter the rebalance pool: native's <c>RandomizeSides</c> never moves
    /// team leaders (<c>fsmission.cpp:5161-5182</c>, <c>GetLeader(sideID) !=
    /// pfsPlayer</c>). A side-less ship, or a ship seated on a side it does not
    /// lead, is not a leader.</summary>
    private static bool IsSideLeader(Mission mission, Ship ship)
    {
        if (ship.SideId is not { } sideId) return false;
        var side = mission.Sides.FirstOrDefault(s => s.Id == sideId);
        return side is not null && side.LeaderShipId == ship.Id;
    }

    /// <summary>P195 — the port of native <c>CFSMission::GetSideRankSum</c>
    /// (<c>fsmission.cpp:1353-1365</c>) restricted to a single real side: the
    /// sum of every <b>leader</b> <c>PlayerScore.Rank</c> on that side, floored
    /// at 1 per player (<c>iRankSum += (iTempRank &lt; 1) ? 1 : iTempRank</c>).
    /// Because the Stage-1 rebalance moves players one-at-a-time into a running
    /// per-side sum (not a physical lobby flush), this counts only the players
    /// already anchored to the side — the leaders, who are never moved. The
    /// just-placed pool members are folded into the running sum directly by
    /// <see cref="RebalanceSides"/>, matching native's live recomputation via
    /// <c>AddPlayerToSide</c> before the next loop iteration. This port has no
    /// ghost/spectator concept, so <c>bCountGhosts</c> is moot (sum every
    /// seated leader).</summary>
    private int GetLeaderRankSum(Mission mission, short sideId)
    {
        int sum = 0;
        foreach (var kv in _peerShips)
        {
            var ship = kv.Value.Ship;
            if (ReferenceEquals(ship.Mission, mission) && ship.SideId == sideId
                && IsSideLeader(mission, ship))
                sum += Math.Max(kv.Value.PlayerScore.Rank, (short)1);
        }
        return sum;
    }

    /// <summary>P195 — the <c>RandomizeSides</c> port (<c>fsmission.cpp:5133-
    /// 5260</c>) as the Stage-1 substitute the brief specifies. Native's
    /// two-phase flush-then-reassign (move every non-leader to a neutral
    /// <c>SIDE_TEAMLOBBY</c>, then greedily re-seat them) cannot be ported
    /// because this port has no lobby side — every ship is always seated on a
    /// real side. The buildable substitute rebalances <b>directly across the
    /// existing real sides in one pass</b>: collect every non-leader seated on
    /// any real side (analogous to native's lobby population, never physically
    /// moved first), then repeatedly seat the highest-<c>Rank</c> remaining
    /// player onto whichever real side currently has the lowest rank sum
    /// (ties broken by connection/iteration order via LINQ's stable
    /// <c>OrderBy</c>). The end state — players redistributed to minimize
    /// per-side rank-sum imbalance — is equivalent to native; the mechanism —
    /// no visible "everyone stripped to a lobby" intermediate state — is not.
    /// <para><b>Step 3 faithful</b>: pending join requests (P184) for this
    /// mission are cleared before any reassignment (native
    /// <c>fsmission.cpp:5183-5196</c>), since a mass reassignment would
    /// otherwise leave stale pending entries. <b>LockSides bypass</b>: the
    /// dispatch intentionally does not refuse while sides are locked (native
    /// never consults bLockSides here). Reassignment broadcasts ride the
    /// existing per-player join/leave announcements inside
    /// <see cref="TryJoinSide"/> (QUIT_SIDE / JOIN_SIDE / SET_TEAM_LEADER /
    /// SET_MISSION_OWNER) — no new broadcast shape is invented. Simplifications
    /// (native concepts absent here, all stated in docs/RANDOMIZE_TEAMS_P195.md):
    /// no banned-side mask, no AFK/ready filter (every non-leader is
    /// rebalanced), no ghost counting, and no <c>iMaxImbalance</c> player-count
    /// override (pure rank-sum minimization).</summary>
    private void RebalanceSides(Mission mission)
    {
        // Step 3 (native fsmission.cpp:5183-5196): clear this mission's pending
        // join requests before reassigning — a mass reassignment would leave
        // stale entries otherwise.
        ClearPendingJoinRequestsForMission(mission);

        // Running per-side rank sums, seeded with the leaders' contributions
        // (leaders are never moved; their rank counts from the start — the
        // equivalent of native's sides still holding their leaders after the
        // non-leader flush). Computed fresh at the top so a leader's
        // contribution is always anchored.
        var sums = new Dictionary<short, int>();
        foreach (var side in mission.Sides)
            sums[side.Id] = GetLeaderRankSum(mission, side.Id);

        // The rebalance pool: every non-leader seated on any real side of this
        // mission, ranked highest-first (LINQ OrderBy is stable, so equal ranks
        // keep connection/iteration order — the port's "arrival order" tie-break,
        // since all PlayerScores seed at Rank 0 per P193 and only a future
        // feature ever differentiates them).
        var pool = _peerShips
            .Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission))
            .Where(kv => kv.Value.Ship.SideId is not null)
            .Where(kv => !IsSideLeader(mission, kv.Value.Ship))
            .Select(kv => (Ship: kv.Value.Ship, Rank: kv.Value.PlayerScore.Rank))
            .OrderByDescending(x => x.Rank)
            .ToList();

        foreach (var (ship, rank) in pool)
        {
            // Whichever real side currently has the lowest rank sum gets this
            // player (every side is eligible — no banned-side mask exists).
            short bestSide = -1;
            int bestSum = int.MaxValue;
            foreach (var side in mission.Sides)
            {
                if (!sums.TryGetValue(side.Id, out var s)) s = 0;
                if (s < bestSum) { bestSum = s; bestSide = side.Id; }
            }
            if (bestSide == -1) break;   // no real side — nothing to seat onto

            // Fold the newcomer into the running sum immediately (native adds
            // via AddPlayerToSide before the next PickNewSide), then move them.
            sums[bestSide] = bestSum + Math.Max(rank, (short)1);
            TryJoinSide(ship, bestSide);   // announces QUIT_SIDE / JOIN_SIDE / leader changes
        }

        Console.WriteLine($"[randomizeteams] rebalanced {pool.Count} non-leader player(s) across {mission.Sides.Count} side(s)");
    }

    /// <summary>P195 — clear every pending join request (P184) belonging to a
    /// player in <paramref name="mission"/>, scoped to that mission (pending
    /// requests are keyed by connection, so the mission filter requires mapping
    /// each key to its live ship). Part of <see cref="RebalanceSides"/> step 3.</summary>
    private void ClearPendingJoinRequestsForMission(Mission mission)
    {
        int removed = 0;
        foreach (var key in _pendingJoinRequests.Keys.ToList())
        {
            if (_peerShips.TryGetValue(key, out var live) && ReferenceEquals(live.Ship.Mission, mission))
            {
                _pendingJoinRequests.Remove(key);
                removed++;
            }
        }
        Console.WriteLine($"[randomizeteams] cleared {removed} pending join request(s)");
    }

    /// <summary>P190 — a <c>CHANGE_ALLIANCE</c> side id is acceptable iff it is
    /// <c>NA</c> (-1) or a valid side id in <c>[0, sideCount)</c>, mirroring the
    /// native filter in <c>UpdateAlliances</c> (<c>fsmission.cpp:5642-5647</c>).</summary>
    private static bool IsValidAllianceId(short id, int sideCount) =>
        id == -1 || (id >= 0 && id < sideCount);

    /// <summary>
    /// P190 — the port of native <c>CFSMission::UpdateAlliances</c>
    /// (<c>fsmission.cpp:5638-5811</c>, read in full this session; the merge /
    /// clear algorithm is quoted in docs/ALLIANCES_P190.md). Given a requested
    /// <c>sideID</c>/<c>sideAlly</c> pair (already range- and owner- and
    /// stage-gated by the caller), re-derives the whole mission's alliance
    /// groups from the current per-side state, applies the requested change
    /// (ally <c>sideID</c> to <c>sideAlly</c>, clear <c>sideID</c>'s alliances
    /// when <c>sideAlly == NA</c>, or — when <c>sideID == NA</c> — just
    /// re-normalize the existing state), stores the result back on each
    /// <see cref="Side.AllyGroup"/>, and <b>always</b> broadcasts
    /// <c>CHANGE_ALLIANCES(197)</c> whole-mission.
    /// <para><b>Faithfulness notes (divergences from this prompt's assumptions,
    /// verified against the live native source):</b> (1) native sends the
    /// broadcast <em>unconditionally</em> at the end of the function
    /// (<c>fsmission.cpp:5797-5807</c>) — there is <b>no</b> "only if the array
    /// changed" guard, contrary to the task brief's guess. (2) The final
    /// validation rule (<c>fsmission.cpp:5786-5792</c>) clears ALL alliances
    /// when the result is a single group covering every side with no solo team
    /// group-swapped — so "merge three sides into one group" is only a valid
    /// outcome when a fourth side remains solo. (3) "Active" sides map to
    /// <see cref="Side.Active"/> (the P202 field, native <c>rgfActive</c> /
    /// <c>GetSideActive</c>): every side starts active, and the
    /// <c>SIDE_INACTIVE(68)</c> pre-game toggle can deactivate a side — which
    /// then contributes nothing to the <c>activesides &gt;= 3</c> gate and
    /// cannot hold alliances (native <c>fsmission.cpp:5675,5691,5783</c>).
    /// </summary>
    private void UpdateAlliances(Mission mission, short sideID, short sideAlly)
    {
        var sides = mission.Sides;
        int n = sides.Count;
        const int AlliancesMax = ChangeAlliances.SideAlliesMax / 2;   // c_cAlliancesMax = 3 (igc.h:508)
        const sbyte Na = -1;

        // Convert the current alliance groups to per-side bitmasks: each side's
        // own bit plus every other side sharing its group value.
        var masks = new int[n];
        for (int i = 0; i < n; i++)
        {
            sbyte? g = sides[i].AllyGroup;
            if (g.HasValue)
            {
                masks[i] = 1 << i;
                for (int j = 0; j < n; j++)
                    if (sides[j].AllyGroup == g)
                        masks[i] |= 1 << j;
            }
            else masks[i] = 0;
        }

        if (sideID != Na)   // we got a valid parameter sideID
        {
            // native: an INACTIVE side forms/keeps no alliances
            // (GetSideActive(sideID) && sideAlly != NA, fsmission.cpp:5675).
            if (sides[sideID].Active && sideAlly != Na)
                masks[sideID] = masks[sideID] | (1 << sideAlly) | (1 << sideID);   // new allies = current + sideAlly + itself
            else
                masks[sideID] = 0;
        }

        // Propagate the alliances & count active sides.
        int activesides = 0;
        for (int i = 0; i < n; i++)
        {
            if (!sides[i].Active)             // inactive side: clear its alliances, don't count (fsmission.cpp:5691)
            {
                masks[i] = 0;
                continue;
            }
            activesides++;
            if (sideID == Na) continue;
            if (i == sideID) continue;
            if ((masks[sideID] & (1 << i)) != 0)   // sideID says it's allied with i: merge
            {
                int merged = masks[sideID] | masks[i];
                masks[sideID] = merged;
                masks[i] = merged;
            }
            else
                masks[i] = masks[i] & ~(1 << sideID);   // make sure i isn't allied with sideID
        }

        // Build the final allied groups.
        var allies = new sbyte[n];
        for (int i = 0; i < n; i++) allies[i] = Na;

        if (activesides >= 3)   // need at least 3 active teams for alliances
        {
            // Reset allies for solo teams (remove them from their own mask).
            for (int i = 0; i < n; i++)
                if (masks[i] == (1 << i)) masks[i] = 0;

            // Propagate the alliances till it's stable.
            bool stop;
            do
            {
                stop = true;
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++)
                    {
                        int ai = masks[i], aj = masks[j];
                        if ((ai & aj) != 0)              // i & j have common allies
                        {
                            int merged = ai | aj;
                            if (ai != merged || aj != merged)
                            {
                                stop = false;
                                masks[i] = merged;
                                masks[j] = merged;
                            }
                        }
                    }
            } while (!stop);

            // Create the different allied groups.
            var groups = new int[AlliancesMax];
            int soloteams = 0, numgroups = 0;
            for (int i = 0; i < n; i++)
            {
                if (masks[i] != 0)   // this side has allies: find its group or build a new one
                {
                    bool found_group = false;
                    for (int g = 0; g < AlliancesMax; g++)
                    {
                        if (masks[i] == groups[g])
                        {
                            allies[i] = (sbyte)g;
                            found_group = true;
                        }
                    }
                    if (!found_group)
                    {
                        groups[numgroups] = masks[i];
                        allies[i] = (sbyte)numgroups;
                        numgroups++;
                    }
                }
                else
                {
                    allies[i] = Na;    // this team has no allies
                    if (sides[i].Active) soloteams++;   // solo team only if active (fsmission.cpp:5783)
                }
            }

            // Validation: no group at all, OR no solo teams and only 1 group →
            // invalid alliances → clear everything.
            if (numgroups == 0 || (soloteams == 0 && numgroups == 1))
                for (int i = 0; i < n; i++) allies[i] = Na;
        }

        // Here we have valid alliances (in allies[]) — save them and broadcast.
        for (int i = 0; i < n; i++)
            sides[i].AllyGroup = allies[i] == Na ? null : allies[i];

        var wire = new ChangeAlliances();
        for (int i = 0; i < ChangeAlliances.SideAlliesMax; i++)
            wire.Allies[i] = i < n ? allies[i] : Na;
        byte[] packet = FedPacket.Pack(new[] { wire.Encode() });
        int cnt = SendToMissionPeers(mission, packet);
        Console.WriteLine($"[alliances] mission #{_missions.IndexOf(mission)}: CHANGE_ALLIANCES(197) whole-mission to {cnt} peer(s) -> [{string.Join(", ", allies)}]");
    }

    /// <summary>P186 — when a side's auto-accept turns <b>on</b>, immediately seat
    /// every player with a pending join request targeting that side. The port of
    /// native <c>SetAutoAccept</c>'s accept-sweep (<c>fsmission.cpp:4982-5009</c>):
    /// each pending entry on the side is removed and its requester re-resolved via
    /// <c>RequestPosition</c> — which, with auto-accept now on, seats them. Here the
    /// equivalent is <see cref="TryJoinSide"/> directly (the accept logic is
    /// P184's, reused rather than duplicated). The pending entry is removed
    /// <em>regardless</em> of outcome, matching native's unconditional
    /// <c>delete plinkJR</c> / "should never add the player back to the list, since
    /// autoaccept is on". A requester whose connection left meanwhile (no live
    /// ship) is simply dropped from the map without seating.</summary>
    private void SweepAutoAcceptPending(Mission mission, short sideId)
    {
        int seated = 0, dropped = 0;
        foreach (var pend in _pendingJoinRequests.Where(kv => kv.Value.SideId == sideId).ToList())
        {
            var targetConn = pend.Key;
            _pendingJoinRequests.Remove(targetConn);   // removed regardless of outcome
            if (_peerShips.TryGetValue(targetConn, out var live) && ReferenceEquals(live.Ship.Mission, mission))
            {
                TryJoinSide(live.Ship, sideId);
                seated++;
            }
            else
            {
                dropped++;
            }
        }
        Console.WriteLine($"[autoaccept] sweep side {sideId}: seated {seated} pending requester(s), dropped {dropped} stale");
    }

    /// <summary>P183 — how many connected players are on <paramref name="sideId"/>
    /// of <paramref name="mission"/>, optionally excluding <paramref name="exclude"/>
    /// (the leaver), computed on demand by filtering <c>_peerShips</c> — the same
    /// per-side live-count style P180's <see cref="RecomputeSideReady"/> uses
    /// (Server.cs:2782-2785), mission-scoped per the fixed P180/P178 bug. This
    /// port keeps no running per-side player counter (native's
    /// <c>m_misdef.rgcPlayers[sideID]</c>), deliberately — one maintenance
    /// mechanism, computed where needed.</summary>
    private int CountPlayersOnSideExcluding(Mission mission, short sideId, Ship exclude) =>
        _peerShips.Count(kv => ReferenceEquals(kv.Value.Ship.Mission, mission)
                            && kv.Value.Ship.SideId == sideId
                            && !ReferenceEquals(kv.Value.Ship, exclude));

    /// <summary>
    /// P183 — the <c>RemovePlayerFromSide</c> port's leader-succession branch
    /// (native fsmission.cpp:848-1099), applied when <paramref name="ship"/>
    /// leaves a side (whether by disconnecting or by switching sides). No-op
    /// unless <paramref name="ship"/> was its side's leader. Three outcomes,
    /// matching native:
    /// <list type="number">
    /// <item><b>Teammates remain</b> — promote the deterministic "first other
    /// player still on the side" (lowest ship id; native's plain list scan,
    /// fsmission.cpp:972-990) to leader, broadcasting <c>SET_TEAM_LEADER</c>
    /// (and <c>SET_MISSION_OWNER</c> if this side is the mission owner).</item>
    /// <item><b>Side empty &amp; was the mission owner</b> — migrate the owner
    /// role to another occupied side's existing leader (broadcasting
    /// <c>SET_TEAM_LEADER</c>/<c>SET_MISSION_OWNER</c> for them), or clear it
    /// with no broadcast if no other occupied side exists.</item>
    /// <item><b>Side empty &amp; not the owner</b> — just clear the side's
    /// leader, no broadcast.</item>
    /// </list>
    /// Stated simplifications (per P183 scope, never silent): leader-succession
    /// always uses "first other player on the side", dropping native's
    /// donation-based (<c>PickNewLeader</c>) and squad-capability
    /// (<c>GetCanLeadSquad</c>) tie-breaks — this port has neither donations
    /// (P119) nor squads; the lobby/side-unlock side effect native does when the
    /// owner vanishes (fsmission.cpp:1031-1062) is a deliberate no-op
    /// (<c>LOCK_LOBBY</c>/<c>LOCK_SIDES</c> aren't ported); rank-range/squad-name
    /// resets are omitted for the same reason.
    /// </summary>
    private void HandleLeaderSuccession(Ship ship)
    {
        if (ship.Mission is not Mission mission) return;
        if (ship.SideId is not { } oldSideId) return;
        var side = mission.Sides.FirstOrDefault(s => s.Id == oldSideId);
        if (side is null) return;
        if (side.LeaderShipId != ship.Id) return;   // only the leader's exit triggers succession

        // Teammates still on the side, excluding the leaver (native decrements
        // rgcPlayers[sideidOld] before checking, fsmission.cpp:917/928). Ordered
        // by ship id so "first other player" is deterministic and testable.
        var remaining = _peerShips
            .Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission)
                      && kv.Value.Ship.SideId == oldSideId
                      && !ReferenceEquals(kv.Value.Ship, ship))
            .Select(kv => kv.Value.Ship)
            .OrderBy(s => s.Id)
            .ToList();

        if (remaining.Count > 0)
        {
            var newLeader = remaining[0];
            side.LeaderShipId = newLeader.Id;
            BroadcastSetTeamLeader(mission, oldSideId, newLeader.Id);
            if (mission.MissionOwnerSideId == oldSideId)
                BroadcastSetMissionOwner(mission, oldSideId, newLeader.Id);

            // P197 — retarget donations when leadership passes (native
            // RemovePlayerFromSide's leader-change block, fsmission.cpp:
            // 1106-1115): clear the new leader's own target if it pointed at the
            // departing leader, then re-point every player who was donating to
            // the departing leader onto the new leader. (Native runs these with
            // bSend=false and one side-group flush; passing bSend:true for each
            // reproduces the identical net AUTODONATE(116) side-group broadcast.)
            RetargetDonorsOffLeaver(mission, oldSideId, ship, newLeader);
            return;
        }

        // Side is now empty: clear its leader, then migrate the owner role if needed.
        side.LeaderShipId = null;
        if (mission.MissionOwnerSideId != oldSideId)
            return;   // wasn't the mission owner — nothing more to do

        mission.MissionOwnerSideId = null;
        foreach (var other in mission.Sides)
        {
            if (other.Id == oldSideId) continue;
            if (other.LeaderShipId is not { } otherLeaderId) continue;   // an occupied side always has a leader
            bool occupied = _peerShips.Any(kv => ReferenceEquals(kv.Value.Ship.Mission, mission)
                                              && kv.Value.Ship.SideId == other.Id);
            if (!occupied) continue;
            mission.MissionOwnerSideId = other.Id;
            BroadcastSetTeamLeader(mission, other.Id, otherLeaderId);
            BroadcastSetMissionOwner(mission, other.Id, otherLeaderId);
            return;
        }
        // No other occupied side: the mission has no owner — nothing to announce.
        // (Native unlocks the lobby/sides here via LOCK_LOBBY/LOCK_SIDES;
        //  neither is ported — separate, unscoped item — so it is a deliberate no-op.)
    }

    /// <summary>
    /// P183 — the <c>AddPlayerToSide</c> port (native fsmission.cpp:539-661) as a
    /// directly-callable side-switch capability: seat <paramref name="ship"/>
    /// (already on <paramref name="ship"/>.Mission) on the given target side in
    /// the same mission, maintaining team-leader and mission-owner status exactly
    /// as native does. Returns <c>false</c> (no state change) if the ship has no
    /// mission or the target side does not exist in it.
    /// <para>Sequence, mirroring native: if the ship was on a real side, quit it
    /// first (<c>RemovePlayerFromSide(QSR_SwitchingSides)</c>, fsmission.cpp:
    /// 562-563) — running leader succession and broadcasting <c>QUIT_SIDE</c> —
    /// then join the new side: determine the new leader (first player onto an
    /// empty side) and mission owner (iff no side owns the mission or the owning
    /// side has no players — always alongside a new leader,
    /// <c>assert(IMPLIES(fMissionOwner, fTeamLeader))</c>), update
    /// <see cref="Side.LeaderShipId"/> / <see cref="Mission.MissionOwnerSideId"/>,
    /// and broadcast <c>JOIN_SIDE</c> always plus <c>SET_TEAM_LEADER</c> /
    /// <c>SET_MISSION_OWNER</c> when applicable — all to the whole mission.</para>
    /// <para>Not wired to any live client message yet (P184's position-request
    /// flow will trigger it); it exists as a directly-callable server capability,
    /// matching this session's "capability first, live-input wiring later"
    /// pattern. The ship leaves the old side even if the target is unoccupied;
    /// switching to the side the ship is already on re-joins it (a harmless
    /// re-announcement, still running the new-leader check for an empty side).</para>
    /// </summary>
    internal bool TryJoinSide(Ship ship, short newSideId)
    {
        if (ship.Mission is not Mission mission) return false;
        var targetSide = mission.Sides.FirstOrDefault(s => s.Id == newSideId);
        if (targetSide is null) return false;

        // Quit the old side first (native AddPlayerToSide, fsmission.cpp:562-563).
        if (ship.SideId is { } oldSideId && oldSideId != newSideId)
        {
            HandleLeaderSuccession(ship);
            BroadcastQuitSide(mission, ship.Id, QuitSideReason.SwitchingSides);
        }

        // New-side leadership: the first player onto an empty side becomes its
        // leader (native !HasPlayers(pside, true), fsmission.cpp:601-613).
        bool newLeader = CountPlayersOnSideExcluding(mission, newSideId, ship) == 0;
        bool newMissionOwner = false;
        if (newLeader)
        {
            // Mission owner iff no side owns it, or the owning side has no players
            // (native fsmission.cpp:584-592). Only ever alongside a new leader.
            if (mission.MissionOwnerSideId is null)
            {
                newMissionOwner = true;
            }
            else
            {
                var ownerSide = mission.Sides.FirstOrDefault(s => s.Id == mission.MissionOwnerSideId.Value);
                if (ownerSide is null || CountPlayersOnSideExcluding(mission, ownerSide.Id, ship) == 0)
                    newMissionOwner = true;
            }
            targetSide.LeaderShipId = ship.Id;
            if (newMissionOwner)
                mission.MissionOwnerSideId = newSideId;
        }

        ship.SideId = newSideId;

        BroadcastJoinSide(mission, ship.Id, newSideId);
        if (newLeader)
        {
            BroadcastSetTeamLeader(mission, newSideId, ship.Id);
            if (newMissionOwner)
                BroadcastSetMissionOwner(mission, newSideId, ship.Id);
        }

        // P197 — default auto-donate-to-leader on join (native AddPlayerToSide,
        // fsmission.cpp:720-723): only when the mission is Started/Starting, and
        // only for a non-leader (the leader itself donates to no one — a leader
        // created by this very join has its target left null). bSend=true, so the
        // side hears the relationship, exactly native's (..., 0, true).
        if (mission.Stage is MissionStage.Started or MissionStage.Starting)
        {
            var joinerLive = LiveOf(ship);
            if (joinerLive is not null)
            {
                Ship? leader = newLeader ? null : LeaderShip(mission, newSideId);
                SetAutoDonate(joinerLive, leader, 0, bSend: true);
            }
            // P197 — resync: tell the joining peer every teammate's donate
            // relationship + wallet (native SendMissionInfo's money/autodonate
            // loop, fsmission.cpp:4276-4302).
            SyncMoneyStateToJoiner(mission, newSideId, ConnOf(ship));
        }
        return true;
    }

    /// <summary>
    /// Pick the peers a chat message is relayed to, per the five wired targets.
    /// <b>Everyone</b> (0) → every connected peer ship (the same loop the ship-
    /// state broadcast walks, Server.cs:305-323). <b>Team</b> (5) → only peers
    /// whose <see cref="Ship.SideId"/> equals the sender's. <b>Individual</b> /
    /// <b>Individual_NoFilter</b> (6,7) → only the peer ship whose
    /// <see cref="Ship.Id"/> equals <see cref="ChatMessage.OidRecipient"/> and,
    /// like Team, only if that ship shares the sender's side — the real
    /// <c>ForwardChatMessage</c> (<c>FedSrv.CPP:3538-3556</c>) treats both
    /// targets identically in one case block and additionally allows allied
    /// sides, but this codebase has no alliance system, so same-side-only is
    /// the correct simplification (matching Team's precedent); the
    /// "NoFilter" distinction is about bypassing mute/ignore lists, and this
    /// server has none, so the two targets collapse into one case. <b>AllSector</b>
    /// (10) → every peer whose ship is currently in the sector named by
    /// <see cref="ChatMessage.OidRecipient"/> (the client picks the sector —
    /// <c>GetCluster(pcd->oidRecipient)</c>, <c>FedSrv.CPP:3428-3441</c>),
    /// regardless of side. <b>FriendlySector</b> (11) → the same sector
    /// membership but only peers sharing the sender's side (<c>FedSrv.CPP:3392-3424</c>;
    /// allied-side allowance in the real code reduced to same-side here).
    /// <b>Group</b> / <b>GroupNoEcho</b> (12,13) → aliases for Individual, per
    /// the real server's rewrite-and-fall-through (<c>FedSrv.CPP:3531-3536</c>).
    /// Every other <see cref="ChatMessage.ChatTarget"/> value is logged plainly
    /// and yields no targets — we refuse to guess a routing behavior for
    /// leader/admin/wing modes this codebase can't express (Leaders needs a
    /// team-leader election, Admin is literally <c>//NYI</c> in the shipped real
    /// server, Wing needs a wing model — see README's chat section), and we do
    /// not fall back to a broad broadcast (a real behavior bug disguised as
    /// leniency) nor throw (an unimplemented target must not take the server
    /// down — README's "report and ignore" rule). Returns empty when nothing
    /// should receive it.
    /// </summary>
    private ConnectionId[] SelectChatTargets(ConnectionId from, IChatRoutingTarget chat, short? senderSideId)
    {
        switch (chat.ChatTarget)
        {
            case (int)ChatTarget.Everyone:      // AGCChat_Everyone = 0 (igc.h:2731)
                return _peerShips.Keys.ToArray();

            case (int)ChatTarget.Team:          // AGCChat_Team = 5 (igc.h:2733)
                return _peerShips
                    .Where(kv => kv.Value.Ship.SideId == senderSideId)
                    .Select(kv => kv.Key)
                    .ToArray();

            // CHAT_INDIVIDUAL / CHAT_INDIVIDUAL_NOFILTER (igc.h:2733):
            // same behaviour here — collapse, don't build two paths for a
            // distinction that can't exist yet (no mute/ignore lists).
            case (int)ChatTarget.Individual:
            case (int)ChatTarget.IndividualNoFilter:      // FedSrv.CPP:3538-3556
                return _peerShips
                    .Where(kv => kv.Value.Ship.Id == chat.OidRecipient)
                    .Where(kv => kv.Value.Ship.SideId == senderSideId)   // same-side-only simplification
                    .Select(kv => kv.Key)
                    .ToArray();

            // CHAT_GROUP / CHAT_GROUP_NOECHO: the real server rewrites these to
            // CHAT_INDIVIDUAL and falls through (FedSrv.CPP:3531-3536) — a
            // client-sent Group target is itself just an alias for Individual.
            // Match that exact fallthrough here.
            case (int)ChatTarget.Group:        // FedSrv.CPP:3531-3536
            case (int)ChatTarget.GroupNoEcho:
                goto case (int)ChatTarget.Individual;

            case (int)ChatTarget.AllSector:               // FedSrv.CPP:3428-3441
                return _peerShips
                    .Where(kv => kv.Value.Sector.Id == chat.OidRecipient)
                    .Select(kv => kv.Key)
                    .ToArray();

            case (int)ChatTarget.FriendlySector:          // FedSrv.CPP:3392-3424
                return _peerShips
                    .Where(kv => kv.Value.Sector.Id == chat.OidRecipient)
                    .Where(kv => kv.Value.Ship.SideId == senderSideId)
                    .Select(kv => kv.Key)
                    .ToArray();

            default:
                Console.WriteLine($"[chat] target {chat.ChatTarget} not implemented in this slice — message from {from} dropped");
                return Array.Empty<ConnectionId>();
        }
    }

    /// <summary>Creates and places a peer's ship, then registers the live record.
    /// The registered <see cref="LiveShip"/> carries a <b>fresh</b>
    /// <see cref="PlayerScore"/> (P193) seeded to the neutral defaults
    /// <c>Rank=0, Score=0</c> — so every (re)connect starts from zero and the
    /// score is never carried across connections. Nothing here reads any prior
    /// score, because there is no persistence layer to read it from.</summary>
    private LiveShip AssignShip(LogonRequest req, short shipId)
    {
        var mission = _missions[0];
        var (ship, sector) = ShipSpawner is not null
            ? ShipSpawner(mission.Core, mission, shipId)
            : SpawnDefaultShip(mission, shipId);
        return new LiveShip(ship, sector, ship.Position, new PlayerScore(), new PlayerWallet());
    }

    /// <summary>Default spawn: first hull in the core (by id), placed at an
    /// x-offset staged by peer count so concurrent bots don't overlap, oriented
    /// straight ahead (identity) in the first sector of the first mission, and
    /// armed with the cheapest real weapon the core offers (see
    /// <see cref="MountCombatWeapon"/>).</summary>
    private (Ship Ship, Sector Sector) SpawnDefaultShip(Mission mission, short shipId)
    {
        if (mission.Core.HullTypes.Count == 0)
            throw new InvalidOperationException("core has no hull types to assign a ship");
        var hull = mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
        float x = 720f * (1 + _peerShips.Count);
        var sector = mission.Sectors[0];
        var ship = new Ship(shipId, hull, new Vector3(x, 730f, 920f), Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(ship, sector);

        // Round-robin across mission.Sides as peers connect, so concurrent
        // bots split roughly evenly across teams (peer N → side N mod
        // count). Reuses the same peer counter that staggers the x-offset
        // above: _peerShips is populated only after AssignShip returns, so
        // Count here is the number of peers already seated. Nothing in the
        // config or LogonRequest carries a side preference (the reference
        // handles that in the lobby, out of scope), so this is the simplest
        // defensible policy. An empty side list (unexpected — validated at
        // boot, Server.cs:231-236, but not assumed) leaves the ship side-less
        // and logs the omission rather than fabricating one.
        if (mission.Sides.Count > 0)
        {
            var sideIdx = _peerShips.Count % mission.Sides.Count;
            ship.SideId = mission.Sides[sideIdx].Id;
            Console.WriteLine($"[spawn] ship {shipId} seated on side {ship.SideId} "
                + $"({mission.Sides[sideIdx].Name ?? $"side{ship.SideId}"})");
        }
        else
        {
            Console.WriteLine($"[spawn] ship {shipId}: mission has no sides — ship left with no side (a finding, not fabricated)");
        }

        MountCombatWeapon(ship);
        return (ship, sector);
    }

    /// <summary>
    /// P147 Stage 1 — spawn an <b>ownerless</b> NPC/drone ship into
    /// <paramref name="sector"/> with <b>no</b> <c>_peerShips</c> entry and no
    /// connection: it exists purely as a sector-resident model, ticking via
    /// <c>Mission.Update</c>'s generic sector model loop and — the whole point
    /// of the stage — flowing straight into every same-sector peer's
    /// <c>HEAVY_SHIPS_UPDATE</c>(176) via
    /// <see cref="Sector.GetShipUpdateCandidates"/> (cluster-membership only;
    /// no peer-ownership gate) and <see cref="BroadcastShipsUpdate"/>, with
    /// <b>zero new broadcast code</b>.
    /// <para>
    /// The id is allocated from the same <c>_nextShipId</c> space as player
    /// ships (Server.cs:59), so it shares the ShipID/ObjectID namespace exactly
    /// like native and can never collide with a seated peer's id (which is
    /// drawn from the same counter).
    /// </para>
    /// <para>
    /// Deliberately minimal: no <c>_peerShips</c> entry, no weapon mount, no
    /// movement/AI/command behavior — it sits still. <b>P160:</b> it <em>does</em>
    /// carry a <c>PilotType</c> — derived from the spawned drone-type's
    /// definition (see below) — because that is the one decision
    /// <see cref="Ship.OnUpdate"/> acts on (the miner gate). It never goes
    /// through <c>ExportShip</c>, so the
    /// <c>PilotTypePlayer</c>-only assertion at ExportShip.cs:181 is untouched;
    /// visibility is proven purely via the heavy update, which carries no pilot
    /// type at all (HeavyShipsUpdate.cs:50-178). Currently called only by the
    /// P147 loopback live test — a real, callable server primitive whose only
    /// call site today is a test, stated honestly (not a fake production site).
    /// </para>
    /// <para>
    /// <b>P160 — drone-type derivation.</b> Pilot type is a property of the
    /// drone <em>type</em>, not the spawn site: native ships get
    /// <c>m_pilotType</c> from their drone-type definition and
    /// <c>PickDefaultOrder</c> dispatches per-type (<c>Igc/shipIGC.h:2216-2463</c>),
    /// which this port's already-decoded <c>DataDroneTypeIGC.PilotType</c>
    /// (<c>Data/DataDroneTypeIGC.cs:33</c>, from byte 376 of the real core
    /// record) directly mirrors. So this method looks the pilot type up from
    /// <c>Mission.Core.DroneTypes</c> for the given <paramref name="droneTypeId"/>
    /// rather than accepting a raw pilot-type argument ad hoc. When
    /// <paramref name="droneTypeId"/> is <c>null</c> (every pre-P160 call site,
    /// which spawns a miner-behaving NPC) or names a drone type absent from the
    /// core, it falls back to <see cref="Ship.PilotTypeMiner"/> directly —
    /// behavior-preserving for all existing callers, chosen over requiring
    /// every test to look up a concrete drone-type id when no such lookup is
    /// convenient there. When a drone-type id <em>is</em> supplied and found, the
    /// ship carries that drone type's real pilot type (e.g. Wingman=2 for
    /// PCore001's id 40 'Wingman').
    /// </para>
    /// </summary>
    internal Ship SpawnNpcShip(Mission mission, Sector sector, short sideId, Vector3 position, short? droneTypeId = null)
    {
        if (mission.Core.HullTypes.Count == 0)
            throw new InvalidOperationException("core has no hull types to assign an NPC ship");
        var hull = mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First();

        // P160 — resolve the ship's real pilot type from the drone-type definition
        // being spawned (native: m_pilotType comes from the drone type; this port's
        // DataDroneTypeIGC.PilotType is the direct analogue). Fall back to
        // PilotTypeMiner when no drone-type id is given (all pre-P160 call sites
        // spawn miner-behaving NPCs) or the id isn't in the core — behavior-
        // preserving for every existing caller.
        byte pilotType = Ship.PilotTypeMiner;
        if (droneTypeId is { } dtId &&
            mission.Core.DroneTypes.TryGetValue(dtId, out var droneType))
            pilotType = droneType.PilotType;

        short shipId = (short)(_nextShipId++);              // same id space as player ships
        var ship = new Ship(shipId, hull, position, Vector3.Zero, Orientation.Identity)
        {
            IsNpc = true,                                   // P148: explicit drone marker (init-only)
            PilotType = pilotType,                          // P160: the drone type's real pilot type (init-only)
        };
        ship.SideId = sideId;                               // drones are side-owned in native
        mission.PlaceShip(ship, sector);                    // no _peerShips entry is created
        Console.WriteLine($"[npc] spawned ownerless NPC ship {shipId} side {sideId} at {position} (pilotType {pilotType})");
        return ship;
    }

    /// <summary>
    /// Handle a client <c>FM_C_VIEW_CLUSTER(104)</c> for a <b>docked</b> peer —
    /// the P106 port of the native <c>FedSrv.CPP:6420-6454</c>
    /// <c>case FM_C_VIEW_CLUSTER</c>:
    /// <list type="bullet">
    /// <item><b>Gate on docked.</b> The dispatch branch already enforced
    /// <see cref="Ship.StationId"/> != null — the port's direct equivalent of the
    /// native <c>GetStation() != NULL</c> (<c>FedSrv.CPP:6421</c>). A flying ship
    /// never reaches this method (its request is a no-op, no reply).</item>
    /// <item><b>NA cluster = silent.</b> <c>clusterID != NA</c> gates the whole
    /// reply in native (<c>FedSrv.CPP:6423</c>); a request naming NA(-1) cluster
    /// gets no reply at all (native still calls <c>SetCluster(NULL)</c> after,
    /// but that persistence is deliberately not ported — see finding below).</item>
    /// <item><b>Target resolution.</b> When <c>otTarget != NA</c> the native
    /// resolves <c>pMission-&gt;GetModel(otTarget, oidTarget)</c>
    /// (<c>FedSrv.CPP:6428</c>) — this port's model list is the peer ships
    /// (plus sector-attached stations), resolved in
    /// <see cref="TryResolveViewTarget"/>.</item>
    /// <item><b>Reply shape.</b> Echo <c>clusterID</c> and, only when the target
    /// resolved to a real object in that cluster (<c>ptarget-&gt;GetCluster() ==
    /// pcluster</c>, <c>FedSrv.CPP:6430</c>) <b>and</b> visible to the requester's
    /// side (<c>ptarget-&gt;SeenBySide(pfsPlayer-&gt;GetSide())</c>,
    /// <c>FedSrv.CPP:6431</c>), the object's real position with
    /// <c>bUsePosition=true</c>; else bare cluster with <c>bUsePosition=false</c>.
    /// The visibility predicate reused here is this port's existing same-side
    /// gate (the P58 teammate broadcast's <c>tv.Value.Ship.SideId != recipientSide
    /// → skip</c>, <c>Server.cs:521</c>), applied to the target: an object with no
    /// side (a neutral world object) is visible; a side-carrying object is
    /// visible only to the same side — this port has no fog-of-war/scanner, so
    /// same-side is the faithful stand-in for native <c>SeenBySide</c>.</item>
    /// <item><b>Reply to requester only.</b> <c>SendPeer(from, …)</c> — the
    /// direct equivalent of <c>g.fm.SendMessages(pfsPlayer-&gt;GetConnection(), …)</c>
    /// (<c>FedSrv.CPP:6433-6434</c>); never a sector/mission broadcast.</item>
    /// </list>
    /// <para><b>Finding on the per-player "currently viewed cluster" state:</b>
    /// the native's trailing <c>pfsPlayer-&gt;SetCluster(pcluster, true)</c>
    /// (<c>FedSrv.CPP:6453</c>) persists a per-player viewed-cluster. Searching
    /// <c>FedSrv.CPP</c>'s native call sites, <b>no other handler reads that
    /// state back</b> — it is written only here and consumed nowhere else in
    /// server code (its consumer would be the client, if anything). So this
    /// port implements <c>VIEW_CLUSTER</c> as a pure request/reply with no new
    /// server-side per-player state, and says so; nothing in the wire protocol
    /// depends on the server remembering what the client is currently viewing.
    /// Confirmed for this task (details and rationale in VIEW_CLUSTER_P106.md).</para>
    /// Runs on the sim thread (the dispatch is drained inside <see cref="Tick"/>,
    /// like every other inbound handler), so it may read <c>_peerShips</c> and
    /// touch <c>SendPeer</c> without extra locking.
    /// </summary>
    private void HandleViewCluster(ConnectionId from, LiveShip live, ViewClusterRequest req)
    {
        var mission = _missions[0];

        // clusterID == NA → native resolves pcluster = NULL and sends NO reply
        // (the whole reply block sits inside the `!= NA` guard). Mirror exactly.
        if (req.ClusterId == -1)
        {
            Console.WriteLine($"[viewcluster] {from} ship {live.Ship.Id}: clusterID=NA — no reply (native FedSrv.CPP:6423)");
            return;
        }

        var requesterSide = live.Ship.SideId;
        var pcluster = mission.Sectors.FirstOrDefault(s => s.Id == req.ClusterId);

        // Target resolution + the native's own two-gate check:
        //   ptarget && ptarget->GetCluster() == pcluster && ptarget->SeenBySide(requesterSide)
        //   (FedSrv.CPP:6429-6432). Resolution is the port's GetModel;
        //   GetCluster()==pcluster maps to "the object's owning sector";
        //   SeenBySide maps to the same-side predicate (finding above).
        bool bUsePosition = false;
        Vector3 targetPos = Vector3.Zero;
        if (req.OtTarget != -1 && pcluster is not null
            && TryResolveViewTarget((IgcObjectType)req.OtTarget, req.OidTarget, out targetPos, out var objectSector, out var objectSide)
            && objectSector == pcluster
            && SeenByRequesterSide(objectSide, requesterSide))
        {
            bUsePosition = true;
        }

        var reply = new ViewClusterReply
        {
            Position = bUsePosition ? targetPos : Vector3.Zero,
            ClusterId = req.ClusterId,
            BUsePosition = bUsePosition,
        };
        SendPeer(from, FedPacket.Pack(new[] { reply.Encode() }));
        Console.WriteLine($"[viewcluster] {from} ship {live.Ship.Id} docked -> cluster {req.ClusterId}" +
            $"{(req.OtTarget != -1 ? $" otarget={(IgcObjectType)req.OtTarget}:{req.OidTarget}" : "")}" +
            $" bUsePosition={bUsePosition} (target ok={bUsePosition})");
    }

    /// <summary>
    /// P108 — the <c>FM_C_BOARD_SHIP(103)</c> handler, the port of
    /// <c>FedSrv.CPP:6385-6418</c> <c>case FM_C_BOARD_SHIP</c>. Dual-purpose
    /// like the native: <paramref name="req"/><c>.SidParent == NA</c> leaves
    /// the player's current parent ship (via <see cref="PerformLeave"/>), any
    /// other value boards that ship (via <see cref="PerformBoard"/>).
    /// <list type="bullet">
    /// <item><b>Gate: mission started.</b> The native refuses unless
    /// <c>pfsMission-&gt;GetStage() == STAGE_STARTED</c>
    /// (<c>FedSrv.CPP:6388</c>). We keep it via <see cref="Ship.Mission"/> /
    /// <see cref="MissionStage.Started"/>. The native's companion
    /// <c>IsGhost()</c> gate is skipped — this port has no ghost concept.</item>
    /// <item><b>Success ⇒ no dedicated ack.</b> Confirmed by reading the whole
    /// native handler: on success neither <c>BoardShip()</c> nor the internal
    /// <c>LeaveShip()</c> sends a dedicated acknowledgement — they only flush
    /// the <c>SHIP_STATUS</c> updates queued by their <c>ShipStatus-&gt;SetState</c>
    /// calls (<c>FedSrv.CPP:2450-2457</c>). This port's equivalent mechanism is
    /// the existing per-tick status refresh (<see cref="BroadcastShipStates"/>
    /// → <see cref="MakeOwnShipStatus"/>, which now derives the boarded state
    /// directly from the ship), so <b>no new success message is introduced</b>.</item>
    /// <item><b>Failure ⇒ <c>BOARD_NACK(178)</c> to the requester alone.</b>
    /// <c>g.fm.SendMessages(pfsPlayer-&gt;GetConnection(), ...)</c>
    /// (<c>FedSrv.CPP:6412-6416</c>). Covers: not a started mission; the leave
    /// case with no current parent; <c>LeaveShip</c> refusing (carrier not
    /// docked / not actually boarded); the board case with a missing or
    /// invalid target.</item>
    /// </list>
    /// Runs on the sim thread (the dispatch drains inside
    /// <see cref="Tick"/>), so it may mutate <see cref="_peerShips"/>/sides
    /// without extra locking, like every other inbound handler.
    /// </summary>
    private void HandleBoardShip(ConnectionId from, LiveShip live, BoardShip req)
    {
        var ship = live.Ship;
        var mission = ship.Mission;
        if (mission is null || mission.Stage != MissionStage.Started)
        {
            Console.WriteLine($"[boardship] {from} ship {ship.Id}: {(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — BOARD_NACK (native gate, FedSrv.CPP:6388)");
            SendBoardNack(from, req.SidParent);
            return;
        }

        bool success;
        if (req.SidParent == Na)
        {
            // Leave whatever this ship is currently boarded on (FedSrv.CPP:6393-6401).
            var carrier = ship.ParentShip;
            success = carrier is not null && PerformLeave(ship, carrier);
        }
        else
        {
            // Board the named ship (FedSrv.CPP:6404-6409): resolved on the
            // boarder's OWN side (native GetShip on GetSide()).
            var target = FindSameSideShip(req.SidParent, ship.SideId, mission);
            success = target is not null && PerformBoard(ship, target);
        }

        if (!success)
        {
            SendBoardNack(from, req.SidParent);
            Console.WriteLine($"[boardship] {from} ship {ship.Id} -> sidParent {req.SidParent} refused — BOARD_NACK(178) to requester only");
        }
    }

    /// <summary>
    /// P109 — the real <c>CshipIGC::ProcessShipUpdate(const ClientActiveTurretUpdate&amp;)</c>
    /// port (<c>shipIGC.cpp:3349-3392</c>), applied to the <b>gunner's</b> own
    /// ship. Called from the <c>FM_C_ACTIVE_TURRET_UPDATE(99)</c> dispatch,
    /// gated here on the sender being a genuine boarded gunner (the same
    /// cluster/parent/turret gate native <c>FedSrv.CPP:4873-4875</c> applies
    /// before routing to <c>SetActiveTurretUpdate</c>). Sequence:
    /// <list type="bullet">
    /// <item><b>Staleness bound</b> — <c>deltaT = GetMyLastUpdate() − atu.time</c>;
    /// outside <c>±1.5 s</c> the whole message is dropped (<c>rc = c_susOutOfDate</c>,
    /// <c>shipIGC.cpp:3353-3354</c>).</item>
    /// <item><b>\"We're shooting\"</b> — <c>SetStateM(weaponsMaskIGC)</c>
    /// (<c>shipIGC.cpp:3359</c>); this makes the turret weapon's
    /// <c>IsWeaponSelected</c> true (gunner present + <c>weaponsMaskIGC</c>).</item>
    /// <item><b>Controls</b> — <c>shipupdate.controls.Export(&amp;m_controls)</c>
    /// (<c>shipIGC.cpp:3361</c>).</item>
    /// <item><b>Snapshot catch-up</b> — decode the reported orientation, then
    /// integrate it forward to the present in ≤ <c>c_fTimeIncrement</c> (0.25 s)
    /// steps via <see cref="Ship.ExecuteTurretMove"/> (the first of native's two
    /// real call sites, <c>shipIGC.cpp:3371-3384</c>), then
    /// <c>SetOrientation(orientation)</c>.</item>
    /// </list>
    /// The <c>CFSPlayer</c> monotonic gate (<c>atu.time &gt; m_timeUpdate</c>,
    /// <c>fsship.cpp:1208-1218</c>) that normally sits above this in the native
    /// is subsumed here: the port has no per-player last-turret-time bookkeeping,
    /// and the <c>deltaT</c> bound already rejects grossly stale frames, so the
    /// step's own staleness check is sufficient for this slice.
    /// </summary>
    private void HandleActiveTurretUpdate(Ship gunner, ClientActiveTurretUpdate atu)
    {
        // FedSrv.CPP:4873-4875 — the gate that precedes SetActiveTurretUpdate.
        if (gunner.Sector is null || gunner.ParentShip is null || gunner.TurretId is null)
        {
            Console.WriteLine($"[turret] ship {gunner.Id}: ACTIVE_TURRET_UPDATE from a non-gunner — ignored (FedSrv.CPP:4873-4875)");
            return;
        }

        // ProcessShipUpdate(const ClientActiveTurretUpdate&), shipIGC.cpp:3349-3392.
        double deltaT = gunner.LastUpdate - atu.Time;          // shipIGC.cpp:3353
        if ((deltaT < 1.5) && (deltaT > -1.5))                 // shipIGC.cpp:3354
        {
            // "If we got this ... we're shooting." (shipIGC.cpp:3358-3359)
            gunner.StateM = ShipControlState.WeaponsMask;      // SetStateM(weaponsMaskIGC)
            gunner.Controls = atu.Controls;                    // Export(&m_controls), shipIGC.cpp:3361
            Orientation orientation = atu.Orientation;         // Export(&orientation), shipIGC.cpp:3363-3364

            // Snapshot catch-up, never advancing more than c_fTimeIncrement
            // (0.25 s) per step (shipIGC.cpp:3366-3384).
            int n = 1 + (int)(MathF.Abs((float)deltaT) / 0.25f);
            double thisTime = atu.Time;
            for (int i = 1; i <= n; i++)
            {
                double nextTime = atu.Time + (deltaT * ((float)i / (float)n));
                gunner.ExecuteTurretMove(thisTime, nextTime, orientation);
                thisTime = nextTime;
            }
            gunner.SetOrientation(orientation);                // SetOrientation(orientation), shipIGC.cpp:3386

            Console.WriteLine($"[turret] ship {gunner.Id} ACTIVE_TURRET_UPDATE time={atu.Time} deltaT={deltaT:F3} -> " +
                              $"StateM=weaponsMaskIGC controls={atu.Controls} orientForward={orientation.GetForward()}");
        }
        else
        {
            Console.WriteLine($"[turret] ship {gunner.Id} ACTIVE_TURRET_UPDATE stale (deltaT={deltaT:F3} outside ±1.5) — dropped (shipIGC.cpp:3354)");
        }
    }

    /// <summary>
    /// P140 — the <c>FM_C_PROMOTE(190)</c> handler, the port of
    /// <c>FedSrv.CPP:6353-6382</c> <c>case FM_C_PROMOTE</c>. A commanding ship
    /// promotes one of its own boarded turret gunners into the command seat —
    /// the real <see cref="Ship.Promote"/> role-swap (hull, loadout,
    /// ammo/energy, position/orientation, hull/shield fractions, and every
    /// sibling gunner transfer to the promoted ship; the old parent is demoted
    /// into the vacated turret mount — nothing is destroyed).
    /// <list type="bullet">
    /// <item><b>Gate: mission started.</b> The native refuses unless
    /// <c>pfsMission-&gt;GetStage() == STAGE_STARTED</c>
    /// (<c>FedSrv.CPP:6357</c>). Kept via <see cref="Ship.Mission"/> /
    /// <see cref="MissionStage.Started"/>. The native's companion
    /// <c>IsGhost()</c> gate is skipped — this port has no ghost concept (the
    /// same omission P108 documents for <c>BOARD_SHIP</c>).</item>
    /// <item><b>Eligibility: the requesting ship's OWN boarded children only.</b>
    /// The native walks <c>GetIGCShip()-&gt;GetChildShips()</c>
    /// (<c>FedSrv.CPP:6359</c>) and matches <c>pship-&gt;GetTurretID() ==
    /// mountidPromoted</c> (<c>FedSrv.CPP:6362</c>). A mount matching no real
    /// boarded child falls through the loop and is a <b>silent no-op</b> — no
    /// crash, no ack, no broadcast.</item>
    /// <item><b>No <c>ShipStatusRecalculate</c> call needed.</b> The native calls
    /// <c>((CFSShip*)pship-&gt;GetPrivateData())-&gt;ShipStatusRecalculate()</c>
    /// (<c>FedSrv.CPP:6364</c>) after the hull-changing promote. This port's
    /// status is not cached — <see cref="MakeOwnShipStatus"/> derives it fresh
    /// from <c>HullType.HullId</c>/<c>ParentShip</c>/<c>StationId</c> every
    /// per-tick refresh (<see cref="BroadcastShipStates"/>), the same mechanism
    /// P108/P110 established for post-hull-change status, so the promoted
    /// state is reflected automatically with no explicit recalc.</item>
    /// <item><b>Send target: sector-wide.</b> The native routes
    /// <c>GetGroupSectorFlying(pcluster)</c> (whole sector) when the requesting
    /// ship is in a cluster, else the requesting side's group
    /// (<c>FedSrv.CPP:6374-6377</c>). During active gameplay this port's ships
    /// are always placed in a <see cref="Ship.Sector"/> (there is no reachable
    /// in-game no-cluster state), so the side-group fallback is unreachable —
    /// routed through <see cref="SendSectorWide"/> (this port's
    /// <c>GetGroupSectorFlying</c> analogue).</item>
    /// </list>
    /// Runs on the sim thread (the dispatch drains inside <see cref="Tick"/>),
    /// so it may mutate <see cref="_peerShips"/> ship graph state without extra
    /// locking, like every other inbound handler.
    /// </summary>
    private void HandlePromote(ConnectionId from, LiveShip live, PromoteRequest req)
    {
        var ship = live.Ship;
        var mission = ship.Mission;
        if (mission is null || mission.Stage != MissionStage.Started)
        {
            Console.WriteLine($"[promote] {from} ship {ship.Id}: {(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — ignored (native gate, FedSrv.CPP:6357)");
            return;
        }

        // Walk the requesting ship's OWN boarded children for the one at the
        // requested turret mount (FedSrv.CPP:6359-6370).
        Ship? promoted = null;
        foreach (var child in ship.ChildShips)
        {
            if (child.TurretId == req.MountidPromoted)
            {
                child.Promote();
                promoted = child;
                break;
            }
        }

        if (promoted is null)
        {
            Console.WriteLine($"[promote] {from} ship {ship.Id}: no boarded child at mount {req.MountidPromoted} — silent no-op (FedSrv.CPP:6362)");
            return;
        }

        // Broadcast PROMOTE(189) to the sector-wide audience
        // (GetGroupSectorFlying(pcluster), FedSrv.CPP:6374-6376). The no-cluster
        // side-group fallback (FedSrv.CPP:6377) is unreachable here — an in-game
        // ship always has a sector — so SendSectorWide is the faithful route.
        var msg = new Promote { ShipidPromoted = promoted.Id };
        SendSectorWide(ship.Sector!.Id, msg.Encode());
        Console.WriteLine($"[promote] {from} ship {ship.Id}: promoted gunner {promoted.Id} at mount {req.MountidPromoted} -> PROMOTE(189) shipidPromoted={promoted.Id} sector {ship.Sector.Id} wide; old parent {ship.Id} demoted to its own seat");
    }

    /// <summary>
    /// P143 — the <c>FM_CS_SWAP_PART(34)</c> handler, the port of
    /// <c>FedSrv.CPP:6459-6501</c> <c>case FM_CS_SWAP_PART</c>. A flying,
    /// unboarded ship asks to move the mounted part at <c>(etOld, mountOld)</c>
    /// to <c>mountNew</c>, swapping it with a <b>different</b> part of the
    /// <b>same</b> <c>etOld</c> type already occupying <c>mountNew</c> if one is
    /// present (otherwise a plain move). The whole original message is then
    /// re-broadcast <b>sector-wide</b> with <c>shipID</c> filled in — everyone
    /// flying in the same cluster (<c>GetGroupSectorFlying(pcluster)</c>,
    /// <c>FedSrv.CPP:6498</c>), a broadcast-to-everyone-nearby message like
    /// PROMOTE, <b>not</b> a same-side-only relay like HIGHLIGHT_CLUSTER.
    /// No <c>LOADOUT_CHANGE</c> is queued from this handler — the broadcast
    /// <b>is</b> the SWAP_PART message itself (confirmed by reading the handler
    /// in full: the only forward is <c>pfmSwapPart</c>, <c>FedSrv.CPP:6497-6498</c>).
    /// <list type="bullet">
    /// <item><b>Gate: mission started + unboarded.</b> The native refuses unless
    /// <c>pfsMission-&gt;GetStage() == STAGE_STARTED</c> and
    /// <c>GetParentShip() == NULL</c> (<c>FedSrv.CPP:6461,6464</c>). The stage
    /// gate is kept verbatim like <see cref="HandlePromote"/>; the unboarded
    /// gate is <see cref="Ship.ParentShip"/> null — a boarded gunner/observer
    /// cannot swap its own parts, and unlike PROMOTE (a gunner can't promote
    /// itself) SWAP_PART needs the explicit check the native has.</item>
    /// <item><b>No-match is a silent no-op.</b> If no part is at
    /// <c>(etOld, mountOld)</c>, native does nothing — no error, no broadcast,
    /// no ack (<c>FedSrv.CPP:6485-6486</c>). Log-and-return, matching
    /// <see cref="HandlePromote"/>'s own <c>promoted is null</c> silent branch.</item>
    /// <item><b>Send target: sector-wide.</b> Native routes
    /// <c>GetGroupSectorFlying(pcluster)</c> (whole sector, all sides), via
    /// <see cref="SendSectorWide"/> (this port's analogue) — the same route
    /// PROMOTE uses; only reached when the requesting ship has a sector.</item>
    /// </list>
    /// Runs on the sim thread (dispatch drains inside <see cref="Tick"/>), so
    /// it may mutate the requesting ship's <c>_parts</c> through
    /// <see cref="Ship.TrySwapPart"/> without extra locking, like every other
    /// inbound handler.
    /// </summary>
    private void HandleSwapPart(ConnectionId from, LiveShip live, SwapPart req)
    {
        var ship = live.Ship;
        var mission = ship.Mission;
        if (mission is null || mission.Stage != MissionStage.Started)
        {
            Console.WriteLine($"[swappart] {from} ship {ship.Id}: {(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — ignored (native gate, FedSrv.CPP:6461)");
            return;
        }

        // Native TrapHackBoot(pfsPlayer->GetIGCShip()->GetParentShip() == NULL)
        // (FedSrv.CPP:6464): a boarded gunner/observer cannot swap parts.
        if (ship.ParentShip is not null)
        {
            Console.WriteLine($"[swappart] {from} ship {ship.Id}: boarded gunner (parent {ship.ParentShip.Id}) — SWAP_PART ignored (FedSrv.CPP:6464)");
            return;
        }

        // The swap-or-move; false means the source mount held no part, which
        // native treats as a silent no-op (FedSrv.CPP:6485-6486). The dispatch
        // is cargo-aware (P192): a negative mountOld/mountNew addresses the
        // ship's cargo hold rather than the equipped-mount list
        // (Ship.TrySwapPartWithCargo), native's mechanism for moving a part
        // into/out of cargo.
        if (!ship.TrySwapPartWithCargo(req.EtOld, req.MountOld, req.MountNew))
        {
            Console.WriteLine($"[swappart] {from} ship {ship.Id}: no mounted {req.EtOld} part at mount {req.MountOld} — silent no-op (native, FedSrv.CPP:6485)");
            return;
        }

        // Fill in shipID and broadcast SWAP_PART(34) sector-wide
        // (pfmSwapPart->shipID = pfsPlayer->GetShipID(); then
        // ForwardMessage(GetGroupSectorFlying(pcluster), pfmSwapPart),
        // FedSrv.CPP:6497-6498). An in-game ship always has a sector, so
        // SendSectorWide is the faithful route (cf. HandlePromote's reasoning).
        var msg = new SwapPart
        {
            ShipID = ship.Id,
            EtOld = req.EtOld,
            MountOld = req.MountOld,
            MountNew = req.MountNew,
        };
        SendSectorWide(ship.Sector!.Id, msg.Encode());
        Console.WriteLine($"[swappart] {from} ship {ship.Id}: {req.EtOld} mount {req.MountOld} -> {req.MountNew} -> SWAP_PART(34) shipID={ship.Id} sector {ship.Sector.Id} wide");
    }

    /// <summary>
    /// P192 — the <c>FM_CS_RELOAD(129)</c> handler, the port of
    /// <c>FedSrv.CPP:6504-6653</c> <c>case FM_CS_RELOAD</c>. A flying, unboarded
    /// ship reloads its cargo hold into its live pools / equipped launchers,
    /// up to 5 <c>{amountTransfered, mount}</c> entries (fuel/ammo/missile/
    /// chaff/dispenser, <c>FedSrv.CPP:6523</c>). Each entry is applied in wire
    /// order by <see cref="Ship.TryApplyReloadEntry"/> (ammo-pack → live
    /// <c>Ship.Ammo</c> + ammo-weapon mounted-fraction reset; fuel-pack → stated
    /// no-op + afterburner fraction reset; launcher → swap <c>Amount</c> into
    /// the equipped same-type launcher). On full success the whole message is
    /// re-broadcast <b>sector-wide</b> with <c>shipID</c> filled in
    /// (<c>FedSrv.CPP:6651-6652</c>).
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><b>Gates: mission started + unboarded + real sector + non-empty
    /// loadout.</b> Native requires <c>Stage == STAGE_STARTED</c> and
    /// <c>GetSide()-&gt;GetObjectID() &gt;= 0</c> (<c>FedSrv.CPP:6506</c>),
    /// <c>GetParentShip() == NULL</c> (<c>FedSrv.CPP:6509</c>), and — the NYI
    /// lifepod hack — <c>GetParts()-&gt;n() != 0</c> with a real cluster
    /// (<c>FedSrv.CPP:6516</c>). The stage + unboarded gates mirror
    /// <see cref="HandleSwapPart"/>'s.</item>
    /// <item><b>Malformed-entry response: softened to log-and-stop, no
    /// disconnect.</b> Native <c>bBoot = true; break;</c> then hard-disconnects
    /// the connection via <c>TrapHackBoot(!bBoot)</c> (<c>FedSrv.CPP:6531-6648</c>).
    /// This port's established TrapHackBoot convention (P182/P184/P186, cf.
    /// HandleTreasureAck/FireExpendable) is a graceful log-and-refuse — so on a
    /// malformed entry the remaining entries in the batch are refused and no
    /// broadcast is sent, but the connection stays up. The <b>no-rollback</b> of
    /// earlier-in-batch transfers is replicated faithfully: entries already
    /// applied before the malformed one keep their effect (a real native quirk,
    /// <c>FedSrv.CPP:6542/6563/6628</c>).</item>
    /// <item><b>Send target: sector-wide</b> — <see cref="SendSectorWide"/>,
    /// the same <see cref="HandleSwapPart"/> relay route (native
    /// <c>GetGroupSectorFlying(pcluster)</c>, <c>FedSrv.CPP:6652</c>).</item>
    /// </list>
    /// Runs on the sim thread (dispatch drains inside <see cref="Tick"/>), so it
    /// may mutate the ship's cargo hold / ammo / mounted-fractions without extra
    /// locking, like every other inbound handler.
    /// </remarks>
    private void HandleReload(ConnectionId from, LiveShip live, Reload req)
    {
        var ship = live.Ship;
        var mission = ship.Mission;
        if (mission is null || mission.Stage != MissionStage.Started)
        {
            Console.WriteLine($"[reload] {from} ship {ship.Id}: {(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — ignored (native gate, FedSrv.CPP:6506)");
            return;
        }

        // Native TrapHackBoot(pfsPlayer->GetIGCShip()->GetParentShip() == NULL)
        // (FedSrv.CPP:6509): a boarded gunner/observer cannot reload its own hold.
        if (ship.ParentShip is not null)
        {
            Console.WriteLine($"[reload] {from} ship {ship.Id}: boarded gunner (parent {ship.ParentShip.Id}) — RELOAD ignored (FedSrv.CPP:6509)");
            return;
        }

        // Native skips the whole body unless there's a real cluster AND a
        // non-empty part list (the NYI lifepod hack, FedSrv.CPP:6516).
        if (ship.Sector is null || ship.Parts.Count == 0)
        {
            Console.WriteLine($"[reload] {from} ship {ship.Id}: {(ship.Sector is null ? "no sector" : "empty loadout")} — skipped (native NYI gate, FedSrv.CPP:6516)");
            return;
        }

        // Native TrapHackBoot((nReloads > 0) && (nReloads <= 5))
        // (FedSrv.CPP:6523). Softened: an out-of-range batch is refused outright.
        int nReloads = req.Entries.Count;
        if (nReloads <= 0 || nReloads > 5)
        {
            Console.WriteLine($"[reload] {from} ship {ship.Id}: {nReloads} entries outside [1,5] — refused (native TrapHackBoot, FedSrv.CPP:6523)");
            return;
        }

        // The per-entry transfer loop (FedSrv.CPP:6529-6643). bBoot short-circuits
        // the batch WITHOUT rolling back earlier entries; the broadcast only
        // happens when every entry was well-formed.
        bool bBoot = false;
        foreach (var entry in req.Entries)
        {
            if (!ship.TryApplyReloadEntry(entry.Mount, entry.AmountTransfered))
            {
                bBoot = true;
                break;   // faithful: earlier-in-batch transfers stay applied
            }
        }

        if (bBoot)
        {
            // Native: TrapHackBoot(!bBoot) — a hard disconnect. This port's
            // established convention softens it to log-and-refuse-the-rest,
            // no broadcast, no disconnect (see remarks).
            Console.WriteLine($"[reload] {from} ship {ship.Id}: malformed entry, refusing remaining reloads (softened TrapHackBoot, FedSrv.CPP:6645-6648)");
            return;
        }

        // Fill in shipID and broadcast RELOAD(129) sector-wide
        // (pfmReload->shipID = pfsPlayer->GetShipID(); then
        // ForwardMessage(GetGroupSectorFlying(pcluster), pfmReload),
        // FedSrv.CPP:6651-6652).
        var msg = new Reload { Entries = req.Entries, ShipID = ship.Id };
        SendSectorWide(ship.Sector.Id, msg.Encode());
        Console.WriteLine($"[reload] {from} ship {ship.Id}: {nReloads} cargo entry/entries applied -> RELOAD(129) shipID={ship.Id} sector {ship.Sector.Id} wide");
    }

    /// <summary>
    /// P144 — the <c>FM_CS_DROP_PART(33)</c> handler, the port of
    /// <c>FedSrv.CPP:6296-6330</c> <c>case FM_CS_DROP_PART</c>. A flying,
    /// unboarded ship asks to drop the mounted part at <c>(et, mount)</c> into
    /// space. The part is removed from the ship's loadout (a removal, unlike
    /// SWAP_PART's reposition), the whole original message is re-broadcast
    /// <b>sector-wide</b> with <c>shipID</c> filled in — everyone flying in the
    /// same cluster (<c>GetGroupSectorFlying(pcluster)</c>,
    /// <c>FedSrv.CPP:6312</c>), the exact same audience SWAP_PART uses — and,
    /// unless the dropped part is an empty (<c>Amount == 0</c>) Pack, a real
    /// part-type treasure is created in the sector and announced via a
    /// sector-wide <c>FM_S_TREASURE(28)</c> send.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><b>Gate: mission started + unboarded.</b> Identical to
    /// <see cref="HandleSwapPart"/>'s — native requires
    /// <c>pfsMission-&gt;GetStage() == STAGE_STARTED</c> and
    /// <c>GetParentShip() == NULL</c> (<c>FedSrv.CPP:6297,6300</c>).</item>
    /// <item><b>No-match is a silent no-op.</b> If no part is at
    /// <c>(et, mount)</c>, native does nothing — no error, no broadcast, no
    /// treasure (<c>FedSrv.CPP:6322</c>). Log-and-return.</item>
    /// <item><b>Relay then conditional treasure.</b> Native forwards the
    /// original <c>DROP_PART</c> <em>regardless</em> of whether a treasure is
    /// created (<c>FedSrv.CPP:6312</c>), and only then creates the treasure
    /// guarded by <c>((type != OT_pack) &amp;&amp; !IlauncherIGC::IsLauncher(type))
    /// || (ppart-&gt;GetAmount() != 0)</c> (<c>FedSrv.CPP:6323</c>) and
    /// terminates the part (<c>FedSrv.CPP:6327</c>). This port collapses the
    /// launcher branch (no magazine/dispenser/chaff-launcher subtype exists in
    /// <see cref="DataPartTypeIGC"/>'s set), so the effective skip is: dropped
    /// part is <c>Pack</c> <em>and</em> its <c>Amount == 0</c>.</item>
    /// <item><b>Send target: sector-wide</b> — <see cref="SendSectorWide"/>,
    /// the same <see cref="HandleSwapPart"/> relay route (native
    /// <c>GetGroupSectorFlying(pcluster)</c>, <c>FedSrv.CPP:6312</c>), for both
    /// the DROP_PART echo and the TREASURE(28) notice.</item>
    /// <item><b>Treasure math.</b> Delegated to
    /// <see cref="Ship.CreateDropTreasure"/> (the port of the native
    /// <c>CreateTreasure</c> chain, <c>FedSrv.CPP:4324-4396</c>): <c>P0</c> =
    /// ship position + <c>(0.5·hull.Length + 10)·</c>random-unit-direction;
    /// <c>V0</c> = that direction × 25 + ship velocity; <c>code = Part</c>;
    /// <c>treasureID = part.PartId</c>; <c>amount = part.Amount</c>;
    /// <c>lifespan = 120.0f</c>; <c>createNow = false</c>;
    /// <c>objectID = mission.NextObjectId()</c>; <c>clusterID = ship sector</c>;
    /// <c>time0 = (uint)TickIndex</c> (this port's uint-Time convention).
    /// <c>TreasureSpawn</c>(28) is this port's first real send of the
    /// previously-built-but-never-sent codec (P123 built it for round-trip
    /// completeness only).</item>
    /// <item><b>Known accepted gaps (explicit, not silent).</b> The created
    /// treasure is <em>not</em> pickup-able (that is <c>ADD_PART</c>(164), out
    /// of scope), its <c>V0</c> is stored on the wire for a real client but not
    /// integrated into <see cref="Treasure.OnUpdate"/> (it rests static, like
    /// cash treasures), and <c>lifespan</c>/<c>time0</c> expiry is not enforced
    /// — all per <see cref="Treasure"/>'s own remarks and this task's scope.</item>
    /// </list>
    /// </remarks>
    private void HandleDropPart(ConnectionId from, LiveShip live, DropPart req)
    {
        var ship = live.Ship;
        var mission = ship.Mission;
        if (mission is null || mission.Stage != MissionStage.Started)
        {
            Console.WriteLine($"[droppart] {from} ship {ship.Id}: {(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — ignored (native gate, FedSrv.CPP:6297)");
            return;
        }

        // Native TrapHackBoot(pfsPlayer->GetIGCShip()->GetParentShip() == NULL)
        // (FedSrv.CPP:6300): a boarded gunner/observer cannot drop its own parts.
        if (ship.ParentShip is not null)
        {
            Console.WriteLine($"[droppart] {from} ship {ship.Id}: boarded gunner (parent {ship.ParentShip.Id}) — DROP_PART ignored (FedSrv.CPP:6300)");
            return;
        }

        // Resolve + remove; false means the mount held no part, which native
        // treats as a silent no-op (FedSrv.CPP:6322).
        if (!ship.TryRemovePart(req.Et, req.Mount, out var dropped))
        {
            Console.WriteLine($"[droppart] {from} ship {ship.Id}: no mounted {req.Et} part at mount {req.Mount} — silent no-op (native, FedSrv.CPP:6322)");
            return;
        }

        // Fill in shipID and broadcast DROP_PART(33) sector-wide
        // (pfmDropPart->shipID = pfsPlayer->GetShipID(); then
        // ForwardMessage(GetGroupSectorFlying(pcluster), pfmDropPart),
        // FedSrv.CPP:6311-6312). An in-game ship always has a sector, so
        // SendSectorWide is the faithful route (cf. HandleSwapPart's reasoning).
        var msg = new DropPart
        {
            ShipID = ship.Id,
            Et = req.Et,
            Mount = req.Mount,
        };
        SendSectorWide(ship.Sector!.Id, msg.Encode());
        Console.WriteLine($"[droppart] {from} ship {ship.Id}: dropped {req.Et}@{req.Mount} -> DROP_PART(33) shipID={ship.Id} sector {ship.Sector.Id} wide");

        // Conditional treasure: native's guard is
        // ((type != OT_pack) && !IsLauncher(type)) || (amount != 0)
        // (FedSrv.CPP:6323); the launcher branch does not exist in this port, so
        // it collapses to: skip iff the dropped part is a Pack with Amount == 0
        // (third CreateTreasure overload also returns NULL in that case,
        // FedSrv.CPP:4385-4387).
        if (dropped.Type.EquipmentType == EquipmentType.Pack && dropped.Amount == 0)
        {
            Console.WriteLine($"[droppart] {from} ship {ship.Id}: dropped empty Pack (amount 0) — no treasure (native skip, FedSrv.CPP:6323)");
            return;
        }

        var treasure = ship.CreateDropTreasure(dropped, _simNow, (uint)TickIndex);
        SendSectorWide(ship.Sector!.Id, new TreasureSpawn { Data = treasure.Data }.Encode());
        Console.WriteLine($"[droppart] {from} ship {ship.Id}: created part treasure obj={treasure.Data.ObjectId} part={treasure.Data.TreasureId} amount={treasure.Data.Amount} -> TREASURE(28) sector {ship.Sector.Id} wide");
    }

    /// <summary>
    /// P146 — the <c>FM_CS_SET_WINGID(106)</c> handler, the port of
    /// <c>FedSrv.CPP:5312-5344</c> <c>case FM_CS_SET_WINGID</c>. A player tags a
    /// ship (normally their own) with a wing-group id — a small bounded group
    /// number <c>[0, c_widMax)</c> — the (server-corrected) message is relayed
    /// <b>side-wide</b>, and the target ship's <c>WingID</c> state is mutated
    /// server-side.
    /// <list type="bullet">
    /// <item><b>Gate: mission present only.</b> Native's sole gate is
    /// <c>if (!pfsMission) break</c> (<c>FedSrv.CPP:5313</c>) — there is
    /// genuinely <b>no</b> <c>STAGE_STARTED</c> check here, unlike most other
    /// in-mission handlers (no <c>GetStage() == STAGE_STARTED</c> line in the
    /// whole case). Re-read of the quoted handler confirms it, so this port adds
    /// none.</item>
    /// <item><b>Self-targeting-only (deliberate simplification).</b> Native lets
    /// a side's <em>leader</em> retag another ship on its own side
    /// (<c>FedSrv.CPP:5323-5330</c>: <c>pfsPlayer == pfsMission-&gt;GetLeader(...)</c>
    /// — a team-leader concept absent everywhere in this port). With no leader
    /// system, that branch is unreachable, so <c>shipID</c> must resolve to the
    /// <em>sending</em> player's own ship; any other target is a silent no-op
    /// (no broadcast, no state change). <c>bCommanded</c> is therefore always
    /// server-corrected to <c>false</c> (the self-assignment value,
    /// <c>FedSrv.CPP:5334-5335</c>).</item>
    /// <item><b>Wing-id range check.</b> Native <c>TrapHackBoot(pfmSW-&gt;wingID
    /// &lt; c_widMax)</c> (<c>FedSrv.CPP:5319</c>); <c>c_widMax = 10</c>
    /// (<c>igc.h:505</c>). The port rejects out-of-range (incl. negative = <c>NA</c>)
    /// ids as a silent no-op, matching the treat-a-bad-request-as-no-op pattern
    /// used elsewhere (cf. <see cref=\"HandleSwapPart\"/>).</item>
    /// <item><b>Audience: side-wide, not sector-wide.</b> Native forwards to
    /// <c>CFSSide::FromIGC(pside)-&gt;GetGroup()</c> (<c>FedSrv.CPP:5337</c>) —
    /// the sending side's peer group, <em>not</em>
    /// <c>GetGroupSectorFlying(pcluster)</c> (the sector audience SWAP_PART/
    /// DROP_PART/PROMOTE use). The port's side-group analogue is the
    /// <c>BroadcastMoneyChangeSideWide</c> shape (<see cref=\"Server\"/>
    /// <c>Server.cs:3626-3637</c>): filter <c>_peerShips</c> by matching
    /// <c>Ship.SideId</c>, pack once, send to each target. A different-side peer
    /// flying in the same sector must <b>not</b> receive it — a different
    /// audience shape than SWAP_PART/DROP_PART/PROMOTE, confirmed by the live
    /// test.</item>
    /// <item><b>Server-side state.</b> <c>pfsShip-&gt;GetIGCShip()-&gt;
    /// SetWingID(pfmSW-&gt;wingID)</c> (<c>FedSrv.CPP:5338</c>) — this port mutates
    /// <see cref="Ship.WingId"/>.</item>
    /// </list>
    /// </summary>
    private void HandleFireExpendable(ConnectionId from, LiveShip live, FireExpendableRequest req)
    {
        var ship = live.Ship;
        var mission = ship.Mission;
        if (mission is null || mission.Stage != MissionStage.Started)
        {
            Console.WriteLine($"[fireexpendable] {from} ship {ship.Id}: {(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — ignored (native gate, FedSrv.CPP:5611)");
            return;
        }

        // Native TrapHackBoot(pShip->GetParentShip() == NULL) (FedSrv.CPP:5644):
        // a boarded gunner/observer cannot fire its own dispenser. Ported as the
        // graceful log-and-return refusal every other TrapHackBoot-guarded C→S
        // handler here uses (cf. HandleSwapPart, FedSrv.CPP:6464).
        if (ship.ParentShip is not null)
        {
            Console.WriteLine($"[fireexpendable] {from} ship {ship.Id}: boarded gunner (parent {ship.ParentShip.Id}) — FIRE_EXPENDABLE ignored (FedSrv.CPP:5644)");
            return;
        }

        // Resolve the mounted dispenser at mount 0 of the requested equipment
        // type (GetMountedPart(et, 0), FedSrv.CPP:5652). No such part is native's
        // silent `if (pdispenser)` guard — a silent no-op.
        var part = ship.Parts.FirstOrDefault(p => p.EquipmentType == req.Et && p.MountId == 0);
        if (part is null)
        {
            Console.WriteLine($"[fireexpendable] {from} ship {ship.Id}: no mounted {req.Et} part at mount 0 — silent no-op (native, FedSrv.CPP:5652)");
            return;
        }

        // Native TrapHackBoot(pdispenser->GetAmount() > 0) (FedSrv.CPP:5656).
        // Ported as a graceful refusal: no chaff, no broadcast, no decrement — the
        // must-fail-if-removed guard. (This port does not throw on a hack-flagged
        // request; it reports and ignores, matching the established idiom.)
        if (part.Amount <= 0)
        {
            Console.WriteLine($"[fireexpendable] {from} ship {ship.Id}: dispenser {req.Et}@0 has Amount {part.Amount} — refused, no fire (native TrapHackBoot, FedSrv.CPP:5656)");
            return;
        }

        // Chaff-only scope (P182): the deployed expendable must be a chaff
        // launcher whose ExpendableTypeId resolves in the core's ChaffTypes. The
        // mine/probe branch of native (FedSrv.CPP:5723-5737) is explicitly OUT OF
        // SCOPE — this port already deploys mines/probes via the separate,
        // already-working Ship.DeployMine/DeployProbe direct calls.
        if (part.Type.Type is not DataPartTypeIGC.ChaffLauncher cl)
        {
            Console.WriteLine($"[fireexpendable] {from} ship {ship.Id}: mounted {req.Et}@0 is {part.Type.Type.GetType().Name}, not a chaff launcher — mine/probe branch out of scope, ignored");
            return;
        }
        if (!mission.Core.ChaffTypes.TryGetValue(cl.ExpendableTypeId, out var chaffType))
        {
            Console.WriteLine($"[fireexpendable] {from} ship {ship.Id}: chaff launcher names unknown chaff type {cl.ExpendableTypeId} — ignored");
            return;
        }

        // Deploy the chaff at the ship's position/velocity and run the native
        // missile-retarget loop; the result names the new Chaff and the spoofed
        // missiles (fed by the server's shared _rand, like native's global random).
        var result = ship.DeployChaff(chaffType, mission.LastUpdate, _rand);

        // Decrement the dispenser's ammo (FedSrv.CPP:5744-5745).
        short newAmount = (short)(part.Amount - 1);
        ship.SetPartAmount(part, newAmount);

        // Build all four broadcasts in one guaranteed batch (native creates them
        // in this same handler and flushes once: SendMessages(GetGroupSectorFlying
        // (pcluster), FM_GUARANTEED, FM_FLUSH), FedSrv.CPP:5749). Audience: the
        // whole cluster, all sides — GetGroupSectorFlying(pcluster), the exact
        // SendSectorWide audience (unlike the same-side-only mine/probe/missile
        // broadcasts, native sends chaff to every ship flying in the sector).
        var enc = new List<byte[]>
        {
            new FireExpendable { LauncherId = ship.Id, EquipmentType = req.Et }.Encode(),
            new CreateChaff
            {
                P0 = result.Chaff.Position,
                V0 = result.Chaff.Velocity,
                Time0 = (uint)TickIndex,   // this project's uint-Time convention (cf. BroadcastFireMissile)
                Etid = chaffType.ExpendableTypeId,
            }.Encode(),
        };
        foreach (var sp in result.Spoofed)
            enc.Add(new MissileSpoofed { MissileId = sp.Id }.Encode());
        enc.Add(new EndSpoofing().Encode());   // exactly once, even when nothing was spoofed

        byte[] packet = FedPacket.Pack(enc.ToArray());
        int n = 0;
        foreach (var kv in _peerShips)
            if (kv.Value.Sector.Id == ship.Sector!.Id) { SendPeer(kv.Key, packet); n++; }

        Console.WriteLine($"[fireexpendable] {from} ship {ship.Id} fired chaff {chaffType.ExpendableTypeId} " +
                          $"(pos {result.Chaff.Position} vel {result.Chaff.Velocity}) -> FE(87)+CREATE_CHAFF(109)" +
                          $"+[{result.Spoofed.Count}×MISSILE_SPOOFED]+END_SPOOFING(111) to {n} cluster peer(s); " +
                          $"dispenser {newAmount + 1} -> {newAmount}");
    }

    private void HandleSetWingId(ConnectionId from, LiveShip live, SetWingId req)
    {
        var ship = live.Ship;
        var mission = ship.Mission;

        // Native's ONLY gate: if (!pfsMission) break (FedSrv.CPP:5313). Deliberately
        // no STAGE_STARTED check — the native SET_WINGID handler has none.
        if (mission is null)
        {
            Console.WriteLine($"[setwingid] {from} ship {ship.Id}: no mission — ignored (native gate, FedSrv.CPP:5313)");
            return;
        }

        // Self-targeting-only simplification: without a team-leader system the
        // native leader-retags-another-ship branch (FedSrv.CPP:5323-5330) is
        // unreachable, so shipID must be the sender's own ship; otherwise silent.
        if (req.ShipID != ship.Id)
        {
            Console.WriteLine($"[setwingid] {from} ship {ship.Id}: SET_WINGID for ship {req.ShipID} != own ship — ignored (self-targeting-only, FedSrv.CPP:5323)");
            return;
        }

        // Native TrapHackBoot(pfmSW->wingID < c_widMax) (FedSrv.CPP:5319);
        // c_widMax = 10 (igc.h:505). Negative ids (= NA) also rejected.
        if (req.WingId < 0 || req.WingId >= SetWingId.WidMax)
        {
            Console.WriteLine($"[setwingid] {from} ship {ship.Id}: wingID {req.WingId} out of range [0,{SetWingId.WidMax}) — ignored (native bound, FedSrv.CPP:5319)");
            return;
        }

        // Server-corrected message: bCommanded always false for a self-assignment
        // (FedSrv.CPP:5334-5335); the branch that would set true (leader retag)
        // is removed with the leader system.
        var corrected = new SetWingId
        {
            ShipID = ship.Id,
            WingId = req.WingId,
            BCommanded = false,
        };
        byte[] packet = FedPacket.Pack(new[] { corrected.Encode() });

        // Side-wide broadcast — port of CFSSide::FromIGC(pside)->GetGroup()
        // (FedSrv.CPP:5337). Filter _peerShips by Ship.SideId, the same
        // side-group analogue BroadcastMoneyChangeSideWide uses
        // (Server.cs:3626-3637). NOT the sector group.
        var sideId = ship.SideId;
        var targets = _peerShips
            .Where(kv => kv.Value.Ship.SideId == sideId)
            .Select(kv => kv.Key)
            .ToArray();
        foreach (var t in targets) SendPeer(t, packet);

        // Mutate the target ship's wing server-side
        // (pfsShip->GetIGCShip()->SetWingID(pfmSW->wingID), FedSrv.CPP:5338).
        ship.WingId = req.WingId;

        Console.WriteLine($"[setwingid] {from} ship {ship.Id}: wingID -> {req.WingId} (bCommanded=false) side {sideId} to {targets.Length} peer(s)");
    }

    /// <summary>
    /// P200 — answer a client's <c>FM_C_REQPINGDATA(195)</c> (the w0dk4
    /// player-pings feature) with that client's server-wide latency roster.
    /// Port of <c>FedSrv.CPP:7743-7808</c> <c>case FM_C_REQPINGDATA</c>:
    /// <list type="bullet">
    /// <item><b>5-second debounce</b> (native
    /// <c>CFMConnection::PingQuery</c>, <c>messagecore.h:217-223</c>): a
    /// request less than 5s after this connection's last successful full send
    /// gets only the unchanged-window marker, never the per-player entries.</item>
    /// <item><b>Whole-server roster, requester-only.</b> On a full send, one
    /// <see cref="PingData"/> is created for EVERY connection on the whole
    /// server with a live player (this port's <c>_peerShips</c> = native's
    /// "all connections with a live CFSPlayer"), deliberately NOT scoped to a
    /// mission or side — native iterates <c>g.fm.GetConnections()</c> outright.
    /// Every entry and the terminator are addressed ONLY to the requester
    /// (native: every <c>SendMessages(pfsPlayer-&gt;GetConnection(), ...)</c>).</item>
    /// <item><b>Terminator marker.</b> The final <c>{shipID = -1, ping = 1}</c>
    /// PINGDATA reuses the exact wire shape with a sentinel <c>shipID</c> as the
    /// "batch just sent / nothing changed" window close — not a player's data.</item>
    /// <item><b>Value sourcing.</b> Native derives <c>ping</c> from DirectPlay's
    /// <c>dwRoundTripLatencyMS</c> and <c>loss</c> from its retry/drop counters;
    /// this port's plain TCP transport exposes no managed access to either, so
    /// entries carry <c>ping = 0, loss = 0</c> (the task's stated, pre-approved
    /// fallback — wire-complete but not a measured value; see
    /// <c>docs/PINGDATA_REQPINGDATA_P200.md</c>).</item>
    /// </list>
    /// Runs on the sim thread (dispatch drains inside <see cref="Tick"/>).
    /// </summary>
    private void HandleReqPingData(ConnectionId from)
    {
        // The debounce gate (native PingQuery: elapsed > 5s ⇒ full roster).
        bool full = !_lastPingQuery.TryGetValue(from, out var last) || (_simNow - last) > 5.0;
        if (full)
            _lastPingQuery[from] = _simNow;   // stamp on the successful full send only

        // The terminator / unchanged-window marker (sentinel shipID = -1,
        // ping = 1; loss left 0 as BEGIN_PFM_CREATE zero-initialises it).
        var marker = new PingData { ShipId = -1, Ping = 1, Loss = 0 };

        if (!full)
        {
            // Debounced: send only the unchanged-window marker to the requester.
            SendPeer(from, FedPacket.Pack(new[] { marker.Encode() }));
            Console.WriteLine($"[pingdata] {from}: REQPINGDATA debounced (<5s) — unchanged-window marker only");
            return;
        }

        // Whole-server roster: one PINGDATA per live player, requester-only.
        var entries = new List<PingData>();
        foreach (var kv in _peerShips)
        {
            entries.Add(new PingData
            {
                ShipId = kv.Value.Ship.Id,
                Ping = 0,   // no managed transport RTT — see class/doc notes
                Loss = 0,   // no managed transport loss counter
            });
        }
        entries.Add(marker);

        byte[] packet = FedPacket.Pack(entries.Select(e => e.Encode()).ToArray());
        SendPeer(from, packet);   // ONLY the requester sees any of this
        Console.WriteLine($"[pingdata] {from}: full roster -> {_peerShips.Count} player(s) + terminator, requester-only");
    }

    /// <summary>
    /// P148 — the <c>FM_CS_ORDER_CHANGE(93)</c> handler, Stage 2 of the
    /// ORDER_CHANGE foundation arc. Ports <b>only the "stay docked" slice</b> of
    /// native <c>FedSrv.CPP:7264-7299</c> <c>case FM_CS_ORDER_CHANGE</c>:
    /// <list type="bullet">
    /// <item><b>Gate: <c>STAGE_STARTED</c> only.</b>
    /// <c>if (!pfsMission || pfsMission-&gt;GetStage() != STAGE_STARTED) break</c>
    /// (<c>FedSrv.CPP:7266</c>) — no other checks precede the decode.</item>
    /// <item><b>Target resolution.</b> <c>pship = pfsPlayer-&gt;GetIGCShip()</c>;
    /// <c>if (pside-&gt;GetShip(pfmOC-&gt;shipID)) pship = pside-&gt;GetShip(...)</c>
    /// (<c>FedSrv.CPP:7271-7278</c>) — the target is looked up on the commanding
    /// player's <em>own side</em>, and an unresolvable <c>shipID</c> leaves
    /// <c>pship</c> as the sender's own ship (not an error). Mirrored via the
    /// P148-extended <see cref="FindSameSideShip"/>, falling back to the sender.</item>
    /// <item><b>Stay-docked mutation.</b> When the target is an NPC
    /// (<c>GetPilotType() &lt; c_ptPlayer</c>, <c>FedSrv.CPP:7285</c>), docked
    /// (<c>GetStation() != NULL</c>, <c>FedSrv.CPP:7288</c>), and commanded
    /// <c>commandID == c_cidDoNothing</c> (<c>FedSrv.CPP:7290</c>), native sets
    /// <c>SetStayDocked(true)</c> (<c>FedSrv.CPP:7292</c>) — this port sets
    /// <see cref="Ship.StayDocked"/>.</item>
    /// </list>
    /// <para><b>Explicit exclusions (documented in ORDER_CHANGE_P148.md /
    /// ORDER_CHANGE_P150.md / ORDER_CHANGE_P152.md):</b> the carrier-specific
    /// shipyard-then-repair fallback (<c>FedSrv.CPP:7312-7320</c>,
    /// unreachable here — this port has no carrier pilot-type distinction);
    /// <c>c_cmdPlan</c> / the generic <c>SetCommand</c> fallback for non-NPC
    /// targets (<c>FedSrv.CPP:7335-7339</c>, which needs the full native 4-slot
    /// command array this port does not have). The docked "launch" sub-branch
    /// (formerly deferred — <c>FedSrv.CPP:7296-7302</c>) IS now handled (P152):
    /// it needs no <c>PickDefaultOrder</c>-equivalent call in this port's scope
    /// (see the launch arm below). Every combination outside the docked /
    /// return-to-base gates is <b>logged and no-oped</b>, not guessed at.</para>
    /// <para><b>Chat ack (stretch goal, not implemented).</b> Native also sends
    /// a <c>SendChat(... CHAT_INDIVIDUAL ...)</c> "Affirmative." ack from the
    /// commanded ship to the commander (<c>FedSrv.CPP:7293-7294</c>). This port's
    /// chat relay is client-initiated (<c>Server.cs:2528+</c>), and no reusable
    /// "server constructs a chat as if from ship X" helper exists; building one
    /// is out of the deliberate-narrow scope. The state mutation is the proof
    /// point and stands alone — the ack is documented as deferred, not dropped
    /// silently.</para>
    /// Runs on the sim thread (the dispatch drains inside <see cref="Tick"/>),
    /// so it may mutate ship state without extra locking.
    /// </summary>
    private void HandleOrderChange(ConnectionId from, LiveShip live, OrderChange req)
    {
        var ship = live.Ship;                        // the commanding player's ship
        var mission = ship.Mission;

        // Native gate: if (!pfsMission || pfsMission->GetStage() != STAGE_STARTED) break (FedSrv.CPP:7266).
        if (mission is null || mission.Stage != MissionStage.Started)
        {
            Console.WriteLine($"[orderchange] {from} ship {ship.Id}: {(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — ignored (native gate, FedSrv.CPP:7266)");
            return;
        }

        // Target resolution: pside->GetShip(shipID) on the commander's own side,
        // falling back to the sender's own ship when shipID doesn't resolve
        // (FedSrv.CPP:7271-7278).
        var target = FindSameSideShip(req.ShipID, ship.SideId, mission) ?? ship;

        // P148/P150/P152 docked NPC handling: target IsNpc && docked. Two arms —
        // commandID==c_cidDoNothing → stay docked (P148); anything else →
        // launch (P152). Both are halves of the native docked branch
        // (FedSrv.CPP:7288-7302).
        if (target.IsNpc && target.StationId is not null)
        {
            if (req.CommandId == OrderChange.CidDoNothing)
            {
                target.StayDocked = true;   // pship->SetStayDocked(true), FedSrv.CPP:7292
                Console.WriteLine($"[orderchange] {from} ship {ship.Id}: commanded NPC {target.Id} (side {target.SideId}, docked@{target.StationId}) to stay docked (commandID=c_cidDoNothing) -> StayDocked=true (FedSrv.CPP:7290-7294)");
            }
            else
            {
                // P152 — the "launch" arm (native FedSrv.CPP:7296-7302):
                // SetStayDocked(false) and actually leave the station.
                //
                // No PickDefaultOrder-equivalent call is made — stated plainly,
                // not omitted silently. Native calls
                // PickDefaultOrder(..., bDocked=true) here (FedSrv.CPP:7301),
                // but that call is a deliberate no-op for this port's scope.
                // Confirmed by reading the only PickDefaultOrder sub-block this
                // port has ported — the P151 take-cover check at
                // shipIGC.h:2230 gates on `!bDocked`, which `true` makes false —
                // and every other native sub-block in that switch (mining/ore,
                // unload, "arriving after player command", cross-cluster
                // FindTarget) is either irrelevant without ore/mining
                // infrastructure or itself gated on things this port doesn't
                // build (c_cidJoin, m_fOre, c_ttAnyCluster). So the state
                // transition alone (StayDocked=false + the shared undock core)
                // IS the port; a PickDefaultOrder call would be dead code.
                target.StayDocked = false;   // pship->SetStayDocked(false), FedSrv.CPP:7298
                if (target.Sector is { } undockSector)
                {
                    // Share the real-peer undock core: clear StationId, push
                    // clear of DockRadius, start the debounce. Connection-free —
                    // an NPC has no owning peer's client to receive the
                    // SET_CLUSTER that PerformUndock additionally sends (that
                    // message only informs the undocking peer's own client-side
                    // sim, appmsg.cpp:1044-1049; observing peers get the NPC's
                    // new flying state via the existing
                    // HeavyShipsUpdate/BroadcastShipsUpdate machinery, the same
                    // reasoning P147 established).
                    UndockShipState(target, undockSector);
                }
                else
                {
                    // Defensive: a docked NPC is always a placed ship
                    // (Mission.PlaceShip sets Sector; the dock seam only sets
                    // StationId afterward), so unreachable in practice — but
                    // still clear the dock rather than leave a contradictory
                    // StayDocked=false + StationId!=null state.
                    target.StationId = null;
                    Console.WriteLine($"[orderchange] docked NPC {target.Id} had no Sector — StationId cleared without reposition (defensive)");
                }
                Console.WriteLine($"[orderchange] {from} ship {ship.Id}: commanded docked NPC {target.Id} (side {target.SideId}) to LAUNCH (commandID={req.CommandId} != c_cidDoNothing) -> StayDocked=false, StationId=null, repositioned clear of DockRadius (FedSrv.CPP:7296-7302; no PickDefaultOrder call — bDocked=true skips the port's only ported sub-block, shipIGC.h:2230)");
            }
            return;
        }

        // P150 — the "return to base" branch: target IsNpc && NOT docked
        // (flying). Native's `else` arm, FedSrv.CPP:7304-7332. No commandID gate
        // here (unlike the docked branch) — confirmed by reading the native:
        // the else branch never reads pfmOC->commandID.
        if (target.IsNpc && target.StationId is null)
        {
            target.StayDocked = true;   // pship->SetStayDocked(true), FedSrv.CPP:7321

            // pship->SetCommand(c_cmdQueued, NULL, c_cidNone): forget any
            // remembered sector (FedSrv.CPP:7322) — unconditional in this branch.
            target.QueuedTarget = null;
            target.QueuedCommandId = OrderChange.CidNone;

            // Find the base server-side because the ship's exact location is
            // only available here when it is in a different sector (comment at
            // FedSrv.CPP:7306). This port has NO carrier pilot-type distinction
            // (Ship.IsNpc is a single bool, P148 — not a PilotType enum), so the
            // native `GetPilotType() != c_ptCarrier` gate (FedSrv.CPP:7307) is
            // UNREACHABLE here: always take the non-carrier path, the nearest
            // friendly repair-capable station in the ship's own sector (P149;
            // c_sabmRepair = 0x40). The carrier shipyard-first search
            // (FedSrv.CPP:7312-7320) is its own future slice.
            var station = target.FindNearestFriendlyStation(0x40);

            // The station-store is gated by `if (pmodel)` (FedSrv.CPP:7323-7331);
            // StayDocked + Queued-clear above happened unconditionally.
            if (station is { } st)
            {
                ApplyCommandSlot(target, req.Command, st, req.CommandId);   // pship->SetCommand(pfmOC->command, pmodel, pfmOC->commandID), FedSrv.CPP:7330
            }

            Console.WriteLine($"[orderchange] {from} ship {ship.Id}: commanded flying NPC {target.Id} (side {target.SideId}, sector {target.Sector?.Id}) to return to base -> StayDocked=true, Queued cleared, " +
                $"{(station is null ? "no friendly repair station in sector -> command slot unset (native if(pmodel) gate, FedSrv.CPP:7323)" : $"station {station.Value.StationId} found -> slot-write dispatched for command={req.Command} cmdID={req.CommandId} (FedSrv.CPP:7330)")}");
            return;
        }

        // P153/P154 — the generic non-NPC fallback (native FedSrv.CPP:7335-7339).
        // This branch is reached exactly when the commanded ship `target` is a
        // REAL player ship (IsNpc=false): the two NPC branches above returned
        // for docked/flown NPCs, so everything left here is the player-owned
        // path. Native runs the generic SetCommand for it (DoNotExecute stays
        // false for a player, FedSrv.CPP:7284-7285; pside->GetObjectID()>=0
        // holds for any seated side):
        //
        //   if (pside->GetObjectID() >= 0 && !DoNotExecute) {
        //       pmodel = pMission->GetModel(pfmOC->objectType, pfmOC->objectID);
        //       pship->SetCommand(pfmOC->command, pmodel, pfmOC->commandID);
        //   }
        //
        // Two real client actions exercise this against a real player's own
        // ship, both sending objectType/objectID naming a target model (or
        // OT_invalid + NA to CLEAR):
        //  - P153 — the SetTarget UI action (WinTrek.cpp:8820-8852) sends
        //    command=c_cmdCurrent; the port's c_cmdCurrent slot is
        //    Ship.CommandTarget (Model?, P139).
        //  - P154 — the SetAccepted menu-order action (WinTrek.cpp:10681-10682,
        //    8907) sends command=c_cmdAccepted with the SAME pmodel as
        //    SetTarget; the port's real-player c_cmdAccepted slot is
        //    Ship.AcceptedShipTarget/AcceptedShipCommandId (Model? + short,
        //    mirroring CommandTarget's shape and distinct from the NPC-specific
        //    DataStationIGC?-typed AcceptedTarget of P150).
        // Both are wired identically: a SHIP target resolves cross-side via
        // FindShipAnySide; objectID=NA clears; any non-ship objectType is a
        // logged no-op (this port's Station is not a Model, so neither slot can
        // hold one — documented scope boundary, not a bug).
        bool accepted = req.Command == OrderChange.CmdAccepted;
        bool current = req.Command == OrderChange.CmdCurrent;
        if (!target.IsNpc && (accepted || current))
        {
            string slot = accepted ? "AcceptedShipTarget" : "CommandTarget";
            if (req.ObjectID == Na)
            {
                // Clear-target: SetTarget/SetAccepted's pmodel==NULL case sends
                // objectType=OT_invalid + objectID=NA (WinTrek.cpp:8834-8836);
                // native SetCommand(command, NULL, cid) clears the slot.
                if (accepted)
                {
                    target.AcceptedShipTarget = null;
                    target.AcceptedShipCommandId = OrderChange.CidNone;
                }
                else
                {
                    target.CommandTarget = null;
                }
                Console.WriteLine($"[orderchange] {from} ship {ship.Id}: {target.Id} cleared {(accepted ? "accepted ship target" : "command target")} ({(accepted ? "c_cmdAccepted" : "c_cmdCurrent")}, objectID=NA sentinel, WinTrek.cpp:8834-8836)");
            }
            else if (req.ObjectType == IgcObjectType.Ship)
            {
                // Cross-side target-accept/lock. found null (unknown id) =
                // native GetModel NULL -> SetCommand(command, NULL, cid) clears.
                var found = FindShipAnySide(req.ObjectID, mission);
                if (accepted)
                {
                    target.AcceptedShipTarget = found;
                    target.AcceptedShipCommandId = req.CommandId;
                }
                else
                {
                    target.CommandTarget = found;
                }
                string foundDesc = found is null
                    ? $"ship {req.ObjectID} not found — {(accepted ? "accepted ship target" : "command target")} cleared (FedSrv.CPP:7336-7338)"
                    : $"target ship {found.Id} (side {found.SideId}, {(found.IsNpc ? "NPC" : "peer")}{(accepted ? $") cmdID={req.CommandId}" : ")")} — {slot} set (FedSrv.CPP:7335-7339)";
                Console.WriteLine($"[orderchange] {from} ship {ship.Id}: {target.Id} {(accepted ? "c_cmdAccepted" : "c_cmdCurrent")} -> {foundDesc}");
            }
            else
            {
                // Non-ship objectType (station/etc.): both slots are Model?-
                // typed and cannot hold it — logged, slot unchanged (documented
                // scope boundary).
                Console.WriteLine($"[orderchange] {from} ship {ship.Id}: {target.Id} {(accepted ? "c_cmdAccepted" : "c_cmdCurrent")} objectType {req.ObjectType} is not a ship — non-ship target out of scope for {slot} (logged, no-op)");
            }
            return;
        }

        // Every other combination is EXPLICITLY OUT OF SCOPE (P148/P150/P153/P154):
        // a non-NPC target with a command other than c_cmdCurrent/c_cmdAccepted,
        // or any objectType/slot pairing not covered above. Log and no-op; don't
        // guess at behavior for the deferred branches.
        Console.WriteLine($"[orderchange] {from} ship {ship.Id}: ORDER_CHANGE target {target.Id} " +
            $"IsNpc={target.IsNpc} StationId={target.StationId} commandID={req.CommandId} command={req.Command} — out-of-scope branch, no-op (P148/P150/P153/P154)");
    }

    /// <summary>
    /// P150 — the port of <c>pship-&gt;SetCommand(pfmOC-&gt;command, pmodel,
    /// pfmOC-&gt;commandID)</c> (<c>FedSrv.CPP:7330</c>): write the found station
    /// into whichever of the port's command slots the decoded <c>command</c>
    /// field names. The port does not build the full native 4-slot array — the
    /// real client's three ORDER_CHANGE send sites only ever use
    /// <c>c_cmdAccepted</c> (<c>WinTrek.cpp:3778</c>, <c>8907</c>) or
    /// <c>c_cmdCurrent</c> (<c>WinTrek.cpp:8842</c>), never <c>c_cmdQueued</c>
    /// or <c>c_cmdPlan</c> — so this switch covers the two new named slots
    /// (<see cref="Ship.AcceptedTarget"/>, <see cref="Ship.QueuedTarget"/>) and
    /// the existing <c>c_cmdCurrent</c> slot (<see cref="Ship.CommandTarget"/>),
    /// logging-and-not-storing anything else (<c>c_cmdPlan</c>, or a degenerate
    /// value) rather than guessing.
    /// </summary>
    private static void ApplyCommandSlot(Ship target, short commandSlot, DataStationIGC station, short commandId)
    {
        switch (commandSlot)
        {
            case OrderChange.CmdAccepted:
                target.AcceptedTarget = station;
                target.AcceptedCommandId = commandId;
                break;
            case OrderChange.CmdQueued:
                target.QueuedTarget = station;
                target.QueuedCommandId = commandId;
                break;
            case OrderChange.CmdCurrent:
                // The port's c_cmdCurrent slot is Ship.CommandTarget — a `Model?`
                // for weapon-aim (P139); stations are DataStationIGC record structs,
                // not Models, so a station cannot be stored there. This is SAFE for
                // every reachable case: the only client send site that emits
                // c_cmdCurrent (WinTrek.cpp:8842) is the client-local autopilot
                // setter that also sets its own local target, and the native drone
                // path is DoNotExecute-guarded (FedSrv.CPP:7287) so no drone command
                // round-trips into the generic fallback either. Logged, not stored,
                // and not silently ignored.
                Console.WriteLine("[orderchange] c_cmdCurrent write into the return-to-base branch is not representable in this port " +
                                  "(Ship.CommandTarget is Model?, stations are DataStationIGC) — logged, no store");
                break;
            default:
                // c_cmdPlan (3) is explicitly out of scope (P150); any degenerate
                // value likewise. Log and no-op — never guess at the slot's meaning.
                Console.WriteLine($"[orderchange] command slot {commandSlot} has no representable home in this port — logged, no store (c_cmdPlan/unknown out of scope)");
                break;
        }
    }

    /// <summary>
    /// P145 — the <c>FM_C_TREASURE_ACK(163)</c> handler, the port of
    /// <c>FedSrv.CPP:6094-6152</c> <c>case FM_C_TREASURE_ACK</c> — the second leg
    /// of the part-treasure pickup round-trip (the offer was buffered + announced
    /// by <see cref="PerformTreasurePickup"/>). The client's decision on the
    /// buffered <see cref="Ship.PendingTreasureOffer"/>:
    /// <list type="bullet">
    /// <item><b>Accept into a real mount</b> (<c>pfmTA-&gt;mountID != c_mountNA</c>,
    /// <c>FedSrv.CPP:6099-6119</c>): merge into an existing same-type part at that
    /// slot (<c>ppart-&gt;SetAmount(ppart-&gt;GetAmount() + amount)</c>,
    /// <c>FedSrv.CPP:6101-6103</c>) or mount fresh
    /// (<c>CreateAndAddPart(ppt, mountID, amount)</c>, <c>FedSrv.CPP:6105-6106</c>),
    /// then broadcast <c>ADD_PART(164)</c> sector-wide via
    /// <c>GetGroupSectorFlying(pcluster)</c> (<c>FedSrv.CPP:6108-6119</c>) with the
    /// <em>granted</em> (buffered) amount, not a merged total.</item>
    /// <item><b>Decline</b> (<c>pfmTA-&gt;mountID == c_mountNA</c>,
    /// <c>FedSrv.CPP:6125-6129</c>): re-create the part as a fresh treasure at the
    /// ship's <em>current</em> position (native <c>CreateTreasure(g.timeNow,
    /// pfsPlayer-&gt;GetIGCShip(), amount, ppt, GetPosition(), 25.0f, 120.0f)</c>)
    /// — a new object, since the original was destroyed on touch.</item>
    /// </list>
    /// Either way the pending offer is cleared (<c>FedSrv.CPP:6148</c>).
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><b>Gates.</b> Stage started (<c>FedSrv.CPP:6096</c>) and a non-null
    /// <see cref="Ship.PendingTreasureOffer"/> (native
    /// <c>GetTreasureObjectID() != NA</c>, <c>FedSrv.CPP:6097</c>). The task's
    /// enumerated gates only — the native's initial
    /// <c>TrapHackBoot(GetParentShip() == NULL)</c> (<c>FedSrv.CPP:6099</c>) is
    /// deliberately <b>not</b> ported: it would boot every ordinary unboarded
    /// pilot sending an ack, which is exactly the primary (working) scenario, so
    /// it reads as a native quirk inconsistent with the feature. Stated, not
    /// silently dropped.</item>
    /// <item><b>Bad mount = native TrapHackBoot abort.</b> A mount out of the
    /// hull's hardpoint range, or one whose <c>PartMask</c> cannot host the part
    /// type (native <c>TrapHackBoot(!CanMount(ppt, pfmTA-&gt;mountID))</c>,
    /// <c>FedSrv.CPP:6100</c>), is a hack: this port's TrapHackBoot analogue is
    /// log-and-return WITHOUT clearing the offer — native's boot abandons the
    /// handler before the <c>SetTreasureObjectID(NA)</c> at the end.</item>
    /// <item><b>Fresh-mount fraction.</b> <see cref="Ship.MountPart"/> is given
    /// <see cref="ExpandedPartData.FullyMounted"/> — an accepted part is mounted
    /// up, the same observer-facing stance the port's other mount paths use.</item>
    /// <item><b>Send target: sector-wide</b> — <see cref="SendSectorWide"/> for
    /// both the <c>ADD_PART</c> echo and the decline's <c>TreasureSpawn</c>,
    /// matching native <c>GetGroupSectorFlying(pcluster)</c>.</item>
    /// </list>
    /// Runs on the sim thread (dispatch drains inside <see cref="Tick"/>), so it
    /// may mutate the ship's <c>_parts</c> / offer state without extra locking,
    /// like every other inbound handler.
    /// </remarks>
    private void HandleTreasureAck(ConnectionId from, LiveShip live, TreasureAck ack)
    {
        var ship = live.Ship;
        var mission = ship.Mission;
        if (mission is null || mission.Stage != MissionStage.Started)
        {
            Console.WriteLine($"[treasureack] {from} ship {ship.Id}: {(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — ignored (native gate, FedSrv.CPP:6096)");
            return;
        }
        if (ship.PendingTreasureOffer is not { } offer)
        {
            Console.WriteLine($"[treasureack] {from} ship {ship.Id}: no pending part-treasure offer — ignored (native gate GetTreasureObjectID()!=NA, FedSrv.CPP:6097)");
            return;
        }

        // Resolve the buffered part type from the static core (native
        // pMission->GetPartType(oid), FedSrv.CPP:6104) — the port's part-type
        // lookup is Mission.Core.PartTypes (StaticCore).
        if (!mission.Core.PartTypes.TryGetValue(offer.PartTypeId, out var partType))
        {
            Console.WriteLine($"[treasureack] {from} ship {ship.Id}: buffered part-type {offer.PartTypeId} absent from core — offer cleared");
            ship.PendingTreasureOffer = null;
            return;
        }

        if (ack.MountId != TreasureAck.MountNA)
        {
            // Accept into a real mount.
            byte mount = ack.MountId;
            var hardpoints = ship.HullType.Hardpoints;
            if (mount >= hardpoints.Length || !CanMount(partType, hardpoints[mount].PartMask))
            {
                Console.WriteLine($"[treasureack] {from} ship {ship.Id}: mount {mount} cannot mount part {offer.PartTypeId} — dropped (native TrapHackBoot !CanMount, FedSrv.CPP:6100)");
                return;
            }

            // Merge into an existing same-type part at this mount, else mount
            // fresh (the exact _parts lookup TrySwapPart/TryRemovePart reuse,
            // then native FedSrv.CPP:6101-6106).
            var existing = ship.Parts.FirstOrDefault(p => p.EquipmentType == partType.EquipmentType && p.MountId == mount);
            if (existing is not null)
            {
                ship.SetPartAmount(existing, (short)(existing.Amount + offer.Amount));
                Console.WriteLine($"[treasureack] {from} ship {ship.Id}: merged part {offer.PartTypeId} amount {offer.Amount} into existing mount {mount} (now {existing.Amount + offer.Amount})");
            }
            else
            {
                ship.MountPart(partType, offer.Amount, mount, ExpandedPartData.FullyMounted);
                Console.WriteLine($"[treasureack] {from} ship {ship.Id}: mounted fresh part {offer.PartTypeId} amount {offer.Amount} at mount {mount}");
            }

            // Broadcast ADD_PART(164) sector-wide with the granted (buffered)
            // amount — native pfmAddPart->newPartData.amount = amount, the
            // granted quantity, not a merged total (FedSrv.CPP:6110-6116).
            var add = new AddPart
            {
                ShipID = ship.Id,
                NewPartData = new PartData(offer.PartTypeId, offer.Amount, mount),
            };
            SendSectorWide(ship.Sector!.Id, add.Encode());
            Console.WriteLine($"[treasureack] {from} ship {ship.Id}: -> ADD_PART(164) shipID={ship.Id} part={offer.PartTypeId} amount={offer.Amount} mount={mount} sector {ship.Sector.Id} wide");
        }
        else
        {
            // Declined: drop it back into space at the ship's CURRENT position
            // (native CreateTreasure(g.timeNow, pfsPlayer->GetIGCShip(), amount,
            // ppt, GetPosition(), 25.0f, 120.0f), FedSrv.CPP:6127-6129) — a NEW
            // treasure (not the original, which was destroyed on touch).
            var treasure = ship.CreateDropTreasure(partType, offer.Amount, _simNow, (uint)TickIndex);
            SendSectorWide(ship.Sector!.Id, new TreasureSpawn { Data = treasure.Data }.Encode());
            Console.WriteLine($"[treasureack] {from} ship {ship.Id}: declined part {offer.PartTypeId} amount {offer.Amount} -> re-created treasure obj={treasure.Data.ObjectId} -> TREASURE(28) sector {ship.Sector.Id} wide");
        }

        ship.PendingTreasureOffer = null;   // clear the buffer either way (FedSrv.CPP:6148)
    }

    /// <summary>
    /// P108+P109 — the server-side board action, the port of
    /// <c>FedSrv.CPP:2322-2475</c> <c>BoardShip()</c>. Establishes turret/
    /// observer <b>occupancy</b> (P108) <em>and</em>, since P109, the real
    /// weapon-<b>gunner binding</b> (native <c>SetTurretID</c>-to-<c>SetGunner</c>,
    /// <c>shipIGC.h:1405-1437</c>), so a boarded gunner actually crews the
    /// carrier's weapon at its slot.
    /// Returns true on a successful board; false lets the caller send
    /// <c>BOARD_NACK</c>. Steps mirrored from the native (each cited):
    /// <list type="bullet">
    /// <item><b>Self-exclusion</b> — <c>pship != pshipBoard</c>
    /// (<c>FedSrv.CPP:2332</c>).</item>
    /// <item><b>Carrier is a player ship</b> — <c>pfsShipBoard-&gt;IsPlayer()</c>
    /// (<c>FedSrv.CPP:2340</c>). In this port that means the carrier has an
    /// owning peer in <c>_peerShips</c> (the player/bot distinction). The
    /// dispatcher already enforced same-side via <see cref=\"FindSameSideShip\"/>,
    /// mirroring native <c>GetShip()</c> on the boarder's side.</item>
    /// <item><b>Both docked</b> — the native precondition
    /// (<c>FedSrv.CPP:2342-2345</c>) requires the boarder and carrier <c>not in
    /// a cluster</c> and <c>at a station</c> (boarding a docked ship from a
    /// docked ship). In this port a docked ship is <c>StationId != null</c>
    /// (docked ships retain their <see cref=\"Ship.Sector\"/>), so the gate
    /// translates to both <c>StationId</c>s being set.</item>
    /// <item><b>Turret-slot search</b> — <see cref=\"ChooseTurretSlot\"/>,
    /// faithful to the native walk (<c>FedSrv.CPP:2347-2402</c>): no turret
    /// slots ⇒ observer; else pick the first unoccupied, equipped
    /// <c>[MaxFixedWeapons, MaxWeapons)</c> slot, falling back to observer.</item>
    /// <item><b>Observer gate collapsed</b> — native
    /// <c>(turret != NA) || CanCheat()</c> (<c>FedSrv.CPP:2406</c>); this port
    /// has no cheat/admin concept, so per the P100/P103 precedent the gate
    /// collapses to always-true — observer boarding is allowed unconditionally.</item>
    /// <item><b>Defensive detach of the boarder's own children</b> — the native
    /// <c>while (psl = pchildren-&gt;first()) LeaveShip(...)</c>
    /// (<c>FedSrv.CPP:2415-2420</c>). No nested boarding exists in this port, so
    /// the list is always empty; kept defensively per the task.</item>
    /// <item><b>Money refund</b> — <c>refund = pship-&gt;GetValue()</c>
    /// (<c>FedSrv.CPP:2428</c>, hull price + Σ part prices,
    /// <c>shipIGC.h:396-406</c>). <b>Simplification:</b> the native credits the
    /// player's individual money and broadcasts <c>MONEY_CHANGE</c>
    /// (<c>FedSrv.CPP:2433-2442</c>); this port has no per-player wallet and no
    /// <c>MONEY_CHANGE</c> codec, so the refund is deposited into the boarder's
    /// side treasury (<see cref=\"Side.Money\"/>) — the P51-59 money primitive —
    /// where payday distributes it. Stated plainly, not a silent drop.</item>
    /// <item><b>State transition</b> — <c>SetParentShip / SetTurretID</c>
    /// (<c>FedSrv.CPP:2445-2446</c>); the boarder's parts are cleared (the
    /// native <c>SetParentShip</c> with no old parent terminates them,
    /// <c>shipIGC.cpp:3619-3633</c> — \"no parts allowed on an observer or
    /// turret\"); if the carrier is docked the boarder inherits its dock state
    /// (<c>FedSrv.CPP:2456-2457</c>). The wire <c>SHIP_STATUS</c> (hull NA,
    /// parent id, turret/observer state) is derived in
    /// <see cref=\"MakeOwnShipStatus\"/>, so the existing per-tick refresh is
    /// what reflects the change — no new message.</item>
    /// </list>
    /// </summary>
    private bool PerformBoard(Ship boarder, Ship carrier)
    {
        if (ReferenceEquals(boarder, carrier))
        {
            Console.WriteLine($"[boardship] ship {boarder.Id} tried to board itself — refused (pship != pshipBoard, FedSrv.CPP:2332)");
            return false;
        }

        // pfsShipBoard->IsPlayer() (FedSrv.CPP:2340) — the carrier must be a
        // peer-owned (player) ship in this port.
        if (!_peerShips.Any(kv => ReferenceEquals(kv.Value.Ship, carrier)))
        {
            Console.WriteLine($"[boardship] ship {boarder.Id} -> {carrier.Id}: carrier is not a player ship (FedSrv.CPP:2340) — refused");
            return false;
        }

        // Both docked: the native precondition (FedSrv.CPP:2342-2345).
        if (boarder.StationId is null || carrier.StationId is null)
        {
            Console.WriteLine($"[boardship] ship {boarder.Id} -> {carrier.Id}: boarder and carrier must both be docked (FedSrv.CPP:2342-2345) — refused");
            return false;
        }

        // Turret-slot search (FedSrv.CPP:2347-2402); null = observer.
        short? turret = ChooseTurretSlot(boarder, carrier);
        // Observer gate collapsed (no cheat/admin concept) — proceed regardless.

        // Defensive detach of the boarder's own existing children
        // (FedSrv.CPP:2415-2420): anyone riding on the boarder must fend for
        // themselves before the boarder climbs aboard the carrier. Always empty
        // today (no nested boarding), kept per the task's defensive guard.
        var kids = boarder.ChildShips.ToArray();
        foreach (var kid in kids)
            PerformLeave(kid, boarder);

        // Money refund (FedSrv.CPP:2426-2443) — computed before the parts are
        // cleared, matching native order (GetValue runs before SetParentShip
        // clears parts). Simplified to credit the side treasury (see doc).
        int refund = ShipValue(boarder);
        if (refund > 0 && boarder.SideId is { } bs && boarder.Mission is { } bm)
        {
            var side = bm.Sides.FirstOrDefault(s => s.Id == bs);
            if (side is not null)
            {
                side.Money += refund;
                // P118: now that the MONEY_CHANGE(60) codec exists, tell the
                // whole side its treasury moved — the faithful port of the
                // native board-refund broadcast (FedSrv.CPP:2435-2441, which
                // sends to CFSSide::FromIGC(pside)->GetGroup(), not just the
                // boarded player). sidFrom = NA marks the server-initiated
                // pooled-treasury credit (Messages.h:444 convention).
                BroadcastMoneyChangeSideWide(side.Id, refund, boarder.Id);
                Console.WriteLine($"[boardship] ship {boarder.Id} -> {carrier.Id}: refunded hardware value {refund} to side {side.Id} treasury + MONEY_CHANGE(60) to side");
            }
        }

        // SetParentShip + SetTurretID (FedSrv.CPP:2445-2446). Reparent cleanly
        // if the boarder was somehow already on another carrier, then register
        // as a child of the carrier.
        if (boarder.ParentShip is { } oldParent && !ReferenceEquals(oldParent, carrier))
            oldParent.RemoveChildShip(boarder);
        boarder.ParentShip = carrier;
        carrier.AddChildShip(boarder);
        boarder.TurretId = turret;

        // P109 — the turret weapon-gunner binding (native SetTurretID →
        // SetGunner, shipIGC.h:1405-1437): if the boarder occupies a real
        // turret slot (not an observer), bind it as the gunner of the
        // carrier's mounted weapon at that mount. The mount lookup reuses the
        // exact weapon→MountId mapping the P108 turret-slot walk already used
        // (MountedPart.MountId / Ship.Weapons[i].MountId), so no new
        // bookkeeping. An observer (turret null) binds nothing — it has no gun
        // to crew. Leave clears the binding (PerformLeave).
        if (turret is { } slot)
        {
            var turretWeapon = carrier.Weapons.FirstOrDefault(w => w.MountId == slot);
            if (turretWeapon is not null)
            {
                turretWeapon.SetGunner(boarder);
                Console.WriteLine($"[boardship] bound gunner ship {boarder.Id} to carrier {carrier.Id} turret weapon mount {slot}");
            }
            else
            {
                // A boarded turret slot with no realized weapon on it (the slot
                // check in ChooseTurretSlot looks at Parts, which may hold an
                // inert/other part). Bind nothing; occupancy stands.
                Console.WriteLine($"[boardship] turret slot {slot} on {carrier.Id} has no realized weapon — gunner binding skipped");
            }
        }

        // "No parts allowed on an observer or turret" (native SetParentShip with
        // no old parent terminates them, shipIGC.cpp:3619-3633).
        boarder.ClearLoadout();

        // If the carrier is docked, the boarder inherits its dock state
        // (FedSrv.CPP:2456-2457 SetStation(pstationBoard)).
        if (carrier.StationId is { } cs)
            boarder.StationId = cs;

        Console.WriteLine($"[boardship] ship {boarder.Id} boarded {carrier.Id} as " +
            $"{(turret is null ? "observer (no turret slot)" : $"turret gunner slot {turret}")} — no ack, rides SHIP_STATUS refresh");
        return true;
    }

    /// <summary>
    /// P108 — the server-side leave action, the port of the internal C++
    /// <c>LeaveShip(CFSShip*, IshipIGC*)</c> (<c>FedSrv.CPP:2277-2320</c>) — the
    /// <em>function</em>, not the wire message (which it builds and sends at
    /// <c>FedSrv.CPP:2312-2316</c>). Called for the <c>sidParent == NA</c> leave
    /// and for the boarder's own children during <see cref=\"BoardShip\"/>.
    /// Returns true on success; false lets the caller send <c>BOARD_NACK</c>.
    /// <list type="bullet">
    /// <item><b>Gate: the carrier is not in a cluster</b> — native
    /// <c>pshipParent-&gt;GetCluster() == NULL</c> (<c>FedSrv.CPP:2285</c>);
    /// in this port a docked carrier is <c>StationId != null</c>.</item>
    /// <item><b>Clears parent/turret</b> and removes this ship from the
    /// carrier's child list (native <c>SetParentShip(NULL)</c>,
    /// <c>shipIGC.cpp:3677-3689</c> DeleteChildShip).</item>
    /// <item><b>Restores a hull</b> — the side's civilization's Lifepod hull
    /// (native <c>SetBaseHullType(lifepod)</c>, <c>FedSrv.CPP:2301-2302</c>),
    /// the same resolution the death-respawn slice uses.</item>
    /// <item><b>Broadcasts <c>LEAVE_SHIP(94)</c> to the whole side</b>
    /// (<c>FedSrv.CPP:2312-2316</c>, <c>sidChild</c> = the detaching ship's id).</item>
    /// </list>
    /// The <c>SHIP_STATUS</c> consequences (state back to docked, parent NA,
    /// hull = the lifepod) are derived in <see cref=\"MakeOwnShipStatus\"/> and
    /// reflected by the existing per-tick refresh.
    /// </summary>
    private bool PerformLeave(Ship boarder, Ship carrier)
    {
        if (carrier.StationId is null)
        {
            Console.WriteLine($"[leaveship] ship {boarder.Id} from {carrier.Id}: carrier not docked — leave refused (FedSrv.CPP:2285)");
            return false;
        }
        if (boarder.ParentShip is not { } parent || !ReferenceEquals(parent, carrier))
        {
            Console.WriteLine($"[leaveship] ship {boarder.Id} from {carrier.Id}: not actually boarded on it — leave refused");
            return false;
        }

        // Clear the parent/turret relationship (native SetParentShip(NULL)):
        // remove from the carrier's child list, then null our back-pointer.
        carrier.RemoveChildShip(boarder);
        boarder.ParentShip = null;

        // P109 — release the turret weapon-gunner binding (native
        // SetParentShip(NULL) → SetGunner(NULL) on the occupied mount,
        // shipIGC.cpp:3652,3681). Capture the slot before clearing it and
        // unbind the carrier's weapon there so the freed turret stops firing
        // for the departing gunner.
        if (boarder.TurretId is { } slot)
        {
            var turretWeapon = carrier.Weapons.FirstOrDefault(w => w.MountId == slot);
            turretWeapon?.SetGunner(null);
        }
        boarder.TurretId = null;

        // Restore the side's Lifepod hull (FedSrv.CPP:2301-2302).
        if (boarder.SideId is { } sideId && boarder.Mission is { } leaveMission)
        {
            var side = leaveMission.Sides.FirstOrDefault(s => s.Id == sideId);
            if (side is not null && leaveMission.Core.HullTypes.TryGetValue(side.Civilization.Lifepod, out var lp))
            {
                boarder.ChangeHull(lp);
            }
            else
            {
                Console.WriteLine($"[leaveship] ship {boarder.Id} side {sideId}: lifepod hull unresolvable — leaving as current hull");
            }
        }

        // LEAVE_SHIP(94) to the WHOLE side, naming the detaching ship
        // (FedSrv.CPP:2312-2316) — not to a single peer.
        if (boarder.SideId is { } sideId2)
        {
            var leave = new LeaveShip { SidChild = boarder.Id };
            byte[] packet = FedPacket.Pack(new[] { leave.Encode() });
            int recipients = 0;
            foreach (var kv in _peerShips)
                if (kv.Value.Ship.SideId == sideId2) { SendPeer(kv.Key, packet); recipients++; }
            Console.WriteLine($"[leaveship] ship {boarder.Id} left {carrier.Id} — LEAVE_SHIP(94) sidChild={boarder.Id} -> side {sideId2} ({recipients} peer(s))");
        }
        else
        {
            Console.WriteLine($"[leaveship] ship {boarder.Id} left {carrier.Id} — no side, LEAVE_SHIP(94) suppressed");
        }
        return true;
    }

    /// <summary>
    /// P111 — the <c>FM_C_RIPCORD_REQUEST(184)</c> handler, the port of
    /// <c>FedSrv.CPP:7715-7728</c> <c>case FM_C_RIPCORD_REQUEST</c>.
    /// Mirrors the native gate exactly (each sub-condition confirmed in
    /// <c>FedSrv.CPP:7717-7721</c>): only a mission-started, real-side,
    /// un-boarded, flying ship whose hull can ripcord may proceed. A request
    /// that fails any gate is <b>silently ignored</b> — native does <c>break</c>;
    /// there is no denial message for a gate violation (only a request that
    /// names a cluster with no valid anchor yields <c>RIPCORD_DENIED</c>, which
    /// is decided later, inside <c>RequestRipcord</c>).
    /// <list type="bullet">
    /// <item><b>Mission started.</b> <c>GetStage() != STAGE_STARTED</c> →
    /// ignored (<c>FedSrv.CPP:7717</c>).</item>
    /// <item><b>Real side.</b> <c>GetSide()-&gt;GetObjectID() &lt; 0</c> → ignored
    /// (<c>FedSrv.CPP:7717</c>). Mirrored as <c>SideId &lt; 0</c>.</item>
    /// <item><b>Not boarded.</b> <c>GetParentShip()</c> → ignored
    /// (<c>FedSrv.CPP:7718</c>). A boarded ship is not its own actor. (Native
    /// always has this gate live; there is no landing-dependency caveat — it
    /// is independent of cluster E.)</item>
    /// <item><b>Flying.</b> <c>GetCluster() == NULL</c> → ignored
    /// (<c>FedSrv.CPP:7719</c>). Native clears the cluster on dock
    /// (<c>shipIGC.h:457,459</c>), so a docked ship has no cluster. In this port
    /// a docked ship retains its <c>Sector</c> but has <c>StationId != null</c>
    /// (the P103/P106 convention), so "flying" = <c>Sector != null &amp;&amp;
    /// StationId == null</c>.</item>
    /// <item><b>Not a flag-carrier.</b> <c>GetFlag() != NA</c> → ignored
    /// (<c>FedSrv.CPP:7720</c>). This port tracks no flags (the same
    /// always-passing gate-collapse precedent P100/P107 used); phase 4 /
    /// <c>GAIN_FLAG</c> territory.</item>
    /// <item><b>Hull can ripcord.</b> <c>GetBaseHullType()-&gt;HasCapability(
    /// c_habmNoRipcord)</c> → ignored (<c>FedSrv.CPP:7721</c>). <c>c_habmNoRipcord
    /// = 0x20</c> (<c>igc.h:731</c>), read like P107 read <c>c_habmLifepod</c>.</item>
    /// </list>
    /// Runs on the sim thread (the dispatch drains inside <see cref="Tick"/>),
    /// so it may mutate <c>_peerShips</c>/ships and send without locking.
    /// </summary>
    private void HandleRipcordRequest(ConnectionId from, LiveShip live, RipcordRequest req)
    {
        var ship = live.Ship;
        var mission = ship.Mission;

        if (mission is null || mission.Stage != MissionStage.Started)
        {
            Console.WriteLine($"[ripcord] {from} ship {ship.Id}: {(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — request ignored (gate FedSrv.CPP:7717)");
            return;
        }
        if (ship.SideId is not { } side || side < 0)
        {
            Console.WriteLine($"[ripcord] {from} ship {ship.Id}: side {ship.SideId?.ToString() ?? "(none)"} < 0 — request ignored (gate FedSrv.CPP:7717)");
            return;
        }
        if (ship.ParentShip is not null)
        {
            Console.WriteLine($"[ripcord] {from} ship {ship.Id}: boarded (parent {ship.ParentShip.Id}) — request ignored (gate FedSrv.CPP:7718)");
            return;
        }
        if (ship.Sector is null || ship.StationId is not null)
        {
            Console.WriteLine($"[ripcord] {from} ship {ship.Id}: {(ship.Sector is null ? "no cluster" : "docked")} — request ignored (gate GetCluster(), FedSrv.CPP:7719)");
            return;
        }
        if (ship.HullType.HasCapability(DataHullTypeIGC.HabmNoRipcord))
        {
            Console.WriteLine($"[ripcord] {from} ship {ship.Id}: hull {ship.HullType.HullId} has c_habmNoRipcord — request ignored (gate FedSrv.CPP:7721)");
            return;
        }
        // Flag-carrier gate (FedSrv.CPP:7720) collapses to always-passing: this
        // port tracks no flags. Documented, not silently dropped.

        // Native resolves the requested sector via
        // GetIGCMission()->GetCluster(sidRipcord), which returns NULL for an
        // unknown id — and RequestRipcord treats pcluster==NULL exactly like
        // sidRipcord==NA (a cancel). Mirror that so a bogus sector id behaves
        // as a cancel, never as a wildcard.
        var target = mission.Sectors.FirstOrDefault(s => s.Id == req.SidRipcord);
        RequestRipcord(from, ship, target);
    }

    /// <summary>
    /// P111 — the port of <c>FedSrvSiteBase::RequestRipcord</c>
    /// (<c>FedSrv.CPP:13319-13378</c>), the real countdown-start state machine
    /// a client <c>RIPCORD_REQUEST</c> reaches after the handler gate.
    /// <paramref name="target"/> is the requested target sector (null when the
    /// request named <c>NA</c> or an unknown sector id). Three outcomes:
    /// <list type="number">
    /// <item><b>Cancel</b> — <c>target == null</c> with an active ripcord: clear
    /// the model and broadcast <c>RIPCORD_ABORTED(187)</c> sector-wide to
    /// everyone flying in the requester's current cluster (<c>FedSrv.CPP:13327-13339</c>).</item>
    /// <item><b>(Re)activate</b> — a valid anchor is found in the requested
    /// sector via <see cref="FindRipcordModel"/>: set the model, re-seed the
    /// countdown (<c>ResetRipcordTimeLeft</c>), and send
    /// <c>RIPCORD_ACTIVATE(185)</c> — <em>sector-wide</em> on a first activation
    /// (<c>pmodelOld == NULL</c>) or to the <em>requester alone</em> on a
    /// re-target (<c>FedSrv.CPP:13366-13368</c>). The <c>sidRipcord</c> field is
    /// the <b>requested</b> cluster id (<c>pcluster-&gt;GetObjectID()</c>,
    /// <c>FedSrv.CPP:13364</c>), not the anchor's own sector.</item>
    /// <item><b>Deny</b> — no valid anchor: <c>RIPCORD_DENIED(186)</c> to the
    /// requester alone (<c>FedSrv.CPP:13371-13376</c>).</item>
    /// </list>
    /// If the ship is already anchored in the requested sector the request is
    /// a no-op (the outer <c>else if</c> of <c>FedSrv.CPP:13341-13342</c>).
    /// </summary>
    private void RequestRipcord(ConnectionId from, Ship ship, Sector? target)
        => RequestRipcordCore(ship, target, from);

    /// <summary>
    /// P172 — the connection-agnostic core of <see cref="RequestRipcord"/> (the
    /// P111 countdown-start state machine, port of
    /// <c>FedSrvSiteBase::RequestRipcord</c>, <c>FedSrv.CPP:13319-13378</c>).
    /// <paramref name="from"/> is the initiating peer's connection for a real-
    /// player request, or <c>null</c> for an NPC. <b>NPC-connection resolution</b>
    /// (the task's open question): an ownerless NPC has no <c>_peerShips</c> entry
    /// (P147 — <c>SpawnNpcShip</c> creates no peer; a stale <c>SendPeer</c> would
    /// throw "not connected" in the transport), so the requester-only
    /// <c>SendPeer</c> sends below are skipped when <paramref name="from"/> is
    /// <c>null</c>, while the sector-wide first-activation broadcast
    /// (<see cref="SendRipcordSectorWide"/>) still reaches any real peers flying
    /// in the ripper's sector. Same state machine, unchanged, for both paths;
    /// every downstream step (countdown, revalidation, teleport) is
    /// connection-agnostic, so the NPC ripcord completes exactly like a player's.
    /// </summary>
    private void RequestRipcordCore(Ship ship, Sector? target, ConnectionId? from)
    {
        var current = ship.RipcordModel;
        if (target is null)
        {
            // sidRipcord == NA (or unknown): cancel an active ripcord.
            if (current is not null)
            {
                ship.SetRipcordModel(null);
                SendRipcordSectorWide(ship.Sector!.Id,
                    new RipcordAborted { ShipidRipcord = ship.Id }.Encode());
                Console.WriteLine($"[ripcord] ship {ship.Id} cancels -> RIPCORD_ABORTED(187) sector {ship.Sector.Id} wide");
            }
            return;
        }

        if (current is { } cur && cur.ClusterId == target.Id)
            return;   // already anchored in this sector — nothing to do (FedSrv.CPP:13341-13342)

        var found = FindRipcordModel(ship, target);
        if (found is { } anchor)
        {
            bool firstActivation = current is null;
            ship.SetRipcordModel(anchor);
            ship.ResetRipcordTimeLeft();

            var msg = new RipcordActivate
            {
                ShipidRipcord = ship.Id,
                OtRipcord = anchor.Ot,
                OidRipcord = anchor.Oid,
                SidRipcord = target.Id,   // the REQUESTED cluster, FedSrv.CPP:13364
            };
            if (firstActivation)
            {
                SendRipcordSectorWide(ship.Sector!.Id, msg.Encode());
                Console.WriteLine($"[ripcord] ship {ship.Id} FIRST activation anchor (ot={anchor.Ot} oid={anchor.Oid}) -> sector {target.Id} RIPCORD_ACTIVATE(185) sector {ship.Sector.Id} wide");
            }
            else
            {
                if (from is { } f) SendPeer(f, FedPacket.Pack(new[] { msg.Encode() }));
                Console.WriteLine($"[ripcord] ship {ship.Id} RE-TARGET anchor (ot={anchor.Ot} oid={anchor.Oid}) -> sector {target.Id} RIPCORD_ACTIVATE(185) requester-only");
            }
        }
        else
        {
            if (from is { } f) SendPeer(f, FedPacket.Pack(new[] { new RipcordDenied().Encode() }));
            Console.WriteLine($"[ripcord] ship {ship.Id}: no valid anchor in sector {target.Id} -> RIPCORD_DENIED(186) requester-only");
        }
    }

    /// <summary>
    /// P172 — the <see cref="ServerHost.RequestRipcordReceived"/> handler: an
    /// <b>NPC</b>-initiated ripcord request routed up through the IIgcSite seam
    /// (<c>Ship.TryRipcordOrDefer</c> → <c>Mission.Site.RequestRipcord</c> →
    /// this handler). Enters the exact same <see cref="RequestRipcordCore"/>
    /// state machine with a <c>null</c> connection — the NPC-connection
    /// resolution (no <c>_peerShips</c> entry, so the requester-only sends are
    /// skipped; sector-wide broadcasts and the whole countdown/teleport chain are
    /// connection-agnostic). Runs on the sim thread (called from a ship's
    /// <c>OnUpdate</c> inside <c>TickSim</c>), same threading as every inbound
    /// handler. Runs on the sim thread, like the direct real-player path.
    /// </summary>
    private void OnNpcRequestRipcord(Ship ship, Sector? target)
        => RequestRipcordCore(ship, target, null);

    /// <summary>Send a pre-encoded server message to every peer currently
    /// flying in <paramref name="sectorId"/> — the port's
    /// <c>GetGroupSectorFlying(pclusterShip)</c> (<c>FedSrv.CPP:13338</c>), the
    /// sector-wide broadcast both the ripcord slices and the P117 relaunch
    /// (<c>FedSrv.CPP:13019</c>) use.</summary>
    private void SendSectorWide(short sectorId, byte[] encoded)
    {
        var packet = FedPacket.Pack(new[] { encoded });
        foreach (var kv in _peerShips)
            if (kv.Value.Sector.Id == sectorId)
                SendPeer(kv.Key, packet);
    }

    /// <summary>Broadcast a <c>MONEY_CHANGE(60)</c> to every peer seated on
    /// side <paramref name="sideId"/> — the P118 side-wide money broadcast,
    /// port of the native <c>MONEY_CHANGE</c> sends that go to the whole side's
    /// peer group (<c>CFSSide::FromIGC(pside)-&gt;GetGroup()</c>, e.g.
    /// <c>FedSrv.CPP:2435-2441</c>, <c>7202-7206</c>). <c>sidFrom = NA</c>
    /// marks the server-initiated pooled-treasury credit, matching the native
    /// convention (<c>Messages.h:444</c>) for this port's refunds.</summary>
    private void BroadcastMoneyChangeSideWide(short sideId, int dMoney, short sidTo)
    {
        var targets = _peerShips
            .Where(kv => kv.Value.Ship.SideId == sideId)
            .Select(kv => kv.Key)
            .ToArray();
        if (targets.Length == 0) return;   // no peers on the side — nothing to tell
        var msg = new MoneyChange { DMoney = dMoney, SidTo = sidTo, SidFrom = -1 /* NA */ };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        foreach (var t in targets) SendPeer(t, packet);
        Console.WriteLine($"[moneychange] side {sideId} -> MONEY_CHANGE(60) dMoney={dMoney} sidTo={sidTo} sidFrom=NA to {targets.Length} peer(s)");
    }

    /// <summary>
    /// Handle a client <c>FM_C_BUCKET_DONATE(64)</c> — the P119 port of
    /// <c>FedSrv.CPP:7137-7211</c> <c>case FM_C_BUCKET_DONATE</c>, changed in
    /// P198 to spend from the donating player's own wallet. Gates on
    /// <c>STAGE_STARTED</c> (:7139), resolves the donating side and its bucket
    /// by id (:7145), and validates the <b>donating player's personal wallet</b>
    /// (<see cref="PlayerWallet.Money"/>, P197) covers the offered amount —
    /// the real native gate <c>pfsPlayer-&gt;GetMoney() &gt;= moneyGiven</c>
    /// (:7148). <see cref="Side.Money"/> is no longer consulted or debited by
    /// this handler at all: income still accrues there via payday, but it is no
    /// longer a bucket-spend source (P198 scope boundary — accrual untouched).
    /// The donation is capped by <c>Bucket.AddMoney</c> at what the bucket still
    /// needs (bucketigc.h:281-293); only the actually-spent figure is deducted
    /// from the player's wallet, and the excess is returned to the player as an
    /// explicit refund <c>MONEY_CHANGE(60)</c> (native :7151-7160, :7199-7208).
    /// On a successful spend a negative <c>MONEY_CHANGE(60)</c> is broadcast to
    /// the whole side (:7162-7168), and finally a <c>BUCKET_STATUS(65)</c> to
    /// the whole side (:7183-7196).
    /// </summary>
    private void HandleBucketDonate(ConnectionId from, LiveShip live, BucketDonate donate)
    {
        var mission = live.Ship.Mission;
        if (mission is null || mission.Stage != MissionStage.Started)
        {
            Console.WriteLine($"[bucketdonate] {from}: ship {live.Ship.Id}: {(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — ignored (native gate FedSrv.CPP:7139)");
            return;
        }
        if (live.Ship.SideId is not { } sideId)
        {
            Console.WriteLine($"[bucketdonate] {from}: ship {live.Ship.Id}: no side yet — ignored");
            return;
        }
        var side = mission.Sides.FirstOrDefault(s => s.Id == sideId);
        if (side is null)
        {
            Console.WriteLine($"[bucketdonate] {from}: side {sideId} not found in mission — ignored");
            return;
        }
        var bucket = side.Buckets.FirstOrDefault(b => b.Id == donate.IBucket);
        if (bucket is null)
        {
            Console.WriteLine($"[bucketdonate] {from}: side {sideId} has no bucket {donate.IBucket} — ignored");
            return;
        }
        if (donate.MoneyGiven < 0)
        {
            Console.WriteLine($"[bucketdonate] {from}: negative donation {donate.MoneyGiven} — ignored (native trap FedSrv.CPP:7149)");
            return;
        }
        // P198: the real native gate — the DONATING PLAYER's own wallet, not the
        // side treasury (FedSrv.CPP:7148, pfsPlayer->GetMoney() >= moneyGiven).
        if (live.Wallet.Money < donate.MoneyGiven)
        {
            Console.WriteLine($"[bucketdonate] {from}: player {live.Ship.Id} wallet {live.Wallet.Money} < donated {donate.MoneyGiven} — ignored (native per-player gate FedSrv.CPP:7148)");
            return;
        }

        long spent = bucket.AddMoney(donate.MoneyGiven);   // caps at still-needed; returns actually spent (bucketigc.h:289-292)
        live.Wallet.Money -= (int)spent;   // spend from the PLAYER's wallet, not the side (FedSrv.CPP:7160)
        long refund = donate.MoneyGiven - spent;           // amount the bucket didn't need (native mnyRefund, :7151-7158)
        Console.WriteLine($"[bucketdonate] {from}: player {live.Ship.Id} donated {donate.MoneyGiven} into bucket {bucket.Id} -> spent {spent}, invested {bucket.InvestedMoney}/{bucket.Price}, wallet now {live.Wallet.Money}");

        // MONEY_CHANGE(60) to the whole side: negative delta, sidTo == sidFrom ==
        // the donating player's own ship (native FedSrv.CPP:7162-7168).
        BroadcastBucketDonateSpendSideWide(sideId, -(int)spent, live.Ship.Id);

        // Excess the bucket didn't need: explicit refund MONEY_CHANGE(60) to the
        // donating player alone, positive delta, sidFrom = NA (native :7199-7208).
        if (refund > 0)
        {
            var mc = new MoneyChange { DMoney = (int)refund, SidTo = live.Ship.Id, SidFrom = -1 /* NA */ };
            SendPeer(from, FedPacket.Pack(new[] { mc.Encode() }));
            Console.WriteLine($"[moneychange] -> {from}: MONEY_CHANGE(60) dMoney=+{refund} sidTo={live.Ship.Id} sidFrom=NA (bucket-donation excess refund)");
        }

        BroadcastBucketStatusSideWide(sideId, bucket);     // :7183-7196
    }

    /// <summary>
    /// Broadcast the bucket-donation <em>spend</em> <c>MONEY_CHANGE(60)</c> to
    /// every peer seated on side <paramref name="sideId"/> — the P198 port of
    /// the native donation handler's spend notification (<c>FedSrv.CPP:7162-7168</c>,
    /// <c>g.fm.SendMessages(CFSSide::FromIGC(pside)-&gt;GetGroup(), ...)</c>).
    /// Unlike the server-initiated <see cref="BroadcastMoneyChangeSideWide"/>
    /// (whose <c>SidFrom = NA</c> marks a pooled-treasury credit), this is a
    /// <b>player-initiated</b> purchase, so <c>SidFrom == SidTo</c> — both the
    /// donating player's own ship — matching native's
    /// <c>pfmMoneyChange-&gt;sidFrom = pfmMoneyChange-&gt;sidTo</c>.
    /// </summary>
    private void BroadcastBucketDonateSpendSideWide(short sideId, int dMoney, short shipId)
    {
        var targets = _peerShips
            .Where(kv => kv.Value.Ship.SideId == sideId)
            .Select(kv => kv.Key)
            .ToArray();
        if (targets.Length == 0) return;   // no peers on the side — nothing to tell
        var msg = new MoneyChange { DMoney = dMoney, SidTo = shipId, SidFrom = shipId };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        foreach (var t in targets) SendPeer(t, packet);
        Console.WriteLine($"[moneychange] side {sideId} -> MONEY_CHANGE(60) dMoney={dMoney} sidTo={shipId} sidFrom={shipId} to {targets.Length} peer(s)");
    }

    /// <summary>
    /// Broadcast a <c>BUCKET_STATUS(65)</c> for <paramref name="bucket"/> to
    /// every peer seated on side <paramref name="sideId"/> — the P119 side-wide
    /// bucket-status broadcast, port of the native donation handler's send to
    /// the whole side's peer group (<c>CFSSide::FromIGC(pside)-&gt;GetGroup()</c>,
    /// <c>FedSrv.CPP:7183-7196</c>). timeTotal = invested build ms,
    /// moneyTotal = invested money (bucketigc.h:264-272).
    /// </summary>
    private void BroadcastBucketStatusSideWide(short sideId, Bucket bucket)
    {
        var targets = _peerShips
            .Where(kv => kv.Value.Ship.SideId == sideId)
            .Select(kv => kv.Key)
            .ToArray();
        if (targets.Length == 0) return;   // no peers on the side — nothing to tell
        var msg = new BucketStatus
        {
            TimeTotal = (uint)bucket.InvestedMilliseconds,
            SideId = sideId,
            MoneyTotal = (int)bucket.InvestedMoney,
            IBucket = bucket.Id,
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        foreach (var t in targets) SendPeer(t, packet);
        Console.WriteLine($"[bucketstatus] side {sideId} -> BUCKET_STATUS(65) iBucket={bucket.Id} money={bucket.InvestedMoney}/{bucket.Price} timeMs={bucket.InvestedMilliseconds} to {targets.Length} peer(s)");
    }

    /// <summary>Legacy name for <see cref="SendSectorWide"/> retained by the
    /// ripcord call sites; delegates to the canonical generic helper.</summary>
    private void SendRipcordSectorWide(short sectorId, byte[] encoded) => SendSectorWide(sectorId, encoded);

    /// <summary>
    /// P113 — the port of <c>FedSrvSiteBase::UseRipcord</c>
    /// (<c>FedSrv.CPP:13440-13500</c>), the actual relocation that runs when a
    /// ripcord countdown reaches zero with a still-legal anchor. Drained from
    /// <c>Sector.ResolveRipcordCountdowns</c> (via <c>DrainRipcordTeleports</c>)
    /// in the same <see cref="SimPass"/> slot the P112 abort broadcast uses —
    /// the sector pass detects the zero-cross and hands the ripper off here,
    /// exactly the sector-detects / host-acts split that governs the abort path.
    /// The anchor was just revalidated legal in the same tick, so resolving it
    /// here is a straight object lookup (no re-validation).
    /// <para>Three outcomes, mirroring the native function which always returns
    /// <c>true</c> (so the ripper's ripcord state is cleared — the attempt is
    /// <b>consumed, never retried on a later tick</b> even when it aborts):</para>
    /// <list type="number">
    /// <item><b>Station anchor</b> (<c>FedSrv.CPP:13441-13443</c>): native
    /// delegates to the station's own <c>Launch</c>, the exact mechanic the
    /// port already uses for undocking — so this calls the shared
    /// <see cref="RepositionForLaunch"/> (P69's launch core) and moves the
    /// ripper to the anchor station's cluster.</item>
    /// <item><b>Ship anchor with enough energy</b> (<c>FedSrv.CPP:13446-13474</c>):
    /// pay <c>RipcordCost</c> (the ripper's hull cost) from the target's energy,
    /// then the genuinely new ship-relative repositioning: <c>r = target.Radius +
    /// ripper.Radius + 25.0f</c> (<b>25, not the 50-margin station launch</b>),
    /// a <b>random</b> direction (<c>Vector::RandomDirection()</c>), the same
    /// <see cref="ExitStationSpeed"/> velocity (<c>c_fcidExitStationSpeed</c>),
    /// zeroed turn rates, and relocation to the target ship's cluster.</item>
    /// <item><b>Ship anchor without enough energy</b> (<c>FedSrv.CPP:13451-13461</c>):
    /// broadcast <c>RIPCORD_ABORTED(187)</c> to everyone flying in the ripper's
    /// cluster, do <b>not</b> move, and consume the attempt (state cleared) —
    /// native returns <c>true</c> either way, so it is not retried when the
    /// target later regains energy.</item>
    /// </list>
    /// <see cref="ShipStatusSpotted"/> (the native's trailing status refresh,
    /// <c>FedSrv.CPP:13499</c>) has no real equivalent in this port — P104 left
    /// <c>OBJECT_SPOTTED</c>(6) wire-only/untriggered — so it is intentionally
    /// skipped (finding stated in the P113 report), exactly as the task allows.
    /// Runs on the sim thread (the drain sits in <see cref="SimPass"/>).
    /// </summary>
    private void PerformRipcordTeleport(Ship rip)
    {
        var mission = rip.Mission;
        var anchor = rip.RipcordModel;
        if (mission is null || anchor is null)
        {
            rip.SetRipcordModel(null);
            return;
        }

        var a = anchor.Value;
        var targetSector = mission.Sectors.FirstOrDefault(s => s.Id == a.ClusterId);

        if (a.Ot == (short)IgcObjectType.Station)
        {
            // Station anchor: UseRipcord delegates to the station's Launch
            // (FedSrv.CPP:13441-13443) — reuse the undock-launch core, then move
            // to the station's cluster (SetCluster(pcluster), FedSrv.CPP:13474-13476).
            DataStationIGC? station = null;
            if (targetSector is not null)
                foreach (var st in targetSector.Stations)
                    if (st.StationId == a.Oid) { station = st; break; }

            if (station is null || targetSector is null)
            {
                // Anchor vanished between revalidation and drain (a capture/
                // destruction race). Native's UseRipcord returns true regardless,
                // so consume the attempt without relocating.
                rip.SetRipcordModel(null);
                return;
            }

            RepositionForLaunch(rip, station.Value);
            MoveShipToSector(rip, targetSector);
            // P75-style re-dock debounce: the ripper arrives just outside the
            // anchor station's DockRadius; the launch-cooldown latch stops an
            // accidental re-dock on the very next tick, mirroring PerformUndock.
            rip.LastLaunchTime = _simNow;
        }
        else if (a.Ot == (short)IgcObjectType.Ship)
        {
            // Ship anchor: resolve the target ship in its cluster.
            Ship? target = null;
            if (targetSector is not null)
                foreach (var m in targetSector.Models)
                    if (m is Ship s && s.Id == a.Oid) { target = s; break; }

            if (target is null || targetSector is null)
            {
                rip.SetRipcordModel(null);
                return;
            }

            // Energy gate (FedSrv.CPP:13452-13460): the TARGET ship must cover
            // the RIPPER's RipcordCost (pship->GetBaseHullType()->GetRipcordCost()).
            float ripcordCost = rip.HullType.RipcordCost;
            float newEnergy = target.Energy - ripcordCost;
            if (newEnergy < 0.0f)
            {
                // Insufficient energy: broadcast RIPCORD_ABORTED(187) to everyone
                // flying in the RIPPER's cluster (native "broadcast ... to everyone
                // flying in pship's cluster", FedSrv.CPP:13458-13460), do NOT move,
                // and consume the attempt (return true -> state cleared). Never
                // retried when the target later regains energy.
                var ripSec = rip.Sector;
                if (ripSec is not null)
                    SendRipcordSectorWide(ripSec.Id,
                        new RipcordAborted { ShipidRipcord = rip.Id }.Encode());
                rip.SetRipcordModel(null);
                Console.WriteLine($"[ripcord] ship {rip.Id} ripcord to ship-anchor {target.Id} ABORTED — target energy {target.Energy} < RipcordCost {ripcordCost}; attempt consumed (RIPCORD_ABORTED(187) -> ripper sector {ripSec?.Id.ToString() ?? "?"} wide)");
                return;
            }
            target.Energy = newEnergy;

            // Ship-relative reposition (FedSrv.CPP:13461-13474): NOT station-
            // relative — r = target.Radius + ripper.Radius + 25.0f (literal 25,
            // distinct from the station launch's 50 margin), a random direction,
            // ExitStationSpeed velocity, zeroed turn rates, and move to the
            // target's cluster.
            float r = ShipRadius(target) + ShipRadius(rip) + 25.0f;
            Vector3 v = RandomDirection();
            rip.Position = target.Position + v * r;
            rip.Velocity = v * ExitStationSpeed;
            var o = new Orientation();
            o.Set(v);                       // Orientation o(v), FedSrv.CPP:13465
            rip.SetOrientation(o);
            rip.SetTurnRate(ControlData.AxisYaw, 0.0f);
            rip.SetTurnRate(ControlData.AxisPitch, 0.0f);
            rip.SetTurnRate(ControlData.AxisRoll, 0.0f);
            MoveShipToSector(rip, targetSector);
            Console.WriteLine($"[ripcord] ship {rip.Id} ripcord -> ship-anchor {target.Id} at r={r:F1} (cost {ripcordCost} energy paid from target, now {target.Energy}) in sector {targetSector.Id}");
        }
        else
        {
            // Unknown anchor type — impossible per FindRipcordModel's two tiers,
            // but consume the attempt defensively rather than leave state active.
            rip.SetRipcordModel(null);
            return;
        }

        // Successful teleport: clear the ripcord state (the native's
        // SetRipcordModel(NULL) once UseRipcord returns true, shipIGC.cpp:293).
        rip.SetRipcordModel(null);
        UpdatePeerSectorSnapshot(rip, targetSector);
    }

    /// <summary>
    /// P115 — the port of <c>FedSrvSiteBase::RescueShipEvent</c>
    /// (<c>FedSrv.CPP:13033-13074</c>): a <c>c_habmLifepod</c> ship that drifted
    /// within proximity of a friendly <c>c_habmRescue</c>-capable ship is
    /// rescued — <c>PLAYER_RESCUED</c>(16) to both ships' connections, then the
    /// rescued ship is placed at the nearest friendly station. Queued by
    /// <c>Sector.ResolveRescueEvents</c> and drained here on the sim thread
    /// right after <c>Mission.Update</c> (the same accumulate/drain split as the
    /// ripcord teleport, P113). The pair (rescuee, rescuer) is already validated
    /// by the pass — same-side, exactly-one-is-the-lifepod, the other is
    /// rescue-capable, within proximity — so this method only performs the
    /// wire + placement halves of the native event.
    /// </summary>
    /// <remarks>
    /// <para><b>Score / Steam — N/A, skipped.</b> Native awards rescue
    /// achievements before the wire (<c>AddRescue</c>/<c>AwardPodPickup</c>/
    /// <c>AwardGetRescued</c>, <c>FedSrv.CPP:13039-13051</c>). This port has
    /// <b>no score system</b> (<c>PlayerInfo.cs:207</c> throws on non-empty
    /// <c>rgPersistPlayerScores</c> — "score history not supported") and no
    /// Steam integration anywhere, so both are intentionally skipped, the same
    /// precedent every score/Steam-adjacent gap this arc has already hit
    /// (P104's trigger deferral named the same absence).</para>
    /// <para><b>ResetWarpState() — no real equivalent needed here.</b> The
    /// native re-arms the rescued ship's <c>m_warpState = warpReady</c>
    /// (<c>fsship.h:199-201</c>) before docking it. This port's warp-cooldown
    /// stand-in (<c>Ship.LastWarpTime</c> / <c>Sector.WarpCooldownSeconds</c>,
    /// the collapsed <c>warpNoUpdate→warpWaiting→warpReady</c> machine,
    /// <c>Ship.cs:123-135</c>) is consulted only by the two proximity passes —
    /// <c>ResolveWarpCollisions</c> (<c>Sector.cs:1017</c>) and
    /// <c>ResolveDockCollisions</c> (<c>Sector.cs:1255</c>) — as their re-warp /
    /// re-dock debounce. This rescue places the rescuee by <b>direct</b> dock
    /// (below), which never enters those passes, so no warp-readiness gate
    /// stands between the rescue and its dock. Clearing <c>LastWarpTime</c>
    /// would only prematurely drop a legitimate 2 s re-warp debounce with no
    /// benefit to the direct placement. Conclusion: the existing cooldown
    /// already serves the native <c>warpState</c>'s debounce purpose, and no
    /// equivalent call is required here.</para>
    /// <para><b>Station search</b> — reuses P105's <see cref="FindFriendlyStation"/>
    /// (<c>Server.cs:4177</c>), the port's established stand-in for the native
    /// <c>FindTarget(..., c_ttStation | c_ttFriendly | c_ttAnyCluster |
    /// c_ttNearest, ..., c_sabmRestart)</c> nearest-friendly-station search
    /// (<c>FedSrv.CPP:13065-13066</c>). It already returns a same-side station
    /// (any friendly match, first-found — the port's documented simplification
    /// of "nearest"), so the native's same-side fallback
    /// (<c>pstation->GetSide() != pshipRescued's side → GetBase()</c>,
    /// <c>FedSrv.CPP:13067-13071</c>) is inherently satisfied — a same-side
    /// station is never discarded. When no friendly station exists anywhere,
    /// the native returns <c>false</c> with no dock (<c>FedSrv.CPP:13075</c>);
    /// mirrored (no placement, wire already sent).</para>
    /// <para><b>Placement</b> — reuses <c>PerformTeleport</c>'s relocation core
    /// (<c>Server.cs:3284-3299</c>, P110): move the rescuee to the station's
    /// cluster, latch <c>Ship.StationId</c> (and any boarded children), and
    /// point the owning peer's <c>LiveShip.Sector</c> snapshot at the station's
    /// cluster so the next SHIP_STATUS/SINGLE_SHIP_UPDATE read the right
    /// sector. Like the P110 teleport path, no <c>DockWithStationEvent</c>
    /// seam / <c>FM_S_DOCKED</c>(61) is raised here (the direct-dock latch is
    /// enough; the docked state is conveyed by the periodic SHIP_STATUS).</para>
    /// </remarks>
    private void PerformRescue(Ship rescuee, Ship rescuer)
    {
        var mission = rescuee.Mission;
        if (mission is null)
        {
            Console.WriteLine($"[rescue] ship {rescuee.Id} (rescued by {rescuer.Id}) has no mission — rescue dropped");
            return;
        }

        // 1) Wire: PLAYER_RESCUED(16) to BOTH the rescuer's and the rescuee's
        // connections (native pfmPlayerRescued sent to both, FedSrv.CPP:13056-13065).
        // Peer-resolution doubles as the native's "if IsPlayer" gate: only a
        // peer-owned ship has a connection to send to; a non-peer ship's send is
        // skipped (it may still be placed below).
        var rescueMsg = new PlayerRescued { ShipIdRescuer = rescuer.Id, ShipIdRescuee = rescuee.Id };
        byte[] packet = FedPacket.Pack(new[] { rescueMsg.Encode() });
        foreach (var kv in _peerShips)
            if (ReferenceEquals(kv.Value.Ship, rescuee) || ReferenceEquals(kv.Value.Ship, rescuer))
                SendPeer(kv.Key, packet);

        // 2) Nearest friendly station, else no placement (FedSrv.CPP:13065-13075).
        var station = FindFriendlyStation(rescuee);
        if (station is null)
        {
            Console.WriteLine($"[rescue] ship {rescuee.Id} rescued by {rescuer.Id}: no friendly station anywhere — PLAYER_RESCUED(16) sent to {packet.Length}-byte packet but no placement (native returns false, FedSrv.CPP:13075)");
            return;
        }

        // 3) Place the rescuee at the station — PerformTeleport's relocation
        // core (Server.cs:3284-3299): move to the station's cluster and latch
        // docked. FindFriendlyStation already guarantees same-side, so the
        // native's same-side fallback is satisfied.
        var destSector = mission.Sectors.First(s => s.Stations.Contains(station.Value));
        rescuee.Sector = destSector;
        foreach (var kid in rescuee.ChildShips)   // empty for a solo lifepod; mirrored for parity
            kid.StationId = station.Value.StationId;
        rescuee.StationId = station.Value.StationId;

        // Keep the owning peer's broadcast bookkeeping coherent — SHIP_STATUS
        // frames derive Sector from the LiveShip snapshot, so point it at the
        // station's cluster like PerformTeleport (Server.cs:3295) and the P105
        // respawn (Server.cs:4152) do.
        foreach (var kv in _peerShips)
            if (ReferenceEquals(kv.Value.Ship, rescuee))
                _peerShips[kv.Key] = kv.Value with { Sector = destSector };

        var note = $"[rescue] ship {rescuee.Id} (lifepod) rescued by ship {rescuer.Id} -> PLAYER_RESCUED(16) rescuer={rescuer.Id}/rescuee={rescuee.Id}; placed docked at station {station.Value.StationId} '{station.Value.Name}' in sector '{destSector.Name}'";
        Console.WriteLine(note);
        Notable?.Invoke(note);
    }

    /// <summary>
    /// P123 — the cash-treasure pickup, drained from
    /// <c>Sector.ResolveTreasurePickups</c> (via <c>DrainTreasurePickups</c>).
    /// The port of the <c>c_tcCash</c> branch of <c>HitTreasureEvent</c>
    /// followed by <c>KillTreasureEvent</c> (<c>FedSrv.CPP:13589-13616</c>,
    /// <c>12493-12509</c>).
    /// </summary>
    /// <remarks>
    /// <para><b>Pooled-money simplification — confirmed and applied.</b> The
    /// native splits the cash credit by whether the picking ship is a player:
    /// a player credits its autodonate target's per-player wallet and
    /// broadcasts <c>MONEY_CHANGE</c> to the whole side's peer group
    /// (<c>FedSrv.CPP:13591-13611</c>, <c>pfsDonate-&gt;SetMoney(...)</c> with
    /// <c>sidTo = pshipDonate</c>, <c>sidFrom = NA</c>); a non-player calls
    /// <c>m_pfsMission-&gt;GiveSideMoney(ship-&gt;GetSide(), Money(amount))</c>
    /// (<c>FedSrv.CPP:13612-13615</c>). This port has no per-player wallets and
    /// no autodonate mechanism — it tracks ONE pooled number per side
    /// (<see cref="Side.Money"/>, Model/Side.cs:52, P118) — so the two native
    /// branches <b>collapse to the same side-pooled credit</b>: credit the
    /// picking ship's side by <c>amount</c>, then broadcast
    /// <c>MONEY_CHANGE</c> side-wide via P118's
    /// <see cref="BroadcastMoneyChangeSideWide"/> with <c>sidTo = the picking
    /// ship</c> (the autodonate target degenerates to the ship itself since
    /// there is no autodonate), <c>sidFrom = NA</c> (server-initiated pooled
    /// credit). Both the \"player\" and \"non-player\" native branches thus do
    /// the same thing here.</para>
    /// <para><b>Destroy half.</b> Native then (because <c>HitTreasureEvent</c>
    /// returns true) runs <c>KillTreasureEvent</c>
    /// (<c>FedSrv.CPP:12493-12509</c>): broadcast <c>DESTROY_TREASURE</c>
    /// (<c>treasureID</c> = the treasure's object id, <c>sectorID</c> = its
    /// cluster id) <b>whole-mission</b> to <c>GetGroupRealSides()</c> — every
    /// peer on every side, <em>not</em> just the picking side — and terminate
    /// the treasure (removed from its sector). The flag respawn leg is
    /// explicitly out of scope (Phase 6 capture-the-flag).</para>
    /// <para><b>Not wired this slice:</b> the per-player <c>ACQUIRE_TREASURE</c>
    /// notice (<c>FedSrv.CPP:13633-13641</c>, to the picking player's own
    /// connection) — its codec is built (<see cref="Allegiance.Core.Wire.AcquireTreasure"/>)
    /// but the broadcast set is kept to the task's enumerated
    /// <c>MONEY_CHANGE</c> + <c>DESTROY_TREASURE</c>; wiring it is a one-line
    /// follow-up (see the P123 report).</para>
    /// </remarks>
    private void PerformTreasurePickup(Ship ship, Treasure treasure)
    {
        var mission = ship.Mission;
        if (mission is null)
        {
            Console.WriteLine($"[treasure] ship {ship.Id} picked treasure {treasure.Id} but has no mission — pickup dropped");
            return;
        }

        // P201 — flag-treasure pickup, the port of the c_tcFlag case at the top
        // of HitTreasureEvent (FedSrv.CPP:13562-13575). The treasure's
        // TreasureId IS the ORIGINAL side whose flag this is (native
        // `oid = treasure->GetTreasureID()` then `m_sidFlag = oid`,
        // shipIGC.h:1349). Eligibility (not already carrying, not own-side) is
        // enforced by the sector pickup pass (Sector.ResolveTreasurePickups)
        // — a refused pickup is never queued, so it never reaches here (native
        // returns false → no destroy). Set the carrier's Flag, announce
        // whole-mission (native SendMessages(GetGroupRealSides()),
        // FedSrv.CPP:13573), then destroy the treasure (native returns true →
        // KillTreasureEvent → DESTROY_TREASURE, the same destroy cash uses
        // below). `treasure->SetTreasureID(NA)` (FedSrv.CPP:13576, to stop the
        // respawn) collapses to removing the treasure, which
        // BroadcastTreasureDestroyed does.
        if (treasure.Code == TreasureCode.Flag)
        {
            short oid = treasure.Data.TreasureId;
            ship.Flag = oid;
            BroadcastGainFlag(mission, oid, ship.Id, bIsTreasureDocked: false, parttypeidDocked: 0);
            BroadcastTreasureDestroyed(treasure);
            Console.WriteLine($"[flag] ship {ship.Id} picked up side {oid}'s flag -> GAIN_FLAG(156) whole-mission + DESTROY_TREASURE");
            return;
        }

        // P145 — part-treasure pickup (the FM_C_TREASURE_ACK offer leg). Port of
        // the c_tcPart branch of HitTreasureEvent (FedSrv.CPP:13604-13641):
        //   • Buffer the offer on the ship ONLY if none is already pending
        //     ("As long as either it isn't a part or there is space in the
        //     treasure buffer", FedSrv.CPP:13604-13607); a second touch while one
        //     is pending is silently NOT offered.
        //   • When offered, send ACQUIRE_TREASURE to the picking player's OWN
        //     connection only (SendMessages(pfsPlayer->GetConnection(), ...),
        //     FedSrv.CPP:13639-13641) — the first real send of the P123 codec.
        //   • REGARDLESS of the buffer state, destroy the treasure (native
        //     KillTreasureEvent always fires on any successful HitTreasureEvent
        //     return — Step 1's critical finding; the touched treasure is gone
        //     before the player ever chooses a mount, part or cash alike).
        // The part-type id buffered is Treasure.Data.TreasureId (for a part
        // treasure that is the part's PartId, CreateDropTreasure FedSrv.CPP:4369).
        if (treasure.Code == TreasureCode.Part)
        {
            if (ship.PendingTreasureOffer is null)
            {
                ship.PendingTreasureOffer = ((short)treasure.Data.TreasureId, (short)treasure.Amount);
                var acquire = new AcquireTreasure
                {
                    TreasureCode = (byte)TreasureCode.Part,
                    TreasureId = (short)treasure.Data.TreasureId,
                    Amount = (short)treasure.Amount,
                };
                var owner = _peerShips.FirstOrDefault(kv => ReferenceEquals(kv.Value.Ship, ship));
                if (owner.Key != default)
                {
                    SendPeer(owner.Key, FedPacket.Pack(new[] { acquire.Encode() }));
                    Console.WriteLine($"[treasure] ship {ship.Id} offered part {treasure.Data.TreasureId} amount={treasure.Amount} -> ACQUIRE_TREASURE(162) to its own connection");
                }
                else
                {
                    Console.WriteLine($"[treasure] ship {ship.Id} picked part treasure {treasure.Id} but has no connection — offer buffered, ACQUIRE_TREASURE not sent");
                }
            }
            else
            {
                Console.WriteLine($"[treasure] ship {ship.Id} already has a pending offer ({ship.PendingTreasureOffer.Value.PartTypeId}x{ship.PendingTreasureOffer.Value.Amount}) — second part-touch {treasure.Id} NOT offered (native gate, FedSrv.CPP:13604) but still destroyed");
            }
            BroadcastTreasureDestroyed(treasure);   // KillTreasureEvent always fires (FedSrv.CPP:13627-13628 → 12493-12509)
            return;
        }

        if (ship.SideId is not { } sideId)
        {
            Console.WriteLine($"[treasure] ship {ship.Id} picked cash treasure {treasure.Id} but has no side — pickup dropped");
            return;
        }
        var side = mission.Sides.FirstOrDefault(s => s.Id == sideId);
        if (side is null)
        {
            Console.WriteLine($"[treasure] side {sideId} not found in mission — pickup dropped");
            return;
        }

        int amount = treasure.Amount;
        side.Money += amount;                                     // pooled credit (both native branches collapse here)
        Console.WriteLine($"[treasure] ship {ship.Id} picked cash treasure {treasure.Id} amount={amount} -> side {sideId} money {side.Money}");

        BroadcastMoneyChangeSideWide(sideId, amount, ship.Id);     // sidTo = picking ship, sidFrom = NA (P118 helper)
        BroadcastTreasureDestroyed(treasure);                       // DESTROY_TREASURE whole-mission + remove from sector
    }

    /// <summary>P123 — remove a treasure from its sector and broadcast
    /// <c>DESTROY_TREASURE(30)</c> whole-mission (every peer, every side) — the
    /// port of <c>KillTreasureEvent</c>'s wire half (<c>FedSrv.CPP:12498-12503</c>,
    /// <c>GetGroupRealSides()</c>). The treasure's <c>objectID</c> and its
    /// sector id go on the wire; the (out-of-scope) flag respawn is omitted.
    /// Called from the cash-pickup drain; would equally serve an expiry path
    /// once the spawn/lifecycle system exists.</summary>
    private void BroadcastTreasureDestroyed(Treasure treasure)
    {
        var sector = treasure.OwnerSector;
        sector.Remove(treasure);
        var msg = new DestroyTreasure { TreasureId = treasure.Id, SectorId = sector.Id };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        int n = 0;
        foreach (var kv in _peerShips)   // whole-mission: every peer, regardless of side
        {
            SendPeer(kv.Key, packet);
            n++;
        }
        Console.WriteLine($"[treasure] destroyed {treasure.Id} in sector {sector.Id} -> DESTROY_TREASURE(30) whole-mission to {n} peer(s)");
    }

    /// <summary>P201 — broadcast <c>GAIN_FLAG(156)</c> <b>whole-mission</b> (the
    /// native <c>SendMessages(GetGroupRealSides(), ...)</c> audience,
    /// FedSrv.CPP:13573 for the pickup and :13157 for the secure — both
    /// confirmed whole-mission, not side-wide) for one flag-capture event:
    /// <c>sideidFlag</c> = the carried flag's ORIGINAL side on a pickup, or
    /// <see cref="GainFlag.NA"/> on a secure; <c>shipidRecipient</c> = the
    /// carrier's ship id. Scoped to the mission's peers (matching native
    /// GetGroupRealSides), so a world with multiple missions announces only to
    /// the peers on the acting mission. <c>bIsTreasureDocked</c> is always
    /// <c>false</c> in this task (the tech-recovery variant is out of scope).</summary>
    private void BroadcastGainFlag(Mission mission, short sideidFlag, short shipidRecipient, bool bIsTreasureDocked, short parttypeidDocked)
    {
        var msg = new GainFlag
        {
            SideidFlag = sideidFlag,
            ShipidRecipient = shipidRecipient,
            BIsTreasureDocked = bIsTreasureDocked,
            ParttypeidDocked = parttypeidDocked,
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        int n = 0;
        foreach (var kv in _peerShips)   // whole-mission (GetGroupRealSides): every peer on this mission, regardless of side
        {
            if (!ReferenceEquals(kv.Value.Ship.Mission, mission)) continue;
            SendPeer(kv.Key, packet);
            n++;
        }
        Console.WriteLine($"[gainflag] sideidFlag={sideidFlag} shipidRecipient={shipidRecipient} treasureDocked={bIsTreasureDocked} parttypeidDocked={parttypeidDocked} -> GAIN_FLAG(156) whole-mission to {n} peer(s)");
    }

    /// <summary>P164 — broadcast <c>BUILDINGEFFECT_DESTROYED(181)</c>
    /// whole-mission (every peer, every side) for an in-progress building
    /// effect whose Builder died mid-build — the port of
    /// <c>KillBuildingEffectEvent</c>'s wire half (<c>FedSrv.CPP:12484-12492</c>,
    /// <c>GetGroupRealSides()</c>). The interrupted build's target asteroid id
    /// goes on the wire; the message is structurally identical to
    /// <see cref="BroadcastTreasureDestroyed"/> (single destroyed-object id,
    /// whole-mission <c>_peerShips</c> sweep). Called from the
    /// <see cref="DrainBuildingEffectInterruptions"/> drain in the same tick
    /// slot as the treasure drain. The native sends this only on the
    /// <b>interruption</b> path — a build reaching its completion deadline
    /// transforms in place (P163 <c>CompleteBuild</c>) and sends nothing.</summary>
    private void BroadcastBuildingEffectDestroyed(short asteroidId)
    {
        var msg = new BuildingEffectDestroyed { AsteroidId = asteroidId };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        int n = 0;
        foreach (var kv in _peerShips)   // whole-mission: every peer, regardless of side
        {
            SendPeer(kv.Key, packet);
            n++;
        }
        Console.WriteLine($"[build] interrupted building effect on asteroid {asteroidId} -> BUILDINGEFFECT_DESTROYED(181) whole-mission to {n} peer(s)");
    }

    /// <summary>
    /// P117 — the port of <c>FedSrvSiteBase::LandOnCarrierEvent</c>
    /// (<c>FedSrv.CPP:12808-13031</c>), the real effect of a ship landing on a
    /// carrier: restore the lander's ship-level consumable pools, reposition it
    /// at a real launch slot (or the no-slots fallback), and broadcast
    /// <c>RELAUNCH_SHIP(192)</c> sector-wide. Drained from
    /// <c>Sector.ResolveCarrierLandings</c> (via <c>DrainCarrierLandings</c>) in
    /// the same <see cref="SimPass"/> slot the P115 rescue drain uses — the
    /// sector pass detects the geometry, the host performs the effect.
    /// </summary>
    /// <remarks>
    /// <para><b>Resupply, at this port's own granularity.</b> The native resets
    /// per-part amounts and walks the tech-tree successor of each part
    /// (<c>RefillPart</c>, <c>FedSrv.CPP:12589-12618</c>) and then also resets
    /// the ship-level scalars (<c>FedSrv.CPP:12855-12861</c>:
    /// <c>SetAmmo(SHRT_MAX); SetFuel(FLT_MAX);
    /// SetEnergy(GetHullType()-&gt;GetMaxEnergy()); SetFraction(1.0f);</c> plus a
    /// mounted shield's fraction). This port tracks ammo/energy as ship-level
    /// scalar pools seeded from <c>HullType.MaxEnergy</c>/<c>MaxAmmo</c>
    /// (<c>Sim/Ship.cs:62-63</c>) — there is no per-part inventory and no
    /// <c>SuccessorPartType</c>/tech-tree concept anywhere in this port
    /// (confirmed: zero references in <c>Data/DataPartTypeIGC.cs</c> and
    /// <c>Sim/MountedPart.cs</c>) — so the honest port of the whole resupply is
    /// to restore the two scalar pools to their hull maxima. That is exactly
    /// the native's ship-level reset above, at this port's granularity: <c>Ammo
    /// = SHRT_MAX</c> collapses to the finite hull <c>MaxAmmo</c> and
    /// <c>Energy = MaxEnergy</c> is verbatim. The native's <c>SetFraction(1.0f)</c>
    /// and shield-fraction reset are <b>not</b> performed here — the P117 scope
    /// is the consumable resupply (energy+ammo), and hull/shield repair is a
    /// separate, documented, not-yet-portable effect (see the P117 report).</para>
    /// <para><b>Fuel is a confirmed, separate, pre-existing gap.</b> Native
    /// <c>SetFuel(FLT_MAX)</c> (FedSrv.CPP:12856) has no target here: <c>grep
    /// "Fuel"</c> finds zero consumers in <c>Sim/Ship.cs</c>, <c>Model/Sector.cs</c>
    /// and <c>Server.cs</c> (only <c>DataHullTypeIGC.MaxFuel</c> is decoded,
    /// <c>Data/DataHullTypeIGC.cs:32</c>). Fuel tracking is deliberately not
    /// invented in this task.</para>
    /// <para><b>Desired-loadout / successor-upgrade</b>
    /// (<c>CFSPlayer::GetDesiredLoadout()</c>, the <c>ErasePart</c>/
    /// <c>CreateAndAddPart</c> fill-empty-slots logic and the
    /// <c>GetSuccessorPartType()</c> tech-tree walks, FedSrv.CPP:12831-12854)
    /// are the explicitly out-of-scope halves that need infrastructure this
    /// port doesn't have (per-part inventories, tech-tree bitmasks). Skipped,
    /// stated plainly.</para>
    /// <para><b>RecalculateCollisions</b> (<c>FedSrv.CPP:13006</c>) is N/A: this
    /// port has no rigid-body collision-resolution system (established P115/P116),
    /// so there is no post-move collision pass to call.</para>
    /// <para><b>Autopilot command re-tasking</b> (<c>FedSrv.CPP:13024-13030</c>,
    /// the <c>SetCommand</c>/<c>c_cidDefend</c>/<c>c_cidGoto</c> re-assign when
    /// the lander was GOTO-bound for the carrier) is skipped: this port models no
    /// AI/pilot-type/autopilot command concept — the only command-ish state is a
    /// single weapon-aim <c>Ship.CommandTarget</c> with no <c>c_cid*</c> IDs
    /// (confirmed, <c>Sim/Ship.cs:444-456</c>). Same precedent as the
    /// ShipStatusSpotted (P113) and other confirmed-absent native concepts.</para>
    /// <para><b>Round-robin launch slots are ported, not elided.</b> The native
    /// picks <c>slot = pshipCarrier-&gt;GetLaunchSlot()</c> — a per-carrier modulo
    /// counter (<c>shipIGC.h:2168-2172</c>) — so consecutive landings spread
    /// across the bay's <c>launchN</c> frames. <see cref="Ship.GetLaunchSlot"/>
    /// ports that counter faithfully (<see cref="Ship.NextLaunchSlot"/>), driven
    /// off the carrier's real reconstructed bay. The two native branches
    /// (<c>FedSrv.CPP:12970-13000</c>) are reproduced: with launch slots,
    /// position/velocity/orientation derive from the slot's local frame
    /// transformed by the carrier's orientation; with zero slots, the ship is
    /// launched straight back along the carrier's backward axis.</para>
    /// <para><b>Cookie.</b> Native sets <c>cookie = NewCookie()</c> for a player
    /// (<c>FedSrv.CPP:13018</c>); this port's <c>Cookie</c> is a single byte sent
    /// as <c>0</c> (no per-player cookie machinery), the same established
    /// simplification <c>Eject.ForSurvivor</c> and <c>Server.cs:3289,4616</c>
    /// apply — so <c>RelaunchShip.Cookie = 0</c>.</para>
    /// </remarks>
    private void PerformRelaunch(Ship lander, Ship carrier)
    {
        var mission = lander.Mission;
        if (mission is null)
        {
            Console.WriteLine($"[relaunch] ship {lander.Id} landed on carrier {carrier.Id} has no mission — relaunch dropped");
            return;
        }

        // 1) Resupply — restore the lander's ship-level consumable pools to
        //    their hull maxima (see remarks: the port's granularity-equivalent
        //    of the native's RefillPart loop + ship-level scalar reset).
        lander.Energy = lander.HullType.MaxEnergy;
        lander.Ammo = lander.HullType.MaxAmmo;

        // 2) The carrier's real launch slots, resolved through the sector's
        //    landing-bay cache (the same production cache ResolveCarrierLandings
        //    used to detect the landing), falling back to a direct .cvh
        //    reconstruction when no cache is attached. A detected landing
        //    guarantees a resolvable bay with land slots; its launch-slot count
        //    is what selects the native's branch.
        var bay = lander.Sector?.LandingBays?.Bay(carrier.HullType)
                  ?? (carrier.Hull is not null
                        ? HullLandingBay.Reconstruct(carrier.Hull, carrier.HullType.Length)
                        : null);
        int launchSlots = bay?.LaunchSlots.Count ?? 0;

        var orientation = carrier.Orientation;                  // FedSrv.CPP:12963
        float vLaunch = ExitStationSpeed;                       // c_fcidExitStationSpeed = 50, FedSrv.CPP:12959, missionigc.cpp:2689,2744

        var position = carrier.Position + Jitter(0.5f);         // FedSrv.CPP:12964-12968 (small ±0.5 jitter)
        var velocity = carrier.Velocity;                        // FedSrv.CPP:12969

        Orientation newOrientation;
        int selectedSlot = -1;                                  // launch-slot index used (fallback branch = -1)
        if (launchSlots == 0)
        {
            // Fallback: launch straight back along the carrier's backward axis
            // (FedSrv.CPP:12971-12976). Keep the carrier's own orientation.
            var backward = orientation.GetBackward();
            position -= backward * (ShipRadius(carrier) + ShipRadius(lander) + vLaunch * 0.5f);
            velocity -= backward * vLaunch;
            newOrientation = orientation;
        }
        else
        {
            // Real launch slot: transform the slot's local frame by the
            // carrier's orientation (FedSrv.CPP:12978-12999), round-robin across
            // slots, and turn the lander to face the launch direction.
            selectedSlot = carrier.GetLaunchSlot(launchSlots);
            var slotPos = bay!.LaunchSlots[selectedSlot];
            position += slotPos.Position * orientation;               // GetLaunchPosition(slot) * orientation
            var forward = slotPos.Direction * orientation;            // GetLaunchDirection(slot) * orientation
            position += forward * (ShipRadius(lander) + vLaunch * 0.5f);
            velocity += forward * vLaunch;
            newOrientation = new Orientation(orientation);            // orientationBfr = orientation;
            newOrientation.Set(forward);                              // orientationBfr.TurnTo(forward) — align the nose to the launch direction
        }

        lander.Position = position;
        lander.Velocity = velocity;
        lander.SetOrientation(newOrientation);

        // 3) Broadcast RELAUNCH_SHIP(192) to everyone flying in the lander's
        //    cluster (GetGroupSectorFlying, FedSrv.CPP:13019). The loadout blob
        //    is the lander's own ShipLoadout (hullID + mounted parts), reusing
        //    BuyLoadoutRequest's blob shape.
        var relaunch = new RelaunchShip
        {
            ShipId = lander.Id,
            CarrierId = carrier.Id,
            Position = position,
            Velocity = velocity,
            Orientation = newOrientation,
            Cookie = 0,
            HullId = lander.HullType.HullId,
            Parts = lander.Parts.Select(p => p.ToExpandedPartData()).ToList(),
        };
        byte[] encoded = relaunch.Encode();
        SendSectorWide(lander.Sector!.Id, encoded);

        var note = $"[relaunch] ship {lander.Id} relaunched off carrier {carrier.Id} (bay '{carrier.HullType.ModelName}', {launchSlots} launch slots{(selectedSlot >= 0 ? $", slot {selectedSlot}" : "")}) -> RELAUNCH_SHIP(192) sector-wide; energy={lander.Energy:F0}/{lander.HullType.MaxEnergy:F0} ammo={lander.Ammo}/{lander.HullType.MaxAmmo}; pos=({position.X:F1},{position.Y:F1},{position.Z:F1}) vel=({velocity.X:F1},{velocity.Y:F1},{velocity.Z:F1})";
        Console.WriteLine(note);
        Notable?.Invoke(note);
    }

    /// <summary>A per-axis uniform-jitter helper for the relaunch's small
    /// <c>random(-0.5f, 0.5f)</c> positional wobble (FedSrv.CPP:12965-12967).
    /// Independent draw per axis, shared with the port's existing
    /// <see cref="_rand"/>-backed helpers.</summary>
    private static Vector3 Jitter(float amplitude) =>
        new((float)(_rand.NextDouble() * 2.0 - 1.0) * amplitude,
            (float)(_rand.NextDouble() * 2.0 - 1.0) * amplitude,
            (float)(_rand.NextDouble() * 2.0 - 1.0) * amplitude);

    /// <summary>P113 — the ship's bounding radius, <c>GetRadius()</c>. The port
    /// has no per-model radius accessor (test ships carry no collision hull), so
    /// the established <c>0.5 × HullType.Length</c> sphere approximation is used,
    /// the same convention ServerHost uses for its spawn offsets (ServerHost.cs:566).
    /// Feeds the ship-anchor ripcord margin <c>r = target.Radius + ripper.Radius +
    /// 25.0f</c> (FedSrv.CPP:13462).</summary>
    private static float ShipRadius(Ship ship) => 0.5f * ship.HullType.Length;

    /// <summary>P113 — move a ripper into the anchor's sector
    /// (<c>SetCluster(pcluster)</c>, FedSrv.CPP:13474-13476): out of its old
    /// sector's model list, into the target's, and update its <see cref="Ship.Sector"/>
    /// back-ref so it ticks with the new sector next pass.</summary>
    private static void MoveShipToSector(Ship ship, Sector target)
    {
        if (ship.Sector is { } old && !ReferenceEquals(old, target))
        {
            old.Remove(ship);
            target.Add(ship);
            ship.Sector = target;
        }
        else if (ship.Sector is null)
        {
            target.Add(ship);
            ship.Sector = target;
        }
    }

    /// <summary>P113 — keep the owning peer's broadcast bookkeeping coherent
    /// after a ripcord teleport changes the ripper's cluster: SHIP_STATUS /
    /// SINGLE_SHIP_UPDATE frames derive Sector from the <c>_peerShips</c>
    /// LiveShip snapshot, so point it at the anchor's cluster exactly like
    /// PerformTeleport / the lifepod respawn do (Server.cs:3072, 3929).</summary>
    private void UpdatePeerSectorSnapshot(Ship rip, Sector target)
    {
        foreach (var kv in _peerShips)
            if (ReferenceEquals(kv.Value.Ship, rip))
            {
                _peerShips[kv.Key] = kv.Value with { Sector = target };
                break;
            }
    }

    /// <summary>
    /// P111 — the port of <c>CshipIGC::FindRipcordModel</c>
    /// (<c>shipIGC.cpp:3718-3973</c>): what actually qualifies as a valid
    /// ripcord anchor in a target sector, and the priority order. The full
    /// native search has five tiers (command-target probe/ship,
    /// friendly <c>c_sabmRipcord</c> station via <c>FindTarget</c>, friendly
    /// probe, friendly ship, warp-adjacent-cluster expansion). This port
    /// implements the two realistic, deterministic tiers and documents the rest:
    /// <list type="number">
    /// <item><b>Friendly <c>c_sabmRipcord</c> station</b> in the target sector
    /// (<c>FindTarget</c> station tier, <c>shipIGC.cpp:3835-3838</c>): a
    /// station on the requester's own side whose station type has the
    /// <c>c_sabmRipcord</c> ability (<c>igc.h:747</c>).</item>
    /// <item><b>Friendly ripcord-target ship</b> in the target sector
    /// (<c>shipIGC.cpp:3898-3930</c>): a peer ship on the requester's side
    /// whose hull can host a ripcord (light-ripcord refinement reproduced —
    /// the target needs <c>c_habmIsRipcordTarget</c>, or additionally
    /// <c>c_habmIsLtRipcordTarget</c> when the ripper's own hull has
    /// <c>c_habmCanLtRipcord</c>, <c>shipIGC.cpp:3900-3906</c>).</item>
    /// </list>
    /// Deferred (documented, not silently dropped): the command-target probe/
    /// ship tier (<c>shipIGC.cpp:3802-3833</c>) and the friendly-probe tier
    /// (<c>shipIGC.cpp:3841-3896</c>) because this map/core and port have no
    /// probes deployed and no ripcord-to-target UI; the warp-adjacent-cluster
    /// expansion (<c>shipIGC.cpp:3936-3972</c>) because it relies on per-side
    /// <c>CanSee(w)</c> visibility and ripcord-debt ordering that this port
    /// does not model, and — critically — on XXV_simple's densely-connected
    /// 13-sector web (38 warps) the real side-0 station is reachable from
    /// nearly every sector, so expansion would grant an anchor almost anywhere
    /// and make the <c>RIPCORD_DENIED</c> path unreachable in practice. Scoping
    /// the anchor search to the requested sector keeps the deny path real and
    /// deterministic. Revisit with the countdown/teleport follow-up.
    /// </summary>
    private Ship.RipcordTarget? FindRipcordModel(Ship ship, Sector target)
    {
        short side = ship.SideId!.Value;
        var core = ship.Mission!.Core;

        // Tier 1: friendly c_sabmRipcord station (shipIGC.cpp:3835-3838).
        foreach (var st in target.Stations)
        {
            if (st.SideId == side &&
                core.StationTypes.TryGetValue(st.StationTypeId, out var stype) &&
                stype.HasCapability(DataStationTypeIGC.SabmRipcord))
                return new Ship.RipcordTarget((short)IgcObjectType.Station, st.StationId, st.ClusterId);
        }

        // Tier 2: friendly ripcord-target ship (shipIGC.cpp:3898-3930).
        short habm = ship.HullType.HasCapability(DataHullTypeIGC.HabmCanLtRipcord)
            ? (short)(DataHullTypeIGC.HabmIsRipcordTarget | DataHullTypeIGC.HabmIsLtRipcordTarget)
            : DataHullTypeIGC.HabmIsRipcordTarget;
        foreach (var peer in _peerShips.Values)
        {
            var s = peer.Ship;
            if (!ReferenceEquals(s, ship) && s.Sector is not null &&
                s.Sector.Id == target.Id && s.SideId == side &&
                s.HullType.HasCapability(habm))
                return new Ship.RipcordTarget((short)IgcObjectType.Ship, s.Id, target.Id);
        }

        return null;
    }

    /// <summary>Pick a turret slot for <paramref name="boarder"/> on
    /// <paramref name="carrier"/>, faithfully porting the native walk
    /// (<c>FedSrv.CPP:2347-2402</c>). Returns the chosen slot index, or
    /// <c>null</c> (= NA) when the ship boards as an <b>observer</b> — either
    /// because the carrier has no turret slots at all
    /// (<c>minMount == maxMount</c>), or because no unoccupied, equipped slot
    /// exists after a full loop. Occupancy is read from the carrier's existing
    /// <see cref="Ship.ChildShips"/> turret ids; equipment from the carrier's
    /// mounted <see cref="MountedPart.MountId"/>s (the port already tracks
    /// both, so this is a pure reuse, no new fields).</summary>
    internal static short? ChooseTurretSlot(Ship boarder, Ship carrier)
    {
        int minMount = carrier.HullType.MaxFixedWeapons;
        int maxMount = carrier.HullType.MaxWeapons;
        if (minMount == maxMount)
            return null;                                  // no turret slots at all → observer

        int startMount = boarder.TurretId ?? (maxMount - 1);
        int turret = startMount;
        while (true)
        {
            turret++;
            if (turret >= maxMount)
                turret = minMount;

            // Occupied? any existing child already rides this slot.
            bool occupied = false;
            foreach (var child in carrier.ChildShips)
                if (child.TurretId == (short)turret) { occupied = true; break; }

            // Equipped? some mounted part sits in this slot (Turkey 07/10 #145).
            bool equipped = false;
            foreach (var part in carrier.Parts)
                if (part.MountId == turret) { equipped = true; break; }

            if (!occupied && equipped)
                return (short)turret;                     // free and equipped → board here
            if (turret == startMount)
                return null;                              // full loop, no slot → observer
        }
    }

    /// <summary>The boarder's surrendered hardware value = hull price + Σ
    /// mounted part prices (<c>CshipIGC::GetValue</c>, <c>shipIGC.h:396-406</c>).</summary>
    private static int ShipValue(Ship ship) =>
        ship.HullType.Price + ship.Parts.Sum(p => p.Type.Price);

    /// <summary>
    /// P110 — the port's decision for <c>station.CanBuy(baseHullType)</c>,
    /// i.e. whether a station can sell the player's current hull
    /// (<c>FedSrv.CPP:7076</c>). Native: <c>CanBuy(b) = b->GetRequiredTechs()
    /// &lt;= (m_myStationType.GetLocalTechs() | side.GetTechs())</c>
    /// (<c>stationIGC.h:383-391</c>, bitwise containment of the buyable's
    /// required-tech mask in the union of the station's local techs and the
    /// side's techs), where the station's local techs are its station type's
    /// <c>ttbmLocal</c> (<c>stationtypeigc.h:159-161</c>) and the side's techs
    /// are the runtime-developed mask. <b>Simplification, stated plainly:</b>
    /// this port has no tech-tree/development simulation — sides never accrue
    /// developments — so the side's runtime-developed term is approximated by
    /// its civilization's <b>base</b> tech mask (<c>DataCivilizationIGC.TtbmBaseTechs</c>,
    /// the faction's starting knowledge), the closest static proxy this port
    /// carries; "can buy" therefore reduces to
    /// <c>hull.TtbmRequired ⊆ (stationType.TtbmLocal | civ.TtbmBaseTechs)</c>
    /// (all 50-byte / 400-bit tech masks, <c>DataHullTypeIGC.cs:21</c>,
    /// <c>DataStationTypeIGC.cs:29</c>, <c>DataCivilizationIGC.cs:17</c>).
    /// Measured on PCore001.igc this yields a sane dichotomy: a faction's
    /// garrison sells its starter hulls (Scout/Lt Interceptor/Fighter/Miner
    /// per civ — <c>bNewHull=false</c>) and rejects every advanced hull
    /// (<c>bNewHull=true</c>), which is exactly the pair the task's
    /// verification scenarios exercise.
    /// </summary>
    private static bool StationSellsHull(DataStationTypeIGC st, DataCivilizationIGC civ, DataHullTypeIGC hull)
    {
        var need = hull.TtbmRequired;
        var local = st.TtbmLocal;
        var baseTechs = civ.TtbmBaseTechs;
        for (int i = 0; i < need.Length && i < local.Length; i++)
            if ((need[i] & ~(local[i] | (i < baseTechs.Length ? baseTechs[i] : (byte)0))) != 0) return false;
        for (int i = local.Length; i < need.Length; i++)
            if (need[i] != 0 && (i >= baseTechs.Length || (need[i] & ~baseTechs[i]) != 0)) return false;
        return true;
    }

    /// <summary>Resolve a same-side ship by id — the port of native
    /// <c>GetSide()->GetShip(id)</c> (<c>FedSrv.CPP:6404</c>, and the
    /// <c>ORDER_CHANGE</c> target resolution <c>pside-&gt;GetShip(pfmOC-&gt;shipID)</c>
    /// at <c>FedSrv.CPP:7275-7278</c>), which looks the target up on the
    /// commanding/boarding ship's <em>own side</em> (hence the same-side
    /// requirement). Returns <c>null</c> for an unknown id or a mismatched
    /// side.
    /// <para><b>P148 extension.</b> Native <c>GetSide()->GetShip()</c> is
    /// <b>side-scoped, not sector-scoped</b>, and searches every ship on the
    /// side — including NPC/drones. This port's original lookup only searched
    /// <see cref="_peerShips"/> (the peer-connection registry), so it could
    /// <b>not</b> find an NPC ship (which, by P147's design, has no peer
    /// entry). It is therefore extended to also enumerate the mission's full
    /// ship set — every sector's model list, filtered to
    /// <see cref="Ship"/> instances — with the same id + side match. A placed
    /// NPC lives in a sector's model list (<c>Mission.PlaceShip</c> →
    /// <c>sector.Add</c>, <c>Mission.cs:347-358</c>), so this finds it while
    /// remaining side-scoped and id-keyed, faithful to native. The
    /// <c>_peerShips</c> scan is retained (player ships are always there and
    /// it is cheap); the mission scan is the net-new coverage.</para>
    /// </summary>
    private Ship? FindSameSideShip(short id, short? sideId, Mission? mission)
    {
        foreach (var kv in _peerShips)
            if (kv.Value.Ship.Id == id && kv.Value.Ship.SideId == sideId)
                return kv.Value.Ship;

        // P148: also search the mission's full ship set (catches NPC/drones
        // placed via Mission.PlaceShip, which have no _peerShips entry).
        if (mission is not null)
            foreach (var sec in mission.Sectors)
                foreach (var mdl in sec.Models)
                    if (mdl is Ship s && s.Id == id && s.SideId == sideId)
                        return s;

        return null;
    }

    /// <summary>
    /// P153 — cross-side ship-by-id lookup for the generic <c>ORDER_CHANGE</c>
    /// fallback (<c>FedSrv.CPP:7335-7339</c>): the <b>targeted</b> object
    /// (<c>pmodel</c>, decoded from <c>pfmOC-&gt;objectType</c>/<c>objectID</c>)
    /// that the commanding player wishes to lock — an enemy or ally ship, NPC
    /// or real peer. Unlike <see cref="FindSameSideShip"/> this imposes <b>no
    /// side filter</b>: target-locking an <em>enemy</em> ship is the whole
    /// point of the <c>c_cmdCurrent</c> SetTarget action
    /// (<c>WinTrek.cpp:8820-8852</c>), and native resolves it generically via
    /// <c>pMission-&gt;GetModel(objectType, objectID)</c>
    /// (<c>FedSrv.CPP:7336</c>), which spans the whole mission with no side
    /// restriction. The two-source scan mirrors
    /// <see cref="FindSameSideShip"/>'s own P148 extension (peer ships first,
    /// then every sector's model list for NPCs placed via
    /// <c>Mission.PlaceShip</c>, which have no <c>_peerShips</c> entry) —
    /// minus the side equality. Returns <c>null</c> for an unknown id, which
    /// the caller treats as native <c>GetModel</c> returning <c>NULL</c> (a
    /// clear-target / no-op, <c>FedSrv.CPP:7336-7338</c>).
    /// </summary>
    private Ship? FindShipAnySide(short id, Mission? mission)
    {
        foreach (var kv in _peerShips)
            if (kv.Value.Ship.Id == id)
                return kv.Value.Ship;

        // NPC/drone ships placed via Mission.PlaceShip live in a sector's
        // model list with no _peerShips entry (P147) — enumerate them the same
        // way FindSameSideShip does, without the side filter.
        if (mission is not null)
            foreach (var sec in mission.Sectors)
                foreach (var mdl in sec.Models)
                    if (mdl is Ship s && s.Id == id)
                        return s;

        return null;
    }

    /// <summary>Send <c>FM_S_BOARD_NACK(178)</c> to one peer (the requester)
    /// naming the <paramref name="requestedParent"/> id — <c>SendMessages(
    /// pfsPlayer-&gt;GetConnection(), ...)</c>, <c>FedSrv.CPP:6412-6416</c>.</summary>
    private void SendBoardNack(ConnectionId from, short requestedParent)
    {
        var nack = new BoardNack { SidRequestedParent = requestedParent };
        SendPeer(from, FedPacket.Pack(new[] { nack.Encode() }));
    }

    /// <summary>The native <c>NA</c> sentinel for a ShipID (igc.h:480):
    /// board/leave sentinel in <c>FM_C_BOARD_SHIP.sidParent</c>.</summary>
    private const short Na = -1;

    /// <summary>
    /// This port's <c>GetModel(ot, oid)</c> — resolve an object by
    /// <c>(ObjectType, ObjectID)</c> across the objects the port can name: the
    /// peer ships (the only first-class models, all present in <see cref=\"_peerShips\"/>)
    /// and sector-attached stations. On success outputs the object's real
    /// position, the sector that owns it, and its side (nullable for a neutral
    /// object). Returns false for an unnamed type or an unknown id — the caller
    /// then emits a bare-cluster reply. This matches the practical reality of a
    /// spectator target (another player's ship, or a station).
    /// </summary>
    private bool TryResolveViewTarget(IgcObjectType ot, short oid, out Vector3 position, out Sector? sector, out short? side)
    {
        switch (ot)
        {
            case IgcObjectType.Ship:
                foreach (var kv in _peerShips)
                {
                    if (kv.Value.Ship.Id == oid)
                    {
                        position = kv.Value.Ship.Position;
                        sector = kv.Value.Ship.Sector;
                        side = kv.Value.Ship.SideId;
                        return true;
                    }
                }
                break;

            case IgcObjectType.Station:
                foreach (var s in _missions[0].Sectors)
                {
                    foreach (var st in s.Stations)
                    {
                        if (st.StationId == oid)
                        {
                            position = new Vector3(st.Position[0], st.Position[1], st.Position[2]);
                            sector = s;
                            side = st.SideId;
                            return true;
                        }
                    }
                }
                break;
        }

        position = Vector3.Zero;
        sector = null;
        side = null;
        return false;
    }

    /// <summary>The port's stand-in for native <c>ImodelIGC::SeenBySide(side)</c> —
    /// the same-side predicate the P58 teammate broadcast already uses
    /// (<c>Server.cs:521</c>). An object with no side (a neutral world object)
    /// is always visible; a side-carrying object is visible only to its own
    /// side. The port has no fog-of-war/scanner, so this is the faithful
    /// existing gate, not an invented one.</summary>
    private static bool SeenByRequesterSide(short? objectSide, short? requesterSide) =>
        objectSide is null || objectSide == requesterSide;

    /// <summary>
    /// Apply a received <see cref="BuyLoadoutRequest"/> (FM_C_BUY_LOADOUT, 4)
    /// to the peer's ship and reply with the real FM_S_BUY_LOADOUT_ACK (5),
    /// tail carrying the ship's actual mounted-part count. Runs on the sim
    /// thread (ProcessDatagram). This is the P61 generalization of the P26/P27
    /// hull-only adopt-a-hull step: resolve the requested hull against the
    /// core, replace the ship's loadout with the requested parts, and echo the
    /// mounted parts back byte-for-byte.
    /// </summary>
    /// <summary>
    /// P69/P113 — the shared station-launch reposition: push a ship radially
    /// outward from a station to just clear <see cref="Sector.DockRadius"/> by
    /// <see cref="LaunchClearMargin"/> and kick it outward at
    /// <see cref="ExitStationSpeed"/>. Position = stationPos + dir·(DockRadius +
    /// LaunchClearMargin), velocity = dir·ExitStationSpeed, where <c>dir</c> is
    /// the station→ship direction (a fixed <c>(1,0,0)</c> fallback when the ship
    /// sits exactly at the station's position). This is the port's undock-launch
    /// mechanic (born P69, refined P74/P75) — <b>and</b> the exact launch shape
    /// <c>FedSrvSiteBase::UseRipcord</c>'s station-anchor branch produces by
    /// delegating to the station's own <c>Launch</c> (<c>FedSrv.CPP:13441-13443</c>:
    /// native <c>UseRipcord</c> calls <c>((IstationIGC*)pmodel)-&gt;Launch(pship)</c>,
    /// the same launch the undock path uses). The ripcord teleport calls this
    /// rather than duplicating the math.
    /// </summary>
    private void RepositionForLaunch(Ship ship, DataStationIGC station)
    {
        if (station.Position is not { Length: 3 } sp) return;
        var stationPos = new Vector3(sp[0], sp[1], sp[2]);
        var offset = ship.Position - stationPos;
        var dir = offset.LengthSquared() > 0.01f ? offset.Normalize() : new Vector3(1f, 0f, 0f);
        ship.Position = stationPos + dir * (Sector.DockRadius + LaunchClearMargin);
        ship.Velocity = dir * ExitStationSpeed;
    }

    /// <summary>
    /// P152 — the connection-agnostic core of an undock, extracted from
    /// <see cref="PerformUndock"/> so the existing real-peer undock path and the
    /// new docked-NPC "launch" arm of <see cref="HandleOrderChange"/>
    /// (ORDER_CHANGE Stage 6) share one state transition with no duplication:
    /// resolve the ship's docked station in <paramref name="sector"/>, clear
    /// <see cref="Ship.StationId"/>, push the ship physically clear of
    /// <see cref="Sector.DockRadius"/> via <see cref="RepositionForLaunch"/>, and
    /// start the undock debounce (<see cref="Ship.LastLaunchTime"/>, P75).
    /// <para><b>Why this is safe to share with an NPC.</b> The only part of the
    /// real-peer <see cref="PerformUndock"/> that needs a <see cref="ConnectionId"/>
    /// is the trailing <c>FM_S_SET_CLUSTER(7)</c> re-send — which exists solely to
    /// tell the <em>undocking peer's own client-side simulation</em> that
    /// <c>GetStation() == NULL</c> (<c>appmsg.cpp:1044-1049</c>). An NPC has no
    /// owning peer's client to inform (P147: <c>SpawnNpcShip</c> creates no
    /// <c>_peerShips</c> entry), and other real peers observing the NPC learn its
    /// new flying state for free via the existing
    /// <c>HeavyShipsUpdate</c>/<c>BroadcastShipsUpdate</c> machinery (the same
    /// reasoning P147 established) — so the connection-specific message is
    /// genuinely inapplicable to the NPC path, not merely inconvenient. This
    /// helper is therefore the pure state-mutation core, sending no wire
    /// messages; the caller decides whether any peer-specific send follows.</para>
    /// Caller must have already confirmed <c>ship.StationId != null</c>.
    /// </summary>
    private void UndockShipState(Ship ship, Sector sector)
    {
        var station = ship.StationId!.Value;
        ship.StationId = null;

        // Push the ship physically clear of DockRadius and start the undock
        // debounce (Ship.LastLaunchTime / P75). Without the position push,
        // Sector.ResolveDockCollisions (Sector.cs:453) gates re-docking on
        // StationId==null alone, so the very next tick can re-latch a ship
        // that never moved. Reposition just outside DockRadius along the
        // station->ship direction (a fixed fallback direction if the ship
        // happens to be exactly at the station's position), with an outward
        // velocity kick — see the P69/P74 history in the original undock
        // handler (git blame) for why both the position push and the
        // LaunchCooldownSeconds debounce below are needed together. (Logic and
        // comments reproduced verbatim from the former PerformUndock body — the
        // P152 extraction refactor is behavior-preserving.)
        DataStationIGC? stationModel = null;
        foreach (var st in sector.Stations)
            if (st.StationId == station) { stationModel = st; break; }
        if (stationModel is { } stFound)
            RepositionForLaunch(ship, stFound);

        // P75: start the undock debounce — Sector.ResolveDockCollisions won't
        // consider this ship for re-docking again until Sector.LaunchCooldownSeconds
        // elapses, regardless of how close it drifts back to the station.
        ship.LastLaunchTime = _simNow;
    }

    /// <summary>
    /// Undock a docked ship: clear its station, push it clear of DockRadius
    /// with an outward kick, start the re-dock debounce, and re-send
    /// FM_S_SET_CLUSTER(7) — the only message that clears the native client's
    /// own GetStation()/GetCluster() model state (appmsg.cpp:1044-1049), which
    /// BaseClient::flyingF() (clintlib.h:1198) and the Hangar screen's launch
    /// lockdown (appmsg.cpp:991-996) both gate on. Extracted so both real
    /// undock triggers this server recognizes can share it: an explicit
    /// FM_C_DOCKED(48) request (WOPR bots, P69/P71), and BUY_LOADOUT's own
    /// FLaunch=true (the real WinTrek client's Hangar "Launch" button, which
    /// never sends a separate undock message — see ApplyLoadout below).
    /// Caller must have already confirmed <c>live.Ship.StationId != null</c>.
    /// </summary>
    private void PerformUndock(ConnectionId from, LiveShip live)
    {
        // Capture the docked station id before the undock core clears it — the
        // diagnostic below reports which station the ship left.
        var undockedFrom = live.Ship.StationId!.Value;

        // P152 extraction: the connection-independent state mutation (clear
        // StationId, reposition clear of DockRadius, start the debounce) now
        // lives in UndockShipState, shared with the docked-NPC launch arm of
        // HandleOrderChange. Behavior for this real-peer path is unchanged —
        // this is a pure extraction; only the peer-specific SET_CLUSTER send
        // remains here.
        UndockShipState(live.Ship, live.Sector);

        // Re-send FM_S_SET_CLUSTER(7) to the undocking peer — the only place
        // that calls m_ship->SetStation(NULL) client-side (appmsg.cpp:1044-1049).
        // Without this the client still believes GetStation()!=NULL and
        // GetCluster()==NULL forever, so flyingF() never becomes true and (for
        // the real client) the Hangar launch lockdown never clears either.
        var set = new SetCluster
        {
            SectorId = live.Sector.Id,
            Ship = ServerShipUpdate.FromModel(live.Ship.Id, live.Ship, (uint)TickIndex),
            Cookie = 0,   // same simplification OnShipWarped already uses
        };
        SendPeer(from, FedPacket.Pack(new[] { set.Encode() }));

        var undockNote = $"[undock] ship {live.Ship.Id} undocked from station {undockedFrom} — next SHIP_STATUS(90) carries StationID=NA + State=c_ssFlying";
        Console.WriteLine(undockNote);
        Notable?.Invoke(undockNote);      // spectator event feed (same text)
    }

    /// <summary>
    /// P110 — the teleport/relocation dock request, the <b>non-</b><c>NA</c>
    /// half of <c>FM_C_DOCKED(48)</c>. A displaced ship asks to land at a
    /// specific <paramref name="targetStationId"/>; the server validates it
    /// and answers with <c>FM_S_TELEPORT_ACK(102)</c>. Port of
    /// <c>FedSrv.CPP:7054-7131</c>. This is <b>not</b> the proximity-triggered
    /// dock this port already handles via <see cref="Sector.ResolveDockCollisions"/>
    /// (untouched) — it is the explicit-<c>stationID</c> request direction that
    /// a relocated ship (a future rescue/warp-arrival trigger) uses to land
    /// somewhere, exactly the native responder half. The caller has already
    /// gated: a peer-owned, currently-docked ship
    /// (<c>GetStation()!=NULL</c>, <c>FedSrv.CPP:7040-7041</c>). Runs on the
    /// sim thread (called from the <c>FM_C_DOCKED</c> wire handler).
    /// <para><b>Validation</b> (<c>FedSrv.CPP:7063-7066</c>): the station must
    /// exist, be on the player's side or an ally's, and be
    /// <c>c_sabmRestart</c>-capable. If any fails, native sends the ACK with
    /// <c>stationID = NA</c> (<c>FedSrv.CPP:7067-7073</c>) and <c>break</c>s —
    /// the ship is <b>left where it is</b>, not moved. This port's ally check
    /// collapses to same-side (no alliance concept, stated plainly, matching
    /// how <see cref="FindFriendlyStation"/> defines "friendly").</para>
    /// <para><b>Acknowledge</b>: valid → echo the real <paramref
    /// name="targetStationId"/> and compute <c>bNewHull =
    /// !station.CanBuy(ship's base hull)</c> (<c>FedSrv.CPP:7076</c>). When
    /// <c>bNewHull</c>, mirror <c>FedSrv.CPP:7079-7120</c>: boot boarded
    /// children, refund the ship's hardware value, strip its parts, swap to
    /// the side's Lifepod hull — reusing this port's existing helpers
    /// (<see cref="ShipValue"/>, <see cref="Ship.ClearLoadout"/>,
    /// <see cref="Ship.ChangeHull"/>), the same three operations P105/P108
    /// built, <b>not</b> reimplemented. Finally move any boarded children to
    /// the station and the ship itself (<c>FedSrv.CPP:7124-7131</c>).</para>
    /// </summary>
    private void PerformTeleport(ConnectionId from, LiveShip live, short targetStationId)
    {
        var ship = live.Ship;
        var mission = ship.Mission;
        if (mission is null)
        {
            Console.WriteLine($"[teleport] {from} ship {ship.Id}: no mission — teleport request dropped");
            return;
        }

        // Resolve the named station across every sector (native
        // GetStation(id) spans the mission, FedSrv.CPP:7056).
        DataStationIGC? target = null;
        foreach (var sector in mission.Sectors)
        {
            foreach (var st in sector.Stations)
                if (st.StationId == targetStationId) { target = st; break; }
            if (target is not null) break;
        }

        // Validation triple (FedSrv.CPP:7063-7066): exists, friendly/allied,
        // c_sabmRestart-capable. Side can't be null here — the caller's docked
        // gate implies a placed ship with a side — but guard anyway.
        bool exists = target is not null;
        bool sameSide = target is { } t0 && t0.SideId == ship.SideId;
        bool restartOk = target is { } t1
            && mission.Core.StationTypes.TryGetValue(t1.StationTypeId, out var stype)
            && stype.HasCapability(DataStationTypeIGC.SabmRestart);

        if (!exists || !sameSide || !restartOk)
        {
            // Reject (FedSrv.CPP:7067-7073): ack stationID = NA, ship stays put.
            var reject = new TeleportAck { StationId = -1, BNewHull = false };
            SendPeer(from, FedPacket.Pack(new[] { reject.Encode() }));
            var why = string.Join("; ",
                (exists ? null : "station not found"),
                (sameSide ? null : "not on this side or an ally's"),
                (restartOk ? null : "not c_sabmRestart-capable"))
                .Replace("  ", " ");
            Console.WriteLine($"[teleport] {from} ship {ship.Id}: request for station {targetStationId} rejected — {why} -> TELEPORT_ACK stationID=NA (FedSrv.CPP:7067-7073)");
            return;
        }

        // Valid — acknowledge with the real station id (FedSrv.CPP:7075-7077),
        // and the port's bNewHull = !CanBuy(current hull). stype is guaranteed
        // present past the restartOk guard (which itself requires the lookup
        // to succeed), so the re-lookup here is provably safe. The ship's side
        // provides the civilization base-tech term (native side.GetTechs()).
        mission.Core.StationTypes.TryGetValue(target!.Value.StationTypeId, out var validType);
        var civ = mission.Sides.FirstOrDefault(s => s.Id == ship.SideId!.Value)?.Civilization;
        if (civ is null)
        {
            Console.WriteLine($"[teleport] {from} ship {ship.Id}: side {ship.SideId} not in mission — cannot compute bNewHull; teleport rejected");
            var reject = new TeleportAck { StationId = -1, BNewHull = false };
            SendPeer(from, FedPacket.Pack(new[] { reject.Encode() }));
            return;
        }
        bool bNewHull = !StationSellsHull(validType, civ.Value, ship.HullType);
        var ack = new TeleportAck { StationId = targetStationId, BNewHull = bNewHull };
        SendPeer(from, FedPacket.Pack(new[] { ack.Encode() }));
        Console.WriteLine($"[teleport] {from} ship {ship.Id} -> station {targetStationId} '{target!.Value.Name}' bNewHull={bNewHull} — TELEPORT_ACK(102)");

        if (bNewHull)
        {
            // Boot any boarded children (LeaveShip each) — FedSrv.CPP:7083-7087.
            foreach (var kid in ship.ChildShips.ToArray())
                PerformLeave(kid, ship);

            // Refund the ship's hardware value (FedSrv.CPP:7090-7105) —
            // computed BEFORE the parts are cleared, per native order, and
            // (like P108's board refund, Server.cs:2273-2282) deposited into
            // the side treasury: this port has no per-player wallet and no
            // MONEY_CHANGE codec, so the side accumulator is the money
            // primitive. State the simplification, not a silent drop.
            int refund = ShipValue(ship);
            if (refund > 0 && ship.SideId is { } refundSideId)
            {
                var rs = mission.Sides.FirstOrDefault(s => s.Id == refundSideId);
                if (rs is not null)
                {
                    rs.Money += refund;
                    // P118: broadcast the pooled-treasury refund to the whole
                    // side — the port of the native teleport refund
                    // (FedSrv.CPP:7202-7206), which likewise sends MONEY_CHANGE
                    // with sidFrom = NA (server-initiated) to the side group.
                    BroadcastMoneyChangeSideWide(rs.Id, refund, ship.Id);
                    Console.WriteLine($"[teleport] ship {ship.Id} refunded hardware value {refund} to side {rs.Id} treasury + MONEY_CHANGE(60) to side");
                }
            }

            // Sell off / strip all mounted parts (FedSrv.CPP:7107-7113) →
            // ClearLoadout, the P108-ready helper.
            ship.ClearLoadout();

            // Swap to the side's Lifepod hull (FedSrv.CPP:7116-7117) — the
            // same resolution P105/P107/P108 already use.
            var lifepodId = mission.Sides.First(s => s.Id == ship.SideId!.Value).Civilization.Lifepod;
            if (mission.Core.HullTypes.TryGetValue(lifepodId, out var lp))
            {
                ship.ChangeHull(lp);
                Console.WriteLine($"[teleport] ship {ship.Id} hull -> Lifepod {lp.HullId} (old hull not sellable at station {targetStationId})");
            }
            else
            {
                Console.WriteLine($"[teleport] ship {ship.Id} side lifepod hull {lifepodId} not in core — hull swap skipped (still stripped + refunded)");
            }
        }

        // Relocate to the target station's cluster and dock there — move any
        // boarded children first, then the ship itself (FedSrv.CPP:7124-7131).
        var destSector = mission.Sectors.First(s => s.Stations.Contains(target!.Value));
        ship.Sector = destSector;
        foreach (var kid in ship.ChildShips)          // empty in the bNewHull case (booted above)
            kid.StationId = targetStationId;
        ship.StationId = targetStationId;

        // Keep the owning peer's broadcast bookkeeping coherent — SHIP_STATUS
        // frames derive Sector from the snapshot, so point it at the station's
        // cluster like P105 does (Server.cs:3469-3470).
        _peerShips[from] = _peerShips[from] with { Sector = destSector };

        var note = $"[teleport] ship {ship.Id} docked at station {targetStationId} '{target.Value.Name}' in sector '{destSector.Name}'";
        Console.WriteLine(note);
        Notable?.Invoke(note);
    }

    private void ApplyLoadout(ConnectionId from, LiveShip live, BuyLoadoutRequest buy)
    {
        var ship = live.Ship;
        var mission = ship.Mission;
        if (mission is null)
        {
            Console.WriteLine($"[loadout] {from} ship {ship.Id}: no mission (ship not placed) — cannot apply loadout");
            return;
        }

        // Resolve the requested hull against the static core (the established
        // lookup pattern: StaticCore.HullTypes is keyed by HullId,
        // StaticCore.cs:148). Unknown hull ⇒ nothing to grant ⇒ no ACK.
        if (!mission.Core.HullTypes.TryGetValue(buy.HullId, out var hull))
        {
            Console.WriteLine($"[loadout] {from} ship {ship.Id}: BUY_LOADOUT names unknown hull {buy.HullId} — no ACK (report, not fabricated)");
            return;
        }
        if (hull.HullId != ship.HullType.HullId)
        {
            // Ship's _hull is fixed at spawn (readonly), so a hull change can't
            // be hot-swapped here — Phase 2 territory. Grant the parts against
            // the current hull and be explicit that the ACK echoes the
            // REQUESTED hullId (the configuration granted), not the physical hull.
            Console.WriteLine($"[loadout] {from} ship {ship.Id}: requested hull {hull.HullId} ('{hull.Name}') differs from current {ship.HullType.HullId} — hull hot-swap not implemented (Phase 2); granting parts on the current hull, ACK echoes the requested hullId");
        }

        // Replace the ship's loadout with the requested parts. Mirror the
        // native ProcessShipLoadout rebuild (shipIGC.h:901-921).
        ship.ClearLoadout();
        int granted = 0, skipped = 0;
        foreach (var p in buy.Parts)
        {
            if (!mission.Core.PartTypes.TryGetValue(p.PartId, out var partType))
            {
                Console.WriteLine($"[loadout] {from} ship {ship.Id}: requested part {p.PartId} not in core — skipped (report, not fabricated)");
                skipped++;
                continue;
            }
            ship.MountPart(partType, p.Amount, p.MountId, p.FractionMounted);
            granted++;
        }

        // The ACK's tail reflects the ship's REAL mounted parts after the apply
        // (so the round-trip is byte-for-byte when every requested part mounted).
        var ackParts = ship.Parts.Select(x => x.ToExpandedPartData()).ToArray();
        bool allGranted = ackParts.Length == buy.Parts.Count && granted == buy.Parts.Count && skipped == 0;
        // fBoughtEverything = every requested part was actually mounted. Kept
        // false for the zero-part hull-only case so those bytes stay identical
        // to P26/P27/P28 (preserving the tested adopt-a-hull path exactly).
        bool fboughtEverything = buy.Parts.Count > 0 && allGranted;
        var ack = new BuyLoadoutAck
        {
            HullId = buy.HullId,
            FBoughtEverything = fboughtEverything,
            FLaunch = buy.FLaunch,
            Parts = ackParts,
        };
        SendPeer(from, FedPacket.Pack(new[] { ack.Encode() }));
        var note = $"[loadout] ship {ship.Id} adopted loadout hull {ack.HullId} + {ack.Parts.Count} part(s)";
        Console.WriteLine($"[loadout] {from} ship {ship.Id} <- BUY_LOADOUT_ACK hull {ack.HullId} nParts {ack.Parts.Count} fbought {fboughtEverything} fLaunch {ack.FLaunch}{(skipped > 0 ? $" ({skipped} skipped)" : "")}");
        Notable?.Invoke(note);

        // The real WinTrek client's Hangar screen "Launch" button sends
        // exactly this message with FLaunch=true and nothing else
        // (HangarScreen::OnButtonLaunch -> BuyLoadout(ship, true),
        // hangarscreen.cpp:145-160) -- it never sends a separate undock
        // request the way WOPR bots do (FM_C_DOCKED, see PerformUndock's own
        // doc comment). The client's BUY_LOADOUT_ACK handler deliberately
        // does NOT clear the launch lockdown when fLaunch is true
        // (appmsg.cpp:3180: "if (!pfmBuyLoadoutAck->fLaunch && IsLockedDown())
        // EndLockDown(...)") -- it is waiting for FM_S_SET_CLUSTER instead
        // (appmsg.cpp:991-996). Without this, a real player could never
        // launch at all: every click of the Hangar "Launch" button re-hits
        // HangarScreen::OnButtonLaunch's own "if (!IsLockedDown()) ... else
        // assert(false)" (hangarscreen.cpp:147-157) once the first click
        // leaves the lockdown permanently set, since nothing server-side ever
        // released it.
        if (buy.FLaunch && live.Ship.StationId != null)
        {
            PerformUndock(from, live);
        }
    }

    /// <summary>The real engine's <c>CanMount</c> subset test
    /// (<c>src/Igc/hullTypeIGC.h:199-213</c>, <c>partType.PartMask &amp;
    /// ~hardpoint.PartMask == 0</c>): a part type fits a hardpoint iff its
    /// <c>PartMask</c> bits are a subset of the hardpoint's. Factored out of
    /// <see cref="MountCombatWeapon"/>'s inline test (P145) so the same bit
    /// test is shared, not duplicated, by the TREASURE_ACK mount choice
    /// (native <c>CanMount(ppt, pfmTA-&gt;mountID)</c>, <c>FedSrv.CPP:6100</c>).</summary>
    private static bool CanMount(DataPartTypeIGC part, short hardpointMask) =>
        (part.PartMask & ~hardpointMask) == 0;

    /// <summary>
    /// Arm the freshly-placed ship so a live bot can actually fire — the
    /// missing live half of the (unit-tested) combat slice. Requires the ship
    /// to be placed (<see cref="Ship.MountWeapon"/> resolves the weapon's
    /// projectile against the mission core). Picks, <b>per fixed hardpoint</b>,
    /// the cheapest energy-per-shot <c>Weapon</c> part type (dispersion-free,
    /// so a straight shot lands) whose projectile type exists in the core
    /// <em>and</em> whose <see cref="DataPartTypeIGC.PartMask"/> is a subset of
    /// that specific hardpoint's <see cref="DataHullTypeIGC.Hardpoint.PartMask"/>
    /// — the real engine's <c>CanMount</c> subset test (<c>src/Igc/hullTypeIGC.h:199-213</c>,
    /// <c>partType.PartMask &amp; ~hardpoint.PartMask == 0</c>). Different fixed
    /// hardpoints on the same hull may therefore end up with different weapon
    /// types — that is correct, faithful per-hardpoint behaviour, not a bug to
    /// unify. Non-fixed (turret/gunner) hardpoints are skipped:
    /// <see cref="Weapon.IsWeaponSelected"/> never selects a turret gun here
    /// (Weapon.cs:274-275), so mounting one would be a dead-end — worse than
    /// not mounting it. If the core offers no usable weapon at all, a fixed
    /// hardpoint has no PartMask-compatible weapon anywhere in the core, or the
    /// hull has no fixed hardpoints, the relevant slot is left unmounted and
    /// the omission is logged (not fabricated, never thrown).
    ///
    /// Weapon-compatibility scope, stated plainly (this slice's style — no
    /// shields, no economy, no lobby): the real engine consults only
    /// <c>Hardpoint.PartMask</c> for weapons; the hull-level
    /// <see cref="DataHullTypeIGC.PmEquipment"/> gate applies solely to
    /// non-weapon equipment (<c>hullTypeIGC.h:163-176</c>, the <c>mountID==0</c>
    /// <c>else</c> branch) and non-weapon equipment is already out of scope
    /// (no shields repo-wide). So this check is exactly hardpoint vs
    /// part-type <c>PartMask</c> for weapons — <c>PmEquipment</c> and
    /// <c>Ship</c>'s non-weapon equipment handling are deliberately untouched.
    /// </summary>
    private void MountCombatWeapon(Ship ship)
    {
        var mission = ship.Mission;
        if (mission is null)
        {
            Console.WriteLine("[combat] cannot mount a weapon: ship not placed (no mission)");
            return;
        }
        // The usable-weapon universe is exactly what it was before this task:
        // dispersion-free weapons whose projectile exists and has Power > 0,
        // ordered cheapest energy-per-shot first. This task adds the per-hardpoint
        // PartMask subset dimension; it does not change what makes a weapon usable.
        // Materialised once so each hardpoint can scan the same ordered list cheaply.
        var candidates = mission.Core.PartTypes.Values
            .Where(p => p.EquipmentType == EquipmentType.Weapon)
            .Select(p => (part: p, w: (DataPartTypeIGC.Weapon)p.Type))
            .Where(t => t.w.Dispersion == 0f
                        && mission.Core.ProjectileTypes.TryGetValue(t.w.ProjectileTypeId, out var ptyp) && ptyp.Power > 0f)
            .OrderBy(t => t.w.EnergyPerShot)
            .ToList();
        if (candidates.Count == 0)
        {
            Console.WriteLine("[combat] no usable weapon in core — ship left unarmed (a finding, not fabricated)");
            return;
        }

        var hardpoints = ship.HullType.Hardpoints;
        int fixedCount = 0, armed = 0;
        for (short mount = 0; mount < hardpoints.Length; mount++)
        {
            if (!hardpoints[mount].Fixed)
                continue;              // turret/gunner slot: never selects (Weapon.cs:274-275), so skipping is correct
            fixedCount++;
            short hpMask = hardpoints[mount].PartMask;
            var weaponRec = candidates.FirstOrDefault(t => CanMount(t.part, hpMask));
            if (weaponRec.part == default)
            {
                Console.WriteLine($"[combat] ship {ship.Id}: fixed mount {mount} on hull '{ship.HullType.Name}' "
                    + $"(PartMask {hpMask}) has no PartMask-compatible weapon in core — left unmounted (a finding, not fabricated)");
                continue;
            }
            ship.MountWeapon(weaponRec.w, mount);   // the cheapest compatible weapon for THIS hardpoint
            Console.WriteLine($"[combat]   mount {mount}: '{weaponRec.part.Name}' (PartMask {weaponRec.part.PartMask}, {weaponRec.w.EnergyPerShot}/shot)");
            armed++;
        }
        if (armed == 0)
        {
            Console.WriteLine($"[combat] ship {ship.Id}: hull '{ship.HullType.Name}' has no fixed hardpoints (or none arming) — left unarmed (a finding, not fabricated)");
            return;
        }
        int nonFixed = hardpoints.Length - fixedCount;
        int unmounted = fixedCount - armed;
        Console.WriteLine($"[combat] ship {ship.Id} armed {armed} of {fixedCount} fixed hardpoints "
            + $"(per-hardpoint PartMask-compatible cheapest; {unmounted} fixed slot(s) with no compatible weapon left unmounted; "
            + $"hull {fixedCount} fixed/{hardpoints.Length} total, {nonFixed} non-fixed skipped)");
    }

    /// <summary>
    /// Handle a freshly-created projectile (<see cref="ServerHost.ProjectileCreatedEvent"/>,
    /// raised from <c>Sim/Weapon.cs</c> <c>FireWeapon</c> right after
    /// <c>sector.Add(projectile)</c> on the sim thread) by broadcasting a real
    /// <c>FM_CS_PROJECTILE_INSTANCE(12)</c> <see cref="ProjectileInstance"/>
    /// message to the same-side peers who can see the shot. This is P88: the
    /// first slice that lets a client actually SEE a projectile — until now a
    /// shot resolved entirely server-side and only its damage was observed.
    /// Scope is deliberately <b>same-side only</b>, matching every prior
    /// multi-peer-visibility broadcast (P58/P62/P63/P85/P86 are all same-side
    /// only; cross-side ship visibility is its own open roadmap item, Phase 5).
    /// The recipients are exactly: the shooter's own peer (who always sees it —
    /// that peer's ExportShip was sent at join, Server.cs:~1010) plus same-side
    /// teammates who have already received the shooter's <c>ExportShip(26)</c>
    /// (the <c>_teammateExportSent</c>-gated idiom <c>BroadcastShipStates</c>'s
    /// teammate loop already uses, Server.cs:539-557) — a teammate who has no
    /// client-side object for the shooter can't meaningfully receive the shot
    /// (yet), so the broadcast is gated on that export like every other
    /// teammate send. Cross-side/enemy projectile visibility is NOT attempted
    /// (needs Phase 5's cross-side EXPORT first — not built). Runs on the sim
    /// thread (the same one that owns <c>_peerShips</c> and broadcasts ship
    /// states), so no extra locking is needed — mirroring
    /// <see cref="BroadcastKillShip"/>.
    /// </summary>
    private void BroadcastProjectileInstance(Projectile p)
    {
        if (p.Launcher is not Ship shooter)
        {
            Console.WriteLine("[projectile] ProjectileCreatedEvent with a non-Ship launcher — ignored");
            return;
        }
        if (shooter.SideId is not { } shooterSide)
        {
            Console.WriteLine($"[projectile] shooter {shooter.Id} has no side — no PROJECTILE_INSTANCE broadcast");
            return;
        }

        // uint Time = the server tick index (this project's uint-Time convention,
        // same as BroadcastShipStates stamps ServerShipUpdate.Time).
        uint timeCreate = (uint)TickIndex;

        var msg = ProjectileInstance.FromProjectile(p, timeCreate);
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        int n = 0;
        foreach (var kv in _peerShips)
        {
            // Shooter's own peer always receives it (that peer's own EXPORT was
            // sent at join). Teammates receive it only once they already hold
            // the shooter's ExportShip — the exact _teammateExportSent gate
            // (Server.cs:539-557) BroadcastShipStates' teammate loop uses for
            // every same-side teammate visibility send. Everyone else (cross-side
            // enemies, and same-side peers who never got the shooter's export)
            // is excluded.
            bool isShooter = ReferenceEquals(kv.Value.Ship, shooter);
            bool isVisibleTeammate = !isShooter
                                     && kv.Value.Ship.SideId == shooterSide
                                     && _teammateExportSent.Contains((kv.Key, shooter.Id));
            if (isShooter || isVisibleTeammate)
            {
                SendPeer(kv.Key, packet);
                n++;
            }
        }
        Console.WriteLine($"[projectile] ship {shooter.Id} fired -> PROJ_INST(12) type {p.ProjectileType.ProjectileTypeId} " +
                          $"pos {p.Position} vel {p.Velocity} fwd {p.Forward} life {p.ProjectileType.Lifespan} -> {n} same-side peer(s)");
    }

    /// <summary>
    /// Handle a freshly-fired missile (<see cref="ServerHost.MissileCreatedEvent"/>,
    /// raised from <c>Sim/Ship.cs</c> <c>FireMissile</c> right after
    /// <c>sector.Add(missile)</c> on the sim thread) by broadcasting a real
    /// <c>FM_CS_FIRE_MISSILE(70)</c> <see cref="FireMissile"/> message to the
    /// same-side peers who can see the launch. This is P90's creation half:
    /// lets a client SEE a missile being fired (the P89 codec finally used in a
    /// live tick). Carries exactly one <see cref="MissileLaunchData"/> entry —
    /// this slice fires one missile at a time, not a salvo; a multi-missile
    /// salvo (<c>MissileLaunchData[]</c> with &gt; 1 entry per fire) is a separate,
    /// later extension, so <c>Entries</c> always has exactly one element here.
    /// Scope is deliberately <b>same-side only</b>, matching every prior
    /// multi-peer-visibility broadcast (P58/P62/P63/P85/P86/P88 are all same-side
    /// only). The recipients are exactly: the launcher's own peer (who always
    /// sees it — that peer's ExportShip was sent at join, Server.cs:~1010) plus
    /// same-side teammates who have already received the launcher's
    /// <c>ExportShip(26)</c> (the <c>_teammateExportSent</c>-gated idiom
    /// <c>BroadcastShipStates</c>'s teammate loop already uses, Server.cs:539-557)
    /// — the identical idiom <see cref="BroadcastProjectileInstance"/> uses,
    /// copied exactly. The lock/target fields are placeholders (this slice fires
    /// with no target and no lock accumulation; the homing target pipeline is a
    /// later task), so <c>Lock = 0</c>, <c>TargetType = 0</c>, <c>TargetId = NA(-1)</c>,
    /// <c>BDud = false</c>. Runs on the sim thread, so no extra locking — mirroring
    /// <see cref="BroadcastKillShip"/>. Cross-side/enemy visibility is NOT
    /// attempted (needs Phase 5's cross-side EXPORT first — not built).
    /// </summary>
    private void BroadcastFireMissile(Missile m)
    {
        if (m.Launcher is not Ship shooter)
        {
            Console.WriteLine("[missile] MissileCreatedEvent with a non-Ship launcher — ignored");
            return;
        }
        if (shooter.SideId is not { } shooterSide)
        {
            Console.WriteLine($"[missile] launcher {shooter.Id} has no side — no FIRE_MISSILE broadcast");
            return;
        }

        // uint Time = the server tick index (this project's uint-Time convention,
        // same as BroadcastProjectileInstance stamps timeCreate=(uint)TickIndex).
        uint timeFired = (uint)TickIndex;

        var msg = new FireMissile
        {
            Lock = 0f,                               // no homing target this slice
            TimeFired = timeFired,
            MissileTypeId = m.MissileType.ExpendableTypeId,
            LauncherId = shooter.Id,
            ClusterId = m.OwnerSector.Id,
            TargetType = 0,                          // OT_* placeholder — no target this slice
            TargetId = -1,                           // NA — no target
            BDud = false,
            // Exactly one missile per fire — this slice always sends a single
            // missile, never a salvo (see class doc).
            Entries =
            [
                new MissileLaunchData(m.Position, m.Forward, m.Velocity, m.Id),
            ],
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        int n = 0;
        foreach (var kv in _peerShips)
        {
            // Launcher's own peer always receives it (that peer's own EXPORT was
            // sent at join). Teammates receive it only once they already hold the
            // launcher's ExportShip — the exact _teammateExportSent gate (same as
            // BroadcastProjectileInstance, Server.cs:2002-2010). Everyone else is
            // excluded.
            bool isShooter = ReferenceEquals(kv.Value.Ship, shooter);
            bool isVisibleTeammate = !isShooter
                                     && kv.Value.Ship.SideId == shooterSide
                                     && _teammateExportSent.Contains((kv.Key, shooter.Id));
            if (isShooter || isVisibleTeammate)
            {
                SendPeer(kv.Key, packet);
                n++;
            }
        }
        Console.WriteLine($"[missile] ship {shooter.Id} fired -> FIRE_MISSILE(70) type {m.MissileType.ExpendableTypeId} " +
                          $"pos {m.Position} vel {m.Velocity} fwd {m.Forward} life {m.MissileType.Lifespan} -> {n} same-side peer(s)");
    }

    /// <summary>
    /// Handle a missile that just expired (<see cref="ServerHost.MissileDestroyedEvent"/>,
    /// raised from <c>Sim/Missile.cs</c> <c>OnUpdate</c> alongside
    /// <c>sector.Remove(missile)</c> on the sim thread) by broadcasting a real
    /// <c>FM_S_MISSILE_DESTROYED(83)</c> <see cref="MissileDestroyed"/> message —
    /// the \"this missile was destroyed here\" notice — to the same same-side
    /// audience that saw it launch (<see cref="BroadcastFireMissile"/>). This is
    /// P90's destruction half: new territory P88 did not need (a projectile's
    /// expiry is silent), reached by modelling the missile's own expiry as the
    /// analogue of a ship's death (<c>Ship.ReceiveDamage</c> →
    /// <c>KillShipEvent</c>, <c>Sim/Ship.cs:494</c>). The position carried is
    /// where the missile <b>expired</b> (<c>m.Position</c> after its straight-line
    /// flight, ≈ <c>launchPosition + velocity·Lifespan</c>), not its launch
    /// position — the observable contract the verification asserts. Same-side
    /// only, same <c>_teammateExportSent</c>-gated audience, same sim-thread-no-
    /// lock reasoning as the creation broadcast.
    /// </summary>
    private void BroadcastMissileDestroyed(Missile m)
    {
        if (m.Launcher is not Ship shooter)
        {
            Console.WriteLine("[missile] MissileDestroyedEvent with a non-Ship launcher — ignored");
            return;
        }
        if (shooter.SideId is not { } shooterSide)
        {
            Console.WriteLine($"[missile] launcher {shooter.Id} has no side — no MISSILE_DESTROYED broadcast");
            return;
        }

        var msg = new MissileDestroyed
        {
            Position = m.Position,              // where it expired, not where it launched
            ClusterId = m.OwnerSector.Id,
            MissileId = m.Id,
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        int n = 0;
        foreach (var kv in _peerShips)
        {
            bool isShooter = ReferenceEquals(kv.Value.Ship, shooter);
            bool isVisibleTeammate = !isShooter
                                     && kv.Value.Ship.SideId == shooterSide
                                     && _teammateExportSent.Contains((kv.Key, shooter.Id));
            if (isShooter || isVisibleTeammate)
            {
                SendPeer(kv.Key, packet);
                n++;
            }
        }
        Console.WriteLine($"[missile] ship {shooter.Id} expired missile {m.Id} -> MISSILE_DESTROYED(83) " +
                          $"pos {m.Position} sector {m.OwnerSector.Id} -> {n} same-side peer(s)");
    }

    /// <summary>
    /// Handle a ship loadout mutation (<see cref="ServerHost.LoadoutChange"/>,
    /// raised from <c>Sim/Ship.cs</c> <c>ChangeHull</c>/<c>MountPart</c>/
    /// <c>TryRemovePart</c>/<c>ClearLoadout</c>/<c>AddChildShip</c>/
    /// <c>RemoveChildShip</c> right where the mutation lands) by broadcasting a
    /// real <c>FM_S_LOADOUT_CHANGE(96)</c> <see cref="Allegiance.Core.Wire.LoadoutChange"/> to the
    /// same-side peers who can see the changed ship. The message carries the
    /// ship's <b>current whole</b> loadout (hull id + every mounted part) plus its
    /// boarded-child list — built fresh at broadcast time exactly like native
    /// <c>CFSShip::QueueLoadoutChange</c>'s per-event <c>ExportShipLoadout</c>
    /// (<c>FedSrv/fsship.cpp:708-735</c>), not a delta. Scope is <b>same-side
    /// only</b>, matching every prior per-ship broadcast (P58/P62/P63/P85/P86/P88/
    /// P90/P94 are all same-side only): the changed ship's own peer (who always
    /// sees it) plus same-side teammates who have already received that ship's
    /// <c>ExportShip(26)</c> — the exact <c>_teammateExportSent</c>-gated idiom
    /// <see cref="BroadcastExportMine"/> uses. Cross-side/enemy ship visibility is
    /// intentionally not attempted (the port has no cross-side <c>ExportShip</c>;
    /// matching that scope, an enemy does not receive LOADOUT_CHANGE). Runs on the
    /// thread that raised the site event (the mission tick mutating the ship, or a
    /// direct caller of a Ship mutation method), reaching the sim-thread-owned
    /// <c>_peerShips</c> map the same way the other broadcasts do.
    /// </summary>
    private void BroadcastLoadoutChange(IShipIgc pship, IPartIgc ppart, LoadoutChange lc)
    {
        if (pship is not Ship ship)
        {
            Console.WriteLine("[loadout] LoadoutChangeEvent with a non-Ship — ignored");
            return;
        }
        if (ship.SideId is not { } shipSide)
        {
            Console.WriteLine($"[loadout] ship {ship.Id} has no side — no LOADOUT_CHANGE broadcast");
            return;
        }

        // The changed ship's CURRENT whole loadout (hull + every part), rebuilt
        // fresh at broadcast time (native ExportShipLoadout, FedSrv/fsship.cpp:710),
        // plus one PassengerData per boarded child (fsship.cpp:726-733). sidShip is
        // the ship's own real id — the non-force broadcast-to-observers path.
        var msg = new Allegiance.Core.Wire.LoadoutChange
        {
            HullId = ship.HullType.HullId,
            SidShip = ship.Id,
            Parts = ship.Parts
                .Select(p => new ExpandedPartData(p.PartId, p.Amount, p.MountId, p.FractionMounted))
                .ToArray(),
            Passengers = ship.ChildShips
                .Select(c => new PassengerData(c.Id, (byte)(c.TurretId ?? 0)))
                .ToArray(),
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        int n = 0;
        foreach (var kv in _peerShips)
        {
            // Changed ship's own peer always receives it (that peer owns it).
            // Teammates receive it only once they already hold the ship's
            // ExportShip — the exact _teammateExportSent gate BroadcastExportMine
            // uses. Everyone else (cross-side enemies, same-side peers who never
            // got the export) is excluded.
            bool isOwner = ReferenceEquals(kv.Value.Ship, ship);
            bool isVisibleTeammate = !isOwner
                                     && kv.Value.Ship.SideId == shipSide
                                     && _teammateExportSent.Contains((kv.Key, ship.Id));
            if (isOwner || isVisibleTeammate)
            {
                SendPeer(kv.Key, packet);
                n++;
            }
        }
        Console.WriteLine($"[loadout] ship {ship.Id} {lc} -> LOADOUT_CHANGE(96) hull {msg.HullId} " +
                          $"{msg.Parts.Count} part(s) {msg.Passengers.Count} passenger(s) -> {n} same-side peer(s)");
    }

    /// <summary>
    /// Handle a freshly-deployed mine (<see cref="ServerHost.MineCreatedEvent"/>,
    /// raised from <c>Sim/Ship.cs</c> <c>DeployMine</c> right after
    /// <c>sector.Add(mine)</c> on the sim thread) by broadcasting a real
    /// <c>FM_S_EXPORT(26)</c> carrying an <c>OT_mine</c> (3)
    /// <see cref="DataMineExport"/> to the same-side peers who can see the
    /// deployment. This is P94's creation half: the first live use of the P93
    /// <see cref="ExportMine"/> codec. Unlike a missile, a mine needs no dedicated
    /// instance message — the reference server creates it through the same
    /// generic <c>CreateObject(..., OT_mine, &amp;obj, sizeof(obj))</c> path every
    /// other exported object uses (<c>FedSrv.CPP:10072</c>) and it is exported by
    /// <c>CmineIGC::Export</c> (<c>src/Igc/mineigc.cpp:158-185</c>), so the
    /// generic EXPORT envelope is the real wire notice. The message is built by
    /// <see cref="ExportMine.FromMine"/>, which encodes the freshly-armed
    /// fraction 1.0 via the div-240 <see cref="CompactQuant.Percent"/> convention
    /// and sets <c>createNow</c>=true (matching the reference server's
    /// <c>SetCreateNow()</c> after creation, <c>FedSrv.CPP:10072-10073</c>).
    /// Scope is deliberately <b>same-side only</b>, matching every prior
    /// multi-peer-visibility broadcast (P58/P62/P63/P85/P86/P88/P90 are all
    /// same-side only). The recipients are exactly: the laying ship's own peer
    /// (who always sees it — that peer's ExportShip was sent at join,
    /// Server.cs:~1010) plus same-side teammates who have already received the
    /// layer's <c>ExportShip(26)</c> (the <c>_teammateExportSent</c>-gated idiom
    /// <c>BroadcastShipStates</c>'s teammate loop uses, Server.cs:539-557) — the
    /// identical idiom <see cref="BroadcastProjectileInstance"/>/
    /// <see cref="BroadcastFireMissile"/> use, copied exactly. Cross-side/enemy
    /// mine visibility is NOT attempted — a real mine is arguably a hazard any
    /// side should see, but that needs Phase 5's cross-side EXPORT first, and is
    /// not this task's problem. Runs on the sim thread, so no extra locking.
    /// </summary>
    private void BroadcastExportMine(Mine m)
    {
        if (m.Launcher is not Ship layer)
        {
            Console.WriteLine("[mine] MineCreatedEvent with a non-Ship launcher — ignored");
            return;
        }
        if (layer.SideId is not { } layerSide)
        {
            Console.WriteLine($"[mine] layer {layer.Id} has no side — no EXPORT(mine) broadcast");
            return;
        }

        // uint Time = the server tick index (this project's uint-Time convention,
        // same as the other broadcasts stamp timeCreate=(uint)TickIndex).
        uint timeCreate = (uint)TickIndex;
        var msg = ExportMine.FromMine(m, timeCreate, launcherId: layer.Id, sideId: layerSide);
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        int n = 0;
        foreach (var kv in _peerShips)
        {
            // Layer's own peer always receives it (that peer's own EXPORT was
            // sent at join). Teammates receive it only once they already hold
            // the layer's ExportShip — the exact _teammateExportSent gate (same
            // as BroadcastProjectileInstance, Server.cs:2002-2010). Everyone
            // else (cross-side enemies, and same-side peers who never got the
            // layer's export) is excluded.
            bool isLayer = ReferenceEquals(kv.Value.Ship, layer);
            bool isVisibleTeammate = !isLayer
                                     && kv.Value.Ship.SideId == layerSide
                                     && _teammateExportSent.Contains((kv.Key, layer.Id));
            if (isLayer || isVisibleTeammate)
            {
                SendPeer(kv.Key, packet);
                n++;
            }
        }
        Console.WriteLine($"[mine] ship {layer.Id} deployed -> EXPORT(26)/OT_mine {m.MineType.ExpendableTypeId} " +
                          $"pos {m.Position} life {m.MineType.Lifespan} -> {n} same-side peer(s)");
    }

    /// <summary>
    /// Handle a mine that just expired (<see cref="ServerHost.MineDestroyedEvent"/>,
    /// raised from <c>Sim/Mine.cs</c> <c>OnUpdate</c> alongside
    /// <c>sector.Remove(mine)</c> on the sim thread) by broadcasting a real
    /// <c>FM_S_MINE_DESTROYED(84)</c> <see cref="MineDestroyed"/> message — the
    /// \"this mine blew up\" notice (<c>Messages.h:603-606</c>) — to the same
    /// same-side audience that saw it deploy (<see cref="BroadcastExportMine"/>).
    /// This is P94's destruction half, reached by modelling the mine's own
    /// expiry as the analogue of a ship's death (<c>Ship.ReceiveDamage</c> →
    /// <c>KillShipEvent</c>, <c>Sim/Ship.cs:494</c>), exactly as
    /// <see cref="BroadcastMissileDestroyed"/> does for missiles. Because a mine
    /// never moves, the position carried (<c>m.Position</c>) is the <b>same</b>
    /// deployment position the creation export carried — the observable
    /// \"the mine didn't move\" contract this task's verification asserts
    /// explicitly. Same-side only, same <c>_teammateExportSent</c>-gated
    /// audience, same sim-thread-no-lock reasoning as the other broadcasts.
    /// </summary>
    private void BroadcastMineDestroyed(Mine m)
    {
        if (m.Launcher is not Ship layer)
        {
            Console.WriteLine("[mine] MineDestroyedEvent with a non-Ship launcher — ignored");
            return;
        }
        if (layer.SideId is not { } layerSide)
        {
            Console.WriteLine($"[mine] layer {layer.Id} has no side — no MINE_DESTROYED broadcast");
            return;
        }

        var msg = new MineDestroyed
        {
            ClusterId = m.OwnerSector.Id,
            MineId = m.Id,
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        int n = 0;
        foreach (var kv in _peerShips)
        {
            bool isLayer = ReferenceEquals(kv.Value.Ship, layer);
            bool isVisibleTeammate = !isLayer
                                     && kv.Value.Ship.SideId == layerSide
                                     && _teammateExportSent.Contains((kv.Key, layer.Id));
            if (isLayer || isVisibleTeammate)
            {
                SendPeer(kv.Key, packet);
                n++;
            }
        }
        Console.WriteLine($"[mine] ship {layer.Id} expired mine {m.Id} -> MINE_DESTROYED(84) " +
                          $"pos {m.Position} sector {m.OwnerSector.Id} -> {n} same-side peer(s)");
    }

    /// <summary>
    /// Handle a freshly-deployed probe (<see cref="ServerHost.ProbeCreatedEvent"/>,
    /// raised from <c>Sim/Ship.cs</c> <c>DeployProbe</c> right after
    /// <c>sector.Add(probe)</c> on the sim thread) by broadcasting a real
    /// <c>FM_S_EXPORT(26)</c> carrying an <c>OT_probe</c> (4)
    /// <see cref="DataProbeExport"/> to the same-side peers who can see the
    /// deployment. This is P99's creation half: the first live use of the P98
    /// <see cref="ExportProbe"/> codec. Like a mine, a probe needs no dedicated
    /// instance message — the reference server creates it through the same
    /// generic <c>CreateObject(..., OT_probe, &amp;obj, sizeof(obj))</c> path
    /// every other exported object uses and it is exported by
    /// <c>CprobeIGC::Export</c> (<c>src/Igc/probeigc.cpp:535-568</c>), so the
    /// generic EXPORT envelope is the real wire notice. The message is built by
    /// <see cref="ExportProbe.FromProbe"/>, which encodes the targetless probe
    /// with <c>otTarget</c>/<c>oidTarget</c> = NA/-1 (this slice never sets a
    /// target — see <c>Probe.cs</c> remarks) and <c>createNow</c>=true. Scope is
    /// deliberately <b>same-side only</b>, matching every prior ordnance
    /// broadcast (P58/P62/P63/P85/P86/P88/P90/P94). The recipients are exactly:
    /// the deploying ship's own peer (who always sees it — that peer's ExportShip
    /// was sent at join, Server.cs:~1010) plus same-side teammates who have
    /// already received the deployer's <c>ExportShip(26)</c> (the
    /// <c>_teammateExportSent</c>-gated idiom — the identical loop
    /// <see cref="BroadcastExportMine"/> uses, copied exactly). Cross-side/enemy
    /// probe visibility is NOT attempted (Phase 5's cross-side EXPORT remains
    /// unbuilt). Runs on the sim thread, so no extra locking.
    /// </summary>
    private void BroadcastExportProbe(Probe p)
    {
        if (p.Launcher is not Ship deployer)
        {
            Console.WriteLine("[probe] ProbeCreatedEvent with a non-Ship launcher — ignored");
            return;
        }
        if (deployer.SideId is not { } deployerSide)
        {
            Console.WriteLine($"[probe] deployer {deployer.Id} has no side — no EXPORT(probe) broadcast");
            return;
        }

        // uint Time = the server tick index (this project's uint-Time convention,
        // same as the mine/other broadcasts stamp timeCreate=(uint)TickIndex).
        uint timeCreate = (uint)TickIndex;
        var msg = ExportProbe.FromProbe(p, timeCreate, launcherId: deployer.Id, sideId: deployerSide);
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        int n = 0;
        foreach (var kv in _peerShips)
        {
            // Deployer's own peer always receives it. Teammates receive it only
            // once they already hold the deployer's ExportShip — the exact
            // _teammateExportSent gate. Everyone else (cross-side enemies, and
            // same-side peers who never got the deployer's export) is excluded.
            bool isDeployer = ReferenceEquals(kv.Value.Ship, deployer);
            bool isVisibleTeammate = !isDeployer
                                     && kv.Value.Ship.SideId == deployerSide
                                     && _teammateExportSent.Contains((kv.Key, deployer.Id));
            if (isDeployer || isVisibleTeammate)
            {
                SendPeer(kv.Key, packet);
                n++;
            }
        }
        Console.WriteLine($"[probe] ship {deployer.Id} deployed -> EXPORT(26)/OT_probe {p.ProbeType.ExpendableTypeId} " +
                          $"pos {p.Position} life {p.ProbeType.Lifespan} side {deployerSide} -> {n} same-side peer(s)");
    }

    /// <summary>
    /// Handle a probe that just expired (<see cref="ServerHost.ProbeDestroyedEvent"/>,
    /// raised from <c>Sim/Probe.cs</c> <c>OnUpdate</c> alongside
    /// <c>sector.Remove(probe)</c> on the sim thread) by broadcasting a real
    /// <c>FM_S_PROBE_DESTROYED(85)</c> <see cref="ProbeDestroyed"/> message —
    /// the &quot;this probe was destroyed here&quot; notice
    /// (<c>Messages.h:608-611</c>) — to the same same-side audience that saw it
    /// deploy (<see cref="BroadcastExportProbe"/>). This is P99's destruction
    /// half, reached by modelling the probe's own expiry as the analogue of a
    /// ship's death (<c>Ship.ReceiveDamage</c> → <c>KillShipEvent</c>,
    /// <c>Sim/Ship.cs:494</c>), exactly as
    /// <see cref="BroadcastMineDestroyed"/> does for mines. Because a probe
    /// never moves, the position carried (<c>p.Position</c>) is the <b>same</b>
    /// deployment position the creation export carried — the observable
    /// &quot;the probe didn't move&quot; contract this task's verification
    /// asserts explicitly. Same-side only, same <c>_teammateExportSent</c>-gated
    /// audience, same sim-thread-no-lock reasoning as the other broadcasts.
    /// </summary>
    private void BroadcastProbeDestroyed(Probe p)
    {
        if (p.Launcher is not Ship deployer)
        {
            Console.WriteLine("[probe] ProbeDestroyedEvent with a non-Ship launcher — ignored");
            return;
        }
        if (deployer.SideId is not { } deployerSide)
        {
            Console.WriteLine($"[probe] deployer {deployer.Id} has no side — no PROBE_DESTROYED broadcast");
            return;
        }

        var msg = new ProbeDestroyed
        {
            ClusterId = p.OwnerSector.Id,
            ProbeId = p.Id,
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        int n = 0;
        foreach (var kv in _peerShips)
        {
            bool isDeployer = ReferenceEquals(kv.Value.Ship, deployer);
            bool isVisibleTeammate = !isDeployer
                                     && kv.Value.Ship.SideId == deployerSide
                                     && _teammateExportSent.Contains((kv.Key, deployer.Id));
            if (isDeployer || isVisibleTeammate)
            {
                SendPeer(kv.Key, packet);
                n++;
            }
        }
        Console.WriteLine($"[probe] ship {deployer.Id} expired probe {p.Id} -> PROBE_DESTROYED(85) " +
                          $"pos {p.Position} sector {p.OwnerSector.Id} -> {n} same-side peer(s)");
    }

    /// <summary>
    /// Handle a warp bombed by a missile (<see cref="ServerHost.WarpBombEvent"/>,
    /// raised from <c>Sector.ResolveWarpBombTriggers</c> on the sim thread when a
    /// live <see cref="Missile"/> reaches a warp, P101) by broadcasting a real
    /// <c>FM_S_WARP_BOMB(188)</c> <see cref="WarpBomb"/> message — the
    /// "aleph destabilized" notice (<c>Inc/Messages.h:969-973</c>) that a missile
    /// hit a warp and will explode there after the delay
    /// (<see cref="Model.Sector.WarpBombDelaySeconds"/>). <c>timeExplosion</c> is
    /// the future tick at which the sim resolves the real explosion at the warp —
    /// the delay converted to server ticks (<c>_period</c>), stamped in this
    /// project's uint-TickIndex wire-time convention (<c>(uint)TickIndex</c>,
    /// same as every other broadcast).
    /// <para><b>WHOLE MISSION — the one genuine behavioural departure from every
    /// prior ordnance broadcast in this project.</b> Native sends <c>WARP_BOMB</c>
    /// to <c>GetGroupRealSides()</c> (<c>FedSrv.CPP:12558</c>) — all real sides,
    /// not a same-side gate. Every prior ordnance broadcast
    /// (<c>BroadcastProjectileInstance</c>/<c>BroadcastFireMissile</c>/
    /// <c>BroadcastMineDestroyed</c>/<c>BroadcastProbeDestroyed</c>, P88/P90/P94/P99)
    /// is same-side only; this one reaches <b>every</b> connected peer, on any
    /// side. Do not "fix" it to same-side to match the neighbours. Runs on the
    /// sim thread (same one that owns <c>_peerShips</c>), so no extra locking.</para>
    /// </summary>
    private void BroadcastWarpBomb(ResolvedWarp warp, Missile missile)
    {
        long detonationTick = TickIndex + (long)Math.Round(Sector.WarpBombDelaySeconds / _period);
        var msg = new WarpBomb
        {
            TimeExplosion = (uint)detonationTick,
            WarpIdBombed = warp.Warp.WarpId,
            ExpendableIdMissile = missile.MissileType.ExpendableTypeId,
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        // Whole mission: every connected peer with a seated ship, regardless of
        // side (GetGroupRealSides()).
        var targets = _peerShips.Select(kv => kv.Key).ToArray();

        Console.WriteLine($"[warpbomb] warp {warp.Warp.WarpId} bombed by missile {missile.Id} " +
                          $"(missileType {missile.MissileType.ExpendableTypeId}) -> WARP_BOMB(188) " +
                          $"timeExplosion tick {msg.TimeExplosion} -> {targets.Length} peer(s) [WHOLE MISSION]");
        foreach (var t in targets)
            SendPeer(t, packet);
    }

    /// <summary>
    /// Handle a combat-killed asteroid (<see cref="ServerHost.KillAsteroidEvent"/>,
    /// raised from <c>Model.Sector.ResolveCombatCollisions</c> on the sim thread
    /// when a real ordnance hit crosses the rock's <see cref="Asteroid.Fraction"/>
    /// to zero, P102) by broadcasting a real <c>FM_S_ASTEROID_DESTROYED(86)</c>
    /// <see cref="AsteroidDestroyed"/> message — the &quot;this asteroid was
    /// destroyed here&quot; notice (<c>Messages.h:613-617</c>).
    /// <para><b>WHOLE MISSION.</b> Native sends <c>ASTEROID_DESTROYED</c> from the
    /// plain <c>(IAsteroidIgc, bool)</c> overload to <c>GetGroupRealSides()</c>
    /// (<c>FedSrv.CPP:12418</c>) — all real sides, not a same-side gate, the same
    /// audience P101 settled for <c>WARP_BOMB</c>. Do not &quot;fix&quot; it to
    /// same-side to match the neighbouring ship/probe/mine broadcasts. Runs on the
    /// sim thread (same one that owns <c>_peerShips</c>), so no extra locking.
    /// <c>explodeF</c> is true from the only call site this task implements (combat
    /// death), transmitted verbatim.</para>
    /// </summary>
    private void BroadcastAsteroidDestroyed(Asteroid a, bool explodeF)
    {
        var msg = new AsteroidDestroyed
        {
            ClusterId = a.Data.ClusterId,
            AsteroidId = a.Data.AsteroidId,
            ExplodeF = explodeF,
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        // Whole mission: every connected peer with a seated ship, regardless of
        // side (GetGroupRealSides(), FedSrv.CPP:12418).
        var targets = _peerShips.Select(kv => kv.Key).ToArray();

        Console.WriteLine($"[asteroid] asteroid {a.Data.AsteroidId} '{a.Data.Name}' destroyed (explodeF={explodeF}) " +
                          $"=> ASTEROID_DESTROYED(86) sector {a.Data.ClusterId} -> {targets.Length} peer(s) [WHOLE MISSION]");
        foreach (var t in targets)
            SendPeer(t, packet);
    }

    /// <summary>
    /// Handle a combat-destroyed station (<see cref="ServerHost.KillStationEvent"/>,
    /// raised from <c>Model.Sector.ResolveCombatCollisions</c> on the sim thread
    /// when a real ordnance hit crosses the station's
    /// <see cref="Station.ArmorFraction"/> to zero, P125) by, in the native order
    /// (<c>FedSrvSiteBase::KillStationEvent</c>, <c>FedSrv.CPP:12237-12279</c>):
    /// <list type="number">
    /// <item><b>VacateStation</b> (<c>fsmission.cpp:4783</c>) — kick every ship
    /// docked at the dead base out through the real undock path
    /// (<see cref="PerformUndock"/>: clear <see cref="Ship.StationId"/>, push
    /// clear of the (now-gone) dock radius, start the undock debounce, re-send
    /// FM_S_SET_CLUSTER). Reuses the existing undock/eject helper rather than
    /// writing new ejection logic — the task's explicit instruction (P105/P107/
    /// P110 helpers).</item>
    /// <item><b>Broadcast <c>FM_S_STATION_DESTROYED(59)</c></b>
    /// (<see cref="Wire.StationDestroyed"/>, <c>Messages.h:436-439</c>) with
    /// <c>stationID</c> from the station and <c>launcher</c> = the credited
    /// ship's id, or <c>NA</c> when the launcher was not a ship
    /// (<c>FedSrv.CPP:12277-12278</c>).</item>
    /// </list>
    /// <para><b>WHOLE MISSION.</b> Native sends <c>STATION_DESTROYED</c> to
    /// <c>GetGroupRealSides()</c> (<c>FedSrv.CPP:12279</c>) — all real sides, not
    /// a same-side gate, the same audience P102 settled for <c>ASTEROID_DESTROYED</c>.
    /// Do not &quot;fix&quot; it to same-side. The score/Steam bookkeeping in the
    /// native function (<c>KillBase</c>, <c>AwardBaseKillOrCapture</c>,
    /// <c>AddBaseKill</c>) is confirmed N/A — no score or Steam system exists
    /// anywhere in this port. Runs on the sim thread (same one that owns
    /// <c>_peerShips</c>), so no extra locking.</para>
    /// </summary>
    private void BroadcastStationDestroyed(Station station, Model? launcher)
    {
        short launcherId = -1;                     // NA
        if (launcher is Ship s) launcherId = s.Id;

        // 1. Vacate every ship docked at the destroyed station (native
        //    VacateStation, fsmission.cpp:4783) — reuse PerformUndock, the real
        //    undock/eject path (StationId cleared + launch reposition + cooldown
        //    + FM_S_SET_CLUSTER). The dead station is already gone from the
        //    sector's Stations list (removed in the combat pass), so
        //    PerformUndock's reposition lookup finds nothing and simply skips the
        //    launch-reposition — the ship is still correctly ejected.
        foreach (var kv in _peerShips.ToArray())
            if (kv.Value.Ship.StationId == station.StationId)
                PerformUndock(kv.Key, kv.Value);

        // 2. Whole-mission broadcast: every connected peer with a seated ship,
        //    regardless of side (GetGroupRealSides(), FedSrv.CPP:12279).
        var msg = new StationDestroyed
        {
            StationId = station.StationId,
            Launcher = launcherId,
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        var targets = _peerShips.Select(kv => kv.Key).ToArray();

        var note = $"[station] station {station.StationId} destroyed " +
                   $"(launcher {launcherId}) => STATION_DESTROYED(59) -> {targets.Length} peer(s) [WHOLE MISSION]";
        Console.WriteLine(note);
        Notable?.Invoke(note);                     // spectator event feed (same text)

        foreach (var t in targets)
            SendPeer(t, packet);
    }

    /// <summary>
    /// Handle a captured station (<see cref="ServerHost.CaptureStationEvent"/>,
    /// raised from <c>Model.Sector.ResolveDockCollisions</c> on the sim thread
    /// when a hostile ship with a <c>c_habmBoard</c> hull docks an enemy base,
    /// P126) by broadcasting a real <c>FM_S_STATION_CAPTURE(58)</c>
    /// (<see cref="Wire.StationCapture"/>, <c>Messages.h:429-434</c>) with
    /// <c>stationID</c> from the station, <c>sidOld</c> = the station's OLD
    /// owner (still current here because the sector raises this seam BEFORE
    /// re-siding), <c>sidNew</c> = the capturing ship's side, and
    /// <c>shipIDCredit</c> = the capturing ship's id — the native field order of
    /// <c>CFSShip::CaptureStation</c> (<c>fsship.cpp:625-628</c>).
    /// <para><b>WHOLE MISSION.</b> Native sends <c>STATION_CAPTURE</c> to
    /// <c>GetMission()-&gt;GetGroupRealSides()</c> (<c>fsship.cpp:629</c>) — all
    /// real sides, the same audience as <see cref="BroadcastStationDestroyed"/>'s
    /// STATION_DESTROYED and P102's ASTEROID_DESTROYED. Do not &quot;fix&quot; it
    /// to same-side.</para>
    /// <para><b>No vacate.</b> Unlike a destroyed base
    /// (<see cref="BroadcastStationDestroyed"/>), capture does NOT eject ships
    /// already docked at the station — confirmed reading the native: neither
    /// <c>CFSShip::CaptureStation</c> nor its callers call <c>VacateStation</c> on
    /// capture (unlike the kill path, <c>FedSrv.CPP:12237-12279</c>); docked ships
    /// simply remain docked, now at a re-sided base. The score/Steam bookkeeping
    /// in the native (<c>CaptureBase</c>, <c>AwardBaseKillOrCapture</c>,
    /// <c>AddBaseCapture</c>, <c>fsship.cpp:600-617</c>) is confirmed N/A — no
    /// score or Steam system, and <see cref="Model.Side"/> tracks no base-capture
    /// counter, anywhere in this port. Runs on the sim thread (same one that owns
    /// <c>_peerShips</c>), so no extra locking.</para>
    /// </summary>
    private void BroadcastStationCapture(Ship ship, Station station)
    {
        var msg = new StationCapture
        {
            StationId = station.StationId,
            SidOld = station.SideId,             // the OLD owner (seam raised pre-reside)
            SidNew = ship.SideId!.Value,         // the capturing side (ship is sided to dock)
            ShipIdCredit = ship.Id,              // the capturing ship (fsship.cpp:628)
        };

        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        var targets = _peerShips.Select(kv => kv.Key).ToArray();

        var note = $"[station] station {station.StationId} captured: side {msg.SidOld} -> {msg.SidNew} " +
                   $"by ship {msg.ShipIdCredit} => STATION_CAPTURE(58) -> {targets.Length} peer(s) [WHOLE MISSION]";
        Console.WriteLine(note);
        Notable?.Invoke(note);                   // spectator event feed (same text)

        foreach (var t in targets)
            SendPeer(t, packet);
    }

    /// <summary>
    /// P191 — the server half of the conquest/territory win conditions, driven
    /// by the <see cref="ServerHost.VictoryByStationsComputed"/> seam the mission
    /// raises after every station capture/kill recomputes
    /// (<see cref="Mission.RecomputeConquestAndTerritory"/>). Two responsibilities,
    /// matching native <c>CheckForVictoryByStationKill</c>'s tail
    /// (<c>fsmission.cpp:3685-3697</c> then the <c>GameOver</c> at the capture /
    /// kill call sites, <c>fsship.cpp:688-691</c> / <c>FedSrv.CPP:12345</c>):
    /// <list type="number">
    /// <item><b>GAME_STATE(155) whole-mission broadcast</b>, sent once per recompute
    /// and ONLY when at least one side's <c>ConquestPercent</c>/<c>TerritoryCount</c>
    /// actually changed (the native <c>bChange</c> gate, <c>fsmission.cpp:3685</c>),
    /// scoped to THIS mission's peers. <c>nFlags</c>/<c>nArtifacts</c> are always
    /// all-zero (native never sets them — see the <see cref="Wire.GameState"/> codec
    /// remarks).</item>
    /// <item><b>Win declaration via the P178 path</b>: when the recompute produced
    /// a winner, <see cref="DeclareGameOver"/> flips the mission Over and broadcasts
    /// GAME_OVER(62) — the single shared game-over mechanism, never a second one.
    /// Guarded against an already-Over mission (defensive; the recompute's own
    /// stage gate already prevents a post-win recompute).</item>
    /// </list>
    /// Runs on the sim thread (same one that owns <c>_peerShips</c>), so no extra
    /// locking.
    /// </summary>
    private void OnVictoryByStationsComputed(Mission mission, bool changed, Side? winner)
    {
        if (changed)
        {
            var msg = GameState.ForMission(mission);
            byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

            // Whole-mission audience, scoped to THIS mission.
            var targets = _peerShips
                .Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission))
                .Select(kv => kv.Key)
                .ToArray();

            var note = $"[gamestate] mission #{_missions.IndexOf(mission)} -> GAME_STATE(155) " +
                       $"conquest[{string.Join(",", msg.Conquest)}] territory[{string.Join(",", msg.Territory)}] " +
                       $"(nFlags/nArtifacts all 0) -> {targets.Length} peer(s) [WHOLE MISSION]";
            Console.WriteLine(note);
            Notable?.Invoke(note);

            foreach (var t in targets)
                SendPeer(t, packet);
        }

        if (winner is not null && mission.Stage != MissionStage.Over)
        {
            string winnerName = winner.Name ?? $"side{winner.Id}";
            DeclareGameOver(mission, winner.Id,
                $"{winnerName} won by achieving the conquest/territory victory objective.");
        }
    }

    /// <summary>
    /// P133 — the periodic per-side <c>STATIONS_UPDATE</c>(57) broadcast, port of
    /// the native per-cluster-per-side batch in <c>fsship.cpp:820-845</c>+. For
    /// each side that currently has a seated peer it gathers every station in the
    /// mission that side <b>currently sees</b> — reading the <em>live</em>
    /// <see cref="Station.SeenBySide"/> flags P132's per-tick sweep maintains
    /// (never re-deriving visibility independently) — batches them into one
    /// <see cref="Wire.StationsUpdate"/> and sends it to that side's connections
    /// only, gated on <c>if (nStations != 0)</c> exactly as the native snippet.
    /// <list type="bullet">
    /// <item><b>Per-side, not whole-mission.</b> Each side receives only the
    /// stations it can currently see (contrast the whole-mission audiences of
    /// <c>ASTEROID_MINED</c>(198)/<c>STATION_DESTROYED</c>(59)/<c>STATION_CAPTURE</c>(58)).
    /// The send target is <c>pside</c>'s own connection group — here, the seated
    /// peers whose <c>SideId</c> matches (the same side-wide audience
    /// <see cref="BroadcastMoneyChangeSideWide"/> selects, Server.cs:3028-3039).</item>
    /// <item><b>Cadence.</b> Runs every <see cref="SimPass"/> (every tick), the
    /// port's established per-tick-pass convention (P132 §3 rejected inventing a
    /// new update-frequency mechanism; the native every-N delta is a throttle, not
    /// a semantic gate). Because it runs after <c>Mission.Update</c>, each tick's
    /// broadcast reflects the just-computed visibility sweep and the latest
    /// <see cref="Station.ArmorFraction"/>/<see cref="Station.ShieldFraction"/>
    /// (P125) — a station's fractions changing this tick are carried in the next
    /// tick's message. It is periodic per native, <b>not</b> a one-time join-sync
    /// like the <c>CREATE_BUCKETS</c>/<c>SET_MONEY</c>/<c>SET_BRIEFING_TEXT</c> trio.</item>
    /// <item><b>Batching scope.</b> Aggregates across all missions/sectors into
    /// one per-side message. <c>STATIONS_UPDATE</c> carries no clusterID and the
    /// client indexes stations by id, so one combined per-side list is correct
    /// (native's per-cluster split is an iteration artifact, not a wire need).</item>
    /// </list>
    /// Runs on the sim thread (same one that owns <c>_peerShips</c> and the
    /// visibility sweep), so no extra locking.
    /// </summary>
    private void BroadcastStationsUpdate()
    {
        // Side universe = the sides with seated peers (the only sides that have
        // anyone to send to). Group the peer connections by their ship's side in
        // one pass over a stable snapshot.
        var peers = _peerShips.ToArray();
        var connsBySide = new Dictionary<short, List<ConnectionId>>();
        foreach (var kv in peers)
        {
            if (kv.Value.Ship.SideId is { } sid)
            {
                if (!connsBySide.TryGetValue(sid, out var list))
                    connsBySide[sid] = list = [];
                list.Add(kv.Key);
            }
        }
        if (connsBySide.Count == 0) return;

        // All runtime stations in the mission, once — read off the live sweep's
        // subject collection (Sector.StationRuntimes), never re-derived.
        var allStations = _missions
            .SelectMany(m => m.Sectors)
            .SelectMany(s => s.StationRuntimes)
            .ToArray();

        foreach (var (sideId, conns) in connsBySide)
        {
            // Only stations the side currently sees (P132's live SeenBySide).
            var visible = allStations.Where(st => st.SeenBySide(sideId)).ToArray();
            if (visible.Length == 0) continue;              // native: `if (nStations != 0)`
            var msg = StationsUpdate.FromStations(visible);
            byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
            foreach (var t in conns) SendPeer(t, packet);

            Console.WriteLine($"[stations] side {sideId} sees {visible.Length} station(s) " +
                              $"=> STATIONS_UPDATE(57) -> {conns.Count} peer(s) [PER SIDE]");
        }
    }

    /// <summary>
    /// P134 — the periodic per-side ASTEROIDS_UPDATE (179) broadcast, port of
    /// the native per-cluster-per-side batch in fsship.cpp:883-915 — the direct
    /// sibling of the STATIONS_UPDATE batch (fsship.cpp:817-849, P133). For each
    /// side that currently has a seated peer it gathers every asteroid in the
    /// mission that side currently sees — reading the live
    /// <see cref="Asteroid.SeenBySide"/> flags P132's per-tick sweep maintains
    /// (extended in P134 so asteroids are scored as visibility targets, exactly
    /// the c_mtSeenBySide role native gives a rock at asteroidigc.cpp:36; never
    /// re-derived here) — batches them into one
    /// <see cref="Wire.AsteroidsUpdate"/> and sends it to that side's
    /// connections only, gated on if (nAsteroids != 0) exactly as the native
    /// snippet.
    /// <list type="bullet">
    /// <item><b>Per-side, not whole-mission.</b> Each side receives only the
    /// asteroids it can currently see (contrast the whole-mission audiences of
    /// ASTEROID_MINED(198)/ASTEROID_DESTROYED(86)). The send target is pside's
    /// own connection group — the seated peers whose SideId matches, the same
    /// selection BroadcastStationsUpdate and BroadcastMoneyChangeSideWide use.</item>
    /// <item><b>Cadence.</b> Runs every SimPass (every tick), the port's
    /// established per-tick-pass convention, mirroring BroadcastStationsUpdate
    /// and the P132 §3 finding (the native every-10 throttle is not imported).
    /// Because it runs after Mission.Update, each tick's broadcast reflects the
    /// just-computed visibility sweep and the latest Asteroid.OreRemaining /
    /// Asteroid.Fraction (P102/P55) — a rock's ore changing this tick (real
    /// mining) is carried in the next tick's message. It is periodic per native,
    /// not a one-time join-sync.</item>
    /// <item><b>Batching scope.</b> Aggregates across all missions/sectors into
    /// one per-side message. ASTEROIDS_UPDATE carries no clusterID and the
    /// client indexes asteroids by id, so one combined per-side list is correct
    /// (native's per-cluster split is an iteration artifact, not a wire need) —
    /// the identical rationale P133 recorded for STATIONS_UPDATE.</item>
    /// </list>
    /// Runs on the sim thread (same one that owns _peerShips and the visibility
    /// sweep), so no extra locking.
    /// </summary>
    private void BroadcastAsteroidsUpdate()
    {
        // Side universe = the sides with seated peers (the only sides that have
        // anyone to send to). Group the peer connections by their ship's side in
        // one pass over a stable snapshot.
        var peers = _peerShips.ToArray();
        var connsBySide = new Dictionary<short, List<ConnectionId>>();
        foreach (var kv in peers)
        {
            if (kv.Value.Ship.SideId is { } sid)
            {
                if (!connsBySide.TryGetValue(sid, out var list))
                    connsBySide[sid] = list = [];
                list.Add(kv.Key);
            }
        }
        if (connsBySide.Count == 0) return;

        // All runtime asteroids in the mission, once — read off the live sweep's
        // subject collection (Sector.Mineable), never re-derived.
        var allAsteroids = _missions
            .SelectMany(m => m.Sectors)
            .SelectMany(s => s.Mineable)
            .ToArray();

        foreach (var (sideId, conns) in connsBySide)
        {
            // Only asteroids the side currently sees (P132's live SeenBySide,
            // now scoring asteroids as targets — P134).
            var visible = allAsteroids.Where(a => a.SeenBySide(sideId)).ToArray();
            if (visible.Length == 0) continue;              // native: if (nAsteroids != 0)
            var msg = AsteroidsUpdate.FromAsteroids(visible);
            byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
            foreach (var t in conns) SendPeer(t, packet);

            Console.WriteLine($"[asteroids] side {sideId} sees {visible.Length} asteroid(s) " +
                              $"=> ASTEROIDS_UPDATE(179) -> {conns.Count} peer(s) [PER SIDE]");
        }
    }

    /// <summary>
    /// P135 — the periodic per-side PROBES_UPDATE (92) broadcast, port of the
    /// native per-cluster-per-side batch in fsship.cpp:851-881 — the direct
    /// sibling of the STATIONS_UPDATE batch (fsship.cpp:817-849, P133) and the
    /// ASTEROIDS_UPDATE batch (fsship.cpp:883-915, P134), and the simplest of
    /// the three (a ProbeState is just id + health fraction, Messages.h:635-642).
    /// For each side that currently has a seated peer it gathers every probe in
    /// the mission that side currently sees — reading the live
    /// <see cref="Probe.SeenBySide"/> flags P132's per-tick sweep maintains
    /// (<b>with no P134-style extension needed</b>: probes are real Models that
    /// carry a <c>SideId</c>, so the sweep's own probe case scores them as both
    /// targets and scanners for their own side, Sector.cs:2133-2136 — never
    /// re-derived here) — batches them into one <see cref="Wire.ProbesUpdate"/>
    /// and sends it to that side's connections only, gated on if (nProbes != 0)
    /// exactly as the native snippet.
    /// <list type="bullet">
    /// <item><b>Per-side, not whole-mission.</b> Each side receives only the
    /// probes it can currently see (contrast the whole-mission audiences of
    /// PROBE_DESTROYED(85)/EXPORT(26)). The send target is pside's own
    /// connection group — the seated peers whose SideId matches, the same
    /// selection BroadcastStationsUpdate and BroadcastAsteroidsUpdate use.</item>
    /// <item><b>Cadence.</b> Runs every SimPass (every tick), the port's
    /// established per-tick-pass convention, mirroring its two siblings (the
    /// native every-10 throttle is not imported). Because it runs after
    /// Mission.Update, each tick's broadcast reflects the just-computed
    /// visibility sweep and the latest Probe.Fraction (P100) — a probe's health
    /// changing this tick (real combat damage) is carried in the next tick's
    /// message.</item>
    /// <item><b>Batching scope.</b> Aggregates across all missions/sectors into
    /// one per-side message. PROBES_UPDATE carries no clusterID and the client
    /// indexes probes by id, so one combined per-side list is correct (native's
    /// per-cluster split is an iteration artifact, not a wire need) — the
    /// identical rationale P133/P134 recorded for its siblings.</item>
    /// </list>
    /// Runs on the sim thread (same one that owns _peerShips and the visibility
    /// sweep), so no extra locking.
    /// </summary>
    private void BroadcastProbesUpdate()
    {
        // Side universe = the sides with seated peers (the only sides that have
        // anyone to send to). Group the peer connections by their ship's side in
        // one pass over a stable snapshot.
        var peers = _peerShips.ToArray();
        var connsBySide = new Dictionary<short, List<ConnectionId>>();
        foreach (var kv in peers)
        {
            if (kv.Value.Ship.SideId is { } sid)
            {
                if (!connsBySide.TryGetValue(sid, out var list))
                    connsBySide[sid] = list = [];
                list.Add(kv.Key);
            }
        }
        if (connsBySide.Count == 0) return;

        // All runtime probes in the mission, once — read off the live sweep's
        // subject collection (Sector.Models, where the Probe case lives), never
        // re-derived.
        var allProbes = _missions
            .SelectMany(m => m.Sectors)
            .SelectMany(s => s.Models)
            .OfType<Probe>()
            .ToArray();

        foreach (var (sideId, conns) in connsBySide)
        {
            // Only probes the side currently sees (P132's live SeenBySide,
            // already scoring probes as targets — they are Models with a SideId).
            var visible = allProbes.Where(p => p.SeenBySide(sideId)).ToArray();
            if (visible.Length == 0) continue;              // native: if (nProbes != 0)
            var msg = ProbesUpdate.FromProbes(visible);
            byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
            foreach (var t in conns) SendPeer(t, packet);

            Console.WriteLine($"[probes] side {sideId} sees {visible.Length} probe(s) " +
                              $"=> PROBES_UPDATE(92) -> {conns.Count} peer(s) [PER SIDE]");
        }
    }

    /// <summary>
    /// P139 — the periodic <b>per-player</b> LIGHT/HEAVY_SHIPS_UPDATE (175/176)
    /// broadcast, the consumer of P138's per-cluster candidate list. Port of the
    /// native per-player assembly in <c>SendShipUpdates</c>
    /// (<c>FedSrv.CPP:4029-4207</c>). For each connected, seated peer:
    /// <list type="number">
    /// <item><b>Resolve the peer's real command target.</b>
    /// <see cref="Ship.CommandTarget"/> (the port's
    /// <c>m_commandTargets[c_cmdCurrent]</c>, <c>shipIGC.h:2624</c>) — the same
    /// primitive the weapon-aim slice already reads, reused here rather than a
    /// new targeting system. If the target is a ship in the peer's own cluster
    /// (non-self, non-boarded) it fills the <b>first heavy slot</b> and drives
    /// the <c>bpTargetHull</c>/<c>bpTargetShield</c> scalars
    /// (<c>FedSrv.CPP:4075-4124</c>); a probe target sets <c>bpTargetHull</c>
    /// only, no heavy slot.</item>
    /// <item><b>Assemble the heavy list.</b> Every candidate from
    /// <see cref="Sector.GetShipUpdateCandidates"/> (P138 — cluster-membership,
    /// any side), minus the peer's own ship (its state already rides
    /// <c>SingleShipUpdate</c>/<c>ServerShipUpdate</c>, so it must not
    /// double-send here; native skips <c>pfsShip != pfsPlayer</c>,
    /// <c>FedSrv.CPP:4151</c>) and minus the promoted target, quantized as heavy
    /// records. <b>Every candidate goes HEAVY; the LIGHT tier is not
    /// populated</b> — the heavy/light split is the output of the deviation-sorted
    /// bandwidth budget P138 deferred to P141, so without that sort there is no
    /// principled basis to choose light vs heavy (see the split decision in
    /// <c>SHIP_UPDATES_BROADCAST_P139.md</c> §2).</item>
    /// <item><b>Scalar region.</b> <c>timeReference</c> = the tick index
    /// (<c>g.timeNow</c>, FedSrv.CPP:3996); <c>positionReference</c> = the peer's
    /// own ship position (FedSrv.CPP:4128-4133 — the peer is in this cluster by
    /// construction); <c>bpTargetHull</c>/<c>bpTargetShield</c> per the
    /// target-type switch; the six <c>fractions</c> bytes are the <b>recipient's
    /// own</b> fractions (FedSrv.CPP:4044-4047, <c>ExportFractions</c>).</item>
    /// <item><b>Send to the peer's OWN connection only</b> via
    /// <see cref="SendPeer"/> — per-player, not per-side (this family's
    /// send target is each individual connection, unlike STATIONS/ASTEROIDS/
    /// PROBES which are per-side batches). HEAVY is sent every tick to every
    /// seated peer (native: "Send the heavy ship update whenever the player is
    /// flying since it has hitpoint information", FedSrv.CPP:4179-4180). LIGHT
    /// is <b>not</b> emitted: native forwards it only when non-empty
    /// (FedSrv.CPP:4183-4188), and with the every-heavy decision it is always
    /// empty. The active-turret relay blob (P137) is left empty — the port
    /// stores no per-ship <c>ClientActiveTurretUpdate</c> to relay (deferred,
    /// see §3 of the MD).</item>
    /// </list>
    /// Runs on the sim thread (same one that owns <c>_peerShips</c> and the
    /// visibility sweep), after <c>Mission.Update</c>, so every tick's message
    /// reflects the just-computed candidate list — same cadence as the
    /// STATIONS/ASTEROIDS/PROBES siblings.
    /// </summary>
    private void BroadcastShipsUpdate(uint tickIndex)
    {
        var peers = _peerShips.ToArray();
        foreach (var kv in peers)
        {
            var self = kv.Value.Ship;
            var sector = kv.Value.Sector;

            // 1. Resolve the peer's command target within its own cluster
            //    (native pmodelTarget->GetCommandTarget(c_cmdCurrent) then the
            //    OT_-switch, FedSrv.CPP:4081-4124).
            Ship? targetShip = null;
            byte bpTargetHull = 255;    // 255 = "no target" (FedSrv.CPP:4079, appmsg.cpp:742)
            byte bpTargetShield = 255;
            var target = self.CommandTarget;
            if (target is not null && TargetInCluster(target, sector))
            {
                switch (target)
                {
                    case Ship ts:
                        // OT_ship, FedSrv.CPP:4086-4104: only a non-self,
                        // non-boarded source ship occupies the first heavy slot.
                        // The pfsTarget->GetHasUpdate() gate is skipped — P138
                        // drops the recent-update timestamp gate, so every
                        // candidate counts as having an update.
                        if (ts != self && ts.ParentShip is null)
                        {
                            bpTargetHull = Percent(ts.Fraction);
                            bpTargetShield = Percent(ts.ShieldFraction);
                            targetShip = ts;
                        }
                        break;
                    case Probe probe:
                        // OT_probe (also OT_asteroid/OT_missile shape,
                        // FedSrv.CPP:4117-4122): single fraction for hull, no
                        // shield, no heavy slot. OT_station/OT_asteroid are
                        // structurally unreachable here — CommandTarget is
                        // Model? and Station/Asteroid are not Model subclasses
                        // (see §2 of the MD); a Missile target has no damage
                        // fraction concept in this port.
                        bpTargetHull = Percent(probe.Fraction);
                        break;
                }
            }

            // 3. Scalar region: positionReference = own ship position (peer is
            //    in this cluster by construction); fractions = own fractions
            //    (FedSrv.CPP:4044-4047, 4128-4133).
            Vector3 posRef = self.Position;

            // 2. Assemble heavy entries: promoted target first, then candidates
            //    minus self minus target (FedSrv.CPP:4141-4170).
            var entries = new List<HeavyShipUpdateEntry>();
            if (targetShip is not null)
                entries.Add(HeavyShipUpdateEntry.From(tickIndex, posRef, targetShip, tickIndex));
            foreach (var c in sector.GetShipUpdateCandidates())
            {
                if (ReferenceEquals(c, self)) continue;
                if (targetShip is not null && ReferenceEquals(c, targetShip)) continue;
                entries.Add(HeavyShipUpdateEntry.From(tickIndex, posRef, c, tickIndex));
            }

            var heavy = new HeavyShipsUpdate
            {
                TimeReference = tickIndex,
                PositionReference = posRef,
                HeavyShipEntries = entries.ToArray(),
                ActiveTurretUpdates = [],              // turret relay deferred (see §3)
                BpTargetHull = bpTargetHull,
                BpTargetShield = bpTargetShield,
                FractionsHull = Percent(self.Fraction),
                FractionsShield = Percent(self.ShieldFraction),
                FractionsAmmo = 240,
                FractionsFuel = 240,
                FractionsEnergy = 240,
                FractionsOre = Percent(Ship.MineCapacity <= 0f ? 0f : self.Ore / Ship.MineCapacity),
            };

            // 4. Per-player send of HEAVY only (LIGHT is never populated/sent).
            SendPeer(kv.Key, FedPacket.Pack(new[] { heavy.Encode() }));
            Console.WriteLine($"[ships] peer {kv.Key} side {self.SideId?.ToString() ?? "-"} " +
                              $"target={(targetShip?.Id.ToString() ?? "none")} " +
                              $"=> HEAVY_SHIPS_UPDATE(176) x{entries.Count} [PER PLAYER]");
        }
    }

    /// <summary>
    /// Whether a command target <paramref name="target"/> resides in
    /// <paramref name="sector"/> — the port of native
    /// <c>pmodelTarget->GetCluster() == pcluster</c> (FedSrv.CPP:4082). Because
    /// this port's <see cref="Ship.CommandTarget"/> is a <c>Model?</c>, the only
    /// reachable target types are <see cref="Model"/> subclasses; their sector
    /// back-refs are read where the model tracks one (<see cref="Ship.Sector"/>,
    /// <see cref="Probe.OwnerSector"/>, <see cref="Missile.OwnerSector"/>), and
    /// anything else falls back to membership in the sector's model list.
    /// </summary>
    private static bool TargetInCluster(Model? target, Sector sector)
    {
        if (target is null) return false;
        switch (target)
        {
            case Ship ship: return ship.Sector == sector;
            case Probe probe: return probe.OwnerSector == sector;
            case Missile missile: return missile.OwnerSector == sector;
            default: return sector.Models.Contains(target);
        }
    }

    /// <summary>Fraction 0..1 → div-240 byte, matching native
    /// <c>BytePercentage</c> (Utility.h:1307, 1342-1358) exactly — the same
    /// conversion the port's codecs bake in (<c>CompactQuant.Percent</c>,
    /// inaccessible from this assembly). 255 is the reserved "no target"
    /// sentinel and is never produced here (callers pass the raw 255 when
    /// unpresent).</summary>
    private static byte Percent(float f)
    {
        double v = (double)f * 240.0 + 0.5;
        if (v < 0.0) return 0;
        if (v >= 241.0) return 240;
        return (byte)v;
    }

    /// <summary>
    /// Handle a simulated ship death (<see cref="ServerHost.KillShipEvent"/>, raised
    /// from <c>Ship.ReceiveDamage</c> on the sim thread) by broadcasting a real
    /// <c>FM_S_KILL_SHIP(105)</c> <see cref="KillShip"/> message to the peers that
    /// can see the victim. This is the live half of the (unit-tested) combat slice:
    /// the projectiles and collision pass already kill the ship in simulation; this
    /// tells every connected peer whose ship shares the victim's sector that it
    /// happened. Runs on the sim thread (the site raises it inside the mission
    /// tick, same thread that owns <see cref="_peerShips"/> and broadcasts ship
    /// states), so no extra locking is needed.
    /// </summary>
    private void BroadcastKillShip(
        double now, IShipIgc ship, IModelIgc? launcher, float amount, Vector3 p1, Vector3 p2)
    {
        // Resolve the victim and (if it is a ship) the credited attacker to ids.
        if (ship is not Ship victim)
        {
            Console.WriteLine("[combat] KillShipEvent with a non-Ship victim — ignored");
            return;
        }
        short killerId = -1;                       // NA
        IgcObjectType killerType = IgcObjectType.Ship;
        if (launcher is Ship attacker) killerId = attacker.Id;

        // ── P107: the bKilled gate (FedSrv.CPP:11770-11785) ──
        // First, the owning peer — the port's IsPlayer() proxy (native
        // FedSrv.CPP:11772 pfsShip->IsPlayer()). Hoisted above the kill
        // broadcasts where P105 originally resolved it, because the eject
        // gate needs it. Non-player (bot) ships have no owning peer.
        ConnectionId? owner = null;
        foreach (var kv in _peerShips)
            if (ReferenceEquals(kv.Value.Ship, victim)) { owner = kv.Key; break; }

        // bLifepod = (pshipParent == NULL) && baseHull.HasCapability(c_habmLifepod)
        //   → port simplifications: no boarded/child ships yet (cluster E), so the
        //     (pshipParent==NULL) conjunct drops; the "already a lifepod" half is
        //     a real capability test on the CURRENT base hull (bit c_habmLifepod =
        //     0x04, igc.h:728; helper DataHullTypeIGC.HasCapability).
        // bKilled = (!bEjectPods) || bLifepod || (amount < 0.0f)
        //   → bEjectPods is always enabled (MissionParams not decoded in this port —
        //     stated plainly in IN_SPACE_EJECT_P107.md); no caller passes a negative
        //     amount into KillShipEvent (combat amounts are > 0, suicide = 1.0; the
        //     negative-amount repair path in Ship.ReceiveDamage never reaches the
        //     death event — it repairs and returns c_drNoDamage).
        //   → a non-player (bot) ship is always bKilled (native else branch,
        //     FedSrv.CPP:11781-11785).
        bool bLifepod = victim.HullType.HasCapability(DataHullTypeIGC.HabmLifepod);
        bool bKilled = (owner is null) || bLifepod || amount < 0.0f;

        if (!bKilled && EjectSurvivor(victim, launcher, p1, p2))
        {
            // Genuinely different code path from P103/P105: the ship survives
            // in space as a drifting Lifepod hull. No KILL_SHIP(105), no
            // SHIP_DELETE(20), no respawn/ENTER_LIFEPOD(166) — and the ship is
            // NOT removed from the sector (no SetCluster(NULL)).
            return;
        }

        // Peers whose ship is in the same sector as the victim can see the kill.
        var targets = _peerShips
            .Where(kv => kv.Value.Ship.Sector == victim.Sector)
            .Select(kv => kv.Key)
            .ToArray();

        Console.WriteLine($"[combat] ship {victim.Id} killed by {(killerId < 0 ? "(none)" : $"ship {killerId}")} " +
                          $"at sim-t {now:R} — KILL_SHIP(105) -> {targets.Length} peer(s)");
        if (targets.Length == 0) return;

        var msg = KillShip.FromVictim(victim.Id, killerType, killerId);
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        foreach (var t in targets)
            SendPeer(t, packet);

        // P103 belt-and-suspenders: alongside the KILL_SHIP(105) death notice,
        // emit the native's separate FM_S_SHIP_DELETE(20) for the same death.
        // The native client uses SHIP_DELETE to actually remove the client-side
        // object (appmsg.cpp:470-476: SDR_KILLED → SetCluster(NULL)) and
        // KILL_SHIP to play the death effect — both are needed, SHIP_DELETE is
        // genuinely redundant only by KILL_SHIP's own message being separate.
        // This slice emits only SDR_KILLED(1) — the one reason the death path
        // can drive without a visibility-culling system (see
        // SUICIDE_SHIP_DELETE_P103.md). Audience is identical to the KILL_SHIP
        // above: the same `targets` peers in the victim's sector.
        var shipDelete = ShipDelete.ForKilledShip(victim.Id);
        byte[] deletePacket = FedPacket.Pack(new[] { shipDelete.Encode() });
        foreach (var t in targets)
            SendPeer(t, deletePacket);
        Console.WriteLine($"[combat] ship {victim.Id} — SHIP_DELETE(20) SDR_KILLED -> {targets.Length} peer(s)");

        // P105 death-respawn, hooked at the exact seam P103 wired (ShipKilled →
        // this method) and sharing the same trigger. The native function
        // (FedSrv.CPP:12026-12074) turns a player's true death into a docked
        // Lifepod at a friendly station plus a private ENTER_LIFEPOD(166) to
        // that player only; bots just stay dead (native else:
        // FedSrv.CPP:12080-12085). Three guards reproduce it faithfully:
        //  (1) owner — only a peer-owned ship is a player (this port's player/
        //      bot distinction IS peer ownership; a ship with no owning peer in
        //      _peerShips is a bot/unowned ship and is NOT respawned);
        //  (2) a friendly station anywhere (FindFriendlyStation — the simplified
        //      stand-in for native's nearest-search + GetBase fallback);
        //  (3) implicit — FindFriendlyStation returns null when SideId is null,
        //      so a side-less ship is never respawned either.
        // No friendly station ⇒ no respawn, exactly like native's
        // `if (pstationDest)` guard (FedSrv.CPP:12055): the ship simply stays
        // gone, whatever SHIP_DELETE already said. (`owner` was resolved above,
        // before the P107 eject gate; only the guard remains here.)
        if (owner is null)
        {
            Console.WriteLine($"[lifepod] ship {victim.Id} has no owning peer (bot/non-player) — no respawn, stays dead");
            return;
        }
        DataStationIGC? station = FindFriendlyStation(victim);
        if (station is null)
        {
            Console.WriteLine($"[lifepod] player ship {victim.Id} side {(victim.SideId?.ToString() ?? "(none)")} has no friendly station anywhere — no respawn (native FedSrv.CPP:12055 guard)");
            return;
        }
        var dest = station.Value;                             // non-null capture past the guard
        var mission = victim.Mission!;
        // A valid station's ClusterId always names a sector of this mission
        // (DataStationIGC decoding guarantees it) — the station's own cluster
        // is where the Lifepod docks (native SetCluster(pcluster)).
        var destSector = mission.Sectors.First(s => s.Id == dest.ClusterId);
        var side = mission.Sides.FirstOrDefault(s => s.Id == victim.SideId!.Value);
        if (side is null)
        {
            Console.WriteLine($"[lifepod] player ship {victim.Id} side {victim.SideId} not in mission sides — no respawn");
            return;
        }
        if (!mission.Core.HullTypes.TryGetValue(side.Civilization.Lifepod, out var lp))
        {
            Console.WriteLine($"[lifepod] player ship {victim.Id} side {victim.SideId}: lifepod hull {side.Civilization.Lifepod} not in core — no respawn");
            return;
        }

        victim.Sector = destSector;      // SetCluster: out of the old sector, into the station's cluster (FedSrv.CPP:12045→12067)
        victim.ChangeHull(lp);           // SetBaseHullType(pht) — side's Lifepod hull (FedSrv.CPP:12052)
        victim.StationId = dest.StationId; // SetStation + ShipStatusDocked (FedSrv.CPP:12064-12065)

        // Keep the owning peer's broadcast bookkeeping coherent: the status
        // frames (BroadcastShipStates / MakeOwnShipStatus) derive Sector from
        // the LiveShip snapshot, so point it at the station's cluster too —
        // the next SHIP_STATUS(90) shows the Lifepod docked there.
        var conn = owner.Value;                               // non-null capture past the guard
        _peerShips[conn] = _peerShips[conn] with { Sector = destSector };

        var enter = EnterLifepod.ForPlayer(victim.Id);
        SendPeer(conn, FedPacket.Pack(new[] { enter.Encode() }));   // FM_S_ENTER_LIFEPOD(166) → own peer only
        Console.WriteLine($"[lifepod] player ship {victim.Id} -> docked Lifepod (hull {lp.HullId}) at station {dest.StationId} '{dest.Name}' " +
                          $"in sector '{destSector?.Name ?? "?"}' ; ENTER_LIFEPOD(166) -> own peer only");
    }

    /// <summary>
    /// P105's deliberately simplified stand-in for the native
    /// <c>FindTarget(..., c_ttFriendly | c_ttStation | c_ttNearest |
    /// c_ttAnyCluster, ..., c_sabmRestart)</c> nearest-friendly-station search
    /// (<c>FedSrv.CPP:12034-12038</c>). Native picks the spatially-nearest
    /// friendly station in any cluster, falling back to the mission base
    /// (<c>GetBase(pside)</c>); this port has <b>neither</b> a spatial
    /// nearest-search nor a "mission base" concept (confirmed — grep finds
    /// neither in <c>Sector.cs</c>/<c>Mission.cs</c>), so it returns <b>any</b>
    /// station whose <see cref="DataStationIGC.SideId"/> equals the ship's
    /// <see cref="Ship.SideId"/>, first-found in the mission's deterministic
    /// sector-then-station order. Stated plainly (not a silent drop): "best"
    /// station is out of scope — any one friendly match is fine, per the task.
    /// Returns <c>null</c> when no friendly station exists anywhere, mirroring
    /// native's no-<c>pstationDest</c> path (no respawn). Runs on the sim
    /// thread (called from <see cref="BroadcastKillShip"/>).
    /// </summary>
    private static DataStationIGC? FindFriendlyStation(Ship ship)
    {
        if (ship.SideId is not { } sideId || ship.Mission is not { } mission)
            return null;
        foreach (var sector in mission.Sectors)
            foreach (var station in sector.Stations)
                if (station.SideId == sideId)
                    return station;
        return null;
    }

    /// <summary>
    /// P107 — the in-space eject: the <c>!bKilled</c> branch of the native
    /// <c>KillShipEvent</c> (<c>FedSrv.CPP:11918-12022</c>). The dying player's
    /// ship survives, weakened, as a drifting Lifepod hull <b>still in the
    /// same sector</b> — not removed, not killed, no respawn. The caller
    /// (<see cref="BroadcastKillShip"/>) has already decided <c>!bKilled</c>
    /// via the <c>FedSrv.CPP:11770-11785</c> gate; this method only guards the
    /// data it needs (side/civilization/lifepod hull resolvable) and performs
    /// the port-relevant slice of the eject mechanics:
    /// <list type="bullet">
    /// <item>hull swap to the side's civilization's Lifepod hull
    /// (<c>pship-&gt;SetBaseHullType(pht)</c>, <c>FedSrv.CPP:11942</c>);</item>
    /// <item>reposition/reorient/velocity per the native math — an outward
    /// burst away from the killer's position when available, a random
    /// direction otherwise, position staying at the impact point
    /// <c>p1</c> (the parent-branch offset, FedSrv.CPP:11947-11953, is
    /// skipped — no boarded ships);</item>
    /// <item>an outward velocity and a <c>pi·2</c> roll spin
    /// (<c>FedSrv.CPP:12003,12008</c>);</item>
    /// <item>a real <c>FM_S_EJECT(77)</c> broadcast to <b>everyone flying in
    /// the same sector</b> — <c>GetGroupSectorFlying(pcluster)</c>
    /// (<c>FedSrv.CPP:12011-12021</c>), a sector-scoped, <b>cross-side</b>
    /// audience (distinct from <c>_teammateExportSent</c>'s same-side gate and
    /// from P101/P102's mission-wide <c>WARP_BOMB</c>/<c>ASTEROID_DESTROYED</c>
    /// broadcasts — this one is only to the ships in this one sector).</item>
    /// </list>
    /// The ship is deliberately <b>not</b> removed from the sector and gets no
    /// <c>SHIP_DELETE</c>/<c>KILL_SHIP</c>/<c>ENTER_LIFEPOD</c> — this is the
    /// mutually-exclusive sibling of P103/P105's true-death branch. Native also
    /// clears missiles targeting the ship (<c>FedSrv.CPP:11925-11932</c>); this
    /// port's <see cref="Missile"/> carries no target back-reference, so that
    /// step is a structural no-op (noted, not silently dropped). Returns
    /// <c>true</c> when the eject ran and the caller should stop processing
    /// this death as a kill; <c>false</c> when the required data is missing
    /// (should not happen for a live player ship) so the caller falls through
    /// to the kill path. Runs on the sim thread (called from
    /// <see cref="BroadcastKillShip"/>).
    /// </summary>
    private bool EjectSurvivor(Ship victim, IModelIgc? launcher, Vector3 p1, Vector3 p2)
    {
        var pcluster = victim.Sector;
        var mission = victim.Mission;
        if (pcluster is null || mission is null || victim.SideId is not { } sideId)
        {
            Console.WriteLine($"[eject] player ship {victim.Id} lacks sector/mission/side — cannot eject, treating as death");
            return false;
        }
        var side = mission.Sides.FirstOrDefault(s => s.Id == sideId);
        if (side is null)
        {
            Console.WriteLine($"[eject] player ship {victim.Id} side {sideId} not in mission sides — cannot eject, treating as death");
            return false;
        }
        short lifepodId = side.Civilization.Lifepod;
        if (!mission.Core.HullTypes.TryGetValue(lifepodId, out var pht))
        {
            Console.WriteLine($"[eject] player ship {victim.Id} side {sideId}: lifepod hull {lifepodId} not in core — cannot eject, treating as death");
            return false;
        }

        // v = the source ship's velocity (FedSrv.CPP:11935). No boarded ships
        // (cluster E) ⇒ source ship == the victim itself.
        Vector3 v = victim.Velocity;

        // (Missiles targeting this ship would be cleared here — FedSrv.CPP:11925-11932 —
        // but this port's Missile tracks no target; structural no-op.)

        victim.ChangeHull(pht);                 // SetBaseHullType(pht), FedSrv.CPP:11942

        // ── position / orientation / velocity math (FedSrv.CPP:11944-12006) ──
        // Parent branch (FedSrv.CPP:11947-11953) omitted — no boarded ships.
        Vector3 f = RandomDirection();         // Vector::RandomDirection(), vector.cpp:37-49
        bool bLook;
        Vector3 vectorLook;
        if (launcher is not null)
        {
            // plauncher = (source ship of) the credited killer, FedSrv.CPP:11960-11963.
            // No turret chains ⇒ a Ship launcher is its own source ship. IModelIgc
            // is a marker interface, so Position comes from the concrete Model
            // (Ship or another model); a non-Model launcher can't be located.
            if (launcher is Ship attackerShip)
            {
                bool sameSector = attackerShip.Sector == pcluster;   // GetCluster() == pcluster, FedSrv.CPP:11964
                if (sameSector) { bLook = true; vectorLook = attackerShip.Position; }
                else { bLook = false; vectorLook = default; }
            }
            else if (launcher is Model attackerModel)
            {
                bLook = true; vectorLook = attackerModel.Position;
            }
            else { bLook = false; vectorLook = default; }
        }
        else { bLook = true; vectorLook = p2; }  // no launcher → look at p2 (FedSrv.CPP:11974-11978)

        Vector3 orientationForward = f;          // o.Set(f) default (FedSrv.CPP:11995,11998)
        if (bLook)
        {
            Vector3 dp = vectorLook - p1;
            float length2 = dp.LengthSquared();
            if (length2 >= 1.0f)                 // FedSrv.CPP:11985
            {
                orientationForward = dp;         // o.Set(dp), FedSrv.CPP:11987
                // Bend the random forward slightly toward the killer:
                // fNew = f + dp*(8/sqrt(length2)), FedSrv.CPP:11989-11992.
                Vector3 fNew = f + dp * (8.0f / MathF.Sqrt(length2));
                float l2 = fNew.LengthSquared();
                if (l2 > 0.01f) f = fNew / MathF.Sqrt(l2);
            }
        }

        Vector3 position = p1;                   // position = p1 (parentless branch), FedSrv.CPP:12000
        v -= f * 100.0f;                         // outward burst, FedSrv.CPP:12003
        victim.Position = position;              // SetPosition, FedSrv.CPP:12004
        victim.Velocity = v;                     // SetVelocity, FedSrv.CPP:12005
        victim.Orientation.Set(orientationForward); // SetOrientation(o), FedSrv.CPP:12006
        victim.SetTurnRate(ControlData.AxisRoll, 2f * MathF.PI); // SetCurrentTurnRate(c_axisRoll, pi*2), FedSrv.CPP:12008

        // EJECT(77) to everyone flying in this sector — cross-side, sector-scoped.
        var targets = _peerShips
            .Where(kv => kv.Value.Ship.Sector == pcluster)
            .Select(kv => kv.Key)
            .ToArray();
        var eject = Eject.ForSurvivor(victim.Id, position, v, f);
        byte[] packet = FedPacket.Pack(new[] { eject.Encode() });
        foreach (var t in targets) SendPeer(t, packet);
        Console.WriteLine($"[eject] player ship {victim.Id} ejected -> Lifepod hull {pht.HullId} at {position}, vel ({v.X:0.#},{v.Y:0.#},{v.Z:0.#}), fwd ({f.X:0.##},{f.Y:0.##},{f.Z:0.##}); EJECT(77) -> {targets.Length} peer(s) in sector {pcluster.Id}; hull stays in sector (not removed), no SHIP_DELETE/KILL_SHIP");
        return true;
    }

    /// <summary>
    /// <c>Vector::RandomDirection()</c> (<c>vector.cpp:37-49</c>) — a uniformly
    /// distributed unit direction: <c>sinPitch ∈ [-1,1]</c>, <c>yaw ∈ [0, 2π)</c>,
    /// <c>(cos(yaw)·cosPitch, sin(yaw)·cosPitch, sinPitch)</c>. Used by the
    /// eject branch's fallback heading (no killer in the sector).
    /// </summary>
    private static Vector3 RandomDirection()
    {
        float sinPitch = (float)(_rand.NextDouble() * 2.0 - 1.0);
        float cosPitch = MathF.Sqrt(1.0f - sinPitch * sinPitch);
        float yaw = (float)(_rand.NextDouble() * 2.0 * Math.PI);
        return new Vector3(MathF.Cos(yaw) * cosPitch, MathF.Sin(yaw) * cosPitch, sinPitch);
    }

    /// <summary>Relay a notable-moment line from the host's hook to the public
    /// <see cref="Notable"/> event, unchanged. Runs on the sim thread (the host
    /// hook is raised inside the tick), so forwarding to a hub broadcast (e.g.
    /// the vizfight spectator feed) is safe without a lock, exactly like the
    /// kill broadcast's own sim-thread emission.</summary>
    private void OnNotable(string text) => Notable?.Invoke(text);

    /// <summary>
    /// P119 completion-<em>detection</em> handler, wired to
    /// <see cref="ServerHost.BucketCompleted"/> (raised from
    /// <see cref="Bucket.Update"/>'s <c>Complete</c> on the sim thread the
    /// instant a construction bucket is fully funded AND fully time-invested —
    /// the native gate bucketigc.h:51). This is the task's explicit "detect but
    /// don't act yet" boundary: it <b>records</b> the completed bucket id so the
    /// event is observable (logging + a test-visible list) and builds
    /// <b>nothing</b> — no spawned drone, no factory/storage-pit, no further
    /// broadcast. Those completion-effects are the named follow-up task. Runs
    /// on the sim thread, so <c>_bucketCompletions</c> is guarded by
    /// <c>_statsLock</c> for test-thread readers.
    /// </summary>
    private void OnBucketCompleted(IBucketIgc bucketIfc, double now)
    {
        if (bucketIfc is not Bucket bucket)
            return;
        short id = bucket.Id;
        lock (_statsLock) _bucketCompletions.Add(id);

        // P189: a development bucket's completion is the first real consumer of
        // the P119 detection seam — it runs the native DevelopmentCompleted
        // flow (FedSrv.CPP:11259-11276). The owning side is resolved by scanning
        // the world missions' bucket lists (a bucket carries no side back-ref,
        // and completion is a rare event, so the scan is negligible). Any other
        // bucket type stays detection-only (P119 boundary preserved).
        if (bucket.ItemType == BucketItemType.BitDevelopment && bucket.Development is { } dev)
        {
            foreach (var m in _missions)
            {
                var side = m.Sides.FirstOrDefault(s => s.Buckets.Contains(bucket));
                if (side is null) continue;
                CompleteDevelopment(m, side, dev);
                return;
            }
            Console.WriteLine($"[devcomplete] development bucket {id} ('{dev.Name}') completed but no owning side found — no broadcast");
            return;
        }

        Console.WriteLine($"[bucketcomplete] bucket {id} fully funded AND time-invested at {now:0.###}s — completion detected (P119: non-development, nothing produced)");
    }

    /// <summary>
    /// Run the native <c>FedSrvSiteBase::DevelopmentCompleted</c> flow
    /// (<c>FedSrv.CPP:11259-11276</c>) for a completed development bucket on
    /// <paramref name="side"/>, preserving the exact native ordering:
    /// <list type="number">
    /// <item><b>Apply the effect techs</b> (<c>pside-&gt;ApplyDevelopmentTechs</c>,
    /// <c>FedSrv.CPP:11261</c>): only genuinely-new bits (the native
    /// <c>rc = !(ttbm &lt;= ttbmDevelopmentTechs)</c>, <c>sideigc.h:194-210</c>)
    /// are OR-ed into <see cref="Side.DevelopmentTechs"/>, and only then — and
    /// only while the mission is <c>Started</c> — is <c>SIDE_TECH_CHANGE(75)</c>
    /// broadcast to the side's own group (<c>SideDevelopmentTechChange</c>,
    /// <c>FedSrv.CPP:11282-11289</c>).</item>
    /// <item><b>Broadcast <c>DEV_COMPLETED(8)</c></b> to the side's own group
    /// (<c>FedSrv.CPP:11262-11266</c>) — unconditional (no stage gate).</item>
    /// <item><b>Unless tech-only</b>, apply the development's attribute delta
    /// (<c>pside-&gt;ApplyGlobalAttributeSet(pdev-&gt;GetGlobalAttributeSet())</c>,
    /// <c>FedSrv.CPP:11270</c>) — the per-element multiply
    /// <see cref="GlobalAttributeSet.Apply"/> — which fires
    /// <c>SIDE_ATTRIBUTE_CHANGE(76)</c> to the <b>whole mission</b>
    /// (<c>SideGlobalAttributeChange</c>, <c>FedSrv.CPP:11313-11318</c>,
    /// unconditional).</item>
    /// </list>
    /// The <c>c_didTeamMoney</c> economic-victory shortcut is out of scope (see
    /// the stage report).</summary>
    private void CompleteDevelopment(Mission mission, Side side, DataDevelopmentIGC dev)
    {
        var effects = TechTreeBitMask.FromBytes(dev.TtbmEffects);

        // 1. ApplyDevelopmentTechs — OR in only genuinely-new bits; announce via
        //    SIDE_TECH_CHANGE (gated on Stage == Started), native FedSrv.CPP:11283.
        bool newTechs = !effects.IsSubsetOf(side.DevelopmentTechs);   // sideigc.h:197
        if (newTechs)
        {
            side.DevelopmentTechs = side.DevelopmentTechs.Or(effects);
            if (mission.Stage == MissionStage.Started)
                BroadcastSideTechChange(mission, side);
        }

        // 2. DEV_COMPLETED — unconditional, side's own group (FedSrv.CPP:11262-11266).
        BroadcastDevCompleted(mission, side, dev.DevelopmentId);

        // 3. Unless tech-only, apply the attribute delta (the native multiply,
        //    igc.h:935-946) → whole-mission SIDE_ATTRIBUTE_CHANGE.
        bool techOnly = DevelopmentLogic.IsTechOnly(dev);
        if (!techOnly)
        {
            side.GlobalAttributes = side.GlobalAttributes.Apply(GlobalAttributeSet.FromArray(dev.Gas));
            BroadcastSideAttributeChange(mission, side);
        }

        Console.WriteLine($"[devcomplete] side {side.Id}: completed development {dev.DevelopmentId} '{dev.Name}' -> " +
                          $"{(newTechs ? $"+{effects.PopCount} new tech bit(s)" : "no new techs")}; " +
                          $"DEV_COMPLETED(8) side-group" +
                          $"{(newTechs && mission.Stage == MissionStage.Started ? "; SIDE_TECH_CHANGE(75) side-group (stage Started)" : (newTechs ? " (SIDE_TECH_CHANGE suppressed: stage != Started)" : ""))}" +
                          $"{(techOnly ? "; tech-only, no attribute delta" : "; SIDE_ATTRIBUTE_CHANGE(76) whole-mission")}");
    }

    /// <summary>Broadcast <c>SIDE_TECH_CHANGE(75)</c> to the side's own connected
    /// peers — the port of <c>CFSSide::FromIGC(pside)-&gt;GetGroup()</c> (the same
    /// side-group analogue <c>BroadcastMoneyChangeSideWide</c>/the SetWingId relay
    /// use, <c>Server.cs:3626-3637</c>), scoped to BOTH the side's id <em>and</em>
    /// its mission (side ids are not unique across this server's world missions).
    /// Carries the side's post-OR development tech mask. Only called when the
    /// mission is <c>Started</c> (native stage gate, FedSrv.CPP:11283).</summary>
    private void BroadcastSideTechChange(Mission mission, Side side)
    {
        var targets = _peerShips
            .Where(kv => kv.Value.Ship.Mission == mission && kv.Value.Ship.SideId == side.Id)
            .Select(kv => kv.Key)
            .ToArray();
        var msg = new SideTechChange { SideId = side.Id, TtbmDevelopments = side.DevelopmentTechs.ToBytes() };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        foreach (var t in targets) SendPeer(t, packet);
        Console.WriteLine($"[sidetech] side {side.Id} -> SIDE_TECH_CHANGE(75) devMask({side.DevelopmentTechs.PopCount} bits) x{targets.Length} side-group peer(s)");
    }

    /// <summary>Broadcast <c>DEV_COMPLETED(8)</c> <c>{devId}</c> to the side's own
    /// connected peers — unconditional, side's own group
    /// (<c>CFSSide::FromIGC(pside)-&gt;GetGroup()</c>, FedSrv.CPP:11264-11266),
    /// scoped to both the side's id and its mission (side ids repeat across
    /// world missions).</summary>
    private void BroadcastDevCompleted(Mission mission, Side side, short devId)
    {
        var targets = _peerShips
            .Where(kv => kv.Value.Ship.Mission == mission && kv.Value.Ship.SideId == side.Id)
            .Select(kv => kv.Key)
            .ToArray();
        var msg = new DevCompleted { DevId = devId };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        foreach (var t in targets) SendPeer(t, packet);
        Console.WriteLine($"[devcompleted] side {side.Id} -> DEV_COMPLETED(8) devId={devId} x{targets.Length} side-group peer(s)");
    }

    /// <summary>Broadcast <c>SIDE_ATTRIBUTE_CHANGE(76)</c> to the <b>whole
    /// mission</b> (every peer whose ship belongs to <paramref name="mission"/>,
    /// all sides) — the port of native <c>GetGroupMission()</c>
    /// (<c>FedSrv.CPP:11317</c>), unconditional. Carries the side's post-Apply
    /// 25 attribute multipliers.</summary>
    private void BroadcastSideAttributeChange(Mission mission, Side side)
    {
        var targets = _peerShips
            .Where(kv => kv.Value.Ship.Mission == mission)
            .Select(kv => kv.Key)
            .ToArray();
        var msg = new SideAttributeChange { SideId = side.Id, GasAttributes = side.GlobalAttributes.ToArray() };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        foreach (var t in targets) SendPeer(t, packet);
        Console.WriteLine($"[sideattr] side {side.Id} -> SIDE_ATTRIBUTE_CHANGE(76) x{targets.Length} whole-mission peer(s)");
    }

    /// <summary>
    /// Handle a simulated payday (<see cref="ServerHost.PaydayEvent"/>, raised
    /// from <see cref="Mission.Update"/>'s payday pass on the sim thread) by
    /// performing the reference <c>CFSMission::DoPayday(IsideIGC*)</c> payout
    /// (<c>fsmission.cpp:2294-2326</c>) on the wire. <see cref="Mission"/>
    /// has already accrued the side's income into <see cref="Side.Money"/> and
    /// passed it through the site seam; this handler does the distribution
    /// half — the <c>delta</c> split by the side's player count, the
    /// <c>FM_S_PAYDAY(114)</c> broadcast to that side's own connected peers
    /// (the same per-side peer grouping <c>SelectChatTargets</c> uses for
    /// <c>Team</c>, Server.cs:1124-1128), and the <c>delta·np</c> deduction
    /// that leaves only the remainder in the side treasury. Runs on the sim
    /// thread (the same one that owns <c>_peerShips</c> and broadcasts ship
    /// states), so no extra locking is needed — mirroring
    /// <see cref="BroadcastKillShip"/>.
    /// </summary>
    private void OnPayday(ISideIgc sideIfc, float incomeAccrued)
    {
        if (sideIfc is not Side side)
        {
            Console.WriteLine("[payday] PaydayEvent with a non-Side side — ignored");
            return;
        }

        // Per-side player count, matching GetCountOfPlayers(pside, true)
        // (fsmission.cpp:2298): peers seated on this side via their ship's
        // SideId. The grouping is the same peer-resolution the chat Team relay
        // uses (Server.cs:1124-1128).
        var sidePeers = _peerShips
            .Where(kv => kv.Value.Ship.SideId == side.Id)
            .Select(kv => kv.Key)
            .ToArray();
        int playerCount = sidePeers.Length;

        // A side with zero players accrues but never pays out — the money stays
        // in the side treasury (matching the reference `if (np != 0)` guard at
        // fsmission.cpp:2298-2299, which is what avoids a divide-by-zero). Not a
        // crash, not a fabricated fallback.
        if (playerCount == 0)
        {
            Console.WriteLine($"[payday] side {side.Id} '{side.Name}': 0 players — accrued {incomeAccrued:F0} kept in treasury (balance {side.Money})");
            return;
        }

        long deltaLong = side.Money / playerCount;   // integer division, fsmission.cpp:2301
        if (deltaLong <= 0)
        {
            Console.WriteLine($"[payday] side {side.Id} '{side.Name}': delta {deltaLong} — nothing to pay (balance {side.Money})");
            return;
        }

        int delta = (int)deltaLong;                  // native Money is 32-bit (AGCIDL.idl:246)
        var msg = new Payday { DMoney = delta };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        foreach (var t in sidePeers)
            SendPeer(t, packet);                     // side-scoped broadcast, fsmission.cpp:2307

        side.Money -= deltaLong * playerCount;       // keep the remainder < np, fsmission.cpp:2309

        // P197 — the actual per-player wallet credit (native DoPayday,
        // fsmission.cpp:2310-2324): each player's share lands in their resolved
        // auto-donate target's wallet (if one is set), else in their own. This
        // is the real behavior the P51 port skipped (it only broadcast PAYDAY
        // and drained the treasury without crediting anyone). Faithful to native:
        // CFSPlayer::SetMoney is a plain setter that fires NO MONEY_CHANGE here —
        // the per-side gross share is already announced by PAYDAY(114), and the
        // client derives its own wallet from the donate relationship, so there is
        // deliberately no MONEY_CHANGE at payday (see PLAYER_MONEY_AUTODONATE_P197.md).
        foreach (var kv in _peerShips)
        {
            if (kv.Value.Ship.SideId != side.Id) continue;
            var donate = kv.Value.Wallet.AutoDonateTarget;
            var receiver = donate is null ? kv.Value : LiveOf(donate);
            if (receiver is not null)
                receiver.Wallet.Money += delta;
        }

        var payNote = $"[payday] side {side.Id} '{side.Name}' -> PAYDAY(114) delta={delta} balance={side.Money}";
        Console.WriteLine(payNote);
        Notable?.Invoke(payNote);                      // spectator event feed (same text)
    }

    /// <summary>
    /// Handle a simulated mining accrual (<see cref="ServerHost.MineAsteroidEvent"/>,
    /// raised from <see cref="Sector.ResolveMining"/> on the sim thread when an
    /// asteroid's ore moved past the native reporting threshold,
    /// <c>asteroidigc.h:246-251</c>) by logging it <em>and</em> broadcasting a real
    /// <c>FM_S_ASTEROID_MINED(198)</c> <see cref="AsteroidMined"/> message — the
    /// rock's remaining ore made wire-visible to observers in real time (P120).
    /// The <c>newOre</c> arg is the rock's ore fraction (<c>asteroidigc.h:249</c>),
    /// i.e. <c>ore/oreMax</c>, not an absolute mass.
    /// <para><b>WHOLE MISSION.</b> Native sends <c>ASTEROID_MINED</c> from
    /// <c>MineAsteroidEvent</c> behind a per-side <c>GetAsteroidCurrentEye</c>
    /// visibility gate (<c>FedSrv.CPP:12453-12468</c>), but that gate is the same
    /// class of cross-side-visibility infrastructure this port lacks for
    /// <c>PROBES_UPDATE</c>(92)/<c>ASTEROIDS_UPDATE</c>(179) and does not build
    /// here. Following the precedent this port's own sibling message set —
    /// <c>ASTEROID_DESTROYED</c>(86, P102) broadcasts whole-mission with no
    /// per-side gate (<see cref="BroadcastAsteroidDestroyed"/>, confirmed
    /// deliberate) — this broadcasts to every connected peer with a seated ship,
    /// <c>GetGroupRealSides()</c> audience (<c>FedSrv.CPP:12418</c>). The miner's
    /// own cargo ore still travels as <c>ServerShipUpdate</c> byte 45
    /// (<c>OreFraction</c>); this closes the observer gap for the <em>rock's</em>
    /// remaining ore. Runs on the sim thread (same one that owns
    /// <c>_peerShips</c>), so no extra locking.</para>
    /// </summary>
    private void OnMine(IAsteroidIgc pasteroid, float newOre)
    {
        if (pasteroid is not Asteroid ast)
        {
            Console.WriteLine("[mine-event] MineAsteroidEvent with a non-Asteroid asteroid — ignored");
            return;
        }
        Console.WriteLine($"[mine-event] asteroid {ast.Data.AsteroidId} '{ast.Data.Name}' ore fraction {newOre:0.###} (remaining {ast.OreRemaining:0.##}/{ast.OreMax:0.##})");

        var msg = AsteroidMined.ForAsteroid(ast, newOre);
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        // Whole mission: every connected peer with a seated ship, regardless of
        // side (GetGroupRealSides(), FedSrv.CPP:12418) — the ASTEROID_DESTROYED
        // precedent (P102).
        var targets = _peerShips.Select(kv => kv.Key).ToArray();

        Console.WriteLine($"[asteroid] asteroid {ast.Data.AsteroidId} '{ast.Data.Name}' ore fraction {newOre:0.###} " +
                          $"=> ASTEROID_MINED(198) sector {ast.Data.ClusterId} -> {targets.Length} peer(s) [WHOLE MISSION]");
        foreach (var t in targets)
            SendPeer(t, packet);
    }

    /// <summary>
    /// Handle a simulated warp transit (<see cref="ServerHost.HitWarpEvent"/>,
    /// raised from the sector tick's <c>Sector.ResolveWarpCollisions</c> on the
    /// sim thread) by refreshing the per-peer <see cref="LiveShip"/> record and
    /// sending a fresh <c>FM_S_SET_CLUSTER(7)</c>. The client's SET_CLUSTER
    /// handler (<c>appmsg.cpp:1028-1087</c>) is what calls
    /// <c>pship->SetCluster(pcluster)</c> — the client-side sector reference the
    /// whole exploration/movement loop is gated on — so a transit must re-send
    /// it, not just rebroadcast a moved position. The <see cref="ShipWarped"/>
    /// hook fires after the ship is already relocated/reassigned
    /// (<c>Ship.Sector</c> and the sector model lists), so this handler only
    /// does the wire-facing half. Scoped to a single ship by
    /// <see cref="Ship.Id"/>/reference — no multi-peer visibility concerns
    /// (P40's own-ship scoping note), matching this chain's established scope.
    /// </summary>
    /// <remarks>
    /// Two consequences ride on refreshing the record here. (1) The next
    /// <see cref="BroadcastShipStates"/> sees <c>live.Sector.Id !=
    /// _lastStatusSector</c> and re-sends <c>FM_S_SHIP_STATUS(90)</c> with the
    /// new sector — the <c>sectorChanged</c> hook P40 built that never fired for
    /// lack of a real sector change now fires. (2) All the other sector readers
    /// (chat <c>AllSector</c>/<c>FriendlySector</c> targeting,
    /// <c>BroadcastKillShip</c>) agree on the new sector. The debounce window is
    /// set here (<see cref="Ship.LastWarpTime"/>, native fsship.cpp:1188) so the
    /// fresh position near the destination warp cannot be immediately swallowed
    /// back — owned by this method because only <see cref="Server"/> knows the
    /// sim clock (<c>_simNow</c>).
    /// </remarks>
    private void OnShipWarped(Ship ship, Sector oldSector, Sector newSector)
    {
        ship.LastWarpTime = _simNow;   // debounce (native 2s fudge, fsship.cpp:1188)

        foreach (var kv in _peerShips)
        {
            if (!ReferenceEquals(kv.Value.Ship, ship)) continue;

            // Refresh the per-peer LiveShip sector so BroadcastShipStates' own-
            // ship status / sector readers all see the new sector. Established
            // `live with { ... }` reassignment pattern (same as LastBroadcastPos,
            // Server.cs:342,500).
            _peerShips[kv.Key] = kv.Value with { Sector = newSector };

            var set = new SetCluster
            {
                SectorId = newSector.Id,
                Ship = ServerShipUpdate.FromModel(ship.Id, ship, (uint)TickIndex),
                Cookie = 0,   // no per-peer cookie is stored; join re-used the ack's local value
            };
            SendPeer(kv.Key, FedPacket.Pack(new[] { set.Encode() }));
            Console.WriteLine($"[warp] peer {kv.Key} ship {ship.Id} -> SET_CLUSTER sector {newSector.Id} (LiveShip.Sector refreshed; SHIP_STATUS(90) resend via BroadcastShipStates)");
            return;
        }

        // A transit on a ship that is not a peer's own (possible only in a pure
        // sim / future-drone case) still moved the model correctly; only the
        // wire-facing routing is skipped.
        Console.WriteLine($"[warp] ship {ship.Id} transited '{oldSector.Name}' -> '{newSector.Name}' but is not a peer ship — SET_CLUSTER/LiveShip routing skipped (pure-sim transit)");
    }

    /// <summary>
    /// Handle a simulated dock (<see cref="ServerHost.DockWithStationEvent"/>,
    /// raised from the sector tick's <c>Sector.ResolveDockCollisions</c> on the
    /// sim thread, after <see cref="Ship.StationId"/> has been latched) by
    /// sending the <c>FM_S_DOCKED(61)</c> notify to the docking peer. This is
    /// the wire trigger that makes the native client's <c>ship.GetStation()</c>
    /// resolve: the client's <c>FM_S_DOCKED</c> handler
    /// (<c>appmsg.cpp:1092-1115</c>) is the ONLY call site that calls
    /// <c>m_ship-&gt;SetStation(reallyNonNull)</c> (<c>:1113</c>) — the member
    /// <c>IshipIGC::GetStation()</c> reads (<c>shipIGC.h:417-420</c>). Mirrors
    /// <see cref="OnShipWarped"/>'s per-peer routing: scoped to a single ship,
    /// sent to the docked peer only (no multi-peer visibility concerns, P40's
    /// own-ship scoping). Raised once per dock transition because the dock pass
    /// skips already-docked ships — exactly once per dock event.
    /// </summary>
    private void OnShipDocked(Ship ship, short? stationSide)
    {
        // P201 — flag-secure leg (native FedSrv.CPP:13111-13159): a ship
        // docking at ITS OWN side's station while carrying a flag secures it.
        // The gate is the native `pside == psideOld` — pside = the ship's side
        // (pship->GetSide()), psideOld = the station's side (pstation->GetSide())
        // — so docking at ANY other side's station does NOT secure the flag.
        // On secure: increment the owning side's Flags (or Artifacts if the
        // carried value is the team-lobby sentinel), clear the carried flag,
        // and announce whole-mission with sideidFlag = NA.
        var mission = ship.Mission;
        if (ship.Flag is { } sidFlag
            && stationSide is { } ss
            && ship.SideId is { } shipSide
            && shipSide == ss
            && mission is not null)
        {
            var side = mission.Sides.FirstOrDefault(s => s.Id == shipSide);
            if (side is not null)
            {
                // Native branch (FedSrv.CPP:13123-13137): a carried value of
                // SIDE_TEAMLOBBY is a neutral artifact; anything else is a real
                // enemy flag. The team-lobby sentinel (-2) cannot occur in this
                // port (no team-lobby side — real sides only, id >= 0), so the
                // artifact branch is structurally present but unreachable; only
                // the real-enemy-flag branch exercises the counter.
                if (sidFlag == Side.ArtifactSentinelFlag)
                    side.Artifacts++;
                else
                    side.Flags++;
                Console.WriteLine($"[flag] ship {ship.Id} secured side {sidFlag}'s flag at own-side station {ss} -> side {shipSide} flags={side.Flags} artifacts={side.Artifacts}");
            }
            ship.Flag = null;   // pship->SetFlag(NA), FedSrv.CPP:13149

            // Whole-mission announce with sideidFlag = NA (FedSrv.CPP:13150-13157).
            BroadcastGainFlag(mission, GainFlag.NA, ship.Id, bIsTreasureDocked: false, parttypeidDocked: 0);
        }

        foreach (var kv in _peerShips)
        {
            if (!ReferenceEquals(kv.Value.Ship, ship)) continue;

            var notify = new DockedNotify { StationId = ship.StationId!.Value };
            SendPeer(kv.Key, FedPacket.Pack(new[] { notify.Encode() }));
            Console.WriteLine($"[docked] peer {kv.Key} ship {ship.Id} <- FM_S_DOCKED(61) station {notify.StationId} (native ship.GetStation() resolve trigger)");
            return;
        }

        // A dock on a ship that is not a peer's own (possible only in a pure
        // sim / future-drone case) still latched the state correctly; only the
        // wire-facing notify is skipped.
        Console.WriteLine($"[docked] ship {ship.Id} docked but is not a peer ship — FM_S_DOCKED(61) routing skipped (pure-sim dock)");
    }

    // ── teardown ────────────────────────────────────────────────────────────

    /// <summary>Tear down in reverse of boot: stop the transport (joining its
    /// background threads), then drop the host. Idempotent.</summary>
    public void Shutdown()
    {
        // Heartbeat constructed last in Boot → torn down first, so its best-effort
        // deregister DELETE fires before the process tears down. Independent of the
        // transport shutdown (either order is fine) — kept first for reverse-of-boot clarity.
        _heartbeat?.Dispose();
        _heartbeat = null;
        _host?.Transport?.Shutdown();
        lock (_captureLock)
        {
            _captureStream?.Flush();
            _captureStream?.Dispose();
            _captureStream = null;
        }
        Console.WriteLine($"[shutdown] clean: {LoopIterations} ticks, {Overruns} overruns");
    }

    /// <summary>Dispose = <see cref="Shutdown"/>, so <c>using</c> guarantees the
    /// transport is torn down (no orphan threads) even when an assertion fails.</summary>
    public void Dispose() => Shutdown();

    // ── the runnable entry (used by Program.Main) ──────────────────────────

    /// <summary>Boot, then tick until SIGINT/SIGTERM, then shut down cleanly.
    /// Returns 0.</summary>
    public int RunAsync(string configPath, IReadOnlyList<(string Key, string Value)> cliOverrides)
    {
        Boot(configPath, cliOverrides);
        using var cts = new CancellationTokenSource();

        // On Linux use PosixSignalRegistration to intercept SIGINT (Ctrl-C) and
        // SIGTERM and cancel the loop, letting the loop break and teardown run.
        // Skip Console.CancelKeyPress there — it hooks the same signal and
        // doubling up invites ambiguity; it is the fallback for other platforms.
        if (OperatingSystem.IsLinux())
        {
            // PosixSignalContext.Cancel defaults to FALSE: .NET runs the callback
            // then lets the signal's default disposition terminate the process
            // unless we suppress it. Without ctx.Cancel = true a SIGTERM/SIGINT
            // would kill the server mid-loop and teardown would never run.
            using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { cts.Cancel(); ctx.Cancel = true; });
            using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { cts.Cancel(); ctx.Cancel = true; });
            Loop(cts.Token);
        }
        else
        {
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            Loop(cts.Token);
        }

        Shutdown();
        return 0;
    }
}
