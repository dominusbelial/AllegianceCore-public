using System;
using System.Collections.Generic;
using System.IO;
using Allegiance.Core;
using Allegiance.Core.Content;
using Allegiance.Core.Model;
using Microsoft.Xna.Framework;

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

    /// <summary>
    /// P336 — the real, native-rendered size of an imported missile model's
    /// asset, measured as the merged-bounding-sphere radius of its real
    /// <c>.mdl</c> file parsed straight from disk. This is the size the
    /// native-<c>.mdl</c> render path (<see cref="MissileRenderer"/>, which draws
    /// raw <c>.mdl</c> coordinates at identity scale) actually renders that model
    /// at, so it is the correct reference to make the imported content path
    /// agree with the native path.
    ///
    /// <para><b>Why not <c>DataMissileTypeIGC.Radius</c>.</b> The task brief's
    /// preferred real dimension — the per-type <c>radius</c> field on the static
    /// core — does not track the mesh's visual size (measured across PCore001:
    /// mis05 types have <c>Radius=2</c> but the 5.8-unit <c>mis05.mdl</c>; type 83
    /// has <c>Radius=18</c> yet its own <c>mis400.mdl</c> is only ~4.5 units),
    /// so it is not a clean visual reference and normalizing to it would make the
    /// imported and native paths diverge further. The review's documented
    /// fallback — match the native-<c>.mdl</c> path's rendered size — is used
    /// instead. Measured merged radii: mis05 ≈ 5.80, mis400 ≈ 4.52, artifact ≈ 13.33.</para>
    ///
    /// <para>Measurement matches <see cref="ShipRenderScale.RenderRadius"/>'s
    /// merge technique, applied to raw <c>.mdl</c> meshes (no bone bake — the
    /// native path draws them at identity). Result is cached per asset; a missing
    /// <c>.mdl</c> yields 0 so the caller degrades to no scaling.</para>
    /// </summary>
    public static float NativeMdlRadius(string assetName)
    {
        if (string.IsNullOrWhiteSpace(assetName)) return 0f;
        if (_nativeRadiusCache.TryGetValue(assetName, out var cached)) return cached;

        float radius = 0f;
        try
        {
            var mdl = MdlFile.Parse(Path.Combine(ResolveArtworkDir(), assetName + ".mdl"));
            BoundingSphere? acc = null;
            foreach (var m in mdl.Meshes)
            {
                var p = m.Positions;
                Vector3 min = new(float.MaxValue), max = new(float.MinValue);
                for (int v = 0; v < m.VertexCount; v++)
                {
                    var pt = new Vector3(p[3 * v], p[3 * v + 1], p[3 * v + 2]);
                    min = Vector3.Min(min, pt);
                    max = Vector3.Max(max, pt);
                }
                var sph = BoundingSphere.CreateFromBoundingBox(new BoundingBox(min, max));
                acc = acc is null ? sph : BoundingSphere.CreateMerged(acc.Value, sph);
            }
            radius = acc?.Radius ?? 0f;
        }
        catch
        {
            radius = 0f; // degrade-not-crash: caller leaves the model unscaled
        }
        _nativeRadiusCache[assetName] = radius;
        return radius;
    }

    /// <summary>
    /// P336 — the uniform scale that makes an imported content missile model
    /// render at the real native-<c>.mdl</c> size: scale so the model's true
    /// rendered merged-sphere radius (<see cref="ShipRenderScale.RenderRadius"/>,
    /// the ~100× bone bake included) equals <paramref name="nativeRadius"/>
    /// (<see cref="NativeMdlRadius"/>). Guarded: non-positive either side → 1
    /// (no scaling), matching the renderer's degrade-not-crash rule.
    /// </summary>
    public static float ScaleForNativeSize(float nativeRadius, float importedRenderRadius)
    {
        if (nativeRadius <= 0f || importedRenderRadius <= 0f) return 1f;
        return nativeRadius / importedRenderRadius;
    }

    /// <summary>Native-<c>.mdl</c> merged-radius cache keyed by asset stem — each
    /// imported asset maps to exactly one <c>.mdl</c>, so the stem is a stable
    /// key.</summary>
    private static readonly Dictionary<string, float> _nativeRadiusCache = new(StringComparer.Ordinal);
}
