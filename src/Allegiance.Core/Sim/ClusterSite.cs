namespace Allegiance.Core.Sim;

/// <summary>
/// A minimal per-sector host shim, analogous to C++ <c>ClusterSite</c>
/// (<c>src/Igc/igc.h:4511</c>). Kept to the shape the interface requires —
/// identity/owner only. The C++ class is a render-site surface (posters,
/// environment geo, scanners, rotation) with <b>no</b> Update hook, so nothing
/// further is added here. Built by <see cref="IIgcSite.CreateClusterSite"/>.
/// </summary>
public class ClusterSite
{
    /// <summary>The cluster this shim fronts, if one was supplied at creation.</summary>
    public IClusterIgc? Owner { get; init; }

    /// <summary>Constructs an unattached shim (the C++ default ignores the cluster).</summary>
    public ClusterSite() { }
}
