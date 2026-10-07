using System;
using Allegiance.Core;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P567 — the pure, GPU-free rules for rendering an asteroid from its ORIGINAL
/// native <c>.mdl</c> rock mesh + native texture (the P566 native-mesh pipeline
/// applied to asteroids), instead of the shared imported/procedural
/// <see cref="AsteroidRenderer"/> variants. Everything here is CPU-only math /
/// name resolution so it is unit-testable headless, mirroring how
/// <see cref="NativeShipResolver"/> keeps the ship rules pure.
/// </summary>
/// <remarks>
/// <para><b>Per-instance native model + texture.</b> Every asteroid carries its
/// own <c>modelName</c> / <c>textureName</c> in the map record
/// (<c>AsteroidDef</c>, <c>src/Igc/igc.h:1709-1720</c>; loaded in
/// <c>CasteroidIGC::Initialize</c>, <c>src/Igc/asteroidigc.cpp:33-34</c> —
/// <c>Load(0, m_asteroidDef.modelName, m_asteroidDef.textureName, ...)</c>). The
/// port renders each asteroid from ITS OWN model and texture (the <c>.mdl</c>
/// mesh + its decoded surface), cached per model, instead of picking a shared
/// variant by <c>AsteroidId</c>.</para>
///
/// <para><b>Winding / culling.</b> Exactly the P570 conclusion for ships: the
/// native <c>.mdl</c> index stream, shipped RAW, keeps the outside faces under
/// MonoGame's PERSPECTIVE projection with <c>CullCounterClockwiseFace</c>. This
/// is the empirical, real-window-verified convention (P570's
/// <c>P570NativeCullingTests</c>), and we verified it holds for every rock mesh:
/// the native raw geometric normals are ~100% <em>inward</em>-from-centroid for
/// the classic rock meshes (bgrnd03/05/51/52/53/56, drgrock, tacrock — measured
/// 0% outward) yet render solid under that culling. The imported path's
/// <see cref="ImportedRockMeshLoader"/> "winding fix" instead pushed rocks to
/// ~100% outward — the exact OPPOSITE of native — which is why the imported
/// variants rendered inside-out. See <see cref="OutwardGeometricFraction"/> and
/// the <c>ImportedRockMeshLoader</c> change (now anchored to native's polarity).</para>
///
/// <para><b>Scale.</b> Native sizes every model to its gameplay collision radius:
/// <c>ThingGeo::SetRadius</c> sets <c>m_scale = radius / m_radiusMesh</c>
/// (thinggeo.cpp:1185-1187) and the asteroid passes
/// <c>SetRadius((float)m_asteroidDef.radius)</c> (asteroidigc.cpp:38), where
/// <c>m_radiusMesh</c> is the mesh's bounding-sphere radius. So the native
/// uniform scale is <c>radius / meshRadius</c>.</para>
///
/// <para><b>Orientation.</b> Native renders with <c>m_bFlip = (options == 0)</c>
/// (thinggeo.cpp:1029) and asteroids load with options=0 (asteroidigc.cpp:33),
/// so the mesh is rotated <c>RY(π)</c> before the orientation/scale
/// (<c>Rotate(Vector(0,1,0),pi)</c>, thinggeo.cpp:1501-1503). The port folds that
/// flip in, exactly as <see cref="NativeShipRenderer"/> does for ships.</para>
///
/// <para><b>Colour.</b> Native's rock look comes from the model's own texture
/// alone (FindTexture, thinggeo.cpp:1038-1043); there is NO per-capability tint
/// in the native renderer (the ore type is gameplay-only). So the native path
/// uses the rock's real texture with no <see cref="RockTextureFactory"/>
/// capability tint — matching native, per the task's "follow native" rule.</para>
/// </remarks>
public static class NativeAsteroidResolver
{
    /// <summary>
    /// Native's <c>RY(π)</c> mesh flip (thinggeo.cpp:1501-1503; asteroids load
    /// with <c>m_bFlip = (options==0) = true</c>, asteroidigc.cpp:33 +
    /// thinggeo.cpp:1029). A proper rotation (no handedness change); the port's
    /// <c>NativeShipRenderer</c> uses the identical flip.
    /// </summary>
    public static readonly Matrix NoseFlip = Matrix.CreateRotationY(MathHelper.Pi);

    /// <summary>
    /// P570 — the index stream shipped to the GPU for a native rock mesh. The raw
    /// DirectX (native) winding is the correct front-face winding under MonoGame's
    /// PERSPECTIVE projection, so it ships unchanged and <c>CullCounterClockwiseFace</c>
    /// keeps the outside (the same decision <see cref="NativeShipResolver.RenderIndices"/>
    /// makes for ships). Per-vertex normals are unchanged (already correct, P566).
    /// </summary>
    public static ushort[] RenderIndices(ushort[] indices) => indices;

