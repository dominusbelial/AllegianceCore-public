using System;
using System.Collections.Generic;
using Allegiance.Core;
using Allegiance.Core.Model;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P862b — one aleph whose destination sky the glimpse can show: the warp itself,
/// the sector its partner warp leads to and that sector's cluster data.
/// <see cref="DestSector"/> is RESOLVED (native <c>warpIGC.h:129-141</c> —
/// <c>GetWarp(m_warpDef.destinationID)</c>, i.e. the partner warp's
/// <c>ClusterId</c>), never the raw <c>DestinationId</c>, which is a WARP id.
/// </summary>
public readonly record struct GlimpseTarget(
    DataWarpIGC Warp, short DestSector, DataClusterIGC Dest, float DistSq);

/// <summary>
/// P576-B + P585 — the optional, NON-native "aleph destination glimpse": a view
/// of the DESTINATION sector's sky (P576-A's seeded stars + environment dome +
/// planet) shown inside each visible aleph's funnel centre, so you get a peek
/// at where the warp leads. Default ON since P607 (a <c>--aleph-glimpse off</c>
/// flourish to disable); every rule that governs the sky itself is the native
/// port from <see cref="NativeSkyRenderer"/> — this class only adds the "render
/// the destination sky into a small cached target, then composite it into the
/// funnel" step.
///
/// P585 adds two things on top of the P576-B baseline:
/// <list type="bullet">
/// <item><b>Resting glimpse tuning.</b> Stronger (higher centre opacity) and
/// larger (bigger destination-sky disc) than the faint P576-B composite, and it
/// <b>fades in with approach distance</b> (<see cref="ApproachFade"/>: invisible
/// past <see cref="FadeStartDistance"/> u → full at <see cref="FadeFullDistance"/>
/// u) so a resting aleph reads clearly before you transit but stays quiet at
/// range.</item>
/// <item><b>Transit pulse.</b> When any ship crosses an aleph in the viewer's
/// sector, that aleph's destination glimpse briefly <b>opens wide</b> (large
/// radius, high opacity — mixed by <see cref="WarpPulseController"/>'s envelope)
/// and a quick <b>lens-flare burst</b> flashes from the funnel centre (only when
/// the centre is on screen and not occluded by the P547 linear-depth pass).</item>
/// </list>
///
/// <para><b>P862b — the destination is RESOLVED, and the sky is SHARED.</b>
/// Until P862b the glimpse fed <c>DataWarpIGC.DestinationId</c> straight into
/// <c>World.Clusters</c> — but that field is the PARTNER WARP id, not a sector
/// (<c>warpIGC.h:129-141</c>, the P540c mismatch <c>WarpDestinationResolver</c>
/// already fixes for the autopilot and the server transit). On this port's maps
/// the two id spaces do not overlap (clusters 1050-1062 vs warps 2150-2193), so
/// the lookup never resolved and the glimpse drew nothing at all. Now
/// <see cref="CollectTargets"/> resolves through the partner warp (so the cache
/// is keyed by the destination SECTOR), and the destination sky is rendered with
/// the sector-asset SHARED tier renderer (<c>Func&lt;ISkyRenderer&gt;</c>,
/// <see cref="SkySource"/>) instead of a private per-destination sky renderer:
/// the art the glimpse shows is the very art the sector already has resident
/// (the sector you just left was the current sector a frame ago), so preparing a
/// glimpse costs one 256² sky pass and NO decode. <see cref="Warm"/> lets the
/// P862 sector warm-up do that pass during the aleph approach, under its
/// per-frame budget, instead of in the arrival frame.</para>
///
/// Cost is bounded: one low-res <see cref="RenderTarget2D"/> per destination
/// (rendered once, cached until the destination sector changes) with an LRU cap
/// of <see cref="MaxCachedTargets"/>, at most <see cref="MaxGlimpses"/> warps are
/// drawn (nearest-first; sub-threshold on-screen funnels are skipped), and the
/// flare is capped by the pulse controller's own stacking limit. Render targets
/// are only ever switched in <see cref="EnsureTarget"/> (before the composite),
/// never mid-frame during the composite — the P582b lesson.
/// </summary>
public sealed class AlephGlimpseRenderer : IDisposable
{
    /// <summary>Maximum number of simultaneously-glimpsed alephs (nearest first).</summary>
    public const int MaxGlimpses = 6;

