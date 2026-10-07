using System;
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
/// <para><b>Winding / culling.</b> Exactly the P570 conclusion for ships and
/// rocks: the native <c>.mdl</c> index stream, shipped RAW, keeps the outside
/// faces under MonoGame's PERSPECTIVE projection with
/// <c>CullCounterClockwiseFace</c> (see <see cref="RenderIndices"/> and the
/// <c>P570NativeCullingTests</c>). Per-vertex normals are unchanged.</para>
///
/// <para><b>Scale.</b> Native sizes every model to its gameplay collision
/// radius: <c>ThingGeo::SetRadius</c> sets <c>m_scale = radius / m_radiusMesh</c>
/// (thinggeo.cpp:1185-1187) and the station passes
/// <c>SetRadius(m_myStationType.GetRadius())</c> (stationIGC.cpp:249), where
/// <c>m_radiusMesh</c> is the mesh's merged bounding-sphere radius and
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

    /// <summary>
    /// P564/P570 — the index stream shipped to the GPU for a native station mesh,
    /// normalised so <c>CullCounterClockwiseFace</c> keeps the OUTSIDE under
    /// MonoGame's PERSPECTIVE projection for ANY station model.
    /// <see cref="RenderIndices"/> ships the raw DirectX winding unchanged (the
    /// P570 conclusion for ships and rocks — the raw winding is correct under
    /// perspective), but the shipped station corpus is not uniform: a Garrison
    /// (<c>ss24a</c>) and an Outpost (<c>ss92</c>) were measured to wind OPPOSITE on
    /// their near surface under the game transform. Shipping raw everywhere would
    /// make one of them render inside-out/see-through — exactly the per-model defect
    /// class this task hunts. <see cref="OutsideIndices"/> therefore keeps the raw
    /// winding when the near surface is already kept by <c>CullCounterClockwiseFace</c>
    /// (<see cref="OutsideKeptFraction"/> ≥ 0.5) and reverses it otherwise, so every
    /// station renders solid. Per-vertex normals are unchanged.
    /// </summary>
    public static ushort[] RenderIndices(ushort[] indices) => indices;

    /// <summary>Reverse each triangle's indices (<c>a b c</c> → <c>c b a</c>) —
    /// the same reversal <see cref="NativeShipResolver.CorrectWinding"/> applies.</summary>
    public static ushort[] CorrectWinding(ushort[] indices)
    {
        var outIdx = new ushort[indices.Length];
        int whole = indices.Length - (indices.Length % 3);
        for (int t = 0; t < whole; t += 3)
        {
            outIdx[t] = indices[t + 2];
            outIdx[t + 1] = indices[t + 1];
            outIdx[t + 2] = indices[t];
        }
        for (int k = whole; k < indices.Length; k++)
            outIdx[k] = indices[k];
        return outIdx;
    }

    /// <summary>
    /// The index stream that keeps the station's OUTSIDE solid under the game
    /// perspective: the raw native winding when its near surface is already kept by
    /// <c>CullCounterClockwiseFace</c> (≥50%), else the reversed winding. Deterministic
    /// per model, so a station never renders inside-out regardless of authoring polarity.
    /// </summary>
    public static ushort[] OutsideIndices(MdlMesh mesh)
        => OutsideKeptFraction(mesh) >= 0.5f ? mesh.Indices : CorrectWinding(mesh.Indices);

    /// <summary>
    /// The fraction of a model's camera-facing near-surface triangles kept by
    /// <c>CullCounterClockwiseFace</c> under the game transform (nose flip + a camera
    /// 600 units ahead + a 50° FOV perspective). The same pure measure the
    /// <see cref="NativeStationRenderer"/>'s build uses to pick the winding. A fraction
    /// near 1 means the raw winding is solid; near 0 means it is reversed (would be
    /// inside-out) and must be corrected.
    /// </summary>
    public static float OutsideKeptFraction(MdlMesh mesh)
    {
        if (mesh.VertexCount == 0 || mesh.Indices.Length < 3) return 0f;
        var pos = mesh.Positions;
        var idx = mesh.Indices;

        var noseFlip = Matrix.CreateRotationY(MathHelper.Pi);
        var world = Matrix.CreateTranslation(new Vector3(0f, 0f, -600f));
        var local = noseFlip * world;
        var view = Matrix.CreateLookAt(Vector3.Zero, new Vector3(0f, 0f, -600f), Vector3.Up);
        var proj = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(50f), 16f / 9f, 0.1f, 60000f);
        var localViewProj = local * view * proj;

        Vector3 meshCentroid = Vector3.Zero;
        for (int v = 0; v < mesh.VertexCount; v++)
            meshCentroid += new Vector3(pos[3 * v], pos[3 * v + 1], pos[3 * v + 2]);
        meshCentroid /= Math.Max(1, mesh.VertexCount);

        double alignSum = 0;
        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            int ia = idx[t], ib = idx[t + 1], ic = idx[t + 2];
            if (ia < 0 || ib < 0 || ic < 0) continue;
            var pa = V3(pos, ia); var pb = V3(pos, ib); var pc = V3(pos, ic);
            var n = Vector3.Cross(pb - pa, pc - pa);
            alignSum += Vector3.Dot(n, (pa + pb + pc) / 3f - meshCentroid);
        }
        float globalSign = alignSum >= 0 ? 1f : -1f;

        int kept = 0, culled = 0;
        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            int ia = idx[t], ib = idx[t + 1], ic = idx[t + 2];
            if (ia < 0 || ib < 0 || ic < 0) continue;
            var pa = V3(pos, ia); var pb = V3(pos, ib); var pc = V3(pos, ic);
            var wa = Vector3.Transform(pa, local);
            var wb = Vector3.Transform(pb, local);
            var wc = Vector3.Transform(pc, local);
            var outward = globalSign * Vector3.TransformNormal(Vector3.Cross(pb - pa, pc - pa), local);
            var centroid = (wa + wb + wc) / 3f;
            bool cameraFacing = Vector3.Dot(outward, -centroid) > 0f;
            if (!cameraFacing) continue;

            Vector4 ca = Vector4.Transform(new Vector4(pa, 1f), localViewProj);
            Vector4 cb = Vector4.Transform(new Vector4(pb, 1f), localViewProj);
            Vector4 cc = Vector4.Transform(new Vector4(pc, 1f), localViewProj);
            if (ca.W <= 1e-6f || cb.W <= 1e-6f || cc.W <= 1e-6f) continue;
            float ax = ca.X / ca.W, ay = ca.Y / ca.W;
            float bx = cb.X / cb.W, by = cb.Y / cb.W;
            float cx = cc.X / cc.W, cy = cc.Y / cc.W;
            float signedArea = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
            if (signedArea < 0f) kept++; else culled++;
        }
        int total = kept + culled;
        return total == 0 ? 0f : (float)kept / total;
    }

    private static Vector3 V3(float[] p, int v) => new(p[3 * v], p[3 * v + 1], p[3 * v + 2]);

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

    /// <summary>
    /// The native texture STEM (the <c>.mdl</c> basename to decode) for a station
    /// model. Stations call <c>Load(0, modelName, textureName, ...)</c>
    /// (stationIGC.cpp:240). A real station <c>TextureName</c> override wins when
    /// meaningful (trekigc.cpp:1837-1839). Otherwise a station prefers its OWN
    /// exterior skin <c>&lt;modelName&gt;bmp</c> (e.g. <c>ss24abmp</c>) over the
    /// generic "first namespace" ship rule — a deliberate, documented station
    /// deviation: station <c>.mdl</c> files embed MULTIPLE texture namespaces
    /// (exterior skin, hangar-bay interior, background), and the ship rule's
    /// "first existing namespace" picks the wrong one for a Garrison
    /// (<c>ss24a.mdl</c> lists <c>bgrnd03bmp</c> before <c>ss24abmp</c>) leaving the
    /// station painted as a background bitmap. The station's exterior is always
    /// <c>&lt;stem&gt;bmp</c> in the corpus (ss24abmp, ss92bmp, ss27bmp, ss300bmp,
    /// ss101bmp…), so prefer it when present, else fall back to
    /// <see cref="NativeShipResolver.ResolveTextureStem"/>. Returns null when no
    /// texture unit resolves → caller renders lit-solid (never invisible).
    /// </summary>
    public static string? ResolveTextureStem(
        MdlFile model, string? stationTextureName, string modelName, Func<string, bool> fileExists)
    {
        // A real station TextureName override wins (trekigc.cpp:1837-1839).
        if (!string.IsNullOrWhiteSpace(stationTextureName) && char.IsLetter(stationTextureName[0])
            && !string.Equals(stationTextureName, modelName, StringComparison.OrdinalIgnoreCase)
            && fileExists(stationTextureName))
            return stationTextureName;

        // Prefer the station's own exterior skin <modelName>bmp when present.
        string ownSkin = modelName + "bmp";
        if (fileExists(ownSkin))
            return ownSkin;

        // Fall back to the ship rule (first existing imported namespace).
        return NativeShipResolver.ResolveTextureStem(model, null, modelName, fileExists);
    }
}
