namespace Allegiance.Core.Model;

using Allegiance.Core.Sim;

/// <summary>
/// A warp in a <see cref="Sector"/> together with the <b>resolved</b> sector it
/// points at. The reference engine resolves a warp's partner lazily via
/// <c>GetWarp(m_warpDef.destinationID)</c> (<c>warpIGC.h:129-141</c>) — a
/// <c>destinationID</c> names the <b>other warp</b> of the pair, and the
/// destination <b>sector</b> is that partner warp's cluster (the corpus
/// validator also permits <c>destinationID</c> to name a sector directly;
/// <c>CrossRefValidator.cs:139-143</c>). <see cref="Destination"/> is the
/// target <see cref="Sector"/>, or <c>null</c> when it could not be resolved
/// (a dangling reference — reported, never thrown).
/// </summary>
/// <remarks>
/// Implements the empty <see cref="IWarpIgc"/> marker so it can be handed to
/// <see cref="IIgcSite.HitWarpEvent"/>, mirroring <see cref="Sector"/> :
/// <c>IClusterIgc</c> and <see cref="Ship"/> : <c>IShipIgc</c>. Like those, the
/// marker is deliberately empty — this class is the concrete data carrier and
/// the marker is just the seam's object-pointer shape.
/// </remarks>
public sealed class ResolvedWarp : IWarpIgc
{
    /// <summary>The raw warp record.</summary>
    public DataWarpIGC Warp { get; }

    /// <summary>
    /// The sector this warp leads to, or <c>null</c> if <c>DestinationId</c>
    /// named neither a warp nor a sector in the mission (a dangling edge).
    /// </summary>
    public Sector? Destination { get; }

    /// <summary>Construct a warp with its resolved destination (may be <c>null</c> for a dangle).</summary>
    public ResolvedWarp(DataWarpIGC warp, Sector? destination)
    {
        Warp = warp;
        Destination = destination;
    }

    /// <summary>
    /// The other warp of this warp's pair — the C++ <c>GetWarp(m_warpDef.destinationID)</c>
    /// (<c>warpIGC.h:129-141</c>) realized concretely. <see cref="Warp.DestinationId"/>
    /// names the partner warp; it lives in <see cref="Destination"/>'s
    /// <see cref="Sector.Warps"/>. Returns <c>null</c> when there is no partner
    /// (a <see cref="Destination"/> that is a sector named directly, per
    /// <c>CrossRefValidator.cs:139-143</c>). Never occurs in the shipped corpus
    /// (measured 0 / 5212 — every <c>destinationID</c> resolves to a warp);
    /// guarded so the transit can report rather than fabricate.
    /// </summary>
    public ResolvedWarp? Partner() =>
        Destination?.Warps.FirstOrDefault(w => w.Warp.WarpId == Warp.DestinationId);
}
