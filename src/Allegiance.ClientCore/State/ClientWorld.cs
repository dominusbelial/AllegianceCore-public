using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Core.Visual;
using Allegiance.ClientCore.Flight;

namespace Allegiance.ClientCore.State;

/// <summary>
/// A pure domain object: the client's game-state model built only from real
/// inbound wire messages. No I/O, no locking — a caller (e.g.
/// <see cref="Session.ConnectionSession"/> which provides the lock) drives
/// <see cref="Apply"/> from its receive path and reads the tables
/// snapshot-safely from other threads.
///
/// Beyond the ship entity table (keyed by id, one entity per ship), this
/// models the rest of the universe the wire describes: the EXPORT(26) object
/// table (<c>Clusters</c>/<c>Stations</c>/<c>Warps</c>/<c>Asteroids</c>,
/// one-shot per object at join, last-write-wins) and the <see cref="Graph"/>
/// cluster connectivity snapshot. Everything is upserted by id — there is no
/// <c>Add</c>-fresh anywhere, so every branch preserves the one-entity-per-id
/// invariant.
/// </summary>
public sealed partial class ClientWorld
{
    private BotShipTable _ships = new();

    // ── P636 — lock-free decision snapshot ──────────────────────────────────
    // Published by the RECEIVE thread (the sole world writer) via
    // PublishDecisionSnapshot(); read LOCK-FREE by the bot decision loop through
    // Decision. Published snapshots own their arrays for their entire lifetime.
    // A fixed-delay ring cannot prove readers have finished during a long hitch.
    private DecisionSnapshot _publishedDecision = DecisionSnapshot.Empty;
    private long _decisionSnapVersion;
    private long _lastDecisionSnapPublishTick;
    /// <summary>P636 — minimum wall interval between snapshot publishes (ms).
    /// The decision loop targets ~20 Hz, so ~25 Hz publish is comfortably fresh
    /// while bounding the per-bot rebuild cost (it replaces the per-update
    /// O(clusterShips) gate scans, not adds to them).
    /// P636c — made overridable via ALLEGIANCE_BOT_SNAPSHOT_PUBLISH_MS (default
    /// 40) so the live harness can inflate it to a genuinely STALE snapshot and
    /// prove the staleness→lethality mechanism (and the dead-reckon recovery) in
    /// a controlled A/B.
    /// P636d — no longer the primary throttle: since the publish is now LAZY
    /// (requested by the decision loop, §<see cref="PublishDecisionSnapshot"/>),
    /// this interval is a defensive floor against pathological rapid requests
    /// and the sub-switch retains its controlled-staleness A/B meaning.</summary>
    internal static int DecisionSnapPublishIntervalMs = ReadPublishInterval();
    private static int ReadPublishInterval()
    {
        if (int.TryParse(System.Environment.GetEnvironmentVariable("ALLEGIANCE_BOT_SNAPSHOT_PUBLISH_MS"), out var v) && v > 0)
            return v;
        return 40;
    }

    /// <summary>
    /// P636d — whether the lock-free decision snapshot is enabled at all. This is
    /// the SINGLE source of truth for the A/B switch
    /// (<c>ALLEGIANCE_BOT_DECISION_SNAPSHOT</c> ≠ <c>off</c>), moved here from the
    /// tool layer so the CLIENT-CORE receive thread honours it directly. When OFF,
    /// <see cref="PublishDecisionSnapshot"/> performs ZERO publishes — the receive
    /// thread does no rebuild work at all, so the P636c switch now also stops the
    /// WRITE, not just the read. (Previously the switch only reverted the decision
    /// READ path to live tables; the publish still ran at ~25 Hz × every bot,
    /// which is exactly the receive-thread starvation this task measures and fixes.)
    /// Read once at startup (mutable so a unit test can exercise the off-mode
    /// path deterministically; production never reassigns it).
    /// </summary>
    public static bool DecisionSnapshotEnabled { get; set; } =
        string.Equals(System.Environment.GetEnvironmentVariable("ALLEGIANCE_BOT_DECISION_SNAPSHOT"), "on", System.StringComparison.OrdinalIgnoreCase);   // 2026-10-13: default OFF — snapshot on = no kills at 200v200 on the real rig (clash30/32); off fights (clash33)

    // ── P636d — LAZY publish coordination + measurement ─────────────────────
    // The snapshot is no longer published on a fixed ~25 Hz timer: the RECEIVE
    // thread rebuilds only when the bot DECISION thread explicitly requests a
    // fresh snapshot (RequestDecisionRefresh), a minimum interval has elapsed, and
    // the decision-relevant world actually changed since the last publish
    // (skip-unchanged). Publish therefore follows the decision cadence instead of
    // the receive cadence, and idles to ZERO for idle bots and (always) when the
    // snapshot is switched off. Because the decision thread sets the request just
    // before it reads .Decision, the snapshot it consumes is freshly published —
    // which collapses the P636c snapshot age at decision time.
    private int _decisionRefreshPending;      // Volatile 0/1, decision → receive
    private long _worldGen;                   // bumped on snapshot-relevant mutations (receive thread)
    private long _lastPublishedWorldGen = -1;
    private long _snapPublishCount;           // cumulative rebuilds (receive threads)
    private long _snapPublishTicks;           // cumulative rebuild wall-time, Stopwatch ticks
    private long _lastAppliedTick64;          // wall stamp of the newest applied update (decode lag)


    /// <summary>
    /// P636 — the latest published <see cref="DecisionSnapshot"/> for LOCK-FREE
    /// reads by the bot decision loop. Reading it takes one volatile reference read
    /// and never acquires the world gate, so a decision pass no longer stalls behind
    /// the receive thread's bulk ship-application under the gate (the P632/P633
    /// transit collapse above ~250 bots). The MonoGame render path still reads the
    /// live tables under the gate as before; this is additive.
    /// </summary>
    public DecisionSnapshot Decision => System.Threading.Volatile.Read(ref _publishedDecision);

    /// <summary>
    /// P636 — (re)build and publish the decision snapshot. CALLED ONLY ON THE
    /// RECEIVE THREAD, which is the sole writer of the live tables — the rebuild
    /// reads them here without taking the gate (safe: no other thread mutates them),
    /// fills fresh arrays owned by this snapshot for its entire lifetime,
    /// then atomically swaps the published reference so concurrent readers never
    /// observe a torn half-built snapshot.
    /// P636d — now LAZY and request-driven (the P636d fix for the receive-thread
    /// publish cost at ~390 bots). It returns early, doing NO rebuild work, unless
    /// ALL of: (1) the snapshot is enabled (<see cref="DecisionSnapshotEnabled"/>
    /// — when OFF this method is a strict no-op and performs ZERO publishes),
    /// (2) the decision thread has a pending refresh request
    /// (<see cref="RequestDecisionRefresh"/>), (3) a defensive minimum interval
    /// elapsed, and (4) the decision-relevant world changed since the last publish
    /// (skip-unchanged). The decision loop sets a request once per pass, so publish
    /// follows the decision cadence and idles to zero when no bot is deciding.
    /// Returns the published snapshot (new when a rebuild+swap happened, else the
    /// current one).
    /// </summary>
    public DecisionSnapshot PublishDecisionSnapshot()
    {
        if (_botWorld is not null) return PublishSharedDecision();
        if (!DecisionSnapshotEnabled) return _publishedDecision;   // OFF: zero publishes

        // Lazy: no rebuild unless the bot decision loop wants fresh snapshots
        // (a sticky active level — RequestDecisionRefresh is re-asserted each
        // pass and revoked by CancelDecisionRefresh when the bot goes idle).
        if (System.Threading.Volatile.Read(ref _decisionRefreshPending) == 0)
            return _publishedDecision;

        long now = (long)(NowSeconds() * 1000);
        if (_lastPublishedWorldGen == _worldGen)
            return _publishedDecision;                              // world unchanged since last publish
        if (now - _lastDecisionSnapPublishTick < DecisionSnapPublishIntervalMs)
            return _publishedDecision;                              // defensive floor → publish ≤ ~25 Hz while active

        _lastDecisionSnapPublishTick = now;
        _lastPublishedWorldGen = _worldGen;

        long sw = System.Diagnostics.Stopwatch.GetTimestamp();
        var target = new DecisionSnapshot();
        target.Version = ++_decisionSnapVersion;
        target.MissionGeneration = MissionGeneration;
        target.PublishedTick64 = now;
        target.MyShipId = MyShipId;
        target.MySideId = MySideId;
        FillShipSnapshot(target);
        FillStationSnapshot(target);
        FillWarpSnapshot(target);
        if (MyShipId is { } me)
        {
            if (DecisionSnapshot.TryFind(target.Ships, me, out var mine))
                target.MyCluster = mine.SectorId;
            else
                target.MyCluster = -1;
        }
        else
        {
            target.MyCluster = -1;
        }
        System.Threading.Volatile.Write(ref _publishedDecision, target);

        System.Threading.Interlocked.Increment(ref _snapPublishCount);
        System.Threading.Interlocked.Add(ref _snapPublishTicks, System.Diagnostics.Stopwatch.GetTimestamp() - sw);
        return target;
    }

    /// <summary>
    /// P636d — the bot DECISION thread asserts that it wants fresh decision
    /// snapshots (sticky active level). Called once per decision pass per active
    /// bot. The receive thread republishes at the ≤~25 Hz cadence (§skip-unchanged
    /// and the interval floor) while the level is held, and ZERO when it is not
    /// (idle bots call <see cref="CancelDecisionRefresh"/>, and `off` mode never
    /// publishes regardless). Writing the flag is one volatile store — it costs
    /// nothing on this (decision) thread, preserving the P636 lock-free read.
    /// </summary>
    public void RequestDecisionRefresh() => System.Threading.Volatile.Write(ref _decisionRefreshPending, 1);

    /// <summary>
    /// P636d — revoke the active request: the bot is no longer deciding, so this
    /// world's receive thread stops publishing until it is re-requested. Together
    /// with <see cref="RequestDecisionRefresh"/> this bounds publish to active
    /// bots only (idle/not-seated worlds publish zero).
    /// </summary>
    public void CancelDecisionRefresh() => System.Threading.Volatile.Write(ref _decisionRefreshPending, 0);

    /// <summary>
    /// P636d — mark the decision-relevant world state dirty (called from the
    /// receive-thread Apply paths that mutate Ships/Stations/Warps). Lets the lazy
    /// publisher skip a rebuild when nothing the decision reads has changed.
    /// </summary>
    private void MarkSnapshotDirty()
    {
        _worldGen++;
        if (_botWorld is not null) _botWorld.Dirty = true;
    }

    /// <summary>
    /// P636d — the receive thread stamps the wall-clock time (ms, monotonic) just
    /// after applying a batch of messages; a decision-time reader subtracts it from
    /// "now" to measure DECODE LAG — how stale the newest applied update is.
    /// </summary>
    public void MarkApplied() => System.Threading.Volatile.Write(ref _lastAppliedTick64, (long)(NowSeconds() * 1000));

    /// <summary>P636d — the wall-clock stamp (ms) of the newest applied update.</summary>
    public long LastAppliedTick64 => System.Threading.Volatile.Read(ref _lastAppliedTick64);

    /// <summary>P636d — cumulative snapshot-publish rebuilds performed on the
    /// receive threads (this world). Thread-safe (Interlocked).</summary>
    public long SnapshotPublishCount => System.Threading.Interlocked.Read(ref _snapPublishCount);

    /// <summary>P636d — cumulative wall-time spent rebuilding+publishing snapshots
    /// on the receive threads, in MILLISECONDS (this world). Thread-safe.</summary>
    public double SnapshotPublishMs =>
        System.Threading.Interlocked.Read(ref _snapPublishTicks) / (double)System.Diagnostics.Stopwatch.Frequency * 1000.0;

    private void FillShipSnapshot(DecisionSnapshot target)
    {
        // Exact-size array: consumers iterate `.Ships` directly, so there must be
        // no phantom trailing entries. Published arrays are never refilled.
        var entities = _ships.SnapshotEntities;
        var arr = target.Ships;
        if (arr.Length != entities.Count) arr = target.Ships = new DecisionShip[entities.Count];
        int n = 0;
        foreach (var e in entities)
        {
            var s = new DecisionShip
            {
                ShipId = e.ShipId,
                SideId = e.SideId,
                SectorId = e.SectorId,
                HullId = e.HullId,
                StationId = e.StationId,
                Flags = e.IsNotVisible || (_botWorld is not null && e.StationId >= 0) ? (byte)0 : (byte)1,
            };
            if (e.Latest is { } lu)
            {
                s.Flags |= 2;   // bit1 = HasLatest (a pose is available)
                s.PosX = lu.Position.X; s.PosY = lu.Position.Y; s.PosZ = lu.Position.Z;
                var rates = lu.TurnRates;
                s.TurnYaw = rates[ControlData.AxisYaw];
                s.TurnPitch = rates[ControlData.AxisPitch];
                s.TurnRoll = rates[ControlData.AxisRoll];
                s.VelX = lu.Velocity.X; s.VelY = lu.Velocity.Y; s.VelZ = lu.Velocity.Z;
                var r = lu.Orientation.GetRight();
                var u = lu.Orientation.GetUp();
                var f = lu.Orientation.GetForward();
                s.R00 = r.X; s.R01 = r.Y; s.R02 = r.Z;
                s.R10 = u.X; s.R11 = u.Y; s.R12 = u.Z;
                s.R20 = -f.X; s.R21 = -f.Y; s.R22 = -f.Z;
            }
            arr[n++] = s;
        }
        System.Array.Sort(arr, 0, n, ShipIdComparer.Instance);

        // P636b — compact the VISIBLE ships into a dedicated array so the bot
        // decision scans walk the visible population instead of every ship.
        int visCount = 0;
        foreach (var s in arr) if ((s.Flags & 1) != 0) visCount++;
        var vis = target.VisibleShips;
        if (vis.Length != visCount) vis = target.VisibleShips = new DecisionShip[visCount];
        int k = 0;
        foreach (var s in arr) if ((s.Flags & 1) != 0) vis[k++] = s;
    }

    private void FillStationSnapshot(DecisionSnapshot target)
    {
        var arr = target.Stations;
        if (arr.Length != _stations.Count) arr = target.Stations = new DecisionStation[_stations.Count];
        int n = 0;
        foreach (var kv in _stations)
        {
            var st = kv.Value;
            arr[n++] = new DecisionStation { ClusterId = st.ClusterId, SideId = st.SideId };
        }
    }

    private void FillWarpSnapshot(DecisionSnapshot target)
    {
        var arr = target.Warps;
        if (arr.Length != _warps.Count) arr = target.Warps = new DecisionWarp[_warps.Count];
        int n = 0;
        foreach (var kv in _warps)
        {
            var w = kv.Value;
            short destCluster = -1;
            if (_warps.TryGetValue(w.DestinationId, out var partner)) destCluster = partner.ClusterId;
            var p = w.Position;
            arr[n++] = new DecisionWarp
            {
                WarpId = w.WarpId,
                ClusterId = w.ClusterId,
                DestWarpId = w.DestinationId,
                DestClusterId = destCluster,
                PosX = p.Length > 0 ? p[0] : 0f,
                PosY = p.Length > 1 ? p[1] : 0f,
                PosZ = p.Length > 2 ? p[2] : 0f,
                Radius = w.Radius,
            };
        }
    }

    /// <summary>P636 — IComparer for the sorted-by-ShipId snapshot ships array
    /// (a static singleton so Array.Sort allocates nothing).</summary>
    private sealed class ShipIdComparer : System.Collections.Generic.IComparer<DecisionShip>
    {
        public static readonly ShipIdComparer Instance = new();
        public int Compare(DecisionShip a, DecisionShip b) => a.ShipId.CompareTo(b.ShipId);
    }

    // P448b — the clock used to stamp ShipEntity.LatestArrivalTime (the local
    // arrival time that anchors the per-ship dead-reckoner). Production uses the
    // real monotonic wall clock (Environment.TickCount64 / 1000.0); tests substitute
    // a scripted clock so update arrivals can be spaced deterministically. Set once
    // before driving Apply, never after the receive path starts. Deliberately the
    // same clock TryGetRenderPose's "now" is measured in.
    public Func<double> NowSeconds { get; set; } = () => Environment.TickCount64 / 1000.0;

    /// <summary>
    /// P612 — the server's sim time scale (<c>sim.timeScale</c>, 0 for local pause, otherwise 0.1–1.0, 1.0 =
    /// normal). Defaults to 1.0 and is refreshed from the server's
    /// <see cref="TimescaleUpdate"/> message. The per-ship dead-reckoners use it
    /// to extrapolate with SIM time (wall elapsed × scale) so remote ships stay
    /// on their authoritative (slowed) positions instead of overshooting and
    /// rubber-banding. Lives on the world (written on the receive thread, read on
    /// the render thread under the gate).</summary>
    private double _timeScale = 1.0;
    public double TimeScale
    {
        get => _timeScale;
        set
        {
            if (_treasureClockSeeded)
            {
                _treasureServerSeconds = TreasureServerSeconds();
                _treasureLocalSeconds = NowSeconds();
            }
            // Integrate the previous rate before applying a new dilation rate.
            long now = (long)(NowSeconds() * 1000);
            if (_countdownClockLocalMs is { } last)
                _countdownClockServerMs += (now - last) * _timeScale;
            if (_countdownClockLocalMs is not null) _countdownClockLocalMs = now;
            _timeScale = value;
        }
    }

    /// <summary>
    /// P540 — resolve the lifepod <c>HullId</c> for an ejected ship's side
    /// (native <c>pShip-&gt;GetSide()-&gt;GetCivilization()-&gt;GetLifepod()</c>,
    /// <c>appmsg.cpp:216</c>). The client world holds no static core, so this is
    /// injected (the MonoGame client wires it to its <c>ShopCatalog</c>;
    /// tests inject a fake). Null when the side's civilization cannot be
    /// resolved — the hull swap is then skipped (graceful degradation, matching
    /// how every other catalog-dependent client path treats an absent core).
    /// The resolver runs on the receive thread (inside <see cref="Apply(Eject)"/>);
    /// Game1 precomputes a stateless delegate and installs it before the wire
    /// path starts, so it needs no locking.
    /// </summary>
    public Func<short, short?> LifepodHullResolver { get; set; } = _ => null;

    // P448b — per-REMOTE-ship dead-reckoners (the port of native CalculateShip),
    // held SEPARATELY from the wire-truth ShipEntity fields so the reckoner state is
    // clearly client interpolation state, never mistaken for server truth. Each is
    // re-seeded at every pose-arrival site (SetCluster / SingleShipUpdate /
    // HeavyShipsUpdate) from the entity's just-written Latest, anchored at that
    // update's local arrival time, and queried by the renderer via TryGetRenderPose.
    // Own-ship prediction is out of scope (P447), so the local ship never gets one.
    private readonly Dictionary<short, Flight.ShipDeadReckoner> _remoteReckoners = new();

    // P448b — rolling re-seed correction-distance statistics: how far the PREVIOUS
    // seed's extrapolation stood from the new authoritative seed when a fresh update
    // arrived. Used to judge (item 4) whether a visual error-decay is warranted.
    private long _remoteCorrectionCount;
    private double _remoteCorrectionSum;
    private float _remoteCorrectionMax;

    private Dictionary<short, DataClusterIGC> _clusters = new();
    private Dictionary<short, DataStationIGC> _stations = new();
    private Dictionary<short, DataWarpIGC> _warps = new();
    private Dictionary<short, DataAsteroidIGC> _asteroids = new();

    /// <summary>P423 — live per-asteroid condition, refreshed by the periodic
    /// ASTEROIDS_UPDATE(179) sync (see <see cref="Apply(AsteroidsUpdate)"/>).</summary>
    private Dictionary<short, AsteroidLiveState> _asteroidLive = new();

    /// <summary>P423 — live per-station condition, refreshed by the periodic
    /// STATIONS_UPDATE(57) sync (see <see cref="Apply(StationsUpdate)"/>).</summary>
    private Dictionary<short, StationLiveState> _stationLive = new();

    /// <summary>P424 — the local player's own pending queued order, or null when
    /// there is none (fresh / accepted / rejected / auto-rejected). Populated by
    /// the owner-only QUEUED_COMMAND_UPDATE(215) message (see
    /// <see cref="Apply(QueuedCommandUpdate)"/>).</summary>
    private QueuedCommandState? _myQueuedCommand;

    /// <summary>P755 — the ShipID of the player who sent the pending queued
    /// order or money request, tracked so Accept/Reject can reply with the
    /// native voAffirmativeSound/voNegativeSound chat (WinTrek.cpp:10596-10705).
    /// NA (-1) when no sender is known.</summary>
    private short _lastCommandOrMoneySender = Na;
    private readonly List<ProjectileEntity> _projectiles = new();
    // P539b — drain cursor for TakeNewProjectiles (index of the first not-yet-drained entry).
    private int _projectileDrainCursor;
    private readonly Dictionary<short, MissileEntity> _missiles = new();
    // P585 — pending opt-in aleph transit signals (a ship crossing an aleph in
    // the viewer's sector), drained each frame by the renderer's pulse path.
    private readonly List<AlephTransitSignal> _alephTransits = new();
    private Dictionary<short, ProbeEntity> _probes = new();
    private Dictionary<short, MineEntity> _mines = new();
    private Dictionary<short, BuildingEffectEntity> _buildingEffects = new();
    private readonly Dictionary<short, TreasureEntity> _treasures = new();
    private readonly List<ChaffEntity> _chaff = new();

    /// <summary>P638 — the world position of the most recently created chaff
    /// decoy (from <c>CREATE_CHAFF(109)</c>), the port's stand-in for native's
    /// <c>m_pchaffLastCreated</c> pointer (appmsg.cpp:1686). The next
    /// <c>MISSILE_SPOOFED(110)</c> retargets the named missile onto this decoy;
    /// <c>END_SPOOFING(111)</c> clears it (native sets the pointer to NULL).</summary>
    private Vector3? _lastChaffPosition;
    private readonly List<ExplosionEntity> _explosions = new();
    private readonly List<HullHitEntity> _hullHits = new();   // P364 impact sparks
    // P623 — VICTIM_DAMAGED(217) attributions (attacker id, damage, ms) for the
    // local ship's TK_TargetHostile window; drained by Game1 once per frame.
    private readonly List<(short AttackerId, float Damage, long Ms)> _victimDamages = new();
    private long _victimDamageVersion;

    /// <summary>HUD-10 — monotonic count of real <c>VICTIM_DAMAGED(217)</c> hits
    /// delivered to this client. The damage flash arms on a change of this value
    /// (a real per-hit event, native <c>DamageShipEvent</c>, trekigc.cpp:4270-4283)
    /// instead of only inferring hits from sampled hull/shield decreases.</summary>
    public long VictimDamageVersion => System.Threading.Interlocked.Read(ref _victimDamageVersion);
    // P463 — one-shot hit SOUND events (my/other × shield/hull) drained into the
    // audio manager each frame; kept separate from _hullHits because the renderer
    // drains those in Draw (after audio runs in Update) and must not lose them.
    private readonly List<HitSoundEntity> _hitSoundEvents = new();
    private const int MaxHitSoundEvents = 64;
    // P463c — one-shot COLLISION events (COLLISION_EVENT(216)) drained into the
    // audio manager each frame; their own stream so the audio pass treats a
    // collision distinctly from a projectile hit.
    private readonly List<CollisionEntity> _collisionEvents = new();
    private const int MaxCollisionEvents = 64;
    // P574 — one-shot WORLD EXPLOSION sound events (every EXPLOSION_EVENT(207)
    // the server sends: ship/station/asteroid/mine blasts), drained into the
    // audio manager each frame so every world explosion gets its native tiered
    // positional sound (trekigc.cpp:86-101). Kept separate from _explosions
    // (the renderer drains that in Draw) for the same reason as hit sounds.
    private readonly List<ExplosionSoundEntity> _explosionSoundEvents = new();
    private const int MaxExplosionSoundEvents = 64;
    private readonly EventLog _events = new();
    private readonly TeamRoster _roster = new();
    // P282 — per-side aggregate ready (TEAM_READY(46)) and the per-side
    // force-ready flag (FORCE_TEAM_READY(47) echo), keyed by side id. Current
    // state, not history — same semantics as TeamRoster.ReadyByShipId.
    private readonly Dictionary<short, bool> _teamReadyBySideId = new();
    private readonly Dictionary<short, bool> _forceReadyBySideId = new();
    // P455b — per-side ACTIVE state from the SIDE_INACTIVE(68) broadcast
    // (native MissionInfo.rgfActive): side id -> whether the side is active.
    // Seeded active-by-default; the server sends SIDE_INACTIVE{bActive=false}
    // when it deactivates a side (pre-game roster shrink P202, or a mid-game
    // elimination P455/P455b). Drives the scoreboard/team-list marking of an
    // eliminated side as "destroyed", exactly native's team list.
    private readonly Dictionary<short, bool> _sideActiveBySideId = new();
    // P366 — the whole-mission SET_TEAM_LEADER(121) broadcast's per-side
    // authoritative leadership state: side id -> the ship id currently leading
    // that side. Mirrors the server's Side.LeaderShipId. Populated on each
    // SET_TEAM_LEADER relay (which the server sends whole-mission on every
    // leadership assignment/transfer, Server.Lobby.cs BroadcastSetTeamLeader);
    // read by IsTeamLeader to gate the leader-only bot dock/launch UI.
    private readonly Dictionary<short, short> _leaderShipIdBySideId = new();
    // P267 — transient one-shot sound events (station destroyed/captured, dev
    // completed) observed on the wire and drained by Game1 into the audio
    // manager each frame (see <see cref="ClientSoundEvent"/>). Bounded and
    // append-only; TakeSoundEvents() clears them after the drain.
    private readonly List<ClientSoundEvent> _soundEvents = new();
    private const int MaxSoundEvents = 64;
    // P578 — transient eject/death/rescue feedback lines (native PostText →
    // SetCriticalTipText/SetTipText) produced by the KILL_SHIP(105) and
    // PLAYER_RESCUED(16) Apply handlers, drained by Game1 each frame into the
    // HUD tip lines + the critical/non-critical message sounds. Bounded.
    private readonly List<FeedbackLine> _feedbackLines = new();
    private const int MaxFeedbackLines = 32;
    // P578 — pending "swing the camera to the killer" requests (native
    // EjectPlayer → OverrideCamera, 5 s), one per own eject/kill of me.
    private readonly List<CameraOverrideRequest> _cameraOverrideRequests = new();
    private const int MaxCameraOverrideRequests = 4;
    // P373 — one-shot "new contact" (first-sighting) cues (brand-new ship with
    // a real identity, or a P370-gone ship coming back into view) recorded on
    // the wire-presence edge and drained by Game1 into the audio manager each
    // frame (see <see cref="ClientNewContactCue"/>). Bounded and append-only;
    // TakeNewContactCues() clears them after the drain.
    private readonly List<ClientNewContactCue> _newContactCues = new();
    private const int MaxNewContactCues = 128;
    // P374 — the current ClusterWarnings level per sector (from ClusterWarningUpdate
    // broadcasts), used by the starmap to draw the flashing threat icons / hover
    // text. Missing sectors read as NoThreat (0).
    private readonly Dictionary<short, short> _clusterWarningBySectorId = new();
    // P374 — transient one-shot sector-threat cues (a warning increase and/or a
    // sector-secured/lost transition) recorded on the ClusterWarningUpdate edge
    // and drained by Game1 into the audio manager each frame (see
    // ClientClusterWarningCue). Bounded and append-only; TakeClusterWarningCues()
    // clears them after the drain.
    private readonly List<ClientClusterWarningCue> _clusterWarningCues = new();
    private const int MaxClusterWarningCues = 64;

    public ShipWarningState ShipWarnings { get; } = new();
    // P265 — the latest in-mission scoreboard snapshot (name/side/kills/deaths
    // per real-side player) streamed by the periodic owner-only
    // ScoreRosterUpdate(209) snapshots plus ordered ScoreRosterDelta(219) patches.
    private ScoreRosterEntry[] _scoreRoster = [];
    // P639 — the rank-name table from RANK_INFO(13), the server's per-player
    // rank-name sync sent once right after LOGONACK on the valid-connect path
    // (Server.Lobby.cs:3344; native appmsg.cpp:3372-3382). Each receive replaces
    // the whole table. Used by the character sheet / scoreboard / team pane to
    // turn a ScoreRosterEntry.Rank id into its display name via
    // Wire.RankNameLookup (a faithful port of BaseClient::LookupRankName).
    private RankInfoEntry[] _rankTable = [];
    // P583 — the latest endgame per-player roster streamed by GameOverPlayers
    // (63), so the game-over screen can render each player's score breakdown.
    private GameOverPlayerEntry[] _gameOverPlayers = [];
    private MissionInfo? _mission;

    // P284 — each side's live development-tech mask (native
    // <c>Side::GetDevelopmentTechs()</c>), keyed by side id. Seeded at join
    // from CREATE_BUCKETS(191) (TtbmDevelopments — that message has no SideId
    // and is sent only to the joining peer right after it is seated, so it
    // applies to MySideId) and updated by SIDE_TECH_CHANGE(75) (which carries
    // SideId) as developments complete. The MonoGame docked-shop buy gate
    // (P284) reads the local player's own side's mask to decide which hulls and
    // parts the hangar offers: an item is offered iff its TtbmRequired ⊆
    // (stationLocal | mySideDev) — the exact P283 containment.
    private readonly Dictionary<short, TechTreeBitMask> _developmentTechsBySideId = new();
    private readonly Dictionary<short, TechTreeBitMask> _initialTechsBySideId = new();

    // P446 — each side's live global-attribute multiplier set (native
    // <c>Side::GetGlobalAttributeSet()</c>, the 25-factor <c>GlobalAttributeSet</c>),
    // keyed by side id. Learned entirely from the SIDE_ATTRIBUTE_CHANGE(76)
    // whole-mission broadcast (there is no per-side join preface carrying it,
    // unlike CREATE_BUCKETS(191) for the tech mask), so a side the client has
    // never seen an update for is absent here and reads as identity via
    // <see cref="GlobalAttributesFor"/>. Consumed by the autopilot's braking
    // estimate (P446) and made available for the client-side prediction tasks
    // P447/P448.
    private readonly Dictionary<short, GlobalAttributeSet> _globalAttributesBySideId = new();

    // P487 — each side's live per-bucket funding snapshot (invested money +
    // invested build time in ms) from BUCKET_STATUS(65), keyed first by side id
    // then by bucket id. The server sends one BUCKET_STATUS per bucket during
    // join sync (Server.Lobby.cs SendBucketSyncPreface) and re-broadcasts one
    // after every donation (Server.Economy.cs BroadcastBucketStatusSideWide),
    // so a side appears here only once a status has been observed. This is the
    // <b>source of truth</b> for the investment pane's progress bars; the pane
    // never locally debits the wallet on Invest — the server's MONEY_CHANGE(60)
    // is the authoritative debit (P442), so MyMoney and these numbers stay in
    // sync with exactly one authoritative writer each.
    private readonly Dictionary<short, Dictionary<short, BucketFunding>> _bucketFundingBySideId = new();
    /// <summary>P703 selected plan from side join sync; no client-side plan substitution.</summary>
    public ResearchBriefing? ResearchBriefing { get; private set; }
    private readonly HashSet<short> _completedDevelopments = new();
    /// <summary>Native DEV_COMPLETED history, retained separately from the bounded sound queue.</summary>
    public IReadOnlySet<short> CompletedDevelopments => _completedDevelopments;
    private readonly Dictionary<short, ResearchProduction> _researchProduction = new();
    private readonly HashSet<short> _retiredResearchProduction = new();
    /// <summary>P703 read-only production identities; pose/lifetime remain native ship messages.</summary>
    public IReadOnlyDictionary<short, ResearchProduction> ResearchProduction => _researchProduction;
    /// <summary>Mission-wide native SHIP_DELETE (fsship.cpp:196-223) can arrive before a
    /// production ship's first pose export. Retain that lifetime fact without creating a phantom ship.</summary>
    public bool IsResearchProductionAlive(short shipId) => !_retiredResearchProduction.Contains(shipId)
        && (!_ships.TryGetValue(shipId, out var ship) || !ship.IsDead);
    private static readonly IReadOnlyDictionary<short, BucketFunding> EmptySideFunding =
        new Dictionary<short, BucketFunding>();

    /// <summary>
    /// P487 — a bucket's invested money and invested build time, as reported by
    /// <c>BUCKET_STATUS(65)</c> (<see cref="Wire.BucketStatus.MoneyTotal"/> /
    /// <see cref="Wire.BucketStatus.TimeTotal"/>). Completion is not stored here —
    /// it is derived at read time by comparing these against the bucket's static
    /// price / build time from the catalog (money ≥ price ∧ time ≥ ttb·1000).
    /// </summary>
    public readonly record struct BucketFunding(long InvestedMoney, long InvestedTimeMs);

    // P271 — the mission-briefing body text the server sends on join
    // (FM_S_SET_BRIEFING_TEXT(9)). In this port's always-generate-civ-briefing
    // mapping (P121) it is the joining side's starting cluster name, plus the
    // fGenerateCivBriefing flag the server always sets true (there is no
    // mission-authored custom story-text field in this port). Empty until the
    // first SET_BRIEFING_TEXT arrives.
    private string _briefingText = "";
    private bool _briefingFGenerateCivBriefing;

    // P759 — the civ-briefing inputs sent with MISSION_BRIEFING_INFO(993), the
    // data native GetCivMissionBriefing reads live (side/civ name, civ list,
    // win conditions, teamscreen.cpp:732-793). Null until the message arrives.
    private MissionBriefingInfo? _missionBriefingInfo;

    // P262 — the end-of-mission snapshot received on FM_S_GAME_OVER(62). Kept
    // separate from _mission (which only mirrors Stage=Over): this is the real
    // reason string, winning side id and per-side records the Game1 overlay
    // renders verbatim. Null until the first GAME_OVER arrives.
    private GameOver? _gameOver;

    // P230 — the local ship's missile-lock snapshot (from the owner-only
    // MISSILE_LOCK_UPDATE(204)) plus the previous-frame state used to edge-detect
    // the partial/full/lost console tips (native console.cpp:929-946).
    private IgcObjectType _prevLockTargetType;
    private MissileLockState _missileLock = MissileLockState.None;
    private MissileLockFeedback.LockState _prevLockState = MissileLockFeedback.LockState.None;
    private short _prevLockTargetId = -1;

    // P255 — the rock the LOCAL player's own ship is currently mining, streamed
    // by the owner-only OwnShipMiningUpdate(206). NA(-1) = not mining.
    private short _miningAsteroidId = -1;

