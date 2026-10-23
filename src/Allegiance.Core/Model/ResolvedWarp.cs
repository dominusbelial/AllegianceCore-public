namespace Allegiance.Core.Model;

using Allegiance.Core.Collision;
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
public sealed class ResolvedWarp : IWarpIgc, ISideVisible
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
    /// P797 — the aleph's per-side "seen" state: the port's host of the native
    /// <c>ThingSiteImpl::m_rgSideVisibility</c> for an <c>OT_warp</c> model.
    /// Native creates every aleph with
    /// <c>c_mtStatic | c_mtHitable | c_mtSeenBySide | c_mtPredictable</c>
    /// (<c>Igc/warpIGC.cpp:35</c>), so a warp takes part in the cluster's
    /// per-side <c>UpdateSeenBySide</c> sweep exactly like a rock or a station
    /// (<c>clusterIGC.cpp:347-357</c>) and — being <c>c_mtPredictable</c> — stays
    /// known to a side once that side has seen it (<c>FedSrv.CPP:11124-11126</c>).
    /// Written by <see cref="Sector.ResolveCrossSideVisibility"/>; read by the
    /// join export (<c>fsmission.cpp:4175-4186</c>) and the discovery sweep.
    /// </summary>
    public SideVisibilityState SideVisibility { get; } = new() { Predictable = true };

    /// <summary>Whether <paramref name="sideId"/> has seen this aleph — the port of
    /// <c>ImodelIGC::SeenBySide</c> (<c>Igc/modelIGC.h:110-114</c>).</summary>
    public bool SeenBySide(short sideId) => SideVisibility.SeenBySide(sideId);

    /// <summary>Whether <paramref name="sideId"/> is currently the eye on this
    /// aleph (native <c>GetCurrentEye</c>, <c>modelIGC.h:120-124</c>). Decays when
    /// the last scanner leaves range; <see cref="SeenBySide"/> does not.</summary>
    public bool GetCurrentEye(short sideId) => SideVisibility.GetCurrentEye(sideId);

    /// <summary>The aleph's fixed world position in its sector (<c>dataWarp.position</c>,
    /// <c>warpIGC.cpp:47</c>), or the origin when the record has no usable vector.
    /// Mirrors <see cref="Ship.Position"/>'s role for the sweep's range test.</summary>
    public Vector3 Position => Warp.Position is { Length: 3 } p ? new Vector3(p[0], p[1], p[2]) : Vector3.Zero;

    /// <summary>The aleph's collision/visual radius (<c>SetRadius(m_warpDef.radius)</c>,
    /// <c>warpIGC.cpp:45</c>) — the body radius the sweep's <c>InScannerRange</c>
    /// adds to the scanner's own (<c>Igc/common.cpp</c>, <c>InScannerRange</c>).</summary>
    public float Radius => Warp.Radius;

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
