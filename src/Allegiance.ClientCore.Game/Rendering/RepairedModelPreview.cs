using System;
using System.Collections.Generic;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P581 — inspector preview of a <em>repaired</em> Meshy model. The repair
/// pipeline (<c>tools/meshy-repair/repair.py</c>) writes a repaired copy to
/// <c>Content/Models/repaired/&lt;asset&gt;.fbx</c> (originals untouched), compiled by
/// the content pipeline as <c>Models/repaired/&lt;asset&gt;</c>. This pure resolver
/// decides which model key to draw: when the operator asks for the repaired
/// variant AND that asset is loaded, use it; otherwise fall back to the raw
/// Meshy asset. Purely a view-time choice — the in-game ship keeps whatever
/// ship-art.json says (P581 pilot: Adv Scout stays native).
///
/// The returned key is meant to be looked up in the client's loaded-model
/// dictionary (<c>Game1._shipModels</c>, keyed by asset name, with repaired
/// variants keyed <c>"repaired/&lt;asset&gt;"</c>).
/// </summary>
public static class RepairedModelPreview
{
    /// <summary>The dictionary key a repaired variant of a Meshy asset loads under.</summary>
    public static string RepairedKey(string meshyAsset) => "repaired/" + meshyAsset;

    /// <summary>
    /// Choose the model key to draw: the repaired variant when
    /// <paramref name="preferRepaired"/> and it is among <paramref name="availableKeys"/>,
    /// otherwise the raw <paramref name="meshyAsset"/>.
    /// </summary>
    public static string Resolve(IReadOnlySet<string> availableKeys, string meshyAsset, bool preferRepaired)
    {
        if (preferRepaired && !string.IsNullOrEmpty(meshyAsset))
        {
            var repaired = RepairedKey(meshyAsset);
            if (availableKeys.Contains(repaired)) return repaired;
        }
        return meshyAsset;
    }
}