    // P298 — the LOCAL player's own ship's live Kill Bonus readout (0..50, cap
    // 50) and raw streak count, streamed by the owner-only
    // OwnShipKillBonusUpdate(212). Defaults to 0 (a fresh ship has no bonus).
    private float _killBonusPercent = 0f;
    private short _killBonusStreak = 0;

    // HUD-12 — the LOCAL player's own ship's continuous cloak visible fraction
    // (native GetCloaking(), 1 = fully visible), streamed by the owner-only
    // OwnShipCloakUpdate(220). Null until the first message (the HUD then falls
    // back to the CloakActive state bit, the pre-HUD-12 presentation).
    private float? _myCloaking;

    // HUD-12 — remote ships' ripcord activations, from RIPCORD_ACTIVATE(185)
    // (sector-wide on a first activation; FedSrv.CPP:13356-13369). Native's
    // client seeds each remote ship's local countdown on that message
    // (appmsg.cpp:953-970: pship->SetRipcordModel(pship); ResetRipcordTimeLeft())
    // and the radar reads GetRipcordTimeLeft()+1 (radarimage.cpp:1015-1030).
    // Value = local monotonic start instant (ms) of the countdown.
    private readonly Dictionary<short, long> _remoteRipcords = new();

    // P264 — whether an in-sector ENEMY missile's target is the local ship, plus
    // the best lock fraction among them, streamed by the owner-only
    // MissileTrackWarningUpdate(208). Drives the incoming-missile warning loop.
    private MissileTrackState _missileTrack = MissileTrackState.Clear;
    private LeadIndicatorState _leadIndicator = LeadIndicatorState.None;

    /// <summary>The local ship id assigned by SET_CLUSTER(7), with its launch
    /// pose/cookie. JOINED_MISSION's earlier player identity is JoinedShipId.
    /// Keeping these separate preserves the launch contract for consumers.</summary>
    public short? MyShipId { get; private set; }

    /// <summary>appmsg.cpp:2501-2528: the own mission departure returns to browsing.</summary>
    public QuitMission? OwnMissionDeparture { get; private set; }

    private readonly HashSet<short> _mutedPilots = new();
    public bool IsPilotMuted(short shipId) => _mutedPilots.Contains(shipId);

    /// <summary>WinTrek.cpp:3731-3734; trekigc.cpp:4492. Local chat preference,
    /// never FM_CS_MUTE(117), which has no native sender/consumer.</summary>
    public void SetPilotMuted(short shipId, bool muted)
    {
        if (shipId < 0 || shipId == MyShipId) return;
        if (muted) _mutedPilots.Add(shipId); else _mutedPilots.Remove(shipId);
    }

    // ── P711 — ship low-status / combat feedback alarms (trekigc.cpp:1264-1275).
    // Edge-triggered one-shots computed from the local ship's OWN streamed
    // ServerShipUpdate: the previous frame's snapshot (below) vs. the current
    // one yields the downward fraction crossings and the no-ammo/no-fuel
    // attempt edges. Null until the first own-ship update is seen.
    private ShipStatusAlarmState? _prevOwnAlarms;

    /// <summary>P717 — SIM-0243: the local ship's own shield ONLINE state at the
    /// last <see cref="UpdateOwnStatusAlarms"/> sample, for the shields-offline
    /// edge (a shield that was online and then collapsed to ≈0). Null until the
    /// first own-ship update is seen.</summary>
    private bool? _prevOwnShieldOnline;

    /// <summary>P741 — SIM-0241: the local ship's own shield fraction at the last
    /// <see cref="UpdateOwnStatusAlarms"/> sample, for the shield power-up edge
    /// (regenerating from below to at-or-above the 5% threshold,
    /// shieldIGC.cpp:94). Null until the first own-ship update is seen.</summary>
    private float? _prevOwnShieldFraction;

    /// <summary>P735 — SIM-0248: the local ship's own CloakActive stateM bit at
    /// the last <see cref="UpdateOwnStatusAlarms"/> sample, for the cloak-engage
    /// edge (a cloak that was off and then turned on). Null until the first
    /// own-ship update is seen.</summary>
    private bool? _prevOwnCloaked;

    /// <summary>The mutable per-frame driver for the four low-status alarm
    /// one-shots and the two no-ammo/no-fuel attempt cues (SIM-0361..0366).
    /// Game1 calls this each frame under the gate; it samples the local ship's
    /// most recent <c>ServerShipUpdate</c>, edge-detects the native
    /// <c>trekigc.cpp:1264-1275</c> conditions, and records the one-shot
    /// <see cref="ClientSoundEvent"/>s that fired.</summary>
    public void UpdateOwnStatusAlarms()
    {
        if (MyShipId is not { } me) return;
        if (!_ships.TryGetValue(me, out var own) || own.Latest is not { } u) return;

        // "Trying to fire an ammo weapon without enough ammo" / "trying to use
        // the afterburner with no fuel" — the native predicates read the
        // player's firing/boost intent off its own state mask while the
        // resource is depleted (trekigc.cpp:829-889). The port's ServerShipUpdate
        // carries the same StateM bits and the same ammo/fuel fractions, so the
        // faithful edge is: intent bit set while the resource is effectively
        // exhausted (≈0).
        bool firing = (u.StateM & Allegiance.Core.Sim.ShipControlState.AllWeapons) != 0
            || (u.StateM & Allegiance.Core.Sim.ShipControlState.OneWeapon) != 0;
        bool boosting = (u.StateM & Allegiance.Core.Sim.ShipControlState.AfterburnerButton) != 0;

        var next = new ShipStatusAlarmState(
            u.HullFraction, u.AmmoFraction, u.FuelFraction, u.EnergyFraction,
            TryingToFireWithoutAmmo: firing && u.AmmoFraction <= ShipStatusAlarms.AmmoEmptyEpsilon,
            TryingToBoostWithoutFuel: boosting && u.FuelFraction <= ShipStatusAlarms.FuelEmptyEpsilon);

        var prev = _prevOwnAlarms ?? ShipStatusAlarmState.Unarmed;
        foreach (var kind in ShipStatusAlarms.Classify(prev, next))
            RecordSoundEvent(kind, me);
        _prevOwnAlarms = next;

        // P717 — SIM-0243: the shields-offline VO (salShieldsOfflineSound,
        // shieldIGC.cpp:186) fires on the LOCAL ship's own shield collapsing
        // from online to ≈0 (native plays it when overload damage knocks the
        // shield past the downed threshold). Mirrored as a positive→≈0 edge on
        // the streamed ShieldFraction, exactly once per collapse. P741 —
        // SIM-0242: native plays the positional shield power-DOWN cue
        // (m_typeData->deactivateSound, shieldIGC.cpp:185) on the SAME overload
        // branch, so the same edge also fires ShieldDeactivate (shieldup/noshld
        // pair, sounddef.mdl:1310-1311).
        bool shieldOnline = u.ShieldFraction > ShipStatusAlarms.ShieldOfflineEpsilon;
        if (_prevOwnShieldOnline is { } prevOnline && prevOnline && !shieldOnline)
        {
            RecordSoundEvent(ClientSoundEventKind.ShieldsOffline, me);
            RecordSoundEvent(ClientSoundEventKind.ShieldDeactivate, me);
        }
        _prevOwnShieldOnline = shieldOnline;

        // P741 — SIM-0241: the shield power-UP cue (m_typeData->activateSound,
        // shieldIGC.cpp:94) fires when the local ship's own shield regenerates
        // from below 5% to at-or-above 5%. Mirrored as a rising-cross edge on
        // the streamed ShieldFraction, exactly once per power-up.
        if (_prevOwnShieldFraction is { } prevFraction &&
            prevFraction < ShipStatusAlarms.ShieldActivateThreshold &&
            u.ShieldFraction >= ShipStatusAlarms.ShieldActivateThreshold)
            RecordSoundEvent(ClientSoundEventKind.ShieldActivate, me);
        _prevOwnShieldFraction = u.ShieldFraction;

        // P735 — SIM-0248: the cloak-engage VO (salCloakEngageSound,
        // shipIGC.cpp:1608) fires when the ship's CloakActive state bit goes
        // false→true. Native plays it server-side on the cloak state change
        // (PlayNotificationSound(salCloakEngageSound, this)); the port mirrors
        // it as a client one-shot on the LOCAL ship's own streamed StateM bit,
        // exactly once per engagement.
        bool cloaked = (u.StateM & Allegiance.Core.Sim.ShipControlState.CloakActive) != 0;
        if (_prevOwnCloaked is { } prevCloaked && !prevCloaked && cloaked)
            RecordSoundEvent(ClientSoundEventKind.CloakEngaged, me);
        _prevOwnCloaked = cloaked;
    }

    /// <summary>
    /// P462 — the P462 own-shot-echo de-dup predicate, installed by the MonoGame
    /// client's <c>OwnShotEcho</c>. When non-null, <c>Apply(ProjectileInstance)</c>
    /// consults it before creating a projectile entity: if it reports the
    /// incoming <c>PROJECTILE_INSTANCE(12)</c> is our own shot's server echo (the
    /// local tracer already rendered and sounded it at the trigger frame), the
    /// echo is dropped rather than re-rendered/re-sounded — so each shot renders
    /// and sounds exactly once, at the trigger, and other ships' rounds are
    /// untouched. The delegate receives the round's projectile type, its spawn
    /// position, our server-reported position, and the local receipt instant, and
    /// is invoked on the transport receive thread under the world gate.
    /// </summary>
    public Func<short, Vector3, Vector3, long, bool>? OwnEchoDropPredicate { get; set; }

    /// <summary>
    /// P542 — the relationship of a round's launcher to the local ship, used by
    /// the <see cref="ProjectileDiagnostics"/> counters to break received/drawn
    /// rounds down by who fired them (the task's "own / teammate / enemy" axis).
    /// </summary>
    public enum ProjectileRelation { Own = 0, Teammate = 1, Enemy = 2 }

    /// <summary>
    /// P542 — lightweight once-per-second-able counters tracing a received
    /// <c>PROJECTILE_INSTANCE</c> through every hop of the client pipeline:
    /// received, dropped as our own echo, and (at the render/gather stage)
    /// radius-culled, camera-culled, and actually drawn — broken down by
    /// <see cref="ProjectileRelation"/> where the axis is meaningful. This is
    /// what pairs the client's perspective with the server's <c>[projectile] …
    /// -&gt; n peer(s)</c> lines to find WHERE other ships' rounds get lost.
    /// Counters accumulate cheaply on the receive/draw threads; emission is
    /// throttled elsewhere (see <see cref="ProjectileDiagnostics.FormatLine"/>).
    /// </summary>
    public sealed class ProjectileDiagnostics
    {
        public long ReceivedOwn, ReceivedTeammate, ReceivedEnemy;
        /// <summary>Dropped by the P462 own-echo predicate. Pre-P542 a foreign
        /// same-type round near us could land here (the bug); after the fix only
        /// <c>Own</c> ever increments.</summary>
        public long DroppedOwn, DroppedTeammate, DroppedEnemy;
        public long RadiusCulled, CameraCulled;
        public long DrawnOwn, DrawnTeammate, DrawnEnemy;
        // P648 — incoming rounds from a shooter this viewer cannot place on the
        // live world: latched dead (ship saw SDR_KILLED and not yet revived),
        // never posed (no entity/pose for the shooter yet), or stale (pose
        // older than the dead-reckoner's 1.5 s hold window). This is the
        // client-side "shots from ships I can't place" measurement that isolates
        // the invisible/frozen-shooter symptom per category (see Apply(ProjectileInstance)).
        public long FromLatchedDead, FromNeverPosed, FromStale;

        public long TotalReceived => ReceivedOwn + ReceivedTeammate + ReceivedEnemy;
        public long TotalDropped => DroppedOwn + DroppedTeammate + DroppedEnemy;
        public long TotalDrawn => DrawnOwn + DrawnTeammate + DrawnEnemy;

        public void RecordReceived(ProjectileRelation r)
        {
            switch (r)
            {
                case ProjectileRelation.Own: ReceivedOwn++; break;
                case ProjectileRelation.Teammate: ReceivedTeammate++; break;
                default: ReceivedEnemy++; break;
            }
        }
        public void RecordDropped(ProjectileRelation r)
        {
            switch (r)
            {
                case ProjectileRelation.Own: DroppedOwn++; break;
                case ProjectileRelation.Teammate: DroppedTeammate++; break;
                default: DroppedEnemy++; break;
            }
        }
        public void RecordDrawn(ProjectileRelation r)
        {
            switch (r)
            {
                case ProjectileRelation.Own: DrawnOwn++; break;
                case ProjectileRelation.Teammate: DrawnTeammate++; break;
                default: DrawnEnemy++; break;
            }
        }

        /// <summary>One dense line for the <c>[proj-diag]</c> log, emitted once per
        /// second (and only while <see cref="TotalReceived"/> is non-zero). All
        /// figures are cumulative since the last <see cref="Clear"/>.</summary>
        public string FormatLine() =>
            $"received own={ReceivedOwn} tm={ReceivedTeammate} en={ReceivedEnemy} " +
            $"dropped-as-own own={DroppedOwn} tm={DroppedTeammate} en={DroppedEnemy} " +
            $"radius-culled={RadiusCulled} camera-culled={CameraCulled} " +
            $"drawn own={DrawnOwn} tm={DrawnTeammate} en={DrawnEnemy} " +
            $"unplaceable dead={FromLatchedDead} neverposed={FromNeverPosed} stale={FromStale}";

        public void Clear()
        {
            ReceivedOwn = ReceivedTeammate = ReceivedEnemy = 0;
            DroppedOwn = DroppedTeammate = DroppedEnemy = 0;
            RadiusCulled = CameraCulled = 0;
            DrawnOwn = DrawnTeammate = DrawnEnemy = 0;
            FromLatchedDead = FromNeverPosed = FromStale = 0;
        }
    }

    /// <summary>The live <see cref="ProjectileDiagnostics"/> for this world
    /// (per-connection; <see cref="Reset"/> clears it).</summary>
    public ProjectileDiagnostics Diag { get; } = new();

    /// <summary>Classify a round's launcher against the local ship. Own = our own
    /// ship id; Teammate = a same-side visible ship; anything else (enemy or
    /// unknown/not-yet-known shooter) = Enemy.</summary>
    public ProjectileRelation ClassifyLauncher(short launcherId, IgcObjectType launcherType = IgcObjectType.Ship)
    {
        if (launcherType == IgcObjectType.Probe)
            return MySideId is { } side && _probes.TryGetValue(launcherId, out var probe) && probe.SideId == side
                ? ProjectileRelation.Teammate : ProjectileRelation.Enemy;
        if (launcherType != IgcObjectType.Ship) return ProjectileRelation.Enemy;
        if (MyShipId is { } me && launcherId == me) return ProjectileRelation.Own;
        if (MySideId is { } mySide && _ships.TryGetValue(launcherId, out var ent) && ent.SideId == mySide)
            return ProjectileRelation.Teammate;
        return ProjectileRelation.Enemy;
    }

    /// <summary>P648 — how "placeable" an actor ship (e.g. a round's shooter) is
    /// for this viewer. Feeds the "shots from a ship I can't place" diagnostic
    /// and the per-ship pose-age report; also the client half of the invisible/
    /// frozen-shooter symptom classification (latched dead / never posed /
    /// stale pose). <see cref="Live"/> = a current, non-dead pose.</summary>
    public enum ShipPlaceability { Live = 0, LatchedDead = 1, NeverPosed = 2, Stale = 3 }

    /// <summary>P648 — the dead-reckoner's smooth extrapolation horizon
    /// (<see cref="ShipDeadReckoner"/>.ExtrapolationCap = 1.5 s). Beyond this a
    /// remote pose is HELD, not extrapolated, so an actor with a pose this old
    /// is "frozen" to the viewer while its shots still render.</summary>
    public const double StalePoseSeconds = 1.5;

    /// <summary>P648 — age (seconds) of the freshest pose this viewer holds for
    /// <paramref name="shipId"/>; <see cref="double.PositiveInfinity"/> when the
    /// ship is unknown or has never been posed.</summary>
    public double PoseAgeSeconds(short shipId)
    {
        if (_ships.TryGetValue(shipId, out var e) && e.Latest is not null && e.LatestArrivalTime > 0)
            return Math.Max(0.0, NowSeconds() - e.LatestArrivalTime);
        return double.PositiveInfinity;
    }

    /// <summary>P648 — classify an actor ship for this viewer: own/local ship is
    /// always <see cref="ShipPlaceability.Live"/>; otherwise latched-dead beats
    /// never-posed beats stale-beats-live (a ship that was SDR_KILLED and not yet
    /// revived by a re-presence signal is the "invisible shooter" case; a stale
    /// pose is the "frozen but rendered" case).</summary>
    public ShipPlaceability ClassifyShipPlaceability(short shipId)
    {
        if (MyShipId is { } me && shipId == me) return ShipPlaceability.Live;
        if (!_ships.TryGetValue(shipId, out var e)) return ShipPlaceability.NeverPosed;
        if (e.IsDead) return ShipPlaceability.LatchedDead;
        if (e.Latest is null) return ShipPlaceability.NeverPosed;
        return PoseAgeSeconds(shipId) > StalePoseSeconds ? ShipPlaceability.Stale : ShipPlaceability.Live;
    }

    /// <summary>
    /// P260 — the local player's own side id, captured from the
    /// own-ship EXPORT(26) (<c>ShipId = NA(-1)</c> dummy) whose <c>SideId</c> the
    /// server seats at creation (Server.cs:~1781). The MonoGame client otherwise
    /// tracks no sides at all; this is the only wire source of the player's own
    /// team, used to gate the probe ambient loop to the player's own side.
    /// Null until that own-ship EXPORT arrives.</summary>
    public short? MySideId { get; private set; }

    // ── P442 per-player money (native keeps this on PlayerInfo per ship) ─────

    /// <summary>P442 — per-player money, keyed by ship id, mirroring native's
    /// per-<c>PlayerInfo</c> money (appmsg.cpp:1339-1353). Populated by
    /// <c>SET_MONEY(115)</c> (absolute), <c>MONEY_CHANGE(60)</c> (delta),
    /// <c>AUTODONATE(116)</c> one-time gifts and <c>PAYDAY(114)</c> credits; a
    /// ship is present only once a money message names it. The HUD reads
    /// <see cref=\"MyMoney\"/>; the shop's affordability (P468) will read this.</summary>
    public IReadOnlyDictionary<short, int> WalletByShipId => _moneyByShipId;

    /// <summary>P442 — the auto-donate target ship id per player ship, from
    /// <c>AUTODONATE(116)</c> (<c>NA</c> is stored as absent). A ship with no
    /// entry donates to no one (the native leader/opted-out case).
    /// <c>PAYDAY(114)</c> reads this to route each player's share to its target
    /// when one is set, else to the ship itself (native appmsg.cpp:1281-1323).</summary>
    public IReadOnlyDictionary<short, short> AutoDonateTargetByShipId => _autoDonateTargetByShipId;

    /// <summary>P442 — the local player's current money (their own wallet), or
    /// <c>null</c> until the first <c>SET_MONEY(115)</c> for the local ship is
    /// decoded. The HUD renders <em>nothing</em> while this is <c>null</c> —
    /// "money unknown" is deliberately distinct from showing 0.</summary>
    public int? MyMoney
    {
        get
        {
            if (MyShipId is not { } id) return null;
            return _moneyByShipId.TryGetValue(id, out var m) ? m : null;
        }
    }

    private readonly Dictionary<short, int> _moneyByShipId = new();
    private readonly Dictionary<short, short> _autoDonateTargetByShipId = new();

    private const short Na = -1;

    /// <summary>P752c — native <c>OnButtonAutoDonate</c> zeroes the local wallet
    /// immediately after sending the whole-wallet auto-donate
    /// (<c>teampane.cpp:1241</c>, <c>SetMoney(0)</c>). The server's
    /// <c>AUTODONATE(116)</c> relay then skips debiting the local donor (native
    /// <c>pshipBy != m_ship</c>, appmsg.cpp:1248-1263 — see
    /// <see cref="Apply(AutoDonateUpdate)"/>), so the wallet is debited exactly
    /// once. No-op until the own ship is assigned.</summary>
    public void ZeroOwnWallet()
    {
        if (MyShipId is { } me) _moneyByShipId[me] = 0;
    }

    /// <summary>P442 — credit a money delta to a ship's wallet (creating the
    /// entry on first contact). Money is an int on the wire (native 32-bit), so
    /// the deltas land in that same width.</summary>
    private void AddMoney(short ship, int delta)
    {
        _moneyByShipId.TryGetValue(ship, out var cur);
        _moneyByShipId[ship] = cur + delta;
    }

    /// <summary>P442 — the set of player ships this client has been told about:
    /// the union of the wallet and auto-donate key sets. <c>SET_MONEY(115)</c>
    /// and <c>AUTODONATE(116)</c> are the join-sync messages the server sends
    /// once per side player (Server.Economy.cs SyncMoneyStateToJoiner +
    /// the join-time own SET_MONEY), so this union IS the side's players — the
    /// client-side stand-in for native's <c>GetSide()-&gt;GetShips()</c> player
    /// filter that PAYDAY iterates.</summary>
    private IEnumerable<short> KnownPlayerShips()
    {
        HashSet<short> seen = new(_moneyByShipId.Keys);
        foreach (var k in _autoDonateTargetByShipId.Keys) seen.Add(k);
        return seen;
    }

    /// <summary>P366 — the ship id currently leading <paramref name="sideId"/>,
    /// from the whole-mission <c>SET_TEAM_LEADER(121)</c> broadcast. Null until
    /// a leadership announcement for that side is observed.</summary>
    public short? TeamLeaderShipIdFor(short sideId) =>
        _leaderShipIdBySideId.TryGetValue(sideId, out var s) ? s : (short?)null;

    /// <summary>P366 — is the LOCAL player the elected team leader of their own
    /// side? True iff we know our ship and side (SET_CLUSTER + own-ship EXPORT)
    /// and the per-side leader recorded from SET_TEAM_LEADER equals our own
    /// ship id. Gates the leader-only bot dock/launch command UI — a non-leader
    /// must see no such option at all (native WinTrek.cpp:4349-4368 gates the
    /// same way on <c>IsTeamLeader()</c>).</summary>
    public bool IsTeamLeader =>
        MyShipId is { } me && MySideId is { } sid
        && _leaderShipIdBySideId.TryGetValue(sid, out var l) && l == me;

    /// <summary>P281 — the latest SIDE_ROSTER(211) snapshot the server pushed
    /// during the post-connect side-select stage: the mission's joinable sides
    /// (display name + live member count). Null until that broadcast arrives;
    /// replaced wholesale on each receive. The MonoGame client renders the
    /// side-select screen from it and sends FM_C_POSITIONREQ(39) on confirm.</summary>
    public SideRoster? SideRoster { get; private set; }

    /// <summary>P281 — the latest SET_MISSION_OWNER(123) whole-mission broadcast
    /// (side + ship that now owns the mission), kept verbatim so the owner-gated
    /// randomize-teams control can be surfaced only to the current mission owner.
    /// Null until the first broadcast arrives.</summary>
    public SetMissionOwner? MissionOwnerBroadcast { get; private set; }

    /// <summary>P281 — whether the local player is currently the mission owner.
    /// Derived by comparing the latest <see cref="MissionOwnerBroadcast"/> owner
    /// ship against the current <see cref="MyShipId"/>; evaluated live on each
    /// read, so it re-evaluates correctly when MyShipId first becomes known
    /// (post-SET_CLUSTER).</summary>
    public bool AmMissionOwner =>
        MissionOwnerBroadcast is { } mo && MyShipId is { } mine && mo.ShipId == mine;

    /// <summary>P271 — the mission-briefing body text received on
    /// <c>FM_S_SET_BRIEFING_TEXT(9)</c>. In this port's always-generate-civ-
    /// briefing mapping it is the joining side's starting cluster name (the
    /// server's <c>Text</c> field, P121). Empty until the first
    /// SET_BRIEFING_TEXT arrives; the MonoGame client renders it as the
    /// dismissible mission-briefing popup during the pre-launch countdown.</summary>
    public string BriefingText => _briefingText;

    /// <summary>P759 — the live civ-briefing inputs from MISSION_BRIEFING_INFO(993),
    /// or null until the join-sync burst delivers them. The MonoGame client feeds
    /// this to <see cref="Rendering.MissionBriefingGenerator"/> for the
    /// native-shaped civ prose + random tip.</summary>
    public MissionBriefingInfo? MissionBriefing => _missionBriefingInfo;

    /// <summary>P271 — the <c>fGenerateCivBriefing</c> flag from the last
    /// <c>SET_BRIEFING_TEXT</c>. The port's server always sends true (P121);
    /// preserved verbatim so the client could in principle distinguish an
    /// authored story text (false) from a generated location briefing (true).</summary>
    public bool BriefingFGenerateCivBriefing => _briefingFGenerateCivBriefing;

    /// <summary>Ships keyed by ship id, snapshot-consistent (never mutated
    /// while exposed — callers should hold the caller's lock).</summary>
    public IReadOnlyDictionary<short, ShipEntity> Ships => _botWorld is null ? _ships.Shared : _ships;

    /// <summary>P448b — registered by the host client to resolve a REMOTE ship's
    /// already-effective hull stats (plus its afterburner max thrust) for the per-ship
    /// dead-reckoner. The resolving body owns the hull-record lookup (shop catalogue /
    /// static core) and the side's global-attribute factors; ClientWorld stays pure of
    /// that game data. Returns null when the hull/side can't be resolved yet (hull id
    /// not yet known, core missing) — the ship is then rendered on its raw pose until
    /// a later update resolves it. Never invoked for the local player's own ship.</summary>
    public Func<short, short, (Flight.ShipDeadReckoner.HullStats Stats, float AfterburnerMaxThrust)?>?
        RemoteShipHullResolver { get; set; }

    /// <summary>
    /// P633 — whether to maintain a per-remote-ship <see cref="Flight.ShipDeadReckoner"/>
    /// (seeded on every pose arrival) for smooth render interpolation. DEFAULT
    /// TRUE for the graphical client. A HEADLESS consumer (the allegiance-mcp bot
    /// harness) reads only the raw <c>Latest</c> pose and never calls
    /// <see cref="TryGetRenderPose"/>, so it turns this OFF: with N bots × M remote
    /// ships each seeding an allocation on every heavy update, that is real,
    /// nobody-consumed per-bot work and GC pressure at 200v200 (item 4). When off,
    /// the reckoner dictionary stays empty and no per-remote-ship object is
    /// created; <see cref="TryGetRenderPose"/> simply reports "no reckoner" (the
    /// caller falls back to the raw pose — correct for a headless consumer).
    /// </summary>
    public bool ReckonRemotes { get; set; } = true;

    /// <summary>P448b — the dead-reckoned render pose for a remote ship at local time
    /// <paramref name="now"/> (same monotonic clock as <see cref="ShipEntity.LatestArrivalTime"/>:
    /// seconds since boot, <c>Environment.TickCount64 / 1000.0</c>), replaying the real
    /// flight model forward from the last authoritative seed via
    /// <see cref="Flight.ShipDeadReckoner.PoseAt"/>. Returns false — and the caller
    /// renders the ship's raw <c>Latest</c> pose — when the ship has no reckoner
    /// (the local ship, never seeded, or unresolvable hull). The returned
    /// <paramref name="orientation"/> is that reckoner's reusable scratch object,
    /// overwritten on the next call; callers must <c>Copy()</c> it if they retain it
    /// beyond the immediate use.</summary>
    public bool TryGetRenderPose(int shipId, double now, out Vector3 position, out Orientation orientation)
    {
        if (_remoteReckoners.TryGetValue((short)shipId, out var r) && r.Seeded)
        {
            r.TimeScale = TimeScale;      // P612 — extrapolate in scaled sim time
            r.PoseAt(now, out position, out orientation);
            return true;
        }
        position = default;
        orientation = null!;
        return false;
    }

    /// <summary>P448b — (re-)seed the dead-reckoner for a remote ship from its just-
    /// written <see cref="ShipEntity.Latest"/>, anchored at that update's local arrival
    /// time. Called under the world gate from each of the three pose-arrival Apply sites
    /// right after they stamp <c>Latest</c>/<c>LatestArrivalTime</c>. Only REMOTE ships
    /// are reckoned (own-ship prediction is P447, out of scope); the local ship's
    /// reckoner is dropped so a recycled id never reuses stale extrapolation.</summary>
    private void ReseedRemoteReckoner(ShipEntity e)
    {
        // P633 — headless consumers (the bot harness) never render remote ships,
        // so skip maintaining a dead-reckoner per remote ship per heavy update
        // entirely (real per-bot work + GC pressure nobody consumes at 200v200).
        if (!ReckonRemotes) return;
        if (e.IsMine)
        {
            _remoteReckoners.Remove(e.ShipId);
            return;
        }
        if (e.Latest is not { } up) return;                   // no pose to anchor from
        if (RemoteShipHullResolver is not { } resolve) return; // no resolver → raw poses
        if (resolve(e.SideId, e.HullId) is not { } rs) return; // hull not resolvable yet → keep prior seed / raw

        var r = GetReckoner(e.ShipId);

        // Item 4 — measure the re-seed correction: how far the PREVIOUS seed's
        // extrapolation stood from this new authoritative seed at the moment it
        // arrived. Only meaningful once a seed exists (skip the very first seed,
        // which has nothing to correct against).
        if (r.Seeded && e.LatestArrivalTime > r.ArrivalTime)
        {
            r.PoseAt(e.LatestArrivalTime, out var prevPos, out _);
            float corr = (prevPos - up.Position).Length();
            _remoteCorrectionCount++;
            _remoteCorrectionSum += corr;
            if (corr > _remoteCorrectionMax) _remoteCorrectionMax = corr;
        }

        r.Seed(up, rs.Stats, rs.AfterburnerMaxThrust, e.LatestArrivalTime);
    }

    private Flight.ShipDeadReckoner GetReckoner(short shipId)
    {
        if (!_remoteReckoners.TryGetValue(shipId, out var r))
        {
            r = new Flight.ShipDeadReckoner();
            _remoteReckoners[shipId] = r;
        }
        return r;
    }

    /// <summary>P448b — the accumulated re-seed correction-distance statistics (count,
    /// mean, max in metres) since the last call, then reset. The host client polls this
    /// periodically to log the typical re-seed jump and decide whether a visual
    /// error-decay (item 4) is warranted. Thread-safety: call under the world gate.</summary>
    public (long Count, double Mean, float Max) TakeRemoteCorrectionStats()
    {
        var result = (
            Count: _remoteCorrectionCount,
            Mean: _remoteCorrectionCount > 0 ? _remoteCorrectionSum / _remoteCorrectionCount : 0d,
            Max: _remoteCorrectionMax);
        _remoteCorrectionCount = 0;
        _remoteCorrectionSum = 0d;
        _remoteCorrectionMax = 0f;
        return result;
    }

    /// <summary>Clusters keyed by ClusterId, from EXPORT(26)/OT_cluster (13).</summary>
    public IReadOnlyDictionary<short, DataClusterIGC> Clusters => _clusters;

    /// <summary>Stations keyed by StationId, from EXPORT(26)/OT_station (1).</summary>
    public IReadOnlyDictionary<short, DataStationIGC> Stations => _stations;

    /// <summary>Warps keyed by WarpId, from EXPORT(26)/OT_warp (7).</summary>
    public IReadOnlyDictionary<short, DataWarpIGC> Warps => _warps;

    /// <summary>Asteroids keyed by AsteroidId, from EXPORT(26)/OT_asteroid (5).</summary>
    public IReadOnlyDictionary<short, DataAsteroidIGC> Asteroids => _asteroids;

    /// <summary>P423 — live per-asteroid condition (ore + health fractions),
    /// keyed by AsteroidId, from the periodic ASTEROIDS_UPDATE(179) sync.
    /// Empty until the first update is decoded.</summary>
    public IReadOnlyDictionary<short, AsteroidLiveState> AsteroidLive => _asteroidLive;

    /// <summary>P423 — live per-station condition (hull + shield fractions),
    /// keyed by StationId, from the periodic STATIONS_UPDATE(57) sync.
    /// Empty until the first update is decoded.</summary>
    public IReadOnlyDictionary<short, StationLiveState> StationLive => _stationLive;

    /// <summary>P424 — the local player's own pending queued order, or
    /// <c>null</c> when none. Driven by the owner-only QUEUED_COMMAND_UPDATE(215)
    /// message; the MonoGame client's ACCEPT / REJECT / CLEAR keys read it (see
    /// <see cref="Apply(QueuedCommandUpdate)"/>).</summary>
    public QueuedCommandState? MyQueuedCommand => _myQueuedCommand;

    /// <summary>Oldest pending ballot, native clintlib.cpp:2497-2507.
    /// CANCEL_BALLOT removes every matching id without disturbing other votes.</summary>
    public PendingBallotView? ActiveBallot => GetActiveBallot();
    private readonly List<PendingBallotView> _pendingBallots = new();

    public void DismissBallot(short ballotId) => _pendingBallots.RemoveAll(b => b.BallotId == ballotId);

    public PendingBallotView? GetActiveBallot(long? localMs = null)
    {
        // clintlib.cpp:2497-2507: strict expiration < now, oldest first.
        if (_countdownClockLocalMs is { } anchor)
        {
            double now = _countdownClockServerMs + ((localMs ?? (long)(NowSeconds() * 1000)) - anchor) * TimeScale;
            while (_pendingBallots.Count > 0 && _pendingBallots[0].TimeExpiration < now)
                _pendingBallots.RemoveAt(0);
        }
        return _pendingBallots.FirstOrDefault();
    }

    /// <summary>P626 — a lightweight, client-safe view of an active ballot (the
    /// ballot text + id the server announced), kept deliberately small so the
    /// whole wire object isn't cached client-side. Consumed by the vote keys and
    /// a small HUD hint.</summary>
    public sealed record PendingBallotView(short BallotId, string BallotText, uint TimeExpiration = uint.MaxValue);

    /// <summary>Transient projectile instances, in arrival order, pruned by
    /// expiry via <see cref="PruneExpiredProjectiles"/>. A projectile carries no
    /// persistent id (native fire-and-forget), so this is a growing list, not an
    /// id-keyed dict — see <see cref="ProjectileEntity"/>.</summary>
    public IReadOnlyList<ProjectileEntity> Projectiles => _projectiles;

    /// <summary>Live missile instances keyed by MissileId, from the creation
    /// FIRE_MISSILE(70) plus the per-tick MISSILE_UPDATE(205), removed by
    /// MISSILE_DESTROYED(83) / the stale pruner. Unlike projectiles a missile
    /// carries a real id, so this is an id-keyed dict (see
    /// <see cref="MissileEntity"/>).</summary>
    public IReadOnlyDictionary<short, MissileEntity> Missiles => _missiles;

