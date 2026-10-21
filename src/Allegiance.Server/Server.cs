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
    // P378 — the resolved lifepod oxygen-endurance override (seconds), from
    // config.LiveSettings (explicit lifepodEnduranceSeconds, else the debug
    // default when debugMode is on, else null = the core's native 300 s).
    // Applied to every server-created Ship (SpawnDefaultShip / SpawnNpcShip),
    // which Ship.ChangeHull/LifepodEnduranceFraction then read when a hull
    // becomes a drifting Lifepod.
    private double? _lifepodEnduranceOverrideSeconds;
    private ServerHost _host = null!;
    private List<Mission> _missions = null!;
    // P439 — the loaded static core and the shared hull-landing-bay resolver
    // are kept after boot so DeclareGameOver can rebuild a finished mission
    // from the SAME inputs InstantiateGames used. Native regenerates the world
    // on the next start (GenerateMission at StartCountdown, fsmission.cpp:1954,
    // missionigc.cpp:3830+); this port generates at assembly time, so a
    // game-over rotation re-assembles the mission from these retained inputs.
    private StaticCore _core = null!;
    private HullLandingBayCache _landingBays = null!;
    private WeaponMuzzleCache _weaponMuzzles = null!;
    private StationLandingBayCache _stationLandingBays = null!;
    private CollisionHullCache _collisionHulls = null!;
    // P402 — the subset of missions whose map was procedurally generated
    // (g.MapGen set at instantiation) rather than loaded from a file. Populated
    // in InstantiateGames; the debug-topology station/warp machinery consults
    // it (via MissionCameFromMapGen) to decide whether the P379 real-map
    // ganymede/organia ambiguity can even arise. Reference identity of Mission
    // instances, so a HashSet is exact and cheap.
    private readonly HashSet<Mission> _mapGenMissions = [];
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
    // P761 — the connected pilots' names (the directory's player lists, the
    // data native's FIND_PLAYER searches), snapshotted alongside
    // _liveShipCount each tick under _statsLock so the lobby heartbeat can
    // publish them without touching the sim-thread-owned _peerShips map.
    private IReadOnlyList<string> _liveShipNames = Array.Empty<string>();
    private readonly List<short> _bucketCompletions = new();
    private readonly object _statsLock = new();

    // P315 (review finding H3) — rate-limits the per-message "unsupported fmid"
    // refusal log in ProcessDatagram so a flood of garbage/unknown fmids cannot
    // force unbounded synchronous Console.WriteLine calls inside one tick (see
    // PerPeerRateLimiter). Trimmed in ReleasePeer on peer leave to stay bounded
    // by the live peer set.
    private readonly PerPeerRateLimiter _unsupportedLog = new();
    private readonly PerPeerRateLimiter _turretLog = new();

    // P646 (review finding H3) — rate-limits the "DUPLICATE LOGONREQ from a
    // seated peer" refusal log so a duplicate-logon flood cannot force
    // unbounded synchronous console I/O inside one tick (same rationale and
    // trimming as _unsupportedLog; trimmed in ReleasePeer on peer leave).
    private readonly PerPeerRateLimiter _duplicateLogonLog = new();

    // Source of randomness for the eject branch's random fallback heading
    // (Vector::RandomDirection, vector.cpp:37-49). Unseeded by default;
    // local benchmarks can seed this server instance.
    private Random _rand = new();

    // Ships assigned to peers on validated logon (step 3). This map is owned
    // exclusively by the simulation thread (written in ProcessDatagram, which
    // Drain runs on the sim thread, and read in the broadcast step of SimPass),
    // so it needs no locking of its own.
    private readonly Dictionary<ConnectionId, LiveShip> _peerShips = new();
    private int _nextShipId = 8096;

    /// <summary>
    /// P604/P670 — payload bytes offered by the publishing sink, before framing,
    /// coalescing or transport lookup. The [egress] line labels these separately
    /// from transport-enqueued and socket-written bytes (both include framing).
    /// Updated atomically by the sim or publisher thread. Diagnostic only.
    /// </summary>
    private long _egressPayloadOfferedBytes;
    private long _lastEgressWrittenBytes, _lastEgressEnqueuedBytes;
    private long _egressWindowStarted = System.Diagnostics.Stopwatch.GetTimestamp();
    // P604b — last window's GC collection counts, so the [egress] summary can
    // report the per-window GC delta (ties any allocation-burst tick hitch to a
    // GC pause). Sim-thread only.
    private int _lastGc0, _lastGc1, _lastGc2;
    // P619 — last window's GC.GetTotalAllocatedBytes(), so the [egress] summary
    // also reports total managed bytes allocated this window (÷ ticks ≈ bytes
    // per tick) for the before/after allocation A/B. Sim-thread only.
    private long _lastAllocatedBytes;
    // P619c — last window's ConsoleLog dropped-line total, so the [egress]
    // summary reports how many log lines were dropped because the stdout
    // consumer stalled (the bounded queue filled). A rising count pinpoints a
    // slow docker-logs/journal reader without ever blocking the sim thread.
    private long _lastLogDrops;


    /// <summary>
    /// P603 — whether the per-peer broadcast builders run in parallel
    /// (<c>broadcast.parallelBuild</c> config, overridable by the
    /// <c>ALLEGIANCE_BROADCAST_PARALLEL_BUILD</c> env var; env wins). Resolved
    /// once at boot; the value is printed at boot and stamped into the profiler
    /// report header so every measurement is labelled. When false (the default)
    /// the broadcast code path is byte-for-byte today's serial behaviour.
    /// </summary>
    internal bool _broadcastParallelBuild;

    /// <summary>
    /// P604 — which per-peer ship-state update model is active
    /// (<c>broadcast.shipUpdateModel</c> config, overridable by the
    /// <c>ALLEGIANCE_SHIP_UPDATE_MODEL</c> env var; env wins). Resolved once at
    /// boot; the value is printed at boot and stamped into the profiler report
    /// header so every measurement is labelled. <c>true</c> = the "native"
    /// model: the per-tick teammate <c>posBatch</c> SingleShipUpdate stream is
    /// suppressed and remote-ship positions flow only through the native
    /// deviation-ranked HEAVY/LIGHT budget. <c>false</c> (the default, "port")
    /// = today's byte-for-byte behaviour, unchanged. See
    /// docs/reports/P604_NATIVE_SHIP_UPDATE_BUDGET.md.
    /// </summary>
    internal bool _nativeShipUpdates;

    /// <summary>
    /// P604 — the active ship-update-model name ("port" or "native"), retained
    /// for the boot banner / profiler label. Mirrors <see cref="_nativeShipUpdates"/>.
    /// </summary>
    internal string _shipUpdateModel = "port";

    /// <summary>
    /// P603 — the bounded degree of parallelism for the parallel broadcast build
    /// phase (<c>Parallel.ForEach</c> <c>MaxDegreeOfParallelism</c>), resolved at
    /// boot from <c>broadcast.maxDegreeOfParallelism</c> (clamped to
    /// <c>[1, ProcessorCount-1]</c>) or the default <c>ProcessorCount - 1</c>.
    /// Diagnostic only — it never changes which bytes are produced.
    /// </summary>
    internal int _broadcastMaxParallelism = 1;

    /// <summary>
    /// P605 — how the server drains its per-peer send queues
    /// (<c>transport.mode</c> config, overridable by
    /// <c>ALLEGIANCE_TRANSPORT_MODE</c>; env wins). Resolved once at boot; the
    /// value is printed at boot and stamped into the profiler report header.
    /// Default <see cref="TransportMode.ThreadPerPeer"/> = today's behaviour.
    /// </summary>
    internal TransportMode _transportMode = TransportMode.ThreadPerPeer;

    /// <summary>
    /// P605 — pool mode: how many send workers to spin up
    /// (<c>transport.poolWorkers</c> or <c>ALLEGIANCE_TRANSPORT_POOL_WORKERS</c>,
    /// default about the core count). Ignored in thread-per-peer mode.
    /// </summary>
    internal int _transportPoolWorkers = 1;

    /// <summary>
    /// P603 — internal test seam (InternalsVisibleTo): when set, invoked from
    /// <see cref="SendPeer"/> for every outbound packet (recipient, payload, in
    /// order) so the byte-identity harness can collect the exact per-peer payload
    /// stream both broadcast modes produce. Null in production (no behaviour).
    /// </summary>
    internal Action<ConnectionId, byte[]>? _testSendCapture;

    /// <summary>
    /// P608 — internal test seam (InternalsVisibleTo): when set, invoked from
    /// <see cref="SendPeerCoalescing"/> for every latest-wins stream frame with
    /// the recipient, payload, its coalesce key and its guaranteed flag. Lets a
    /// test verify the coalesce-key contract (e.g. that the owner's own-ship
    /// SingleShipUpdate uses a distinct key from the teammate position batch,
    /// and is guaranteed). Null in production (no behaviour).
    /// </summary>
    internal Action<ConnectionId, byte[], int, bool>? _testSendCoalescingCapture;

    /// <summary>
    /// P507 — per-peer state for the native own-ship model: the current cookie
    /// the client must echo in <c>FM_C_SHIP_UPDATE(24)</c> and the rolling
    /// acceptance counters the every-~10s summary log reports. Keyed by the
    /// same <see cref="ConnectionId"/> as <see cref="_peerShips"/> so it
    /// persists across a mission-rotation migration (which rebuilds LiveShip
    /// but not the connection). Created on the first join, mutated on the sim
    /// thread in ProcessDatagram (like <c>_peerShips</c>), removed on
    /// disconnect (<c>Server.Lifecycle.cs</c>).
    /// </summary>
    private readonly Dictionary<ConnectionId, PeerShipUpdate> _peerShipUpdate = new();

    /// <summary>
    /// P507 — native's per-player cookie (<c>CFSPlayer::GetCookie/NewCookie</c>,
    /// <c>fsship.h:313-321</c>): <c>m_cookie</c> starts at 0 and
    /// <c>NewCookie()</c> returns <c>++m_cookie</c>, sent to the client in
    /// <c>SET_CLUSTER</c> at join/warp (<c>fsship.cpp:767</c>) and in
    /// <c>SHIP_RESET</c> on rejection (<c>fsship.cpp:1199</c>). The client
    /// echoes it back in every SHIP_UPDATE (<c>Messages.h:227-231</c>); updates
    /// whose cookie doesn't match are dropped whole (<c>FedSrv.CPP:4851-4864</c>).
    /// Counter values are deliberately the native unsigned counter mod 256 (the
    /// wire cookie is one byte).
    /// </summary>
    private byte NextCookie(PeerShipUpdate p, bool reposition = true)
    {
        if (reposition)
        {
            // SET_CLUSTER/respawn/launch is server-authorized motion, not a
            // client teleport. Rebase travel credit but retain monotonic time.
            p.HasMovementAnchor = false;
            p.LastPoseAt = double.NegativeInfinity;
        }
        return p.Cookie = (byte)(p.Cookie + 1);
    }

    /// <summary>Fetch or create the P507 per-peer ship-update state for <paramref name="id"/>.</summary>
    private PeerShipUpdate EnsurePeerUpdate(ConnectionId id)
    {
        if (!_peerShipUpdate.TryGetValue(id, out var p))
        {
            p = new PeerShipUpdate();
            _peerShipUpdate[id] = p;
        }
        return p;
    }

    private sealed class PeerShipUpdate
    {
        /// <summary>The current cookie the client must echo (native <c>m_cookie</c>).</summary>
        public byte Cookie;
        public bool HasClientTime, HasMovementAnchor;
        public uint LastClientTime;
        public double LastPoseAt = double.NegativeInfinity, MovementBudgetAt;
        public float MovementCredit;
        public Vector3 LastPosePosition;
        public long DroppedReplay, DroppedSameTick;
        public long TotalCorrections;

        /// <summary>Rolling acceptance counters (P507 summary log).</summary>
        public long Accepted, Rejected, OutOfDate, Invalid;
        // Non-exclusive rejection reasons: a small mean position error alone
        // cannot distinguish a speed violation from a stale travel anchor.
        public long RejectedSpeed, RejectedEnvelope, RejectedTravel;
        /// <summary>P518 — rolling DROPPED counters, by reason. A silent drop is
        /// what hid P518 (the server's ship froze parked while the client flew on),
        /// so every drop the dispatch takes before the accept/reject pipeline is
        /// now counted and surfaced in the summary log.</summary>
        public long DroppedNoShip, DroppedPreGame, DroppedBoarded, DroppedNotInCluster, DroppedCookie;
        /// <summary>Sum of squared/linear reject distances for the mean-reject-distance figure.</summary>
        public double RejectDistanceSum;
        public long RejectCount;
        /// <summary>Sim clock (seconds) of the last summary write, for the ~10s cadence.</summary>
        public double LastSummaryAt;
        /// <summary>
        /// P541 — this peer flies its own ship locally (a real P508 native client,
        /// i.e. it sends real-time+pose SHIP_UPDATEs). Sticky once any native
        /// update is seen. The server must NOT autopilot such a ship (a server-side
        /// GotoPlan would fight the client's local sim), so the self-addressed
        /// cluster-goto CHATBUOY re-routes it only when this is false (legacy /
        /// bot clients that don't fly themselves).
        /// </summary>
        public bool NativeClient;
    }

    // Each connected peer's own character name, remembered past the logon
    // handshake. `req.CharacterName` (LogonRequest) is otherwise a local
    // variable consumed inline at the logon handler and then discarded, and
    // neither LiveShip nor Ship carries a name field — yet ExportShip's
    // CharacterName (the field that becomes the client-side ship's name) is
    // required when broadcasting a teammate's ship export. Populated at the
    // same point `_peerShips` is (validated logon), removed on disconnect.
    // Sim-thread only, like its sibling maps. P62.
    private readonly Dictionary<ConnectionId, string> _peerNames = new();

    /// <summary>P480 — the seated peer's original <see cref="LogonRequest"/>
    /// (CharacterName, DwCookie), retained so a game-over rotation
    /// (<see cref="Server.Lobby.RotateMissionAfterGameOver"/>) can re-drive a
    /// connected peer through the same join-burst path
    /// (<see cref="Server.Lobby.CompleteJoinAndLaunch"/>) it took at first
    /// seat. Freed on disconnect alongside the rest of the peer's state.</summary>
    private readonly Dictionary<ConnectionId, LogonRequest> _peerRequests = new();

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

    // P654 (H-24) — per-peer message/chat token budget, the port of native's
    // chat budget (c_chatBudgetMax=100 / c_chatBudgetCost=20, fsship.h:24-25;
    // IncrementChatBudget +1/tick, FedSrv.CPP:8287). Keys are peers that have
    // spent budget (absent = full). Charged on the sim thread only (dispatch),
    // refilled once per eligible player tick by AdvancePeriodicRules.
    // Value: {Budget, LastRefillTick}. Removed on peer leave (ReleasePeer) so
    // the table stays bounded by the live peer set.
    private readonly Dictionary<ConnectionId, (int Budget, long LastRefillTick)> _chatBudgets = new();
    private int _chatBudgetMax = 1000;
    private int _chatBudgetCost = 20;
    private int _chatBudgetRefillPerTick = 5;

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

    // P598 — the port of native's bAutoRestart timer (fsmission.cpp:3539-3546):
    // for a game configured with AutoRestart, the freshly-assembled NotStarted
    // mission after a game-over rotation arms a deadline here; when SimPass's
    // AdvanceAutoRestarts sees it elapse it starts the mission's countdown
    // without requiring anyone to ready up (native's unattended restart). Absent
    // for missions not armed. Written in RotateMissionAfterGameOver (sim thread),
    // read + removed in SimPass's AdvanceAutoRestarts. Distinct from
    // _startCountdownDeadline (which tracks an ALREADY-counting-down mission).
    private readonly Dictionary<Mission, double> _autoRestartDeadline = new();

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

    // P349 — the cross-side (P346) per-recipient "have I already told THIS peer
    // about THIS ship" latch. Exactly parallel to `_teammateExportSent` above and
    // keyed the same way (recipient connection, subject ship): a ship's
    // cross-side identity must reach EVERY peer on the seeing side independently,
    // because multiple peers can share a side and a peer that JOINS AFTER a ship
    // already became visible still has to be caught up on its next tick. This is
    // the reason the P346 edge-triggered transition-queue (which forgot peers who
    // were not connected at the transition instant) was replaced by a per-tick
    // "haven't told YOU yet" sweep (Server.Broadcast.SweepCrossSideExports).
    // Sim-thread only, like the sibling.
    private readonly HashSet<(ConnectionId Recipient, short ShipId)> _crossSideExportSent = new();

    // P453 — the cross-side MINE EXPORT(26)/OT_mine latch, the exact mirror of
    // `_crossSideExportSent` above but for the mine identity sweep
    // (Server.Broadcast.SweepCrossSideMineExports): once a side's scanner net
    // spots an enemy mine (its SeenBySide flag flips true), each seated peer on
    // that side is sent the mine's EXPORT exactly once — and any peer that
    // joins the side later is caught up on its next tick. Keys (recipient
    // connection, mine id). Read by BroadcastMineDestroyed so the SAME
    // latched audience (plus the same-side deploy audience) gets the destroy.
    // Sim-thread only, like the sibling; freed on disconnect.
    private readonly HashSet<(ConnectionId Recipient, short MineId)> _crossSideMineExportSent = new();

    // P366 — the same-side NPC identity + status latch. Exactly parallel to
    // `_teammateExportSent` but for same-side AI bot ships (IsNpc), which the
    // P62 teammate path skips (those are real peers) and the P346 cross-side
    // export skips (it excludes the ship's OWN side). Without it a team leader
    // could never see/select/command its own side's drones, so the leader-only
    // dock/launch UI (P366) needs these to reach the client: the set latches
    // the one-shot FM_S_EXPORT identity send per (recipient, NPC) pair; the
    // tick map throttles the periodic FM_S_SHIP_STATUS (docked state for the
    // Dock/Launch label) at the same cadence as teammates. Sim-thread only,
    // like the siblings.
    private readonly HashSet<(ConnectionId Recipient, short ShipId)> _sameSideNpcExportSent = new();
    private readonly Dictionary<(ConnectionId Recipient, short ShipId), long> _lastSameSideNpcStatusTick = new();

    // One-shot tracking for the P63 teammate-ship PLAYERINFO(10) broadcast:
    // set once the first time a (recipient, subject-ship) pair's real
    // PlayerInfo has been sent. Kept separate from `_teammateExportSent` —
    // these are two different messages with two different one-time gates, even
    // though they fire back-to-back for a given pair (EXPORT then PLAYERINFO).
    // Same reasoning as `_teammateExportSent`: a second PLAYERINFO would hit
    // CreateObject again on the same id, so a repeat is wrong, not merely
    // wasteful. Sim-thread only, like the siblings.
    private readonly HashSet<(ConnectionId Recipient, short ShipId)> _teammatePlayerInfoSent = new();

    // P332 — one-shot tracking for the P85 teammate-ship SINGLE_SHIP_UPDATE
    // position broadcast: set once a (recipient, subject-ship) pair's real
    // position has been broadcast at least once. Unlike the sibling sets above
    // (whose messages are truly one-shot), a position can legitimately repeat
    // when the ship moves; the set therefore does NOT suppress re-sends — the
    // motion dirty-check in BroadcastShipStates still governs those — it only
    // guarantees the FIRST send happens even for a ship that never moves after
    // spawn (whose LastBroadcast* would otherwise sit permanently equal to its
    // real position, gating the SingleShipUpdate off forever and leaving it
    // invisible). Per-(recipient,subject), not per-ship, so a ship that is
    // placed once and left stationary is still sent to each NEW viewer that
    // joins later, not just to whoever was present at its one dirty window.
    // Sim-thread only, like the siblings above.
    private readonly HashSet<(ConnectionId Recipient, short ShipId)> _teammatePositionSent = new();

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

    // P604b — the NEXT tick at which each RECIPIENT's periodic same-side
    // teammate SHIP_STATUS(90) refresh fires, spread per-recipient so the
    // per-pair refreshes never synchronize into an O(P²) one-tick burst (the
    // ~5 s hitch). See the BroadcastShipStates comment for the full rationale;
    // each recipient refreshes all of its mates once per TeammateStatusRefreshTicks
    // on its own phase-offset tick. Sim-thread only, like the sibling above.
    private readonly Dictionary<ConnectionId, long> _nextTeammateRefreshTick = new();

    // P493 — the previous sweep visibility of each (seeing-side, ship) pair,
    // so SweepCrossSideShipStatuses can detect the seen/unseen EDGE and fire the
    // native ShowObject/HideObject ship-status transition exactly once per real
    // change (fsship.cpp ShipStatusSpotted/ShipStatusHidden), instead of every
    // tick the ship stays visible. Keyed by (seeingSideId, subjectShipId); a
    // missing entry means "never swept" (treated as unseen). Sim-thread only.
    private readonly Dictionary<(short SideId, short ShipId), bool> _crossSideShipStatusSeen = new();

    // P493 — last cross-side SHIP_STATUS(90) send tick per (recipient,
    // subject-ship) pair, for the slow periodic catch-up refresh of a
    // currently-seen enemy (see CrossSideStatusRefreshTicks), so a peer that
    // joins an already-informed side (or misses one edge packet) re-learns the
    // enemy's last-known sector within one interval. Sim-thread only.
    private readonly Dictionary<(ConnectionId, short), long> _crossSideShipStatusTick = new();

    // P672 — mission baseline, eligible recipients, and refresh deadlines. Sim-thread only.
    private sealed class ScoreRosterState
    {
        public ScoreRosterEntry[] Entries = [];
        public HashSet<ConnectionId> Recipients = [];
        public long LastTick;
        public double LastFullTime;
        public MissionStage Stage;
    }
    private readonly Dictionary<Mission, ScoreRosterState> _scoreRosterStates = new();

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
    /// P731 — the optional UDP discovery responder (answers the client's
    /// "Find Server" queries). Non-null only when discovery is enabled (the
    /// default; see <see cref="DiscoveryConfig"/>). Started in
    /// <see cref="BindHostAndTransport"/> after the TCP transport, torn down in
    /// <see cref="Shutdown"/>.
    /// </summary>
    private DiscoveryResponder? _discoveryResponder;
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

    /// <summary>
    /// Raised on the sim thread whenever a real cross-side kill credit is granted (the launcher is a
    /// real side-owned ship, the victim is not a lifepod — the <c>!bLifepod</c> gate that already
    /// excludes ejections from kill credit — and the killer is on a different side; Server.Combat
    /// <c>BroadcastKillShip</c>). Payload is (launcherId, victimId). Subscribed by the embedded
    /// training host so a Mission 6 kill goal counts the <em>real</em> kill credit and never a
    /// separate ad-hoc counter, and so an ejection (which never reaches this point) cannot inflate it.
    /// </summary>
    public event Action<int, int>? KillCreditGranted;

    /// <summary>The position-change threshold (world units) that marks a ship
    /// dirty and worth a SINGLE_SHIP_UPDATE broadcast after a tick.</summary>
    private const float DirtyEpsilon = 1e-3f;

    /// <summary>The shield-fraction change threshold (dimensionless 0..1 units)
    /// that marks a ship's own <c>ShieldFraction</c> dirty and worth a fresh
    /// SINGLE_SHIP_UPDATE to its owning peer after a tick. Deliberately
    /// SEPARATE from <see cref="DirtyEpsilon"/>, which is calibrated for world
    /// <c>Vector3</c> position units (metres) and is meaningless for a 0..1
    /// fraction. Chosen just above the wire's representable shield step — the
    /// shield slot (ServerShipUpdate byte 41) is a div-240 BytePercentage, so
    /// the smallest expressible fraction change is 1/240 ≈ 0.004167 — meaning
    /// every rebroadcast this term fires reflects a shield change the client's
    /// HUD can actually display, with no sub-quantization spam. Regeneration
    /// (P389) climbs the fraction continuously, so this fires at most every
    /// regen step and the HUD climbs in smooth 0.5%-steps while the ship sits
    /// still. A ship with no shield mounted has <c>ShieldFraction ≡ 0</c>, so
    /// |0−0| never reaches this threshold — a no-op, never a per-tick spam.
    /// </summary>
    private const float ShieldFractionEpsilon = 0.005f;

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
    // Built-in debug arenas are production spawn policies too. Keep the
    // delegate identity so a test replacing the hook still controls placement.
    private Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)>? _hangarShipSpawner;

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

    /// <summary>P761 — snapshot of the connected pilots' names (captured once
    /// per tick under <c>_statsLock</c>, the same pairing as
    /// <see cref="LivePlayerCount"/>), in seat order. The lobby heartbeat
    /// publishes them so the client's find-player search can locate the game a
    /// pilot is in (native FIND_PLAYER, gamescreen.cpp:1348-1352).</summary>
    public IReadOnlyList<string> LivePlayerNames { get { lock (_statsLock) return _liveShipNames; } }

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
              int CrossSideExportSent, int CrossSideMineExportSent, int LastTeammateStatusTick,
              int NextTeammateRefreshTick) ConnectionScopedCacheCounts()
        => (
            _pendingSideSelect.Count, _peerNames.Count, _playerReady.Count,
            _lastStatusSector.Count, _lastStatusTick.Count, _lastClusterInfoTick.Count,
            _lastPingQuery.Count, _teammateExportSent.Count, _teammatePlayerInfoSent.Count,
            _teammateLoadoutVersionSent.Count, _teammateLoadoutCache.Count,
            _crossSideExportSent.Count, _crossSideMineExportSent.Count, _lastTeammateStatusTick.Count,
            _nextTeammateRefreshTick.Count);

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

    /// <summary>P493 — cadence (ticks) of the slow periodic cross-side
    /// SHIP_STATUS(90) catch-up refresh for a currently-seen enemy: re-send the
    /// spotted status to a side's seated peer on a slow interval even with no
    /// visibility change, so a peer that joins an already-informed side (or
    /// loses one edge packet) re-learns the enemy's last-known sector within one
    /// interval. This mirrors the same-side teammate refresh cadence
    /// (<see cref="TeammateStatusRefreshTicks"/>) and the P369 identity
    /// self-heal; it is deliberately NOT per-tick (the visible/hidden EDGES are
    /// the primary trigger, native's ShowObject/HideObject cadence —
    /// fsship.cpp ShipStatusSpotted/ShipStatusHidden). 100 ticks @ 20 upd/s = 5 s.</summary>
    private const int CrossSideStatusRefreshTicks = 100;

    // Retain the existing maximum two-second score latency; full recovery every 30 sim seconds.
    private const int ScoreRosterRefreshTicks = 40;
    private const double ScoreRosterFullRefreshSeconds = 30;

    /// <summary>P369 — cadence (ticks) of the slow periodic identity REFRESH for
    /// cross-side ships: re-send the one-shot <c>EXPORT(26)</c> of an already-
    /// latched (peer, ship) pair, so a peer that missed its single identity
    /// EXPORT (the client's ONLY source of a foreign ship's <c>SideId</c>) heals
    /// itself instead of carrying the wrong side forever. Gated exactly like the
    /// first send (ship still visible to that side, peer still seated), so it is
    /// bounded and rare — once per (peer, visible ship) per 30 s — and does NOT
    /// reintroduce P346/P349's per-tick packet spam. The render client applies a
    /// repeat EXPORT idempotently (Upsert + field-set), so this is safe.
    /// 600 ticks @ 20 upd/s = 30 s.</summary>
    private const int CrossSideIdentityRefreshTicks = 600;

    /// <summary>P604b — the client dead-reckoner's smooth extrapolation horizon,
    /// in ticks (1.5 s at 20 upd/s). Matches
    /// <c>ShipDeadReckoner.ExtrapolationCap = 1.5 s</c>
    /// (<c>src/Allegiance.ClientCore/Flight/ShipDeadReckoner.cs:64</c>; native's
    /// ±1.5 s <c>ProcessShipUpdate</c> window, <c>shipIGC.cpp:3113-3115</c>).
    /// The native-mode heavy ship-update budget (BroadcastShipsUpdatePeer) is
    /// scaled so every in-sector ship is re-fed at least once per this horizon;
    /// beyond it the renderer-only client HOLDS the dead-reckoned pose (a
    /// visible freeze).</summary>
    internal const int DeadReckonExtrapolationTicks = 30;
    /// <summary>P281 — a peer that has logged on but not yet chosen a side (the
    /// post-connect side-select hold). Held here (NOT in <c>_peerShips</c>) with
    /// no Ship until it picks a side (matching native's POSITIONREQ wait); only
    /// the opt-in <see cref="ServerConfig.SideSelectAutoSeatSeconds"/> timeout
    /// seats a silent peer. Its ship id is reserved up-front so LogonAck carries
    /// a stable id.</summary>
    private readonly struct PendingSideSelect
    {
        public short ShipId { get; }
        public LogonRequest Request { get; }
        public double ConnectSimTime { get; }
        /// <summary>P281 — the requested side once the peer chooses (null = not
        /// chosen yet); NA(-1) is never stored here (auto-assign is expressed by
        /// a null RequestedSide when the opt-in auto-seat timer, or an immediate
        /// NA request, seats it).</summary>
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

    /// <summary>Default spawn: a small scout-class hull (falling back to the
    /// first hull in the core by id if the core has none), placed on a
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
    /// <para><b>Hull pick, revised 2026-09-22.</b> Originally the globally
    /// lowest <c>HullId</c> in the core, which every real PCore001 core
    /// happens to resolve to hull 1 "Adv Miner" (confirmed live: every new
    /// connection spawned in the same miner, regardless of side). Hull data
    /// carries no civ/side association of its own (civ resolution for
    /// rendering is a client-side-only concept, per the MonoGame client's
    /// own <c>HullModelResolver</c> doc comment — deliberately not
    /// duplicated here), so a real per-side/civ starter pick is future work
    /// (the hangar/loadout plan's own deferred "Stage 4"). For now, pick the
    /// lowest-<c>HullId</c> hull whose name contains "Scout" — every core
    /// this port ships with has several, all small combat-capable craft —
    /// a far more sensible universal default than a miner, without
    /// pretending to guess a side's civilization.</para>
    /// </remarks>
    /// <summary>
    /// P368 — resolve a side's real home sector from the mission's owned
    /// stations (<see cref="Station.SideId"/>), not a sector-0 convention.
    /// A side's home is the sector holding a station that side owns; the
    /// runtime <see cref="Station.SideId"/> is initialised from the static
    /// <c>DataStationIGC.SideId</c> at mission load and is the same field the
    /// friendly-fire gate (<see cref="Station.ReceiveDamage"/>) reads, so this
    /// is real per-map data (verified on the live map <c>m2xspiderw.igc</c>:
    /// side 0's garrison sits in 'Ganymede', side 1's in 'Organia'). Iterates
    /// <see cref="Mission.Sectors"/> in mission order, so when a side owns
    /// more than one station the <b>first</b> such sector deterministically
    /// wins. Returns <c>null</c> when the side has no resolvable owned
    /// station (no stations at all, or only stations whose types failed to
    /// resolve into runtime models) — the caller falls back to
    /// <c>mission.Sectors[0]</c> and logs it plainly, never fabricating.
    /// </summary>
    private static Sector? HomeSectorForSide(Mission mission, short sideId)
    {
        // Primary: a runtime Station the side owns (live ownership, re-sided
        // on STATION_CAPTURE — the authoritative "whose home is this" answer).
        foreach (var sector in mission.Sectors)
            foreach (var st in sector.StationRuntimes)
                if (st.SideId == sideId)
                    return sector;
        // Secondary: a static-only station (its type never resolved in the
        // core) still records the side's home in DataStationIGC.SideId, so it
        // must not cause a wrong fallback to sector 0 when every runtime
        // model is absent.
        foreach (var sector in mission.Sectors)
            foreach (var st in sector.Stations)
                if (st.SideId == sideId)
                    return sector;
        return null;
    }

    private (Ship Ship, Sector Sector) SpawnDefaultShip(Mission mission, short shipId, short? sideIdOverride = null)
    {
        if (mission.Core.HullTypes.Count == 0)
            throw new InvalidOperationException("core has no hull types to assign a ship");
        var scoutHulls = mission.Core.HullTypes.Values
            .Where(h => h.Name.Contains("Scout", StringComparison.OrdinalIgnoreCase))
            .OrderBy(h => h.HullId)
            .ToList();
        var hull = scoutHulls.Count > 0
            ? scoutHulls[0]
            : mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First();

        // P368 — resolve the FINAL side BEFORE picking the sector, so the ship
        // is placed ONCE in that side's real home sector instead of always
        // sector 0 (the old bug: every ship, auto-assigned or explicit-picked,
        // physically spawned in mission.Sectors[0] and the side was chosen
        // only after placement, never used to pick the sector). The caller
        // (CompleteSideSelection → AssignShip) threads an explicit pick
        // through sideIdOverride; otherwise we fall back to the same
        // round-robin across mission.Sides the old code used (peer N → side N
        // mod count) — but computed up front, so the sector depends on it.
        short? sideId;
        if (sideIdOverride is { } overridden)
            sideId = overridden;
        else if (mission.Sides.Count > 0)
            sideId = mission.Sides[_peerShips.Count % mission.Sides.Count].Id;
        else
            sideId = null;

        Sector sector;
        if (sideId is { } sid)
        {
            var home = HomeSectorForSide(mission, sid);
            sector = home ?? mission.Sectors[0];
            if (home is null)
                Console.WriteLine($"[spawn] ship {shipId}: side {sid} has no owned station — falling back to sector 0 '{mission.Sectors[0].Name}' (a finding, not fabricated)");
            else
                Console.WriteLine($"[spawn] ship {shipId} seated on side {sid} "
                    + $"({mission.Sides.FirstOrDefault(s => s.Id == sid)?.Name ?? $"side{sid}"}) "
                    + $"in its home sector '{sector.Name}' (id {sector.Id})");
        }
        else
        {
            sector = mission.Sectors[0];
            Console.WriteLine($"[spawn] ship {shipId}: mission has no sides — placed in sector 0 '{mission.Sectors[0].Name}', ship left with no side (a finding, not fabricated)");
        }

        int column = _peerShips.Count % SpawnGridColumns;
        int row = _peerShips.Count / SpawnGridColumns;
        float x = 720f * (1 + column);
        float z = 920f + row * 720f;
        var ship = new Ship(shipId, hull, new Vector3(x, 730f, z), Vector3.Zero, Orientation.Identity);
        if (sideId is { } ss) ship.SideId = ss;
        // P378 — carry the resolved live-settings lifepod endurance override so
        // a later hull-swap to a drifting Lifepod (death/eject/rescue) uses the
        // configured/debug endurance instead of racing the native 5-minute timer.
        ship.LifepodEnduranceOverrideSeconds = _lifepodEnduranceOverrideSeconds;
        // Single placement into the CORRECT sector (computed from the final
        // side above). We never spawn-then-relocate, so the P368 pitfall of
        // Mission.PlaceShip double-registering a ship in two sectors'
        // collections (it does ship.Sector = sector; sector.Add(ship) without
        // removing from any old sector) cannot occur.
        mission.PlaceShip(ship, sector);

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
        // P458 — use the drone type's real hull (native CreateStockDrone uses
        // pdronetype->GetHullTypeID(), FedSrv.CPP:2228). Falls back to the
        // core's lowest-hull-id hull when no drone type is given (all pre-P160
        // call sites) or the drone type's hull id is absent from the core
        // (defensive — the drone type is real but its hull record isn't).
        var hull = mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First();

        // P160 — resolve the ship's real pilot type from the drone-type definition
        // being spawned (native: m_pilotType comes from the drone type; this port's
        // DataDroneTypeIGC.PilotType is the direct analogue). Fall back to
        // PilotTypeMiner when no drone-type id is given (all pre-P160 call sites
        // spawn miner-behaving NPCs) or the id isn't in the core — behavior-
        // preserving for every existing caller.
        byte pilotType = Ship.PilotTypeMiner;
        if (droneTypeId is { } dtId && mission.Core.DroneTypes.TryGetValue(dtId, out var droneType))
        {
            pilotType = droneType.PilotType;
            if (mission.Core.HullTypes.TryGetValue(droneType.HullTypeId, out var realHull))
                hull = realHull;   // P458: the drone type's real hull
        }

        short shipId = AllocateShipId();   // same id space as player ships (P316 collision-skip)
        var ship = new Ship(shipId, hull, position, Vector3.Zero, Orientation.Identity)
        {
            DroneTypeId = droneTypeId ?? -1,
            IsNpc = true,                                   // P148: explicit drone marker (init-only)
            PilotType = pilotType,                          // P160: the drone type's real pilot type (init-only)
        };
        ship.SideId = sideId;                               // drones are side-owned in native
        ship.LifepodEnduranceOverrideSeconds = _lifepodEnduranceOverrideSeconds;   // P378: consistent live-settings override
        mission.PlaceShip(ship, sector);                    // no _peerShips entry is created
        Console.WriteLine($"[npc] spawned ownerless NPC ship {shipId} side {sideId} at {position} (pilotType {pilotType})");
        return ship;
    }

    /// <summary>Legacy name for <see cref="SendSectorWide"/> retained by the
    /// ripcord call sites; delegates to the canonical generic helper.</summary>
    private void SendRipcordSectorWide(Sector sector, byte[] encoded) => SendSectorWide(sector, encoded);

    /// <summary>A per-axis uniform-jitter helper for the relaunch's small
    /// <c>random(-0.5f, 0.5f)</c> positional wobble (FedSrv.CPP:12965-12967).
    /// Independent draw per axis, shared with the port's existing
    /// <see cref="_rand"/>-backed helpers.</summary>
    private Vector3 Jitter(float amplitude) =>
        new((float)(_rand.NextDouble() * 2.0 - 1.0) * amplitude,
            (float)(_rand.NextDouble() * 2.0 - 1.0) * amplitude,
            (float)(_rand.NextDouble() * 2.0 - 1.0) * amplitude);

    /// <summary>
    /// P111/P454 — the port of <c>CshipIGC::FindRipcordModel</c>
    /// (<c>shipIGC.cpp:3718-3973</c>): what actually qualifies as a valid
    /// ripcord anchor in a target sector, and the priority order. The tiers,
    /// in search order:
    /// <list type="number">
    /// <item><b>Command-target probe</b> (<c>shipIGC.cpp:3803-3814</c>, P454):
    /// if the ripper's command target is a same-side <see cref="Probe"/> in the
    /// target sector that <see cref="Probe.CanRipcord"/>, it is the anchor.</item>
    /// <item><b>Friendly <c>c_sabmRipcord</c> station</b> in the target sector
    /// (<c>FindTarget</c> station tier, <c>shipIGC.cpp:3835-3838</c>): a
    /// station on the requester's own side whose station type has the
    /// <c>c_sabmRipcord</c> ability (<c>igc.h:747</c>).</item>
    /// <item><b>Friendly probe</b> (<c>shipIGC.cpp:3841-3896</c>, P454), for
    /// players only (<c>m_pilotType &gt;= c_ptPlayer</c>): the most recently
    /// deployed same-side <see cref="Probe.CanRipcord"/> probe in the target
    /// sector, or the one nearest the goal position (the command target's
    /// position) when one exists.</item>
    /// <item><b>Friendly ripcord-target ship</b> in the target sector
    /// (<c>shipIGC.cpp:3898-3930</c>): a peer ship on the requester's side
    /// whose hull can host a ripcord (light-ripcord refinement reproduced —
    /// the target needs <c>c_habmIsRipcordTarget</c>, or additionally
    /// <c>c_habmIsLtRipcordTarget</c> when the ripper's own hull has
    /// <c>c_habmCanLtRipcord</c>, <c>shipIGC.cpp:3900-3906</c>).</item>
    /// </list>
    /// Deferred (documented, not silently dropped): the command-target
    /// <b>ship</b> tier (<c>shipIGC.cpp:3816-3831</c>) — an explicit
    /// follow-up; the warp-adjacent-cluster expansion
    /// (<c>shipIGC.cpp:3936-3972</c>) because it relies on per-side
    /// <c>CanSee(w)</c> visibility and ripcord-debt ordering that this port
    /// does not model, and — critically — on XXV_simple's densely-connected
    /// 13-sector web (38 warps) the real side-0 station is reachable from
    /// nearly every sector, so expansion would grant an anchor almost anywhere
    /// and make the <c>RIPCORD_DENIED</c> path unreachable in practice.
    /// Scoping the anchor search to the requested sector keeps the deny path
    /// real and deterministic. The command target
    /// (<c>c_cmdCurrent</c>-falling-back-to-<c>c_cmdAccepted</c>,
    /// <c>shipIGC.cpp:3775-3790</c>) still seeds the goal position used by the
    /// friendly-probe nearness pick, and — when it is a <c>Probe</c> — the
    /// whole Tier 1.
    /// </summary>
    private Ship.RipcordTarget? FindRipcordModel(Ship ship, Sector target)
    {
        short side = ship.SideId!.Value;
        var core = ship.Mission!.Core;
        bool Friendly(short? other) => other == side ||
            ship.Mission.AllowAlliedRip && AlliedSides(ship.Mission, side, other);
        double now = ship.Mission.LastUpdate;
        float ripcordSpeed = ship.RipcordSpeed;     // GetRipcordSpeed(), shipIGC.cpp:3796

        // Goal position for the friendly-probe nearness pick (shipIGC.cpp:3775-3790):
        // the command target (c_cmdCurrent, falling back to c_cmdAccepted) — used
        // ONLY when the goal model is visible and its cluster (resolved through
        // the IIgcSite override, igc.h:4909-4913) is the ripcord target sector
        // (shipIGC.cpp:3784-3785). Native also conjuncts
        // pmodelGoal->SeenBySide(pside); ServerHost.GetCluster's CanSee (own side
        // OR SeenBySide OR in scanner range, shipIGC.h:380-388) is a superset,
        // and the P132 sweep marks own-side models seen (Sector.cs:4618-4621) as
        // well as in-range ones — but an EquipmentTarget wrapper's own
        // SideVisibility is never swept (EquipmentTarget.CanSee reads the body),
        // so a raw goalModel.SeenBySide(side) would wrongly reject station goals.
        // Bonus over the raw Probe/Ship switch this replaces: goal models of type
        // EquipmentTarget/Mine/Missile/Treasure now resolve their cluster through
        // the site's ClusterOf instead of being dropped (_ => null).
        Vector3? goal = null;
        var goalModel = ship.CommandTarget ?? ship.AcceptedShipTarget;
        if (goalModel is not null &&
            _host.GetCluster(ship, goalModel) is Sector goalSector && goalSector.Id == target.Id)
            goal = goalModel.Position;

        // Tier 1: command-target probe (shipIGC.cpp:3803-3814). If the ripper's
        // command target is a same-side probe in the target sector that can be
        // ripcorded to, it is the anchor. (The command-target SHIP case,
        // shipIGC.cpp:3816-3831, is a scoped-out follow-up.)
        if (ship.CommandTarget is Probe cmdProbe &&
            Friendly(cmdProbe.SideId) &&
            cmdProbe.OwnerSector.Id == target.Id &&
            cmdProbe.CanRipcord(now, ripcordSpeed))
            return new Ship.RipcordTarget((short)IgcObjectType.Probe, cmdProbe.Id, target.Id);

        // Tier 2: friendly c_sabmRipcord station (shipIGC.cpp:3835-3838).
        foreach (var st in target.Stations)
        {
            if (Friendly(st.SideId) &&
                core.StationTypes.TryGetValue(st.StationTypeId, out var stype) &&
                stype.HasCapability(DataStationTypeIGC.SabmRipcord))
                return new Ship.RipcordTarget((short)IgcObjectType.Station, st.StationId, st.ClusterId);
        }

        // Tier 3: friendly probe (shipIGC.cpp:3841-3896), players only
        // (m_pilotType >= c_ptPlayer). Search the target sector's probes; the
        // most recently deployed same-side CanRipcord probe wins, or the one
        // nearest the goal position when a goal exists.
        if (ship.PilotType >= Ship.PilotTypePlayer)
        {
            Probe? best = null;
            double bestDist2 = double.MaxValue;
            foreach (var m in target.Models)
            {
                if (m is not Probe pr) continue;
                if (!Friendly(pr.SideId) || !pr.CanRipcord(now, ripcordSpeed)) continue;

                if (goal is { } g)
                {
                    double d2 = (pr.Position - g).LengthSquared();
                    if (best is null || d2 < bestDist2) { best = pr; bestDist2 = d2; }
                }
                else if (best is null || pr.DeployTime > best.DeployTime)
                {
                    best = pr;   // most recently deployed wins (backward list, shipIGC.cpp:3849/3874)
                }
            }
            if (best is not null)
                return new Ship.RipcordTarget((short)IgcObjectType.Probe, best.Id, target.Id);
        }

        // Tier 4: friendly ripcord-target ship (shipIGC.cpp:3898-3930).
        // Route the capability check through the IIgcSite override
        // (GetRipcordCluster, igc.h:4915-4921) so production callers exercise
        // the site seam (common.cpp:811,824; shipIGC.cpp:3741,4262).
        short habm = ship.HullType.HasCapability(DataHullTypeIGC.HabmCanLtRipcord)
            ? (short)(DataHullTypeIGC.HabmIsRipcordTarget | DataHullTypeIGC.HabmIsLtRipcordTarget)
            : DataHullTypeIGC.HabmIsRipcordTarget;
        foreach (var peer in _peerShips.Values)
        {
            var s = peer.Ship;
            if (!ReferenceEquals(s, ship) && s.Sector is not null &&
                s.Sector.Id == target.Id && Friendly(s.SideId) &&
                _host.GetRipcordCluster(s, habm) is not null)
                return new Ship.RipcordTarget((short)IgcObjectType.Ship, s.Id, target.Id);
        }

        return null;
    }

    /// <summary>The native <c>NA</c> sentinel for a ShipID (igc.h:480):
    /// board/leave sentinel in <c>FM_C_BOARD_SHIP.sidParent</c>.</summary>
    private const short Na = -1;

    /// <summary>
    /// <c>Vector::RandomDirection()</c> (<c>vector.cpp:37-49</c>) — a uniformly
    /// distributed unit direction: <c>sinPitch ∈ [-1,1]</c>, <c>yaw ∈ [0, 2π)</c>,
    /// <c>(cos(yaw)·cosPitch, sin(yaw)·cosPitch, sinPitch)</c>. Used by the
    /// eject branch's fallback heading (no killer in the sector).
    /// </summary>
    private Vector3 RandomDirection()
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
    /// Resolve a harness grant only after checking both configured name prefix
    /// and server-verifiable connection provenance (P655).
    /// </summary>
    private TestHarnessConfig? HarnessGrantFor(ConnectionId from)
        => IsHarnessPeer(from, _peerNames.GetValueOrDefault(from)) ? _config.LiveSettings!.TestHarness : null;


}