    /// <summary>Smallest on-screen funnel radius (px) that earns a glimpse.</summary>
    public const float MinScreenRadiusPx = 8f;

    /// <summary>Low-res glimpse target resolution (the sky is static; the faint
    /// composite needs no more).</summary>
    public const int TargetResolution = 256;

    /// <summary>Cap on cached destination-sky targets (each is a 256² colour+depth
    /// render target). A sector shows at most <see cref="MaxGlimpses"/> alephs and
    /// the warm-up adds the arrival one, so this bounds the whole feature to a few
    /// MB; the least recently used target is dropped past the cap.</summary>
    public const int MaxCachedTargets = 8;

    // ── P585 resting-glimpse tuning ─────────────────────────────────────────
    /// <summary>Centre opacity of the RESTING glimpse. Raised from P576-B's
    /// faint 0.32 (the "too subtle to notice" report) so the destination-sky
    /// disc reads clearly against the bright funnel, yet still <see
    /// cref="ApproachFade"/>-gated at range and let the f02 star core's
    /// brightest part show through "above" it (the composite is alpha-blended
    /// OVER the already-drawn core, so the core stays dominant at the very
    /// centre).</summary>
    public const float RestingCentreOpacity = 0.55f;

    /// <summary>RESTING disc radius as a multiple of the warp's raw funnel
    /// radius (was 1.0× implicitly). Larger, so the destination sky is a
    /// clearly-visible disc; still well inside the 2.5× visual funnel mouth
    /// (P237 <see cref="WarpRenderer.VisualScaleMultiplier"/>), i.e. "inside the
    /// inner funnel, below the star core's brightest part".</summary>
    public const float RestingDiscRadiusFactor = 1.3f;

    /// <summary>Pulse disc radius as a multiple of the raw funnel radius —
    /// the glimpse OPENS WIDE toward (past) the funnel mouth while a transit
    /// pulse is at its peak.</summary>
    public const float PulseDiscRadiusFactor = 2.4f;

    /// <summary>Centre opacity at the pulse PEAK (the glimpse opens almost
    /// fully, showing the destination sky plainly before it settles).</summary>
    public const float PulseCentreOpacity = 0.92f;

    /// <summary>Approach fade: distance (u) at/below which the resting glimpse
    /// is at full strength (task's "full at ~400 u").</summary>
    public const float FadeFullDistance = 430f;

    /// <summary>Approach fade: distance (u) at/above which the resting glimpse
    /// is fully invisible (task's "invisible past ~3000 u").</summary>
    public const float FadeStartDistance = 3400f;

    /// <summary>Minimum pulse strength before the lens-flare burst draws
    /// (keeps the flare from twinkling on trivial envelopes).</summary>
    public const float PulseFlareThreshold = 0.045f;

    /// <summary>Base screen-space half-size (px) of the flare halo at pulse
    /// strength 1 (scaled by the envelope and the streak ratio below).</summary>
    public const float FlareHaloHalfPx = 34f;

    /// <summary>How much longer than wide the flare streaks are (screen-space
    /// cross streaks along the screen axes from the funnel centre).</summary>
    public const float FlareStreakRatio = 4.5f;

    /// <summary>Soft radial texture resolution for the flare halo/streaks.</summary>
    public const int FlareTextureSize = 64;

