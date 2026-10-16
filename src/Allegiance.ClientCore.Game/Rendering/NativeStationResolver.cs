using System;
using System.Collections.Generic;
using Allegiance.Core;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P564 — the pure, GPU-free rules for rendering a station from its ORIGINAL
/// native <c>.mdl</c> station model + native texture (the P566 native-mesh
/// pipeline applied to stations), replacing the procedural hub+ring
/// <see cref="StationMeshFactory"/> placeholder. Everything here is CPU-only
/// math / name resolution so it is unit-testable headless, mirroring how
/// <see cref="NativeShipResolver"/> / <see cref="NativeAsteroidResolver"/>
/// keep their rules pure.
/// </summary>
/// <remarks>
/// <para><b>Model.</b> Every station type carries its own native model name —
/// <c>DataStationTypeIGC.ModelName</c> (<c>src/Igc/igc.h:2649</c>, decoded at
/// offset 8). Real PCore001 examples: Garrison <c>ss24a</c> (station types
/// 10/15), <c>ss27</c> (110/115), <c>ss300</c> (310/315); Outposts <c>ss92</c>,
/// <c>ss90</c>, <c>ss101</c>, <c>ss301</c>, <c>ss98</c>, …
/// (<c>DataStationTypeIGC.ModelName</c>).</para>
///
/// <para><b>Winding / culling.</b> This station pass preserves native winding
/// through <see cref="RenderIndices"/>; it never infers a group's facing from
/// its centroid. Per-vertex normals are unchanged.</para>
///
/// <para><b>Scale.</b> Native sizes every model to its gameplay collision
/// radius: <c>ThingGeo::SetRadius</c> sets <c>m_scale = radius / m_radiusMesh</c>
/// (thinggeo.cpp:1185-1187) and the station passes
/// <c>SetRadius(m_myStationType.GetRadius())</c> (stationIGC.cpp:249), where
/// <c>m_radiusMesh</c> is its maximum vertex distance from the model origin and
/// <c>radius = DataStationTypeIGC.Radius</c>. So the native uniform scale is
/// <c>radius / meshRadius</c> — identical to an asteroid's
/// <see cref="NativeAsteroidResolver.ScaleForRadius"/>.</para>
///
/// <para><b>Orientation.</b> Native renders with <c>m_bFlip = (options == 0)</c>
/// (thinggeo.cpp:1029) and stations load with options=0
/// (stationIGC.cpp:240), so the mesh is rotated <c>RY(π)</c> before the
/// orientation/scale (<c>Rotate(Vector(0,1,0),pi)</c>, thinggeo.cpp:1501-1503).
/// The port folds that flip in, exactly as
/// <see cref="NativeShipRenderer"/> does for ships.</para>
///
/// <para><b>Colour.</b> Native's station look comes from the model's own
/// texture alone (FindTexture, thinggeo.cpp:1038-1043); there is NO per-side
/// team tint applied to the station mesh in the native renderer (team is shown
/// via HUD/icons, not a re-coloured model). So, per the task's "follow native"
/// rule, the native station path uses the station's real texture with NO
/// friendly/enemy tint — the procedural placeholder's tint is dropped only for
/// the native path (the placeholder keeps its tint as a fallback).</para>
/// </remarks>
public static class NativeStationResolver
{
    /// <summary>
    /// Native's <c>RY(π)</c> mesh flip (thinggeo.cpp:1501-1503; stations load
    /// with <c>m_bFlip = (options==0) = true</c>, stationIGC.cpp:240 +
    /// thinggeo.cpp:1029). The same flip <see cref="NativeShipRenderer"/> uses.
    /// </summary>
    public static readonly Matrix NoseFlip = Matrix.CreateRotationY(MathHelper.Pi);

    /// <summary>Native defaults to CullModeCCW (engine/context.cpp:345) and
    /// uploads the file's indices unchanged (modeler.cpp:1637-1660;
    /// VBIBManager.cpp:503). Keep every group's authored winding, including
    /// concave bay walls; a radial centroid does not identify their front faces.</summary>
    public static ushort[] RenderIndices(ushort[] indices) => indices;

    /// <summary>Native TMeshGeo::GetRadius is the maximum vertex distance from
    /// the model origin (geometry.cpp:408-418); GroupGeo and LODGeo take the
    /// maximum child radius (geometry.cpp:1486-1496, 1748-1757). An enclosing
    /// bounding-box sphere changes the size of bays and their light decals.</summary>
    public static float MeshRadius(IReadOnlyList<MdlMesh> meshes, IReadOnlyList<MdlLodLevel>? lods = null)
    {
        float radiusSquared = 0f;
        void Include(IReadOnlyList<MdlMesh> group)
        {
            foreach (var mesh in group)
                for (int i = 0; i < mesh.Positions.Length; i += 3)
                {
                    var p = mesh.Positions;
                    radiusSquared = MathF.Max(radiusSquared, p[i] * p[i] + p[i+1] * p[i+1] + p[i+2] * p[i+2]);
                }
        }
        Include(meshes);
        if (lods is not null)
            foreach (var lod in lods) Include(lod.Meshes);
        return MathF.Sqrt(radiusSquared);
    }

    /// <summary>
    /// Native uniform scale for a station: <c>m_scale = radius / m_radiusMesh</c>
    /// (thinggeo.cpp:1187) with <c>radius = DataStationTypeIGC.Radius</c>
    /// (stationIGC.cpp:249). Guarded: non-positive radius or mesh radius → 1 (no
    /// scaling), matching the codebase's degrade-not-crash rule.
    /// </summary>
    public static float ScaleForRadius(float radius, float meshRadius)
    {
        if (radius <= 0f || meshRadius <= 0f) return 1f;
        return radius / meshRadius;
    }

    /// <summary>ThingGeo's initial parent texture: a meaningful station override,
    /// else the first TextureGeo found depth-first (thinggeo.cpp:1035-1043;
    /// geometry.cpp:1459-1473). Each group's TextureGeo subsequently binds its
    /// own image; this fallback affects only geometry without a texture wrapper.</summary>
    public static string? ResolveTextureStem(
        MdlFile model, string? stationTextureName, string modelName, Func<string, bool> fileExists)
    {
        if (!string.IsNullOrWhiteSpace(stationTextureName) && char.IsLetter(stationTextureName[0])
            && !string.Equals(stationTextureName, modelName, StringComparison.OrdinalIgnoreCase)
            && fileExists(stationTextureName))
            return stationTextureName;

        foreach (var mesh in model.Meshes)
            if (mesh.TextureStem is { } stem && fileExists(stem)) return stem;
        return null;
    }
}
