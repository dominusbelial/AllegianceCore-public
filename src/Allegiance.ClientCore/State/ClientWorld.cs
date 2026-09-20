using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;

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
public sealed class ClientWorld
{
    private readonly Dictionary<short, ShipEntity> _ships = new();
    private readonly Dictionary<short, DataClusterIGC> _clusters = new();
    private readonly Dictionary<short, DataStationIGC> _stations = new();
    private readonly Dictionary<short, DataWarpIGC> _warps = new();
    private readonly Dictionary<short, DataAsteroidIGC> _asteroids = new();
    private readonly List<ProjectileEntity> _projectiles = new();
    private readonly Dictionary<short, MissileEntity> _missiles = new();
    private readonly Dictionary<short, ProbeEntity> _probes = new();
    private readonly Dictionary<short, MineEntity> _mines = new();
    private readonly List<ChaffEntity> _chaff = new();
    private readonly List<ExplosionEntity> _explosions = new();
    private readonly EventLog _events = new();
    private readonly TeamRoster _roster = new();
    // P282 — per-side aggregate ready (TEAM_READY(46)) and the per-side
    // force-ready flag (FORCE_TEAM_READY(47) echo), keyed by side id. Current
    // state, not history — same semantics as TeamRoster.ReadyByShipId.
    private readonly Dictionary<short, bool> _teamReadyBySideId = new();
    private readonly Dictionary<short, bool> _forceReadyBySideId = new();
    // P267 — transient one-shot sound events (station destroyed/captured, dev
    // completed) observed on the wire and drained by Game1 into the audio
    // manager each frame (see <see cref="ClientSoundEvent"/>). Bounded and
    // append-only; TakeSoundEvents() clears them after the drain.
    private readonly List<ClientSoundEvent> _soundEvents = new();
    private const int MaxSoundEvents = 64;
    // P265 — the latest in-mission scoreboard snapshot (name/side/kills/deaths
    // per real-side player) streamed by the periodic owner-only
    // ScoreRosterUpdate(209). Each receive replaces the whole snapshot.
    private ScoreRosterEntry[] _scoreRoster = [];
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

    // P271 — the mission-briefing body text the server sends on join
    // (FM_S_SET_BRIEFING_TEXT(9)). In this port's always-generate-civ-briefing
    // mapping (P121) it is the joining side's starting cluster name, plus the
    // fGenerateCivBriefing flag the server always sets true (there is no
    // mission-authored custom story-text field in this port). Empty until the
    // first SET_BRIEFING_TEXT arrives.
    private string _briefingText = "";
    private bool _briefingFGenerateCivBriefing;

    // P262 — the end-of-mission snapshot received on FM_S_GAME_OVER(62). Kept
    // separate from _mission (which only mirrors Stage=Over): this is the real
    // reason string, winning side id and per-side records the Game1 overlay
    // renders verbatim. Null until the first GAME_OVER arrives.
    private GameOver? _gameOver;

    // P230 — the local ship's missile-lock snapshot (from the owner-only
    // MISSILE_LOCK_UPDATE(204)) plus the previous-frame state used to edge-detect
    // the partial/full/lost console tips (native console.cpp:929-946).
    private MissileLockState _missileLock = MissileLockState.None;
    private MissileLockFeedback.LockState _prevLockState = MissileLockFeedback.LockState.None;
    private short _prevLockTargetId = -1;

    // P255 — the rock the LOCAL player's own ship is currently mining, streamed
    // by the owner-only OwnShipMiningUpdate(206). NA(-1) = not mining.
    private short _miningAsteroidId = -1;

    // P264 — whether an in-sector ENEMY missile's target is the local ship, plus
    // the best lock fraction among them, streamed by the owner-only
    // MissileTrackWarningUpdate(208). Drives the incoming-missile warning loop.
    private MissileTrackState _missileTrack = MissileTrackState.Clear;
    private LeadIndicatorState _leadIndicator = LeadIndicatorState.None;

