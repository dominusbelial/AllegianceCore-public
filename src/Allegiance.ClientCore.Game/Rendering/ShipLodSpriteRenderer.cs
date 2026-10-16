using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// One far-tier ship drawn as a cheap lit, side-tinted sprite. Eye-relative
/// centre (P612b camera-relative convention), world-space half-extent (so it
/// subtends the ship's real projected size), a per-vertex RGBA colour
/// (side-tint × lighting × fade alpha), a screen-plane rotation and a
/// <see cref="LookKey"/> grouping hull types into one batched draw call.
/// </summary>
public readonly record struct FarSprite(string LookKey, Vector3 CentreEye, float Half, Color Col, float Angle);

/// <summary>
/// P628 — pure grouping of far-tier sprites so "identical hulls share one
/// instanced/batched draw call per type". Groups by <see cref="FarSprite.LookKey"/>
/// preserving first-seen order (stable, deterministic). Headless-testable.
/// </summary>
public static class ShipLodBatching
{
    public static IReadOnlyList<KeyValuePair<string, List<FarSprite>>> GroupByKey(IEnumerable<FarSprite> sprites)
    {
        var order = new List<string>();
        var map = new Dictionary<string, List<FarSprite>>(StringComparer.Ordinal);
        foreach (var s in sprites)
        {
            if (!map.TryGetValue(s.LookKey, out var list))
            {
                list = new List<FarSprite>();
                map[s.LookKey] = list;
                order.Add(s.LookKey);
            }
            list.Add(s);
        }
        var result = new List<KeyValuePair<string, List<FarSprite>>>(order.Count);
        foreach (var k in order) result.Add(new KeyValuePair<string, List<FarSprite>>(k, map[k]));
        return result;
    }
}

/// <summary>
/// P628 — the GPU batcher for the far tier. Packs every far ship of the same
/// hull <see cref="FarSprite.LookKey"/> into a <b>single</b>
/// <c>DrawUserIndexedPrimitives</c> billboard call (the ParticleBillboardBatcher /
/// ProjectileSpriteRenderer pattern), so a wide 390-ship view collapses hundreds
/// of full-model draws into a handful of sprite batches. Sprites are lit
/// (brightness folded into the vertex colour) and side-tinted (team colour ×
/// lighting), alpha-blended (NOT additive, so team colours stay readable), with
/// depth test on so nearer opaque geometry still occludes them. Render-state
/// hygiene is the P339/P525/P529 lesson: blend/depth/rasterizer are pinned for
/// each draw and restored afterwards.
/// </summary>
public sealed class ShipLodSpriteRenderer : IDisposable
{
    private readonly GraphicsDevice _gd;
    private readonly BasicEffect _effect;
    private readonly RasterizerState _cullNone;
    private readonly BlendState _alphaBlend;
    private readonly Texture2D _softDot;
    private readonly Dictionary<string, Texture2D?> _textures = new(StringComparer.Ordinal);

    /// <summary>Soft round dot used when a hull type has no livery texture
    /// registered — a tiny radial falloff so a distant ship reads as a glowing
    /// point, tinted by its team colour.</summary>
    public const string SoftLook = "";

    public ShipLodSpriteRenderer(GraphicsDevice gd)
    {
        _gd = gd;
        _effect = new BasicEffect(gd)
        {
            TextureEnabled = true,
            VertexColorEnabled = true,
        };
        _cullNone = new RasterizerState { CullMode = CullMode.None };
        _alphaBlend = new BlendState
        {
            ColorSourceBlend = Blend.SourceAlpha,
            AlphaSourceBlend = Blend.SourceAlpha,
            ColorDestinationBlend = Blend.InverseSourceAlpha,
            AlphaDestinationBlend = Blend.InverseSourceAlpha,
        };
        _softDot = BuildSoftDot(gd);
    }

    /// <summary>Register the livery texture for a hull look key (called once per
    /// hull type). null/unregistered → the soft dot.</summary>
    public void RegisterTexture(string lookKey, Texture2D? tex)
        => _textures[lookKey] = tex;

    public Texture2D TextureFor(string lookKey)
        => _textures.TryGetValue(lookKey, out var t) && t is not null ? t : _softDot;

    /// <summary>Draw every far sprite, one batched call per look key. Returns the
    /// number of batched draw calls issued (for the P628 draw-call benchmark).</summary>
    public int Draw(IReadOnlyList<FarSprite> sprites, Matrix view, Matrix projection)
    {
        if (sprites.Count == 0) return 0;
        var gd = _gd;

        gd.BlendState = _alphaBlend;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = _cullNone;
        gd.SamplerStates[0] = SamplerState.LinearClamp;

        _effect.View = view;
        _effect.Projection = projection;
        _effect.Alpha = 1f;

        int batches = 0;
        foreach (var (key, group) in ShipLodBatching.GroupByKey(sprites))
        {
            var billboards = new ParticleBillboard[group.Count];
            for (int i = 0; i < group.Count; i++)
            {
                var s = group[i];
                billboards[i] = new ParticleBillboard(s.CentreEye, s.Half, s.Col, s.Angle);
            }
            ParticleBillboardBatcher.BuildBillboardArrays(billboards, view,
                out var verts, out var indices);

            _effect.Texture = TextureFor(key);
            _effect.CurrentTechnique.Passes[0].Apply();

            gd.DrawUserIndexedPrimitives(
                PrimitiveType.TriangleList, verts, 0, verts.Length,
                indices, 0, indices.Length / 3);
            batches++;
        }

        gd.BlendState = BlendState.Opaque;
        return batches;
    }

    private static Texture2D BuildSoftDot(GraphicsDevice gd)
    {
        const int S = 16;
        var cols = new Color[S * S];
        float cx = (S - 1) / 2f, cy = (S - 1) / 2f;
        for (int y = 0; y < S; y++)
        {
            for (int x = 0; x < S; x++)
            {
                float d = MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / cx;   // 0..1+
                float a = Math.Clamp(1f - d, 0f, 1f);
                a *= a;   // quadratic falloff
                cols[y * S + x] = new Color(Color.White, a);
            }
        }
        var t = new Texture2D(gd, S, S);
        t.SetData(cols);
        return t;
    }

    public void Dispose()
    {
        _effect.Dispose();
        _cullNone.Dispose();
        _alphaBlend.Dispose();
        _softDot.Dispose();
    }
}