    /// <summary>P260 — live deployed probes keyed by ProbeId, from the creation
    /// EXPORT(26)/OT_probe (P224) and removed by PROBE_DESTROYED(85). A probe is
    /// static (fixed position), so no per-tick stream updates it (see
    /// <see cref="ProbeEntity"/>).</summary>
    public IReadOnlyDictionary<short, ProbeEntity> Probes => _probes;

    /// <summary>P291 — live deployed mines keyed by MineId, from the creation
    /// EXPORT(26)/OT_mine (P94) and removed by MINE_DESTROYED(84). A mine is
    /// static (fixed position), so no per-tick stream updates it (see
    /// <see cref="MineEntity"/>).</summary>
    public IReadOnlyDictionary<short, MineEntity> Mines => _mines;

    /// <summary>P294 — live in-progress station constructions keyed by the
    /// target <c>AsteroidId</c>, from the creation
    /// <c>FM_S_EXPORT(26)/OT_buildingEffect</c> broadcast and removed by
    /// <c>FM_S_BUILDINGEFFECT_DESTROYED(181)</c> (interruption) or by
    /// <see cref="PruneExpiredBuildingEffects"/> when the build completes
    /// (native self-expires its cosmetic effect at <c>m_timeComplete</c>, so no
    /// completion message exists). A building effect is transient — it appears
    /// over the target asteroid and disappears — so it carries a local receipt
    /// anchor and a <see cref="BuildingEffectEntity.ProgressFraction"/> reveal
    /// (see <see cref="BuildingEffectEntity"/>).</summary>
    public IReadOnlyDictionary<short, BuildingEffectEntity> BuildingEffects => _buildingEffects;

    /// <summary>P293 — live treasures keyed by TreasureId, from the creation
    /// <c>FM_S_TREASURE(28)</c> (the DROP_PART part-treasure and treasure-ack
    /// re-spawn broadcasts) and removed by <c>FM_S_DESTROY_TREASURE(30)</c>
    /// (on pickup/expiry). Position follows the exported trajectory
    /// (see <see cref="TreasureEntity"/>).</summary>
    public IReadOnlyDictionary<short, TreasureEntity> Treasures => _treasures;

    /// <summary>P292 — live deployed chaff in arrival order, pruned by expiry
    /// via <see cref="PruneExpiredChaff"/>. A chaff carries no persistent id and
    /// no destruction wire message (native chaff expiry just removes the model
    /// from its cluster), so — like <see cref="Projectiles"/> — it is a growing
    /// list, not an id-keyed dict, and its lifetime is resolved from its type
    /// (see <see cref="ChaffEntity"/>).</summary>
    public IReadOnlyList<ChaffEntity> Chaff => _chaff;

    /// <summary>P263 — live explosions in arrival order, pruned by expiry via
    /// <see cref="PruneExpiredExplosions"/>. Fire-and-forget visuals like
    /// projectiles, so a growing list (no persistent id), one entry per real
    /// EXPLOSION_EVENT(207) broadcast (see <see cref="ExplosionEntity"/>).</summary>
    public IReadOnlyList<ExplosionEntity> Explosions => _explosions;

    /// <summary>P364 — live projectile-impact sparks in arrival order, pruned by
    /// expiry via <see cref="PruneExpiredHullHits"/>. Fire-and-forget visuals like
    /// explosions, so a growing list (no persistent id), one entry per real
    /// PROJECTILE_HULL_HIT(213) broadcast (see <see cref="HullHitEntity"/>).</summary>
    public IReadOnlyList<HullHitEntity> HullHits => _hullHits;

    /// <summary>P364 — atomically drain (clear and return) the pending
    /// projectile-impact sparks. Called once per frame by <c>Game1</c> under the
    /// gate lock so each real PROJECTILE_HULL_HIT(213) is emitted into the spark
    /// particle pool exactly once (the drain-then-forget lifecycle, mirroring
    /// <see cref="TakeSoundEvents"/>). The renderer then filters to the local
    /// ship's own sector when emitting.</summary>
    public List<HullHitEntity> TakeNewHullHits()
    {
        var taken = new List<HullHitEntity>(_hullHits);
        _hullHits.Clear();
        return taken;
    }

    /// <summary>P623 — VICTIM_DAMAGED(217) attributions buffered since the last
    /// drain: who last hurt the local ship and by how much, fed to Game1's
    /// TK_TargetHostile tracker (the port of native's DamageBucket population).
    /// Drained atomically under the gate lock exactly once per frame.</summary>
    public List<(short AttackerId, float Damage, long Ms)> TakeNewVictimDamages()
    {
        var taken = new List<(short, float, long)>(_victimDamages);
        _victimDamages.Clear();
        return taken;
    }

    /// <summary>P539b — atomically drain the newly-arrived projectile instances
    /// since the last call (a cursor over <see cref="_projectiles"/>), for
    /// receivers that need to count REAL fired rounds (e.g. the MCP combat bot
    /// counting its own shots via <see cref="ProjectileEntity.LauncherId"/> ==
    /// its ship id) rather than a synthetic fire decision. The MonoGame client
    /// reads <see cref="_projectiles"/> live and prunes it by expiry, so it
    /// never drains; a MCP bot connection (which never prunes) drains here. A
    /// defensive reset guards against an external prune shrinking the list
    /// underneath the cursor.</summary>
    /// <summary>Headless observers count their own rounds without retaining rendering entities.
    /// Set before receiving traffic. The game client leaves this false.</summary>
    public bool CountOnlyProjectiles { get; set; }
    private long _ownProjectileCount;
    public long TakeOwnProjectileCount()
    {
        long count = _ownProjectileCount;
        _ownProjectileCount = 0;
        return count;
    }

    public List<ProjectileEntity> TakeNewProjectiles()
    {
        if (_projectileDrainCursor > _projectiles.Count) _projectileDrainCursor = 0;
        if (_projectileDrainCursor >= _projectiles.Count) return new List<ProjectileEntity>();
        var taken = new List<ProjectileEntity>(_projectiles.GetRange(_projectileDrainCursor, _projectiles.Count - _projectileDrainCursor));
        _projectileDrainCursor = _projectiles.Count;
        return taken;
    }

    /// <summary>P463 — pending one-shot hit SOUND events (my/other × shield/hull)
    /// in arrival order, one per real PROJECTILE_HULL_HIT(213) broadcast. Game1's
    /// audio slice drains them via <see cref="TakeHitSoundEvents"/> each frame;
    /// kept distinct from <see cref="HullHits"/> (which the renderer drains in
    /// Draw) so the audio pass that runs in Update doesn't steal them.</summary>
    public IReadOnlyList<HitSoundEntity> HitSoundEvents => _hitSoundEvents;

    /// <summary>P463 — atomically drain (clear and return) the pending hit-sound
    /// events. Called once per frame by <c>Game1</c>'s audio update under the gate
    /// lock so each real hit is sounded exactly once.</summary>
    public List<HitSoundEntity> TakeHitSoundEvents()
    {
        var copy = new List<HitSoundEntity>(_hitSoundEvents);
        _hitSoundEvents.Clear();
        return copy;
    }

    /// <summary>P463c — pending collision events (one per real
    /// <c>COLLISION_EVENT(216)</c> broadcast) in arrival order. Game1's audio
    /// slice drains them via <see cref="TakeCollisionEvents"/> each frame.</summary>
    public IReadOnlyList<CollisionEntity> CollisionEvents => _collisionEvents;

    /// <summary>P463c — atomically drain (clear and return) the pending collision
    /// events. Called once per frame by <c>Game1</c>'s audio update under the gate
    /// lock so each real collision is sounded exactly once.</summary>
    public List<CollisionEntity> TakeCollisionEvents()
    {
        var copy = new List<CollisionEntity>(_collisionEvents);
        _collisionEvents.Clear();
        return copy;
    }

    /// <summary>P574 — pending WORLD EXPLOSION sound events (one per real
    /// <c>EXPLOSION_EVENT(207)</c> broadcast) in arrival order. Game1's audio
    /// slice drains them via <see cref="TakeExplosionSoundEvents"/> each frame so
    /// every world explosion is voiced exactly once.</summary>
    public List<ExplosionSoundEntity> TakeExplosionSoundEvents()
    {
        var copy = new List<ExplosionSoundEntity>(_explosionSoundEvents);
        _explosionSoundEvents.Clear();
        return copy;
    }

    /// <summary>P267 — one-shot sound events (station destroyed/captured, dev
    /// completion) observed on the wire, in arrival order. Game1 drains them via
    /// <see cref="TakeSoundEvents"/> into the audio manager.</summary>
    public IReadOnlyList<ClientSoundEvent> SoundEvents => _soundEvents;

    /// <summary>P267 — atomically drain (clear and return) the pending sound
    /// events. Called once per frame by <c>Game1</c> under the gate lock so each
    /// observed event is played exactly once.</summary>
    public List<ClientSoundEvent> TakeSoundEvents()
    {
        var copy = new List<ClientSoundEvent>(_soundEvents);
        _soundEvents.Clear();
        return copy;
    }

    /// <summary>P578 — the pending eject/death/rescue HUD feedback lines
    /// (native PostText → SetCriticalTipText/SetTipText) in arrival order.
    /// Game1 drains them via <see cref="TakeFeedbackLines"/> each frame into
    /// the HUD tip lines and the critical/non-critical message sounds.</summary>
    public IReadOnlyList<FeedbackLine> FeedbackLines => _feedbackLines;

    /// <summary>P578 — atomically drain (clear and return) the pending feedback
    /// lines. Called once per frame by <c>Game1</c> under the gate lock so each
    /// eject/kill/rescue is shown and voiced exactly once.</summary>
    public List<FeedbackLine> TakeFeedbackLines()
    {
        var copy = new List<FeedbackLine>(_feedbackLines);
        _feedbackLines.Clear();
        return copy;
    }

    /// <summary>P578 — the pending "swing the camera to the killer" requests.
    /// Game1 drains them via <see cref="TakeCameraOverrideRequests"/> and turns
    /// each into a 5-second override camera aimed at the killer (native
    /// OverrideCamera, WinTrek.cpp:6501).</summary>
    public IReadOnlyList<CameraOverrideRequest> CameraOverrideRequests => _cameraOverrideRequests;

    /// <summary>P578 — atomically drain (clear and return) the pending camera-
    /// override requests. Called once per frame by <c>Game1</c> under the gate
    /// lock so each own eject/kill swings the camera exactly once.</summary>
    public List<CameraOverrideRequest> TakeCameraOverrideRequests()
    {
        var copy = new List<CameraOverrideRequest>(_cameraOverrideRequests);
        _cameraOverrideRequests.Clear();
        return copy;
    }

    /// <summary>P373 — the pending one-shot "new contact" (first-sighting)
    /// cues, in arrival order (see <see cref="ClientNewContactCue"/>). Game1
    /// drains them via <see cref="TakeNewContactCues"/> into the audio manager.</summary>
    public IReadOnlyList<ClientNewContactCue> NewContactCues => _newContactCues;

    /// <summary>P373 — atomically drain (clear and return) the pending
    /// first-sighting cues. Called once per frame by <c>Game1</c> under the
    /// gate lock so each genuine sighting edge plays exactly once (drain-then-
    /// forget, mirroring <see cref="TakeSoundEvents"/>).</summary>
    public List<ClientNewContactCue> TakeNewContactCues()
    {
        var copy = new List<ClientNewContactCue>(_newContactCues);
        _newContactCues.Clear();
        return copy;
    }

    /// <summary>P374 — the current <see cref="ClusterWarnings"/> level for a
    /// sector (0 = no threat when unknown or unset). Read under the caller's
    /// gate lock by the starmap to flash the threat icons and show hover text.</summary>
    public short ClusterWarningFor(short sectorId) =>
        _clusterWarningBySectorId.TryGetValue(sectorId, out var w) ? w : ClusterWarnings.NoThreat;

    /// <summary>P374 — the pending one-shot sector-threat cues (warning increase
    /// and/or sector-secured/lost), in arrival order (see
    /// <see cref="ClientClusterWarningCue"/>). Game1 drains them via
    /// <see cref="TakeClusterWarningCues"/> into the audio manager.</summary>
    public IReadOnlyList<ClientClusterWarningCue> ClusterWarningCues => _clusterWarningCues;

    /// <summary>P374 — atomically drain (clear and return) the pending
    /// sector-threat cues. Called once per frame by <c>Game1</c> under the gate
    /// lock so each genuine threat transition plays exactly once
    /// (drain-then-forget, mirroring <see cref="TakeSoundEvents"/> /
    /// <see cref="TakeNewContactCues"/>).</summary>
    public List<ClientClusterWarningCue> TakeClusterWarningCues()
    {
        var copy = new List<ClientClusterWarningCue>(_clusterWarningCues);
        _clusterWarningCues.Clear();
        return copy;
    }

    /// <summary>The cluster-connectivity graph from a single CLUSTERINFO(200);
    /// null until that message is seen (set once, not re-built on repeats).</summary>
    public ClusterGraph? Graph { get; private set; }

    /// <summary>
    /// P634 — the client-side "world complete" signal: the routing
    /// GRAPH (CLUSTERINFO) and at least one cluster have arrived, so a routing
    /// consumer (the MCP bot harness's <c>EnsureCombatPath</c>) can resolve a
    /// cluster path. False until then. This is the P634 measurement's endpoint
    /// ("time from logon to world complete"), which lets a join-time world-sync
    /// stall be distinguished from a live-streaming stall: a bot whose world
    /// data is starved behind the position stream stays !WorldReady and loiters
    /// at its home sector (P633's ~224/390 symptom).
    /// </summary>
    public bool WorldReady => Graph is not null && Clusters.Count > 0;

    /// <summary>The transient "what just happened" log — chat, kills, paydays,
    /// player-ready relays — in arrival order (event-table state, not
    /// entity-table state; no upsert semantics, bounded window, see
    /// <see cref="EventLog"/>).</summary>
    public EventLog Events => _events;

    /// <summary>The current team-lobby roster — ship id → currently ready flag
    /// (upserted, last write wins), distinct from the append-only ready history
    /// in <see cref="EventLog.PlayerReadyEchoes"/>. Starts empty, never null;
    /// <see cref="TeamRoster"/> for the semantics.</summary>
    public TeamRoster Roster => _roster;

    /// <summary>P282 — <c>TEAM_READY(46)</c>: the per-side aggregate ready flag,
    /// keyed by side id (last write wins). The server broadcasts this to the
    /// whole mission whenever a side's aggregate readiness flips (P180's
    /// RecomputeSideReady, <c>fsmission.cpp:5449-5471</c>). Empty until the
    /// first broadcast; a side absent here reads as "not ready".</summary>
    public IReadOnlyDictionary<short, bool> TeamReadyBySideId => _teamReadyBySideId;

    /// <summary>P282 — <c>FORCE_TEAM_READY(47)</c>: the per-side force-ready
    /// flag, keyed by side id, from the whole-mission forward of the leader's
    /// force trigger (the server forwards it to every mission peer incl. the
    /// sender). Empty until the first forward; a side absent here reads as
    /// not forced.</summary>
    public IReadOnlyDictionary<short, bool> ForceReadyBySideId => _forceReadyBySideId;

    /// <summary>P455b — <c>SIDE_INACTIVE(68)</c>: whether the named side is
    /// active. Unknown sides default to active (the server only ever sends a
    /// <c>bActive=false</c> to DEACTIVATE a side, plus a possible later
    /// re-activation via the P202 pre-game toggle), so an absent entry means
    /// the side is alive. Mirrors native's <c>MissionInfo::SideActive(sideID)</c>.</summary>
    public bool IsSideActive(short sideId)
        => _sideActiveBySideId.TryGetValue(sideId, out var b) ? b : true;

    /// <summary>P455b — the mission-wide <c>AllowEmptyTeams</c> (AET) parameter,
    /// set only when a <c>SIDE_INACTIVE</c> arrives with <c>bChangeAET=true</c>
    /// (native appmsg.cpp:2585-2587 updates the AET bit under that flag; the
    /// P455 elimination broadcast carries <c>bChangeAET=false</c>, so this stays
    /// null during normal deactivation).</summary>
    public bool? AllowEmptyTeams { get; private set; }

    /// <summary>P265 — the latest in-mission scoreboard snapshot (name / side /
    /// kills / deaths per real-side player), streamed by the periodic owner-only
    /// ScoreRosterUpdate(209) plus ScoreRosterDelta(219); empty until
    /// the first snapshot lands.</summary>
    public IReadOnlyList<ScoreRosterEntry> ScoreRoster => _scoreRoster;

    /// <summary>P639 — the rank-name table from RANK_INFO(13). Empty until the
    /// server's post-logon rank sync arrives; see <see cref="RankNameLookup"/>
    /// for resolving a rank id to a name.</summary>
    public IReadOnlyList<RankInfoEntry> RankTable => _rankTable;

    /// <summary>P583 — the latest endgame per-player roster streamed by
    /// GameOverPlayers(63): each connected player's score + counter breakdown
    /// at game over. Replaced wholesale on each receive; empty until the
    /// first endgame roster lands.</summary>
    public IReadOnlyList<GameOverPlayerEntry> GameOverPlayersList => _gameOverPlayers;

    /// <summary>P284 — the live development-tech mask (<c>DevelopmentTechs</c>,
    /// native <c>GetDevelopmentTechs()</c>) per side id, from CREATE_BUCKETS(191)
    /// at join and SIDE_TECH_CHANGE(75) on research. The MonoGame docked-shop
    /// buy gate reads the local player's own side's mask here. Empty until the
    /// first CREATE_BUCKETS arrives; a side absent here reads as "tech unknown".</summary>
    public IReadOnlyDictionary<short, TechTreeBitMask> DevelopmentTechsBySideId => _developmentTechsBySideId;

    /// <summary>P284 — convenience read of <see cref="DevelopmentTechsBySideId"/>
    /// (null when this side has no recorded dev techs yet).</summary>
    public TechTreeBitMask? DevelopmentTechsFor(short sideId) =>
        _developmentTechsBySideId.TryGetValue(sideId, out var m) ? m : null;

    /// <summary>The stable bucket-catalog seed from CREATE_BUCKETS.TtbmInitial
    /// (native appmsg.cpp:2386-2387), independent of current shop/research techs.</summary>
    public TechTreeBitMask? InitialTechsFor(short sideId) =>
        _initialTechsBySideId.TryGetValue(sideId, out var m) ? m : null;

    /// <summary>P446 — the live global-attribute multiplier set
    /// (<c>GlobalAttributeSet</c>, native <c>Side::GetGlobalAttributeSet()</c>)
    /// per side id, from SIDE_ATTRIBUTE_CHANGE(76) whole-mission broadcasts as
    /// developments complete. Identity (all 1.0) when this side is unknown —
    /// the honest neutral before the first broadcast, matching a no-bonus
    /// side.</summary>
    public IReadOnlyDictionary<short, GlobalAttributeSet> GlobalAttributesBySideId => _globalAttributesBySideId;

    /// <summary>P446 — convenience read of <see cref="GlobalAttributesBySideId"/>:
    /// the side's multiplier set, or <see cref="GlobalAttributeSet.Identity"/>
    /// when no SIDE_ATTRIBUTE_CHANGE has been seen for it yet.</summary>
    public GlobalAttributeSet GlobalAttributesFor(short sideId) =>
        _globalAttributesBySideId.TryGetValue(sideId, out var g) ? g : GlobalAttributeSet.Identity;

    /// <summary>P487 — a side's per-bucket funding snapshot (bucket id →
    /// invested money/time from <c>BUCKET_STATUS(65)</c>), or an empty map when no
    /// status has been observed for the side yet (a mid-mission side the client
    /// joined before its buckets broadcast, or simply none seen).</summary>
    public IReadOnlyDictionary<short, BucketFunding> BucketFundingFor(short sideId) =>
        _bucketFundingBySideId.TryGetValue(sideId, out var m) ? m : EmptySideFunding;

    /// <summary>P487 — one bucket's funding, or null when no <c>BUCKET_STATUS</c>
    /// has been seen for that side+bucket pair (treat as 0 invested).</summary>
    public BucketFunding? BucketFundingOf(short sideId, short bucketId) =>
        _bucketFundingBySideId.TryGetValue(sideId, out var m) && m.TryGetValue(bucketId, out var f)
            ? f
            : null;

    /// <summary>
    /// P363 — resolve a side id to its RGBA colour (floats 0..1) from the last
    /// <see cref="SideRoster"/> snapshot (which carries each side's colour from
    /// the server's real <c>Side.ColorR..A</c> — the port of native
    /// <c>pside-&gt;GetColor()</c>, shipIGC.cpp:149). Used to tint the ship
    /// motion trail per side. Returns <c>false</c> (and the caller falls back to
    /// the neutral gray) when no roster has been received yet or the side id is
    /// absent from it.</summary>
    public bool TryGetSideColor(short sideId, out float r, out float g, out float b)
    {
        if (SideRoster is { } roster)
        {
            foreach (var s in roster.Sides)
            {
                if (s.SideId == sideId)
                {
                    r = s.R; g = s.G; b = s.B;
                    return true;
                }
            }
        }
        r = 0.5f; g = 0.5f; b = 0.5f;
        return false;
    }

    /// <summary>Persistent state about the mission itself (static core, cookie,
    /// owner, stage) — distinct from any one ship. Null until the first
    /// <c>MISSIONDEF(38)</c> or <c>MISSION_STAGE(37)</c> arrives.</summary>
    public MissionInfo? Mission { get { return _mission; } }

    /// <summary>P262 — the end-of-mission snapshot from the last
    /// <c>FM_S_GAME_OVER(62)</c> broadcast: the real reason string
    /// <see cref="GameOver.GameoverMessage"/>), winning side id
    /// (<see cref="GameOver.SideWinner"/>, <see cref="GameOver.NA"/>
    /// on a draw) and the six per-side endgame records. <c>null</c> until the
    /// first GAME_OVER arrives — the MonoGame client shows its on-screen
    /// result panel only once this is set (i.e. only on a real end-of-game).</summary>
    public GameOver? GameOverResult => _gameOver;

    /// <summary>The local ship's missile lock (P230) — the snapshot delivered on
    /// the owner-only <c>MISSILE_LOCK_UPDATE(204)</c> (server-computed, P226).
    /// <see cref="MissileLockState.None"/> until the first such message. Read under
    /// the caller's lock like the other world tables; <c>Game1</c> uses it to gate
    /// the lock tone, draw the target box, and reflect the three lock states.</summary>
    public MissileLockState MissileLock => _missileLock;

    /// <summary>P264 — whether an in-sector enemy missile's target is the local
    /// ship (and the best lock among them), streamed on the owner-only
    /// <c>MissileTrackWarningUpdate(208)</c>. Read each frame by Game1 to drive
    /// the incoming-missile warning loop.</summary>
    public MissileTrackState MissileTrack => _missileTrack;

    /// <summary>P255 — the <see cref="DataAsteroidIGC.AsteroidId"/> of the rock
    /// the local player's own ship is currently mining, or -1 (NA) when it is
    /// not mining. Streamed on the owner-only <c>OwnShipMiningUpdate(206)</c>;
    /// the MonoGame client resolves this id against <see cref="Asteroids"/> to
    /// orient the own hull toward the mine target.</summary>
    public short MiningAsteroidId => _miningAsteroidId;

    /// <summary>P298 — the local player's own ship's live Kill Bonus readout
    /// (native <c>100·(GetExperienceMultiplier()−1)</c>, 0..50 cap 50), streamed
    /// on the owner-only <c>OwnShipKillBonusUpdate(212)</c>. 0 = no bonus (a
    /// fresh ship). Feeds the HUD's Kill Bonus gauge.</summary>
    public float KillBonusPercent => _killBonusPercent;

    /// <summary>P414 — whether the LOCAL player's own ship currently has a
    /// ripcord countdown active (a <c>RIPCORD_ACTIVATE(185)</c> naming our own
    /// ship was the last ripcord edge; cleared on <c>RIPCORD_ABORTED(187)</c>,
    /// a re-target to nowhere, or our own <c>SET_CLUSTER</c> when the teleport
    /// lands). Drives the HUD's "RIPCORDING" tag; the Game1 key handler reads
    /// it to decide initiate-vs-cancel.</summary>
    public bool RipcordActive { get; private set; }

    /// <summary>P414 — the requested target cluster of the ripcord named in the
    /// last <c>RIPCORD_ACTIVATE(185)</c> for our own ship (the REQUESTED sector,
    /// <c>sidRipcord</c> — not necessarily the anchor's own sector). Meaningful
    /// only while <see cref="RipcordActive"/> is true; the HUD resolves it to a
    /// sector name via <see cref="Clusters"/>.</summary>
    public short? RipcordTargetSectorId { get; private set; }

    /// <summary>P414 — local monotonic instant (<see cref="Environment.TickCount64"/>)
    /// at which <see cref="RipcordActive"/> was last set (the last
    /// <c>RIPCORD_ACTIVATE(185)</c> for our own ship). The server never sends a
    /// dedicated "ripcord completed" message (the teleport is silent on the
    /// wire), so the MonoGame client uses this + the own hull's
    /// <c>RipcordSpeed</c> (the real countdown duration) to self-expire the HUD
    /// tag when the countdown would have completed. <c>null</c> when no ripcord
    /// is active. Reset on abort / reconnect / completion.</summary>
    public long? RipcordActiveSinceLocalMs { get; private set; }

    /// <summary>P298 — the raw kill-streak count behind
    /// <see cref="KillBonusPercent"/> (native <c>m_experience</c>), for the
    /// gauge's kill tally. Streamed on the same <c>OwnShipKillBonusUpdate(212)</c>.</summary>
    public short KillBonusStreak => _killBonusStreak;

    /// <summary>HUD-12 — the LOCAL ship's native <c>GetCloaking()</c> visible
    /// fraction (1 = fully visible; 0 = fully cloaked), from the owner-only
    /// <c>OwnShipCloakUpdate(220)</c>. Null until the first message; the HUD
    /// derives <c>strength = 100·(1−Cloaking)</c> and
    /// <c>IsCloaked = Cloaking &lt; 1</c> (<c>consoledata.cpp:480-489,563-575</c>).</summary>
    public float? MyCloaking => _myCloaking;

    /// <summary>HUD-12 — the local monotonic start instant (ms) of a REMOTE
    /// ship's ripcord, recorded from the sector-wide <c>RIPCORD_ACTIVATE(185)</c>
    /// exactly like native's remote handler (<c>appmsg.cpp:953-970</c>). The
    /// radar derives the countdown with the ship's own hull <c>RipcordSpeed</c>,
    /// which is what the native client's <c>ResetRipcordTimeLeft()</c> does.
    /// False for the own ship (its state lives in
    /// <see cref="RipcordActiveSinceLocalMs"/>) and for ships with no active
    /// ripcord.</summary>
    public bool TryGetRemoteRipcord(short shipId, out long startedLocalMs) =>
        _remoteRipcords.TryGetValue(shipId, out startedLocalMs);

    /// <summary>HUD-12 — the native <c>MissionParams::iLives</c> from
    /// <c>MISSIONDEF(38)</c>, or the unlimited sentinel until it arrives.</summary>
    public short MissionLives => _mission?.ILives ?? Allegiance.Core.Wire.MissionDef.UnlimitedLives;

    /// <summary>HUD-12 — the LOCAL player's mission kills/deaths, the native
    /// <c>PlayerInfo::MissionKills()/MissionDeaths()</c> pair
    /// (<c>clintlib.h:346-347</c>). Seeded from <c>PLAYERINFO(10)</c>
    /// (<c>appmsg.cpp:2254-2256</c>) and kept current from <c>KILL_SHIP(105)</c>
    /// (<c>appmsg.cpp:2766-2773,2807-2810</c>). False until the own ship's
    /// PlayerInfo is known.</summary>
    public bool TryGetOwnMissionStats(out short kills, out short deaths)
    {
        kills = 0;
        deaths = 0;
        if (MyShipId is not { } me || !_ships.TryGetValue(me, out var own) || own.Player is not { } info)
            return false;
        kills = info.NKills;
        deaths = info.NDeaths;
        return true;
    }

    /// <summary>P266 — the local ship's predictive lead-aim point, streamed on the
    /// owner-only <c>LeadIndicatorUpdate(210)</c> (server-computed from the real
    /// <c>solveForLead</c> physics, <see cref="Wire.LeadIndicatorUpdate"/>). The
    /// server solves where the mounted weapon must point to intercept the ship's
    /// selected moving target and gates it by the <c>c_habmLeadIndicator</c> hull
    /// capability / relay scanner; the MonoGame client renders the reticle at the
    /// projected lead world point. <see cref="LeadIndicatorState.None"/> until the
    /// first such message; refreshed every tick (the server always sends, even to
    /// clear). Read under the caller's lock like the other world tables.</summary>
    public LeadIndicatorState LeadIndicator => _leadIndicator;

    /// <summary>Local receipt instant of the latest SET_START_TIME announcement.
    /// Reset when the restart hold is replaced by the briefing deadline.
    /// CountdownRemainingSeconds uses the absolute server-time target.</summary>
    public long? CountdownStartLocalMs { get; private set; }
    private uint? _countdownTargetMs;
    private uint? _missionTimeStartMs;
    /// <summary>Native OnMissionCountdown event serial (appmsg.cpp:2330-2334).
    /// SET_START_TIME changes only the epoch, not the audio change detector.</summary>
    public long CountdownVersion { get; private set; }
    private double _countdownClockServerMs;
    private long? _countdownClockLocalMs;

    /// <summary>P660: native SET_START_TIME deadline in server simulation time.
    /// PING samples provide the epoch; wall extrapolation uses the live scale.</summary>
    public int? CountdownRemainingSeconds(long? localMs = null)
    {
        if (_countdownTargetMs is not { } target || _countdownClockLocalMs is not { } anchor) return null;
        double now = _countdownClockServerMs + ((localMs ?? (long)(NowSeconds() * 1000)) - anchor) * TimeScale;
        return Math.Max(0, (int)Math.Ceiling((target - now) / 1000.0));
    }

    /// <summary>HUD-10 — native mission-clock input <c>dt = time - timeStart</c>
    /// (WinTrek.cpp:8131-8139), evaluated on the same PING-anchored server clock
    /// the countdown sound uses. Null until both the epoch and a server-time
    /// sample are known; the caller applies <see cref="Rendering.MissionClock"/>
    /// to turn it into countdown remaining or elapsed time.</summary>
    public double? MissionElapsedSeconds(long? localMs = null)
    {
        if (_missionTimeStartMs is not { } start || _countdownClockLocalMs is not { } anchor) return null;
        double now = _countdownClockServerMs + ((localMs ?? (long)(NowSeconds() * 1000)) - anchor) * TimeScale;
        return Math.Max(0d, (now - start) / 1000.0);
    }

    /// <summary>Native CheckCountdownSound (WinTrek.cpp:11135-11156). The
    /// announcer lags by one second; Starting adds one AFTER truncation,
    /// whereas Started adds it BEFORE truncation. Keep this separate from HUD
    /// ceiling arithmetic. MISSIONDEF supplies late joiners' duration/epoch;
    /// SET_START_TIME replaces the epoch for the briefing and ensuing game.</summary>
    public int? MissionCountdownSoundSeconds(long? localMs = null)
    {
        if (_missionTimeStartMs is not { } start || _countdownClockLocalMs is not { } anchor) return null;
        double now = _countdownClockServerMs + ((localMs ?? (long)(NowSeconds() * 1000)) - anchor) * TimeScale;
        double untilStart = (start - now) / 1000.0;
        if (_mission?.Stage == MissionStageMessage.StageStarting)
            return Math.Max(0, (int)untilStart + 1);
        if (_mission?.Stage == MissionStageMessage.StageStarted && _mission.GameLengthSeconds != 0f)
            return Math.Max(0, (int)(_mission.GameLengthSeconds + untilStart + 1));
        return null;
    }

    public bool IsIntermission => _countdownTargetMs is not null
        && _mission?.Stage == MissionStageMessage.StageNotStarted;

    private bool _treasureClockSeeded;
    private double _treasureServerSeconds;
    private double _treasureLocalSeconds;
    private double TreasureServerSeconds() => _treasureServerSeconds
        + (NowSeconds() - _treasureLocalSeconds) * TimeScale;

    /// <summary>Most recent authoritative PING sim-clock sample, in seconds. The ship pose
    /// Time slot is a tick index in this port and must never be interpreted as milliseconds.</summary>
    public double SimulationTimeSeconds => _treasureClockSeeded ? _treasureServerSeconds : 0;

    public void ObserveServerTime(uint serverMs, long localMs)
    {
        _treasureClockSeeded = true;
        _treasureServerSeconds = serverMs / 1000.0;
        _treasureLocalSeconds = NowSeconds();
        _countdownClockServerMs = serverMs;
        _countdownClockLocalMs = localMs;
    }