    /// <summary>The local player's ship id, set on the first SET_CLUSTER(7)
    /// assignment; null until the server has told us which ship we are.</summary>
    public short? MyShipId { get; private set; }

    /// <summary>P260 — the local player's own side id, captured from the
    /// own-ship EXPORT(26) (<c>ShipId = NA(-1)</c> dummy) whose <c>SideId</c> the
    /// server seats at creation (Server.cs:~1781). The MonoGame client otherwise
    /// tracks no sides at all; this is the only wire source of the player's own
    /// team, used to gate the probe ambient loop to the player's own side.
    /// Null until that own-ship EXPORT arrives.</summary>
    public short? MySideId { get; private set; }

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

    /// <summary>P271 — the <c>fGenerateCivBriefing</c> flag from the last
    /// <c>SET_BRIEFING_TEXT</c>. The port's server always sends true (P121);
    /// preserved verbatim so the client could in principle distinguish an
    /// authored story text (false) from a generated location briefing (true).</summary>
    public bool BriefingFGenerateCivBriefing => _briefingFGenerateCivBriefing;

    /// <summary>Ships keyed by ship id, snapshot-consistent (never mutated
    /// while exposed — callers should hold the caller's lock).</summary>
    public IReadOnlyDictionary<short, ShipEntity> Ships => _ships;

    /// <summary>Clusters keyed by ClusterId, from EXPORT(26)/OT_cluster (13).</summary>
    public IReadOnlyDictionary<short, DataClusterIGC> Clusters => _clusters;

    /// <summary>Stations keyed by StationId, from EXPORT(26)/OT_station (1).</summary>
    public IReadOnlyDictionary<short, DataStationIGC> Stations => _stations;

    /// <summary>Warps keyed by WarpId, from EXPORT(26)/OT_warp (7).</summary>
    public IReadOnlyDictionary<short, DataWarpIGC> Warps => _warps;

    /// <summary>Asteroids keyed by AsteroidId, from EXPORT(26)/OT_asteroid (5).</summary>
    public IReadOnlyDictionary<short, DataAsteroidIGC> Asteroids => _asteroids;

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

    /// <summary>The cluster-connectivity graph from a single CLUSTERINFO(200);
    /// null until that message is seen (set once, not re-built on repeats).</summary>
    public ClusterGraph? Graph { get; private set; }

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

    /// <summary>P265 — the latest in-mission scoreboard snapshot (name / side /
    /// kills / deaths per real-side player), streamed by the periodic owner-only
    /// ScoreRosterUpdate(209). Replaced wholesale on each receive; empty until
    /// the first snapshot lands.</summary>
    public IReadOnlyList<ScoreRosterEntry> ScoreRoster => _scoreRoster;

    /// <summary>P284 — the live development-tech mask (<c>DevelopmentTechs</c>,
    /// native <c>GetDevelopmentTechs()</c>) per side id, from CREATE_BUCKETS(191)
    /// at join and SIDE_TECH_CHANGE(75) on research. The MonoGame docked-shop
    /// buy gate reads the local player's own side's mask here. Empty until the
    /// first CREATE_BUCKETS arrives; a side absent here reads as "tech unknown".</summary>
    public IReadOnlyDictionary<short, TechTreeBitMask> DevelopmentTechsBySideId => _developmentTechsBySideId;

    /// <summary>P284 — convenience read of <see cref="DevelopmentTechsBySideId"/>
    /// for one side; null when that side's mask has not yet been observed.</summary>
    public TechTreeBitMask? DevelopmentTechsFor(short sideId) =>
        _developmentTechsBySideId.TryGetValue(sideId, out var m) ? m : null;

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

