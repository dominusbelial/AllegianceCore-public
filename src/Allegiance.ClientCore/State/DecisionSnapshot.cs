using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.State;

/// <summary>
/// P636 — a frozen, decision-grade view of a <see cref="ClientWorld"/>, published
/// by that world's RECEIVE thread (the sole writer) and read LOCK-FREE by the bot
/// decision loop. It breaks the P632/P633 "transit collapse above ~250 bots":
/// previously every per-bot decision pass took the per-bot <c>cs.Gate</c> to scan
/// the live <c>Ships</c>/<c>Stations</c>/<c>Warps</c> dictionaries, and fought the
/// receive thread that holds the same gate while applying hundreds of ship records
/// per tick — so at 200v200 a pass stalled for seconds and bots decided ~once per
/// 30 s (never reaching staging/aleph transit). By publishing an immutable snapshot
/// the decision pass reads the LATEST one with a single volatile reference read and
/// no gate acquisition; the receive side's lock hold is unchanged, and the decision
/// side's lock hold drops to ~0 (microseconds → effectively none).
///
/// Immutability contract: once constructed and published, a <see cref="DecisionSnapshot"/>
/// is never mutated — the arrays are fully populated before the writer swaps the
/// published reference (a <c>Volatile.Read</c> in <see cref="ClientWorld.Decision"/>),
/// so a reader holding an older snapshot is safe from the writer's rebuilds. This is
/// the "publishes an immutable snapshot" option of the P636 decoupling requirement.
/// </summary>
public sealed class DecisionSnapshot
{
    /// <summary>Monotonic publish counter — a reader can detect a fresher snapshot.</summary>
    public long Version;

    /// <summary>The local player's ship id, or null before seating.</summary>
    public short? MyShipId;

    /// <summary>The local player's side id, or null before seating.</summary>
    public short? MySideId;

    /// <summary>The local ship's current sector id (-1 = none). Derived at publish.</summary>
    public short MyCluster = -1;

    /// <summary>All ships known to this world, SORTED ascending by <see cref="DecisionShip.ShipId"/>
    /// for binary-search lookups. Value copies; safe to scan or search without a lock.</summary>
    public DecisionShip[] Ships = [];

    /// <summary>Stations (cluster, side) — for the friendly-station-in-my-cluster flag.</summary>
    public DecisionStation[] Stations = [];

    /// <summary>Warps (id, cluster, destination warp+cluster, position, radius) — for transit routing.</summary>
    public DecisionWarp[] Warps = [];

    /// <summary>A shared empty snapshot (published initially and after <c>Reset</c>).</summary>
    public static readonly DecisionSnapshot Empty = new();

    /// <summary>Binary-search <paramref name="ships"/> (sorted ascending by ShipId) for
    /// <paramref name="shipId"/>. True + the entry when found.</summary>
    public static bool TryFind(in DecisionShip[] ships, short shipId, out DecisionShip found)
    {
        int lo = 0, hi = ships.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int id = ships[mid].ShipId;
            if (id == shipId) { found = ships[mid]; return true; }
            if (id < shipId) lo = mid + 1; else hi = mid - 1;
        }
        found = default;
        return false;
    }
}

/// <summary>P636 — the decision-relevant subset of one ship, as a value copy in the
/// snapshot (ShipEntity is a heap object mutated under the gate; the snapshot holds
/// the flat fields the bot decision loop reads, so it never touches the live entity).</summary>
public struct DecisionShip
{
    public short ShipId;
    public short SideId;
    public short SectorId;
    public short HullId;
    public short StationId;

    /// <summary>bit0 = the ship is visible to the local peer (not dead / not out of view).</summary>
    public byte Flags;

    public float PosX, PosY, PosZ;    // Latest.Position
    public float VelX, VelY, VelZ;    // Latest.Velocity
    // Orientation's 3x3 basis (r00..r22) — enough to rebuild an Orientation for aiming.
    public float R00, R01, R02, R10, R11, R12, R20, R21, R22;

    public bool IsVisible => (Flags & 1) != 0;
    /// <summary>bit1 — a pose (<c>Latest</c>) is available.</summary>
    public bool HasLatest => (Flags & 2) != 0;
    public Vector3 Position => new(PosX, PosY, PosZ);
    public Vector3 Velocity => new(VelX, VelY, VelZ);

    /// <summary>Materialise an <see cref="Orientation"/> from the stored basis (the
    /// aiming/steer laws consume it). Allocated on demand, never in a scan.</summary>
    public Orientation MaterializeOrientation()
    {
        var o = new Orientation();
        o.SetBasis(R00, R01, R02, R10, R11, R12, R20, R21, R22);
        return o;
    }
}

/// <summary>P636 — one station, flattened to (cluster, side) for the friendly-station check.</summary>
public struct DecisionStation
{
    public short ClusterId;
    public short SideId;
}

/// <summary>P636 — one warp, flattened for transit routing (<c>TryBuildWarpDestMap</c>/
/// <c>TryFindWarpTo</c> read cluster + destination + position + radius).</summary>
public struct DecisionWarp
{
    public short WarpId;
    public short ClusterId;
    public short DestWarpId;
    public short DestClusterId;
    public float PosX, PosY, PosZ;
    public float Radius;
    public Vector3 Position => new(PosX, PosY, PosZ);
}