    /// <summary>
    /// P321 — reset the world to the pristine state it had at construction,
    /// dropping every entity table, the local-ship/side assignment
    /// (<see cref="MyShipId"/>/<see cref="MySideId"/>), and all derived/current
    /// snapshots (roster, mission, briefing, dev-tech masks, per-side ready
    /// flags, transient lists, graph). Called by
    /// <see cref="Session.ConnectionSession.Connect"/> at the start of a fresh
    /// connection attempt so a retry never inherits stale ships/ids from a
    /// previous failed or dead connection.
    ///
    /// Importantly this does NOT change the same-session <c>??=</c> semantics of
    /// <see cref="MyShipId"/> in <see cref="Apply(SetCluster)"/>: within one live
    /// session (sector-transit <c>SET_CLUSTER</c>s reusing the same ship id) the
    /// world is never Reset between them, so the first-assignment-wins pattern
    /// still behaves identically. Reset only fires across a fresh
    /// <see cref="Session.ConnectionSession.Connect"/> boundary. This method is
    /// intended to be called under the caller's <see cref="Session.ConnectionSession.Gate"/>
    /// lock, exactly where the receive thread mutates the same tables.
    /// </summary>
    public void Reset()
    {
        DetachBotWorld();
        OwnMissilesFired = 0;
        _waitingForRestart = false;
        SectorGeneration++;
        ShipWarnings.Reset();
        MissionGeneration++;
        MyShipId = null;
        OwnMissionDeparture = null;
        _mutedPilots.Clear();
        _prevOwnAlarms = null;   // P711 — fresh session: re-arm the low-status alarm snapshots
        MyAcceptedCommand = null;
        MyCurrentCommand = null;
        _myQueuedCommand = null;
        _moneyRequest = null;
        JoinedShipId = null;
        JoinedMissionCookie = null;
        JoinError = null;
        MissionCore = null;
        MySideId = null;
        // P636 — a fresh connection starts with an empty published snapshot; a
        // reader holding the pre-reconnect snapshot keeps a valid (stale) object.
        _publishedDecision = DecisionSnapshot.Empty;
        SideRoster = null;
        MissionOwnerBroadcast = null;
        Graph = null;
        CountdownStartLocalMs = null;
        _countdownTargetMs = null;
        _missionTimeStartMs = null;
        _countdownClockLocalMs = null;
        _mission = null;
        _gameOver = null;
        _gameOverPlayers = [];
        _scoreRoster = [];
        _briefingText = "";
        _briefingFGenerateCivBriefing = false;
        _missionBriefingInfo = null;
        _missileLock = MissileLockState.None;
        _prevLockState = MissileLockFeedback.LockState.None;
        _prevLockTargetId = -1;
        _missileTrack = MissileTrackState.Clear;
        _leadIndicator = LeadIndicatorState.None;
        _miningAsteroidId = -1;
        _killBonusPercent = 0f;
        _killBonusStreak = 0;
        _myCloaking = null;              // HUD-12 — no stale cloak fraction across missions
        _remoteRipcords.Clear();         // HUD-12 — remote ripcords do not survive a reset
        ClearRipcord();                  // P414 — drop any ripcord state on reconnect

        _ships.Clear();
        _clusters.Clear();
        _stations.Clear();
        _warps.Clear();
        _warpBombs.Clear();   // P758 — no live warp bombs survive a reset
        _asteroids.Clear();
        _asteroidLive.Clear();
        _stationLive.Clear();
        _projectiles.Clear();
        _ownProjectileCount = 0;
        _projectileDrainCursor = 0;
        _missiles.Clear();
        _lastMissileFiredByShip.Clear();
        MarkSnapshotDirty();
        Diag.Clear();
        _probes.Clear();
        _mines.Clear();
        _buildingEffects.Clear();
        _treasures.Clear();
        _treasureClockSeeded = false;
        _chaff.Clear();
        _explosions.Clear();
        _explosionSoundEvents.Clear();
        _hullHits.Clear();
        _teamReadyBySideId.Clear();
        _forceReadyBySideId.Clear();
        _sideActiveBySideId.Clear();     // P455b — per-side active state resets on reconnect
        AllowEmptyTeams = null;          // P455b — AET bit resets on reconnect
        _leaderShipIdBySideId.Clear();
        _developmentTechsBySideId.Clear();
        _globalAttributesBySideId.Clear();
        _bucketFundingBySideId.Clear();
        ResearchBriefing = null;
        _lastBases = new();
        _completedDevelopments.Clear();
        _researchProduction.Clear();
        _retiredResearchProduction.Clear();
        _initialTechsBySideId.Clear();
        _moneyByShipId.Clear();                    // P442 — per-player wallet state
        _autoDonateTargetByShipId.Clear();         // P442 — per-player donate targets
        _feedbackLines.Clear();
        _cameraOverrideRequests.Clear();
        _soundEvents.Clear();
        _newContactCues.Clear();   // P373 — drop any pending first-sighting cues on disconnect
        _highlightedClusters.Clear();
        Array.Fill(_allianceGroups, (sbyte)-1);
        ViewedClusterId = null;
        TelemetrySectorOverride = null;
        ViewedClusterPosition = null;
        ViewClusterVersion = 0;
        _clusterWarningBySectorId.Clear();   // P374 — drop per-sector threat state on disconnect
        _clusterWarningCues.Clear();         // P374 — drop any pending threat cues on disconnect
        _events.Clear();
        _roster.Clear();
        ResetLobby();
        ResetInterfaceState();
    }

    /// <summary>Upsert the world state from a decoded message. Switch on the
    /// concrete decoded type; every ship branch targets the same ship id, so
    /// any interleaving yields one entity per id. Entity-table entries for the
    /// non-ship objects are upserted by their own ids (replace-on-arrival).</summary>
    public void Apply(IFedMessage msg)
    {
        switch (msg)
        {
            case JoinedMission joined:
                Apply(joined);
                break;
            case ObjectSpotted spotted:
                Apply(spotted);
                break;
            case ChangeTeamCiv messageChangeTeamCiv:
                Apply(messageChangeTeamCiv);
                break;
            case AsteroidDestroyed messageAsteroidDestroyed:
                Apply(messageAsteroidDestroyed);
                break;
            case ProbesUpdate messageProbesUpdate:
                Apply(messageProbesUpdate);
                break;
            case QuitMission messageQuitMission:
                Apply(messageQuitMission);
                break;
            case QuitSide messageQuitSide:
                Apply(messageQuitSide);
                break;
            case EnterGame messageEnterGame:
                Apply(messageEnterGame);
                break;
            case GameState messageGameState:
                Apply(messageGameState);
                break;
            case SetWingId wing:
                Apply(wing);
                break;
            case OrderChange order:
                Apply(order);
                break;
            case Promote promote:
                Apply(promote);
                break;
            case HighlightCluster highlight:
                Apply(highlight);
                break;
            case ViewClusterReply viewCluster:
                Apply(viewCluster);
                break;
            case SetCluster setCluster:
                Apply(setCluster);
                break;
            case SingleShipUpdate update:
                Apply(update);
                break;
            case ShipStatusMessage status:
                Apply(status);
                break;
            case HeavyShipsUpdate heavy:
                Apply(heavy);
                break;
            case LightShipsUpdate light:
                Apply(light);
                break;
            case ExportShip exportShip:
                Apply(exportShip);
                break;
            case ExportCluster exportCluster:
                Apply(exportCluster);
                break;
            case ExportStation exportStation:
                Apply(exportStation);
                break;
            case ExportWarp exportWarp:
                Apply(exportWarp);
                break;
            case ExportAsteroid exportAsteroid:
                Apply(exportAsteroid);
                break;
            case AsteroidsUpdate asteroidsUpdate:
                // P423 — the periodic per-asteroid ore/health sync.
                Apply(asteroidsUpdate);
                break;
            case StationsUpdate stationsUpdate:
                // P423 — the periodic per-station hull/shield sync.
                Apply(stationsUpdate);
                break;
            case QueuedCommandUpdate queuedCommandUpdate:
                // P424 — the owner-only "you have a pending queued order" sync.
                Apply(queuedCommandUpdate);
                break;
            case Reload reload:
                Apply(reload);
                break;
            case SwapPart swapPart:
                Apply(swapPart);
                break;
            case DropPart dropPart:
                Apply(dropPart);
                break;
            case Allegiance.Core.Wire.RankInfo rankInfo:
                Apply(rankInfo);
                break;
            case PlayerInfo playerInfo:
                Apply(playerInfo);
                break;
            case ClusterInfo clusterInfo:
                Apply(clusterInfo);
                break;
            case DockedNotify dockedNotify:
                Apply(dockedNotify);
                break;
            case BuyLoadoutAck buyLoadoutAck:
                Apply(buyLoadoutAck);
                break;
            case Allegiance.Core.Wire.LoadoutChange loadoutChange:
                // P648 — FM_S_LOADOUT_CHANGE(96): a ship's loadout/hull changed
                // (same-side broadcast; a post-death relaunch sends this with the
                // new hull). Adopts hull/parts, re-seats passengers, and revives
                // a ship the viewer had latched dead.
                Apply(loadoutChange);
                break;
            case ChatMessage chatMessage:
                Apply(chatMessage);
                break;
            case Ballot ballot:
                // P626 — a new in-flight ballot was announced (P187); latch it
                // as ActiveBallot + surface a system chat line.
                Apply(ballot);
                break;
            case CancelBallot cancelBallot:
                // clintlib/appmsg.cpp:377-392: remove all queued matches.
                DismissBallot(cancelBallot.BallotId);
                break;
            case PositionReqBroadcast positionRequest:
                Apply(positionRequest);
                break;
            case DelPositionReq removeRequest:
                Apply(removeRequest);
                break;
            case LockLobby lockLobby:
                Apply(lockLobby);
                break;
            case LockSides lockSides:
                Apply(lockSides);
                break;
            case AutoAccept autoAccept:
                Apply(autoAccept);
                break;
            case ChatBuoy chatBuoy:
                Apply(chatBuoy);
                break;
            case Payday payday:
                Apply(payday);
                break;
            case SetMoney setMoney:
                // P442 — absolute per-ship money sync (join sync + the
                // per-teammate SET_MONEY in the join burst).
                Apply(setMoney);
                break;
            case MoneyChange moneyChange:
                // P442 — per-ship money delta (bucket spend, purchase, refund,
                // player donation).
                Apply(moneyChange);
                break;
            case RequestMoney requestMoney:
                Apply(requestMoney);
                break;
            case AddPart addPart: Apply(addPart); break;
            case FireExpendable fireExpendable: Apply(fireExpendable); break;
            case RelaunchShip relaunchShip: Apply(relaunchShip); break;
            case PingData pingData: Apply(pingData); break;
            case AutoDonateUpdate autoDonateUpdate:
                // P442 — a player's auto-donate relationship (payday routing) +
                // any one-time gift.
                Apply(autoDonateUpdate);
                break;
            case GainFlag gainFlag:
                // P714 — FM_S_GAIN_FLAG(156): a flag/artifact changed hands
                // (whole-mission announce, FedSrv.CPP:13157,13573); the
                // steal/found/lost announce VOs are classified in Apply.
                Apply(gainFlag);
                break;
            case KillShip killShip:
                Apply(killShip);
                break;
            case PlayerRescued playerRescued:
                // P578 — a lifepod was picked up; produce the rescue feedback
                // lines (appmsg.cpp:311-340). Previously ignored by the client.
                Apply(playerRescued);
                break;
            case ShipDelete shipDelete:
                // P370 — the server's explicit "this ship is no longer in your
                // view" notice (docked / warped to another sector / logged off /
                // died). Without it a departed ship kept rendering frozen.
                Apply(shipDelete);
                break;
            case PlayerReady playerReady:
                Apply(playerReady);
                break;
            case MissionDef missionDef:
                Apply(missionDef);
                break;
            case MissionStageMessage missionStage:
                Apply(missionStage);
                break;
            case Ping ping:
                ObserveServerTime(ping.TimeServer, (long)(NowSeconds() * 1000));
                break;
            case SetStartTime setStartTime:
                Apply(setStartTime);
                break;
            case SetBriefingText briefing:
                Apply(briefing);
                break;
            case MissionBriefingInfo briefingInfo:
                Apply(briefingInfo);
                break;
            case GameOver gameOver:
                Apply(gameOver);
                break;
            case MissileLockUpdate missileLock:
                Apply(missileLock);
                break;
            case ProjectileInstance projectile:
                Apply(projectile);
                break;
            case FireMissile fireMissile:
                Apply(fireMissile);
                break;
            case MissileUpdate missileUpdate:
                Apply(missileUpdate);
                break;
            case MissileDestroyed missileDestroyed:
                Apply(missileDestroyed);
                break;
            case ExportProbe exportProbe:
                Apply(exportProbe);
                break;
            case ProbeDestroyed probeDestroyed:
                Apply(probeDestroyed);
                break;
            case ExportMine exportMine:
                Apply(exportMine);
                break;
            case MineDestroyed mineDestroyed:
                Apply(mineDestroyed);
                break;
            case MineStrength mineStrength:
                // P574 — a live mine's current strength fraction after a hit;
                // thins the mine's decal field (native SetMineStrength).
                Apply(mineStrength);
                break;
            case ExportBuildingEffect exportBuildingEffect:
                Apply(exportBuildingEffect);
                break;
            case BuildingEffectDestroyed buildingEffectDestroyed:
                Apply(buildingEffectDestroyed);
                break;
            case CreateChaff createChaff:
                // P292 — the server's deploy broadcast for a fresh chaff decoy.
                Apply(createChaff);
                break;
            case ChangeAlliances alliances:
                ApplyAlliances(alliances);
                break;
            case TreasureSpawn treasureSpawn:
                // P293 — the server's "a treasure is here" broadcast.
                Apply(treasureSpawn);
                break;
            case DestroyTreasure destroyTreasure:
                // P293 — a treasure was picked up / expired.
                Apply(destroyTreasure);
                break;
            case AcquireTreasure acquire:
                // P710 — a treasure was acquired (picked up). Drives the
                // pickUp*Sound family (appmsg.cpp:282-303); P758 adds the
                // native TREASURE_ACK reply decision surface (the ack itself is
                // sent by ConnectionSession) and the cash-reward console line.
                Apply(acquire);
                break;
            case WarpBomb warpBomb:
                // P758 — FM_S_WARP_BOMB(188): a missile hit an aleph and it
                // will detonate at the carried deadline (appmsg.cpp:850-868).
                Apply(warpBomb);
                break;
            case OwnShipMiningUpdate mining:
                Apply(mining);
                break;
            case OwnShipKillBonusUpdate killBonus:
                Apply(killBonus);
                break;
            case OwnShipCloakUpdate cloak:
                Apply(cloak);
                break;
            case MissileTrackWarningUpdate missileTrack:
                Apply(missileTrack);
                break;
            case ScoreRosterDelta scoreDelta:
                _scoreRoster = scoreDelta.ApplyTo(_scoreRoster);
                break;
            case ScoreRosterUpdate scoreRoster:
                Apply(scoreRoster);
                break;
            case GameOverPlayers gameOverPlayers:
                // P583 — the per-player endgame roster for the game-over screen.
                Apply(gameOverPlayers);
                break;
            case LeadIndicatorUpdate leadIndicator:
                Apply(leadIndicator);
                break;
            case ExplosionEvent explosion:
                Apply(explosion);
                break;
            case ProjectileHullHit hullHit:
                // P364 — a weapon shot connected on a damagable target.
                Apply(hullHit);
                break;
            case Allegiance.Core.Wire.CollisionEvent collision:
                // P463c — two bodies collided in a sector; buffer the collision
                // for the audio pass (positional sound + own-ship jiggle feed).
                Apply(collision);
                break;
            case VictimDamaged victimDamaged:
                // P623 — the local ship took damage from a ship; buffer the
                // attribution (attacker id + damage) for TK_TargetHostile.
                Apply(victimDamaged);
                break;
            case StationDestroyed stationDestroyed:
                Apply(stationDestroyed);
                break;
            case StationCapture stationCapture:
                Apply(stationCapture);
                break;
            case ResearchProduction production:
                _researchProduction[production.ShipId] = production;
                _retiredResearchProduction.Remove(production.ShipId);
                break;
            case LastBaseNotice lastBase:
                Apply(lastBase);
                break;
            case ResearchBriefing briefing:
                ResearchBriefing = briefing;
                _completedDevelopments.UnionWith(briefing.CompletedDevelopments);
                break;
            case DevCompleted devCompleted:
                Apply(devCompleted);
                break;
            case SideRoster sideRoster:
                // P281 — post-connect side roster; drives the side-select screen.
                Apply(sideRoster);
                break;
            case SetMissionOwner missionOwner:
                // P281 — whole-mission owner broadcast; gates randomize-teams.
                Apply(missionOwner);
                break;
            case TeamReady teamReady:
                // P282 — whole-mission aggregate per-side ready broadcast.
                Apply(teamReady);
                break;
            case ForceTeamReady forceTeamReady:
                // P282 — whole-mission forward of the leader's force-ready trigger.
                Apply(forceTeamReady);
                break;
            case CreateBuckets createBuckets:
                // P284 — the joining side's development-tech mask.
                Apply(createBuckets);
                break;
            case BucketStatus bucketStatus:
                // P487 — a side bucket's funding snapshot (money + build-time ms).
                Apply(bucketStatus);
                break;
            case SideTechChange sideTechChange:
                // P284 — a development completed: the side's new mask.
                Apply(sideTechChange);
                break;
            case SideAttributeChange sideAttributeChange:
                // P446 — a development applied an attribute delta: the side's
                // post-Apply 25-multiplier set (whole-mission broadcast).
                Apply(sideAttributeChange);
                break;
            case SideInactive sideInactive:
                // P455b — a side was deactivated (or re-activated): record its
                // active state (and AET when bChangeAET).
                Apply(sideInactive);
                break;
            case SetTeamLeader setTeamLeader:
                // P366 — whole-mission leadership assignment/transfer.
                Apply(setTeamLeader);
                break;
            case JoinSide joinSide:
                // P438 — whole-mission side-join broadcast.
                Apply(joinSide);
                break;
            case ShipWarningUpdate warning:
                if (warning.ShipId == MyShipId) ShipWarnings.Apply(warning.Mask);
                break;
            case ClusterWarningUpdate clusterWarning:
                // P374 — a side's per-sector threat state changed.
                Apply(clusterWarning);
                break;
            case RipcordActivate ripcordActivate:
                Apply(ripcordActivate);
                break;
            case RipcordDenied ripcordDenied:
                Apply(ripcordDenied);
                break;
            case RipcordAborted ripcordAborted:
                Apply(ripcordAborted);
                break;
            case ShipReset shipReset:
                // P508 — the server rejected this ship's pose and sent an
                // authoritative reset + new cookie; the world stores the pose and
                // flags the own ship for the local simulation to re-seed.
                Apply(shipReset);
                break;
            case Eject eject:
                // P540 — the server broadcast a player ejecting into a drifting
                // Lifepod hull in this sector; the world swaps the hull, clears
                // the ship's parts/missiles, sets the pod pose + spin, and — for
                // our own ship — latches the autopilot-home (consume in Game1).
                Apply(eject);
                break;
            case TeleportAck teleportAck:
                // P638 — FM_S_TELEPORT_ACK(102): accepted/denied teleport reply.
                Apply(teleportAck);
                break;
            case BoardNack boardNack:
                // P638 — FM_S_BOARD_NACK(178): boarding/teleport refusal.
                Apply(boardNack);
                break;
            case MissileSpoofed missileSpoofed:
                // P638 — FM_S_MISSILE_SPOOFED(110): a missile's lock was broken
                // by chaff; retarget it onto the decoy.
                Apply(missileSpoofed);
                break;
            case EndSpoofing endSpoofing:
                // P638 — FM_S_END_SPOOFING(111): close the spoof-resolution window.
                Apply(endSpoofing);
                break;
            case EnterLifepod enterLifepod:
                // P638 — FM_S_ENTER_LIFEPOD(166): forced into a Lifepod hull.
                Apply(enterLifepod);
                break;
            case LeaveShip leaveShip:
                // P638 — FM_S_LEAVE_SHIP(94): a ship left its boarded parent.
                Apply(leaveShip);
                break;
            case AsteroidMined asteroidMined:
                // P638 — FM_S_ASTEROID_MINED(198): authoritative He3-mined ore sync.
                Apply(asteroidMined);
                break;
        }
    }

    private void Apply(SetCluster set)
    {
        // Native treats EVERY SET_CLUSTER as a delete for every remote ship,
        // even when re-entering the same sector (appmsg.cpp:1009-1023).
        SectorGeneration++;
        foreach (var ship in _ships.Values)
            if (ship.ShipId != set.Ship.ShipId && ship.ShipId != MyShipId
                && !OtherBotObserves(ship.SectorId))
                RetireSectorPose(ship);
        ClearSectorEffects();
        ShipWarnings.Reset();
        _treasures.Clear();
        _ships.OwnId = set.Ship.ShipId;
        MarkSnapshotDirty();
        // On first arrival this is the "here is your ship" assignment.
        MyShipId ??= set.Ship.ShipId;

        var e = Upsert(set.Ship.ShipId);
        e.IsMine = true;
        // Native SET_CLUSTER calls SetStation(NULL) (appmsg.cpp:1044-1049).
        // It confirms launch immediately, before the next periodic SHIP_STATUS.
        e.StationId = -1;
        // P585 — remember whether this SET_CLUSTER moved the own ship to a new
        // sector (a warp-bound sector change = the arrival half of a transit),
        // before the seat below overwrites SectorId.
        bool ownSectorChanged = e.SectorId != set.SectorId && set.Ship.ShipId == (MyShipId ?? set.Ship.ShipId);
        e.SectorId = set.SectorId;   // top-level SET_CLUSTER sectorID
        e.PoseSectorId = set.SectorId;
        e.Unknown = false;
        e.State = ShipStatusMessage.StateFlying;
        e.Latest = set.Ship;         // store verbatim, don't re-flatten
        e.LastUpdateTick = set.Ship.Time;
        // P508 — the ship cookie the server stamped on this SET_CLUSTER (a fresh
        // NewCookie() at join and on warp, fsship.cpp:767) is what every native
        // SHIP_UPDATE must echo.
        e.Cookie = set.Cookie;
        // P448b — stamp the local arrival time and (re-)seed the dead-reckoner for a
        // remote ship (the local ship's own is dropped — own prediction is P447).
        e.LatestArrivalTime = NowSeconds();
        ReseedRemoteReckoner(e);
        // P369 — the converse ordering: if the own-ship dummy EXPORT (which
        // carried MySideId) arrived BEFORE this SET_CLUSTER assigned MyShipId,
        // this entity was just created without a side. Stamp its SideId from
        // MySideId so the local hull's ShipEntity.SideId is never the C#
        // default 0 (which is a real, valid side in some missions).
        if (MySideId is { } sideId) e.SideId = sideId;
        // P370 — a fresh assignment/relocation for this ship means it is present
        // and in view again; clear any transient "gone" latch.
        e.IsGone = false;
        // P521 — SET_CLUSTER for the OWN ship is native's definitive "you are back
        // in a cluster, alive" signal (native re-homes m_ship into the cluster and
        // sets the cookie here, appmsg.cpp:1042-1087). It is sent on respawn/launch/
        // undock/warp/join — exactly the moment a death (KILL_SHIP) must be undone.
        // Without this the IsDead latch set by a prior death (KILL_SHIP /
        // SHIP_DELETE SDR_KILLED) stuck forever, so P508's own-ship sim froze at
        // "dead" and never resumed after respawn + undock. A SET_CLUSTER is
        // server-authoritative (TCP-in-order) and only ever issued for a ship the
        // server has genuinely (re)seated in a cluster, so it cannot revive a
        // corpse from a stale packet.
        e.IsDead = false;

        // P414 — a SET_CLUSTER for our own ship may accompany a ripcord teleport
        // landing (a relocated ship that happens to re-synchronise its sector);
        // clear the local ripcord HUD state so it never lingers past the move.
        if (set.Ship.ShipId == MyShipId)
            ClearRipcord();

        // SIM-0044: native trekigc.cpp:4057-4064 inhibits He3 ore updates for
        // all asteroids in the newly entered cluster for a few seconds, so the
        // client does not flash stale server ore values while the sector is
        // still loading. Apply the same 3-second wall-clock inhibit here — but
        // ONLY to asteroid live-state entries that already exist. Inventing a
        // zeroed AsteroidLiveState(0,0,…) would make the selection pane show a
        // bogus 0% hull/ore until the first ASTEROIDS_UPDATE arrives.
        if (ownSectorChanged)
        {
            double inhibitUntil = NowSeconds() + 3.0;
            foreach (var (id, def) in _asteroids)
            {
                if (def.ClusterId != set.SectorId) continue;
                if (!def.HasCapability(Asteroid.CapMineHe3)) continue;
                if (_asteroidLive.TryGetValue(id, out var existing))
                    _asteroidLive[id] = existing with { InhibitOreExpiry = inhibitUntil };
            }
        }

        // P585 — the own ship ARRIVED in a new sector via a warp: if we emerged
        // beside an aleph (within its transit-attribution radius), signal it so
        // the opt-in transit pulse flares the arrival aleph.
        if (ownSectorChanged && set.Ship.Position is { } ownPos)
        {
            var warp = NearestWarpInSector(set.SectorId, ownPos);
            if (warp is not null)
            {
                SignalAlephTransit(warp.Value.WarpId, set.Ship.ShipId, NowSeconds());
                // P716 — the own ship's warp crossing plays the native jumpSound
                // (trekigc.cpp:3998,4415; wave jumpin). This is the arrival half
                // of the transit; the departure half is the same wave, so the cue
                // fires once per warp.
                RecordSoundEvent(ClientSoundEventKind.SectorJump, set.Ship.ShipId);
            }
        }
    }

    /// <summary>
    /// P508 — <c>FM_S_SHIP_RESET(183)</c>: the server rejected this ship's
    /// client-reported pose and answers with its OWN authoritative pose + a fresh
    /// <c>NewCookie()</c> (<c>fsship.cpp:1192-1200</c>). Updates are dropped by the
    /// server until the client applies the reset and echoes the new cookie. This
    /// updates the entity's authoritative <see cref="ShipEntity.Latest"/> to the
    /// reset pose, stamps the new cookie, and — for the OWN ship — latches
    /// <see cref="ShipEntity.HasPendingReset"/> so the P508 own-ship host snaps its
    /// local simulation to the reset pose and resumes sending with the new cookie.
    /// </summary>
    private void Apply(ShipReset reset)
    {
        MarkSnapshotDirty();
        var e = Upsert(reset.Ship.ShipId);
        e.Latest = reset.Ship;         // store verbatim, don't re-flatten
        e.LastUpdateTick = reset.Ship.Time;
        e.LatestArrivalTime = NowSeconds();
        e.Cookie = reset.Cookie;       // the NEW cookie to echo
        e.IsGone = false;              // a reset is a presence-establishing update
        e.IsDead = false;              // P521 — the server actively re-engaged this ship's live pose: it is alive again
        if (reset.Ship.ShipId == MyShipId)
        {
            e.IsMine = true;
            e.HasPendingReset = true;  // single consumer: the own-ship host clears it
            ClearRipcord();
        }
    }

    /// <summary>
    /// P508 — clear the own ship's pending-SHIP_RESET latch after the host has
    /// snapped its local simulation to the reset pose and adopted the new cookie
    /// (a single consumer; <c>ShipEntity.HasPendingReset</c>'s setter is internal to
    /// this assembly, so the cross-assembly host clears it through this method).
    /// Must be called under the world gate.
    /// </summary>
    public void ClearOwnShipResetFlag(short shipId)
    {
        if (_ships.TryGetValue(shipId, out var e))
            e.HasPendingReset = false;
    }

    /// <summary>
    /// P540 — clear the own ship's pending-EJECT latch after the MonoGame host
    /// has re-seeded the local simulation to the pod pose and engaged the rescue
    /// autopilot (a single consumer; <see cref="ShipEntity.HasPendingEject"/>'s
    /// setter is internal to this assembly, so the cross-assembly host clears it
    /// through this method). Must be called under the world gate.
    /// </summary>
    public void ClearOwnEjectFlag(short shipId)
    {
        if (_ships.TryGetValue(shipId, out var e))
            e.HasPendingEject = false;
    }

    /// <summary>
    /// P540 — <c>FM_S_EJECT(77)</c>: a ship in this sector ejected into a
    /// drifting Lifepod hull (<c>appmsg.cpp:170-242</c>, the client half of the
    /// P107 server eject). Faithful to the native handler:
    /// <list type="number">
    /// <item>If the ship was boarded (<c>GetParentShip()</c>) it is unboarded
    /// (<c>SetParentShip(NULL)</c> + <c>OnBoardShip(pShip, NULL)</c>, :183-187);
    /// otherwise it "died": its parts are terminated (<c>Loadout</c> cleared)
    /// and ammo/fuel zeroed (:190-201 — this client keeps no ammo/fuel pools, so
    /// that tail is a structural no-op, noted here).</item>
    /// <item>Missiles targeting the ship are cleared (:203-213 — a structural
    /// no-op: <see cref="MissileEntity"/> tracks no target back-reference, the
    /// same omission already noted on the server, <c>Server.Combat.cs:1939</c>).</item>
    /// <item>The hull swaps to the side's lifepod (:216), a <c>pi·2</c> roll spin
    /// is set (:220), and the position/velocity/orientation come from the message
    /// (:222-227) — all reflected in <see cref="ShipEntity.Latest"/> so the
    /// renderer and (for the own ship) the local simulation see the pod
    /// immediately.</item>
    /// <item>For the OWN ship (:230-237): the new cookie is adopted and
    /// <see cref="ShipEntity.HasPendingEject"/> is latched for the MonoGame host
    /// to re-seed the sim and engage the rescue autopilot toward the nearest
    /// friendly station/probe.</item>
    /// </list>
    /// The pod is alive and present — <see cref="ShipEntity.IsDead"/> /
    /// <see cref="ShipEntity.IsGone"/> are cleared so it renders and stays in
    /// flight (the server never sends KILL_SHIP/SHIP_DELETE for an eject).
    /// </summary>
    private void Apply(Eject e)
    {
        MarkSnapshotDirty();
        var ent = Upsert(e.ShipId);

        // 1 — boarded? unboard; else "died": parts/ammo/fuel cleared.
        if (ent.ParentId != -1)
        {
            ent.ParentId = -1;             // SetParentShip(NULL) + OnBoardShip(pShip,NULL), :183-187
        }
        else
        {
            ent.Loadout = null;            // parts Terminate(); no ammo/fuel pools on the client, :190-201
        }

        // 2 — clear missiles targeting this ship (structural no-op, :203-213 —
        //    the client's MissileEntity tracks no target back-reference).

        // 3 — hull swap to the side's lifepod (:216) + roll spin (:220).
        if (LifepodHullResolver(ent.SideId) is { } lifepod)
            ent.HullId = lifepod;

        // 4 — pose + spin from the message (:222-227).
        ent.Latest = BuildEjectUpdate(e);
        ent.LastUpdateTick++;
        ent.LatestArrivalTime = NowSeconds();

        // The pod is alive and present — it stays in flight. The server
        // sends KILL_SHIP with bDeathCredit=false, then EJECT.
        ent.IsGone = false;
        ent.IsDead = false;

        // 5 — own ship: cookie + pending-eject latch (:229-237).
        if (e.ShipId == MyShipId)
        {
            ent.Cookie = e.Cookie;
            ent.IsMine = true;
            ent.HasPendingEject = true;
        }
    }

    /// <summary>P540 — materialise the EJECT message's pose into a canonical
    /// <see cref="ServerShipUpdate"/> (the same rich object every other pose
    /// source writes into <see cref="ShipEntity.Latest"/>), carrying the pod's
    /// position/velocity, an orientation built from the message forward, and the
    /// native <c>pi·2</c> roll turn rate as its <c>TurnRates[2]</c> — so the
    /// own-ship sim's <c>Seed</c> reproduces the tumble, and the renderer reads
    /// the pod pose exactly like any other authoritative update.</summary>
    private static ServerShipUpdate BuildEjectUpdate(Eject e)
    {
        Orientation o = new();
        o.Set(e.Forward);                 // Orientation(forward), appmsg.cpp:225-226
        return ServerShipUpdate.FromValues(
            e.ShipId, stateM: 0, ControlData.Neutral, time: 0u,
            e.Position, o, e.Velocity,
            new[] { 0f, 0f, 2f * MathF.PI },   // SetCurrentTurnRate(c_axisRoll, pi*2), :220
            hullFrac: 1f, shieldFrac: 1f, ammoFrac: 0f, fuelFrac: 0f,
            energyFrac: 0f, oreFrac: 0f, power: 0f);
    }

    /// <summary>P414 — <c>FM_S_RIPCORD_ACTIVATE(185)</c>: the server acknowledged
    /// a ripcord countdown toward a valid anchor in the requested sector began
    /// (or was re-targeted). Broadcast sector-wide on a first activation and
    /// requester-only on a re-target (<c>RequestRipcordCore</c>,
    /// <c>Server.Docking.cs:388</c>), always naming the ripping ship. We care
    /// only when it is OUR OWN ship — that is the only ripcord the local HUD /
    /// key handler tracks. Sets <see cref="RipcordActive"/> and records the
    /// REQUESTED target sector (<c>sidRipcord</c>, the sector the pilot picked).</summary>
    private void Apply(RipcordActivate m)
    {
        if (m.ShipidRipcord == MyShipId)
        {
            RipcordActive = true;
            RipcordTargetSectorId = m.SidRipcord;
            RipcordActiveSinceLocalMs = (long)(NowSeconds() * 1000);
            return;
        }
        // HUD-12 — a REMOTE ship's ripcord. Native's else-branch seeds that
        // ship's local countdown (appmsg.cpp:953-970:
        // SetRipcordModel(pship); ResetRipcordTimeLeft()), which the radar then
        // reads as (int)GetRipcordTimeLeft()+1 (radarimage.cpp:1015-1030).
        // First activations are sector-wide, so this reaches every observer.
        if (m.ShipidRipcord >= 0)
            _remoteRipcords[m.ShipidRipcord] = (long)(NowSeconds() * 1000);
    }

    /// <summary>P414/P711 — <c>FM_S_RIPCORD_DENIED(186)</c>: the server refused our
    /// ripcord request because the named sector has no valid anchor. Sent to the
    /// <b>requester alone</b> (never broadcast), so it is always about our own
    /// request. Surfaced as a bare system chat line (the port's convention for
    /// denied/console notices, same as the P230 missile-lock tips) so it is
    /// never silently dropped, and voices the native salNoRipcordSound
    /// (appmsg.cpp:970; SIM-0425) as a one-shot sound event.</summary>
    private void Apply(RipcordDenied m)
    {
        PostSystemLine("Ripcord denied - no ripcord destination in that sector.");
        if (MyShipId is { } me)
            RecordSoundEvent(ClientSoundEventKind.RipcordDenied, me);
    }

    /// <summary>P414 — <c>FM_S_RIPCORD_ABORTED(187)</c>: a ripcord countdown was
    /// cancelled (our own cancel request, or a mid-countdown legality failure),
    /// broadcast sector-wide naming the ripping ship. When it is our own ship,
    /// clear the local ripcord HUD state and queue the single "ripcord aborted"
    /// VO cue (the already-loaded <c>vo_sal_ripcordaborted</c> asset,
    /// sounddef.mdl:351) for Game1 to play.</summary>
    private void Apply(RipcordAborted m)
    {
        // HUD-12 — a remote ship's countdown ended without teleporting; native
        // clears that ship's ripcord model (appmsg.cpp:973-984), so the radar
        // stops drawing its marker/seconds.
        _remoteRipcords.Remove(m.ShipidRipcord);
        if (m.ShipidRipcord != MyShipId) return;
        ClearRipcord();
        RecordSoundEvent(ClientSoundEventKind.RipcordAborted, m.ShipidRipcord);
    }

    /// <summary>P414 — clear the local ripcord HUD/key state (active flag,
    /// requested target, and the activation timestamp). Called on
    /// <c>RIPCORD_ABORTED</c>, on our own <c>SET_CLUSTER</c> (a ripcord that
    /// lands on a manually-warpable boundary), on reconnect, and by the MonoGame
    /// client to self-expire the tag once the countdown's <c>RipcordSpeed</c>
    /// duration elapses (the server's teleport is silent on the wire).</summary>
    public void ClearRipcord()
    {
        RipcordActive = false;
        RipcordTargetSectorId = null;
        RipcordActiveSinceLocalMs = null;
    }

