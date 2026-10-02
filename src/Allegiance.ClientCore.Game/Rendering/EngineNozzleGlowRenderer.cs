using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// One ship's engine-nozzle glow decal for a frame: its aft-centre world position
/// (already offset to the nozzle), the quad's half-extent in world units, and the
/// linear RGB tint from <see cref="EngineNozzleGlow.Compute"/>.
/// </summary>
public readonly record struct EngineGlowView(SimVec Position, float HalfExtent, Vector3 Rgb);

/// <summary>
/// Draws the engine-nozzle glow (P365) — a real flat glow-decal sprite at every
/// thrusting visible ship's aft nozzle — as a camera-facing soft quad tinted by
/// the native HSB thrust math (<see cref="EngineNozzleGlow"/>). Faithful port of
/// native <c>ThingGeoImpl::RenderGlow</c> (<c>effect/thinggeo.cpp:1419-1443</c>):
/// a static decal image (native <c>AWF_EFFECT_AFTERBURNER_GLOW</c>) drawn at each
/// "thrust"-named hardpoint frame, scaled by <c>m_scaleGlow·m_scale</c> and tinted
/// by <c>m_colorGlow</c>. Distinct from P358's exhaust particle plume (a separate
/// <c>smoke</c>/<c>rocket</c>-hardpoint effect) — this is the static nozzle bloom.
/// </summary>
/// <remarks>
/// <para><b>Deliberate port substitutions (all documented, matching P358's
/// conclusions).</b> (1) This port renders Meshy-regenerated content hulls, not the
/// native <c>.mdl</c> geometry, so mapping the native per-hardpoint frame offsets
/// ("thrust"/"rthrust"/"lthrust") onto each regenerated mesh is not cleanly
/// portable across the 70+ content hulls. Like P358 (and
/// <c>DamageDebrisParticles</c> for damage vents), the glow sits at the hull's
/// real aft-centre (<c>position + backward·(radius·<see cref="AftCenterFraction"/>)</c>),
/// which is faithful to where the native "thrust" frames live (the model aft) and
/// lands on the actually-rendered hull. (2) Native draws the decal at
/// <c>m_scaleGlow·m_scale</c> in native mesh units; this port normalizes every hull
/// to real world units (radius = 0.5·Length), so the quad's half-extent is
/// <c>scaleGlow·radius·<see cref="GlowSizeFactor"/></c> to keep the bloom
/// proportional on every hull class — the same scale normalization P358 documents.
/// (3) The glow texture is the port's standard procedural soft radial sprite
/// (white, alpha falloff), exactly the convention the other effect renderers use
/// (there is no dedicated glow texture asset in this port's content); the HSB tint
/// is applied per-vertex.</para>
/// <para><b>Trigger/data.</b> The glow is gated on <c>scaleGlow &gt; 0</c> (native
/// RenderGlow's gate), reached only while a ship's real thrust value is above 0 —
/// driven by the same already-known per-ship wire data P358 uses: the real
/// <c>StateM.AfterburnerButton</c> bit (forces value 1) and the real throttle axis
/// (<c>value = 0.5·(Throttle+1)</c>). No new wire data.</para>
/// </remarks>
public sealed class EngineNozzleGlowRenderer : IDisposable
{
    /// <summary>How far behind the ship's centre the nozzle glow sits, as a
    /// fraction of its radius — the same aft-centre point as the P358 exhaust
    /// emitter (native "thrust" frames sit at the model aft, like the "smoke"
    /// frames at ≈1.05×radius).</summary>
    public const float AftCenterFraction = 0.95f;

    /// <summary>The port's normalization of native's decal scale
    /// (<c>m_scaleGlow·m_scale</c>, in native mesh units) to the port's real-hull
    /// world units: the glow's half-extent is <c>scaleGlow·radius·GlowSizeFactor</c>,
    /// so at full thrust (scaleGlow 1) the bloom spans ≈0.9× the hull radius —
    /// a bright, clearly-the-nozzle glow without engulfing the ship, on every hull
    /// class.</summary>
    public const float GlowSizeFactor = 0.45f;

    private readonly Texture2D _softSprite;
    private readonly BasicEffect _effect;
    private readonly BlendState _additiveSoft;
    private readonly RasterizerState _cullNone;

    /// <param name="gd">The graphics device the generated sprite/effect are created on.</param>
    public EngineNozzleGlowRenderer(GraphicsDevice gd)
    {
        _softSprite = GenerateSoftSprite(gd, 64);

        _effect = new BasicEffect(gd)
        {
            TextureEnabled = true,
            VertexColorEnabled = true,
            Texture = _softSprite,
        };

        // Additive soft-alpha: output += srcColor * srcAlpha — the repo's particle
        // convention, so overlapping glow blooms accumulate softly (a bright core
        // with a soft halo, exactly what a decal glow should read as).
        _additiveSoft = new BlendState
        {
            ColorSourceBlend = Blend.SourceAlpha,
            AlphaSourceBlend = Blend.SourceAlpha,
            ColorDestinationBlend = Blend.One,
            AlphaDestinationBlend = Blend.One,
            ColorBlendFunction = BlendFunction.Add,
            AlphaBlendFunction = BlendFunction.Add,
        };
        _cullNone = new RasterizerState { CullMode = CullMode.None };
    }