    /// <summary>Native depth-normalization range (LinearDepth.cs) — the
    /// occlusion test compares scene depth against the aleph's view depth in
    /// the same normalized units.</summary>
    public const float DepthNear = Rendering.LinearDepth.DefaultNear;
    public const float DepthFar = Rendering.LinearDepth.DefaultFar;

    private static readonly short[] QuadIndices = [0, 1, 2, 2, 1, 3];

    private readonly GraphicsDevice _gd;
    private readonly Texture2D _flareTexture;
    private readonly Func<ISkyRenderer?>? _sky;
    private readonly VertexPositionColorTexture[] _quad = new VertexPositionColorTexture[4];
    // P862b proof aid (inert unless asked): ALLEGIANCE_GLIMPSE_DUMP=<dir> writes
    // each destination sky target once, so the report can show exactly which
    // sector's sky the glimpse prepared.
    private readonly string _dumpDir = Environment.GetEnvironmentVariable("ALLEGIANCE_GLIMPSE_DUMP") ?? "";
    private BasicEffect? _quadEffect;
    private BasicEffect? _flareEffect;
    private long _frame;

    private sealed class Entry
    {
        public required RenderTarget2D Target;
        public bool Rendered;
        public long LastFrame;
    }

    private readonly Dictionary<short, Entry> _cache = new();

    /// <param name="gd">Graphics device the glimpse targets are created on.</param>
    /// <param name="skySource">Supplies the ACTIVE sky-tier renderer (native / HD /
    /// black), i.e. the one <c>Game1</c> already draws the current sector's sky
    /// with. The glimpse renders its destinations through that shared instance so
    /// the destination sky's art is the art the sector already has resident — no
    /// second decode of a 2048² planet per glimpsed sector. The renderer is NOT
    /// owned by this class (never disposed here). <c>null</c> renders no glimpse.</param>
    public AlephGlimpseRenderer(GraphicsDevice gd, Func<ISkyRenderer?>? skySource)
    {
        _gd = gd;
        _sky = skySource;
        _flareTexture = CreateFlareTexture(gd);
    }

    /// <summary>
    /// P862b — the current sector's alephs whose PARTNER warp leads somewhere the
    /// client knows, nearest-last order irrelevant (the caller/renderer sorts).
    /// This is the glimpse's whole notion of "destination": resolve
    /// <c>DestinationId</c> to its partner warp (native <c>warpIGC.h:129-141</c>)
    /// and take that warp's <c>ClusterId</c> (<see cref="WarpDestinationResolver"/>);
    /// a warp whose partner is not synced yet contributes nothing rather than
    /// guessing a sector from a warp id (the P540c rule).
    /// </summary>
    public static List<GlimpseTarget> CollectTargets(
        IReadOnlyDictionary<short, DataWarpIGC> warps,
        IReadOnlyDictionary<short, DataClusterIGC> clusters,
        short currentSector,
        Vector3 cameraEye)
    {
        var list = new List<GlimpseTarget>();
        if (warps is null || clusters is null) return list;
        foreach (var kv in warps)
        {
            var w = kv.Value;
            if (w.ClusterId != currentSector) continue;          // only this sector's alephs
            if (WarpDestinationResolver.DestinationSector(warps, w.WarpId) is not { } destSector) continue;
            if (!clusters.TryGetValue(destSector, out var dest)) continue;
            float dx = w.Position[0] - cameraEye.X;
            float dy = w.Position[1] - cameraEye.Y;
            float dz = w.Position[2] - cameraEye.Z;
            list.Add(new GlimpseTarget(w, destSector, dest, dx * dx + dy * dy + dz * dz));
        }
        return list;
    }

    /// <summary>
    /// P585 — the resting-glimpse approach fade as a pure function of
    /// camera-to-aleph distance: 1 at ≤ <see cref="FadeFullDistance"/>, 0 at ≥
    /// <see cref="FadeStartDistance"/>, easing smoothly between (so the glimpse
    /// "fades in on approach" rather than popping).
    /// </summary>
    public static float ApproachFade(float dist)
    {
        if (dist <= FadeFullDistance) return 1f;
        if (dist >= FadeStartDistance) return 0f;
        float t = (dist - FadeFullDistance) / (FadeStartDistance - FadeFullDistance); // 0 near → 1 far
        float p = 1f - t;
        return p * p;                                   // gentler onset near the full-distance knee
    }