    private void Apply(SingleShipUpdate update)
    {
        MarkSnapshotDirty();
        var e = Upsert(update.OidTarget);
        if (_botWorld is not null && e.Latest is { } previous && previous.Time > update.Ship.Time) return;
        e.Latest = update.Ship;
        e.LastUpdateTick = update.Ship.Time;
        // P448b — stamp the local arrival time and (re-)seed the dead-reckoner. A
        // SingleShipUpdate may arrive for a ship whose identity (SideId/HullId) isn't
        // set yet; ReseedRemoteReckoner then leaves any prior seed in place (raw pose
        // when there is none) and a later update re-seeds once identity is known.
        e.LatestArrivalTime = NowSeconds();
        ReseedRemoteReckoner(e);
        // NOTE (P370): do NOT clear IsGone here. This message is sent for the
        // own ship AND for same-side teammates in ANY sector (P85 teammate
        // position), so it can arrive for a ship that is NOT in our view — e.g.
        // a teammate who warped to another sector still gets a dirty SingleShip-
        // Update to us. Clearing IsGone here would un-hide a departed ship. The
        // sector-correct "it is back in OUR view" signal is HeavyShipsUpdate
        // (176, only ships in the recipient's own cluster) plus SetCluster/
        // ExportShip, which is where IsGone is cleared.
        // A docked commander's explicit sector preview is the exception: its
        // fresh same-side pose plus flying status identifies the viewed object.
        if (MyShipId is { } viewer && _ships.TryGetValue(viewer, out var own)
            && own.State == ShipStatusMessage.StateDocked
            && ViewedClusterId == e.SectorId && e.SideId == own.SideId
            && e.State == ShipStatusMessage.StateFlying && !e.Unknown && !e.IsDead)
        {
            e.PoseSectorId = e.SectorId;
            e.IsGone = false;
        }
    }

    private void Apply(ShipStatusMessage status)
    {
        MarkSnapshotDirty();
        var e = Upsert(status.ShipId);
        if (_botWorld is not null && e.StateTime > status.StateTime) return;
        e.HullId = status.HullId;
        e.SectorId = status.SectorId;
        e.StationId = status.StationId;
        e.ParentId = status.ParentId;
        e.State = status.State;
        e.Unknown = status.Unknown;
        e.Detected = status.Detected;
        e.StateTime = status.StateTime;
        // SHIP_STATUS is strategic last-known information, not a pose. The
        // hidden/docked/other-sector transitions retire the old local object.
        if (!e.IsMine && (status.Unknown || status.State == ShipStatusMessage.StateDocked
            || status.State is ShipStatusMessage.StateObserver or ShipStatusMessage.StateTurret
            || (e.PoseSectorId is { } posedSector && posedSector != status.SectorId)))
            RetireSectorPose(e);
    }

    /// <summary>
    /// P347 — the periodic cross-side visibility sync (<c>FM_S_HEAVY_SHIPS_UPDATE</c>,
    /// 176). Every ship the viewer's side can see in the cluster arrives here as a
    /// delta-compressed kinematic record — including, since P346, ships of OTHER
    /// factions, whose identity (<c>HullId</c>/<c>SideId</c>/name) reached us earlier
    /// via the cross-side EXPORT broadcast (see <see cref="Apply(ExportShip)"/>). Each
    /// record is a live-position refresh exactly like <see cref="Apply(SingleShipUpdate)"/>:
    /// upsert the entity and stamp its <c>Latest</c> pose/tick, so the render and
    /// target paths see a genuinely moving cross-side hull. Identity fields are
    /// deliberately NOT touched here — they come solely from EXPORT, never from this
    /// message (which carries no <c>HullId</c>/<c>SideId</c>). The heavy record's
    /// absolute time/position are recovered from the message's per-recipient
    /// references (<c>CompactTime</c>/<c>CompactPosition</c> deltas).</summary>
    /// <summary>Opt-in benchmark/spectator stream sector. Ordinary play derives it from the own ship.</summary>
    public short? TelemetrySectorOverride { get; set; }
    private void Apply(HeavyShipsUpdate heavy)
    {
        MarkSnapshotDirty();
        // P375 — HEAVY_SHIPS_UPDATE(176) is sent by the server for exactly the
        // ships in the RECIPIENT's OWN cluster (BroadcastShipsUpdate gathers
        // candidates from the peer's own sector), so every entry here — same-side
        // OR cross-side — is by construction in the sector the local ship is in.
        // The client therefore derives each entry's ShipEntity.SectorId from the
        // local ship's own SectorId, the native-faithful rule (native's client
        // runs a local IGC sim and every object 176 instantiates lives in the
        // client's own cluster; the wire carries no per-ship sector and
        // DataShipIGC has no sector field, so there is no other source).
        //
        // This is the fix for the P371 target-pick / radar gap for CROSS-SIDE
        // ships: the only other setters are SET_CLUSTER (own ship only) and
        // SHIP_STATUS(90) (same-side teammate only, P58-era scope), so a cross-side
        // ship's SectorId was PERMANENTLY stuck at the C# default 0 and could
        // never pass `ship.SectorId == mySector` in CollectTargetCandidates /
        // DrawRadar — an enemy directly in front of you was unselectable (T) and
        // absent from the radar. Deriving here keeps the value CURRENT as a ship
        // moves between sectors: while it is in our sector 176 flows and re-stamps;
        // when it warps out P370's IsGone hides it; when it returns 176 re-stamps.
        short ownSector = 0;
        if (MyShipId is { } meOwn && _ships.TryGetValue(meOwn, out var ownEnt))
            ownSector = ownEnt.SectorId;
        ownSector = TelemetrySectorOverride ?? ownSector;

        foreach (var entry in heavy.HeavyShipEntries)
        {
            var e = Upsert(entry.ShipId);
            if (_botWorld is not null && e.Latest is { } previous
                && (previous.Time > entry.Time(heavy.TimeReference)
                    || (previous.Time == entry.Time(heavy.TimeReference) && e.PoseSectorId == ownSector && !e.IsNotVisible))) continue;
            // P585/P599 — remember whether this ship was out of view (IsGone)
            // OR had never been posed in our sector (no Latest yet) so a
            // (re/first-)sighting here can be attributed as an aleph ARRIVAL
            // below. The second clause is the P599 widening: an ENEMY crossing
            // into our sector via an aleph is a FIRST-ever sighting (the server
            // only sends cross-side ships while they fly in our sector, so the
            // entity may already exist from its cross-side EXPORT without ever
            // having had a pose here — the first 176 record is the sighting).
            bool wasGone = e.IsGone;
            bool hadPose = e.Latest is not null;
            e.SectorId = ownSector;   // P375 — every 176 entry is in our own cluster (see above)
            e.PoseSectorId = ownSector;
            // The explicitly authorized local benchmark observer receives all ships
            // in its requested sector; scanner-hidden status belongs to ordinary play.
            if (TelemetrySectorOverride is not null) e.Unknown = false;
            // Native sends in-cluster telemetry even for undetected ships.
            // Only a spotted SHIP_STATUS may clear the scanner-hidden bit.
            e.StationId = -1;
            e.ParentId = -1;
            e.State = ShipStatusMessage.StateFlying;
            e.Latest = ServerShipUpdate.FromValues(
                entry.ShipId, entry.StateM, entry.Controls,
                entry.Time(heavy.TimeReference),
                entry.Position(heavy.PositionReference),
                entry.Orientation, entry.Velocity, entry.TurnRates,
                entry.HullFraction, entry.ShieldFraction,
                entry.AmmoFraction, entry.FuelFraction,
                entry.EnergyFraction, entry.OreFraction, entry.Power);
            e.LastUpdateTick = entry.Time(heavy.TimeReference);
            // P448b — stamp the local arrival time and (re-)seed the dead-reckoner for
            // each heavy/light (176) telemetry record.
            e.LatestArrivalTime = NowSeconds();
            ReseedRemoteReckoner(e);
            // P370 — telemetry flowing for this ship proves it is in our view
            // again (docked/warped ships are excluded from the sector's update
            // list, so their records stop arriving); clear any transient "gone".
            e.IsGone = false;
            // P521 — HEAVY_SHIPS_UPDATE(176) is the remote counterpart of the
            // respawn signal: the server only carries ships that are genuinely
            // FLYING IN THE RECIPIENT'S OWN CLUSTER (P375), so a 176 entry for a
            // ship we'd marked dead (SDR_KILLED) means it has respawned and
            // re-entered our view with real presence. A docked/warped/dead ship is
            // excluded from its sector's 176 list and TCP delivers in order, so
            // this cannot un-hide a corpse from a stale or late packet (unlike
            // SingleShipUpdate, which is sent for out-of-view teammates and is
            // deliberately left alone — see Apply(SingleShipUpdate)).
            e.IsDead = false;
            // P373 — a ship returning to view can arrive via telemetry with NO
            // fresh EXPORT (the P369 cross-side identity latch is one-shot per
            // (peer,ship), re-sent only on the slow self-heal cadence), so the
            // first-sighting edge must be recognized here too. Gate on the ship
            // having established identity (EXPORT) — a not-yet-identified ship
            // has a default SideId 0 that must not be misclassified; its cue
            // fires from Apply(ExportShip) instead.
            if (e.HasRealIdentity)
                AnnounceNewContactIfNew(e);
            // P585/P599 — a ship that was out of view (IsGone) or never seen in
            // our sector before (no pose yet) is now (re/)first-sighted here;
            // if it lies just past an aleph moving outward it just ARRIVED
            // through that aleph. (P599 widened the arrival to first-ever
            // sightings — an enemy crossing INTO our sector — and added the
            // outward-velocity gate so a ship merely passing near an aleph
            // never fakes an arrival.)
            if ((wasGone || !hadPose) && ownSector != 0)
                MaybeSignalArrival(ownSector, e.ShipId, e.Latest?.Position, entry.Velocity, e.IsMine);
        }
    }

    /// <summary>
    /// P347 — the cheap half of the visibility sync (<c>FM_S_LIGHT_SHIPS_UPDATE</c>,
    /// 175). A light record carries only id + stateM + controls — no time/position/
    /// orientation — so unlike the heavy variant there is nothing to refresh in
    /// <c>Latest</c> (the heavy half is what carries pose). This port's server never
    /// even populates/sends LIGHT (P219 folded the light tier into the heavy list),
    /// so this branch exists for wire-decode parity with <c>Allegiance.Bot</c> and for
    /// any future light-tier use: it merely ensures a light-named ship exists in the
    /// entity table without disturbing any pose (a Latest-less entity is skipped by the
    /// render/target loops, which all guard on <c>Latest is not null</c>).</summary>
    private void Apply(LightShipsUpdate light)
    {
        MarkSnapshotDirty();
        foreach (var entry in light.LightShipEntries)
            Upsert(entry.ShipId);
    }

    private void Apply(ExportShip export)
    {
        // The server's own-ship EXPORT deliberately carries ShipId = NA(-1) —
        // the native client's placeholder dummy ship (see Server.cs's OwnShipExportId).
        // It has no real id, so it must not create a phantom Ships[-1] entry;
        // only exports naming a REAL ship id (ours or a teammate's) belong in the
        // entity table. (Teammate exports always carry the real id.) P260: this
        // dummy export is nonetheless the only wire source of the player's OWN
        // side (the server seats SideId on it, Server.cs:~1781), so capture it
        // as MySideId before discarding the phantom entry.
        if (export.ShipId == -1)
        {
            MySideId = export.SideId;
            // P369 — this dummy EXPORT is the only wire source of the player's
            // own side, but its early `return` used to leave the real own-ship
            // entity (keyed by the real MyShipId) with ShipEntity.SideId stuck
            // at the C# default 0 — which is a *valid* side in some missions, so
            // the local hull silently resolved as the wrong civ. Stamp it from
            // MySideId when we already know which ship is ours (SET_CLUSTER may
            // have preceded this export), so ShipEntity.SideId stays the single
            // source of truth for the local hull too (model/texture/trail all
            // read it).
            if (MyShipId is { } myId && _ships.TryGetValue(myId, out var own))
                own.SideId = export.SideId;
            ConfirmBotSide();
            return;
        }

        // Any OT_ship export — a teammate's ship entering the table for the
        // first time, or (later in the join) our own already-present one being
        // refreshed. IsMine is computed from whatever MyShipId currently holds,
        // so a teammate arriving before our SET_CLUSTER stays IsMine=false
        // (we simply don't know yet — noted in the README's "not yet covered".
        var e = Upsert(export.ShipId);
        e.HullId = export.HullId;
        e.SideId = export.SideId;
        e.CharacterName = export.CharacterName;
        e.HasRealIdentity = true;   // P373 — identity (real SideId/HullId) now established by EXPORT
        if (MyShipId.HasValue && export.ShipId == MyShipId.Value) e.IsMine = true;

        // Server.cs's teammate export loop now sends a real PartData[] tail
        // (see ExportShip's own doc comment) — populated at the teammate's
        // initial export and re-sent whenever their mounted parts change
        // afterward, since BuyLoadoutAck (below) never reaches anyone but the
        // requester. Unconditional, like BuyLoadoutAck's own Loadout set: an
        // empty tail here means genuinely hull-only, same meaning as there.
        // Never fires for our own ship — the own-ship EXPORT always carries
        // the NA(-1) dummy id, filtered out above, and never a real one.
        // FractionMounted has no wire representation here (PartData is the
        // smaller, requester-agnostic observer struct, see its own doc
        // comment); an already-mounted part is definitionally fully mounted.
        var exportedInventory = export.Parts
            .Select(p => new ExpandedPartData(p.PartId, p.Amount, p.MountId, ExpandedPartData.FullyMounted)).ToList();
        StoreInventory(e, exportedInventory);
        // P370 — an identity EXPORT re-establishes this ship in our view (e.g. a
        // cross-side ship we'd marked gone warped back into our sector, or we
        // re-gained visibility after the server re-sent it); clear the transient
        // "gone" latch so it renders/resumes live updates again.
        e.IsGone = false;
        // P521 — an identity EXPORT for a ship we'd marked dead means it has
        // respawned and the server has re-established its identity/visibility to
        // us (the server never re-exports a corpse; a post-death EXPORT in our
        // in-order TCP stream is genuinely the respawn re-export). Clear the death
        // latch so the ship renders and resumes live updates again — the remote
        // counterpart of SET_CLUSTER's own-ship respawn clear.
        e.IsDead = false;
        // P373 — the EXPORT that first grants a ship its identity (a brand-new
        // teammate/enemy, or a P370-gone ship being re-exported on return) is a
        // first-sighting edge: queue the enemy/friendly sound cue exactly once.
        // Routine refreshes of a continuously-visible ship are suppressed by the
        // NewContactSounded latch inside AnnounceNewContactIfNew.
        AnnounceNewContactIfNew(e);
    }

    private void Apply(ExportCluster export) =>
        _clusters[export.Cluster.ClusterId] = export.Cluster;

    private void Apply(ExportStation export)
    {
        _stations[export.Station.StationId] = export.Station;
        MarkSnapshotDirty();
    }

    private void Apply(ExportWarp export)
    {
        _warps[export.Warp.WarpId] = export.Warp;
        MarkSnapshotDirty();
    }

    private void Apply(ExportAsteroid export) =>
        _asteroids[export.Asteroid.AsteroidId] = export.Asteroid;

    /// <summary>
    /// P423 — the periodic per-side asteroid condition sync
    /// (<c>FM_S_ASTEROIDS_UPDATE</c>, 179). This is the first time live
    /// per-asteroid state reaches the client's world: the server bakes each
    /// visible rock's <em>current</em> ore pool and health fraction into the
    /// message each cadence tick (Server.Broadcast.cs
    /// <c>BroadcastAsteroidsUpdate</c>, from live <c>Asteroid.OreRemaining</c> /
    /// <c>Asteroid.Fraction</c>), so decoding it gives the client the two real
    /// native readouts — remaining He3 content (<c>GetOreFraction</c>) and
    /// structural hit points (<c>GetFraction</c>). The wire carries the raw ore
    /// amount (short); the ore <em>fraction</em> is derived against the static
    /// <see cref="DataAsteroidIGC.OreMax"/> so it matches the sim's own
    /// <c>Asteroid.OreFraction</c> exactly (quantization aside). The health
    /// fraction is the div-240 BytePercentage. Entities whose definition is not
    /// yet known (no EXPORT) still get their health fraction; the ore fraction
    /// is only derivable once OreMax is known.
    /// </summary>
    private void Apply(AsteroidsUpdate u)
    {
        double now = NowSeconds();
        foreach (var e in u.Entries)
        {
            float oreFrac = 0f;
            if (_asteroids.TryGetValue(e.AsteroidId, out var def) && def.OreMax > 0f)
                oreFrac = Math.Clamp(e.Ore / def.OreMax, 0f, 1f);
            // SIM-0044: native inhibits He3 ore updates for ~3 wall-clock seconds
            // after the own ship enters a cluster (trekigc.cpp:4057-4064). During
            // the inhibit window keep the previously known ore fraction (or 0 if
            // none) but still allow the structural health fraction to update.
            _asteroidLive.TryGetValue(e.AsteroidId, out var prev);
            float keptOreFrac = oreFrac;
            if (prev.InhibitOreExpiry > now)
                keptOreFrac = prev.OreFraction;
            _asteroidLive[e.AsteroidId] = new AsteroidLiveState(
                OreFraction: keptOreFrac,
                Fraction: e.BpFraction / 240.0f,
                InhibitOreExpiry: prev.InhibitOreExpiry);
        }
    }

    /// <summary>
    /// P423 — the periodic per-side station condition sync
    /// (<c>FM_S_STATIONS_UPDATE</c>, 57). Each visible station's live armor +
    /// shield fractions (div-240 BytePercentages) reach the client so a station
    /// participates in the generic target health bar alongside ships and
    /// asteroids (a station is a real damageable object with an armor pool).
    /// </summary>
    private void Apply(StationsUpdate u)
    {
        foreach (var e in u.Entries)
            _stationLive[e.StationId] = new StationLiveState(
                HullFraction: e.BpHullFraction / 240.0f,
                ShieldFraction: e.BpShieldFraction / 240.0f);
    }

    /// <summary>
    /// P424 — the owner-only "you have a pending queued order" sync
    /// (<c>QUEUED_COMMAND_UPDATE</c>, 215). Sets the local player's own pending
    /// queued order (<see cref="MyQueuedCommand"/>) from the wire. A message
    /// whose <see cref="QueuedCommandUpdate.CommandId"/> is
    /// <see cref="OrderChange.CidNone"/> clears it (fresh / accepted / rejected /
    /// auto-rejected). The MonoGame client's ACCEPT / REJECT / CLEAR keys read
    /// this state to build their real ORDER_CHANGE sends (ACCEPT echoes the
    /// pending command id; REJECT/CLEAR don't need it but still only act sensibly
    /// in flight).
    /// </summary>
    private void Apply(QueuedCommandUpdate u)
    {
        if (u.HasPending)
        {
            _moneyRequest = null;
            _lastCommandOrMoneySender = u.SenderId;
            _myQueuedCommand = new QueuedCommandState(u.CommandId, u.TargetType, u.TargetId);
        }
        else
        {
            _myQueuedCommand = null;
            // Native clears the sender when the queued command times out
            // (WinTrek.cpp:7687), so a later Reject can't reply to a stale sender.
            if (_moneyRequest is null) _lastCommandOrMoneySender = Na;
        }
    }

    /// <summary>Successful reload relay: retain the weapon-status counter and
    /// apply cargo transfers (native clintlib/appmsg.cpp:1468-1566). Own echoes
    /// are applied once, matching the port's SWAP_PART receive convention.</summary>
    private void Apply(Reload reload)
    {
        if (!_ships.TryGetValue(reload.ShipID, out var ent)) return;
        ent.ReloadCount++;
        // Classify the source cargo before applying the authoritative transfer.
        RecordReloadFeedback(reload);
        ApplyReloadInventory(ent, reload);
    }

    /// <summary>
    /// P737 — SIM-0263..0266, SIM-0443/0444/0449/0450/0451: the own-ship reload
    /// one-shots. Native plays a type-specific <c>salReloading*Sound</c> plus the
    /// generic <c>startReloadSound</c> when a fire empties a pool and a spare
    /// pack auto-reloads from cargo (<c>FireMissile</c> clintlib.cpp:3216-3225;
    /// <c>FireExpendable</c> clintlib.cpp:3394-3417; <c>SetAmmo</c>/<c>SetFuel</c>
    /// shipIGC.h:605-650). This pure-renderer client observes a SUCCESSFUL reload
    /// as the own ship's FM_S_RELOAD(129) relay (the server broadcasts the
    /// applied cargo→pool transfers sector-wide on success; failures are never
    /// relayed). Each relay entry names the consumed cargo slot (negative mount);
    /// the slot's part type is classified exactly like the server's
    /// <c>Ship.TryApplyReloadEntry</c> to pick the type-specific cue. Gated to the
    /// local ship, native's <c>pShip == m_ship</c> (clintlib.cpp:3217,3397).
    /// </summary>
    private void RecordReloadFeedback(Reload reload)
    {
        if (MyShipId is not { } me || reload.ShipID != me) return;
        if (!_ships.TryGetValue(me, out var own) || own.Cargo is not { } cargo) return;

        foreach (var e in reload.Entries)
        {
            // e.Mount is a negative cargo-bay mount (-1..-5); a cargo entry
            // carries the same signed mount as a high-bit byte (ExpandedPartData).
            ExpandedPartData? found = null;
            foreach (var c in cargo)
                if ((sbyte)c.MountId == e.Mount) { found = c; break; }
            if (found is not { } cp) continue;

            if (PartTypeResolver(cp.PartId) is { } type)
            {
                switch (type.Type)
                {
                    case DataPartTypeIGC.Pack p when p.PackType == 0:   // c_packAmmo (igc.h:549)
                        RecordSoundEvent(ClientSoundEventKind.SalReloadingAmmo, me);
                        break;
                    case DataPartTypeIGC.Pack p when p.PackType == 1:   // c_packFuel (igc.h:550)
                        RecordSoundEvent(ClientSoundEventKind.SalReloadingFuel, me);
                        break;
                    case DataPartTypeIGC.MissileLauncher:
                        RecordSoundEvent(ClientSoundEventKind.SalReloadingMissiles, me);
                        break;
                    case DataPartTypeIGC.ChaffLauncher:
                        RecordSoundEvent(ClientSoundEventKind.SalReloadingChaff, me);
                        break;
                    case DataPartTypeIGC.MineLauncher:
                    case DataPartTypeIGC.ProbeLauncher:
                        RecordSoundEvent(ClientSoundEventKind.SalReloadingDispenser, me);
                        break;
                }
            }

            // The generic "reloading..." cue plays on EVERY successful reload of
            // any pool/launcher (native startReloadSound, clintlib.cpp:3219,3404;
            // shipIGC.h:617,647).
            RecordSoundEvent(ClientSoundEventKind.StartReload, me);
        }
    }

    /// <summary>P639 — <c>FM_S_RANK_INFO(13)</c>: the server's rank-name table
    /// sync (sent once right after LOGONACK, Server.Lobby.cs:3344; native
    /// appmsg.cpp:3372-3382). Replaces the whole table each receive (the same
    /// replace-wholesale semantics native's handler uses with a fresh
    /// <c>new RankInfo[cRanks]</c> + memcpy). Stored so the character sheet,
    /// scoreboard and team pane can resolve a <c>ScoreRosterEntry.Rank</c> id
    /// to its display name via <see cref="RankNameLookup"/>.</summary>
    private void Apply(Allegiance.Core.Wire.RankInfo rankInfo)
    {
        _rankTable = rankInfo.Ranks;
    }

    /// <summary>P260 — <c>FM_S_EXPORT(26)/OT_probe</c>: a freshly-deployed probe
    /// (P224) enters the table keyed by its real <c>ProbeId</c>. A probe is
    /// static, so this is the only write it ever gets (no per-tick stream);
    /// its position is final for its lifetime (<c>probeigc.cpp:115,142</c>). The
    /// SideId (owner) and ShipId (deployer) ride along for the sound attribution.</summary>
    private void Apply(ExportProbe export)
    {
        var dp = export.Probe;
        _probes[dp.ProbeId] = new ProbeEntity
        {
            ProbeId = dp.ProbeId,
            SideId = dp.SideId,
            LauncherShipId = dp.ShipId,
            ClusterId = dp.ClusterId,
            ProbetypeId = dp.ProbetypeId,
            Position = dp.P0,
        };
    }

    /// <summary>P260 — <c>FM_S_PROBE_DESTROYED(85)</c>: the probe was destroyed
    /// (combat) or its lifespan expired (the server's ProbeDestroyedEvent), so it
    /// leaves the world table — which is what ends the probe ambient loop (the
    /// MonoGame probe-sound planner's disappearance pass observes its absence).</summary>
    private void Apply(ProbeDestroyed destroyed) =>
        _probes.Remove(destroyed.ProbeId);

    /// <summary>P291 — <c>FM_S_EXPORT(26)/OT_mine</c>: a freshly-deployed mine
    /// (P94) enters the table keyed by its real <c>MineId</c>. A mine is static,
    /// so this is the only write it ever gets (no per-tick stream); its position
    /// is final for its lifetime (<c>Server.BroadcastExportMine</c>,
    /// <c>Server.cs:10253</c>, and native has no position integration). The
    /// SideId (owner), LauncherShipId (layer) and MinetypeId (radius source)
    /// ride along — <see cref="MineEntity"/> for why the type id matters.</summary>
    private void Apply(ExportMine export)
    {
        var dm = export.Mine;
        _mines[dm.MineId] = new MineEntity
        {
            MineId = dm.MineId,
            SideId = dm.SideId,
            LauncherShipId = dm.LauncherId,
            MinetypeId = dm.MinetypeId,
            ClusterId = dm.ClusterId,
            Position = dm.P0,
        };
        // P717 — SIM-0447: the LOCAL ship's own deploy plays the mine-deploy
        // one-shot (native deployMineSound, clintlib.cpp:3253, gated
        // `if (pShip == m_ship)`). The server's FM_S_EXPORT(26)/OT_mine carries
        // the launcher ship id, so the own-deploy edge is `LauncherId == MyShipId`.
        if (MyShipId is { } me && dm.LauncherId == me)
            RecordSoundEvent(ClientSoundEventKind.MineDeployed, dm.MineId);
    }

    /// <summary>P291 — <c>FM_S_MINE_DESTROYED(84)</c>: the mine detonated or its
    /// lifespan expired (the server's MineDestroyedEvent), so it leaves the world
    /// table — which is what removes its decal field from the scene (the
    /// renderer's per-frame snapshot simply no longer contains it).</summary>
    private void Apply(MineDestroyed destroyed) =>
        _mines.Remove(destroyed.MineId);

    /// <summary>P574 — apply a mine-strength update (native
    /// <c>SetMineStrength(m_fraction)</c>, mineigc.h:142,180): thin the mine's
    /// decal field to the new fraction. Clamped to [0,1]; a stale/unknown mine
    /// id is ignored.</summary>
    private void Apply(MineStrength s)
    {
        if (!_mines.TryGetValue(s.MineId, out var me)) return;
        me.Strength = Math.Clamp(s.Strength, 0f, 1f);
    }

    /// <summary>P294 — <c>FM_S_EXPORT(26)/OT_buildingEffect</c>: a newly-started
    /// station construction (a Builder committed to building at an asteroid)
    /// enters the table keyed by its target <c>AsteroidId</c>. A building effect
    /// is static over the asteroid (no per-tick stream), so this is its only
    /// placement write; the receipt instant anchors
    /// <see cref="BuildingEffectEntity.ProgressFraction"/> and the eventual
    /// self-expiry (<c>buildingeffectigc.h:160-165</c> terminates the cosmetic
    /// effect at <c>m_timeComplete</c>, so native sends nothing on completion).</summary>
    private void Apply(ExportBuildingEffect export)
    {
        var d = export.Effect;
        _buildingEffects[d.AsteroidId] = new BuildingEffectEntity
        {
            AsteroidId = d.AsteroidId,
            ClusterId = d.ClusterId,
            RadiusAsteroid = d.RadiusAsteroid,
            RadiusStation = d.RadiusStation,
            PositionStart = d.PositionStart,
            PositionStop = d.PositionStop,
            ReceivedLocalMs = (long)(NowSeconds() * 1000),
        };
    }

    /// <summary>P294 — <c>FM_S_BUILDINGEFFECT_DESTROYED(181)</c>: the build was
    /// interrupted (its Builder died mid-build, the server's
    /// <c>KillBuildingEffectEvent</c>), so the construction leaves the world
    /// table — which is what removes its visual from the scene.</summary>
    private void Apply(BuildingEffectDestroyed destroyed) =>
        _buildingEffects.Remove(destroyed.AsteroidId);

    /// <summary>Remove building effects whose build has completed by the given
    /// local monotonic instant (elapsed ≥ <see cref="ConstructionPhase.Duration"/>
    /// since receipt). <see cref="ClientWorld"/> is pure (no clock), so the
    /// caller supplies the time and typically calls this once per frame under
    /// the same gate lock guarding the other world reads, before gathering the
    /// live set to render — the exact shape of
    /// <see cref="PruneExpiredProjectiles"/>.</summary>
    public void PruneExpiredBuildingEffects(long nowLocalMs)
    {
        foreach (var kv in _buildingEffects.ToArray())
            if (kv.Value.ProgressFraction(nowLocalMs) >= 1f)
                _buildingEffects.Remove(kv.Key);
    }

    /// <summary>Retain TREASURE's original trajectory and predict its motion,
    /// just as native CtreasureIGC does after export/import.</summary>
    private void Apply(TreasureSpawn spawn)
    {
        var d = spawn.Data;
        if (!_treasureClockSeeded)
        {
            _treasureClockSeeded = true;
            _treasureServerSeconds = d.Time0 / 1000.0;
            _treasureLocalSeconds = NowSeconds();
        }
        _treasures[d.ObjectId] = new TreasureEntity
        {
            TreasureId = d.ObjectId,
            ClusterId = d.ClusterId,
            PayloadId = d.TreasureId,
            Amount = d.Amount,
            Code = d.TreasureCode,
            InitialVelocity = Allegiance.Core.Sim.TreasureMotion.Vector(d.V0),
            CreatedAt = d.CreateNow ? TreasureServerSeconds() : d.Time0 / 1000.0,
            ServerClock = TreasureServerSeconds,
            Position = d.P0 is { Length: 3 } p
                ? new Vector3(p[0], p[1], p[2])
                : Vector3.Zero,
        };
    }

    /// <summary>P293 — <c>FM_S_DESTROY_TREASURE(30)</c>: the treasure was picked
    /// up or its lifespan expired (the server's pickup/expiry path,
    /// <c>Server.cs:8824</c>), so it leaves the world table — which is what
    /// removes its gold gem from the scene.</summary>
    private void Apply(DestroyTreasure destroyed) =>
        _treasures.Remove(destroyed.TreasureId);

    /// <summary>P292 — <c>FM_S_CREATE_CHAFF(109)</c>: the server's sector-wide
    /// deploy broadcast for a fresh chaff decoy. Appends a transient
    /// <see cref="ChaffEntity"/> capturing the real spawn position/velocity and
    /// the chaff type id plus the local receipt instant (see
    /// <see cref="ChaffEntity"/> for why receipt time anchors coasting +
    /// expiry). One entry per real broadcast; there is no id to upsert on and no
    /// destruction message (native chaff expiry just removes the model from its
    /// cluster, <c>chaffigc.h:68-73</c>), so removal is purely time-based via
    /// <see cref="PruneExpiredChaff"/>.</summary>
    private void Apply(CreateChaff m)
    {
        _chaff.Add(new ChaffEntity
        {
            Etid = m.Etid,
            P0 = m.P0,
            V0 = m.V0,
            ReceivedLocalMs = (long)(NowSeconds() * 1000),
        });
        // P638 — remember the newest decoy's spawn position so a following
        // MISSILE_SPOOFED(110) can retarget a missile onto it (native's
        // m_pchaffLastCreated, appmsg.cpp:1686).
        _lastChaffPosition = m.P0;
    }

    /// <summary>Remove chaff entries whose lifetime has elapsed by the given
    /// local monotonic instant. A chaff's lifespan is <em>not</em> on the wire,
    /// so it is resolved from the static core's chaff-type table
    /// (<c>DataChaffTypeIGC.Lifespan</c>) via the supplied dictionary, falling
    /// back to <see cref="ChaffEntity.DefaultLifespan"/> when the type is unknown
    /// or the table absent (the degrade-not-crash rule). Pure (no clock) — the
    /// caller supplies the time and typically calls this once per frame under
    /// the same gate lock guarding the other world reads, before gathering the
    /// live set to render.</summary>
    public void PruneExpiredChaff(long nowLocalMs, IReadOnlyDictionary<short, DataChaffTypeIGC>? chaffTypes)
    {
        _chaff.RemoveAll(c =>
            c.ReceivedLocalMs + (long)(ResolveLifespan(c.Etid, chaffTypes) * 1000.0) <= nowLocalMs);
    }

    /// <summary>Resolve a chaff type's real lifespan (seconds) from the static
    /// core's chaff table, falling back to <see cref="ChaffEntity.DefaultLifespan"/>
    /// when the type is unknown or the table absent.</summary>
    private static float ResolveLifespan(short etid, IReadOnlyDictionary<short, DataChaffTypeIGC>? chaffTypes)
    {
        if (chaffTypes is not null && chaffTypes.TryGetValue(etid, out var ct) && ct.Lifespan > 0f)
            return ct.Lifespan;
        return ChaffEntity.DefaultLifespan;
    }

    private void Apply(ExplosionEvent explosion)
    {
        // Ignore a malformed/unknown category so a bad wire type degrades to
        // "no explosion" rather than throwing in the receive path (the server
        // only sends the seven native categories, but be defensive).
        if (!ExplosionPresets.IsKnown(explosion.Type)) return;
        _explosions.Add(new ExplosionEntity
        {
            Type = explosion.Type,
            Position = explosion.Position,
            ClusterId = explosion.ClusterId,
            Velocity = explosion.Velocity,
            ReceivedLocalMs = (long)(NowSeconds() * 1000),
        });
        // P574 — the same event feeds the world-explosion SOUND stream (native
        // plays a tiered positional sound for every AddExplosion in the player's
        // cluster, trekigc.cpp:86-101). Kept in its own buffer so the audio
        // manager can drain it in Update without racing the renderer's Draw-time
        // drain of _explosions.
        _explosionSoundEvents.Add(new ExplosionSoundEntity(explosion.Type, explosion.ClusterId, explosion.Position, explosion.Scale));
        if (_explosionSoundEvents.Count > MaxExplosionSoundEvents) _explosionSoundEvents.RemoveAt(0);
    }

