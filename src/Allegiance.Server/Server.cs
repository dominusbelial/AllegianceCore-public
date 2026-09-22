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
public sealed partial class Server : IDisposable
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
    // P314 — SimPass stages that threw and were contained by the per-stage
    // exception barrier (<see cref="Step"/>). Read under _statsLock so an
    // off-sim-thread test can assert that a poisoned tick degraded gracefully
    // instead of killing the process.
    private long _simPassStageFailures;
    private int _liveShipCount;
    private readonly List<short> _bucketCompletions = new();
    private readonly object _statsLock = new();

    // P315 (review finding H3) — rate-limits the per-message "unsupported fmid"
    // refusal log in ProcessDatagram so a flood of garbage/unknown fmids cannot
    // force unbounded synchronous Console.WriteLine calls inside one tick (see
    // PerPeerRateLimiter). Trimmed in ReleasePeer on peer leave to stay bounded
    // by the live peer set.
    private readonly PerPeerRateLimiter _unsupportedLog = new();

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

    // Loadout-dirty tracking for the teammate ExportShip(26) PartData[] tail
    // (P317): two coordinated structures replace the old per-(recipient,
    // subject) `IReadOnlyList<PartData>` snapshot. `_teammateLoadoutCache` is
    // ONE snapshot per subject SHIP (the same for every recipient, keyed by
    // ship id, tagged with the ship's `LoadoutVersion` at compute time) so
    // `SnapshotParts` runs at most once per ship per actual loadout change,
    // never once per (recipient, subject) pair per tick. `_teammateLoadoutVersionSent`
    // records, per (recipient, subject) pair, the ship's `LoadoutVersion` the
    // last EXPORT carried — the per-pair dirty check is now a cheap integer
    // compare ("has this ship's cached snapshot changed since I last told this
    // recipient"), replacing the O(peers²) per-tick SnapshotParts
    // allocation-and-compare. Not one-shot (a teammate can re-buy any number
    // of times). Sim-thread only, like the siblings above.
    private readonly Dictionary<short, (long Version, IReadOnlyList<PartData> Snapshot)> _teammateLoadoutCache = new();
    private readonly Dictionary<(ConnectionId Recipient, short ShipId), long> _teammateLoadoutVersionSent = new();

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

    // P265 — last ScoreRosterUpdate(209) send tick per mission, throttling the
    // periodic in-mission scoreboard broadcast in BroadcastScoreRoster (see
    // ScoreRosterRefreshTicks). Sim-thread only.
    private readonly Dictionary<Mission, long> _lastScoreRosterTick = new();

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

    // P324 (M4) — sides that must force an immediate per-side object sync
    // (STATIONS/ASTEROIDS/PROBES_UPDATE) on the next SimPass, regardless of the
    // every-10 cadence. Populated when a peer newly joins/seats on a side (so a
    // fresh joiner is not left waiting up to a full cadence window for the
    // sector's stations/asteroids/probes to appear — native sends them to a
    // player on cluster entry), and cleared after the object broadcasts run.
    // Sim-thread only, like the _peerShips siblings.
    private readonly HashSet<short> _objectSyncDirtySides = new();

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

    /// <summary>P314 — number of <see cref="SimPass"/> stages that threw and were
    /// contained by the per-stage exception barrier (a poisoned tick degraded
    /// gracefully instead of killing the server). Monotonic unless reset.</summary>
    public long SimPassStageFailures { get { lock (_statsLock) return _simPassStageFailures; } }

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

    /// <summary>M1 test seam (InternalsVisibleTo): snapshot the sizes of the
    /// connection-scoped caches <see cref="ReleasePeer"/> is responsible for
    /// freeing, so live disconnect tests can prove they return to (near) zero
    /// after peers leave instead of growing monotonically with churn. Each is
    /// a sim-thread-owned collection; the sizes are best-effort snapshots.</summary>
    internal (int PendingSideSelect, int PeerNames, int PlayerReady,
              int LastStatusSector, int LastStatusTick, int LastClusterInfoTick,
              int LastPingQuery, int TeammateExportSent, int TeammatePlayerInfoSent,
              int TeammateLoadoutVersionSent, int TeammateLoadoutCache,
              int LastTeammateStatusTick) ConnectionScopedCacheCounts()
        => (
            _pendingSideSelect.Count, _peerNames.Count, _playerReady.Count,
            _lastStatusSector.Count, _lastStatusTick.Count, _lastClusterInfoTick.Count,
            _lastPingQuery.Count, _teammateExportSent.Count, _teammatePlayerInfoSent.Count,
            _teammateLoadoutVersionSent.Count, _teammateLoadoutCache.Count,
            _lastTeammateStatusTick.Count);

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

    /// <summary>10 s at 20 upd/s -- see the CLUSTERINFO refresh block in
    /// <see cref="BroadcastShipStates"/> for why this exists.</summary>
    private const int ClusterInfoRefreshTicks = 200;

    /// <summary>Ticks between periodic in-mission SCORE_ROSTER_UPDATE(209)
    /// broadcasts. 40 ticks @ 20 upd/s = 2 s — fast enough to feel live, slow
    /// enough to be negligible alongside the per-tick syncs.</summary>
    private const int ScoreRosterRefreshTicks = 40;
    /// <summary>P281 — a peer that has logged on but not yet chosen a side (the
    /// post-connect side-select hold). Held here (NOT in <c>_peerShips</c>) with
    /// no Ship until it picks a side or the timeout seats it; its ship id is
    /// reserved up-front so LogonAck carries a stable id.</summary>
    private readonly struct PendingSideSelect
    {
        public short ShipId { get; }
        public LogonRequest Request { get; }
        public double ConnectSimTime { get; }
        /// <summary>P281 — the requested side once the peer chooses (null = not
        /// chosen yet); NA(-1) is never stored here (auto-assign is expressed by
        /// a null RequestedSide at timeout).</summary>
        public short? RequestedSide { get; }
        /// <summary>P281 — monotonically increasing logon-order index, so the
        /// side-select drain seats peers in the order they LOGGED ON (not the
        /// order their POSITIONREQ raced in), keeping round-robin sides and
        /// injected ShipSpawner ordering deterministic exactly as pre-P281.</summary>
        public long JoinSeq { get; }
        public PendingSideSelect(short shipId, LogonRequest request, double connectSimTime, long joinSeq)
            : this(shipId, request, connectSimTime, joinSeq, null) { }
        private PendingSideSelect(short shipId, LogonRequest request, double connectSimTime, long joinSeq, short? requestedSide)
        {
            ShipId = shipId; Request = request; ConnectSimTime = connectSimTime; JoinSeq = joinSeq; RequestedSide = requestedSide;
        }
        public PendingSideSelect WithRequestedSide(short? side) =>
            new(ShipId, Request, ConnectSimTime, JoinSeq, side);
    }

    /// <summary>P281 — how long a logged-on peer may sit at the side-select
    /// screen before the server auto-assigns it a side (rationale in
    /// <see cref="AdvanceSideSelection"/>).</summary>
    private const double SideSelectTimeoutSeconds = 30.0;

    /// <summary>P281 — peers awaiting a post-connect side choice (keyed by
    /// connection). Distinct from <see cref="_pendingJoinRequests"/> (P184),
    /// which tracks an ALREADY-SPAWNED peer's request to switch sides.</summary>
    private readonly Dictionary<ConnectionId, PendingSideSelect> _pendingSideSelect = new();

    /// <summary>P281 — logon-order counter for <see cref="PendingSideSelect.JoinSeq"/>.</summary>
    private long _nextJoinSeq;

    /// <summary>Ships per row before wrapping to the next row in
    /// <see cref="SpawnDefaultShip"/>'s spawn grid — see its remarks for why
    /// this replaced a single ever-growing line.</summary>
    private const int SpawnGridColumns = 12;

    /// <summary>Default spawn: first hull in the core (by id), placed on a
    /// bounded grid (staged by peer count so concurrent bots don't overlap)
    /// oriented straight ahead (identity) in the first sector of the first
    /// mission, and armed with the cheapest real weapon the core offers (see
    /// <see cref="MountCombatWeapon"/>).</summary>
    /// <remarks>
    /// Originally a single ever-growing x-offset line (<c>x = 720 * (1 +
    /// peerCount)</c>). Confirmed live (2026-09-15) that this spreads ships
    /// absurdly far apart at real peer counts — by the 150th concurrent
    /// connection, ships sit ~108,000 units apart, genuinely out of each
    /// other's rendered/gameplay range despite being reported as valid
    /// same-sector <see cref="Model.Sector.GetShipUpdateCandidates"/>
    /// entries. This was the actual root cause of a long, confusing "bots
    /// aren't visible" live-debugging session that initially (wrongly)
    /// suspected Docker networking, TCP_NODELAY, and netem — none of which
    /// were the real problem. `vizfight`'s own explorer spawner
    /// (`InjectExplorerSpawn`, `tools/vizfight/Program.cs`) already solved
    /// this correctly with a bounded 2D grid jitter; this ports the same
    /// idea (wrap into rows instead of one unbounded line) into the shared
    /// default spawner so every caller gets it, not just vizfight's own
    /// override.
    /// </remarks>
    private (Ship Ship, Sector Sector) SpawnDefaultShip(Mission mission, short shipId)
    {
        if (mission.Core.HullTypes.Count == 0)
            throw new InvalidOperationException("core has no hull types to assign a ship");
        var hull = mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
        int column = _peerShips.Count % SpawnGridColumns;
        int row = _peerShips.Count / SpawnGridColumns;
        float x = 720f * (1 + column);
        float z = 920f + row * 720f;
        var sector = mission.Sectors[0];
        var ship = new Ship(shipId, hull, new Vector3(x, 730f, z), Vector3.Zero, Orientation.Identity);
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

        short shipId = AllocateShipId();   // same id space as player ships (P316 collision-skip)
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
            ConsoleLog.Write($"[boardship] {from} ship {ship.Id} -> sidParent {req.SidParent} refused — BOARD_NACK(178) to requester only");
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
            ConsoleLog.Write($"[turret] ship {gunner.Id}: ACTIVE_TURRET_UPDATE from a non-gunner — ignored (FedSrv.CPP:4873-4875)");
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
            ConsoleLog.Write($"[promote] {from} ship {ship.Id}: {(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — ignored (native gate, FedSrv.CPP:6357)");
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
            ConsoleLog.Write($"[swappart] {from} ship {ship.Id}: {(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — ignored (native gate, FedSrv.CPP:6461)");
            return;
        }

        // Native TrapHackBoot(pfsPlayer->GetIGCShip()->GetParentShip() == NULL)
        // (FedSrv.CPP:6464): a boarded gunner/observer cannot swap parts.
        if (ship.ParentShip is not null)
        {
            ConsoleLog.Write($"[swappart] {from} ship {ship.Id}: boarded gunner (parent {ship.ParentShip.Id}) — SWAP_PART ignored (FedSrv.CPP:6464)");
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
            ConsoleLog.Write($"[reload] {from} ship {ship.Id}: {(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — ignored (native gate, FedSrv.CPP:6506)");
            return;
        }

        // Native TrapHackBoot(pfsPlayer->GetIGCShip()->GetParentShip() == NULL)
        // (FedSrv.CPP:6509): a boarded gunner/observer cannot reload its own hold.
        if (ship.ParentShip is not null)
        {
            ConsoleLog.Write($"[reload] {from} ship {ship.Id}: boarded gunner (parent {ship.ParentShip.Id}) — RELOAD ignored (FedSrv.CPP:6509)");
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
            ConsoleLog.Write($"[reload] {from} ship {ship.Id}: {nReloads} entries outside [1,5] — refused (native TrapHackBoot, FedSrv.CPP:6523)");
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
            ConsoleLog.Write($"[reload] {from} ship {ship.Id}: malformed entry, refusing remaining reloads (softened TrapHackBoot, FedSrv.CPP:6645-6648)");
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
            ConsoleLog.Write($"[droppart] {from} ship {ship.Id}: {(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — ignored (native gate, FedSrv.CPP:6297)");
            return;
        }

        // Native TrapHackBoot(pfsPlayer->GetIGCShip()->GetParentShip() == NULL)
        // (FedSrv.CPP:6300): a boarded gunner/observer cannot drop its own parts.
        if (ship.ParentShip is not null)
        {
            ConsoleLog.Write($"[droppart] {from} ship {ship.Id}: boarded gunner (parent {ship.ParentShip.Id}) — DROP_PART ignored (FedSrv.CPP:6300)");
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
            ConsoleLog.Write($"[fireexpendable] {from} ship {ship.Id}: {(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — ignored (native gate, FedSrv.CPP:5611)");
            return;
        }

        // Native TrapHackBoot(pShip->GetParentShip() == NULL) (FedSrv.CPP:5644):
        // a boarded gunner/observer cannot fire its own dispenser. Ported as the
        // graceful log-and-return refusal every other TrapHackBoot-guarded C→S
        // handler here uses (cf. HandleSwapPart, FedSrv.CPP:6464).
        if (ship.ParentShip is not null)
        {
            ConsoleLog.Write($"[fireexpendable] {from} ship {ship.Id}: boarded gunner (parent {ship.ParentShip.Id}) — FIRE_EXPENDABLE ignored (FedSrv.CPP:5644)");
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
            ConsoleLog.Write($"[fireexpendable] {from} ship {ship.Id}: dispenser {req.Et}@0 has Amount {part.Amount} — refused, no fire (native TrapHackBoot, FedSrv.CPP:5656)");
            return;
        }

        // P224 — dispatch the mounted launcher by its construction-only subtype:
        // chaff / mine / probe. Native resolves the expendable from the
        // dispenser's expenditureTypeID and branches on its object type
        // (OT_chaffType / OT_mineType / OT_probeType, FedSrv.CPP:5696); the port's
        // mounted-part subtype (DataPartTypeIGC.ChaffLauncher / MineLauncher /
        // ProbeLauncher, DataPartTypeIGC.cs) is that resolution's stand-in.
        bool sectorWide;          // chaff: whole cluster; mine/probe: same-side only
        List<byte[]> enc;
        string deployed;          // human-readable description for the log line
        switch (part.Type.Type)
        {
            case DataPartTypeIGC.ChaffLauncher cl:
            {
                if (!mission.Core.ChaffTypes.TryGetValue(cl.ExpendableTypeId, out var chaffType))
                {
                    ConsoleLog.Write($"[fireexpendable] {from} ship {ship.Id}: chaff launcher names unknown chaff type {cl.ExpendableTypeId} — ignored");
                    return;
                }

                // Deploy the chaff at the ship's position/velocity and run the native
                // missile-retarget loop; the result names the new Chaff and the spoofed
                // missiles (fed by the server's shared _rand, like native's global random).
                var result = ship.DeployChaff(chaffType, mission.LastUpdate, _rand);
                sectorWide = true;

                // Build all four broadcasts in one guaranteed batch (native creates them
                // in this same handler and flushes once: SendMessages(GetGroupSectorFlying
                // (pcluster), FM_GUARANTEED, FM_FLUSH), FedSrv.CPP:5749). Audience: the
                // whole cluster, all sides — GetGroupSectorFlying(pcluster), the exact
                // SendSectorWide audience.
                enc = new List<byte[]>
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
                deployed = $"chaff {chaffType.ExpendableTypeId} (pos {result.Chaff.Position} vel {result.Chaff.Velocity}) " +
                           $"-> FE(87)+CREATE_CHAFF(109)+[{result.Spoofed.Count}×MISSILE_SPOOFED]+END_SPOOFING(111)";
                break;
            }
            case DataPartTypeIGC.MineLauncher ml:
            {
                if (!mission.Core.MineTypes.TryGetValue(ml.ExpendableTypeId, out var mineType))
                {
                    ConsoleLog.Write($"[fireexpendable] {from} ship {ship.Id}: mine launcher names unknown mine type {ml.ExpendableTypeId} — ignored");
                    return;
                }
                var mine = ship.DeployMine(mineType, mission.LastUpdate);
                sectorWide = false;
                // Mine/probe carry no dedicated instance message: native's single
                // S FIRE_EXPENDABLE (pfmSFE) plus the mine object exported through the
                // generic CreateObject/OT_mine path. The object EXPORT is triggered here
                // automatically by DeployMine → MineCreatedEvent → BroadcastExportMine.
                enc = new List<byte[]> { new FireExpendable { LauncherId = ship.Id, EquipmentType = req.Et }.Encode() };
                deployed = $"mine {mineType.ExpendableTypeId} (id {mine.Id}) -> FE(87)+EXPORT(26)/OT_mine";
                break;
            }
            case DataPartTypeIGC.ProbeLauncher pl:
            {
                if (!mission.Core.ProbeTypes.TryGetValue(pl.ExpendableTypeId, out var probeType))
                {
                    ConsoleLog.Write($"[fireexpendable] {from} ship {ship.Id}: probe launcher names unknown probe type {pl.ExpendableTypeId} — ignored");
                    return;
                }
                var probe = ship.DeployProbe(probeType, mission.LastUpdate);
                sectorWide = false;
                enc = new List<byte[]> { new FireExpendable { LauncherId = ship.Id, EquipmentType = req.Et }.Encode() };
                deployed = $"probe {probeType.ExpendableTypeId} (id {probe.Id}) -> FE(87)+EXPORT(26)/OT_probe";
                break;
            }
            default:
            {
                ConsoleLog.Write($"[fireexpendable] {from} ship {ship.Id}: mounted {req.Et}@0 is {part.Type.Type.GetType().Name}, not a chaff/mine/probe launcher — ignored");
                return;
            }
        }

        // Decrement the dispenser's ammo (FedSrv.CPP:5744-5745).
        short newAmount = (short)(part.Amount - 1);
        ship.SetPartAmount(part, newAmount);

        byte[] packet = FedPacket.Pack(enc.ToArray());
        int n = 0;
        if (sectorWide)
        {
            // Chaff: whole cluster, all sides — GetGroupSectorFlying(pcluster), the
            // exact SendSectorWide audience. Native's single flush (FedSrv.CPP:5783)
            // technically also covers mine/probe, but this port broadcasts mine/probe
            // OBJECT creation same-side only (no cross-side EXPORT exists), so a
            // cross-side FE(87) without the object would be incoherent.
            foreach (var kv in _peerShips)
                if (kv.Value.Sector.Id == ship.Sector!.Id) { SendPeer(kv.Key, packet); n++; }
        }
        else
        {
            // Mine/probe: SAME-SIDE only, matching this port's established mine/probe
            // broadcast audience — BroadcastExportMine / BroadcastExportProbe
            // (Server.cs:9739-9743 / 9793-9797: the layer's own peer always, plus
            // same-side teammates who hold its ExportShip via the _teammateExportSent
            // gate) and BroadcastFireMissile (Server.cs:9538-9546). Every FE(87)
            // recipient therefore also receives the mine/probe EXPORT.
            if (ship.SideId is { } sideId)
            {
                foreach (var kv in _peerShips)
                {
                    bool isLayer = ReferenceEquals(kv.Value.Ship, ship);
                    bool isVisibleTeammate = !isLayer
                                             && kv.Value.Ship.SideId == sideId
                                             && _teammateExportSent.Contains((kv.Key, ship.Id));
                    if (isLayer || isVisibleTeammate) { SendPeer(kv.Key, packet); n++; }
                }
            }
        }

        Console.WriteLine($"[fireexpendable] {from} ship {ship.Id} fired {deployed} to {n} {(sectorWide ? "cluster" : "same-side")} peer(s); " +
                          $"dispenser {newAmount + 1} -> {newAmount}");
    }

    /// <summary>
    /// P226 — the inbound <c>FM_CS_FIRE_MISSILE(70)</c> player-launch request,
    /// the port of native <c>FedSrv.CPP:5413-5507</c> <c>case FM_CS_FIRE_MISSILE</c>.
    /// Gates (mirroring native): the mission must be <c>STAGE_STARTED</c>
    /// (<c>FedSrv.CPP:5416</c>), the launcher unboarded (<c>TrapHackBoot(parent
    /// == NULL)</c>, <c>FedSrv.CPP:5423</c>), and a missile launcher mounted at
    /// <c>ET_Magazine</c>@0 (<c>GetMountedPart(ET_Magazine, 0)</c>,
    /// <c>FedSrv.CPP:5425</c>) with <c>Amount &gt; 0</c> (<c>FedSrv.CPP:5426</c>).
    /// The server is authoritative and ignores the client-supplied ids/missile-type/
    /// target ("Ignore the IDs provided by the client and generate one ourselves",
    /// <c>FedSrv.CPP:5444</c>): it resolves the launcher's own missile type and
    /// fires against the ship's server-side <see cref="Ship.CommandTarget"/> — the
    /// lock target <c>Ship.UpdateMissileLock</c> builds against each tick — via the
    /// shared <see cref="Ship.TryFireMissile"/> gate, which requires the lock to be
    /// sufficient (a <c>MaxLock == 0</c> type needs none) <em>and</em> ammo
    /// remaining before a <see cref="Missile"/> is created. Creation raises
    /// <c>MissileCreatedEvent</c> → <see cref="BroadcastFireMissile"/>, the outbound
    /// FIRE_MISSILE(70) to the firing ship's sector, and decrements ammo. No
    /// inbound <c>Server.cs</c> FIRE_MISSILE case existed before P226 — the brief
    /// called that absence correct — but native's real player path is exactly this
    /// inbound message, so P226 adds it (documented in <c>docs/MISSILE_FIRE_P226.md</c>).
    /// </summary>
    private void HandleFireMissile(ConnectionId from, LiveShip live, FireMissile req)
    {
        var ship = live.Ship;
        var mission = ship.Mission;
        if (mission is null || mission.Stage != MissionStage.Started)
        {
            ConsoleLog.Write($"[firemissile] {from} ship {ship.Id}: {(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — ignored (native gate, FedSrv.CPP:5416)");
            return;
        }
        if (ship.ParentShip is not null)
        {
            ConsoleLog.Write($"[firemissile] {from} ship {ship.Id}: boarded gunner (parent {ship.ParentShip.Id}) — FIRE_MISSILE ignored (FedSrv.CPP:5423)");
            return;
        }
        var part = ship.Parts.FirstOrDefault(p => p.EquipmentType == EquipmentType.Magazine && p.MountId == 0);
        if (part is not { Type.Type: DataPartTypeIGC.MissileLauncher })
        {
            ConsoleLog.Write($"[firemissile] {from} ship {ship.Id}: no mounted missile launcher (ET_Magazine@0) — ignored (native GetMountedPart(ET_Magazine,0), FedSrv.CPP:5425)");
            return;
        }
        if (part.Amount <= 0)
        {
            ConsoleLog.Write($"[firemissile] {from} ship {ship.Id}: launcher has Amount {part.Amount} — refused, no fire (native TrapHackBoot amount>0, FedSrv.CPP:5426)");
            return;
        }

        short amountBefore = part.Amount;
        bool fired = ship.TryFireMissile(mission.LastUpdate);
        short amountAfter = fired ? ship.Parts.FirstOrDefault(p => p.EquipmentType == EquipmentType.Magazine && p.MountId == 0)?.Amount ?? amountBefore : amountBefore;
        Console.WriteLine(fired
            ? $"[firemissile] {from} ship {ship.Id} launched -> FIRE_MISSILE(70) lock {ship.MissileLock:F3} ammo {amountBefore} -> {amountAfter}"
            : $"[firemissile] {from} ship {ship.Id}: FIRE_MISSILE requested but lock gate not met (MissileLock {ship.MissileLock:F3}, MaxLock need none iff 0) — no missile");
    }

    private void HandleSetWingId(ConnectionId from, LiveShip live, SetWingId req)
    {
        var ship = live.Ship;
        var mission = ship.Mission;

        // Native's ONLY gate: if (!pfsMission) break (FedSrv.CPP:5313). Deliberately
        // no STAGE_STARTED check — the native SET_WINGID handler has none.
        if (mission is null)
        {
            ConsoleLog.Write($"[setwingid] {from} ship {ship.Id}: no mission — ignored (native gate, FedSrv.CPP:5313)");
            return;
        }

        // Self-targeting-only simplification: without a team-leader system the
        // native leader-retags-another-ship branch (FedSrv.CPP:5323-5330) is
        // unreachable, so shipID must be the sender's own ship; otherwise silent.
        if (req.ShipID != ship.Id)
        {
            ConsoleLog.Write($"[setwingid] {from} ship {ship.Id}: SET_WINGID for ship {req.ShipID} != own ship — ignored (self-targeting-only, FedSrv.CPP:5323)");
            return;
        }

        // Native TrapHackBoot(pfmSW->wingID < c_widMax) (FedSrv.CPP:5319);
        // c_widMax = 10 (igc.h:505). Negative ids (= NA) also rejected.
        if (req.WingId < 0 || req.WingId >= SetWingId.WidMax)
        {
            ConsoleLog.Write($"[setwingid] {from} ship {ship.Id}: wingID {req.WingId} out of range [0,{SetWingId.WidMax}) — ignored (native bound, FedSrv.CPP:5319)");
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
            ConsoleLog.Write($"[orderchange] {from} ship {ship.Id}: {(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — ignored (native gate, FedSrv.CPP:7266)");
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
            ConsoleLog.Write($"[treasureack] {from} ship {ship.Id}: {(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — ignored (native gate, FedSrv.CPP:6096)");
            return;
        }
        if (ship.PendingTreasureOffer is not { } offer)
        {
            ConsoleLog.Write($"[treasureack] {from} ship {ship.Id}: no pending part-treasure offer — ignored (native gate GetTreasureObjectID()!=NA, FedSrv.CPP:6097)");
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
                // P314 — the MountPart cap (Ship.cs) refuses a fresh mount once
                // a ship is at the wire's 255-part ceiling; log and drop the
                // buffered offer instead of adding a 256th part that would
                // crash ExportShip.Encode (the teammate-export path).
                if (ship.MountPart(partType, offer.Amount, mount, ExpandedPartData.FullyMounted) is null)
                {
                    Console.WriteLine($"[treasureack] {from} ship {ship.Id}: REFUSED fresh mount of part {offer.PartTypeId} at mount {mount} — ship already at the 255-part wire ceiling; offer dropped");
                    ship.PendingTreasureOffer = null;
                    return;
                }
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
            ConsoleLog.Write($"[boardship] ship {boarder.Id} tried to board itself — refused (pship != pshipBoard, FedSrv.CPP:2332)");
            return false;
        }

        // pfsShipBoard->IsPlayer() (FedSrv.CPP:2340) — the carrier must be a
        // peer-owned (player) ship in this port.
        if (!_peerShips.Any(kv => ReferenceEquals(kv.Value.Ship, carrier)))
        {
            ConsoleLog.Write($"[boardship] ship {boarder.Id} -> {carrier.Id}: carrier is not a player ship (FedSrv.CPP:2340) — refused");
            return false;
        }

        // Both docked: the native precondition (FedSrv.CPP:2342-2345).
        if (boarder.StationId is null || carrier.StationId is null)
        {
            ConsoleLog.Write($"[boardship] ship {boarder.Id} -> {carrier.Id}: boarder and carrier must both be docked (FedSrv.CPP:2342-2345) — refused");
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
            ConsoleLog.Write($"[leaveship] ship {boarder.Id} from {carrier.Id}: carrier not docked — leave refused (FedSrv.CPP:2285)");
            return false;
        }
        if (boarder.ParentShip is not { } parent || !ReferenceEquals(parent, carrier))
        {
            ConsoleLog.Write($"[leaveship] ship {boarder.Id} from {carrier.Id}: not actually boarded on it — leave refused");
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
            ConsoleLog.Write($"[ripcord] {from} ship {ship.Id}: {(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — request ignored (gate FedSrv.CPP:7717)");
            return;
        }
        if (ship.SideId is not { } side || side < 0)
        {
            ConsoleLog.Write($"[ripcord] {from} ship {ship.Id}: side {ship.SideId?.ToString() ?? "(none)"} < 0 — request ignored (gate FedSrv.CPP:7717)");
            return;
        }
        if (ship.ParentShip is not null)
        {
            ConsoleLog.Write($"[ripcord] {from} ship {ship.Id}: boarded (parent {ship.ParentShip.Id}) — request ignored (gate FedSrv.CPP:7718)");
            return;
        }
        if (ship.Sector is null || ship.StationId is not null)
        {
            ConsoleLog.Write($"[ripcord] {from} ship {ship.Id}: {(ship.Sector is null ? "no cluster" : "docked")} — request ignored (gate GetCluster(), FedSrv.CPP:7719)");
            return;
        }
        if (ship.HullType.HasCapability(DataHullTypeIGC.HabmNoRipcord))
        {
            ConsoleLog.Write($"[ripcord] {from} ship {ship.Id}: hull {ship.HullType.HullId} has c_habmNoRipcord — request ignored (gate FedSrv.CPP:7721)");
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
            ConsoleLog.Write($"[ripcord] ship {ship.Id}: no valid anchor in sector {target.Id} -> RIPCORD_DENIED(186) requester-only");
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
        MoveShipToSector(rescuee, destSector);   // P307: real list move (old.Remove → dest.Add → Sector), not just the back-ref
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
        Notify(note);
    }

    /// <summary>
    /// P301 — the port of <c>FedSrvSiteBase::RescueShipEvent</c> for the
    /// station-rescuer case (<c>FedSrv.CPP:13033-13074</c> with
    /// <c>pshipRescuer == NULL</c>), driven by
    /// <c>Sector.ResolveStationRescueEvents</c> (via <c>DrainStationRescues</c>).
    /// A drifting <c>c_habmLifepod</c> that reached a same-side station whose
    /// type carries <c>c_sabmRescue</c> is docked at that station — this is the
    /// real native mechanic behind native's OT_station collision branch
    /// (<c>shipIGC.cpp:862-867</c>) and the counterpart to the P115 ship-ship
    /// rescue (<see cref="PerformRescue"/>).
    /// </summary>
    /// <remarks>
    /// <para><b>No wire.</b> Native's NULL-rescuer branch performs the placement
    /// with <b>no</b> <c>PLAYER_RESCUED</c> message and no score/Steam award —
    /// the whole wire block (including the rescuer connection send) sits inside
    /// <c>if (pshipRescuer)</c> (<c>FedSrv.CPP:13037-13057</c>). A station is
    /// not a ship with a connection or a score, so none of that applies. This
    /// method therefore only does the placement half: dock the pod at the
    /// station.</para>
    /// <para><b>Placement / rescue-point locality.</b> Native then finds the
    /// nearest friendly <c>c_sabmRestart</c> station and docks there
    /// (<c>FedSrv.CPP:13060-13076</c>). The pod is, by construction of the
    /// detection pass, physically AT this same-side <c>c_sabmRescue</c> station
    /// — a friendly, reachable base — so docking it at that very station is the
    /// local, faithful equivalent (the native search would resolve to a nearby
    /// friendly restart base in practice). Docking latches <c>Ship.StationId</c>,
    /// which is exactly what cancels the P279 oxygen timer
    /// (<c>Ship.OnUpdate</c>'s <c>StationId is null</c> conjunct,
    /// <c>Ship.cs:5538</c>), so a rescued pod stops burning oxygen.</para>
    /// <para><b>Ship-station idempotence.</b> The dock pass latches
    /// <c>StationId</c>, so a pod re-detected on a later tick is re-docked to
    /// the same station harmlessly (same as PerformRescue's direct dock).</para>
    /// </remarks>
    private void PerformStationRescue(Ship rescuee, Station station)
    {
        var mission = rescuee.Mission;
        if (mission is null)
        {
            Console.WriteLine($"[rescue] lifepod ship {rescuee.Id} reached station {station.StationId} but has no mission — station rescue dropped");
            return;
        }

        // Dock the pod at the rescue station — the same direct-dock relocation
        // core PerformRescue/PerformTeleport use (move to the station's sector,
        // latch StationId for the pod and any boarded children, and point the
        // owning peer's LiveShip.Sector snapshot at the station's cluster so the
        // next SHIP_STATUS/SINGLE_SHIP_UPDATE read the right sector).
        var destSector = mission.Sectors.First(s => s.Stations.Contains(station.Data));
        MoveShipToSector(rescuee, destSector);   // P307: real list move (old.Remove → dest.Add → Sector), not just the back-ref
        foreach (var kid in rescuee.ChildShips)
            kid.StationId = station.StationId;
        rescuee.StationId = station.StationId;
        foreach (var kv in _peerShips)
            if (ReferenceEquals(kv.Value.Ship, rescuee))
                _peerShips[kv.Key] = kv.Value with { Sector = destSector };

        var note = $"[rescue] lifepod ship {rescuee.Id} rescued by station {station.StationId} '{station.Data.Name}' (c_sabmRescue) -> docked; no PLAYER_RESCUED (native NULL-rescuer, FedSrv.CPP:13037); oxygen timer cancelled by dock";
        Console.WriteLine(note);
        Notify(note);
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

    /// <summary>P294 — broadcast <c>EXPORT(26)/OT_buildingEffect</c> when a new
    /// in-progress building effect starts (a Builder committed to constructing
    /// a station at an asteroid) — the port of
    /// <c>FedSrvSiteBase::CreateBuildingEffect</c>'s broadcast half
    /// (<c>FedSrv.CPP:13733-13767</c>), which exports the fresh effect to every
    /// side that can see the build. This port has no per-side SeenBySide
    /// tracking for building effects (the DESTROYED sibling is whole-mission
    /// too, <see cref="BroadcastBuildingEffectDestroyed"/>), so the creation is
    /// broadcast to every peer regardless of side — a strict superset of
    /// native's audience, harmless to the client (its draw loop filters to the
    /// local player's own sector by <c>ClusterId</c>). The message carries the
    /// full <see cref="DataBuildingEffectExport"/> so the client can render the
    /// in-progress visual and reconstruct native's 4-phase animation from
    /// <c>timeStart</c>. The visual colour is native's hardcoded building-effect
    /// tint <c>Color(50/255, 28/255, 146/255)</c> (<c>buildingeffectigc.h:93</c>,
    /// which ignores the side colour via the commented-out <c>//*pcolor</c>).
    /// Called from the <see cref="DrainBuildingEffectCreations"/> drain in the
    /// same tick slot as the interruption drain. Native sends <b>no</b>
    /// periodic progress and nothing on completion (the client self-expires at
    /// <see cref="BuildingEffect.BuildDuration"/>); this port mirrors that.</summary>
    private void BroadcastBuildingEffectCreated(BuildingEffect effect)
    {
        uint timeStart = (uint)TickIndex;
        var msg = ExportBuildingEffect.FromBuildingEffect(
            effect, timeStart, effect.Builder.Position,
            50f / 255f, 28f / 255f, 146f / 255f, 1f);
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        int n = 0;
        foreach (var kv in _peerShips)   // whole-mission: every peer, regardless of side
        {
            SendPeer(kv.Key, packet);
            n++;
        }
        Console.WriteLine($"[build] building effect started on asteroid {effect.Target.Data.AsteroidId} (station {effect.StationType.Name}) -> EXPORT(26)/OT_buildingEffect({ExportBuildingEffect.OtBuildingEffect}) whole-mission to {n} peer(s)");
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
        Notify(note);
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
        // P307 hardening (ghost-ship sector-membership desync): make the
        // relocation exact-once-safe so every call site converges on the same
        // invariant — the ship ends up in exactly the target sector's _models
        // list (not its old one's, and not none) with the Sector back-ref in
        // sync. This is a strict superset of the original logic and is
        // behavior-identical for the ripcord callers: it additionally
        // tolerates a ship that a death/relocation pass has ALREADY removed
        // from its old sector's list (the remove is then a harmless no-op)
        // and a same-sector move where the ship may already be present in the
        // target (the Contains guard prevents a double-add).
        var old = ship.Sector;
        if (old is not null && !ReferenceEquals(old, target))
            old.Remove(ship);
        if (!target.Models.Contains(ship))
            target.Add(ship);
        ship.Sector = target;
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
        // Equal -> no turret slots at all (observer). The `>=` (rather than `==`)
        // also swallows an INVERTED range (MaxFixedWeapons > MaxWeapons, possible
        // on a corrupt/hand-edited core — review H1's data-dependent trigger)
        // into the observer case instead of handing an empty range to the walk.
        if (minMount >= maxMount)
            return null;                                  // no turret slots at all → observer

        // P313 / review H1: the search-start heuristic reuses the boarder's
        // PREVIOUS slot so re-boarding the SAME carrier keeps the same turret,
        // but a stale TurretId inherited from a previous, LARGER carrier can lie
        // outside THIS carrier's [minMount, maxMount) range. Native trusts the
        // assert `GetParentShip() != NULL` (FedSrv.CPP:2359) to rule that out;
        // the port (like native, FedSrv.CPP:2341-2345) lets a docked boarder
        // re-board a DIFFERENT carrier without leaving first, so that id can go
        // out of range and the loop's wrap `turret >= maxMount -> minMount` never
        // revisits the start -> infinite spin (server-wide freeze, unrecoverable).
        // Clamping the start INTO the real range revalidates the stale id against
        // the new carrier's slots (the root cause) and keeps the full-loop
        // termination (turret cycling back to startMount) reachable.
        int startMount = Math.Clamp(boarder.TurretId ?? (maxMount - 1), minMount, maxMount - 1);
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
    /// P110 + P283 — the port's decision for <c>station.CanBuy(baseHullType)</c>,
    /// i.e. whether a station can sell the player's current hull
    /// (<c>FedSrv.CPP:7076</c>). Native: <c>CanBuy(b) = b->GetRequiredTechs()
    /// &lt;= (m_myStationType.GetLocalTechs() | side.GetTechs())</c>
    /// (<c>stationIGC.h:383-391</c>, bitwise containment of the buyable's
    /// required-tech mask in the union of the station's local techs and the
    /// side's techs), where the station's local techs are its station type's
    /// <c>ttbmLocal</c> (<c>stationtypeigc.h:159-161</c>) and the side's techs
    /// are the runtime-accreted mask <c>side.GetTechs() = ttbmBuildingTechs |
    /// ttbmDevelopmentTechs</c> (<c>sideigc.h:133-136</c>). <b>P283 wiring.</b>
    /// This port has <b>no</b> <c>BuildingTechs</c> field
    /// (<see cref="Side.DevelopmentTechs"/>' doc notes the other,
    /// <c>BuildingTechs</c>, is out of scope) — <b>but</b> it <b>does</b> have
    /// the live, mutation-tracked <see cref="Side.DevelopmentTechs"/> (P189),
    /// seeded from the civ's base techs at side construction and grown by
    /// development completion — so the side's whole native <c>GetTechs()</c>
    /// term is the port's <c>side.DevelopmentTechs</c>, which (being seeded
    /// from and always a super-set of the civ's base mask) subsumes the
    /// P110-era static <c>civ.TtbmBaseTechs</c> approximation this method
    /// formerly used. <c>bNewHull</c> therefore reduces to
    /// <c>hull.TtbmRequired ⊆ (stationType.TtbmLocal | side.DevelopmentTechs)</c>
    /// (all 50-byte / 400-bit tech masks, <c>DataHullTypeIGC.cs:21</c>,
    /// <c>DataStationTypeIGC.cs:29</c>, <c>TechTreeBitMask.cs</c>).
    /// Measured on PCore001.igc: a faction's garrison sells its starter hulls
    /// (<c>bNewHull=false</c>), rejects advanced hulls until their governing
    /// development is researched (<c>bNewHull=true</c> before,
    /// <c>bNewHull=false</c> after <c>CompleteDevelopment</c> grows
    /// <c>Side.DevelopmentTechs</c>) — exactly the pair the task's
    /// verification scenarios exercise.
    /// </summary>
    private static bool StationSellsHull(DataStationTypeIGC st, Side side, DataHullTypeIGC hull)
    {
        var need = hull.TtbmRequired;
        var local = st.TtbmLocal;
        var sideTechs = side.DevelopmentTechs.AsSpan();
        for (int i = 0; i < need.Length && i < local.Length; i++)
            if ((need[i] & ~(local[i] | (i < sideTechs.Length ? sideTechs[i] : (byte)0))) != 0) return false;
        for (int i = local.Length; i < need.Length; i++)
            if (need[i] != 0 && (i >= sideTechs.Length || (need[i] & ~sideTechs[i]) != 0)) return false;
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

    /// <summary>Allocate the next ship id, skipping any id currently held by a
    /// live ship (P316, option (b)). The ShipID wire field is a 16-bit
    /// <c>short</c> (native <c>typedef ObjectID ShipID</c>, <c>Igc/igc.h:480</c>;
    /// <c>ObjectID</c> at <c>igc.h:477</c>), so the space cannot be widened
    /// without breaking wire compatibility. Wrapping blindly would let a newly
    /// validated logon receive an id still held by a live ship, mis-directing
    /// <c>POSITIONACK</c>/<c>ORDER_CHANGE</c>/boarding lookups
    /// (<see cref="FindConnectionOfShip"/>/<see cref="FindSameSideShip"/>) and
    /// stale-hitting the never-pruned teammate-export cache (review findings H4
    /// and M1). Rather than wrap, walk forward past any still-live id at
    /// allocation time. Only called for validated logons (and NPC spawns), so
    /// an unauthenticated LOGONREQ flood burns no ids.</summary>
    private short AllocateShipId()
    {
        for (int attempt = 0; attempt < 65536; attempt++)
        {
            short candidate = unchecked((short)_nextShipId++);
            if (!IsShipIdLive(candidate))
                return candidate;
        }
        // Pathological: the entire 16-bit space is occupied. Hand out the last
        // visited value rather than spinning forever.
        return unchecked((short)(_nextShipId - 1));
    }

    /// <summary>Whether <paramref name="id"/> is currently held by any live
    /// ship: a seated peer (<see cref="_peerShips"/>), a peer still awaiting
    /// side selection (its <see cref="PendingSideSelect.ShipId"/> is reserved
    /// even though no <c>Ship</c> object exists yet), or any NPC ship placed in
    /// a mission sector (P147, no <c>_peerShips</c> entry).</summary>
    private bool IsShipIdLive(short id)
    {
        foreach (var kv in _peerShips)
            if (kv.Value.Ship.Id == id)
                return true;
        foreach (var kv in _pendingSideSelect)
            if (kv.Value.ShipId == id)
                return true;
        foreach (var mission in _missions)
            foreach (var sector in mission.Sectors)
                foreach (var model in sector.Models)
                    if (model is Ship s && s.Id == id)
                        return true;
        return false;
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
        {
            // P261: the launch repair/reload — native fires
            // CstationIGC::RepairAndRefuel in ChangeStation's undock branch
            // (FedSrv.CPP:13189-13191), i.e. at LAUNCH, gated on the DOCKED
            // station's type capabilities. Both undock arms (real-peer
            // PerformUndock and the docked-NPC launch arm of HandleOrderChange)
            // converge here, so this single call restores every launching ship.
            // The station's StationTypeId is resolved to its DataStationTypeIGC
            // from the mission core exactly as FindNearestFriendlyStation does
            // (Server's existing core-resolution idiom); an unresolvable type
            // can't gate capabilities, so the restore is skipped (defensive —
            // every real map station has a resolvable type).
            if (ship.Mission?.Core.StationTypes.TryGetValue(stFound.StationTypeId, out var dtype) == true)
                ship.RepairAndRefuel(dtype);

            RepositionForLaunch(ship, stFound);
        }

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
        Notify(undockNote);      // spectator event feed (same text)
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
            ConsoleLog.Write($"[teleport] {from} ship {ship.Id}: request for station {targetStationId} rejected — {why} -> TELEPORT_ACK stationID=NA (FedSrv.CPP:7067-7073)");
            return;
        }

        // Valid — acknowledge with the real station id (FedSrv.CPP:7075-7077),
        // and the port's bNewHull = !CanBuy(current hull). stype is guaranteed
        // present past the restartOk guard (which itself requires the lookup
        // to succeed), so the re-lookup here is provably safe. The ship's side
        // provides the runtime side-tech term (native side.GetTechs() = the
        // port's Side.DevelopmentTechs, P283) StationSellsHull consults.
        mission.Core.StationTypes.TryGetValue(target!.Value.StationTypeId, out var validType);
        var side = mission.Sides.FirstOrDefault(s => s.Id == ship.SideId!.Value);
        if (side is null)
        {
            ConsoleLog.Write($"[teleport] {from} ship {ship.Id}: side {ship.SideId} not in mission — cannot compute bNewHull; teleport rejected");
            var reject = new TeleportAck { StationId = -1, BNewHull = false };
            SendPeer(from, FedPacket.Pack(new[] { reject.Encode() }));
            return;
        }
        bool bNewHull = !StationSellsHull(validType, side, ship.HullType);
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
        MoveShipToSector(ship, destSector);      // P307: real list move (old.Remove → dest.Add → Sector), not just the back-ref
        foreach (var kid in ship.ChildShips)          // empty in the bNewHull case (booted above)
            kid.StationId = targetStationId;
        ship.StationId = targetStationId;

        // Keep the owning peer's broadcast bookkeeping coherent — SHIP_STATUS
        // frames derive Sector from the snapshot, so point it at the station's
        // cluster like P105 does (Server.cs:3469-3470).
        _peerShips[from] = _peerShips[from] with { Sector = destSector };

        var note = $"[teleport] ship {ship.Id} docked at station {targetStationId} '{target.Value.Name}' in sector '{destSector.Name}'";
        Console.WriteLine(note);
        Notify(note);
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
            // P314 — the MountPart cap (Ship.cs) refuses a fresh mount once a
            // ship is at the wire's 255-part ceiling; log and count it skipped
            // instead of letting a 256th part crash ExportShip.Encode later.
            if (ship.MountPart(partType, p.Amount, p.MountId, p.FractionMounted) is null)
            {
                ConsoleLog.Write($"[loadout] {from} ship {ship.Id}: refused mounting part {p.PartId} at mount {p.MountId} — ship already at the 255-part wire ceiling; counted as skipped");
                skipped++;
                continue;
            }
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
        Notify(note);

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

    /// <summary>
    /// P285 — apply a received <see cref="SetBuilderTarget"/> (fmid 201): set the
    /// peer's live ship <see cref="Ship.BuilderStationType"/> to the player-chosen
    /// station type — the purchase-time lever the hangar shop (P268/P284) had no
    /// way to set. Port of native's per-ship creation-time <c>baseObjectID</c>
    /// wiring (<c>m_pbaseData = pmission->GetStationType(dataShip->baseObjectID)</c>,
    /// <c>Igc/shipIGC.cpp:109</c>, only for <c>c_ptBuilder</c>) expressed as the
    /// plain settable field this port already owns (<c>Sim/Ship.cs:1040</c>); this
    /// port doesn't need a fixed hull-catalog entry per target type, so it can be
    /// set any time before the ship starts building. Two validations before
    /// honouring, both soft-fail (log-and-ignore, per port convention, no
    /// disconnect): (1) the named hull must resolve in the core AND carry the
    /// <c>c_habmBuilder</c> (0x8000) capability — a player may only assign a build
    /// target when purchasing a Builder-class hull (native <c>c_habmBuilder</c>,
    /// <c>Igc/igc.h:741</c>); (2) the named station type must resolve in the core.
    /// Runs on the sim thread (called from ProcessDatagram).
    /// </summary>
    private void SetBuilderTargetOnShip(ConnectionId from, LiveShip live, SetBuilderTarget req)
    {
        var ship = live.Ship;
        var mission = ship.Mission;
        if (mission is null)
        {
            ConsoleLog.Write($"[builder] {from} ship {ship.Id}: no mission — SET_BUILDER_TARGET ignored");
            return;
        }
        if (!mission.Core.HullTypes.TryGetValue(req.HullId, out var hull))
        {
            ConsoleLog.Write($"[builder] {from} ship {ship.Id}: SET_BUILDER_TARGET names unknown hull {req.HullId} — ignored");
            return;
        }
        if (!hull.HasCapability(DataHullTypeIGC.HabmBuilder))
        {
            ConsoleLog.Write($"[builder] {from} ship {ship.Id}: SET_BUILDER_TARGET hull {req.HullId} '{hull.Name}' is not a Builder (c_habmBuilder) hull — ignored");
            return;
        }
        if (!mission.Core.StationTypes.TryGetValue(req.StationTypeId, out var stationType))
        {
            ConsoleLog.Write($"[builder] {from} ship {ship.Id}: SET_BUILDER_TARGET names unknown station type {req.StationTypeId} — ignored");
            return;
        }

        ship.BuilderStationType = stationType;
        var note = $"[builder] ship {ship.Id} (player {from}) -> BuilderStationType = '{stationType.Name}' (type {stationType.StationTypeId}), hull '{hull.Name}' (c_habmBuilder)";
        Console.WriteLine(note);
        Notify(note);
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
        var targets = new List<ConnectionId>();
        foreach (var kv in _peerShips)
        {
            bool isShooter = ReferenceEquals(kv.Value.Ship, shooter);
            bool isVisibleTeammate = !isShooter
                                     && kv.Value.Ship.SideId == shooterSide
                                     && _teammateExportSent.Contains((kv.Key, shooter.Id));
            if (isShooter || isVisibleTeammate)
            {
                SendPeer(kv.Key, packet);
                targets.Add(kv.Key);
                n++;
            }
        }
        Console.WriteLine($"[missile] ship {shooter.Id} expired missile {m.Id} -> MISSILE_DESTROYED(83) " +
                          $"pos {m.Position} sector {m.OwnerSector.Id} -> {n} same-side peer(s)");

        // P263 — the missile's impact/expiry blast VFX to the same audience.
        // Native Explode() plays the c_etMissile explosion wherever the missile
        // terminates (missileigc.cpp:531, scale = GetRadius()); the port emits
        // it server-side as the explosion category + position + velocity so the
        // fireball sprites ride the missile's momentum.
        if (targets.Count > 0)
            SendExplosionEvent(targets, ExplosionType.Missile, m.OwnerSector.Id, m.Position, m.Velocity, m.MissileType.Radius);
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
        var targets = new List<ConnectionId>();
        foreach (var kv in _peerShips)
        {
            bool isLayer = ReferenceEquals(kv.Value.Ship, layer);
            bool isVisibleTeammate = !isLayer
                                     && kv.Value.Ship.SideId == layerSide
                                     && _teammateExportSent.Contains((kv.Key, layer.Id));
            if (isLayer || isVisibleTeammate)
            {
                SendPeer(kv.Key, packet);
                targets.Add(kv.Key);
                n++;
            }
        }
        Console.WriteLine($"[mine] ship {layer.Id} expired mine {m.Id} -> MINE_DESTROYED(84) " +
                          $"pos {m.Position} sector {m.OwnerSector.Id} -> {n} same-side peer(s)");

        // P263 — the mine's detonation blast VFX to the same audience that saw
        // the MINE_DESTROYED. Native plays the c_etMine explosion wherever the
        // mine blows (the deploy-side AddExplosion at shipIGC.h:2024 passes the
        // literal scale 1.5f); the port emits it server-side as the explosion
        // category + position.
        if (targets.Count > 0)
            SendExplosionEvent(targets, ExplosionType.Mine, m.OwnerSector.Id, m.Position, Vector3.Zero, 1.5f);
    }

    /// <summary>
    /// P263 — broadcast a real <see cref="Wire.ExplosionEvent"/>(207) to the
    /// given peers so the MonoGame client renders the blast VFX. This is the
    /// port's server-authoritative replacement for native's
    /// <c>IclusterSite::AddExplosion(position, forward, right, velocity, scale,
    /// type)</c> callback (<c>clusterIGC.cpp:407</c>): the sim decides WHAT died
    /// (the type category) and WHERE, and the thin render client plays the
    /// matching sprite preset. Runs on the sim thread (same one that owns
    /// _peerShips and the caller's targets), so no extra locking.
    /// </summary>
    /// <remarks><c>scale</c> is the native 2 * GetRadius() audio-tier value
    /// (<c>trekigc.cpp:183</c>, tiers &gt;100 / &gt;10 / else at
    /// <c>trekigc.cpp:94-104</c>); the rendered size comes from the type preset,
    /// not this. Logged for the live-proof seam.</remarks>
    private void SendExplosionEvent(
        IEnumerable<ConnectionId> targets,
        int type,
        short clusterId,
        Vector3 position,
        Vector3 velocity,
        float scale)
    {
        var msg = new ExplosionEvent
        {
            Type = type,
            ClusterId = clusterId,
            Scale = scale,
            Position = position,
            Velocity = velocity,
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        int n = 0;
        foreach (var t in targets) { SendPeer(t, packet); n++; }
        Console.WriteLine($"[explosion] type {type} cluster {clusterId} scale {scale} at {position} -> EXPLOSION_EVENT(207) -> {n} peer(s)");
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
        Notify(note);                     // spectator event feed (same text)

        foreach (var t in targets)
            SendPeer(t, packet);

        // P263 — the station's destruction blast VFX. Native picks the category
        // by the station's victory-flag capability on STATION_DESTROYED
        // (appmsg.cpp:525-528: c_sabmFlag ? c_etLargeStation : c_etSmallStation)
        // and the port emits it server-side to the same whole-mission audience.
        // The scale is the native 2 * GetRadius() audio tier; the rendered size
        // comes from the type preset (350u large / 100u small-station), so a
        // nominal station radius suffices here.
        int stationType = station.Type.HasCapability(DataStationTypeIGC.SabmFlag)
            ? ExplosionType.LargeStation
            : ExplosionType.SmallStation;
        SendExplosionEvent(targets, stationType, station.ClusterId, station.Center, Vector3.Zero, 100f);
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
        Notify(note);                   // spectator event feed (same text)

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
            Notify(note);

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
    /// P302 — the server half of the prosperity/artifact win conditions, driven
    /// by the <see cref="ServerHost.ProsperityArtifactComputed"/> seam the
    /// world mission raises after a team-money development completes
    /// (<see cref="CompleteDevelopment"/>) or an artifact is secured
    /// (<see cref="OnShipDocked"/>), matching native's
    /// <c>FedSrvSiteBase::DevelopmentCompleted</c> win (FedSrv.CPP:11274-11279)
    /// and the artifact leg of <c>CheckForVictoryByFlags</c>
    /// (fsmission.cpp:3888-3894). It mirrors <see cref="OnVictoryByStationsComputed"/>
    /// exactly: on a change it broadcasts a whole-mission GAME_STATE(155) (its
    /// <c>nFlags</c>/<c>nArtifacts</c> arrays carry the real counters), and on
    /// a winner it declares the mission over via the single shared P178
    /// <see cref="DeclareGameOver"/> path. Runs on the sim thread (same one
    /// that owns <c>_peerShips</c>), so no extra locking.
    /// </summary>
    private void OnProsperityArtifactComputed(Mission mission, bool changed, Side? winner)
    {
        if (changed)
        {
            var msg = GameState.ForMission(mission);
            byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

            // Whole-mission audience, scoped to THIS mission (P191 idiom).
            var targets = _peerShips
                .Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission))
                .Select(kv => kv.Key)
                .ToArray();

            var note = $"[gamestate] mission #{_missions.IndexOf(mission)} -> GAME_STATE(155) " +
                       $"nFlags[{string.Join(",", msg.NFlags)}] nArtifacts[{string.Join(",", msg.NArtifacts)}] " +
                       $"(conquest/territory unchanged) -> {targets.Length} peer(s) [WHOLE MISSION]";
            Console.WriteLine(note);
            Notify(note);

            foreach (var t in targets)
                SendPeer(t, packet);
        }

        if (winner is not null && mission.Stage != MissionStage.Over)
        {
            string winnerName = winner.Name ?? $"side{winner.Id}";
            DeclareGameOver(mission, winner.Id,
                $"{winnerName} won by achieving the prosperity/artifact victory objective.");
        }
    }

    /// <summary>
    /// P303 — the server half of the flags/kills win conditions, driven by the
    /// <see cref="ServerHost.FlagsKillsComputed"/> seam the world mission raises
    /// after a captured enemy flag is secured (<see cref="OnShipDocked"/>) or a
    /// side earns a credited kill (<see cref="BroadcastKillShip"/>), matching
    /// native's flag leg of <c>CheckForVictoryByFlags</c> (fsmission.cpp:3883-3887,
    /// FedSrv.CPP:13149) and <c>CheckForVictoryByKills</c> (fsmission.cpp:3749-3760,
    /// FedSrv.CPP:11872). It mirrors <see cref="OnProsperityArtifactComputed"/>
    /// exactly: on a change it broadcasts a whole-mission GAME_STATE(155) (its
    /// <c>nFlags</c> array carries the real counters; native GAME_STATE has no
    /// kills field, so a kills-win change still broadcasts the current
    /// flags/artifacts verbatim), and on a winner it declares the mission over
    /// via the single shared P178 <see cref="DeclareGameOver"/> path. Runs on the
    /// sim thread (same one that owns <c>_peerShips</c>), so no extra locking.
    /// </summary>
    private void OnFlagsKillsComputed(Mission mission, bool changed, Side? winner)
    {
        if (changed)
        {
            var msg = GameState.ForMission(mission);
            byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

            // Whole-mission audience, scoped to THIS mission (P191 idiom).
            var targets = _peerShips
                .Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission))
                .Select(kv => kv.Key)
                .ToArray();

            var note = $"[gamestate] mission #{_missions.IndexOf(mission)} -> GAME_STATE(155) " +
                       $"nFlags[{string.Join(",", msg.NFlags)}] nArtifacts[{string.Join(",", msg.NArtifacts)}] " +
                       $"(conquest/territory unchanged) -> {targets.Length} peer(s) [WHOLE MISSION]";
            Console.WriteLine(note);
            Notify(note);

            foreach (var t in targets)
                SendPeer(t, packet);
        }

        if (winner is not null && mission.Stage != MissionStage.Over)
        {
            string winnerName = winner.Name ?? $"side{winner.Id}";
            DeclareGameOver(mission, winner.Id,
                $"{winnerName} won by achieving the flags/kills victory objective.");
        }
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

        // ── P278: reset the dying ship's own Kill Bonus on a REAL death ──
        // Native FedSrv.CPP:11783-11787: `if (bKilled) pship->SetExperience(0.0f)`.
        // AFS's "if your own pod is destroyed" ⇔ bKilled here: under this
        // port's always-enabled eject pods, a player ship only dies for real
        // when it IS a lifepod (or amount<0, unreachable in combat) — so
        // destroying the pilot's eject pod resets the streak to 0, exactly the
        // AFS trigger. The !bKilled eject branch (below) deliberately does NOT
        // reset it (an ejection is not a lost streak).
        if (bKilled)
            victim.ResetKillBonus();

        if (!bKilled && EjectSurvivor(victim, launcher, p1, p2))
        {
            // Genuinely different code path from P103/P105: the ship survives
            // in space as a drifting Lifepod hull. No KILL_SHIP(105), no
            // SHIP_DELETE(20), no respawn/ENTER_LIFEPOD(166) — and the ship is
            // NOT removed from the sector (no SetCluster(NULL)).
            return;
        }

        // ── P215: real kill/death + Elo combat-rating scoring ──
        // The Stage-1 wiring that finally makes PlayerScore reflect real
        // combat (the port of FedSrv.CPP's kill handling). It sits here —
        // after the P107 eject gate (an ejection is NOT a death: native calls
        // AddEjection, not AddDeath, in its !bKilled branch) and before the
        // observer-targets gate — because native counts the victim's death
        // regardless of who is watching (FedSrv.CPP:12026-12032).
        //
        // (1) Victim death — on EVERY real death of a player-owned ship
        // (native `if (pfsShip->IsPlayer()) pfsPlayer->AddDeath()`,
        // FedSrv.CPP:12026-12032; CFSPlayer::AddDeath → m_plrscore.AddDeath(),
        // fsship.h:50-53). This port's IsPlayer() is exactly "has an owning
        // peer in _peerShips", so an unowned/bot victim has no PlayerScore and
        // gets no death (drones get none in native either).
        if (owner is not null)
        {
            _peerShips[owner.Value].PlayerScore.AddDeath();
            Console.WriteLine($"[scoring] victim ship {victim.Id} death +1 (player peer)");
        }

        // P303 — the SIDE-level death counter (native `pside->AddDeath()`,
        // sideigc.h:466-473; the port's is under Side.Deaths). Unlike the
        // per-player death above (which only a peer-owned victim earns), the
        // side counter reflects the side itself regardless of ownership —
        // native's per-side m_data.nDeaths counts every one of the side's
        // ships dying (GetDeaths, sideigc.h:466-468). Increment it for the
        // victim's side whenever the victim has a real side here.
        if (victim.Mission is { } deathMission && victim.SideId is { } victimSideId)
        {
            var victimSide = deathMission.Sides.FirstOrDefault(s => s.Id == victimSideId);
            if (victimSide is not null)
                victimSide.Deaths++;
        }

        // (2) Killer kill-credit + combat-rating update — native awards this
        // only when the launcher is a real side-owned SHIP, the victim was not
        // a lifepod, and the killer is on a DIFFERENT side (FedSrv.CPP:
        // 11811-11821: `pcredit && !bLifepod && (psideCredit != pside)` then
        // `if (type == OT_ship) pfsshipCredit->AddKill() +
        // AdjustCombatRating(...)`). A suicide/environmental/team kill
        // therefore never grants kill credit or a rating swing — only the
        // victim's death (above) is counted, matching native. The `attacker !=
        // victim` guard is the self-kill case kept explicit (native covers it
        // via the same-side gate). Only the player-owned side is scored: a bot
        // attacker has no PlayerScore (no credit), and a bot victim has no
        // rating storage, so the two-object rating update requires BOTH sides
        // to be real peers (stated simplification of native, which carries a
        // score object even on drones — a bot victim's folded-in 0.0 rating is
        // skipped rather than fabricating a rating holder).
        if (launcher is Ship attackingShip &&
            attackingShip != victim &&
            !bLifepod &&
            attackingShip.SideId != victim.SideId &&
            victim.Mission is { } killMission &&
            killMission.Core.Constants.FloatConstants is { } fc &&
            fc.Length > PlayerScore.CombatRatingDivideIndex)
        {
            // The same _peerShips reverse-lookup used above for the victim's
            // `owner` — no second lookup mechanism.
            ConnectionId? killerOwner = null;
            foreach (var kv in _peerShips)
                if (ReferenceEquals(kv.Value.Ship, attackingShip)) { killerOwner = kv.Key; break; }

            if (killerOwner is not null)
            {
                var killerScore = _peerShips[killerOwner.Value].PlayerScore;
                killerScore.AddKill();
                Console.WriteLine($"[scoring] killer ship {attackingShip.Id} kill +1 (player peer)");

                if (owner is not null)
                {
                    PlayerScore.AdjustCombatRating(
                        fc[PlayerScore.CombatRatingAddIndex],
                        fc[PlayerScore.CombatRatingDivideIndex],
                        killerScore, _peerShips[owner.Value].PlayerScore);
                    Console.WriteLine("[scoring] combat rating adjusted on both sides");
                }
            }

            // ── P303: the SIDE-level kill + the kills (deathmatch) win ──
            // Native increments the credited side's kill counter here
            // (`psideCredit->AddKill()`, FedSrv.CPP:11868) then tests
            // CheckForVictoryByKills (`GetKills() >= nGoalTeamKills`,
            // fsmission.cpp:3749-3760) and ends the game on reaching it
            // (FedSrv.CPP:11871-11880). We bump the killer's side counter (the
            // P215 per-player kill already happened above) and, gated on
            // KillsEnabled so a non-deathmatch mission is unaffected, recompute
            // the win and raise the seam the host consumes (GAME_STATE + P178).
            var killerSide = killMission.Sides.FirstOrDefault(s => s.Id == attackingShip.SideId);
            if (killerSide is not null)
                killerSide.Kills++;
            if (killMission.KillsEnabled)
            {
                var (kWinner, kChanged) = killMission.RecomputeFlagsAndKills();
                if (kChanged || kWinner is not null)
                    killMission.Site.FlagsKillsComputed(killMission, kChanged, kWinner);
            }

            // ── P278: Kill Bonus credit on the killer SHIP ──
            // Native FedSrv.CPP:11824: `if ((pside != psideCredit) &&
            // !pside->AlliedSides(pside,psideCredit)) pcredit->AddExperience()`.
            // Operates on the credit ship's own KB counter (per-ship scope),
            // INDEPENDENT of peer-ness, so it sits outside the killerOwner block
            // (a bot killer still bumps its KB, faithful to native which acts on
            // the credit ship). Note this gate is STRICTER than AddKill's plain
            // cross-side test: KB does NOT rise on an ALLIED cross-side kill
            // ("#ALLY: kgjv dont up KB when FF is on - Imago 7/3/09"). The outer
            // guard already ensures attacker != victim, !lifepod, cross-side.
            if (!AlliedSides(killMission, attackingShip.SideId, victim.SideId))
                attackingShip.AddKillBonus();
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

        // P263 — the ship's death blast VFX to the same same-sector audience.
        // Native picks the category by the victim base hull's fighter capability
        // on KILL_SHIP (appmsg.cpp:2853-2857: c_habmFighter ? c_etSmallShip :
        // c_etBigShip) — the exact split the port reproduces here. Scale is the
        // native 2 * GetRadius() = hull Length (shipRadius = 0.5*Length).
        int shipType = victim.HullType.HasCapability(DataHullTypeIGC.HabmFighter)
            ? ExplosionType.SmallShip
            : ExplosionType.BigShip;
        SendExplosionEvent(targets, shipType, victim.Sector!.Id, victim.Position, victim.Velocity, victim.HullType.Length);

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
            FinalizeDeadShip(victim);   // P323 M6 — really leave the sector & mark dead (native deletes the drone, FedSrv.CPP:12084)
            return;
        }
        DataStationIGC? station = FindFriendlyStation(victim);
        if (station is null)
        {
            Console.WriteLine($"[lifepod] player ship {victim.Id} side {(victim.SideId?.ToString() ?? "(none)")} has no friendly station anywhere — no respawn (native FedSrv.CPP:12055 guard)");
            FinalizeDeadShip(victim);   // P323 M6 — the ghost-ship fix: remove the dead ship server-side (it used to keep flying/mining/broadcasting)
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
            FinalizeDeadShip(victim);   // P323 M6 — same ghost-ship finalization as the no-station branch
            return;
        }
        if (!mission.Core.HullTypes.TryGetValue(side.Civilization.Lifepod, out var lp))
        {
            Console.WriteLine($"[lifepod] player ship {victim.Id} side {victim.SideId}: lifepod hull {side.Civilization.Lifepod} not in core — no respawn");
            FinalizeDeadShip(victim);   // P323 M6 — same ghost-ship finalization as the no-station branch
            return;
        }

        MoveShipToSector(victim, destSector);   // P307: SetCluster — a REAL list move out of the old sector into the station's cluster (FedSrv.CPP:12045→12067), not just the back-ref
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
    /// P323 M6 — finalize a true death server-side when there is no respawn
    /// ("stays dead"). Native's KillShipEvent removes a dying player ship from
    /// its cluster unconditionally (<c>pship-&gt;SetCluster(NULL)</c>,
    /// FedSrv.CPP:12045) BEFORE deciding whether a station exists to respawn at
    /// (and a bot ship is deleted outright, FedSrv.CPP:12084); the port's combat
    /// death path achieves the same by zeroing the hull fraction in
    /// <see cref="Ship.ReceiveDamage"/> (Ship.cs:3199) and removing the dead
    /// ship in <see cref="Sector.ResolveCombatCollisions"/>
    /// (<c>if (ship.Dead) _models.Remove(ship)</c>, Sector.cs:1579). The
    /// oxygen-death / direct-KillShipEvent path (Ship.OnUpdate) performs NEITHER
    /// of those, and BroadcastKillShip's "no respawn, stays dead" branches used
    /// to log and return WITHOUT removing anything — leaving the ghost ship
    /// alive in its sector's <c>_models</c>, still flying / mining / shooting /
    /// broadcast every tick (a permanent server/client desync). This helper
    /// applies the missing finalization (match the port's death contract: mark
    /// Dead and leave the sector) so the "stays dead" log's claim is actually
    /// true. Idempotent: safe on an already-dead / already-removed ship.
    /// </summary>
    private static void FinalizeDeadShip(Ship ship)
    {
        ship.SetFraction(0f);                 // Dead == true (the port's death contract)
        if (ship.Sector is { } sec) sec.Remove(ship);   // leave the sector's _models (native SetCluster(NULL))
        ship.Sector = null;
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

        // P308 — an ejected lifepod must be born ALIVE, not dead. Ship.ReceiveDamage
        // zeroes _fraction BEFORE raising KillShipEvent (Ship.cs:3131-3132), and
        // ChangeHull/SetBaseHullType is documented NOT to reset it ("a lifepod is
        // still the dead ship", Ship.cs:194-196), so without this the pod is born
        // with Dead == true and stays that way forever — silently disabling every
        // downstream gate the eject mechanic exists for (Sector.ResolveRescueEvents
        // requires !s.Dead, Sector.cs:772; GetShipUpdateCandidates skips dead ships,
        // Sector.cs:410; the combat/mine/explosion passes all skip dead ships). The
        // port's faithful stand-in for native's behaviour: native keeps an ejected
        // pod operational (its rescue is collision-based with NO Dead gate,
        // shipIGC.cpp:1033-1036, and GetShipUpdateCandidates/combat don't filter by
        // fraction-death) without ever naming an eject fraction — so restoring the
        // pod to FULL hull is the natural "fresh intact pod" value, and it is
        // native's own canonical "make a ship whole" number (SetFraction(1.0f),
        // FedSrv.CPP:12759/12950, 12954; shipIGC.h:311-318).
        victim.SetFraction(1.0f);

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
    private void OnNotable(string text) => Notify(text);

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
                // enemy flag. The team-lobby sentinel (-2) cannot arise from a
                // real-side flag treasure's TreasureId (real sides id >= 0), but
                // P302 makes it reachable via a NEUTRAL artifact treasure whose
                // TreasureId is the sentinel — the artifact win is P302-scoped
                // and gated on mission.ArtifactEnabled below.
                if (sidFlag == Side.ArtifactSentinelFlag)
                    side.Artifacts++;
                else
                    side.Flags++;
                Console.WriteLine($"[flag] ship {ship.Id} secured side {sidFlag}'s flag at own-side station {ss} -> side {shipSide} flags={side.Flags} artifacts={side.Artifacts}");

                // P302 — the artifact win: securing a neutral artifact (carried
                // value == SIDE_TEAMLOBBY sentinel) increments Side.Artifacts,
                // then CheckForVictoryByFlags scores the game to the first side
                // reaching nGoalArtifactsCount (fsmission.cpp:3888-3894). Gated
                // on ArtifactEnabled so a non-artifact mission's flag-secure is
                // unaffected; the recompute raises the seam the host consumes.
                if (sidFlag == Side.ArtifactSentinelFlag && mission.ArtifactEnabled)
                {
                    var (aWinner, aChanged) = mission.RecomputeProsperityAndArtifacts();
                    if (aChanged || aWinner is not null)
                        mission.Site.ProsperityArtifactComputed(mission, aChanged, aWinner);
                }
                // P303 — the FLAGS win: securing a captured ENEMY flag (carried
                // value is a real enemy side's id, != the neutral sentinel)
                // increments Side.Flags, then the flag leg of CheckForVictoryByFlags
                // scores the game to the first side reaching nGoalFlagsCount
                // (fsmission.cpp:3883-3887; the secure is the FedSrv.CPP:13149
                // call site). Gated on FlagsEnabled so a non-flags mission's
                // flag-secure is unaffected; the recompute raises the seam.
                if (sidFlag != Side.ArtifactSentinelFlag && mission.FlagsEnabled)
                {
                    var (fWinner, fChanged) = mission.RecomputeFlagsAndKills();
                    if (fChanged || fWinner is not null)
                        mission.Site.FlagsKillsComputed(mission, fChanged, fWinner);
                }
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

}