    /// <summary>P585 — linear mix of a resting and a pulse value by strength s
    /// (0 = resting, 1 = pulse peak).</summary>
    public static float Mix(float resting, float pulse, float s)
        => resting + (pulse - resting) * s;

    /// <summary>How many destination skies are cached right now (diagnostics: the
    /// sector-entry line reports a glimpse sky prepared inside the entry frames).</summary>
    public int LoadedTargetCount => _cache.Count;

    /// <summary>Whether a destination's sky target is already rendered (a second
    /// glimpse of the same sector costs one composite quad, nothing else).</summary>
    public bool IsRendered(short destSector) => _cache.TryGetValue(destSector, out var e) && e.Rendered;

    /// <summary>
    /// P862b — prepare (and cache) the destination sky for one sector WITHOUT
    /// compositing it, for the P862 sector warm-up: called during the aleph
    /// approach so the arrival frame finds the sky the glimpse will look back at
    /// already resident. <paramref name="currentCluster"/> is the viewer's own
    /// sector — the shared tier renderer is switched back to it before returning,
    /// so warming is invisible to the sky the frame draws. Returns 1 when a new
    /// target was rendered now, 0 when it was already cached, -1 when the glimpse
    /// cannot render at all (glimpse disabled / no sky tier).
    /// </summary>
    public int Warm(short destSector, DataClusterIGC dest, DataClusterIGC currentCluster, Vector3 lookDirection)
    {
        var sky = _sky?.Invoke();
        if (sky is null) return 0;
        if (IsRendered(destSector)) return 0;
        try
        {
            return EnsureTarget(sky, destSector, dest, lookDirection) ? 1 : 0;
        }
        finally
        {
            sky.SetCluster(currentCluster);
        }
    }

