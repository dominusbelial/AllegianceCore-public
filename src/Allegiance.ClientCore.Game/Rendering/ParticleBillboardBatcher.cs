using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>One camera-facing billboard quad to draw: its eye-relative centre
/// (world minus camera eye — the P207 small-coordinates lesson), half-extent,
/// per-vertex RGBA colour and a screen-plane rotation in radians (native
/// <c>DrawDecal</c>'s <c>angle</c>, applied about the view/forward axis).</summary>
public readonly record struct ParticleBillboard(Vector3 CentreEye, float Half, Color Col, float Angle);

/// <summary>One world-oriented patch quad to draw (P545 shield flare): its
/// eye-relative centre, an explicit surface <see cref="Normal"/>, half-extent,
/// colour and a roll about that normal. Drawn in a tangent plane so it lies
/// flush on a curved surface at the hit — native's <c>TextureGeo</c> flare
/// patch on the shield (trekigc.cpp:1624-1645).</summary>
public readonly record struct ParticlePatch(Vector3 CentreEye, Vector3 Normal, float Half, Color Col, float Roll);

/// <summary>
/// P545 — the shared batched billboard drawer for the native-effect particle
/// renderers. Owns the <see cref="BasicEffect"/>, the additive blend state
/// (native's particle-decal blend — see <see cref="NativeEffectTextures"/>
/// remarks) and the cull-none rasterizer once, and packs every live particle
/// into a single <c>DrawUserIndexedPrimitives</c> call (the previous
/// per-renderer batching), drawing a real <see cref="Texture2D"/> (native art
/// or the soft-sprite fallback) with optional per-quad screen-plane rotation.
/// Render-state hygiene is the P339/P525/P529 lesson: the three states we set
/// (blend, depth-read/no-write, cull-none) are restored afterwards so nothing
/// leaks into the passes that draw after this one.
/// </summary>
public sealed class ParticleBillboardBatcher : IDisposable
{
    private readonly BasicEffect _effect;
    private readonly BlendState _additive;
    private readonly BlendState _premul;
    private readonly RasterizerState _cullNone;

    public ParticleBillboardBatcher(GraphicsDevice gd)
    {
        _effect = new BasicEffect(gd)
        {
            TextureEnabled = true,
            VertexColorEnabled = true,
        };
        // Additive soft-alpha (native BlendModeAdd): SRCALPHA/ONE — the sprite's
        // black surround contributes 0 and only the painted pixels add.
        _additive = new BlendState
        {
            ColorSourceBlend = Blend.SourceAlpha,
            AlphaSourceBlend = Blend.SourceAlpha,
            ColorDestinationBlend = Blend.One,
            AlphaDestinationBlend = Blend.One,
            ColorBlendFunction = BlendFunction.Add,
            AlphaBlendFunction = BlendFunction.Add,
        };
        // P546 — premultiplied-alpha (ONE, INVSRCALPHA): used by the enhanced
        // smoke, whose vertex colours are already premultiplied (rgb scaled by
        // the puff alpha) so overlapping smoke accumulates translucency instead
        // of clipping to white.
        _premul = new BlendState
        {
            ColorSourceBlend = Blend.One,
            AlphaSourceBlend = Blend.One,
            ColorDestinationBlend = Blend.InverseSourceAlpha,
            AlphaDestinationBlend = Blend.InverseSourceAlpha,
        };
        _cullNone = new RasterizerState { CullMode = CullMode.None };
    }

    /// <summary>The shared premultiplied-alpha blend state (ONE/INVSRCALPHA),
    /// owned here so every enhanced-smoke pass uses the same one.</summary>
    public BlendState Premultiplied => _premul;

