namespace Allegiance.Core.Model;

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
public sealed class ResolvedWarp
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
}
