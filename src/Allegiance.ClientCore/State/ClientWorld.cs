using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.State;

/// <summary>
/// A pure domain object: the client's game-state model built only from real
/// inbound wire messages. No I/O, no locking — a caller (e.g.
/// <see cref="Session.ConnectionSession"/> which provides the lock) drives
/// <see cref="Apply"/> from its receive path and reads <see cref="Ships"/>
/// snapshot-safely from other threads.
///
/// This is deliberately the smallest slice of the protocol, not the whole
/// thing: it understands exactly three inbound message types (plus, in
/// <see cref="Session.ConnectionSession"/>, <see cref="LogonAck"/> for connect
/// proof). Everything else passes through untouched.
///
/// The core invariant this class exists to prove: the <em>same</em> ship id
/// arriving across SET_CLUSTER(7), SINGLE_SHIP_UPDATE(78) and SHIP_STATUS(90)
/// must upsert ONE <see cref="ShipEntity"/>, never create three. Every branch
/// below upserts by ship id — there is no <c>Add</c> and no per-message entity.
/// </summary>
public sealed class ClientWorld
{
    private readonly Dictionary<short, ShipEntity> _ships = new();

    /// <summary>The local player's ship id, set on the first SET_CLUSTER(7)
    /// assignment; null until the server has told us which ship we are.</summary>
    public short? MyShipId { get; private set; }

    /// <summary>Ships keyed by ship id, snapshot-consistent (never mutated
    /// while exposed — callers should hold the caller's lock).</summary>
    public IReadOnlyDictionary<short, ShipEntity> Ships => _ships;

    /// <summary>Upsert one ship's accumulated state from a decoded message.
    /// Switch on the concrete decoded type; every branch targets the same ship
    /// id, so any interleaving yields one entity per id.</summary>
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

    private ShipEntity Upsert(short shipId)
    {
        if (_ships.TryGetValue(shipId, out var existing)) return existing;
        var created = new ShipEntity { ShipId = shipId };
        _ships.Add(shipId, created);
        return created;
    }
}
