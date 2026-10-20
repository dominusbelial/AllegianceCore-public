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

    /// <summary>
    /// P585 — the funnel-vortex BRIGHTNESS boost (≥1) applied while an opt-in
    /// transit pulse is active on this warp (<c>--aleph-glimpse on</c> only; 1.0
    /// when off/resting, so the native look is unchanged). Multiplies the
    /// baked-in vertex colours before the additive funnel draw, brightening the
    /// whole vortex briefly.
    /// </summary>
    public const float IdleFunnelBoost = 1f;

    /// <summary>Peak funnel brightness multiple during a transit pulse.</summary>
    public const float PeakFunnelBoost = 2.6f;

    private readonly GraphicsDevice _gd;
    private readonly Assets.IAssetSource _assets;
    private readonly NativeEffectTextures _native;
    private readonly WarpMesh[] _lodMeshes;
    private readonly DynamicVertexBuffer[] _lodBuffers;   // VertexPositionColorTexture, one per tier
    private readonly IndexBuffer[] _lodIndices;
    private readonly VertexPositionColorTexture[][] _scratch;
    private readonly HdMaterialRenderer _hd;
    private readonly bool _ownsHd;
    private readonly BasicEffect _effect;
    private readonly RasterizerState _cullNone;
    private readonly BlendState _additive;
    private readonly ParticleBillboardBatcher _batcher;
    private readonly Dictionary<string, string?> _funnelStems = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture2D> _funnelTexture = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<short, float> _swellTrigger = new();
    private Texture2D? _starTexture;
    private float _time;
    // P568 — per-aleph diagnostics: set ALLEGIANCE_DEBUG_WARP=1 to log each
    // warp's draw metrics (tier, world rim radius, camera distance, texture
    // size / mean luminance, active sampler). Throttled to ~once/sec per warp
    // so it can run in a live window without flooding.
    private readonly Dictionary<short, int> _diagFrame = new();
    private readonly Dictionary<Texture2D, float> _diagMeanLum = new();
    private static readonly bool _diagEnabled =
        Environment.GetEnvironmentVariable("ALLEGIANCE_DEBUG_WARP") is { Length: > 0 } s &&
        (s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase));

    public WarpRenderer(GraphicsDevice gd, IAssetSource assets, HdMaterialRenderer? hd = null)
    {
        _gd = gd;
        _hd = hd ?? new HdMaterialRenderer(gd, NativeMaterialParts.NativeReader(assets));
        _ownsHd = hd is null;
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

    /// <summary>Old-sector arrival swells must not survive a cluster reset.</summary>
    public void ResetTransits() => _swellTrigger.Clear();

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
        float worldRadius, string textureName, short warpId, float funnelBoost = IdleFunnelBoost,
        float destabilize = 0f)
    {
        _time += (float)Math.Max(0.0, deltaSeconds);

        // --- Funnel: textured double-horn vortex ---------------------------------
        int tier = SelectLodTier(world, projection, worldRadius);
        UpdateFunnelVertices(tier, _time, funnelBoost, destabilize);
        var nativeTexture = FunnelTexture(textureName);
        string? stem = _funnelStems.GetValueOrDefault(textureName);
        _effect.Texture = stem is null ? nativeTexture : _hd.EmissiveTexture(stem) ?? nativeTexture;
        _effect.World = world;
        _effect.View = view;
        _effect.Projection = projection;
        _effect.CurrentTechnique.Passes[0].Apply();

        _gd.BlendState = _additive;
        _gd.DepthStencilState = DepthStencilState.DepthRead;   // test, no Z-write (native :221)
        _gd.RasterizerState = _cullNone;                       // hollow funnel (native :218)
        // P568 — native's funnel UVs wrap (u ∈ [0,4] around the circumference,
        // v ∈ [0,2] radially; alephgeo.cpp:128-137) and D3D9's default address
        // mode is WRAP, so the vortex tiles around the funnel. The real window
        // runs ship/station/missile passes first that all leave
        // SamplerStates[0] = LinearClamp, which CLAMPS those UVs to the
        // texture's black border and makes the funnel effectively invisible
        // (measured ~190× fewer lit pixels). Pin the funnel sampler to WRAP
        // explicitly so it never depends on whatever a previous pass left, and
        // restore the caller's sampler after (P339 B2 — don't leak).
        var prevSampler = _gd.SamplerStates[0];
        _gd.SamplerStates[0] = SamplerState.LinearWrap;
        _gd.SetVertexBuffer(_lodBuffers[tier]);
        _gd.Indices = _lodIndices[tier];
        _gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, _lodMeshes[tier].TriangleCount);

        // --- Star core: two counter-rotating f02 decals --------------------------
        DrawStarCore(world, view, projection, worldRadius, warpId);

        // P568 — per-aleph diagnostics (throttled; ALLEGIANCE_DEBUG_WARP=1).
        MaybeLogDiag(warpId, world, worldRadius, tier);

        // Restore the caller's expectations (P339 B2 — don't leak additive /
        // depth-read / cull-none / wrap into later passes).
        _gd.SamplerStates[0] = prevSampler;
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

    private void UpdateFunnelVertices(int tier, float time, float funnelBoost, float destabilize = 0f)
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
        float boost = Math.Clamp(funnelBoost, 0f, 8f);
        // P758 — a destabilized (bombed) aleph burns red-hot: the red channel is
        // driven up and green/blue down with the WarpBombVisual pulse strength,
        // the port of native's bomb art swap (appmsg.cpp:858-866 AddBomb).
        float d = Math.Clamp(destabilize, 0f, 1f);

        for (int vi = 0; vi < n; vi++)
        {
            // P585 — the funnel-brightness boost scales the baked colour up
            // (clamped at 255/channel) so the additive funnel burns brighter
            // during an opt-in transit pulse; 1.0 leaves the native look intact.
            float r = col[vi * 4] * boost, g = col[vi * 4 + 1] * boost, b = col[vi * 4 + 2] * boost;
            if (d > 0f)
            {
                r = MathF.Min(1f, r + d * (1f - r));
                g *= 1f - 0.75f * d;
                b *= 1f - 0.75f * d;
            }
            v[vi] = new VertexPositionColorTexture(
                new Vector3(pos[vi * 3], pos[vi * 3 + 1], pos[vi * 3 + 2]),
                new Color(Math.Clamp(r, 0f, 1f), Math.Clamp(g, 0f, 1f), Math.Clamp(b, 0f, 1f), col[vi * 4 + 3]),   // colours are floats in [0,1] (WarpMesh.Colors)
                new Vector2(uv[vi * 2], timeFlow + uv[vi * 2 + 1]));
        }
        _lodBuffers[tier].SetData(v);
    }

    private void MaybeLogDiag(short warpId, Matrix world, float worldRadius, int tier)
    {
        if (!_diagEnabled) return;
        if (!_diagFrame.TryGetValue(warpId, out int n)) n = 0;
        // Throttle to once every 60 frames (~1 Hz at 60 FPS).
        if (n % 60 != 0) { _diagFrame[warpId] = n + 1; return; }
        _diagFrame[warpId] = n + 1;

        var rel = new Vector3(world.M41, world.M42, world.M43);
        float dist = rel.Length();

        Texture2D? tex = _effect.Texture;
        float meanLum = -1f;
        string texDesc = "none";
        if (tex is not null)
        {
            texDesc = $"{tex.Width}x{tex.Height}";
            if (!_diagMeanLum.TryGetValue(tex, out meanLum))
            {
                var px = new Color[tex.Width * tex.Height];
                try { tex.GetData(px); } catch { px = Array.Empty<Color>(); }
                if (px.Length > 0)
                {
                    long s = 0;
                    foreach (var c in px) s += (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
                    meanLum = (float)s / px.Length;
                }
                _diagMeanLum[tex] = meanLum;
            }
        }

        Console.WriteLine(
            $"[warp-diag] #{warpId} drawn=true tier={tier} worldRimRadius={worldRadius:0.#} " +
            $"camDist={dist:0.#} tex={texDesc} texMeanLum={(meanLum < 0 ? "n/a" : meanLum.ToString("0.#"))} " +
            $"sampler={(_gd.SamplerStates[0]?.ToString() ?? "null")}");
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
        _lastLoadedStem = null;
        Texture2D tex;
        if (IsAlephFamily(textureName))
            tex = LoadTextureFile(textureName) ?? LoadTextureFile("aleph") ?? _native.SoftFallback;
        else
            tex = LoadTextureFile("aleph") ?? LoadTextureFile(textureName) ?? _native.SoftFallback;
        _funnelStems[textureName] = _lastLoadedStem;
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
    /// P749 — never lets a missing/unlisted/unreadable optional texture escape:
    /// any fault is swallowed and returns null so the caller falls back to the
    /// soft placeholder (logged once per stem).</summary>
    private readonly HashSet<string> _missingLogged = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastLoadedStem;
    private Texture2D? LoadTextureFile(string stem)
    {
        try
        {
            string rel = stem + "bmp.mdl";
            if (!_assets.Exists(rel))
            {
                rel = FindCaseInsensitive(stem + "bmp.mdl") ?? "";
                if (rel.Length == 0) return null;
            }

            var bytes = TryReadOptional(_assets, rel);
            if (bytes is null) return null;
            var res = NativeEffectTextures.Resolve(bytes);
            if (res.Kind == NativeEffectTextures.NativeTextureKind.Binary)
            {
                _lastLoadedStem = Path.GetFileNameWithoutExtension(rel);
                return UploadOpaque(res.Texture!);
            }
            if (res.Kind == NativeEffectTextures.NativeTextureKind.Png && res.PngName is not null
                && _assets.Exists(res.PngName))
            {
                _lastLoadedStem = Path.GetFileNameWithoutExtension(rel);
                using (var s = _assets.OpenRead(res.PngName!))
                    return Texture2D.FromStream(_gd, s);
            }
            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                                   or ObjectDisposedException or System.Runtime.InteropServices.ExternalException
                                   or ArgumentException)
        {
            // P749 — an optional warp texture that is missing, unlisted in the
            // manifest, or corrupt must fall back to the soft placeholder, never
            // crash the player. Log once per stem so a broken install is visible
            // without flooding every frame.
            if (_missingLogged.Add(stem))
                Console.WriteLine($"[warp] texture '{stem}bmp' unavailable ({ex.GetType().Name}: {ex.Message}) — using the soft fallback");
            return null;
        }
    }

    /// <summary>
    /// P749 — guarded read of an optional artwork file. Returns the bytes, or
    /// null when the file is absent, unlisted in the player manifest, or
    /// unreadable — swallowing the controlled faults (never the programmer
    /// bugs) so a player-mode optional texture falls back instead of crashing.
    /// Static and GPU-free so tests drive it directly.
    /// </summary>
    internal static byte[]? TryReadOptional(Assets.IAssetSource assets, string relativePath)
    {
        try
        {
            if (!assets.Exists(relativePath)) return null;
            using var s = assets.OpenRead(relativePath);
            using var m = new MemoryStream();
            s.CopyTo(m);
            return m.ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                                   or ObjectDisposedException or System.Runtime.InteropServices.ExternalException
                                   or ArgumentException)
        {
            return null;
        }
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
        if (_ownsHd) _hd.Dispose();
        _effect.Dispose();
        _cullNone.Dispose();
        _additive.Dispose();
        _batcher.Dispose();
        _native.Dispose();
    }
}