    /// <summary>Local receipt instant (monotonic ms, <see cref="Environment.TickCount64"/>)
    /// of the first <c>SET_START_TIME(14)</c> countdown announcement, i.e. the
    /// moment the mission's visible start countdown began. <c>null</c> until such
    /// a message arrives. The wire's <c>TimeStart</c> is a server-clock millisecond
    /// value that shares no epoch with the client, so the port anchors the
    /// countdown to the moment it learned the countdown had begun (see
    /// <c>Game1</c>'s wiring), rather than trying to reconstruct absolute time.
    /// Set once; the countdown is a one-shot per mission in this port.</summary>
    public long? CountdownStartLocalMs { get; private set; }

    /// <summary>Upsert the world state from a decoded message. Switch on the
    /// concrete decoded type; every ship branch targets the same ship id, so
    /// any interleaving yields one entity per id. Entity-table entries for the
    /// non-ship objects are upserted by their own ids (replace-on-arrival).</summary>
    public void Apply(IFedMessage msg)
    {
        switch (msg)
        {
            case SetCluster setCluster:
                Apply(setCluster);
                break;
            case SingleShipUpdate update:
                Apply(update);
                break;
            case ShipStatusMessage status:
                Apply(status);
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
            case ChatMessage chatMessage:
                Apply(chatMessage);
                break;
            case ChatBuoy chatBuoy:
                Apply(chatBuoy);
                break;
            case Payday payday:
                Apply(payday);
                break;
            case KillShip killShip:
                Apply(killShip);
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
            case SetStartTime setStartTime:
                Apply(setStartTime);
                break;
            case SetBriefingText briefing:
                Apply(briefing);
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
            case CreateChaff createChaff:
                // P292 — the server's deploy broadcast for a fresh chaff decoy.
                Apply(createChaff);
                break;
            case OwnShipMiningUpdate mining:
                Apply(mining);
                break;
            case MissileTrackWarningUpdate missileTrack:
                Apply(missileTrack);
                break;
            case ScoreRosterUpdate scoreRoster:
                Apply(scoreRoster);
                break;
            case LeadIndicatorUpdate leadIndicator:
                Apply(leadIndicator);
                break;
            case ExplosionEvent explosion:
                Apply(explosion);
                break;
            case StationDestroyed stationDestroyed:
                Apply(stationDestroyed);
                break;
            case StationCapture stationCapture:
                Apply(stationCapture);
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
            case SideTechChange sideTechChange:
                // P284 — a development completed: the side's new mask.
                Apply(sideTechChange);
                break;
        }
    }

    private void Apply(SetCluster set)
    {
        // On first arrival this is the "here is your ship" assignment.
        MyShipId ??= set.Ship.ShipId;

        var e = Upsert(set.Ship.ShipId);
        e.IsMine = true;
        e.SectorId = set.SectorId;   // top-level SET_CLUSTER sectorID
        e.Latest = set.Ship;         // store verbatim, don't re-flatten
        e.LastUpdateTick = set.Ship.Time;
    }

    private void Apply(SingleShipUpdate update)
    {
        var e = Upsert(update.OidTarget);
        e.Latest = update.Ship;
        e.LastUpdateTick = update.Ship.Time;
    }

    private void Apply(ShipStatusMessage status)
    {
        var e = Upsert(status.ShipId);
        e.HullId = status.HullId;
        e.SectorId = status.SectorId;
        e.StationId = status.StationId;
        e.ParentId = status.ParentId;
        e.State = status.State;
        e.Unknown = status.Unknown;
        e.Detected = status.Detected;
        e.StateTime = status.StateTime;
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
        e.Loadout = export.Parts
            .Select(p => new ExpandedPartData(p.PartId, p.Amount, p.MountId, ExpandedPartData.FullyMounted))
            .ToArray();
    }

    private void Apply(ExportCluster export) =>
        _clusters[export.Cluster.ClusterId] = export.Cluster;

    private void Apply(ExportStation export) =>
        _stations[export.Station.StationId] = export.Station;

    private void Apply(ExportWarp export) =>
        _warps[export.Warp.WarpId] = export.Warp;

