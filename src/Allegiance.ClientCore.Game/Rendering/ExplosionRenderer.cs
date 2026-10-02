using System.Collections.Generic;
using Allegiance.ClientCore.State;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XnaVec = Microsoft.Xna.Framework.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Draws live explosions (P263) using the REAL original-game sprite artwork.
/// Each blast is played by sampling <see cref="ExplosionSampler"/> for the
/// current elapsed time and drawing its fireball decals (animated through the
/// category's film frames at the native 20&nbsp;fps, each a camera-facing,
/// randomly-rotated, slightly-offset quad) plus, when the category has one, the
/// expanding shock-wave ring (native <c>ExplosionGeo</c>,
/// <c>effect/explosion.cpp:149-202</c>). Everything is additive-blended so the
/// sprites' black surround vanishes and the glowing fireball reads on top of the
/// scene — the same reason native uses additive decals.
/// </summary>
/// <remarks>
/// <para><b>Where the size comes from.</b> The quad size and the shock-wave
/// growth come straight from the category preset
/// (<see cref="ExplosionPreset.RadiusExplosion"/> / <c>.RadiusShockWave</c>),
/// which is the native <c>AddExplosion</c> switch table
/// (<c>trekigc.cpp:95-177</c>) — so a fighter death is small/short and a
/// station death is huge/long, exactly as native intends.</para>
/// <para><b>Shock-wave orientation.</b> Native orients the ring in the emitter's
/// plane (<c>forward</c>/<c>right</c>); this renderer draws it camera-facing
/// (billboarded) so it always reads as an expanding ring. This is the one
/// documented rendering divergence (the wire therefore omits forward/right —
/// see <c>Wire/ExplosionEvent.cs</c>).</para>
/// <para><b>Batching.</b> Visible fireballs/shock waves are grouped by their
/// actual frame texture (different films/frames = different textures, so one
/// batch per texture) and each group is drawn with one
/// <c>DrawUserIndexedPrimitives</c> under the additive blend state, which is
/// restored to <c>BlendState.Opaque</c> afterwards (the P235/MissileTrail
/// restore precedent).</para>
/// </remarks>
public sealed class ExplosionRenderer : IDisposable
{
    private readonly ExplosionAssets _assets;
    private readonly BasicEffect _effect;
    private readonly BlendState _additive;
    private readonly RasterizerState _cullNone;

