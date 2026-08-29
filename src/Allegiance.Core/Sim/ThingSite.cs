namespace Allegiance.Core.Sim;

/// <summary>
/// A minimal per-object host shim, analogous to C++ <c>ThingSite</c>
/// (<c>src/Igc/igc.h:2986</c>). Kept to the shape the interface requires —
/// identity/owner only. The C++ class is a large render-site surface
/// (visibility, flares, damage decals, geometry); none of that is ported and
/// the C++ type has <b>no</b> Update hook, so nothing further is added here.
/// Built by <see cref="IIgcSite.CreateThingSite"/>.
/// </summary>
public class ThingSite
{
    /// <summary>The model this shim fronts, if one was supplied at creation.</summary>
    public IModelIgc? Owner { get; init; }

    /// <summary>Constructs an unattached shim (the C++ default ignores the model).</summary>
    public ThingSite() { }
}