    /// <summary>
    /// A deterministic, stable-per-position screen-plane rotation for a
    /// particle at <paramref name="pos"/> — the port's stand-in for native's
    /// per-particle <c>m_fAngle</c> (seeded randomly at spawn; see the
    /// Smoke/Fire behaviors' <c>DrawDecal(…, angle)</c>). A cheap hash spreads
    /// particles across all orientations so a smoky texture reads as churning,
    /// not as a static aligned stamp. Returns radians in [0, 2π].
    /// </summary>
    public static float RotationSeed(Vector3 pos) =>
        (pos.X * 12.9898f + pos.Y * 78.233f + pos.Z * 37.719f) % MathF.Tau
        + ((pos.X * 12.9898f + pos.Y * 78.233f + pos.Z * 37.719f) < 0 ? MathF.Tau : 0f);

    /// <summary>
    /// Draw every billboard in one batched call under <paramref name="tex"/>
    /// with additive blending (or <paramref name="blend"/> when supplied).
    /// <paramref name="view"/>/<paramref name="projection"/>
    /// are the frame's real camera matrices, which also supply the billboard
    /// right/up basis (row-major orthonormal view: column 0 = camera right,
    /// column 1 = camera up).
    /// </summary>
    public void Draw(IReadOnlyList<ParticleBillboard> items, Texture2D tex, Matrix view, Matrix projection,
        BlendState? blend = null)
    {
        if (items.Count == 0) return;

        var camRight = new Vector3(view.M11, view.M12, view.M13);
        var camUp = new Vector3(view.M21, view.M22, view.M23);

        var verts = new VertexPositionColorTexture[items.Count * 4];
        var indices = new short[items.Count * 6];

        int v = 0, idxBase = 0;
        foreach (var b in items)
        {
            var centre = b.CentreEye;
            float half = b.Half;

            // Rotate the quad about the view axis by b.Angle (native DrawDecal angle).
            float c = MathF.Cos(b.Angle), s = MathF.Sin(b.Angle);
            var rAxis = camRight * c + camUp * s;
            var uAxis = camUp * c - camRight * s;

            var tl = centre - rAxis * half + uAxis * half;   // uv (0,0)
            var tr = centre + rAxis * half + uAxis * half;   // uv (1,0)
            var br = centre + rAxis * half - uAxis * half;   // uv (1,1)
            var bl = centre - rAxis * half - uAxis * half;   // uv (0,1)

            var col = b.Col;
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

        _effect.Texture = tex;
        _effect.World = Matrix.Identity;
        _effect.View = view;
        _effect.Projection = projection;
        _effect.CurrentTechnique.Passes[0].Apply();

        var gd = _effect.GraphicsDevice;
        gd.BlendState = blend ?? _additive;
        gd.RasterizerState = _cullNone;
        gd.DepthStencilState = DepthStencilState.DepthRead;   // test, never write (P339 B2)
        gd.DrawUserIndexedPrimitives(
            PrimitiveType.TriangleList, verts, 0, verts.Length, indices, 0, items.Count * 2);

        // Restore all three states we touched (P339 B2 — ExplosionRenderer pattern).
        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = RasterizerState.CullCounterClockwise;
    }

    /// <summary>
    /// Draw every oriented patch quad in one batched additive call, each laid
    /// flat in the tangent plane of its surface <see cref="ParticlePatch.Normal"/>
    /// at the hit (rolled by <see cref="ParticlePatch.Roll"/> about that normal)
    /// — the native shield-flare <c>TextureGeo</c> on the shield. Centres are
    /// eye-relative. Same single-draw batching and state hygiene as
    /// <see cref="Draw"/>.
    /// </summary>
    public void DrawPatches(IReadOnlyList<ParticlePatch> items, Texture2D tex, Matrix view, Matrix projection)
    {
        if (items.Count == 0) return;

        var verts = new VertexPositionColorTexture[items.Count * 4];
        var indices = new short[items.Count * 6];

        int v = 0, idxBase = 0;
        foreach (var p in items)
        {
            var n = Vector3.Normalize(p.Normal);
            // A tangent basis perpendicular to the surface normal (native orients
            // the flare geo along the hit direction; here the quad lies in the
            // tangent plane). Avoid a degenerate cross when normal ≈ Y.
            var up = Math.Abs(n.Y) < 0.995f ? Vector3.Up : Vector3.UnitX;
            var right = Vector3.Normalize(Vector3.Cross(up, n));
            up = Vector3.Normalize(Vector3.Cross(n, right));

            // Roll about the normal by p.Roll.
            float c = MathF.Cos(p.Roll), s = MathF.Sin(p.Roll);
            var rAxis = right * c + up * s;
            var uAxis = up * c - right * s;

            float half = p.Half;
            var tl = p.CentreEye - rAxis * half + uAxis * half;   // uv (0,0)
            var tr = p.CentreEye + rAxis * half + uAxis * half;   // uv (1,0)
            var br = p.CentreEye + rAxis * half - uAxis * half;   // uv (1,1)
            var bl = p.CentreEye - rAxis * half - uAxis * half;   // uv (0,1)

            var col = p.Col;
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

        _effect.Texture = tex;
        _effect.World = Matrix.Identity;
        _effect.View = view;
        _effect.Projection = projection;
        _effect.CurrentTechnique.Passes[0].Apply();

        var gd = _effect.GraphicsDevice;
        gd.BlendState = _additive;
        gd.RasterizerState = _cullNone;
        gd.DepthStencilState = DepthStencilState.DepthRead;
        gd.DrawUserIndexedPrimitives(
            PrimitiveType.TriangleList, verts, 0, verts.Length, indices, 0, items.Count * 2);

        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = RasterizerState.CullCounterClockwise;
    }

    /// <summary>One quad spanned by two arbitrary world axes (P545 hull-hit
    /// flash decals): <see cref="CentreEye"/>, axis A / B half-vectors, colour.
    /// Generalises the billboard/patch forms — lets a quad lie along any
    /// surface frame (tangent decal) or with one edge along the surface normal
    /// (normal decal), exactly as native's <c>DrawDecal(…, forward, right)</c>.</summary>
    public readonly record struct AxisQuad(Vector3 CentreEye, Vector3 AxisA, Vector3 AxisB, Color Col);

    /// <summary>
    /// Draw every axis-spanning quad in one batched additive call, each a
    /// parallelogram spanned by <see cref="AxisQuad.AxisA"/> × half and
    /// <see cref="AxisQuad.AxisB"/> × half (native's forward/right decal
    /// convention, engine/context.cpp Decal::GetVertices). Centres are
    /// eye-relative. Same single-draw batching and state hygiene as
    /// <see cref="Draw"/>.
    /// </summary>
    public void DrawAxisQuads(IReadOnlyList<AxisQuad> items, Texture2D tex, Matrix view, Matrix projection)
    {
        if (items.Count == 0) return;

        var verts = new VertexPositionColorTexture[items.Count * 4];
        var indices = new short[items.Count * 6];

        int v = 0, idxBase = 0;
        foreach (var q in items)
        {
            var tl = q.CentreEye - q.AxisA - q.AxisB;   // uv (0,0)
            var tr = q.CentreEye + q.AxisA - q.AxisB;   // uv (1,0)
            var br = q.CentreEye + q.AxisA + q.AxisB;   // uv (1,1)
            var bl = q.CentreEye - q.AxisA + q.AxisB;   // uv (0,1)

            var col = q.Col;
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

        _effect.Texture = tex;
        _effect.World = Matrix.Identity;
        _effect.View = view;
        _effect.Projection = projection;
        _effect.CurrentTechnique.Passes[0].Apply();

        var gd = _effect.GraphicsDevice;
        gd.BlendState = _additive;
        gd.RasterizerState = _cullNone;
        gd.DepthStencilState = DepthStencilState.DepthRead;
        gd.DrawUserIndexedPrimitives(
            PrimitiveType.TriangleList, verts, 0, verts.Length, indices, 0, items.Count * 2);

        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = RasterizerState.CullCounterClockwise;
    }

    public void Dispose()
    {
        _effect.Dispose();
        _additive.Dispose();
        _premul.Dispose();
        _cullNone.Dispose();
    }
}
