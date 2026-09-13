using Allegiance.Core;
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
    private readonly EventLog _events = new();
    private readonly TeamRoster _roster = new();
    private MissionInfo? _mission;

    /// <summary>The local player's ship id, set on the first SET_CLUSTER(7)
    /// assignment; null until the server has told us which ship we are.</summary>
    public short? MyShipId { get; private set; }

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

    /// <summary>Persistent state about the mission itself (static core, cookie,
    /// owner, stage) — distinct from any one ship. Null until the first
    /// <c>MISSIONDEF(38)</c> or <c>MISSION_STAGE(37)</c> arrives.</summary>
    public MissionInfo? Mission { get { return _mission; } }

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
        // entity table. (Teammate exports always carry the real id.)
        if (export.ShipId == -1) return;

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

    private void Apply(PlayerInfo info)
    {
        var e = Upsert(info.ShipId);        // create-if-absent: a ship we only
                                            // heard of via PLAYERINFO
        e.Player = info;
        if (string.IsNullOrEmpty(e.CharacterName)) e.CharacterName = info.CharacterName;
    }

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

    private ShipEntity Upsert(short shipId)
    {
        if (_ships.TryGetValue(shipId, out var existing)) return existing;
        var created = new ShipEntity { ShipId = shipId };
        _ships.Add(shipId, created);
        return created;
    }
}
