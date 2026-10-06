namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Generates the deterministic, camera-facing radial-gradient GLOW DISC that
/// anchors the aleph (warp gate) — the "bright spot in the center" of native's
/// own flavor text (<c>training/mission4.cpp:494</c>), rendered literally.
/// This is Layer 1 of the P341 aleph redesign (see the kimi-k3 consultation
/// log for the diagnosis): a hollow hourglass surface of revolution has no
/// angle-independent silhouette, so the warp's single most important element
/// — its bright core — must NOT be geometry whose apparent size collapses when
/// viewed obliquely. A flat disc carries the whole read with constant size and
/// brightness from every approach angle by construction.
/// </summary>
/// <remarks>
/// <para><b>Pure logic, no MonoGame on the surface</b> — the same discipline as
/// <see cref="WarpMeshFactory"/>: it emits plain float arrays and a triangle-list
/// index buffer packaged in the shared <see cref="WarpMesh"/> record, so the
/// geometry is unit-testable without a graphics device. The <b>billboarding</b>
/// (keeping the disc camera-facing) is NOT baked into the mesh — a disc is a
/// flat XY-plane fan whose normal is local +Z; it is the renderer's job to
/// orient that normal toward the camera each frame (see
/// <see cref="WarpRenderer.Draw"/>'s billboard composition). This factory only
/// produces the flat, axis-aligned, unit-radius radial gradient disc.</para>
/// <para><b>Radial gradient:</b> a white-cyan core (u=0, the warp's white-hot
/// center) fading smoothly to a transparent deep-navy rim (u=1, alpha → 0), so
/// under the renderer's additive blend the disc reads as a soft self-lit glow
/// whose alpha-weighted center burns hottest and whose edge vanishes into the
/// background — structurally identical to the funnel's dark-rim/bright-center
/// palette, but in 2D where it cannot collapse.</para>
/// <para><b>Determinism is the contract:</b> same params &rarr; byte-identical
/// mesh across calls and runs. Only <c>MathF</c> arithmetic, no
/// <c>System.Random</c>, no time.</para>
/// </remarks>
public static class WarpCoreMeshFactory
{
    /// <summary>Number of concentric rings in the disc (row 0 is the center
    /// point, the outermost row is the unit rim).</summary>
    public const int DefaultRings = 12;

    /// <summary>Vertices per ring around the circumference.</summary>
    public const int DefaultSegments = 32;

    /// <summary>Unit disc radius; the renderer scales the whole disc by the
    /// real per-instance <c>DataWarpIGC.Radius</c> (through the same
    /// <see cref="WarpRenderer.VisualScaleMultiplier"/> the funnel uses), so
    /// the core's radius in world units equals the funnel's rim radius.</summary>
    public const float DefaultRadius = 1f;

    /// <summary>Exponent applied to the rim-ward fraction for the ALPHA channel:
    /// <c>alpha = (1 − u)^Falloff</c>. &gt;1 concentrates the opacity near the
    /// bright core and makes the fade to the transparent rim gentler, so the
    /// disc reads as a glow rather than a hard-edged coin.</summary>
    private const float Falloff = 2.0f;

    // White-cyan core (u=0) — the warp's white-hot center, matching the funnel's
    // tip colour (WarpMeshFactory TipR/G/B) so the two elements agree where they
    // overlap.
    private const float CoreR = 1.000f, CoreG = 0.984f, CoreB = 1.000f;

    // Transparent deep-navy rim (u=1) — same blue-family pole as the funnel's
    // rims (blue dominant over red), faded to alpha 0 so the disc's edge never
    // reads as a hard boundary under additive blend.
    private const float RimR = 0.084f, RimG = 0.228f, RimB = 0.600f;

    /// <summary>
    /// Build the flat radial-gradient glow-disc mesh in the local XY plane
    /// (normal = +Z, so a +Z-facing billboard in the renderer makes it face the
    /// camera).
    /// </summary>
    /// <param name="rings">Concentric rings including the center point (≥ 2).</param>
    /// <param name="segments">Vertices per ring (≥ 3).</param>
    /// <param name="radius">Disc radius in mesh units (default 1).</param>
    public static WarpMesh Generate(
        int rings = DefaultRings,
        int segments = DefaultSegments,
        float radius = DefaultRadius)
    {
        if (rings < 2)
            throw new ArgumentOutOfRangeException(nameof(rings), "rings must be >= 2");
        if (segments < 3)
            throw new ArgumentOutOfRangeException(nameof(segments), "segments must be >= 3");
        if (radius <= 0f)
            throw new ArgumentOutOfRangeException(nameof(radius), "radius must be > 0");

        int vertexCount = rings * segments;
        var pos = new float[vertexCount * 3];
        var col = new float[vertexCount * 4];
        var uv = new float[vertexCount * 2];

        for (int ring = 0; ring < rings; ring++)
        {
            // u ∈ [0,1]: 0 = the center point, 1 = the unit rim.
            float u = rings == 1 ? 0f : (float)ring / (rings - 1);
            float r = radius * u;

            // Colour lerps linearly core → rim; alpha falls off to 0 at the rim.
            float alpha = MathF.Pow(MathF.Max(0f, 1f - u), Falloff);
            float cr = CoreR + (RimR - CoreR) * u;
            float cg = CoreG + (RimG - CoreG) * u;
            float cb = CoreB + (RimB - CoreB) * u;

            for (int j = 0; j < segments; j++)
            {
                float angle = MathF.Tau * j / segments;
                float x = r * MathF.Cos(angle);
                float y = r * MathF.Sin(angle);

                int vi = ring * segments + j;
                pos[vi * 3 + 0] = x;
                pos[vi * 3 + 1] = y;
                pos[vi * 3 + 2] = 0f;

                col[vi * 4 + 0] = cr;
                col[vi * 4 + 1] = cg;
                col[vi * 4 + 2] = cb;
                col[vi * 4 + 3] = alpha;

                // UVs (unused by the P562 textured path, but the WarpMesh record
                // carries them; harmless radial mapping).
                uv[vi * 2 + 0] = (float)j / segments;
                uv[vi * 2 + 1] = u;
            }
        }

        // Concentric grid triangulation between consecutive rings (the same
        // quad-split scheme as WarpMeshFactory). Ring 0 is the degenerate center
        // point (all `segments` vertices coincide at the origin), so the ring 0↔1
        // band fans the innermost disk closed. Winding is arbitrary — the renderer
        // draws with back-face culling disabled, exactly like the funnel.
        var indices = new List<int>((rings - 1) * segments * 6);
        for (int ring = 0; ring < rings - 1; ring++)
        {
            int row0 = ring * segments;
            int row1 = (ring + 1) * segments;
            for (int j = 0; j < segments; j++)
            {
                int j2 = (j + 1) % segments;
                int v00 = row0 + j;
                int v01 = row0 + j2;
                int v10 = row1 + j;
                int v11 = row1 + j2;
                indices.Add(v00); indices.Add(v10); indices.Add(v11);
                indices.Add(v00); indices.Add(v11); indices.Add(v01);
            }
        }

        return new WarpMesh(pos, col, uv, indices.ToArray());
    }
}