    /// <param name="gd">The graphics device.</param>
    /// <param name="assets">The decoded real explosion artwork.</param>
    public ExplosionRenderer(GraphicsDevice gd, ExplosionAssets assets)
    {
        _assets = assets;
        _effect = new BasicEffect(gd)
        {
            TextureEnabled = true,
            VertexColorEnabled = true,
        };
        // Additive: dst += src.rgb * src.a. With opaque (a=255) sprite pixels
        // the black surround contributes 0 and only the glowing fireball adds.
        _additive = new BlendState
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
    /// Draw every live explosion in the local ship's sector.
    /// <paramref name="explosions"/> is the world's live set (typically already
    /// gathered under the gate lock); <paramref name="ownSectorId"/> filters to
    /// the local ship's sector; <paramref name="nowLocalMs"/> is the local
    /// monotonic instant for ageing; <paramref name="cameraEye"/>/<c>view</c>/
    /// <c>projection</c> are the frame's real camera data (also giving the
    /// billboard basis).
    /// </summary>
    public void Draw(
        IReadOnlyList<ExplosionEntity> explosions,
        short ownSectorId,
        long nowLocalMs,
        XnaVec cameraEye,
        Matrix view,
        Matrix projection)
    {
        // Camera world-space right/up (rotation-only view): columns 0 and 1.
        var camRight = new XnaVec(view.M11, view.M12, view.M13);
        var camUp = new XnaVec(view.M21, view.M22, view.M23);

        // Group billboards by their texture so each batch shares one texture.
        var batches = new Dictionary<Texture2D, List<Quad>>();

        foreach (var e in explosions)
        {
            if (e.ClusterId != ownSectorId) continue;      // native cluster gate
            ExplosionPreset preset;
            try { preset = ExplosionPresets.For(e.Type); }
            catch (ArgumentOutOfRangeException) { continue; }

            float elapsed = Math.Max(0f, (nowLocalMs - e.ReceivedLocalMs) / 1000.0f);
            var sample = ExplosionSampler.Sample(preset, elapsed);

            // Fireball sprites ride the emitter's velocity (native dposition).
            var centre = new XnaVec(e.Position.X + e.Velocity.X * elapsed,
                                    e.Position.Y + e.Velocity.Y * elapsed,
                                    e.Position.Z + e.Velocity.Z * elapsed) - cameraEye;

            foreach (var fb in sample.Fireballs)
            {
                var tex = _assets.Frame(preset.Films[fb.FilmIndex].Stem, fb.FrameIndex);
                if (tex is null) continue;
                var off = new XnaVec(fb.Offset.X, fb.Offset.Y, fb.Offset.Z);
                AddBatch(batches, tex, centre + off, preset.RadiusExplosion, fb.Angle,
                         Color.White, camRight, camUp);
            }

            if (sample.Shockwave is { } sw && _assets.Shockwave is { } shockTex)
            {
                // Ring tint = the category colour scaled by the quadratic fade
                // (native bright * color, effect/explosion.cpp:155-156).
                var col = new Color(
                    (int)(preset.ShockR * sw.Bright * 255f),
                    (int)(preset.ShockG * sw.Bright * 255f),
                    (int)(preset.ShockB * sw.Bright * 255f),
                    255);
                AddBatch(batches, shockTex, centre, sw.Scale, 0f, col, camRight, camUp);
            }
        }

        _effect.View = view;
        _effect.Projection = projection;
        _effect.World = Matrix.Identity;

        var gd = _effect.GraphicsDevice;
        gd.BlendState = _additive;
        gd.RasterizerState = _cullNone;
        gd.DepthStencilState = DepthStencilState.DepthRead;

        _effect.CurrentTechnique.Passes[0].Apply();
        foreach (var (tex, quads) in batches)
        {
            _effect.Texture = tex;
            _effect.CurrentTechnique.Passes[0].Apply();
            DrawBatch(gd, quads);
        }

        gd.BlendState = BlendState.Opaque;         // restore (P235 precedent)
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = RasterizerState.CullCounterClockwise;
    }

    private static void AddBatch(
        Dictionary<Texture2D, List<Quad>> batches,
        Texture2D tex,
        XnaVec centre,
        float half,
        float angle,
        Color colour,
        XnaVec camRight,
        XnaVec camUp)
    {
        if (!batches.TryGetValue(tex, out var list))
            batches[tex] = list = [];

        // In-plane rotation about the billboard centre (native random angle).
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        void Corners(float dx, float dy, out XnaVec corner)
        {
            float rx = dx * c - dy * s;
            float ry = dx * s + dy * c;
            corner = centre + camRight * rx + camUp * ry;
        }

        Corners(-half, -half, out var tl);
        Corners(half, -half, out var tr);
        Corners(half, half, out var br);
        Corners(-half, half, out var bl);

        list.Add(new Quad(tex, tl, tr, br, bl, colour));
    }

    private static void DrawBatch(GraphicsDevice gd, List<Quad> quads)
    {
        var verts = new VertexPositionColorTexture[quads.Count * 4];
        var indices = new short[quads.Count * 6];
        int v = 0, idx = 0;
        foreach (var q in quads)
        {
            verts[v] = new VertexPositionColorTexture(q.TL, q.Colour, new Vector2(0f, 0f));
            verts[v + 1] = new VertexPositionColorTexture(q.TR, q.Colour, new Vector2(1f, 0f));
            verts[v + 2] = new VertexPositionColorTexture(q.BR, q.Colour, new Vector2(1f, 1f));
            verts[v + 3] = new VertexPositionColorTexture(q.BL, q.Colour, new Vector2(0f, 1f));
            indices[idx] = (short)v; indices[idx + 1] = (short)(v + 1); indices[idx + 2] = (short)(v + 2);
            indices[idx + 3] = (short)v; indices[idx + 4] = (short)(v + 2); indices[idx + 5] = (short)(v + 3);
            v += 4; idx += 6;
        }
        gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, verts, 0, verts.Length, indices, 0, quads.Count * 2);
    }

    private readonly record struct Quad(Texture2D Tex, XnaVec TL, XnaVec TR, XnaVec BR, XnaVec BL, Color Colour);

    public void Dispose()
    {
        _effect.Dispose();
        _additive.Dispose();
        _cullNone.Dispose();
    }
}
