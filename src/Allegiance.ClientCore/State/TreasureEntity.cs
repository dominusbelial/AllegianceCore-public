using Allegiance.Core;
using Allegiance.Core.Collision;

namespace Allegiance.ClientCore.State;

/// <summary>A treasure's original exported trajectory (Igc/treasureIGC.cpp:268-277).
/// Position is evaluated with the same analytical curve as the server.</summary>
public sealed class TreasureEntity
{
    /// <summary>The treasure's ObjectID (<c>DataTreasureIGC.ObjectId</c> = short,
    /// the key — <c>DESTROY_TREASURE</c>'s <c>treasureID</c> refers to it).</summary>
    public short TreasureId;

    /// <summary>The sector (cluster) this treasure lives in — the draw loop's
    /// sector filter key, exactly like <see cref="MineEntity.ClusterId"/> /
    /// probes / warps.</summary>
    public short ClusterId;

    /// <summary>The per-code payload (<c>DataTreasureIGC.TreasureId</c>): for
    /// <c>Cash</c> the dollar value; for <c>Part</c> the part type id; for a
    /// <c>Flag</c> the owning side — interpreted via <see cref="Code"/>.</summary>
    public short PayloadId;

    /// <summary>The cash amount / part count (<c>DataTreasureIGC.Amount</c>).</summary>
    public short Amount;

    /// <summary>The treasure's code (<c>DataTreasureIGC.TreasureCode</c>) —
    /// cash/part/powerup/development/flag.</summary>
    public TreasureCode Code;

    private Vector3 _position;
    internal Vector3 InitialVelocity;
    internal double CreatedAt;
    internal Func<double>? ServerClock;
    public Vector3 Position
    {
        get => ServerClock is null ? _position : Allegiance.Core.Sim.TreasureMotion.PositionAt(
            _position, InitialVelocity, ServerClock() - CreatedAt);
        set => _position = value;
    }
    public Vector3 Velocity => ServerClock is null ? Vector3.Zero
        : Allegiance.Core.Sim.TreasureMotion.VelocityAt(InitialVelocity, ServerClock() - CreatedAt);
}