    /// <summary>
    /// Native uniform scale for an asteroid: <c>m_scale = radius / m_radiusMesh</c>
    /// (thinggeo.cpp:1187) with <c>radius = (float)m_asteroidDef.radius</c>
    /// (asteroidigc.cpp:38). Guarded: non-positive radius or mesh radius → 1 (no
    /// scaling), matching the codebase's degrade-not-crash rule.
    /// </summary>
    public static float ScaleForRadius(float radius, float meshRadius)
    {
        if (radius <= 0f || meshRadius <= 0f) return 1f;
        return radius / meshRadius;
    }

    /// <summary>
    /// The native texture STEM (the <c>.mdl</c> basename to decode, e.g. <c>bgrnd05bmp</c>)
    /// for an asteroid, resolving exactly as native. Asteroids pass
    /// <c>Load(0, modelName, textureName, ...)</c> (asteroidigc.cpp:33); the wire
    /// <c>textureName</c> is empty in the corpus, so native falls to the model's own
    /// imported texture — <c>m_pgeoMesh-&gt;FindTexture()</c> (thinggeo.cpp:1038-1043) —
    /// the first namespace whose <c>&lt;ns&gt;.mdl</c> exists (= <c>modelName+"bmp"</c>
    /// for the rock meshes, e.g. <c>bgrnd05.mdl</c> imports <c>bgrnd05bmp</c>). If a
    /// real <c>textureName</c> override were ever present it would win
    /// (trekigc.cpp:1837-1839). Returns null when no texture unit resolves → caller
    /// renders lit-solid (never invisible).
    /// </summary>
    public static string? ResolveTextureStem(
        MdlFile model, string? overrideTextureName, string modelName, Func<string, bool> fileExists)
        => NativeShipResolver.ResolveTextureStem(model, overrideTextureName, modelName, fileExists);

    /// <summary>
    /// P567 — classify a mesh's triangle winding against its centroid:
    /// the fraction (0..1) of triangles whose geometric normal
    /// <c>cross(v1−v0, v2−v0)</c> points "outward from the centroid" (dot &gt; 0 with
    /// the face-centre direction). Measured on the native rock meshes this is
    /// <b>~0</b> (0% outward for bgrnd03/05/51/52/53/56/drgrock/tacrock; bgrnd55 6.7%;
    /// bgrnd57 47.3% — a strongly concave rock) yet those same raw-winding meshes
    /// render SOLID under <c>CullCounterClockwiseFace</c> (P570). The imported path's
    /// <see cref="ImportedRockMeshLoader.Finalize"/> was flipping rocks to ~100%
    /// outward — the opposite of native — which is the inside-out root cause. This
    /// pure metric backs the failing-first tests and the imported-path fix's anchor.
    /// </summary>
    public static float OutwardGeometricFraction(float[] positions, int[] indices)
    {
        int vcount = positions.Length / 3;
        if (vcount == 0 || indices.Length < 3) return 0f;

        double cx = 0, cy = 0, cz = 0;
        for (int v = 0; v < vcount; v++)
        {
            cx += positions[3 * v]; cy += positions[3 * v + 1]; cz += positions[3 * v + 2];
        }
        cx /= vcount; cy /= vcount; cz /= vcount;

        int outward = 0, total = 0;
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            int i0 = indices[t], i1 = indices[t + 1], i2 = indices[t + 2];
            if (i0 < 0 || i1 < 0 || i2 < 0) continue;
            double ax = positions[3 * i0] - cx, ay = positions[3 * i0 + 1] - cy, az = positions[3 * i0 + 2] - cz;
            double bx = positions[3 * i1] - cx, by = positions[3 * i1 + 1] - cy, bz = positions[3 * i1 + 2] - cz;
            double dx = positions[3 * i2] - cx, dy = positions[3 * i2 + 1] - cy, dz = positions[3 * i2 + 2] - cz;
            double ux = bx - ax, uy = by - ay, uz = bz - az;
            double vx = dx - ax, vy = dy - ay, vz = dz - az;
            double gx = uy * vz - uz * vy;
            double gy = uz * vx - ux * vz;
            double gz = ux * vy - uy * vx;
            double fx = (ax + bx + dx) / 3, fy = (ay + by + dy) / 3, fz = (az + bz + dz) / 3;
            if (gx * fx + gy * fy + gz * fz >= 0) outward++;
            total++;
        }
        return total == 0 ? 0f : (float)outward / total;
    }
}
