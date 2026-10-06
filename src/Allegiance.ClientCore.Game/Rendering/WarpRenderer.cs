using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Allegiance.Core.Model;
using Allegiance.ClientCore.MonoGame.Assets;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Draws the shared textured warp vortex mesh once per warp instance (P562 —
/// the native <c>AlephGeo</c> art pass). The P242/P258 procedural hourglass
/// silhouette is kept (double horn, per Domingo's explicit scope), but it is
/// now TEXTURED with the original game's flowing <c>alephbmp</c> vortex and
/// driven by native's animated UVs instead of a rigid whole-mesh spin — the
/// "water draining" look.
/// </summary>
/// <remarks>
/// <para><b>Funnel (textured).</b> A shared double-horn mesh (built per LOD
/// tier by <see cref="WarpMeshFactory"/>) whose per-vertex colour is native's
/// <c>2r(1−r)</c> brightness band (dark pinch &amp; rim, bright mid-band) and
/// whose per-vertex UVs are native's spiral twist, with the inward
/// <c>v</c>-flow recomputed every frame (<see cref="WarpMeshFactory.FlowV"/>).
/// A <see cref="BasicEffect"/> with <c>TextureEnabled</c> +
/// <c>VertexColorEnabled</c> (Gouraud) modulates the per-warp
/// <c>textureName + "bmp"</c> texture by the vertex brightness under the same
/// additive blend / cull-none / no-Z-write state native uses
/// (<c>alephgeo.cpp:215-230</c>). No rigid whole-mesh rotation — the flow is
/// entirely from the animated UVs, exactly as native.</para>
/// <para><b>Star core.</b> Two counter-rotating <c>f02bmp</c> star decals at
/// the pinch (native's <c>Render :190-208</c>, rates <c>+0.25</c> and
/// <c>−0.375</c> rad/s), scaled to <c>0.1 ×</c> the aleph's world rim radius
/// (alephgeo.cpp:160), swelled to ~6× with 8 pulses on transit
/// (<see cref="WarpVortex.StarScale"/>). Falls back to the shared soft sprite
/// if the artwork is missing (never invisible).</para>
/// <para><b>Transit swell.</b> <see cref="TriggerTransit"/> records a per-warp
/// swell start; subsequent draws scale the star core up with native's 8-pulse
/// swell curve over <see cref="TransitSwellDurationSeconds"/> then a 0.25 s
/// decay. The caller (Game1) fires it when any ship transits the aleph.</para>
/// <para><b>LOD.</b> Native's four <c>(countDepth, countSection)</c> tiers
/// (<c>alephgeo.cpp:239-251</c>) are selected by the warp's projected on-screen
/// pixel radius against native's 64 / 32 / 16 thresholds.</para>
/// </remarks>
public sealed class WarpRenderer : IDisposable
{
    /// <summary>Seconds for the transit star swell to run its 8 pulses before
    /// the 0.25 s decay. (Native's event duration is caller-supplied; this is
    /// the port's transit-event duration.)</summary>
    public const float TransitSwellDurationSeconds = 1.2f;

    /// <summary>
    /// P237/P238 — the scale multiplier for the rendered warp funnel, applied
    /// by <see cref="Game1"/> when it composes each warp's world matrix. Its
    /// value is aliased to <see cref="Sector.WarpVisualScale"/> (2.5x, live
    /// flight-tested — see that member's own doc comment and
    /// <see cref="WarpMeshFactoryTests"/>.VisualScaleMultiplier test).
    /// </summary>
    public const float VisualScaleMultiplier = Sector.WarpVisualScale;

    private readonly GraphicsDevice _gd;
    private readonly Assets.IAssetSource _assets;
    private readonly NativeEffectTextures _native;
    private readonly WarpMesh[] _lodMeshes;
    private readonly DynamicVertexBuffer[] _lodBuffers;   // VertexPositionColorTexture, one per tier
    private readonly IndexBuffer[] _lodIndices;
    private readonly VertexPositionColorTexture[][] _scratch;
    private readonly BasicEffect _effect;
    private readonly RasterizerState _cullNone;
    private readonly BlendState _additive;
    private readonly ParticleBillboardBatcher _batcher;
    private readonly Dictionary<string, Texture2D> _funnelTexture = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<short, float> _swellTrigger = new();
    private Texture2D? _starTexture;
    private float _time;

    public WarpRenderer(GraphicsDevice gd, IAssetSource assets)
    {
        _gd = gd;
        _assets = assets;
        _native = NativeEffectTextures.Load(gd, assets);

        // Build one mesh per native LOD tier (finest first).
        _lodMeshes = new WarpMesh[WarpMeshFactory.LodTiers.Length];
        _lodBuffers = new DynamicVertexBuffer[_lodMeshes.Length];
        _lodIndices = new IndexBuffer[_lodMeshes.Length];
        _scratch = new VertexPositionColorTexture[_lodMeshes.Length][];
        for (int i = 0; i < _lodMeshes.Length; i++)
        {
            var (depth, section) = WarpMeshFactory.LodTiers[i];
            var mesh = WarpMeshFactory.Generate(depth, section);
            _lodMeshes[i] = mesh;
            _scratch[i] = new VertexPositionColorTexture[mesh.VertexCount];
            _lodBuffers[i] = new DynamicVertexBuffer(
                gd, typeof(VertexPositionColorTexture), mesh.VertexCount, BufferUsage.WriteOnly);
            _lodIndices[i] = new IndexBuffer(
                gd, IndexElementSize.ThirtyTwoBits, mesh.Indices.Length, BufferUsage.WriteOnly);
            _lodIndices[i].SetData(mesh.Indices);
        }

        _effect = new BasicEffect(gd)
        {
            TextureEnabled = true,
            VertexColorEnabled = true,
        };
        _cullNone = new RasterizerState { CullMode = CullMode.None };

        // Native additive blend (alephgeo.cpp:219 SetBlendMode(BlendModeAdd)):
        // SRCALPHA/ONE — the (mostly black) aleph texture's painted pixels add,
        // its black surround contributes 0, and whatever is BEHIND the warp
        // stays visible brightened rather than painted over.
        _additive = new BlendState
        {
            ColorSourceBlend = Blend.SourceAlpha,
            AlphaSourceBlend = Blend.SourceAlpha,
            ColorDestinationBlend = Blend.One,
            AlphaDestinationBlend = Blend.One,
            ColorBlendFunction = BlendFunction.Add,
            AlphaBlendFunction = BlendFunction.Add,
        };

        _batcher = new ParticleBillboardBatcher(gd);
        _starTexture = _native.TextureFor(NativeEffectTextures.AlephStem);
    }

    /// <summary>
    /// Record a ship transit through the aleph identified by
    /// <paramref name="warpId"/> so its star core plays native's swell. Safe to
    /// call any frame; idempotent (later transits just restart the timeline).
    /// </summary>
    public void TriggerTransit(short warpId) => _swellTrigger[warpId] = _time;

    /// <summary>
    /// Draw the shared warp vortex at <paramref name="world"/> under the
    /// provided view/projection, animating the native inward UV flow each
    /// frame. The caller bakes the real per-instance radius scale and
    /// camera-relative translation into <paramref name="world"/> (as
    /// <see cref="Game1"/> does); <paramref name="worldRadius"/> is that
    /// funnel's WORLD rim radius (Radius ×
    /// <see cref="VisualScaleMultiplier"/>) used to size the star core;
    /// <paramref name="textureName"/> selects the per-warp funnel texture
    /// (<c>textureName + "bmp"</c>, native <c>LoadAleph</c>); <paramref
    /// name="warpId"/> keys the transit-swell timeline.
    /// </summary>
    public void Draw(Matrix world, Matrix view, Matrix projection, double deltaSeconds,
        float worldRadius, string textureName, short warpId)
    {
        _time += (float)Math.Max(0.0, deltaSeconds);

        // --- Funnel: textured double-horn vortex ---------------------------------
        int tier = SelectLodTier(world, projection, worldRadius);
        UpdateFunnelVertices(tier, _time);
        _effect.Texture = FunnelTexture(textureName);
        _effect.World = world;
        _effect.View = view;
        _effect.Projection = projection;
        _effect.CurrentTechnique.Passes[0].Apply();

        _gd.BlendState = _additive;
        _gd.DepthStencilState = DepthStencilState.DepthRead;   // test, no Z-write (native :221)
        _gd.RasterizerState = _cullNone;                       // hollow funnel (native :218)
        _gd.SetVertexBuffer(_lodBuffers[tier]);
        _gd.Indices = _lodIndices[tier];
        _gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, _lodMeshes[tier].TriangleCount);

        // --- Star core: two counter-rotating f02 decals --------------------------
        DrawStarCore(world, view, projection, worldRadius, warpId);

        // Restore the caller's expectations (P339 B2 — don't leak additive /
        // depth-read / cull-none into later passes).
        _gd.BlendState = BlendState.Opaque;
        _gd.DepthStencilState = DepthStencilState.Default;
        _gd.RasterizerState = RasterizerState.CullCounterClockwise;
    }

    private void DrawStarCore(Matrix world, Matrix view, Matrix projection, float worldRadius, short warpId)
    {
        var relPos = new Vector3(world.M41, world.M42, world.M43);   // camera-relative centre

        float starScale = _swellTrigger.TryGetValue(warpId, out float trig)
            ? WarpVortex.StarScale(_time, trig, TransitSwellDurationSeconds)
            : WarpVortex.StarBaseScaleFraction;
        float starSize = starScale * worldRadius;                    // 0.1 × rim radius idle

        var tex = _starTexture ?? _native.SoftFallback;
        if (_starTexture is null)
        {
            // Fallback: one symmetric soft star (no rotation needed).
            _batcher.Draw(
                new[] { new ParticleBillboard(relPos, starSize * 0.5f, Color.White, 0f) },
                tex, view, projection);
            return;
        }

        // Two counter-rotating decals (native alephgeo.cpp:190-208).
        var items = new[]
        {
            new ParticleBillboard(relPos, starSize * 0.5f, Color.White, WarpVortex.StarRateA * _time),
            new ParticleBillboard(relPos, starSize * 0.5f, Color.White, WarpVortex.StarRateB * _time),
        };
        _batcher.Draw(items, tex, view, projection);
    }

    private void UpdateFunnelVertices(int tier, float time)
    {
        var mesh = _lodMeshes[tier];
        var pos = mesh.Positions;
        var col = mesh.Colors;
        var uv = mesh.Uvs;
        var v = _scratch[tier];
        int n = mesh.VertexCount;
        // The time-flow component of native's v is shared across all vertices
        // this frame; per-vertex v = timeFlow + baseV (baseV = r).
        float timeFlow = (time / 10f) % 1f;
        if (timeFlow < 0f) timeFlow += 1f;

        for (int vi = 0; vi < n; vi++)
        {
            v[vi] = new VertexPositionColorTexture(
                new Vector3(pos[vi * 3], pos[vi * 3 + 1], pos[vi * 3 + 2]),
                new Color(col[vi * 4], col[vi * 4 + 1], col[vi * 4 + 2], col[vi * 4 + 3]),
                new Vector2(uv[vi * 2], timeFlow + uv[vi * 2 + 1]));
        }
        _lodBuffers[tier].SetData(v);
    }

    /// <summary>
    /// Pick the native LOD tier whose on-screen pixel radius threshold the
    /// warp meets (near = finest). Computes the warp's projected radius in
    /// pixels from its camera distance and the projection's vertical FOV,
    /// against native's 64/32/16 cutoffs (alephgeo.cpp:246-248).
    /// </summary>
    private int SelectLodTier(Matrix world, Matrix projection, float worldRadius)
    {
        var relPos = new Vector3(world.M41, world.M42, world.M43);
        float dist = relPos.Length();
        float tanHalfFov = 1f / MathF.Max(projection.M22, 1e-5f);
        int vpHeight = Math.Max(_gd.Viewport.Height, 1);
        float px = (worldRadius / MathF.Max(dist, 1e-5f)) * (vpHeight * 0.5f) / tanHalfFov;

        if (px > 64f) return 0;   // finest (8×32)
        if (px > 32f) return 1;   // 4×16
        if (px > 16f) return 2;   // 2×16
        return 3;                 // 2×8
    }

    /// <summary>
    /// The funnel vortex texture. Native loads the warp's own
    /// <c>textureName + "bmp"</c> (<c>LoadAleph</c>, trekigc.cpp). For
    /// aleph-family warp names (<c>aleph</c>/<c>homealeph</c>/<c>neutralaleph</c>)
    /// that IS the vortex art and is honoured per-warp. However the actual
    /// <c>m2xspiderw.igc</c> warp names are planet surfaces
    /// (<c>plnt19</c>/<c>plnt42</c>, 1024² full-coverage — the task's
    /// "textureName is aleph for all 46 warps" spot-check is DISPROVEN by the
    /// data), which would paint a solid planet over the funnel and destroy the
    /// water-draining look. So a non-aleph name falls back to the canonical
    /// <c>alephbmp</c> vortex — the look the art pass exists to deliver.
    /// </summary>
    private Texture2D FunnelTexture(string textureName)
    {
        if (_funnelTexture.TryGetValue(textureName, out var t)) return t;
        Texture2D tex;
        if (IsAlephFamily(textureName))
            tex = LoadTextureFile(textureName) ?? LoadTextureFile("aleph") ?? _native.SoftFallback;
        else
            tex = LoadTextureFile("aleph") ?? LoadTextureFile(textureName) ?? _native.SoftFallback;
        _funnelTexture[textureName] = tex;
        return tex;
    }

    private static bool IsAlephFamily(string textureName) =>
        textureName.Contains("aleph", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Decode a funnel texture by stem (<c>stem + "bmp"</c>). On a
    /// case-sensitive filesystem the igc name is lowercase (<c>"aleph"</c>)
    /// while the artwork file is capitalised (<c>Alephbmp.mdl</c>), so a failed
    /// exact lookup falls back to a case-insensitive match in the artwork root.
    /// </summary>
    private Texture2D? LoadTextureFile(string stem)
    {
        string rel = stem + "bmp.mdl";
        if (!_assets.Exists(rel))
        {
            rel = FindCaseInsensitive(stem + "bmp.mdl") ?? "";
            if (rel.Length == 0) return null;
        }

        var bytes = AssetSources.ReadAllBytes(_assets, rel);
        var res = NativeEffectTextures.Resolve(bytes);
        if (res.Kind == NativeEffectTextures.NativeTextureKind.Binary)
            return UploadOpaque(res.Texture!);
        if (res.Kind == NativeEffectTextures.NativeTextureKind.Png && res.PngName is not null
            && _assets.Exists(res.PngName))
            using (var s = _assets.OpenRead(res.PngName!))
                return Texture2D.FromStream(_gd, s);
        return null;
    }

    private string? FindCaseInsensitive(string rel)
    {
        foreach (var f in _assets.Enumerate("."))
            if (string.Equals(f, rel, StringComparison.OrdinalIgnoreCase))
                return f;
        return null;
    }

    private Texture2D UploadOpaque(MdlTexture tex)
    {
        int w = tex.Width, h = tex.Height;
        var surface = new Texture2D(_gd, w, h);
        var colors = new Color[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int si = (y * w + x) * 4;
                colors[y * w + x] = new Color(
                    (int)tex.Rgba[si], (int)tex.Rgba[si + 1], (int)tex.Rgba[si + 2], 255);
            }
        surface.SetData(colors);
        return surface;
    }

    public void Dispose()
    {
        foreach (var b in _lodBuffers) b?.Dispose();
        foreach (var i in _lodIndices) i?.Dispose();
        foreach (var t in _funnelTexture.Values) t?.Dispose();
        _effect.Dispose();
        _cullNone.Dispose();
        _additive.Dispose();
        _batcher.Dispose();
        _native.Dispose();
    }
}
