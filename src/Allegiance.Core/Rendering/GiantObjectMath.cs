namespace Allegiance.Core.Rendering;

using Allegiance.Core.Sim;

/// <summary>
/// P592 — pure geometry for the <b>GIANT sky object</b>: one texture drawn on
/// one <b>curved patch of the sky sphere</b> (~90° across by default), so a
/// whole face-on galaxy (our own Milky Way, or Andromeda) can read as a vast
/// single object on the black sky instead of a flat sticker.
///
/// <b>Why curved, not a billboard:</b> the P589 distant objects are small
/// camera-facing <see cref="SkyBillboard"/>s (≤3.5° half-angle) where a flat
/// quad is indistinguishable from a sphere patch. A ~90° object cannot be a
/// flat billboard — a single tangent square that large bows visibly at its
/// edges and looks warped as the camera pans. Instead the giant is a
/// tessellated <b>spherical cap</b>: every vertex lies on the sky sphere at a
/// fixed radius from the camera (camera-relative at infinity, no parallax,
/// exactly like the planet poster, <c>efimage.cpp:515-563</c>), so it curves
/// seamlessly with the sky and never stretches or shows an edge while panning.
///
/// <b>Mapping — the patch's own centre on its local equator (no pole
/// pinching):</b> the patch is parameterised in the tangent plane around the
/// centre direction <c>c</c> using a local orthonormal basis
/// <c>(right, upᵥ)</c>. Grid node <c>(t,s) ∈ [−1,1]²</c> maps to
/// <c>offset = right·(t·tan θ) + upᵥ·(s·tan θ)</c> and then
/// <c>dir = normalize(c + offset)</c> — a flat tangent square <b>lifted</b>
/// onto the sphere by normalisation. Because the square is lifted (a smooth,
/// locally-invertible map), the centre node lands exactly at the patch centre
/// and, crucially, <b>no grid lines converge to a single point</b>: there is no
/// latitude/longitude "pole" at the centre where neighbouring UV columns
/// collapse into a fan (the pinch a naive lat-long patch centred on a pole
/// produces). The centre of the patch maps to the centre of the texture
/// (u=v=0.5). UV stays in [0,1] everywhere — the patch is a single bounded
/// sheet, never a full dome and never wrapping.
///
/// Framework-neutral (only <see cref="NativeSkyMath.Vector3Unit"/>, no XNA in
/// Core) so the patch is verifiable headlessly: count, UV bounds (no wrap),
/// centre↔UV alignment, node distinctness (no pole pinch), and angular extent.
/// The renderer (<c>GiantObjectRenderer</c>) lifts these directions to XNA
/// vertices at the shell radius and draws one premultiplied texture.
/// </summary>
public static class GiantObjectMath
{
    /// <summary>One grid node of the patch: a unit direction on the sphere
    /// plus its texture coordinates (both in [0,1]).</summary>
    public readonly record struct PatchVertex(NativeSkyMath.Vector3Unit Dir, float U, float V);

    /// <summary>Compute the patch grid. Row-major: <c>index = i*(segments+1)+j</c>,
    /// <c>i</c> walks <c>u</c> (the local-right axis), <c>j</c> walks <c>v</c>
    /// (local-up axis). The grid is <c>(segments+1)×(segments+1)</c> nodes —
    /// <c>segments</c> cells per side, so <c>2·segments²</c> triangles.</summary>
    /// <param name="center">Unit direction of the patch centre (the object's
    /// placement direction). Normalised internally.</param>
    /// <param name="halfAngleRad">Angular radius of the patch, i.e. half the
    /// "degrees across" span (<c>span = 2·halfAngleRad</c>).</param>
    /// <param name="segments">Cells per side (&gt;0).</param>
    /// <param name="rotationRad">Roll of the texture about the patch centre
    /// (radians), applied in the tangent plane so a face-on spiral can be
    /// tilted to match its natural orientation.</param>
    public static PatchVertex[] BuildPatch(NativeSkyMath.Vector3Unit center, float halfAngleRad, int segments, float rotationRad = 0f)
    {
        if (segments < 1) segments = 1;
        var c = Normalize(center);
        var up = MathF.Abs(c.Y) > 0.997f
            ? new NativeSkyMath.Vector3Unit(0f, 0f, 1f)   // vertical centre: pick Z as up
            : new NativeSkyMath.Vector3Unit(0f, 1f, 0f);
        var right = Normalize(Cross(up, c));
        var upv = Cross(c, right);
        float tanH = MathF.Tan(Math.Max(1e-4f, halfAngleRad));
        float cr = MathF.Cos(rotationRad), sr = MathF.Sin(rotationRad);

        int n = segments + 1;
        var verts = new PatchVertex[n * n];
        int k = 0;
        for (int i = 0; i < n; i++)
        {
            float t = (i / (float)segments - 0.5f) * 2f;       // [-1,1]
            float u = (t + 1f) * 0.5f;
            for (int j = 0; j < n; j++)
            {
                float s = (j / (float)segments - 0.5f) * 2f;   // [-1,1]
                float v = (s + 1f) * 0.5f;
                float tr = t * cr - s * sr;                    // roll in tangent plane
                float srv = t * sr + s * cr;
                var offset = new NativeSkyMath.Vector3Unit(
                    (right.X * tr + upv.X * srv) * tanH,
                    (right.Y * tr + upv.Y * srv) * tanH,
                    (right.Z * tr + upv.Z * srv) * tanH);
                verts[k++] = new PatchVertex(Normalize(new NativeSkyMath.Vector3Unit(
                    c.X + offset.X, c.Y + offset.Y, c.Z + offset.Z)), u, v);
            }
        }
        return verts;
    }

    /// <summary>Normalise a vector (NaN-safe: a zero/NaN input returns +Z).</summary>
    private static NativeSkyMath.Vector3Unit Normalize(NativeSkyMath.Vector3Unit v)
    {
        float len = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        if (len < 1e-6f || float.IsNaN(len)) return new NativeSkyMath.Vector3Unit(0f, 0f, 1f);
        return new NativeSkyMath.Vector3Unit(v.X / len, v.Y / len, v.Z / len);
    }

    private static NativeSkyMath.Vector3Unit Cross(NativeSkyMath.Vector3Unit a, NativeSkyMath.Vector3Unit b)
        => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
}
