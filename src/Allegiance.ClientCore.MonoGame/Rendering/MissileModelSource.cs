using System;
using Allegiance.Core;
using Allegiance.Core.Model;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P234/P288 — resolves which real missile model the renderer loads.
///
/// <para><b>P288 real per-type lookup.</b> The port's MonoGame client now parses
/// the real static core (<see cref="ShopCatalog"/> → <see cref="StaticCore"/>)
/// and the wire carries each fired missile's real <c>TypeId</c>
/// (<see cref="ClientCore.State.MissileEntity.TypeId"/> from FIRE_MISSILE(70)),
/// so this can map <c>typeId → ModelName → imported asset</c> the same way the
/// server (<c>DataMissileTypeIGC.ModelName</c>) can. Of the ~3 dozen missile
/// ModelNames in PCore001 only <b>three</b> were imported by P287 —
/// <c>mis05</c> (types 32/33/34/61/62/63), <c>mis400</c> (type 83) and
/// <c>artifact</c> (types 14/31/70/71) — so every other type falls back to the
/// single native <c>.mdl</c> demo model. See the P288 writeup for the type
/// census.</para>
/// </summary>
public static class MissileModelSource
{
    /// <summary>The real <c>.mdl</c> stem for the P234 demo missile type (PCore001
    /// type 34). Kept as the fallback native-model stem for types with no
    /// imported content asset (P234's single-model scope).</summary>
    public const string DemoModelStem = "mis05";

    /// <summary>The configured artwork root (ALLEGIANCE_ARTWORK_PATH, falling
    /// back to the standard dev location), where <c>.mdl</c> assets live.</summary>
    public static string ResolveArtworkDir() =>
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    /// <summary>Parse the demo missile's real <c>.mdl</c> once. Throws if the
    /// artwork path is unavailable — the model is required for rendering.</summary>
    public static MdlFile LoadDemoModel() =>
        MdlFile.Parse(System.IO.Path.Combine(ResolveArtworkDir(), DemoModelStem + ".mdl"));

    /// <summary>
    /// Map a fired missile's real <c>TypeId</c> to the P287-imported content
    /// asset (<c>mis05</c>/<c>mis400</c>/<c>artifact</c>), or null when that
    /// type's model has no imported art (→ caller uses the native demo model /
    /// fallback). <paramref name="missileTypes"/> is the static core's missile
    /// table (<see cref="ShopCatalog.MissileTypes"/>), null-safe: a missing
    /// table yields null → fallback, matching the renderer's degrade-not-crash
    /// rule.
    /// </summary>
    public static string? ResolveImportedAsset(short typeId, IReadOnlyDictionary<short, DataMissileTypeIGC>? missileTypes)
    {
        if (missileTypes is null || !missileTypes.TryGetValue(typeId, out var mt))
            return null;
        var model = mt.ModelName?.Trim();
        if (string.IsNullOrEmpty(model)) return null;
        // Only the three P287-imported missile meshes have content art.
        return Array.IndexOf(HullModelResolver.MissileAssets, model) >= 0 ? model : null;
    }
}
