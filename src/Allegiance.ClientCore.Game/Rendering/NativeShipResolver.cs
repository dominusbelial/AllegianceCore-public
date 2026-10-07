using System;
using System.Collections.Generic;
using Allegiance.Core;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P566 — the pure, GPU-free rules for rendering a hull from its ORIGINAL native
/// <c>.mdl</c> mesh + native texture (instead of the P287 Meshy content models).
/// Everything here is CPU-only math / name resolution so it is unit-testable
/// headless, mirroring how <see cref="ShipRenderScale"/> / <see cref="MdlFile"/>
/// keep their pure parts apart from the graphics device.
///
/// <para><b>Winding.</b> The native <c>.mdl</c> index stream is authored for the
/// DirectX left-handed engine (clockwise front faces); MonoGame's default
/// <c>CullCounterClockwise</c> expects counter-clockwise front faces, so each
/// triangle's indices are reversed (<c>a b c</c> → <c>c b a</c>) — the same
/// reversal the reference OBJ writer proved (wiki
/// <c>allegiance-mdl-model-format</c>: "write each face's three indices in
/// reverse order (c b a instead of a b c)"). The per-vertex normals are already
/// correct (same wiki), so only the winding is touched.</para>
///
/// <para><b>Scale.</b> Native sizes every ship to its gameplay collision radius:
/// <c>ThingGeo::SetRadius</c> sets <c>m_scale = radius / m_radiusMesh</c>
/// (thinggeo.cpp:1185-1187) and the ship passes <c>SetRadius(0.5 * Length)</c>
/// (shipIGC.cpp:1419), where <c>m_radiusMesh</c> is the merged bounding-sphere
/// radius of the raw mesh. So the native uniform scale is
/// <c>(0.5 * HullType.Length) / meshRadius</c> — the .mdl is authored near-native
/// size, so this is ≈1 for a correctly-authored ship (unlike the ~100× Meshy
/// bone bake that <see cref="ShipRenderScale"/> had to compensate).</para>
///
/// <para><b>Orientation.</b> Native renders ships with <c>m_bFlip = (options == 0)</c>
/// (thinggeo.cpp:1029) and ships load with options=0 (shipIGC.cpp:1412), so the mesh
/// is rotated 180° about Y before the orientation/scale (<c>Rotate(Vector(0,1,0),pi)</c>,
/// thinggeo.cpp:1501-1503). The .mdl long/nose axis is model +Z (the P544 beams span
/// their radius along +Z), so after the RY(π) flip the nose lies along −Z — which is
/// exactly this port's forward convention (<see cref="PoseConverter.ToXnaRotation"/>
/// maps local −Z → world forward). Mirroring native's RY(π) therefore reproduces
/// native's nose-forward orientation through the existing world matrix.</para>
///
/// <para><b>Texture.</b> Native resolves a ship's texture as: the hull's
/// <c>TextureName</c> override when it is present/meaningful
/// (<c>LoadModel</c> nulls it when it equals <c>modelName</c> or is non-alpha,
/// trekigc.cpp:1837-1839), else the model's OWN imported texture —
/// <c>m_pgeoMesh-&gt;FindTexture()</c> (thinggeo.cpp:1038-1043). The model's
/// imported texture is its texture namespace (e.g. <c>fig05.mdl</c> imports
/// <c>fig05bmp</c>; <c>wc_gcfig.mdl</c> imports the shared <c>fig07bmp</c>;
/// <c>utl18a.mdl</c> imports the shared <c>utl18bmp</c> — note NOT
/// <c>utl18abmp</c>, the wiki's explicit caution against assuming
/// <c>&lt;modelname&gt;bmp</c>): a texture is its own compiled unit imported by
/// name, shared across model variants.</para>
/// </summary>
public static class NativeShipResolver
{
    /// <summary>
    /// Reverse each triangle's indices (<c>a b c</c> → <c>c b a</c>) so the
    /// DirectX clockwise-front native winding becomes MonoGame
    /// counter-clockwise-front. Odd permutation ⇒ front-face orientation flips;
    /// per-vertex normals are untouched. Degenerate/partial trailing indices are
    /// carried through unchanged (a triangle list is always a whole multiple of 3).
    /// </summary>
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
    /// The merged bounding-sphere radius of a set of <c>.mdl</c> meshes — native's
    /// <c>m_radiusMesh</c> (<c>m_pgeoMesh-&gt;GetRadius(identity)</c>,
    /// thinggeo.cpp:1053). Same merge technique as <see cref="ShipRenderScale.RenderRadius"/>,
    /// applied to the RAW native coordinates (no bone bake — native draws them at identity).
    /// </summary>
    public static float MergedRadius(IReadOnlyList<MdlMesh> meshes)
    {
        BoundingSphere? acc = null;
        foreach (var m in meshes)
        {
            if (m.VertexCount == 0) continue;
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
        return acc?.Radius ?? 0f;
    }

    /// <summary>
    /// Native uniform scale for a ship: <c>m_scale = radius / m_radiusMesh</c>
    /// (thinggeo.cpp:1187) with <c>radius = 0.5 * HullType.Length</c>
    /// (shipIGC.cpp:1419). Guarded: non-positive length or radius → 1 (no scaling),
    /// matching the codebase's degrade-not-crash rule.
    /// </summary>
    public static float NativeScaleForLength(float hullLength, float meshRadius)
    {
        if (hullLength <= 0f || meshRadius <= 0f) return 1f;
        return (0.5f * hullLength) / meshRadius;
    }

    /// <summary>
    /// The native texture STEM (the <c>.mdl</c> basename to decode, e.g.
    /// <c>fig05bmp</c>) for a hull, resolving exactly as native:
    /// <list type="number">
    ///   <item>the hull <c>TextureName</c> override, when it is a meaningful
    ///   texture name (present, alpha-leading, differs from <c>modelName</c>)
    ///   and its texture unit exists — mirroring <c>LoadModel</c>'s override +
    ///   null-out (trekigc.cpp:1837-1845);</item>
    ///   <item>else the model's OWN imported texture namespace
    ///   (<c>m_pgeoMesh-&gt;FindTexture()</c>, thinggeo.cpp:1038-1043) — the
    ///   first namespace of <c>model</c> whose <c>&lt;ns&gt;.mdl</c> texture
    ///   unit exists. This is the faithful rule (the wiki's caution: do NOT
    ///   assume <c>&lt;modelname&gt;bmp</c> — e.g. <c>utl18a → utl18bmp</c>,
    ///   <c>wc_gcfig → fig07bmp</c>, shared textures).</item>
    /// </list>
    /// Returns null when no texture unit can be resolved → caller renders
    /// untextured/lit-solid (never invisible). <paramref name="fileExists"/> tests
    /// whether <c>&lt;stem&gt;.mdl</c> exists in the artwork source.
    /// </summary>
    public static string? ResolveTextureStem(
        MdlFile model, string? hullTextureName, string modelName, Func<string, bool> fileExists)
    {
        // Native LoadModel: a texture override is honoured only when it is a real
        // alpha-leading name that differs from the model (trekigc.cpp:1837-1839).
        string? hullTex = NormaliseTextureName(hullTextureName, modelName);
        if (hullTex is not null && fileExists(hullTex))
            return hullTex;

        // Fall back to the model's own imported texture namespace: the first
        // namespace whose <ns>.mdl texture unit exists (its real import).
        foreach (var ns in model.Namespaces)
            if (fileExists(ns))
                return ns;
        return null;
    }

    /// <summary>Normalise a hull TextureName to a usable override stem, or null
    /// (native nulls it when non-alpha or equal to the model name,
    /// trekigc.cpp:1837-1839).</summary>
    private static string? NormaliseTextureName(string? name, string modelName)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var t = name.Trim();
        if (t.Length == 0) return null;
        if (!char.IsLetter(t[0])) return null;                  // !isalpha(textureName[0])
        if (string.Equals(t, modelName, StringComparison.OrdinalIgnoreCase)) return null;
        return t;
    }

    /// <summary>
    /// P566 — the local-space aft-engine-nozzle anchor of a ship from its native
    /// <c>FrameData</c>: the first frame whose name mentions <c>rocket</c> /
    /// <c>thrust</c> / <c>smoke</c> (the frames native's own <c>AddThrustParticles</c>
    /// keys on, thinggeo.cpp:786-794). Falls back to a guessed aft-centre
    /// <c>(0, 0, fallbackRadius)</c> (the pre-P566 client guess) when no such frame
    /// exists, so a missing frame degrades gracefully. The offset is in the model's
    /// local frame; apply the native RY(π) flip + the ship orientation to get world.
    /// </summary>
    public static Vector3 AftNozzleLocalOffset(IReadOnlyList<MdlFrame> frames, float fallbackRadius)
    {
        foreach (var f in frames)
        {
            if (f.Name.IndexOf("rocket", StringComparison.OrdinalIgnoreCase) >= 0
                || f.Name.IndexOf("thrust", StringComparison.OrdinalIgnoreCase) >= 0
                || f.Name.IndexOf("smoke", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new Vector3(f.Position[0], f.Position[1], f.Position[2]);
            }
        }
        return new Vector3(0f, 0f, fallbackRadius);
    }
}