    private void Apply(ProjectileHullHit hit)
    {
        _hullHits.Add(new HullHitEntity
        {
            TargetId = hit.TargetId,
            ClusterId = hit.ClusterId,
            Radius = hit.Radius,
            Position = hit.Position,
            Normal = hit.Normal,
            AbsorbedByShield = hit.AbsorbedByShield,   // P463 — shield-hit discriminator
            SourceId = hit.SourceId,                   // P539b — the shooter, so a receiver can attribute the hit
            ReceivedLocalMs = (long)(NowSeconds() * 1000),
        });
        // P463 — the same hit also feeds the hit-SOUND stream (my/other ×
        // shield/hull). Kept in its own buffer so the audio manager can drain it
        // in Update without racing the renderer's Draw-time drain of _hullHits.
        _hitSoundEvents.Add(new HitSoundEntity(hit.TargetId, hit.ClusterId, hit.AbsorbedByShield, hit.SourceId, hit.Position));
        if (_hitSoundEvents.Count > MaxHitSoundEvents) _hitSoundEvents.RemoveAt(0);
    }

    /// <summary>P623 — buffer a VICTIM_DAMAGED(217) attribution (who hurt the
    /// local ship and by how much) for Game1's TK_TargetHostile tracker. Targeted
    /// unicast to the victim's own peer only (see VictimDamaged).</summary>
    private void Apply(VictimDamaged v)
    {
        if (v.Damage <= 0f) return;
        _victimDamages.Add((v.AttackerId, v.Damage, (long)(NowSeconds() * 1000)));
        System.Threading.Interlocked.Increment(ref _victimDamageVersion);   // HUD-10 — real per-hit edge for the damage flash
        if (_victimDamages.Count > 128) _victimDamages.RemoveAt(0);
    }

    /// <summary>P463c — buffer a resolved collision for the audio pass: the
    /// positional collision sound (both parties share the single impact point)
    /// and, when the local ship is a party, its jiggle feed into P449b.</summary>
    private void Apply(Allegiance.Core.Wire.CollisionEvent collision)
    {
        _collisionEvents.Add(new CollisionEntity(
            collision.Party1Id, collision.Party2Id, collision.ClusterId,
            collision.Jiggle1, collision.Jiggle2, collision.Position));
        if (_collisionEvents.Count > MaxCollisionEvents) _collisionEvents.RemoveAt(0);
    }

    /// <summary>P267 — <c>FM_S_STATION_DESTROYED(59)</c>: an own/friendly station
    /// plays the <c>DestroyedSound</c> one-shot, an enemy station the
    /// <c>EnemyDestroyedSound</c> (native <c>appmsg.cpp:548-551</c>), decided by
    /// the destroyed station's side and alliances (appmsg.cpp:548-551).
    /// The station type is retained in the queued event before it leaves the world
    /// table, which also ends its exterior ambient loop via the station-sound
    /// planner's disappearance pass.</summary>
    private void Apply(StationDestroyed destroyed)
    {
        RemoveLastBase(destroyed.StationId);
        MarkSnapshotDirty();
        if (!_stations.TryGetValue(destroyed.StationId, out var st)) return;
        bool friendly = IsFriendlyStationSide(st.SideId);
        RecordSoundEvent(friendly ? ClientSoundEventKind.StationDestroyed : ClientSoundEventKind.StationEnemyDestroyed,
            destroyed.StationId, st.StationTypeId);
        _stations.Remove(destroyed.StationId);
    }

    /// <summary>P267 — <c>FM_S_STATION_CAPTURE(58)</c>: if the station was OURS
    /// (sidOld == our side) play <c>CapturedSound</c>; if it became OURS
    /// (sidNew == our side) play <c>EnemyCapturedSound</c> (native
    /// <c>appmsg.cpp:674-677</c>). The station re-sides to its new owner in the
    /// world table, mirroring native's <c>station-&gt;SetSide(psideNew)</c>.</summary>
    private void Apply(StationCapture capture)
    {
        RemoveLastBase(capture.StationId);
        MarkSnapshotDirty();
        if (!_stations.TryGetValue(capture.StationId, out var st)) return;
        // Native appmsg.cpp:674-679: old friendly side takes precedence;
        // unrelated captures are silent, but still change ownership.
        if (IsFriendlyStationSide(capture.SidOld))
            RecordSoundEvent(ClientSoundEventKind.StationCaptured, capture.StationId, st.StationTypeId);
        else if (IsFriendlyStationSide(capture.SidNew))
            RecordSoundEvent(ClientSoundEventKind.StationEnemyCaptured, capture.StationId, st.StationTypeId);

        _stations[capture.StationId] = st with { SideId = capture.SidNew };
    }

    private bool IsFriendlyStationSide(short side) =>
        MySideId is { } mine && (side == mine || AreAllied(mine, side));

    /// <summary>P267 — <c>FM_S_DEV_COMPLETED(8)</c>: a development finished
    /// researching → its completion chime (native <c>appmsg.cpp:3326</c>).</summary>
    private void Apply(DevCompleted completed)
    {
        _completedDevelopments.Add(completed.DevId);
        RecordSoundEvent(ClientSoundEventKind.DevCompleted, completed.DevId);
    }

    private void RecordSoundEvent(ClientSoundEventKind kind, short id, short stationTypeId = -1,
        Allegiance.Core.Audio.NativeVoiceCue? voice = null)
    {
        _soundEvents.Add(new ClientSoundEvent(kind, id, stationTypeId, voice));
        if (_soundEvents.Count > MaxSoundEvents) _soundEvents.RemoveAt(0);
    }

    /// <summary>Post a bare system/console line into the transient event log
    /// (sender NA so <see cref="EventLog.Chats"/> renders it with no player
    /// prefix) — the same convention the P230 missile-lock tips and the P414
    /// ripcord-denied / no-target hints use. Public so the MonoGame client can
    /// surface client-side notices (e.g. the key pressed with no sector
    /// selected) that originate outside the wire. Must be called under the
    /// session's <see cref="Session.ConnectionSession.Gate"/> lock, like the
    /// other world mutations.</summary>
    public void PostSystemLine(string text)
    {
        _events.Record(new ChatMessage
        {
            Message = text,
            SidSender = -1,
            ChatTarget = (int)Allegiance.Core.Sim.ChatTarget.Everyone,
        });
    }

    /// <summary>FM_CS_PROJECTILE_INSTANCE(12) — append a transient projectile
    /// entity capturing the real spawn position/velocity/heading/lifespan plus
    /// the local receipt instant (see <see cref="ProjectileEntity"/> for why
    /// receipt time anchors expiry and motion extrapolation here). One entry per
    /// real broadcast; no id to upsert on — native projectiles are fire-and-forget.
    /// <para>P462 — the <see cref="OwnEchoDropPredicate"/> (installed by the
    /// MonoGame client's own-shot echo) de-duplicates our OWN server echo: a
    /// round the local tracer already emitted at the trigger frame — same type,
    /// spawned within <c>FireOriginRadius</c> of our server position, arrived
    /// within the echo window — is dropped so it neither re-renders nor re-sounds.
    /// A round that doesn't match falls through and is created normally (the
    /// local/server disagreement fallback).</para></summary>
    private void Apply(ProjectileInstance m)
    {
        var rel = ClassifyLauncher(m.LauncherId, m.LauncherType);
        Diag.RecordReceived(rel);
        // P648 — count incoming rounds from a shooter this viewer cannot place,
        // split by category (latched dead / never posed / stale pose). A
        // latched-dead shooter is the "invisible ship shooting" case; a stale
        // one is the "frozen but still firing" case. This is the per-viewer
        // measurement the task drives on (fixes prove out when these drop).
        switch (m.LauncherType == IgcObjectType.Probe
            ? (_probes.ContainsKey(m.LauncherId) ? ShipPlaceability.Live : ShipPlaceability.NeverPosed)
            : m.LauncherType == IgcObjectType.Ship ? ClassifyShipPlaceability(m.LauncherId) : ShipPlaceability.NeverPosed)
        {
            case ShipPlaceability.LatchedDead: Diag.FromLatchedDead++; break;
            case ShipPlaceability.NeverPosed: Diag.FromNeverPosed++; break;
            case ShipPlaceability.Stale: Diag.FromStale++; break;
        }

        // P542 — the own-echo de-dup is PER OUR OWN SHOT. An incoming round is
        // only ever dropped as our own echo when it was LAUNCHED BY US
        // (LauncherId == MyShipId). A foreign round — even a same-type one fired
        // by a teammate/enemy next to us in a dogfight — is never our echo and
        // must render. (Pre-P542 the predicate was consulted regardless of who
        // fired, so a bot flying the same hull + default gun as the player had
        // its near-field shots dropped as "our echo" and vanished.) Our server
        // position is read from the current authoritative update (the de-dup
        // radius is anchored to server space).
        bool own = rel == ProjectileRelation.Own;
        if (own
            && OwnEchoDropPredicate is { } pred
            && MyShipId is { } me
            && _ships.TryGetValue(me, out var ownEnt)
            && ownEnt.Latest is { } up
            && pred(m.ProjectileTypeId, m.Position, up.Position, (long)(NowSeconds() * 1000)))
        {
            Diag.RecordDropped(rel);
            return;
        }

        if (CountOnlyProjectiles)
        {
            if (own) _ownProjectileCount++;
            return;
        }
        _projectiles.Add(new ProjectileEntity
        {
            LauncherId = m.LauncherId,
            LauncherType = m.LauncherType,
            ProjectileTypeId = m.ProjectileTypeId,
            Position = m.Position,
            Forward = m.Forward,
            Velocity = m.Velocity,
            Lifespan = m.Lifespan,
            ReceivedLocalMs = (long)(NowSeconds() * 1000),
        });
    }

    /// <summary>Remove projectile entries whose lifetime has elapsed by the given
    /// local monotonic instant. <see cref="ClientWorld"/> is pure (no clock), so
    /// the caller supplies the time and typically calls this once per frame under
    /// the same gate lock guarding the other world reads, before gathering the
    /// live set to render.</summary>
    public void PruneExpiredProjectiles(long nowLocalMs) =>
        _projectiles.RemoveAll(p => p.ReceivedLocalMs + (long)(p.Lifespan * 1000.0) <= nowLocalMs);

    /// <summary>
    /// P542 — walk the live <see cref="_projectiles"/> with the EXACT draw
    /// decision the MonoGame renderer uses (distance-visibility against
    /// <paramref name="anchorPos"/>, then the near-camera cull), counting each
    /// outcome into <see cref="Diag"/> and returning how many would actually be
    /// DRAWN. This is the shared "how many received rounds survive to the
    /// screen" measurement: the MonoGame client calls it once per frame (feeding
    /// its <c>[proj-diag]</c> line) and the headless live tests drive the same
    /// method, so "enemy received ≈ drawn" is measured with one rule. Passing
    /// <paramref name="projectileTypes"/> lets it resolve the real per-type bolt
    /// width (else the amber default), matching the renderer's
    /// <c>ProjectileStyle.For</c> sizing.
    /// </summary>
    public int DiagnoseDraw(
        Vector3 anchorPos, Vector3 cameraEye, long nowLocalMs,
        IReadOnlyDictionary<short, DataProjectileTypeIGC>? projectileTypes)
    {
        PruneExpiredProjectiles(nowLocalMs);
        int drawn = 0;
        foreach (var pj in _projectiles)
        {
            var pos = pj.PositionAt(nowLocalMs);
            var toMe = pos - anchorPos;
            if (toMe.LengthSquared() > ProjectileVisibility.VisibilityRadiusSqr)
            {
                Diag.RadiusCulled++;
                continue;
            }

            Vector3 dir;
            if (pj.Velocity.LengthSquared() > 1e-6f) dir = pj.Velocity.Normalize();
            else if (pj.Forward.LengthSquared() > 1e-6f) dir = pj.Forward.Normalize();
            else dir = new Vector3(0f, 0f, -1f);

            float len = Math.Clamp(pj.Velocity.Length() * ProjectileVisibility.BoltLengthPerUnitSeconds,
                ProjectileVisibility.MinBoltLength, ProjectileVisibility.MaxBoltLength);
            float width = projectileTypes is not null
                ? ProjectileStyle.For(pj.ProjectileTypeId, projectileTypes).Width
                : ProjectileStyle.DefaultWidth;

            if (ProjectileVisibility.CameraCulled(
                    cameraEye.X, cameraEye.Y, cameraEye.Z,
                    pos.X, pos.Y, pos.Z,
                    dir.X, dir.Y, dir.Z,
                    len, width, ProjectileVisibility.NearCamClearance))
            {
                Diag.CameraCulled++;
                continue;
            }

            if (!pj.EverDrawn)
            {
                // P542 — count each UNIQUE round as "drawn" once (first time it
                // survives the radius + camera culls), so the drawn counters are
                // comparable to the per-broadcast received counters.
                pj.EverDrawn = true;
                Diag.RecordDrawn(ClassifyLauncher(pj.LauncherId, pj.LauncherType));
                drawn++;
            }
        }
        return drawn;
    }

    /// <summary>The stale horizon for a missile entity, in local ms: if no
    /// per-tick MISSILE_UPDATE has refreshed it for this long, prune it. Longer
    /// than the longest real missile lifespan in the demo core (19s for the
    /// dumbfire/launcher types), so a live missile is never pruned early — the
    /// guard exists only to cover a missed MISSILE_DESTROYED(83).</summary>
    public const long MissileStaleHorizonMs = 30_000;

    /// <summary>FM_CS_FIRE_MISSILE(70) — the server's creation broadcast for a
    /// freshly-launched missile. Hydrates (or refreshes) the id-keyed missile
    /// entity from the single <see cref="MissileLaunchData"/> entry (P90 sends
    /// exactly one missile per fire) plus the message's type/cluster, stamped
    /// with the local receipt instant. Later per-tick MISSILE_UPDATE(205)
    /// records keep the same id current (upsert).</summary>
    /// <summary>Confirmed own missile quantity, not fire requests. Native consumes
    /// the salvo count (clintlib/clintlib.cpp:3114-3120,3206).</summary>
    public long OwnMissilesFired { get; private set; }

    private void Apply(FireMissile m)
    {
        if (m.Entries.Count == 0) return;               // degenerate (legal) empty salvo
        if (m.LauncherId == MyShipId) OwnMissilesFired += m.Entries.Count;
        foreach (var e in m.Entries)
        {
            _missiles[e.MissileId] = new MissileEntity
            {
                MissileId = e.MissileId,
                LauncherId = m.LauncherId,
                TargetType = (IgcObjectType)m.TargetType,
                TargetId = m.TargetId,
                TypeId = m.MissileTypeId,
                ClusterId = m.ClusterId,
                Position = e.VecPosition,
                Forward = e.VecForward.LengthSquared() > 1e-6f ? e.VecForward.Normalize() : new Vector3(0f, 0f, -1f),
                LastUpdatedLocalMs = (long)(NowSeconds() * 1000),
            };
            _lastMissileFiredByShip[m.LauncherId] = e.MissileId;
        }
    }

    // Native GetLastMissileFired (WinTrek.cpp:7237-7268). Expiry of the last
    // shot falls back to chase; it must never switch to an older or enemy shot.
    private readonly Dictionary<short, short> _lastMissileFiredByShip = new();
    public MissileEntity? LastMissileFiredBy(short shipId) =>
        _lastMissileFiredByShip.TryGetValue(shipId, out short id) && _missiles.TryGetValue(id, out var missile) && missile.LauncherId == shipId ? missile : null;

    /// <summary>FM_S_MISSILE_UPDATE(205) — the P234 per-tick ongoing-position
    /// stream. Upsert every carried record by missile id (an id not seen via a
    /// FIRE_MISSILE creation — e.g. a peer that missed the one-shot — is hydrated
    /// straight from the update), refreshing the authoritative position/heading
    /// and stamping the receipt instant so the stale pruner knows it is alive.</summary>
    private void Apply(MissileUpdate m)
    {
        long now = (long)(NowSeconds() * 1000);
        foreach (var r in m.Records)
        {
            if (!_missiles.TryGetValue(r.MissileId, out var missile))
                _missiles[r.MissileId] = missile = new MissileEntity { MissileId = r.MissileId };
            missile.TypeId = r.TypeId;
            missile.ClusterId = r.ClusterId;
            missile.Position = r.Position;
            missile.Forward = r.Forward.LengthSquared() > 1e-6f ? r.Forward.Normalize() : new Vector3(0f, 0f, -1f);
            missile.LastUpdatedLocalMs = now;
        }
    }

    /// <summary>FM_S_MISSILE_DESTROYED(83) — the server's destruction notice:
    /// remove the missile by id (it has expired or been destroyed).</summary>
    private void Apply(MissileDestroyed m) => _missiles.Remove(m.MissileId);

    /// <summary>Remove missile entities that have not been refreshed by a per-tick
    /// MISSILE_UPDATE for <see cref="MissileStaleHorizonMs"/>, i.e. that are no
    /// longer in flight server-side (a missed MISSILE_DESTROYED(83) guard — the
    /// per-tick 205 stream stops for an expired missile at the same instant 83 is
    /// sent, so both removal paths agree). Pure (no clock) — the caller supplies
    /// the local monotonic instant and typically calls this once per frame under
    /// the gate lock before gathering the live set to render.</summary>
    public void PruneExpiredMissiles(long nowLocalMs)
    {
        foreach (var id in _missiles.Where(kv => kv.Value.LastUpdatedLocalMs + MissileStaleHorizonMs <= nowLocalMs).Select(kv => kv.Key).ToList())
            _missiles.Remove(id);
    }

    /// <summary>Remove explosion entities whose category's real visual duration
    /// has elapsed (<see cref="ExplosionPreset.TotalDurationSeconds"/>), so a
    /// finished blast leaves the render set rather than lingering forever. Pure
    /// (no clock) — the caller supplies the local monotonic instant and calls it
    /// once per frame under the gate lock before gathering the live set.</summary>
    public void PruneExpiredExplosions(long nowLocalMs)
    {
        for (int i = _explosions.Count - 1; i >= 0; i--)
        {
            var e = _explosions[i];
            float dur = ExplosionPresets.For(e.Type).TotalDurationSeconds;
            if ((nowLocalMs - e.ReceivedLocalMs) / 1000.0f >= dur)
                _explosions.RemoveAt(i);
        }
    }

    private void Apply(PlayerInfo info)
    {
        var e = Upsert(info.ShipId);        // create-if-absent: a ship we only
                                            // heard of via PLAYERINFO
        e.Player = info;
        e.SideId = info.ISide;
        if (info.ShipId == (MyShipId ?? JoinedShipId))
        {
            MySideId = info.ISide;
            ConfirmBotSide();
        }
        if (string.IsNullOrEmpty(e.CharacterName) || e.CharacterName == "<error>") e.CharacterName = info.CharacterName;
    }

    private void Apply(ScoreRosterUpdate m) =>
        // P265 — replace the whole scoreboard snapshot on each periodic receive.
        _scoreRoster = m.Entries;

    private void Apply(ClusterInfo info) =>
        // One-shot: capture the graph verbatim; a repeat CLUSTERINFO (unusual)
        // is ignored rather than rebuilt.
        Graph ??= new ClusterGraph { Clusters = info.Clusters, Warps = info.Warps };

    /// <summary>FM_S_DOCKED(61) — the authoritative dock/undock latch. The
    /// message carries no ship id (only the station id) and the server routes
    /// it only to the docking peer, so it always concerns our own ship: apply
    /// it to <see cref="MyShipId"/>. The real client treats this message as the
    /// <b>only</b> site that resolves <c>GetStation()</c> (appmsg.cpp:1113),
    /// so even though SHIP_STATUS(90) also carries <c>StationId</c>, this
    /// message is the transition trigger — mirror that by having it win on the
    /// StationId field too (it is applied last when both arrive around the same
    /// event, which the server serialises as status-then-notify).</summary>
    private void Apply(DockedNotify notify)
    {
        if (MyShipId is not { } id) return;   // not assigned a ship yet — nothing to dock
        var e = Upsert(id);
        bool wasDocked = e.StationId >= 0;
        e.StationId = notify.StationId;
        // P719 — SIM-0388/0389: the LOCAL ship docked (entering the hangar),
        // native plays dockedSound + salWelcomeHomeSound (trekigc.cpp:2859-2860).
        // Edge-triggered on StationId going from none to a real station.
        if (!wasDocked && notify.StationId >= 0)
        {
            RecordSoundEvent(ClientSoundEventKind.Docked, id);
            RecordSoundEvent(ClientSoundEventKind.WelcomeHome, id);
        }
    }

    /// <summary>FM_S_BUY_LOADOUT_ACK(5) — the granted loadout. The message has
    /// no ship id and the server replies only to the requesting peer
    /// (Server.cs ApplyLoadout → SendPeer(from)), so it always concerns our own
    /// ship: apply it to <see cref="MyShipId"/>, adopting the granted
    /// <c>HullId</c> and mounting the ack's part tail.</summary>
    private void Apply(BuyLoadoutAck ack)
    {
        if (MyShipId is not { } id) return;   // not assigned a ship yet — nothing to load
        var e = Upsert(id);
        e.HullId = ack.HullId;          // the granted hull (echo of the requested one)
        // P626 — the ack tail mixes mounted (non-negative mounts) and cargo
        // (negative mounts, encoded as high-bit bytes — native Mount is a
        // signed char) exactly like native's ProcessShipLoadout; split them so
        // the mounted tail feeds the existing Loadout consumers (docked loadout
        // preview) unchanged and the cargo hold feeds the in-flight cargo keys.
        var mounted = new List<Allegiance.Core.Wire.ExpandedPartData>();
        var cargo = new List<Allegiance.Core.Wire.ExpandedPartData>();
        foreach (var p in ack.Parts)
        {
            if (p.MountId >= 0x80)   // high bit => negative (cargo) mount
                cargo.Add(p);
            else
                mounted.Add(p);
        }
        e.Loadout = mounted;
        e.Cargo = cargo;
    }

    /// <summary>
    /// P648 — <c>FM_S_LOADOUT_CHANGE(96)</c>: a ship's whole loadout (hull +
    /// mounted parts + boarded children) changed, ported from native
    /// <c>appmsg.cpp:1991-2084</c> (server half: <c>CFSShip::QueueLoadoutChange</c>
    /// builds it fresh per event, <c>FedSrv/fsship.cpp:708-735</c>). The port's
    /// server broadcasts it <b>same-side</b> (<see cref="Server.Combat.Server.BroadcastLoadoutChange"/>)
    /// whenever a ship's hull/loadout changes — <b>including the post-death
    /// relaunch</b>: a docked player buying a new ship runs <c>SetBaseHullType</c>,
    /// which raises this with the new hull (the clash36 trace: "swapping base
    /// hull 113 ('Lifepod') -&gt; 15 ('Adv Fighter') … LOADOUT_CHANGE(96) hull 15 -&gt;
    /// 195 same-side peer(s)"). This was a documented <b>unconsumed</b> message
    /// (P627 known-unconsumed); the client dropped it, so a same-side peer that
    /// had latched the ship dead (KILL_SHIP / SHIP_DELETE SDR_KILLED) kept it
    /// dead and kept its pre-relaunch hull until a later HEAVY_SHIPS_UPDATE.
    ///
    /// Native's handler is the canonical "alive again" revive: it detaches the
    /// ship from any parent (<c>SetParentShip(NULL)</c>, :2007-2008), re-applies
    /// the whole loadout including the hull (<c>ProcessShipLoadout</c>,
    /// :2029-2031 — the <c>bFirstOrder</c> argument is literally
    /// <c>pship-&gt;GetCluster() == NULL</c>, i.e. "was cached away by
    /// SDR_KILLED", which is exactly the death-latch undo), and re-seats boarded
    /// children (:2035-2079). This mirrors that for the port's pure-renderer
    /// client: adopt the new hull + parts, clear the death latch and per-life
    /// caches so the ship renders/is targetable again, and re-parent any
    /// boarded children. (<c>sidShip</c> is a real id from this port's server,
    /// but native also resolves <c>NA</c> to the local ship, :1998-2000.)
    /// </summary>
    private void Apply(Allegiance.Core.Wire.LoadoutChange m)
    {
        short? targetId = m.SidShip == Na ? MyShipId : m.SidShip;
        if (targetId is not { } id) return;              // no ship to re-load
        MarkSnapshotDirty();
        var e = Upsert(id);

        // Native: a passenger is no longer boarded (SetParentShip(NULL),
        // appmsg.cpp:2007-2008).
        e.ParentId = -1;

        // Native re-applies the whole loadout incl. hull (ProcessShipLoadout,
        // :2029-2031). Split mounted vs cargo like BuyLoadoutAck (P626).
        e.HullId = m.HullId;
        var mounted = new List<Allegiance.Core.Wire.ExpandedPartData>();
        var cargo = new List<Allegiance.Core.Wire.ExpandedPartData>();
        foreach (var p in m.Parts)
        {
            if (p.MountId >= 0x80) cargo.Add(p);
            else mounted.Add(p);
        }
        e.Loadout = mounted;
        e.Cargo = cargo;

        // Native re-seats boarded children (passenger loop, :2035-2079).
        foreach (var pd in m.Passengers)
        {
            if (pd.ShipId != id && _ships.TryGetValue(pd.ShipId, out var child))
            {
                // P719 — SIM-0432: a player boarded the LOCAL ship (native
                // boardSound, appmsg.cpp:2071: `pship == m_ship && m_ship->GetCluster()
                // == NULL`). Edge-triggered when a passenger's parent becomes
                // the local ship and it wasn't already parented to it.
                if (id == MyShipId && child.ParentId != id)
                    RecordSoundEvent(ClientSoundEventKind.Boarded, pd.ShipId);
                child.ParentId = id;
            }
        }

        // P648 — the relaunch revive, gated on the ship having actually been
        // latched dead so a routine mid-flight part/hull change (mining mount,
        // boarding) never disturbs a live ship's dead-reckoner or re-cues it,
        // AND on the new hull being a genuine relaunch hull rather than the
        // side's Lifepod. The death path itself fires a LOADOUT_CHANGE to a
        // Lifepod hull (ChangeHull(lifepod) on the kill seam); that is NOT an
        // "alive again" signal — it must leave the death latched (the pod stays
        // dead until a real relaunch). Only a change to a confirmed NON-lifepod
        // hull (the relaunch: Lifepod -> fighter, the clash36 "113 -> 15" case)
        // is the revive.
        bool wasDead = e.IsDead;
        e.IsGone = false;
        if (wasDead && !IsLifepodHullFor(m.HullId, e.SideId))
        {
            // A same-side LOADOUT_CHANGE with the new hull is the server's
            // "this ship is alive again, with hull H" signal — the same-side
            // counterpart of SET_CLUSTER/EXPORT/176 for remote ships. Clear the
            // death latch and per-life caches (native's bFirstOrder re-seat,
            // :2030): the dead-reckoner is dropped so the next pose/176 re-seeds
            // fresh, and the first-sighting latch is reset so the return cues
            // as a fresh sighting (P373 semantics).
            e.IsDead = false;
            e.NewContactSounded = false;
            _remoteReckoners.Remove(id);
        }
    }

    /// <summary>P648 — whether a LOADOUT_CHANGE hull is the side's Lifepod, i.e.
    /// a death/pod swap rather than a relaunch. Resolves via
    /// <see cref="LifepodHullResolver"/> (wired by the MonoGame client,
    /// Game1.Lifecycle); when the side's lifepod is unknown (resolver absent —
    /// e.g. the headless test harness) it is treated AS a lifepod so a
    /// LOADOUT_CHANGE never spuriously un-latches a death it cannot positively
    /// classify as a relaunch.</summary>
    private bool IsLifepodHullFor(short hullId, short? sideId)
    {
        var lp = LifepodHullResolver(sideId ?? -1);
        return lp is null || lp.Value == hullId;
    }

    // ── P442 money: SET_MONEY / MONEY_CHANGE / AUTODONATE / PAYDAY ─────────
    // Native keeps each ship's money + auto-donate target on its PlayerInfo
    // (appmsg.cpp:1248-1383); the port mirrors that per-ship-id on the world so
    // the HUD can show the local player's wallet and payday can route shares.

    /// <summary>P442 — <c>FM_S_SET_MONEY(115)</c>: set that ship's player's
    /// money absolutely (native appmsg.cpp:1367-1383). Sent for every side
    /// player to a joiner by <c>SyncMoneyStateToJoiner</c>, and for the joiner's
    /// own ship by the join-time SET_MONEY in the launch burst.</summary>
    private void Apply(SetMoney m)
    {
        _moneyByShipId[m.ShipId] = m.Money;
    }

    /// <summary>P442 — <c>FM_S_AUTODONATE(116)</c>: set
    /// <c>SidDonateBy</c>'s auto-donate relationship, then apply the one-time
    /// gift (if any) exactly as native does (appmsg.cpp:1192-1278):
    /// <list type="bullet">
    /// <item>gift to a target: +amount to the target, and -amount to the donor
    /// ONLY when the donor is not the local ship (<c>pshipBy != m_ship</c>,
    /// appmsg.cpp:1248-1263) — the local client already zeroed its own wallet at
    /// send time (<see cref="ZeroOwnWallet"/>, teampane.cpp:1241);</item>
    /// <item>refused request (<c>SidDonateTo == NA</c>): refund +amount to the
    /// local ship (appmsg.cpp:1265-1270).</item>
    /// </list></summary>
    private void Apply(AutoDonateUpdate m)
    {
        // Relationship first (NA = absent = donates to no one). Remember the
        // previous target so we can detect a NEW "donate to me" relationship
        // (native's pshipOld != pshipTo change test, appmsg.cpp:1202-1205).
        bool hadPrevious = _autoDonateTargetByShipId.TryGetValue(m.SidDonateBy, out short oldTarget);
        bool relationshipChanged = !hadPrevious || oldTarget != m.SidDonateTo;

        if (m.SidDonateTo == Na) _autoDonateTargetByShipId.Remove(m.SidDonateBy);
        else _autoDonateTargetByShipId[m.SidDonateBy] = m.SidDonateTo;

        // P714 — native investorSound (appmsg.cpp:1227-1238): when a teammate
        // (not ourselves) begins donating to US and, after the change, we have
        // exactly one donor, native plays the investor VO.
        if (relationshipChanged && m.SidDonateBy != MyShipId && m.SidDonateTo == MyShipId && MyShipId is { } me)
        {
            int donors = 0;
            foreach (var kv in _autoDonateTargetByShipId)
                if (kv.Value == me) donors++;
            if (donors == 1)
                RecordSoundEvent(ClientSoundEventKind.InvestorReceived, m.SidDonateBy);
        }

        if (m.Amount != 0)
        {
            if (m.SidDonateTo != Na)
            {
                // One-time gift to an existing target: +amount to the target,
                // and -amount to the donor only when it is not us (the local
                // wallet was already zeroed at send time, teampane.cpp:1241).
                AddMoney(m.SidDonateTo, m.Amount);
                if (m.SidDonateBy != MyShipId)
                    AddMoney(m.SidDonateBy, -m.Amount);
            }
            else if (m.SidDonateBy == MyShipId)
            {
                // We made a donation request that was refused — refund it
                // (appmsg.cpp:1265-1270).
                AddMoney(m.SidDonateBy, m.Amount);
            }
        }
    }

    /// <summary>
    /// P714 — <c>FM_S_GAIN_FLAG(156)</c> objective announce VOs, faithful to
    /// native <c>appmsg.cpp:3137-3170</c>:
    /// <list type="bullet">
    /// <item>recipient on OUR side + flag origin != lobby (sentinel -2) →
    /// <c>enemyFlagLostSound</c> (we stole an enemy flag);</item>
    /// <item>recipient on OUR side + flag origin == lobby → <c>artifactFoundSound</c>;</item>
    /// <item>otherwise flag origin == OUR side → <c>flagLostSound</c> (an enemy
    /// stole our flag).</item>
    /// </list>
    /// The classification needs the RECIPIENT ship's side (native <c>pship-&gt;GetSide()</c>);
    /// when that ship is not in our entity table the cue is skipped (documented
    /// limitation — the whole-mission announce can name carriers outside our
    /// visible sector).</summary>
    private void Apply(GainFlag m)
    {
        if (_waitingForRestart) return; // appmsg.cpp:3115: waiting for restart
        if (!_ships.TryGetValue(m.ShipidRecipient, out var recipient))
            return;                                 // recipient side unknown → cannot classify

        short mySide = MySideId ?? Na;
        if (m.BIsTreasureDocked)
        {
            if (recipient.SideId == mySide && ResolveInterfacePart(m.ParttypeidDocked) is { } part)
                PostText(true, "{0} has secured {1}", recipient.CharacterName, part.Name);
            return; // tech recovery never changes the carried flag or voices it
        }
        recipient.CarriedFlagSideId = m.SideidFlag;
        if (m.SideidFlag == GainFlag.NA) return;
        string SideName(short side) => SideRoster?.Sides.FirstOrDefault(s => s.SideId == side).Name ?? $"side {side}";
        if (recipient.SideId == mySide)
        {
            // A teammate picked up a flag (enemy or neutral-artifact).
            if (m.SideidFlag != Side.ArtifactSentinelFlag)
            {
                PostText(true, "{0} has stolen {1}'s flag.", recipient.CharacterName, SideName(m.SideidFlag));
                RecordSoundEvent(ClientSoundEventKind.EnemyFlagStolen, m.ShipidRecipient);
            }
            else
            {
                PostText(true, "{0} has found an artifact.", recipient.CharacterName);
                RecordSoundEvent(ClientSoundEventKind.ArtifactFound, m.ShipidRecipient);
            }
        }
        else if (m.SideidFlag > 0 && m.SideidFlag == mySide)
        {
            // An enemy is carrying OUR side's flag.
            PostText(true, "{0} of {1} has stolen our flag.", recipient.CharacterName, SideName(recipient.SideId));
            RecordSoundEvent(ClientSoundEventKind.OurFlagStolen, m.ShipidRecipient);
        }
    }

    /// <summary>P442 — <c>FM_S_MONEY_CHANGE(60)</c>: a per-ship money delta.
    /// Native (appmsg.cpp:1326-1366) skips the whole message when
    /// <c>sidFrom == my ship</c>, because the local client already applied the
    /// change at purchase time. <b>This port diverges deliberately:</b> the
    /// server is authoritative and the client does <em>not</em> apply purchases
    /// locally, so we ALWAYS add <c>dMoney</c> to <c>sidTo</c>. That is exactly
    /// what makes a donor's own wallet decrement precisely once on a bucket
    /// spend — whose broadcast is <c>sidFrom == sidTo == donor</c>
    /// (Server.Economy.cs BroadcastBucketDonateSpendSideWide) — and the
    /// P441 purchase broadcast (<c>sidFrom = NA</c>) behave identically for the
    /// buyer. A <c>sidTo != sidFrom && sidFrom != NA</c> message is a
    /// player-to-player donation, so also debit <c>sidFrom</c>.</summary>
    private void Apply(MoneyChange m)
    {
        AddMoney(m.SidTo, m.DMoney);
        if (m.SidTo != m.SidFrom && m.SidFrom != Na)
            AddMoney(m.SidFrom, -m.DMoney);   // player-to-player donation: debit the sender
    }

