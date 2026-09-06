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

    private ShipEntity Upsert(short shipId)
    {
        if (_ships.TryGetValue(shipId, out var existing)) return existing;
        var created = new ShipEntity { ShipId = shipId };
        _ships.Add(shipId, created);
        return created;
    }
}