    /// <summary>
    /// Draw the destination-sky glimpse into each qualifying aleph's funnel
    /// centre, resting strength faded in by approach distance and (when a
    /// transit pulse is active on the warp, per <paramref name="pulseStrength"/>)
    /// opened wide plus a lens-flare burst. <paramref name="targets"/> are the
    /// current sector's warps with a RESOLVED destination sector
    /// (<see cref="CollectTargets"/>); <paramref name="currentCluster"/> is the
    /// sector the frame is drawing, restored on the shared sky renderer after any
    /// destination targets were rendered; <paramref name="flareOccluded"/> is the
    /// P547 depth occlusion test (warp, screen x, screen y, view depth).
    /// </summary>
    public void Draw(
        IReadOnlyList<GlimpseTarget> targets,
        Vector3 cameraEye,
        Matrix view,
        Matrix projection,
        DataClusterIGC currentCluster,
        Func<short, float> pulseStrength,
        Func<short, int, int, float, bool> flareOccluded)
    {
        if (targets.Count == 0) return;
        _frame++;

        // Nearest-first, bounded (cost: at most MaxGlimpses composite quads).
        var ordered = new List<GlimpseTarget>(Math.Min(targets.Count, MaxGlimpses));
        for (int i = 0; i < targets.Count; i++) ordered.Add(targets[i]);
        ordered.Sort(static (a, b) => a.DistSq.CompareTo(b.DistSq));
        if (ordered.Count > MaxGlimpses) ordered.RemoveRange(MaxGlimpses, ordered.Count - MaxGlimpses);

        var vp = _gd.Viewport;
        float focus = projection.M22;                 // cot(vFOV/2)
        float halfVP = vp.Height / 2f;

        // ── pass 1: what shows this frame, and prepare the destination skies ──
        // (all render-target switches happen here, before the composite)
        var shown = new List<Visible>(ordered.Count);
        var sky = _sky?.Invoke();
        bool switched = false;
        try
        {
            for (int i = 0; i < ordered.Count; i++)
            {
                var t = ordered[i];
                float dist = MathF.Sqrt(t.DistSq);
                if (dist < 1f) continue;

                var warpPos = new Vector3(t.Warp.Position[0], t.Warp.Position[1], t.Warp.Position[2]);
                var dir = warpPos - cameraEye;
                if (dir.LengthSquared() < 1e-6f) continue;
                dir = Vector3.Normalize(dir);

                // On-screen funnel inner radius (px) at this distance.
                float screenRadius = (t.Warp.Radius / dist) * halfVP * focus;
                if (screenRadius < MinScreenRadiusPx) continue;

                // P585 — resting strength fades in with approach; a transit pulse
                // pushes the disc wider and far more opaque (opens wide).
                float s = Math.Max(0f, pulseStrength(t.Warp.WarpId));
                float fade = ApproachFade(dist);

                // P862 — a glimpse whose resting fade is zero and that has no
                // transit pulse draws NOTHING (it is past FadeStartDistance). Do
                // not build its destination sky behind it: entering a sector used
                // to prepare up to MaxGlimpses destination skies in the ONE frame
                // the new sector first appeared.
                if (s <= 0.001f && RestingCentreOpacity * fade <= 0.001f) continue;

                float opacity = Mix(RestingCentreOpacity * fade, PulseCentreOpacity, s);
                float radiusFactor = Mix(RestingDiscRadiusFactor, PulseDiscRadiusFactor, s);

                // P862b — a disc the CAMERA is inside (or nearly inside) is not a
                // glimpse, it is a full-screen sky wash: the ship comes out of a
                // warp ~12 u from the partner aleph, where the disc half-size is
                // tens of times the distance and the quad spans the whole viewport
                // (and straddles the near plane). Only reachable now that the
                // destination resolution works; a legitimate glimpse is hundreds of
                // units out (the fade starts at FadeStartDistance).
                if (dist <= t.Warp.Radius * radiusFactor) continue;

                if (sky is not null && EnsureTarget(sky, t.DestSector, t.Dest, dir)) switched = true;
                if (!_cache.TryGetValue(t.DestSector, out var entry) || !entry.Rendered) continue;

                shown.Add(new Visible(
                    t.Warp.WarpId,
                    entry.Target,
                    dist,
                    t.Warp.Radius,
                    dir,
                    s,
                    opacity,
                    radiusFactor));
            }
        }
        finally
        {
            // The destination skies were rendered through the SHARED tier renderer;
            // put it back on the sector this frame draws (skipping this would leave
            // the main sky stuck on the last glimpsed sector — the sky latch in
            // DrawSkyP576 would not re-set it).
            if (switched && sky is not null) sky.SetCluster(currentCluster);
        }

        var prevBlend = _gd.BlendState;
        var prevDepth = _gd.DepthStencilState;
        var prevRaster = _gd.RasterizerState;
        var prevSampler = _gd.SamplerStates[0];

        try
        {
            _gd.DepthStencilState = DepthStencilState.None;
            _gd.RasterizerState = RasterizerState.CullCounterClockwise;
            _gd.SamplerStates[0] = SamplerState.LinearClamp;
            _gd.BlendState = BlendState.AlphaBlend;

            for (int i = 0; i < shown.Count; i++)
            {
                var v = shown[i];
                float half = v.WarpRadius * v.RadiusFactor;

                var up = Math.Abs(Vector3.Dot(v.Direction, Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up;
                var right = Vector3.Normalize(Vector3.Cross(up, v.Direction));
                var upv = Vector3.Cross(v.Direction, right);
                var center = v.Direction * v.Dist;        // camera-relative (eye at origin)

                // Per-vertex alpha: radial edge-fade × the (approach/pulse) opacity.
                FillQuad(center, right, upv, half, v.Opacity);
                var effect = QuadEffect(v.Target, view, projection);
                foreach (var pass in effect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    _gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, _quad, 0, _quad.Length, QuadIndices, 0, 2);
                }

                // P585 — lens-flare burst at the funnel centre while the pulse is
                // strong, the centre is on screen, and it is not occluded (P547 depth).
                if (v.Strength >= PulseFlareThreshold)
                    DrawFlare(view, projection, center, v, flareOccluded, vp);
            }
        }
        finally
        {
            _gd.BlendState = prevBlend;
            _gd.DepthStencilState = prevDepth;
            _gd.RasterizerState = prevRaster;
            _gd.SamplerStates[0] = prevSampler;
        }
    }

    /// <summary>A glimpse that will be composited this frame.</summary>
    private readonly record struct Visible(
        short WarpId, Texture2D Target, float Dist, float WarpRadius, Vector3 Direction,
        float Strength, float Opacity, float RadiusFactor);

    /// <summary>
    /// Render (once) the destination sector's sky into its cached target, using
    /// the shared tier renderer. The renderer's cluster is switched to the
    /// destination — the caller MUST restore it (see <see cref="Draw"/>,
    /// <see cref="Warm"/>). Returns true when a target was rendered now.
    /// </summary>
    private bool EnsureTarget(ISkyRenderer sky, short destSector, DataClusterIGC destCluster, Vector3 lookDirection)
    {
        if (_cache.TryGetValue(destSector, out var e) && e.Rendered)
        {
            e.LastFrame = _frame;
            return false;
        }

        if (e is null)
        {
            e = new Entry
            {
                Target = new RenderTarget2D(_gd, TargetResolution, TargetResolution, false,
                    SurfaceFormat.Color, DepthFormat.Depth24),
            };
            _cache[destSector] = e;
        }

        sky.SetCluster(destCluster);

        var dir = lookDirection.LengthSquared() < 1e-6f ? Vector3.Forward : Vector3.Normalize(lookDirection);
        var up = Math.Abs(Vector3.Dot(dir, Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up;
        // Camera-relative view (eye at origin, looking along the warp direction):
        // the tier renderers draw camera-relative (dome/stars around the origin).
        var view = Matrix.CreateLookAt(Vector3.Zero, dir, up);
        var proj = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(50f), 1f, 10f, 80000f);

        var savedTargets = _gd.GetRenderTargets();
        _gd.SetRenderTarget(e.Target);
        _gd.Clear(Color.Transparent);
        // Render the destination sky into the small target (depth off inside the
        // sky renderer). Transparent clear + the colorkeyed dome/planet let the
        // empty sky show as transparent for the composite.
        sky.Draw(view, proj, Vector3.Zero);
        _gd.SetRenderTargets(savedTargets);

        e.Rendered = true;
        e.LastFrame = _frame;
        Evict();
        if (_dumpDir.Length > 0) DumpTarget(destSector, destCluster, e.Target);
        return true;
    }

    /// <summary>P862b proof aid — write a destination sky target to a PNG once, so
    /// the report can show the exact sky the glimpse prepared for a sector (and
    /// that it is the RESOLVED sector's idea, not a partner-warp id's).</summary>
    private void DumpTarget(short destSector, DataClusterIGC cluster, RenderTarget2D target)
    {
        try
        {
            System.IO.Directory.CreateDirectory(_dumpDir);
            string name = new string((cluster.Name ?? "").Select(
                c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
            string path = System.IO.Path.Combine(_dumpDir, $"glimpse_{destSector}_{name}.png");
            if (System.IO.File.Exists(path)) return;
            using var stream = System.IO.File.Create(path);
            target.SaveAsPng(stream, TargetResolution, TargetResolution);
            Console.WriteLine($"[glimpse] destination sky for {cluster.Name} ({destSector}) dumped to {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[glimpse] destination sky dump failed: {ex.Message}");
        }
    }

    /// <summary>Drop the least recently used destination target past
    /// <see cref="MaxCachedTargets"/> (never one used this frame).</summary>
    private void Evict()
    {
        while (_cache.Count > MaxCachedTargets)
        {
            short victim = 0;
            long oldest = long.MaxValue;
            foreach (var (id, entry) in _cache)
            {
                if (entry.LastFrame >= _frame) continue;
                if (entry.LastFrame < oldest) { oldest = entry.LastFrame; victim = id; }
            }
            if (oldest == long.MaxValue) return;            // everything is live this frame
            if (_cache.Remove(victim, out var dropped)) dropped.Target.Dispose();
        }
    }

    private BasicEffect QuadEffect(Texture2D texture, Matrix view, Matrix projection)
    {
        _quadEffect ??= new BasicEffect(_gd)
        {
            TextureEnabled = true,
            LightingEnabled = false,
            VertexColorEnabled = true,
        };
        var effect = _quadEffect;
        effect.Texture = texture;
        effect.Alpha = 1f;                                 // per-vertex alpha carries opacity
        effect.View = view;
        effect.Projection = projection;
        effect.World = Matrix.Identity;
        return effect;
    }

    /// <summary>Fill <see cref="_quad"/> with the disc corners (centre ± right/up
    /// × half) carrying the radial edge-fade × opacity in their alpha.</summary>
    private void FillQuad(Vector3 center, Vector3 right, Vector3 up, float half, float opacity)
    {
        _quad[0] = new VertexPositionColorTexture(center - right * half - up * half, Tint(EdgeFade(0f, 1f), opacity), new Vector2(0, 1));
        _quad[1] = new VertexPositionColorTexture(center + right * half - up * half, Tint(EdgeFade(1f, 1f), opacity), new Vector2(1, 1));
        _quad[2] = new VertexPositionColorTexture(center - right * half + up * half, Tint(EdgeFade(0f, 0f), opacity), new Vector2(0, 0));
        _quad[3] = new VertexPositionColorTexture(center + right * half + up * half, Tint(EdgeFade(1f, 0f), opacity), new Vector2(1, 0));
    }

    private static Color Tint(float edgeFade, float opacity)
        => new((byte)255, (byte)255, (byte)255, (byte)(255 * (edgeFade * opacity)));

    /// <summary>
    /// P585 — screen-space lens-flare burst from the funnel centre: an additive
    /// soft halo plus cross-streaks along the screen axes, sized by the pulse
    /// strength. Only drawn when the projected centre lies inside the viewport
    /// and <paramref name="flareOccluded"/> reports nothing closer than the aleph
    /// (the P547 depth). Uses a self-contained orthographic pass and only touches
    /// the blend state (restored after).
    /// </summary>
    private void DrawFlare(Matrix view, Matrix projection, Vector3 center,
        in Visible v, Func<short, int, int, float, bool> flareOccluded, Viewport vp)
    {
        // Project the (camera-relative) funnel centre to screen space.
        var p = Vector4.Transform(new Vector4(center, 1f), view * projection);
        if (p.W <= 0f) return;                                  // behind the camera
        int w = vp.Width, h = vp.Height;
        float sx = (p.X / p.W * 0.5f + 0.5f) * w;
        float sy = (0.5f - p.Y / p.W * 0.5f) * h;
        if (sx < 0 || sx > w || sy < 0 || sy > h) return;       // off screen → no flare
        float viewDepth = -(Vector3.Transform(center, view).Z); // positive view depth
        if (flareOccluded(v.WarpId, (int)sx, (int)sy, viewDepth)) return; // hidden behind geometry

        float s = v.Strength;
        var prevBlend = _gd.BlendState;
        var prevSampler = _gd.SamplerStates[0];
        _gd.BlendState = BlendState.Additive;
        _gd.SamplerStates[0] = SamplerState.LinearClamp;

        _flareEffect ??= new BasicEffect(_gd)
        {
            TextureEnabled = true,
            Texture = _flareTexture,
            VertexColorEnabled = true,
            LightingEnabled = false,
            View = Matrix.Identity,
            World = Matrix.Identity,
        };
        var effect = _flareEffect;
        effect.Projection = Matrix.CreateOrthographicOffCenter(0, w, h, 0, 0, 1);

        float half = FlareHaloHalfPx * s;                        // halo grows with the pulse
        var bright = new Color((byte)255, (byte)255, (byte)255, (byte)(255 * s));

        DrawQuad(effect, new Vector2(sx, sy), half, half, bright);

        // Cross-streaks along the screen axes (long, thin).
        float streakHalfX = FlareHaloHalfPx * FlareStreakRatio * s;
        float streakHalfY = FlareHaloHalfPx * 0.28f * s;
        DrawQuad(effect, new Vector2(sx, sy), streakHalfX, streakHalfY, bright);
        DrawQuad(effect, new Vector2(sx, sy), streakHalfY, streakHalfX, bright);

        _gd.BlendState = prevBlend;
        _gd.SamplerStates[0] = prevSampler;
    }

    private void DrawQuad(BasicEffect effect, Vector2 center, float halfW, float halfH, Color color)
    {
        _quad[0] = new VertexPositionColorTexture(new Vector3(center.X - halfW, center.Y - halfH, 0), color, new Vector2(0, 0));
        _quad[1] = new VertexPositionColorTexture(new Vector3(center.X + halfW, center.Y - halfH, 0), color, new Vector2(1, 0));
        _quad[2] = new VertexPositionColorTexture(new Vector3(center.X - halfW, center.Y + halfH, 0), color, new Vector2(0, 1));
        _quad[3] = new VertexPositionColorTexture(new Vector3(center.X + halfW, center.Y + halfH, 0), color, new Vector2(1, 1));
        foreach (var pass in effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            _gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, _quad, 0, _quad.Length, QuadIndices, 0, 2);
        }
    }

    /// <summary>Build a soft radial-gradient texture for the flare halo/streaks
    /// (bright centre fading to transparent at the rim) so no art asset is
    /// required for this opt-in effect.</summary>
    private static Texture2D CreateFlareTexture(GraphicsDevice gd)
    {
        const int n = FlareTextureSize;
        var data = new Color[n * n];
        float half = n / 2f;
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f - half) / half, dy = (y + 0.5f - half) / half;
                float r = MathF.Sqrt(dx * dx + dy * dy);
                float a = Math.Clamp(1f - r, 0f, 1f);
                a = a * a;                                        // smooth falloff
                data[y * n + x] = new Color((byte)255, (byte)255, (byte)255, (byte)(255 * a));
            }
        }
        var tex = new Texture2D(gd, n, n, false, SurfaceFormat.Color);
        tex.SetData(data);
        return tex;
    }

    /// <summary>Radial fade: 1 at the target centre, 0 toward the corners (so the
    /// glimpse dissolves before the funnel wall).</summary>
    private static float EdgeFade(float cx, float cy)
    {
        float dx = cx - 0.5f, dy = cy - 0.5f;
        float r = MathF.Sqrt(dx * dx + dy * dy) / 0.7071f;   // 1 at the corner
        return Math.Clamp(1f - r, 0f, 1f);
    }

    /// <summary>Drop the cache entry for a sector (e.g. on sector change).</summary>
    public void Invalidate(short destination)
    {
        if (_cache.Remove(destination, out var e)) e.Target.Dispose();
    }

    public void Dispose()
    {
        foreach (var e in _cache.Values) e.Target.Dispose();
        _cache.Clear();
        _flareTexture.Dispose();
        _quadEffect?.Dispose();
        _flareEffect?.Dispose();
        // The sky tiers are owned by Game1 (see SkySource) — never disposed here.
    }
}