    /// <summary>P442 — <c>FM_S_PAYDAY(114)</c>: for every player ship on this
    /// side, credit <c>dMoney</c> to that ship's auto-donate target when it has
    /// one, else to the ship itself (native appmsg.cpp:1281-1323). The port's
    /// server sends PAYDAY and then credits wallets the same way
    /// (Server.Economy.cs OnPayday), with <em>no</em> MONEY_CHANGE at payday —
    /// so the client derives the credit from this message + the donate
    /// relationships it already holds, exactly as native does.</summary>
    private void Apply(Payday m)
    {
        foreach (var ship in KnownPlayerShips())
        {
            var target = _autoDonateTargetByShipId.TryGetValue(ship, out var t) ? t : ship;
            AddMoney(target, m.DMoney);
        }
        _events.Record(m);   // the pre-existing event-log record is kept
        // P710 — native plays the payday cue (appmsg.cpp:1313) on each payday.
        RecordSoundEvent(ClientSoundEventKind.PaydayReceived, Na);
    }

    /// <summary>P710 — <c>FM_S_ACQUIRE_TREASURE(162)</c>: this client's ship
    /// picked up a treasure. The pickup drives the per-type cue from native's
    /// FM_S_ACQUIRE_TREASURE handler (clintlib/appmsg.cpp:262-308): a
    /// <c>c_tcPowerup</c> pickup plays <c>pickUpPowerupSound</c> (:282), a
    /// <c>c_tcDevelopment</c> pickup plays <c>pickUpDevelopmentSound</c> (:286),
    /// a <c>c_tcCash</c> pickup plays <c>pickUpCashSound</c> (:291), and a
    /// <c>c_tcFlag</c> pickup reuses the development cue (:303 — the dedicated
    /// flag cue is NYI in native). A <c>c_tcPart</c> pickup plays
    /// <c>pickUpPartSound</c> (P735 — SIM-0268; the player-facing cue native
    /// plays server-side at the ship, shipIGC.h:1307, while the client handler
    /// is silent and only sends a TREASURE_ACK). The message carries no ship
    /// id; it is only ever addressed to the acquiring client, so the cue is
    /// recorded unconditionally.
    private void Apply(AcquireTreasure m)
    {
        // P758 — the native handler's first gate (appmsg.cpp:264):
        // a client that is not in game ignores the pickup notice entirely.
        if (!InGame) return;

        // P758 — c_tcCash carries the reward text (appmsg.cpp:289-299): the
        // autodonate target's name when one is set ("You donated your reward
        // of $%d to %s."), else the plain reward line ("You received a reward
        // of $%d."). The picking side's wallets already moved server-side
        // (MONEY_CHANGE); this is the player-facing notice.
        if ((Allegiance.Core.TreasureCode)m.TreasureCode == Allegiance.Core.TreasureCode.Cash)
        {
            string? donateName = null;
            if (MyShipId is { } me
                && _autoDonateTargetByShipId.TryGetValue(me, out short target)
                && _ships.TryGetValue(target, out var targetShip))
                donateName = targetShip.CharacterName;
            PostSystemLine(donateName is { Length: > 0 } named
                ? $"You donated your reward of ${m.Amount} to {named}."
                : $"You received a reward of ${m.Amount}.");
        }

        ClientSoundEventKind? kind = (Allegiance.Core.TreasureCode)m.TreasureCode switch
        {
            Allegiance.Core.TreasureCode.Powerup => ClientSoundEventKind.PickupPowerup,
            Allegiance.Core.TreasureCode.Development => ClientSoundEventKind.PickupDevelopment,
            Allegiance.Core.TreasureCode.Cash => ClientSoundEventKind.PickupCash,
            Allegiance.Core.TreasureCode.Flag => ClientSoundEventKind.PickupDevelopment, // NYI flag cue → reuses development (appmsg.cpp:303)
            // P735 — SIM-0268: a c_tcPart pickup. Native's client ACQUIRE_TREASURE
            // handler is silent for parts (appmsg.cpp:266-275 — it only sends a
            // TREASURE_ACK); the player-facing pickUpPartSound is played
            // server-side at the ship (shipIGC.h:1307). The port reproduces that
            // cue on the same local pickup.
            Allegiance.Core.TreasureCode.Part => ClientSoundEventKind.PickupPart,
            _ => null,
        };
        if (kind is { } k)
            RecordSoundEvent(k, m.TreasureId);
    }

    // ── Event log (transient, append-only, bounded) ────────────────────────
    /// <summary>P710 — classify an incoming chat line's announcement cue
    /// exactly as native's <c>ReceiveChat</c> decides which wave to play
    /// (trekigc.cpp:4536-4560). The port reproduces the non-sonic destination
    /// tree: a message carrying a real command id plays <c>newCommandMsgSound</c>
    /// (:4540); an individual message addressed to the local pilot plays
    /// <c>newPersonalMsgSound</c> (:4548) — or the commander variant when sent
    /// by the team leader (:4546); any other line plays <c>newChatMsgSound</c>
    /// (:4557) — the commander variant when the sender leads the team (:4555) —
    /// or the quieter <c>newOfflineChatMsgSound</c> when disconnected (:4560).
    /// Own echoes are dropped before the sound stage (appmsg.cpp:2644), and a
    /// sonic chat or direct SoundID takes precedence over the plain chat cue.
    /// <paramref name="senderIsLeader"/> mirrors native's <c>bIsLeader</c>
    /// (<c>ppi-&gt;IsTeamLeader()</c>); <paramref name="online"/> mirrors
    /// <c>m_fm.IsConnected()</c> (always true in this client, present so the
    /// offline branch is faithfully representable).</summary>
    public static ClientSoundEventKind? ClassifyChatSound(
        ChatMessage m, short? myShipId, bool senderIsLeader, bool online = true)
    {
        // Native voices outgoing sonic chat locally, then ignores its wire
        // echo (appmsg.cpp:2644). The port voices that single server echo.
        bool sonic = Allegiance.Core.Audio.NativeVoiceCatalog.SonicChats.TryGetValue(m.VoiceOver, out var entry)
            && (m.MessageIsAbsent || m.Message == entry.Caption);
        if (myShipId is { } me && m.SidSender == me && !sonic)
            return null;                              // own echo — dropped (:2644)

        if (m.VoiceOver != Na)
            return ClientSoundEventKind.ChatVoiceOver; // (:4530-4536); unknown IDs stay silent

        // Native precedence: command → individual/personal → general.
        if (m.CommandID != Na)
            return ClientSoundEventKind.ChatCommand;  // (:4540)

        bool individualToMe =
            m.ChatTarget == (int)Allegiance.Core.Sim.ChatTarget.Individual
            && (m.OidRecipient == Na || (myShipId is { } mine && m.OidRecipient == mine));
        if (individualToMe)
            return senderIsLeader
                ? ClientSoundEventKind.ChatCommander  // commander/training PM (:4546)
                : ClientSoundEventKind.ChatPersonal;  // (:4548)

        if (!online)
            return ClientSoundEventKind.ChatOffline;  // (:4560)

        return senderIsLeader
            ? ClientSoundEventKind.ChatCommander      // leader-sent general (:4555)
            : ClientSoundEventKind.ChatRegular;       // (:4557)
    }

    private void Apply(ChatMessage m) => ReceiveChat(m);

    /// <summary>P626 — <c>FM_S_BALLOT(17)</c>: a new in-flight ballot was
    /// announced (P187). The client latches it as the <see cref="ActiveBallot"/>
    /// so the VoteYes/VoteNo keys can cast a VOTE(19) against it, and surfaces
    /// the proposal as a system chat line so the pilot actually sees what they
    /// are voting on (the port has no native-style ballot popup).</summary>
    private void Apply(Ballot m)
    {
        _pendingBallots.Add(new PendingBallotView(m.BallotId, m.BallotText, m.TimeExpiration));
        _events.Record(new ChatMessage
        {
            Message = "[VOTE] " + m.BallotText,
            SidSender = Na,
            ChatTarget = (int)ChatTarget.Everyone,
        });
    }
    private void Apply(ChatBuoy m) => ReceiveChat(m);
    private void Apply(KillShip m)
    {
        _events.Record(m);
        // P578 — native sends KILL_SHIP(105) on BOTH a true death AND an
        // in-space eject, distinguishing them via bDeathCredit (= bKilled,
        // FedSrv.CPP:11797-11798). An eject's ship survives as a drifting
        // Lifepod (handled by the EJECT(77) Apply), so the death flags must be
        // set ONLY for a true death — otherwise the ejecting pod would be marked
        // dead/gone and vanish. (Previously the port suppressed KILL_SHIP on
        // eject entirely, so Apply(KillShip) could assume death.)
        var e = Upsert(m.ShipId);
        // A killed (or ejected) ship's ripcord is over whether or not the server
        // credited a death; the countdown belongs to the model that just died.
        _remoteRipcords.Remove(m.ShipId);
        if (m.BDeathCredit)
        {
            // HUD-12 — native's KILL_SHIP handler credits the victim's
            // PlayerInfo death and the killer's PlayerInfo kill
            // (appmsg.cpp:2766-2773,2807-2810, PlayerInfo::AddDeath/AddKill at
            // clintlib.h:346-347). Those are the counters the console's lives /
            // kills badges read (console.cpp:812-821), so keep them current
            // from the same events native does; PLAYERINFO(10) seeds them at
            // join (appmsg.cpp:2254-2256, FedSrv.CPP:2085-2086).
            e.Player?.AddDeath();
            // The victim stays in the entity table (that's the point of keying by
            // id — "did ship X die" stays answerable) but gains the death flag.
            e.IsDead = true;
            e.PoseSectorId = null;
            e.IsGone = true;   // P370 — a dead ship is also out of view (unified render exclusion)
            e.NewContactSounded = false;   // P373 — a corpse does NOT cue while dead (IsDead guards AnnounceNewContactIfNew); if it GENUINELY respawns (P521 clears IsDead on the real re-presence signal) re-entering view is a fresh sighting and cues again.
            // P448b — a dead ship stops being dead-reckoned (its extrapolation would walk
            // a corpse onward); a recycled id re-seeds fresh when/if it comes back.
            _remoteReckoners.Remove(m.ShipId);

            // P760b — enqueue the kill for the game thread's native-style
            // retargeting (trekigc.cpp:2934-2974). Never raised inline: this
            // Apply runs on the receive thread and the retarget reads the own
            // pose and mutates Game1's target state.
            if (_ships.ContainsKey(m.ShipId))
                RecordTargetKilled(IgcObjectType.Ship, m.ShipId);
        }

        // P578 — produce the eject/kill HUD feedback line for this receiver's
        // perspective (appmsg.cpp:2797-2844) and queue the "gloat camera" swing
        // to the killer when this receiver's own ship was hit (appmsg.cpp:2900-2902,
        // clintlib.cpp:3063, trekigc.cpp:3101-3105).
        // HUD-12 — the credited killer's kill (native only when the killer's
        // PlayerInfo exists: a launcher that is a ship with player data,
        // appmsg.cpp:2807-2810; NPCs have no PlayerInfo here).
        if (m.BKillCredit && m.TypeCredit == (short)IgcObjectType.Ship && m.IdCredit >= 0
            && _ships.TryGetValue(m.IdCredit, out var killerEnt))
            killerEnt.Player?.AddKill();

        short me = MyShipId ?? -1;
        var line = EjectFeedback.ForKillShip(m, me, DisplayNameFor(m.ShipId), KillerNameFor(m));
        _feedbackLines.Add(line with { AtSeconds = NowSeconds() });
        if (_feedbackLines.Count > MaxFeedbackLines) _feedbackLines.RemoveAt(0);
        if (EjectFeedback.WantsOverrideToKiller(m, me))
        {
            _cameraOverrideRequests.Add(new CameraOverrideRequest(m.IdCredit, NowSeconds()));
            if (_cameraOverrideRequests.Count > MaxCameraOverrideRequests)
                _cameraOverrideRequests.RemoveAt(0);
        }
        ChangeGameState();
    }

    /// <summary>P578 — the best-effort display name for a ship entity: its
    /// wire <c>CharacterName</c> when known (incl. the roster-side name), else a
    /// neutral "Ship &lt;id&gt;" label so the feedback line is never blank.</summary>
    private string DisplayNameFor(short shipId)
    {
        if (_ships.TryGetValue(shipId, out var se) && se.CharacterName.Length > 0)
            return se.CharacterName;
        return $"Ship {shipId}";
    }

    /// <summary>P578 — the credited killer's name (null when no launcher is
    /// credited, <c>idCredit == NA(-1)</c> — native's <c>pLauncherInfo == NULL</c>
    /// case, appmsg.cpp:2798), resolved from the world's ship table by
    /// <see cref="KillShip.IdCredit"/>.</summary>
    private string? KillerNameFor(KillShip m)
        => m.TypeCredit != (short)IgcObjectType.Ship || m.IdCredit == -1 ? null : DisplayNameFor(m.IdCredit);

    /// <summary>P578 — <c>FM_S_PLAYER_RESCUED(16)</c>: a lifepod was picked up
    /// by another ship (appmsg.cpp:311-340). Previously ignored by the client;
    /// now produces the rescuer/rescuee feedback lines. The pod's flight is
    /// unaffected — docking (P575) and the eject autopilot (P540/P571) carry on.</summary>
    private void Apply(PlayerRescued m)
    {
        short me = MyShipId ?? -1;
        var line = EjectFeedback.ForPlayerRescued(
            m, me, DisplayNameFor(m.ShipIdRescuer), DisplayNameFor(m.ShipIdRescuee));
        if (line is null) return;
        _feedbackLines.Add(line.Value with { AtSeconds = NowSeconds() });
        if (_feedbackLines.Count > MaxFeedbackLines) _feedbackLines.RemoveAt(0);
    }

    // ── P638 — parity wave 2: player feedback messages ───────────────────────

    /// <summary>P638 — <c>FM_S_TELEPORT_ACK(102)</c>: the reply to our teleport
    /// dock request (<c>FedSrv.CPP:7054-7131</c>), sent to our own connection
    /// only. Accepted (<c>stationID != NA</c>) → native docks the ship and plays
    /// the <c>personalJumpSound</c> (appmsg.cpp:1901); the dock/relocation state
    /// itself arrives via the follow-up <c>SET_CLUSTER</c>/<c>SHIP_RESET</c>, so
    /// here we just queue the arrival jump cue. Denied (<c>stationID == NA</c>)
    /// → native silently ignores the request (appmsg.cpp:1856-1874) — that is
    /// exactly the "why didn't that work?" gap this wave targets, so the
    /// refusal is surfaced as a system line.</summary>
    private void Apply(TeleportAck m)
    {
        if (m.StationId == -1)
        {
            PostSystemLine("Teleport failed - unable to relocate to that station.");
            return;
        }
        RecordSoundEvent(ClientSoundEventKind.TeleportArrived, m.StationId);
    }

    /// <summary>Monotonic observation of BOARD_NACK, used to end pending
    /// docked disembark/launch sequences (native appmsg.cpp:1899-1911).</summary>
    public long BoardFailureRevision { get; private set; }

    /// <summary>P757 — the requested parent ship id from the most recent
    /// BOARD_NACK (native resolves <c>pfmBoardNack-&gt;sidRequestedParent</c> to
    /// <c>IshipIGC*</c> for the failure branch, appmsg.cpp:1908-1909;
    /// teleportpane.cpp:276-294). <c>Na</c> when no nack has arrived.</summary>
    public short LastBoardFailureRequestedParent { get; private set; } = -1;

    /// <summary>P638 — <c>FM_S_BOARD_NACK(178)</c>: the server refused our
    /// board/leave request (<c>FedSrv.CPP:6412-6416</c>), own connection only.
    /// Native surfaces it as a popup in the teleport pane's <c>OnBoardFailed</c>
    /// (teleportpane.cpp:276-293): "Teleportation canceled - no turret positions
    /// were open." (when the requested parent had a free turret, playing the
    /// <c>noTurretsSound</c>) else "Teleportation failed.". The MonoGame client
    /// has no modal box, so it surfaces the catch-all native string as a system
    /// line (the port's convention for refusals, cf. <see cref="RipcordDenied"/>).
    /// P757 — the full native recovery (appmsg.cpp:1899-1910 →
    /// <c>TeleportPane::OnBoardFailed</c>, teleportpane.cpp:276-294): gated on
    /// being in game; while the player is docked (own ship has no cluster,
    /// <c>GetShip()-&gt;GetCluster() == NULL</c>) the refusal picks the native
    /// branch — when the requested parent is known and still shows a free
    /// turret (<c>MannedTurrets &lt; NumTurrets</c>), the turret race message
    /// plus the <c>noTurretsSound</c> cue; otherwise the generic failure line.
    /// Manned turrets are the parent's children in the TURRET state only —
    /// native <c>MannedTurrets</c> counts <c>LastSeenState() == c_ssTurret</c>
    /// ships whose <c>LastSeenParent</c> is the requested parent
    /// (teleportpane.cpp:499-518); a docked or flying child never occupies a
    /// turret slot. Slots come from the mission core's hull table
    /// (<c>MaxWeapons - MaxFixedWeapons</c>, teleportpane.cpp:520-531).</summary>
    private void Apply(BoardNack m)
    {
        if (!InGame) return;                       // appmsg.cpp:1900-1901
        BoardFailureRevision++;
        LastBoardFailureRequestedParent = m.SidRequestedParent;

        bool docked = MyShipId is { } me
            && _ships.TryGetValue(me, out var own)
            && own.StationId != Na;                 // no live cluster (a gunner rides the parent's cluster)
        if (!docked) return;                       // appmsg.cpp:277-278

        bool turretRace = false;
        if (m.SidRequestedParent != Na && _ships.TryGetValue(m.SidRequestedParent, out var parent))
        {
            int slots = MissionCore is { } core
                && core.HullTypes.TryGetValue(parent.HullId, out var hull)
                ? hull.MaxWeapons - hull.MaxFixedWeapons
                : 0;
            int manned = 0;
            foreach (var s in _ships.Values)
                if (s.ParentId == m.SidRequestedParent
                    && s.State == ShipStatusMessage.StateTurret)   // c_ssTurret only, teleportpane.cpp:510
                    manned++;
            turretRace = manned < slots;
        }

        if (turretRace)
        {
            PostSystemLine("Teleportation canceled - no turret positions were open.");
            RecordSoundEvent(ClientSoundEventKind.BoardRefusedTurretsFull, m.SidRequestedParent);
        }
        else
        {
            PostSystemLine("Teleportation failed.");
        }
    }

    /// <summary>P638 — <c>FM_S_MISSILE_SPOOFED(110)</c>: one missile's lock was
    /// broken and it was retargeted onto the most recently deployed chaff decoy
    /// (native <c>pmissile->SetTarget(m_pchaffLastCreated)</c>, appmsg.cpp:1667-1681).
    /// This renderer-only client has no static core / per-missile target id, so
    /// it reproduces the retarget as the decoy's world position on the missile
    /// entity — letting the renderer draw the missile aiming at the decoy.
    /// Broadcast to the whole side (Server.ShipCommands.cs HandleFireExpendable).</summary>
    private void Apply(MissileSpoofed m)
    {
        if (_lastChaffPosition is not { } chaffPos) return;
        if (_missiles.TryGetValue(m.MissileId, out var missile))
            missile.SpoofTargetPosition = chaffPos;
    }

    /// <summary>P638 — <c>FM_S_END_SPOOFING(111)</c>: closes the spoof-resolution
    /// window by forgetting which decoy was "last created" (native sets
    /// <c>m_pchaffLastCreated = NULL</c>, appmsg.cpp:1686-1690). Clears the port's
    /// stand-in so a later <c>MISSILE_SPOOFED</c> no longer resolves against this
    /// decoy. Already-spoofed missiles keep their retarget (native does the same).</summary>
    private void Apply(EndSpoofing m)
    {
        _lastChaffPosition = null;
    }

    /// <summary>P638 — <c>FM_S_ENTER_LIFEPOD(166)</c>: sent to the dying player's
    /// own connection only (<c>FedSrv.CPP:12059-12062</c>) to force them into their
    /// side's Lifepod hull, detached from any parent and with a fresh loadout
    /// (native appmsg.cpp:1945-1990: <c>SetParentShip(NULL)</c>, <c>OnBoardShip</c>,
    /// <c>SetBaseHullType(lifepod)</c>, <c>ReplaceLoadout</c>). Mirrors the own-ship
    /// half of <see cref="Apply(Eject)"/>. Native's conversion is silent; the
    /// notice line is modern surfacing so the forced transition is visible.</summary>
    private void Apply(EnterLifepod m)
    {
        if (m.ShipId != MyShipId) return;
        MarkSnapshotDirty();
        var ent = Upsert(m.ShipId);
        ent.ParentId = -1;                             // SetParentShip(NULL), :1952
        if (LifepodHullResolver(ent.SideId) is { } lifepod)
            ent.HullId = lifepod;                      // SetBaseHullType(lifepod), :1963
        PostSystemLine("You have entered a lifepod.");
    }

    /// <summary>P638 — <c>FM_S_LEAVE_SHIP(94)</c>: broadcast to the whole side
    /// that a ship detached from its parent (<c>FedSrv.CPP:2316</c>). Native
    /// (appmsg.cpp:1914-1945): the leaver detaches; if the leaver is OUR ship we
    /// are thrown into a Lifepod; if the leaver's parent is our ship we get
    /// "<c>%s has left your ship.</c>" (:1935-1936). The own-ship notice line is
    /// modern surfacing (native performs the conversion silently).</summary>
    private void Apply(LeaveShip m)
    {
        if (m.SidChild == MyShipId)
        {
            MarkSnapshotDirty();
            var ent = Upsert(m.SidChild);
            ent.ParentId = -1;                          // SetParentShip(NULL), :1920
            // Disembarking a boarded seat with no own hull -> native converts to
            // the side's Lifepod (:1924-1929).
            if (LifepodHullResolver(ent.SideId) is { } lp)
                ent.HullId = lp;
            PostSystemLine("You have left your ship.");
            return;
        }
        if (_ships.TryGetValue(m.SidChild, out var child))
        {
            bool leftOurShip = child.ParentId == MyShipId;
            child.ParentId = -1;                        // no longer boarded anywhere
            if (leftOurShip)
                PostSystemLine($"{DisplayNameFor(m.SidChild)} has left your ship.");
        }
    }

    /// <summary>P638 — <c>FM_S_ASTEROID_MINED(198)</c>: the server's authoritative
    /// "this He3 rock was mined" notice carrying the rock's remaining ore as a
    /// div-240 <c>BytePercentage</c> (native <c>SetOreWithFraction</c>,
    /// appmsg.cpp:1733-1744, Xynth #132). Complements the periodic
    /// <c>ASTEROIDS_UPDATE(179)</c> sync (P423) by refreshing the live ore
    /// fraction immediately, so the renderer's rock readout depletes in step
    /// with the sim rather than waiting for the next cadence tick.</summary>
    private void Apply(AsteroidMined m)
    {
        float oreFrac = Math.Clamp(m.BpOreFraction / 240.0f, 0f, 1f);
        _asteroidLive.TryGetValue(m.AsteroidId, out var prev);
        _asteroidLive[m.AsteroidId] = new AsteroidLiveState(
            OreFraction: oreFrac,
            Fraction: prev.Fraction,
            InhibitOreExpiry: prev.InhibitOreExpiry);
    }

    /// <summary>P370 — <c>FM_S_SHIP_DELETE(20)</c>: the server's explicit "this
    /// ship is no longer in your view" notice. The MonoGame client previously
    /// only learned of departures via KILL_SHIP(105) (death); docking and
    /// sector warp-outs were never announced, so the departed ship's last-known
    /// pose rendered frozen forever. Consuming SHIP_DELETE closes that gap for
    /// every reason the server emits it:
    /// <list type="bullet">
    /// <item><c>SDR_KILLED</c> (1) — a death (arrives alongside KILL_SHIP; both
    /// paths agree).</item>
    /// <item><c>SDR_LEFTSECTOR</c> (2) — the ship warped to a different
    /// sector.</item>
    /// <item><c>SDR_DOCKED</c> (3) — the ship docked at a station.</item>
    /// <item><c>SDR_LOGGEDOFF</c> (4) — the owning peer disconnected.</item>
    /// </list>
    /// Only marks an ALREADY-KNOWN ship (guarded on entity existence) so a
    /// delete for a ship we never learned about cannot fabricate a phantom
    /// entry. Death latches <see cref="ShipEntity.IsDead"/> permanently; the
    /// transient reasons set only <see cref="ShipEntity.IsGone"/>, which a later
    /// pose/identity update clears when the ship returns to view (undocks,
    /// warps back, or is re-exported to us).</summary>
    private void Apply(ShipDelete m)
    {
        if (m.Sdr is ShipDelete.SdrKilled or ShipDelete.SdrTerminate)
            _retiredResearchProduction.Add(m.ShipId);
        if (m.ShipId == MyShipId) ShipWarnings.Reset();
        // HUD-12b — a deleted ship no longer has a live ripcord countdown, whatever
        // the reason (native removes/moves the ship model on SHIP_DELETE,
        // appmsg.cpp:455-495); pruning here also covers a departure that already
        // dropped it from the visible ship table.
        _remoteRipcords.Remove(m.ShipId);
        if (!_ships.TryGetValue(m.ShipId, out var e)) return;
        if (_botWorld is not null && m.Sdr == ShipDelete.SdrLeftSector
            && MyShipId is { } viewer && _ships.TryGetValue(viewer, out var own)
            && e.PoseSectorId != own.SectorId) return;
        MarkSnapshotDirty();
        RetireSectorPose(e);
        // P448b — a ship that left view (warped/docked/logged off) stops being
        // dead-reckoned; if it returns, the next pose update re-seeds fresh.
        _remoteReckoners.Remove(m.ShipId);
        // P373 — a ship that leaves view resets its first-sighting latch so a
        // later return (undock / warp-back / re-export) cues as a FRESH sighting.
        e.NewContactSounded = false;
        if (m.Sdr is ShipDelete.SdrKilled or ShipDelete.SdrTerminate)
            e.IsDead = true;
        // P585 — a ship that WARPED OUT of the viewer's sector (SDR_LEFTSECTOR,
        // the only departure-with-a-warp reason) crossed an aleph here; signal
        // the nearest warp to its last known position so the opt-in transit
        // pulse can flare that aleph. (Docking/logger are not warp crossings.)
        if (m.Sdr == ShipDelete.SdrLeftSector)
            MaybeSignalDeparture(e);
    }

    /// <summary>Changes on every SET_CLUSTER, including same-sector re-seats,
    /// and reconnect. Render pools use this to discard old cluster effects.</summary>
    public long SectorGeneration { get; private set; }

    private void RetireSectorPose(ShipEntity ship)
    {
        ship.PoseSectorId = null;
        ship.IsGone = true;
        ship.NewContactSounded = false;
        _remoteReckoners.Remove(ship.ShipId);
    }

    private void ClearSectorEffects()
    {
        _projectiles.Clear();
        _projectileDrainCursor = 0;
        _missiles.Clear();
        _lastMissileFiredByShip.Clear();
        _chaff.Clear();
        _lastChaffPosition = null;
        _explosions.Clear();
        _hullHits.Clear();
        _hitSoundEvents.Clear();
        _collisionEvents.Clear();
        _explosionSoundEvents.Clear();
        _alephTransits.Clear();
    }
    
    private void Apply(PlayerReady m)
    {
        // The current-state half: upsert this ship's ready flag (last write
        // wins — a lobby reads "is ship X ready NOW", not "when did X toggle").
        _roster.Set(m.ShipId, m.FReady);
        // The history half stays too — both are useful, for different questions.
        _events.Record(m);
    }

    // ── Mission info (persistent mission state, distinct from any ship) ────
    private void Apply(MissionDef def)
    {
        AttachBotWorld(def.DwCookie);
        // Both MISSIONDEF(38) and MISSION_STAGE(37) can carry a Stage. The
        // server sends MISSIONDEF first (twice, in fact, at stage 0) and
        // MISSION_STAGE last (StageStarted) in the join burst, but rather than
        // assume that order we honour the simplest honest rule — the one whose
        // Apply runs last owns Stage, because BOTH replace the whole MissionInfo
        // snapshot (and MISSIONDEF sets every field including Stage). So whoever
        // arrives last, their Stage is what the snapshot ends with; no special
        // ordering logic needed. The other four fields (static file/version/
        // cookie/owner) come only from MISSIONDEF.
        _mission = new MissionInfo
        {
            StaticCoreFile = def.SzIGCStaticFile,
            StaticCoreVersion = def.VerIgcCore,
            Cookie = def.DwCookie,
            GameLengthSeconds = def.GameLengthSeconds,
            ILives = def.ILives,
            SideMissionOwner = def.ISideMissionOwner,
            Stage = def.Stage,
            LockGameOpen = def.BLockGameOpen,
        };
        _missionTimeStartMs = def.TimeStart;
    }

    private void Apply(MissionStageMessage stage)
    {
        // P481 — detect the full game-over's transition INTO Over. The server
        // sends MISSION_STAGE Over to the old mission's peers (P439 line 420)
        // immediately before rotating + migrating them into a fresh world (the
        // P480 re-burst). That Over receipt is the order-safe point to discard
        // the finished game's world state (old ships/objects + our old
        // MyShipId) BEFORE the re-burst's new-world EXPORTs/SET_CLUSTER land —
        // so the rotation re-populates a clean slate and SET_CLUSTER re-assigns
        // the fresh ship id. The side-only elimination (P455b) never sends
        // MISSION_STAGE Over, so it never triggers this reset (the mission is
        // still running for the other sides). Guarded on the prior stage so it
        // fires once, not on every identical Over broadcast.
        int? previousStage = _mission?.Stage;
        bool enteringOver = stage.Stage == MissionStageMessage.StageOver
                            && _mission?.Stage != MissionStageMessage.StageOver;

        // Refine just the stage on whatever mission we know about, or mint the
        // first snapshot if a MISSION_STAGE arrived before any MISSIONDEF (then
        // the core fields stay their defaults until a MISSIONDEF lands).
        _mission = new MissionInfo
        {
            StaticCoreFile = _mission?.StaticCoreFile ?? "",
            StaticCoreVersion = _mission?.StaticCoreVersion ?? 0,
            Cookie = _mission?.Cookie ?? 0,
            GameLengthSeconds = _mission?.GameLengthSeconds ?? 0,
            ILives = _mission?.ILives ?? Allegiance.Core.Wire.MissionDef.UnlimitedLives,
            SideMissionOwner = _mission?.SideMissionOwner ?? 0,
            Stage = stage.Stage,
        };

        if (enteringOver)
            BeginNextMission();
        // Native gameoverscreen.cpp:638-645 advances the game-over screen on Starting
        // to the new mission. Keep scoreboard during NotStarted intermission.
        if (stage.Stage is MissionStageMessage.StageStarting or MissionStageMessage.StageStarted)
            _waitingForRestart = false;
        if (stage.Stage == MissionStageMessage.StageStarting)
        {
            CountdownVersion++;
            ClearGameOver();
        }
        if (stage.Stage == MissionStageMessage.StageStarted)
        {
            if (previousStage != MissionStageMessage.StageStarted) ClearGameOver();
            _countdownTargetMs = null;
        }
    }

    /// <summary>
    /// P481 — reset every piece of client state that "assumed the previous
    /// game was running", so a rotated (P480) world can repopulate cleanly. The
    /// full game-over + rotation re-sends the whole new world (P480 re-burst:
    /// new clusters/stations/EXPORTs and a FRESH ship id), so the old world's
    /// entities and our stale <see cref="MyShipId"/> must be discarded first,
    /// exactly as native's <c>JOINED_MISSION</c> → <c>ResetStaticData</c> flushes
    /// the client game state on the native side (clintlib.cpp:4193-4224). This
    /// covers the entities, the scoreboard/kill-feed rosters, the per-side ready /
    /// force-ready / leader / dev-tech / money maps, the transient systems
    /// (ripcord, missile lock, briefing) and the derived snapshots — everything
    /// <see cref="Reset"/> clears for a fresh connection, EXCEPT the game-over
    /// panel (<see cref="GameOverResult"/>, which must stay up until the Back
    /// action) and <see cref="MySideId"/> (needed to title the panel; also
    /// re-established by the re-burst on the same side). Caller holds the
    /// session lock, like the receive thread that invokes this via
    /// <see cref="Apply(MissionStageMessage)"/>.
    /// </summary>
    public long MissionGeneration { get; private set; }

    public void BeginNextMission()
    {
        if (_botWorld is not null) _botWorlds!.Retire(_botWorld);
        DetachBotWorld();
        OwnMissilesFired = 0;
        SectorGeneration++;
        MissionGeneration++;
        ShipWarnings.Reset();
        MyShipId = null;
        MyAcceptedCommand = null;
        MyCurrentCommand = null;
        _myQueuedCommand = null;
        _moneyRequest = null;
        JoinedShipId = null;
        JoinedMissionCookie = null;
        JoinError = null;
        MissionCore = null;
        Graph = null;
        CountdownStartLocalMs = null;
        _countdownTargetMs = null;
        _missionTimeStartMs = null;
        _scoreRoster = [];
        _briefingText = "";
        _briefingFGenerateCivBriefing = false;
        _missionBriefingInfo = null;
        _missileLock = MissileLockState.None;
        _prevLockState = MissileLockFeedback.LockState.None;
        _prevLockTargetId = -1;
        _missileTrack = MissileTrackState.Clear;
        _leadIndicator = LeadIndicatorState.None;
        _miningAsteroidId = -1;
        _killBonusPercent = 0f;
        _killBonusStreak = 0;
        _myCloaking = null;              // HUD-12
        _remoteRipcords.Clear();         // HUD-12
        ClearRipcord();

        _ships.Clear();
        _clusters.Clear();
        _stations.Clear();
        _warps.Clear();
        _warpBombs.Clear();   // P758 — no live warp bombs survive a reset
        _asteroids.Clear();
        _asteroidLive.Clear();
        _stationLive.Clear();
        _projectiles.Clear();
        _ownProjectileCount = 0;
        _projectileDrainCursor = 0;
        _missiles.Clear();
        _lastMissileFiredByShip.Clear();
        MarkSnapshotDirty();
        Diag.Clear();
        _probes.Clear();
        _mines.Clear();
        _buildingEffects.Clear();
        _treasures.Clear();
        _treasureClockSeeded = false;
        _chaff.Clear();
        _explosions.Clear();
        _explosionSoundEvents.Clear();
        _hullHits.Clear();
        _teamReadyBySideId.Clear();
        _forceReadyBySideId.Clear();
        _sideActiveBySideId.Clear();
        AllowEmptyTeams = null;
        _leaderShipIdBySideId.Clear();
        _developmentTechsBySideId.Clear();
        _globalAttributesBySideId.Clear();
        _bucketFundingBySideId.Clear();
        ResearchBriefing = null;
        _lastBases = new();
        _completedDevelopments.Clear();
        _researchProduction.Clear();
        _retiredResearchProduction.Clear();
        _initialTechsBySideId.Clear();
        _moneyByShipId.Clear();
        _autoDonateTargetByShipId.Clear();
        _feedbackLines.Clear();
        _cameraOverrideRequests.Clear();
        _soundEvents.Clear();
        _newContactCues.Clear();
        _highlightedClusters.Clear();
        Array.Fill(_allianceGroups, (sbyte)-1);
        ViewedClusterId = null;
        TelemetrySectorOverride = null;
        ViewedClusterPosition = null;
        ViewClusterVersion = 0;
        _clusterWarningBySectorId.Clear();
        _clusterWarningCues.Clear();
        _events.Clear();
        _roster.Clear();
        ResetLobby();
        ResetInterfaceState();
    }