    private void Apply(ExportAsteroid export) =>
        _asteroids[export.Asteroid.AsteroidId] = export.Asteroid;

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
    }

    /// <summary>P291 — <c>FM_S_MINE_DESTROYED(84)</c>: the mine detonated or its
    /// lifespan expired (the server's MineDestroyedEvent), so it leaves the world
    /// table — which is what removes its decal field from the scene (the
    /// renderer's per-frame snapshot simply no longer contains it).</summary>
    private void Apply(MineDestroyed destroyed) =>
        _mines.Remove(destroyed.MineId);

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
            ReceivedLocalMs = Environment.TickCount64,
        });
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
            ReceivedLocalMs = Environment.TickCount64,
        });
    }

    /// <summary>P267 — <c>FM_S_STATION_DESTROYED(59)</c>: an own/friendly station
    /// plays the <c>DestroyedSound</c> one-shot, an enemy station the
    /// <c>EnemyDestroyedSound</c> (native <c>appmsg.cpp:548-551</c>), decided by
    /// the destroyed station's side vs <see cref="MySideId"/> (collapsed to
    /// same-side — the client has no alliance graph; see
    /// <see cref="StationEventSoundKind"/>). The station then leaves the world
    /// table, which also ends its exterior ambient loop via the station-sound
    /// planner's disappearance pass.</summary>
    private void Apply(StationDestroyed destroyed)
    {
        bool enemy = true;
        if (_stations.TryGetValue(destroyed.StationId, out var st))
            enemy = !(MySideId.HasValue && st.SideId == MySideId.Value);
        RecordSoundEvent(enemy ? ClientSoundEventKind.StationEnemyDestroyed : ClientSoundEventKind.StationDestroyed,
            destroyed.StationId);
        _stations.Remove(destroyed.StationId);
    }

    /// <summary>P267 — <c>FM_S_STATION_CAPTURE(58)</c>: if the station was OURS
    /// (sidOld == our side) play <c>CapturedSound</c>; if it became OURS
    /// (sidNew == our side) play <c>EnemyCapturedSound</c> (native
    /// <c>appmsg.cpp:674-677</c>). The station re-sides to its new owner in the
    /// world table, mirroring native's <c>station-&gt;SetSide(psideNew)</c>.</summary>
    private void Apply(StationCapture capture)
    {
        ClientSoundEventKind kind;
        if (MySideId.HasValue && capture.SidOld == MySideId.Value)
            kind = ClientSoundEventKind.StationCaptured;
        else if (MySideId.HasValue && capture.SidNew == MySideId.Value)
            kind = ClientSoundEventKind.StationEnemyCaptured;
        else
            kind = ClientSoundEventKind.StationCaptured;   // neither ours — default to the neutral-capture readout

        RecordSoundEvent(kind, capture.StationId);

        // Re-side the station to its new owner (native SetSide).
        if (_stations.TryGetValue(capture.StationId, out var st))
            _stations[capture.StationId] = st with { SideId = capture.SidNew };
    }

    /// <summary>P267 — <c>FM_S_DEV_COMPLETED(8)</c>: a development finished
    /// researching → its completion chime (native <c>appmsg.cpp:3326</c>).</summary>
    private void Apply(DevCompleted completed) =>
        RecordSoundEvent(ClientSoundEventKind.DevCompleted, completed.DevId);

    private void RecordSoundEvent(ClientSoundEventKind kind, short id)
    {
        _soundEvents.Add(new ClientSoundEvent(kind, id));
        if (_soundEvents.Count > MaxSoundEvents) _soundEvents.RemoveAt(0);
    }

    /// <summary>FM_CS_PROJECTILE_INSTANCE(12) — append a transient projectile
    /// entity capturing the real spawn position/velocity/heading/lifespan plus
    /// the local receipt instant (see <see cref="ProjectileEntity"/> for why
    /// receipt time anchors expiry and motion extrapolation here). One entry per
    /// real broadcast; no id to upsert on — native projectiles are fire-and-forget.</summary>
    private void Apply(ProjectileInstance m)
    {
        _projectiles.Add(new ProjectileEntity
        {
            ProjectileTypeId = m.ProjectileTypeId,
            Position = m.Position,
            Forward = m.Forward,
            Velocity = m.Velocity,
            Lifespan = m.Lifespan,
            ReceivedLocalMs = Environment.TickCount64,
        });
    }

    /// <summary>Remove projectile entries whose lifetime has elapsed by the given
    /// local monotonic instant. <see cref="ClientWorld"/> is pure (no clock), so
    /// the caller supplies the time and typically calls this once per frame under
    /// the same gate lock guarding the other world reads, before gathering the
    /// live set to render.</summary>
    public void PruneExpiredProjectiles(long nowLocalMs) =>
        _projectiles.RemoveAll(p => p.ReceivedLocalMs + (long)(p.Lifespan * 1000.0) <= nowLocalMs);

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
    private void Apply(FireMissile m)
    {
        if (m.Entries.Count == 0) return;               // degenerate (legal) empty salvo
        var e = m.Entries[0];
        _missiles[e.MissileId] = new MissileEntity
        {
            MissileId = e.MissileId,
            TypeId = m.MissileTypeId,
            ClusterId = m.ClusterId,
            Position = e.VecPosition,
            Forward = e.VecForward.LengthSquared() > 1e-6f ? e.VecForward.Normalize() : new Vector3(0f, 0f, -1f),
            LastUpdatedLocalMs = Environment.TickCount64,
        };
    }

    /// <summary>FM_S_MISSILE_UPDATE(205) — the P234 per-tick ongoing-position
    /// stream. Upsert every carried record by missile id (an id not seen via a
    /// FIRE_MISSILE creation — e.g. a peer that missed the one-shot — is hydrated
    /// straight from the update), refreshing the authoritative position/heading
    /// and stamping the receipt instant so the stale pruner knows it is alive.</summary>
    private void Apply(MissileUpdate m)
    {
        long now = Environment.TickCount64;
        foreach (var r in m.Records)
        {
            _missiles[r.MissileId] = new MissileEntity
            {
                MissileId = r.MissileId,
                TypeId = r.TypeId,
                ClusterId = r.ClusterId,
                Position = r.Position,
                Forward = r.Forward.LengthSquared() > 1e-6f ? r.Forward.Normalize() : new Vector3(0f, 0f, -1f),
                LastUpdatedLocalMs = now,
            };
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
        if (string.IsNullOrEmpty(e.CharacterName)) e.CharacterName = info.CharacterName;
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
        e.StationId = notify.StationId;
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
        e.Loadout = ack.Parts;          // the mounted part tail, in mount order (possibly empty = hull-only)
    }

    // ── Event log (transient, append-only, bounded) ────────────────────────
    private void Apply(ChatMessage m) => _events.Record(m);
    private void Apply(ChatBuoy m) => _events.Record(m);
    private void Apply(Payday m) => _events.Record(m);
    private void Apply(KillShip m)
    {
        _events.Record(m);
        // The victim stays in the entity table (that's the point of keying by
        // id — "did ship X die" stays answerable) but gains the death flag.
        // Upsert (create-if-absent) so a KILL_SHIP for a ship we haven't seen
        // otherwise still marks a dead entity rather than throwing.
        Upsert(m.ShipId).IsDead = true;
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
    private void Apply(MissionDef def) =>
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
            SideMissionOwner = def.ISideMissionOwner,
            Stage = def.Stage,
        };

    private void Apply(MissionStageMessage stage)
    {
        // Refine just the stage on whatever mission we know about, or mint the
        // first snapshot if a MISSION_STAGE arrived before any MISSIONDEF (then
        // the core fields stay their defaults until a MISSIONDEF lands).
        _mission = new MissionInfo
        {
            StaticCoreFile = _mission?.StaticCoreFile ?? "",
            StaticCoreVersion = _mission?.StaticCoreVersion ?? 0,
            Cookie = _mission?.Cookie ?? 0,
            SideMissionOwner = _mission?.SideMissionOwner ?? 0,
            Stage = stage.Stage,
        };
    }

    /// <summary>FM_S_SET_START_TIME(14) — the server's visible countdown
    /// announcement (broadcast whole-mission in Server.StartCountdown). The wire
    /// carries only the target start time in server-clock milliseconds, which
    /// shares no epoch with the client; the port treats the RECEIPT of this
    /// message as the countdown's t=0 and records the local monotonic instant
    /// once (the countdown is one-shot per mission here). This is the real
    /// message native's <c>CheckCountdownSound</c> anchors on (it reads
    /// <c>timeStart</c>, WinTrek.cpp:11144), so it is the faithful "countdown
    /// began" signal — distinct from <c>Mission.Stage</c>, which the join burst
    /// pins to <c>StageStarted</c> and therefore cannot distinguish the brief
    /// STAGE_STARTING window (see the Game1 wiring).</summary>
    private void Apply(SetStartTime startTime) =>
        CountdownStartLocalMs ??= Environment.TickCount64;

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

    /// <summary>FM_S_GAME_OVER(62) — the server's real end-of-mission broadcast
    /// (Server.DeclareGameOver → GAME_OVER to every peer still on the mission).
    /// Drives the existing <see cref="MissionInfo.Stage"/> field to
    /// <see cref="MissionStageMessage.StageOver"/>: this is the moment the
    /// client learns the mission has ended, reusing the same
    /// <c>world.Mission.Stage</c> field the P207-era auto-ready/auto-undock
    /// logic already reads — no second parallel notion of "ended". Mirrors
    /// <see cref="Apply(MissionStageMessage)"/>'s mint-if-absent snapshot
    /// replace, preserving the static-core fields.</summary>
    private void Apply(GameOver gameOver)
    {
        // P262 — retain the full snapshot (reason + winner + per-side records)
        // for the on-screen result panel; the stage flip below stays the
        // canonical "the mission ended" signal the auto-undock/announcer reads.
        _gameOver = gameOver;

        _mission = new MissionInfo
        {
            StaticCoreFile = _mission?.StaticCoreFile ?? "",
            StaticCoreVersion = _mission?.StaticCoreVersion ?? 0,
            Cookie = _mission?.Cookie ?? 0,
            SideMissionOwner = _mission?.SideMissionOwner ?? 0,
            Stage = MissionStageMessage.StageOver,
        };
    }

    /// <summary>P281 — the SIDE_ROSTER(211) snapshot; replace-on-arrival (a late
    /// roster simply supersedes the earlier one — sides don't change identity,
    /// only their player counts).</summary>
    private void Apply(SideRoster roster) => SideRoster = roster;

    /// <summary>P282 — the whole-mission <c>TEAM_READY(46)</c> broadcast:
    /// replace-on-arrival for that side's aggregate ready flag (last write
    /// wins). Mirrors the <see cref="TeamRoster"/> current-state semantics for
    /// the per-side aggregate level.</summary>
    private void Apply(TeamReady ready) => _teamReadyBySideId[ready.SideId] = ready.FReady;

    /// <summary>P282 — the whole-mission forward of <c>FORCE_TEAM_READY(47)</c>
    /// (the server relays it to every mission peer, incl. the sender): records
    /// that side's persistent force-ready flag.</summary>
    private void Apply(ForceTeamReady force) => _forceReadyBySideId[force.SideId] = force.FForceReady;

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
            m.LockFraction, m.TargetShipId, m.TargetPosition, m.MissileTypeName);

        var curr = MissileLockFeedback.Classify(m.LockFraction);
        bool targetChanged = m.TargetShipId != _prevLockTargetId;
        string? tip = MissileLockFeedback.TipFor(_prevLockState, curr, targetChanged, m.MissileTypeName);
        _prevLockState = curr;
        _prevLockTargetId = m.TargetShipId;

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
        var created = new ShipEntity { ShipId = shipId };
        _ships.Add(shipId, created);
        return created;
    }
}