    /// <summary>
    /// Draw every glow as a camera-facing soft quad tinted by its HSB colour.
    /// <paramref name="cameraEye"/> is the camera's true world position
    /// (subtracted so vertex positions stay small, the P207 lesson);
    /// <paramref name="view"/>/<paramref name="projection"/> are the frame's real
    /// camera matrices (also supplying the billboard right/up basis).
    /// </summary>
    public void Draw(IReadOnlyList<EngineGlowView> glows, Vector3 cameraEye, Matrix view, Matrix projection)
    {
        if (glows.Count == 0) return;

        // Row-major orthonormal view: column 0 = camera right, column 1 = camera up.
        var camRight = new Vector3(view.M11, view.M12, view.M13);
        var camUp = new Vector3(view.M21, view.M22, view.M23);

        var verts = new VertexPositionColorTexture[glows.Count * 4];
        var indices = new short[glows.Count * 6];

        int v = 0, idxBase = 0;
        foreach (var g in glows)
        {
            var centre = new Vector3(g.Position.X, g.Position.Y, g.Position.Z) - cameraEye;
            float half = g.HalfExtent;
            // Solid tint at alpha 1 — the soft sprite's own alpha falloff provides
            // the soft edge, and the HSB tint (bri 0 → black) carries the dimming.
            var col = new Color(g.Rgb.X, g.Rgb.Y, g.Rgb.Z, 1f);

            var tl = centre - camRight * half + camUp * half;   // uv (0,0)
            var tr = centre + camRight * half + camUp * half;   // uv (1,0)
            var br = centre + camRight * half - camUp * half;   // uv (1,1)
            var bl = centre - camRight * half - camUp * half;   // uv (0,1)

            verts[v] = new VertexPositionColorTexture(tl, col, new Vector2(0f, 0f));
            verts[v + 1] = new VertexPositionColorTexture(tr, col, new Vector2(1f, 0f));
            verts[v + 2] = new VertexPositionColorTexture(br, col, new Vector2(1f, 1f));
            verts[v + 3] = new VertexPositionColorTexture(bl, col, new Vector2(0f, 1f));

            indices[idxBase] = (short)v;
            indices[idxBase + 1] = (short)(v + 1);
            indices[idxBase + 2] = (short)(v + 2);
            indices[idxBase + 3] = (short)v;
            indices[idxBase + 4] = (short)(v + 2);
            indices[idxBase + 5] = (short)(v + 3);

            v += 4;
            idxBase += 6;
        }

        _effect.World = Matrix.Identity;
        _effect.View = view;
        _effect.Projection = projection;
        _effect.CurrentTechnique.Passes[0].Apply();

        var gd = _effect.GraphicsDevice;
        gd.BlendState = _additiveSoft;
        gd.RasterizerState = _cullNone;
        gd.DepthStencilState = DepthStencilState.DepthRead;   // test, never write (P339 B2)
        gd.DrawUserIndexedPrimitives(
            PrimitiveType.TriangleList, verts, 0, verts.Length, indices, 0, glows.Count * 2);

        // Restore all three states (P339 B2 — ExplosionRenderer's exact pattern).
        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = RasterizerState.CullCounterClockwise;
    }

    /// <summary>A soft-edged circular sprite: alpha 1 at the centre falling off
    /// smoothly (squared) to 0 at the rim, white RGB — the glow tint is applied
    /// per-vertex. Fully deterministic, no asset (the port's standard particle
    /// sprite convention).</summary>
    private static Texture2D GenerateSoftSprite(GraphicsDevice gd, int size)
    {
        var tex = new Texture2D(gd, size, size);
        var data = new Color[size * size];
        float inv = 2f / size;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float nx = (x + 0.5f) * inv - 1f;   // [-1, 1]
                float ny = (y + 0.5f) * inv - 1f;
                float r = MathF.Sqrt(nx * nx + ny * ny);
                float a = MathHelper.Clamp(1f - r, 0f, 1f);
                a *= a;   // smooth falloff
                data[y * size + x] = new Color(1f, 1f, 1f, a);
            }
        }
        tex.SetData(data);
        return tex;
    }

    public void Dispose()
    {
        _softSprite.Dispose();
        _effect.Dispose();
        _additiveSoft.Dispose();
        _cullNone.Dispose();
    }
}