    /// <summary>Native SET_START_TIME can be reset for the briefing after the
    /// restart hold (fsmission.cpp:1931-1938, 3539-3546). Use its absolute deadline.</summary>
    private void Apply(SetStartTime startTime)
    {
        _countdownTargetMs = startTime.TimeStart;
        _missionTimeStartMs = startTime.TimeStart;
        CountdownStartLocalMs = (long)(NowSeconds() * 1000);
    }

    /// <summary>FM_S_SET_BRIEFING_TEXT(9) — the server's per-player mission
    /// briefing, sent once in the join-sync burst (P121, Server.cs:2011-2051).
    /// In this port's always-generate-civ-briefing mapping <c>Text</c> is the
    /// joining side's starting cluster name; the client renders it as the
    /// dismissible mission-briefing popup during the pre-launch countdown. It
    /// concerns only the joining player's own connection, so it applies to the
    /// local mission unconditionally (there is only ever one briefing).</summary>
    private void Apply(SetBriefingText briefing)
    {
        _briefingText = briefing.Text;
        _briefingFGenerateCivBriefing = briefing.FGenerateCivBriefing;
    }

    /// <summary>P759 — store the civ-briefing inputs the server sends beside
    /// SET_BRIEFING_TEXT. The message is per-joiner join-sync, so it applies to
    /// the local mission unconditionally (there is only ever one briefing).</summary>
    private void Apply(MissionBriefingInfo info) => _missionBriefingInfo = info;

    /// <summary>FM_S_GAME_OVER(62) — the server's real end-of-mission broadcast
    /// (Server.DeclareGameOver → GAME_OVER to every peer still on the mission).
    /// P481 — this handler retains the full snapshot for the on-screen result
    /// panel but does <b>not</b> flip <see cref="MissionInfo.Stage"/> to Over.
    /// The stage is driven solely by the server's real <c>MISSION_STAGE(37)</c>
    /// broadcast (<c>SetStage</c>, fsmission.cpp:1750-1768): a full game-over
    /// sends <c>MISSION_STAGE Over</c> before the P439/P480 rotation, while the
    /// P455b side-only game-over deliberately does not flip the stage — so
    /// GAME_OVER alone cannot (and must not) pretend the mission ended. This is
    /// what lets the game-over Back button mirror native
    /// <c>GameOverScreen::OnButtonBack</c> (gameoverscreen.cpp:609-625): stage
    /// Over → leave, otherwise (side-only elimination keeps the stage Started,
    /// rotation moves it to NotStarted) → the team lobby.</summary>
    private void Apply(GameOver gameOver)
    {
        _lastBases = new();
        _waitingForRestart = true;
        InGame = false;
        // P262 — retain the full snapshot (reason + winner + per-side records)
        // for the on-screen result panel. The stage is NOT touched here; see
        // the class remark above (P481).
        _gameOver = gameOver;
        // A GAME_OVER begins a new result; following native 50-player chunks
        // update the same roster (clintlib/appmsg.cpp:3048-3111).
        _gameOverPlayers = [];
    }

    /// <summary>FM_S_GAME_OVER_PLAYERS(63) — the per-player endgame roster the
    /// server sends alongside GAME_OVER (P583): each connected player's score
    /// and counter breakdown (kills/deaths/ejections/rescues/baseKills/
    /// baseCaptures/artifacts/flags). Accumulate native's 50-player chunks
    /// until the next GAME_OVER; the game-over screen reads <see cref="GameOverPlayersList"/>.</summary>
    private void Apply(GameOverPlayers players)
    {
        _gameOverPlayers = [.. _gameOverPlayers, .. players.Entries];
    }

    /// <summary>P481 — dismiss the game-over result panel (the Back action's
    /// "clear GameOverResult and show the team lobby"). Idempotent; leaves all
    /// other world state untouched (the caller also moves the client state
    /// machine to the lobby overlay).</summary>
    public void ClearGameOver() => _gameOver = null;

    /// <summary>P281 — the SIDE_ROSTER(211) snapshot; replace-on-arrival (a late
    /// roster simply supersedes the earlier one — sides don't change identity,
    /// only their player counts).</summary>
    private void Apply(SideRoster roster)
    {
        SideRoster = roster;
        foreach (var side in roster.Sides)
            _sideCivilizations[side.SideId] = new(side.CivId,
                _sideCivilizations.TryGetValue(side.SideId, out var previous) && previous.Random);
    }

    /// <summary>P282 — the whole-mission <c>TEAM_READY(46)</c> broadcast:
    /// replace-on-arrival for that side's aggregate ready flag (last write
    /// wins). Mirrors the <see cref="TeamRoster"/> current-state semantics for
    /// the per-side aggregate level.</summary>
    private void Apply(TeamReady ready) => _teamReadyBySideId[ready.SideId] = ready.FReady;

    /// <summary>P282 — the whole-mission forward of <c>FORCE_TEAM_READY(47)</c>
    /// (the server relays it to every mission peer, incl. the sender): records
    /// that side's persistent force-ready flag.</summary>
    private void Apply(ForceTeamReady force) => _forceReadyBySideId[force.SideId] = force.FForceReady;

    /// <summary>P455b — <c>FM_CS_SIDE_INACTIVE(68)</c>: record the side's active
    /// state (<c>MissionInfo.rgfActive</c>), and update the mission-wide
    /// <c>AllowEmptyTeams</c> bit when the message carries
    /// <c>bChangeAET=true</c> (native appmsg.cpp:2580-2591 → SetSideActive +
    /// optional SetAllowEmptyTeams). The P455 mid-game elimination broadcast
    /// carries <c>bChangeAET=false</c>, so this only ever flips AET on the P202
    /// pre-game roster path.</summary>
    private void Apply(SideInactive m)
    {
        _sideActiveBySideId[m.SideId] = m.BActive;
        if (!m.BActive) _lastBases = new()
        {
            FinaleSecondsRemaining = _lastBases.FinaleSecondsRemaining,
            Targets = _lastBases.Targets.Where(t => t.SideId != m.SideId).ToArray(),
        };
        if (m.BChangeAet)
            AllowEmptyTeams = m.BAet;
    }

    /// <summary>P284 — <c>FM_S_CREATE_BUCKETS(191)</c>: the join-sync preface
    /// that carries the joining side's real development-tech mask
    /// (<c>TtbmDevelopments</c>). It has no SideId and is sent only to the
    /// joining peer right after it is seated, so it applies to the local
    /// player's own side (<see cref="MySideId"/>). This is the client's initial
    /// seed of the mask the docked-shop buy gate reads; later growth arrives on
    /// <see cref="Apply(SideTechChange)"/>.</summary>
    private void Apply(CreateBuckets buckets)
    {
        if (MySideId is not { } side || buckets.TtbmDevelopments is not { Length: TechTreeBitMask.ByteCount } mask) return;
        _developmentTechsBySideId[side] = TechTreeBitMask.FromBytes(mask);
        if (buckets.TtbmInitial is { Length: TechTreeBitMask.ByteCount } initial)
            _initialTechsBySideId[side] = TechTreeBitMask.FromBytes(initial);
    }

    /// <summary>P487 — <c>FM_S_BUCKET_STATUS(65)</c>: the server's per-bucket
    /// funding snapshot (invested money + invested build-time ms) for one side's
    /// construction bucket. Sent once per bucket during join sync and re-broadcast
    /// to the whole side after every donation. Replaces that bucket's funding
    /// wholesale — the live progress source for the investment pane. Note this is
    /// <em>not</em> a wallet mutation: the pane's Invest never locally debits
    /// <see cref="MyMoney"/>; the server's MONEY_CHANGE(60) is the authoritative
    /// debit (P442), so there is no double-spend here.</summary>
    private void Apply(BucketStatus status)
    {
        if (!_bucketFundingBySideId.TryGetValue(status.SideId, out var side))
            _bucketFundingBySideId[status.SideId] = side = new Dictionary<short, BucketFunding>();
        side[status.IBucket] = new BucketFunding(status.MoneyTotal, status.TimeTotal);
    }

    /// <summary>P284 — <c>FM_S_SIDE_TECH_CHANGE(75)</c>: a development completed,
    /// so the server broadcasts the side's post-OR development-tech mask
    /// (<c>TtbmDevelopments</c>, <see cref="SideTechChange.SideId"/>). Replaces
    /// that side's mask wholesale — the live source that flips a previously-hidden
    /// hull/weapon to buyable in the docked shop once its governing development
    /// is researched.</summary>
    private void Apply(SideTechChange change)
    {
        if (change.TtbmDevelopments is not { Length: TechTreeBitMask.ByteCount } mask) return;
        _developmentTechsBySideId[change.SideId] = TechTreeBitMask.FromBytes(mask);
    }

    /// <summary>P446 — <c>FM_S_SIDE_ATTRIBUTE_CHANGE(76)</c>: a completed
    /// development applied an attribute delta, so the server broadcasts the
    /// side's post-<c>Apply</c> 25-factor multiplier set (<c>GasAttributes</c>,
    /// <see cref="SideAttributeChange.SideId"/>), whole-mission. Replaces that
    /// side's set wholesale — the live source that scales the client's
    /// autopilot braking estimate (P446) and will feed the client-side
    /// prediction tasks P447/P448.</summary>
    private void Apply(SideAttributeChange change)
    {
        if (change.GasAttributes is not { Length: GlobalAttributeSet.Count } gas) return;
        _globalAttributesBySideId[change.SideId] = GlobalAttributeSet.FromArray(gas);
    }

    /// <summary>P366 — <c>SET_TEAM_LEADER(121)</c>: the whole-mission, authoritative
    /// leadership announcement (the server re-broadcasts it on every leadership
    /// assignment/transfer, Server.Lobby.cs BroadcastSetTeamLeader). Upsert this
    /// side's leader ship id — last announcement wins (current state, not history)
    /// — mirroring the server's <c>Side.LeaderShipId</c>; <see cref=\"IsTeamLeader\"/>
    /// reads it to gate the leader-only bot dock/launch UI.</summary>
    private void Apply(SetTeamLeader stl)
    {
        _leaderShipIdBySideId[stl.SideId] = stl.ShipId;

        // P714 — native commanderSound (appmsg.cpp:2965): when the new leader
        // is OUR OWN ship we became the team leader and native plays the
        // commander VO (FM_S_SET_TEAM_LEADER → pplayer == MyPlayerInfo).
        if (MyShipId is { } me && stl.ShipId == me)
            RecordSoundEvent(ClientSoundEventKind.CommanderElected, stl.ShipId);
    }

    /// <summary>
    /// P438 — <c>FM_S_JOIN_SIDE(125)</c>: the whole-mission, authoritative
    /// announcement that a ship joined / switched to a side (the server
    /// broadcasts it on every <c>TryJoinSide</c>, Server.Lobby.cs
    /// BroadcastJoinSide). Mirrors the ship-side half of native's
    /// <c>AddPlayerToSide</c> (clintlib.cpp:4101-4125): update the affected
    /// ship's <c>ShipEntity.SideId</c>, and — when the ship is the local
    /// player's own — <c>MySideId</c> too. Both feed the friend/enemy
    /// classification (<c>Game1.Camera</c> isEnemy), so without this the P376
    /// target bracket / P372 radar / P373 first-sighting all classify by a
    /// stale side after a side switch.
    /// <para>
    /// <b>Unknown-ship decision (ignore, not buffer):</b> a <c>JOIN_SIDE</c>
    /// for a ship the client has no entity for yet is ignored. It carries no
    /// hull/identity (only <c>SideId</c>/<c>ShipId</c>), so creating a phantom
    /// entity would fabricate an identity-less ship that violates P373's
    /// <c>HasRealIdentity</c> invariant (a not-yet-identified ship must not be
    /// announced or classified). The ship's EXPORT — the authoritative source
    /// of <c>SideId</c> + hull — arrives on (re)contact and stamps the real
    /// side anyway, so buffering buys nothing. This handler never flips
    /// <c>HasRealIdentity</c>: <c>JOIN_SIDE</c> alone does not establish
    /// identity.
    /// </para>
    /// <para>
    /// <b>SIDE_TEAMLOBBY:</b> the lobby side is <c>Side.ArtifactSentinelFlag</c>
    /// (= -2, igc.h:514). If a <c>JOIN_SIDE</c> ever carries it, storing it on
    /// the entity is harmless: <c>isEnemy</c> compares <c>SideId == MySideId</c>,
    /// and <c>MySideId</c> is always a real faction side, so -2 can never equal
    /// it — a ship parked in the team lobby just resolves as "not on my side"
    /// (rendered as a non-friend), never aliased to a real side.
    /// </para>
    /// </summary>
    private void Apply(JoinSide join)
    {
        // clintlib/clintlib.cpp:4133-4135: joining satisfies this side's request.
        _positionRequests.Remove(new(join.SideId, join.ShipId));
        if (join.ShipId == (JoinedShipId ?? MyShipId)) MyPositionRequestResult = null;
        // Known ship: update its side. Unknown ship: do not mint a phantom
        // entity (see the doc comment) — its EXPORT will carry the side.
        if (_ships.TryGetValue(join.ShipId, out var e))
        {
            e.SideId = join.SideId;
            if (e.Player is { } player) player.ISide = join.SideId;
        }

        // The local player's own side, refreshed when the own ship is named —
        // MySideId is otherwise only set from the one-shot own-ship dummy EXPORT
        // (so it goes stale after the local player's own side switch too).
        if (join.ShipId == (JoinedShipId ?? MyShipId))
            MySideId = join.SideId;

        // P736 — SIM-0464/0465: the presence salutation when a HUMAN player
        // joins a side (native BaseClient::AddPlayerToSide,
        // clintlib.cpp:4140-4172). Same gates as native: a human player, the
        // mission started, connected (always true here), and not our own ship.
        // A same-side joiner voices salRecruitsArrivedSound (:4155); an
        // enemy-side (non-allied) joiner voices salEnemyJoinersSound (:4162),
        // gated additionally on us holding a real side. An allied-but-different
        // side join is text-only in native (sal id 0, :4151) — no sound, so
        // none is recorded here.
        if (join.ShipId != (MyShipId ?? JoinedShipId)
            && _mission?.Stage == MissionStageMessage.StageStarted
            && _ships.TryGetValue(join.ShipId, out var jp)
            && jp.Player is { PilotType: PlayerInfo.PilotTypePlayer }
            && MySideId is { } jMy)
        {
            if (join.SideId == jMy)
                RecordSoundEvent(ClientSoundEventKind.SalRecruitsArrived, join.ShipId);
            else if (!AreAllied(jMy, join.SideId) && jMy != Side.ArtifactSentinelFlag)
                RecordSoundEvent(ClientSoundEventKind.SalEnemyJoiners, join.ShipId);
        }
    }

    /// <summary>
    /// P736 — SIM-0461/0462/0463/0468: the shared presence-salutation decision
    /// for a player leaving a side (native <c>BaseClient::RemovePlayerFromSide</c>,
    /// clintlib.cpp:3867-4009). Mirrors native's gates and branches:
    /// <list type="bullet">
    /// <item><b>Gates</b>: a human player, the mission started, connected
    /// (always true here), and not our own ship (:3924-3926).</item>
    /// <item><b>Same-side leaver</b> (the leaver's side == our side, :3928): the
    /// reason picks the cue — LinkDead → <c>salMIASound</c> (:3999, SIM-0463),
    /// SwitchingSides → <c>salChangeSidesSound</c> (:3975, SIM-0461), any other
    /// same-side quit reason → <c>salQuitSound</c> (SIM-0462).</item>
    /// <item><b>Other-side leaver</b> (:4004-4009): allied → <c>salQuitSound</c>,
    /// enemy (non-allied) → <c>salEnemyLeavesSound</c> (SIM-0468 — the dynamic
    /// leave voice).</item>
    /// </list>
    /// P722 also broadcasts authorized boot and ordinary quit reasons. The
    /// cue is recorded before P718 cleanup changes the old side or removes
    /// the player, so a subsequent departure cannot voice it again.
    /// </summary>
    private void ApplySideLeave(short shipId, QuitSideReason reason)
    {
        if (_mission?.Stage != MissionStageMessage.StageStarted) return;
        if (shipId == (MyShipId ?? JoinedShipId)) return;              // our own leave — OnQuitSide, no VO
        if (!_ships.TryGetValue(shipId, out var e)) return;            // unknown ship: nothing to classify
        if (e.Player is not { PilotType: PlayerInfo.PilotTypePlayer }) return; // not human (:3924)
        if (MySideId is not { } mySide) return;

        short sideOld = e.SideId;   // the side the leaver was on before leaving

        if (sideOld == mySide)
        {
            switch (reason)
            {
                case QuitSideReason.LinkDead:
                    RecordSoundEvent(ClientSoundEventKind.SalMia, shipId);        // SIM-0463
                    break;
                case QuitSideReason.SwitchingSides:
                    RecordSoundEvent(ClientSoundEventKind.SalChangeSides, shipId);// SIM-0461
                    break;
                default:
                    RecordSoundEvent(ClientSoundEventKind.SalQuit, shipId);       // SIM-0462
                    break;
            }
        }
        else
        {
            // Other-side leaver: allied → salQuitSound, enemy → salEnemyLeavesSound
            // (clintlib.cpp:4006-4009 — SIM-0468).
            bool allied = AreAllied(mySide, sideOld);
            RecordSoundEvent(allied ? ClientSoundEventKind.SalQuit : ClientSoundEventKind.SalEnemyLeaves, shipId);
        }
    }

    /// <summary>
    /// P374 — <c>FM_S_CLUSTER_WARNING(214)</c>: a side's per-sector threat state
    /// changed (warning level and/or a sector-secured/lost station transition).
    /// Mirrors native's client-side <c>ClusterSiteImpl::UpdateClusterWarnings</c>
    /// (trekigc.cpp:327-383):
    /// <list type="bullet">
    /// <item>The current <see cref="ClusterWarnings"/> level is stored per sector
    /// (<see cref="ClusterWarningFor"/>), so the starmap's flashing icons and
    /// hover text track the live threat.</item>
    /// <item>The INCREASE edge (<c>cwNew &gt; cwOld &amp;&amp; cwNew &gt; NoThreat</c>)
    /// fires the alert sound cue and posts the "<c>{description} in {sector}</c>"
    /// status line (trekigc.cpp:349-350, <c>PostText(cwNew &gt; cwOld, ...)</c>).</item>
    /// <item>A <c>Secured</c> transition posts "<c>{sector} secured.</c>" + the
    /// secured cue; a <c>Lost</c> transition posts "<c>{sector} lost.</c>" + the
    /// lost cue (trekigc.cpp:375-381).</item>
    /// </list>
    /// Status lines are bare system lines (sender NA), the same shape P230's
    /// lock-tip posting uses, so they render in the chat panel with no prefix.
    /// </summary>
    private void Apply(ClusterWarningUpdate m)
    {
        short prev = _clusterWarningBySectorId.TryGetValue(m.SectorId, out var w) ? w : ClusterWarnings.NoThreat;
        _clusterWarningBySectorId[m.SectorId] = m.Warning;

        string name = _clusters.TryGetValue(m.SectorId, out var dc) ? dc.Name : $"sector {m.SectorId}";
        bool alert = m.Warning > prev && m.Warning > ClusterWarnings.NoThreat;
        bool fire = alert || m.Secured || m.Lost;
        if (!fire) return;   // a decrease/steady level: icon updates above, no sound/text

        if (_clusterWarningCues.Count < MaxClusterWarningCues)
            _clusterWarningCues.Add(new ClientClusterWarningCue(m.SectorId, m.Warning, m.Secured, m.Lost));

        if (alert)
        {
            string desc = ClusterWarnings.GetWarningText(m.Warning);
            if (desc.Length > 0)
                RecordSystemLine($"{desc} in {name}");
        }
        if (m.Secured)
            RecordSystemLine($"{name} secured.");
        else if (m.Lost)
            RecordSystemLine($"{name} lost.");
    }

    /// <summary>Post a bare system/console chat line (sender NA so the renderer
    /// shows no player prefix) — the P230 lock-tip posting shape.</summary>
    private void RecordSystemLine(string text)
    {
        _events.Record(new ChatMessage
        {
            Message = text,
            SidSender = -1,
            ChatTarget = (int)Allegiance.Core.Sim.ChatTarget.Everyone,
        });
    }

    /// <summary>P281 — the SET_MISSION_OWNER(123) broadcast; replace-on-arrival.
    /// Whether it makes US the owner is derived (see <see cref="AmMissionOwner"/>),
    /// since our own ShipId may not be known the moment this arrives.</summary>
    private void Apply(SetMissionOwner owner) => MissionOwnerBroadcast = owner;

    /// <summary>FM_S_MISSILE_LOCK_UPDATE(204) — the owner-only P230 lock snapshot
    /// (server-computed, P226). Updates <see cref="MissileLock"/> and edge-detects
    /// the three native console tips (partial / acquired / lost, <c>console.cpp:929-946</c>),
    /// posting any that fire into the chat log as bare system lines (sender NA so
    /// the renderer shows the tip with no player prefix). Exactly one tip posts on
    /// each crossing; holding a state posts nothing (never spammed per frame).</summary>
    private void Apply(MissileLockUpdate m)
    {
        _missileLock = new MissileLockState(
            m.LockFraction, m.TargetShipId, m.TargetPosition, m.MissileTypeName, m.TargetType, m.IsReady);

        var curr = MissileLockFeedback.Classify(m.LockFraction);
        bool targetChanged = m.TargetShipId != _prevLockTargetId || m.TargetType != _prevLockTargetType;
        string? tip = MissileLockFeedback.TipFor(_prevLockState, curr, targetChanged, m.MissileTypeName);
        _prevLockState = curr;
        _prevLockTargetId = m.TargetShipId;
        _prevLockTargetType = m.TargetType;

        if (tip is not null)
        {
            // Sender NA(-1): ChatLogFormat renders a ChatMessage with an unknown
            // sender as a bare line (BuildChatLines leaves the name empty for
            // SidSender < 0), so the tip reads as a system/console message.
            _events.Record(new ChatMessage
            {
                Message = tip,
                SidSender = -1,
                ChatTarget = (int)Allegiance.Core.Sim.ChatTarget.Everyone,
            });
        }
    }

    /// <summary>FM_S_OWN_SHIP_MINING_UPDATE(206) — the owner-only P255 snapshot of
    /// the rock the local ship is currently mining. Overwrites
    /// <see cref="MiningAsteroidId"/> wholesale each tick (NA = not mining); the
    /// caller feeds <see cref="Game1"/> which resolves the id against
    /// <see cref="Asteroids"/> to orient the own hull toward the mine target.</summary>
    private void Apply(OwnShipMiningUpdate m) => _miningAsteroidId = m.AsteroidId;

    /// <summary>FM_S_OWN_SHIP_KILL_BONUS_UPDATE(212) — the owner-only P298
    /// snapshot of the local ship's live Kill Bonus. Overwrites
    /// <see cref="KillBonusPercent"/>/<see cref="KillBonusStreak"/> wholesale
    /// each tick (the server always sends the current value, even 0).</summary>
    private void Apply(OwnShipKillBonusUpdate m)
    {
        _killBonusPercent = m.KillBonusPercent;
        _killBonusStreak = m.Streak;
    }

    /// <summary>HUD-12 — <c>OWN_SHIP_CLOAK_UPDATE(220)</c>: the owner-only
    /// continuous <c>GetCloaking()</c> visible fraction (the port's answer to
    /// native's locally-simulated <c>CcloakIGC</c> ramp, <c>cloakigc.cpp:85-122</c>).
    /// Overwrites <see cref="MyCloaking"/> wholesale each tick.</summary>
    private void Apply(OwnShipCloakUpdate m) => _myCloaking = m.Cloaking;

    /// <summary>FM_S_MISSILE_TRACK_WARNING(208) — the owner-only P264 snapshot of
    /// "an enemy missile's target is the local ship". Overwrites
    /// <see cref="MissileTrack"/> wholesale each tick (not-tracked = clear); the
    /// caller (Game1) edge-detects arm/disarm to drive the warning loop.</summary>
    private void Apply(MissileTrackWarningUpdate m) =>
        _missileTrack = new MissileTrackState(m.Tracked, m.BestLock);

    /// <summary>FM_S_LEAD_INDICATOR_UPDATE(210) — the owner-only P266 snapshot of
    /// the local ship's predictive lead-aim point (server-computed from the real
    /// solveForLead physics). Overwrites <see cref="LeadIndicator"/> wholesale each
    /// tick; a <c>HasLead=false</c> (NA target) message clears the reticle. The
    /// server always sends, so a stale lead never lingers when the target dies,
    /// leaves sector, or the hull/relay gate stops granting it.</summary>
    private void Apply(LeadIndicatorUpdate m)
    {
        _leadIndicator = m.HasLead
            ? new LeadIndicatorState(m.HasLead, m.InRange, m.Green, m.LeadWorldPos, m.TargetPos)
            : LeadIndicatorState.None;
    }

    private ShipEntity Upsert(short shipId)
    {
        if (_ships.TryGetValue(shipId, out var existing)) return existing;
        MarkSnapshotDirty();
        var created = new ShipEntity { ShipId = shipId };
        _ships.Add(shipId, created);
        return created;
    }

    /// <summary>
    /// P373 — the client-side "new contact" (first-sighting) edge. Queues a
    /// one-shot <see cref="ClientNewContactCue"/> exactly once per genuine
    /// not-visible→visible transition for a ship, mirroring native
    /// <c>UpdateSideVisibility</c>'s one-shot fire when a ship's
    /// <c>m_sideVisibility.fVisible()</c> goes false→true (trekigc.cpp:2080-2094).
    /// Called from every Apply that re-establishes a ship's presence
    /// (ExportShip, HeavyShipsUpdate) with the <see cref="ShipEntity.IsGone"/>
    /// already cleared; the <see cref="ShipEntity.NewContactSounded"/> latch
    /// (reset on departure) ensures a continuously-visible ship's routine pose
    /// refreshes never re-fire, while a brand-new or just-returned ship cues.
    /// The enemy/friendly split reads the SAME source P372's visual side colour
    /// uses — <see cref="ShipEntity.SideId"/> vs <see cref="MySideId"/> (the
    /// port has no alliance concept, so "allied" = own side only). The local
    /// player's own ship and dead ships never announce.
    /// </summary>
    private void AnnounceNewContactIfNew(ShipEntity e)
    {
        if (e.IsMine || e.IsDead) return;        // not another ship / not a corpse
        if (e.NewContactSounded) return;         // already announced this episode
        if (MySideId is not { } mySide) return;  // can't classify enemy/friendly yet
        e.NewContactSounded = true;
        bool enemy = e.SideId != mySide;
        if (_newContactCues.Count < MaxNewContactCues)
            _newContactCues.Add(new ClientNewContactCue(e.ShipId, enemy));
    }

    // ── P585 — aleph transit signals (the opt-in transit-pulse source) ──────

    /// <summary>
    /// P585 — record that a ship crossed the aleph <paramref name="warpId"/>
    /// (client-side wire-signal detection; the renderer drains these each frame
    /// via <see cref="DrainAlephTransits"/> to drive the opt-in transit pulse).
    /// <paramref name="time"/> is the monotonic clock (seconds) at detection.
    /// </summary>
    public void SignalAlephTransit(short warpId, short shipId, double time)
        => _alephTransits.Add(new AlephTransitSignal(warpId, shipId, time));

    /// <summary>P585 — drain (and clear) the pending aleph transit signals.</summary>
    public IReadOnlyList<AlephTransitSignal> DrainAlephTransits()
    {
        if (_alephTransits.Count == 0) return Array.Empty<AlephTransitSignal>();
        var copy = new List<AlephTransitSignal>(_alephTransits);
        _alephTransits.Clear();
        return copy;
    }

    /// <summary>
    /// P585 — the client-side transit-attribution radius for a warp: native
    /// fires the transit at <c>warp.Radius + shipRadius</c>
    /// (Sector.cs:3249-3251, shipIGC.cpp:629-636); pad past the raw Radius for
    /// the last-telemetry-pose lag so a ship whose final update precedes the
    /// exact crossing still attributes to the aleph it went through.
    /// </summary>
    private static float TransitAttributionRadius(DataWarpIGC w)
        => Math.Max(w.Radius * 2f, 90f);

    /// <summary>
    /// P599 — minimum cosine of the angle between an arriving ship's velocity
    /// and the straight-out-from-the-aleph direction for it to count as a warp
    /// ARRIVAL rather than a ship merely passing near the aleph. Native places
    /// an arriving ship just BEHIND the destination aleph and launches it
    /// straight outward along the warp axis — position = destination
    /// <c>+ up·rand(2..5) + right·rand(2..5) − (shipRadius+5)·backward</c>,
    /// velocity = <c>backward·−(speed + exitWarpSpeed)</c>, i.e. net motion
    /// along <c>forward</c> (trekigc.cpp:4407-4413) — so a fresh post-emergence
    /// velocity is essentially PARALLEL to the radial-outward direction (dot ≈
    /// 1; only the tiny ±5u lateral jitter perturbs it). 0.5 (cos 60°) is a
    /// deliberately generous cone that still rejects a ship crossing the sector
    /// tangentially or flying IN toward the aleph (an imminent departure, cued
    /// by SDR_LEFTSECTOR, not an arrival).
    /// </summary>
    private const float ArrivalOutwardMinDot = 0.5f;

    /// <summary>
    /// P585 — nearest aleph to <paramref name="pos"/> in <paramref name="sectorId"/>
    /// that is within ITS OWN transit-attribution radius, or null (a ship that
    /// leaves/arrives not through a warp never fakes a pulse).
    /// </summary>
    private DataWarpIGC? NearestWarpInSector(short sectorId, Vector3 pos)
    {
        DataWarpIGC? best = default; float bestDsq = float.MaxValue;
        foreach (var (_, w) in _warps)
        {
            if (w.ClusterId != sectorId) continue;
            float max = TransitAttributionRadius(w);
            float dx = w.Position[0] - pos.X, dy = w.Position[1] - pos.Y, dz = w.Position[2] - pos.Z;
            float dsq = dx * dx + dy * dy + dz * dz;
            if (dsq > max * max) continue;
            if (dsq < bestDsq) { bestDsq = dsq; best = w; }
        }
        return bestDsq == float.MaxValue ? null : best;
    }

    /// <summary>P585 — a ship DEPARTED the viewer's sector via an aleph: signal
    /// the nearest warp to its last known position (the sector it left = the
    /// viewer's own sector, since SDR_LEFTSECTOR is only addressed to peers
    /// flying in it).</summary>
    private void MaybeSignalDeparture(ShipEntity e)
    {
        if (e.Latest?.Position is not Vector3 p) return;
        if (MyShipId is not { } me || !_ships.TryGetValue(me, out var own)) return;
        var warp = NearestWarpInSector(own.SectorId, p);
        if (warp is not null)
            SignalAlephTransit(warp.Value.WarpId, e.ShipId, NowSeconds());
    }

    /// <summary>P585/P599 — a ship ARRIVED into the viewer's sector near an aleph
    /// (first or re-sighted, at <paramref name="pos"/> with
    /// <paramref name="velocity"/>): signal that arrival aleph, but ONLY when it
    /// lies just past the aleph AND is moving outward along the warp axis —
    /// i.e. it genuinely emerged from the aleph rather than passing near it.
    /// Skips the own ship (its arrival is signalled from SET_CLUSTER).</summary>
    private void MaybeSignalArrival(short sectorId, short shipId, Vector3? pos, Vector3 velocity, bool isMine)
    {
        if (isMine) return;
        if (pos is not Vector3 p) return;
        if (velocity.LengthSquared() <= 1e-9f) return;   // stationary: not an emergence
        var warp = NearestWarpInSector(sectorId, p);
        if (warp is null) return;

        // The outward gate: the ship must be moving AWAY from the aleph centre
        // along the warp axis (a transit exit), not drifting past it.
        Vector3 fromAleph = p - new Vector3(warp.Value.Position[0], warp.Value.Position[1], warp.Value.Position[2]);
        if (fromAleph.LengthSquared() <= 1e-9f) return;  // dead on centre: no direction
        if ((velocity.Normalize()) * (fromAleph.Normalize()) < ArrivalOutwardMinDot)
            return;                                       // not an arrival through this aleph

        SignalAlephTransit(warp.Value.WarpId, shipId, NowSeconds());
    }
}

/// <summary>
/// P585/P599 — a client-detected aleph transit: some ship crossed the aleph
/// <see cref="WarpId"/> in the viewer's current sector at monotonic clock
/// <see cref="Time"/> (seconds). Carries the triggering <see cref="ShipId"/> so
/// the renderer can log WHOSE transit pulsed (the ship's side vs the viewer's),
/// the objective evidence the live proof uses to show an ENEMY (not a
/// teammate) crossing. Recorded by <see cref="ClientWorld"/> from the wire
/// signals (SHIP_DELETE SDR_LEFTSECTOR for departures, SET_CLUSTER for the
/// own arrival, re-sighting for other arrivals) and drained each frame by the
/// renderer to drive the opt-in transit pulse.
/// </summary>
public readonly record struct AlephTransitSignal(short WarpId, short ShipId, double Time);
