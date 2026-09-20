using Allegiance.Core;
using Allegiance.Core.Collision;

namespace Allegiance.ClientCore.State;

/// <summary>
/// P293 — a live treasure in the client's world model — one per real treasure,
/// keyed by <see cref="TreasureId"/> (the <c>DataTreasureIGC.ObjectId</c>,
/// which <c>FM_S_TREASURE(28)</c> carries verbatim and
/// <c>FM_S_DESTROY_TREASURE(30)</c> references by id). A treasure is a
/// <b>static</b> object in this port (P123 places it already at rest; the
/// deferred spawn system has no launch/<c>v0</c> path yet — see
/// <c>Allegiance.Core.Sim.Treasure</c>'s honest-scope remarks), so unlike a
/// <see cref="MissileEntity"/> it carries no per-tick streamed position — the
/// <c>TREASURE</c> <c>P0</c> is final for its lifetime. Populated from the
/// creation <c>FM_S_TREASURE(28)</c> broadcast (this port's real first send is
/// the <c>DROP_PART(33)</c> → part-treasure path, <c>Server.cs:7014</c>, and the
/// treasure-ack decline re-spawn, <c>Server.cs:7817</c>) and removed by the
/// server's <c>FM_S_DESTROY_TREASURE(30)</c> (sent on pickup/expiry,
/// <c>Server.cs:8824</c>), so a <see cref="TreasureEntity"/>'s presence in the
/// world mirrors a live treasure's survival — the client-side substrate P293's
/// renderer reads.
/// </summary>
/// <remarks>
/// <para><b>Why no size field rides along.</b> A mine carries its
/// <c>MinetypeId</c> so the renderer resolves the real <c>MineRadius</c> from
/// the static core, but a treasure's <c>DataTreasureIGC</c> carries <b>no
/// radius/size</b> (igc.h:2517-2529) and native hardcodes the collision radius
/// to <c>SetRadius(5.0f)</c> (<c>treasureIGC.cpp:193</c>, <c>//NYI -- needs to
/// be gotten from the DB</c>). So the renderer uses that fixed 5.0 — no core
/// lookup, unlike mines/chaff.</para>
/// <para><b>What the per-code fields are for.</b> The treasure's
/// <see cref="Code"/> (cash/part/powerup/development/flag) decides how
/// <see cref="PayloadId"/> is interpreted — cash value, part id, powerup
/// bitmap, development id, or (for flags) the owning side
/// (<c>treasureIGC.cpp:91-92,128-149</c>) — and native derives the (unavailable)
/// visual from it. The renderer draws a single gold-gem placeholder for every
/// code (P289 station-placeholder framing), but the fields are carried so a
/// future per-code differentiation has the data and the entity faithfully
/// mirrors the wire record.</para>
/// </remarks>
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

    /// <summary>The treasure's position (<c>DataTreasureIGC.P0</c>) — fixed for
    /// the treasure's whole life (this port places treasures already at rest;
    /// the deceleration-to-stop integration is part of the deferred spawn
    /// system, <c>Allegiance.Core.Sim.Treasure</c> remarks).</summary>
    public Vector3 Position;
}
