using Allegiance.Core;
using Allegiance.ClientCore.MonoGame.Assets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P576-B + P585 — the optional, NON-native "aleph destination glimpse": a view
/// of the DESTINATION sector's sky (P576-A's seeded stars + environment dome +
/// planet) shown inside each visible aleph's funnel centre, so you get a peek
/// at where the warp leads. Default OFF (a `--aleph-glimpse on` flourish);
/// every rule that governs the sky itself is the native port from
/// <see cref="NativeSkyRenderer"/> — this class only adds the "render the
/// destination sky into a small cached target, then composite it into the
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
/// Cost is bounded: one low-res <see cref="RenderTarget2D"/> (rendered once,
/// cached until the destination sector changes) per destination, at most
/// <see cref="MaxGlimpses"/> warps are drawn (nearest-first; sub-threshold
/// on-screen funnels are skipped), and the flare is capped by the pulse
/// controller's own stacking limit. Render targets are only ever switched in
/// <see cref="EnsureRendered"/> (before the composite), never mid-frame during
/// the composite — the P582b lesson.
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

    private readonly GraphicsDevice _gd;
    private readonly IAssetSource _assets;
    private readonly Texture2D _flareTexture;
    private string _skyTier = "native";

    private sealed class Entry : IDisposable
    {
        public required ISkyRenderer Sky;
        public required RenderTarget2D Target;
        public short DestinationId = -1;
        public bool Dirty = true;

        public void Dispose() { Sky.Dispose(); Target.Dispose(); }
    }

    private readonly Dictionary<short, Entry> _cache = new();

    public AlephGlimpseRenderer(GraphicsDevice gd, IAssetSource assets)
    {
        _gd = gd;
        _assets = assets;
        _flareTexture = CreateFlareTexture(gd);
    }

    /// <summary>P577/P588 — render the glimpse in the SAME sky tier as the
    /// arriving sector: HD (NASA star cubemap + HD nebula/planet), native
    /// (P576 dome + planet), or black (P588: point stars + native planet, no
    /// dome). Set once at startup; the glimpse then matches the arriving sky.</summary>
    public string SkyTier { get => _skyTier; set => _skyTier = value; }

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

    /// <summary>
    /// Render the destination sky for <paramref name="destination"/> (once,
    /// cached) into its target, as seen along <paramref name="lookDirection"/>
    /// (the direction from the current camera through the aleph). Uses the real
    /// <see cref="NativeSkyRenderer"/> so the glimpse is the identical native sky
    /// the sector shows when you actually arrive there.
    /// </summary>
    private void EnsureRendered(short destination, DataClusterIGC destCluster, Vector3 lookDirection)
    {
        if (_cache.TryGetValue(destination, out var e) && !e.Dirty)
            return;

        if (e is null)
        {
            e = new Entry
            {
                // P577/P588 — the glimpse renders the SAME tier as the arriving
                // sky: HD (NASA cubemap + HD nebula/planet), native (P576 dome +
                // planet), or black (P588: point stars + native planet, no dome).
                Sky = string.Equals(_skyTier, "hd", StringComparison.OrdinalIgnoreCase) ? new HdSkyRenderer(_gd, _assets)
                    : string.Equals(_skyTier, "black", StringComparison.OrdinalIgnoreCase) ? new BlackSkyRenderer(_gd, _assets)
                    : new NativeSkyRenderer(_gd, _assets),
                Target = new RenderTarget2D(_gd, TargetResolution, TargetResolution, false,
                    SurfaceFormat.Color, DepthFormat.Depth24),
            };
            _cache[destination] = e;
        }

        e.Sky.SetCluster(destCluster);
        e.DestinationId = destination;

        var up = Math.Abs(Vector3.Dot(lookDirection, Vector3.Up)) > 0.99f
            ? Vector3.Forward : Vector3.Up;
        // Camera-relative view (eye at origin, looking along the warp direction):
        // NativeSkyRenderer draws camera-relative (dome/stars around the origin).
        var view = Matrix.CreateLookAt(Vector3.Zero, lookDirection, up);
        var proj = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(50f), 1f, 10f, 80000f);

        var savedTargets = _gd.GetRenderTargets();
        _gd.SetRenderTarget(e.Target);
        _gd.Clear(Color.Transparent);
        // Render the destination sky into the small target (depth off inside the
        // sky renderer). Transparent clear + the colorkeyed dome/planet let the
        // empty sky show as transparent for the composite.
        e.Sky.Draw(view, proj, Vector3.Zero);
        _gd.SetRenderTargets(savedTargets);

        e.Dirty = false;
    }

    /// <summary>
    /// Draw the destination-sky glimpse into each qualifying aleph's funnel
    /// centre, resting strength faded in by approach distance and (when a
    /// transit pulse is active on the warp, per <paramref name="pulseStrength"/>)
    /// opened wide plus a lens-flare burst. <paramref name="warps"/> are the
    /// current sector's warps with a resolvable destination; the caller supplies
    /// the destination-cluster lookup, the per-warp pulse strength and the
    /// occlusion test for the flare.
    /// </summary>
    public void Draw(
        IReadOnlyList<(DataWarpIGC Warp, DataClusterIGC Dest, float DistSq)> warps,
        Vector3 cameraEye,
        Matrix view,
        Matrix projection,
        Func<short, DataClusterIGC> resolve,
        Func<short, float> pulseStrength,
        Func<int, int, float, bool> flareOccluded)
    {
        if (warps.Count == 0) return;

        // Nearest-first, bounded (cost: at most MaxGlimpses composite quads).
        var ordered = warps.OrderBy(w => w.DistSq).Take(MaxGlimpses).ToList();

        var vp = _gd.Viewport;
        float focus = projection.M22;                 // cot(vFOV/2)
        float halfVP = vp.Height / 2f;

        var prevBlend = _gd.BlendState;
        var prevDepth = _gd.DepthStencilState;
        var prevRaster = _gd.RasterizerState;
        var prevSampler = _gd.SamplerStates[0];

        try
        {
            _gd.DepthStencilState = DepthStencilState.None;
            _gd.RasterizerState = RasterizerState.CullCounterClockwise;
            _gd.SamplerStates[0] = SamplerState.LinearClamp;

            foreach (var (warp, dest, distSq) in ordered)
            {
                float dist = MathF.Sqrt(distSq);
                if (dist < 1f) continue;

                var warpPos = new Vector3(warp.Position[0], warp.Position[1], warp.Position[2]);
                var dir = warpPos - cameraEye;
                if (dir.LengthSquared() < 1e-6f) continue;
                dir = Vector3.Normalize(dir);

                // On-screen funnel inner radius (px) at this distance.
                float screenRadius = (warp.Radius / dist) * halfVP * focus;
                if (screenRadius < MinScreenRadiusPx) continue;

                // P585 — resting strength fades in with approach; a transit pulse
                // pushes the disc wider and far more opaque (opens wide).
                float s = Math.Max(0f, pulseStrength(warp.WarpId));
                float fade = ApproachFade(dist);

                // P862 — a glimpse whose resting fade is zero and that has no
                // transit pulse draws NOTHING (it is past FadeStartDistance). Do
                // not build its destination sky behind it: entering a sector used
                // to prepare up to MaxGlimpses destination skies — a 2048² planet,
                // the p589obj library and a per-destination sky renderer each — in
                // the ONE frame the new sector first appeared, which measured as a
                // second ~1 s stall on top of the sector's own assets. The sky is
                // built the moment the aleph is close enough to show one (or when
                // a transit pulse fires), which is where it has always been built.
                if (s <= 0.001f && RestingCentreOpacity * fade <= 0.001f) continue;

                EnsureRendered(dest.ClusterId, dest, dir);

                if (!_cache.TryGetValue(dest.ClusterId, out var e) || e.Target is null)
                    continue;

                float opacity = Mix(RestingCentreOpacity * fade, PulseCentreOpacity, s);
                float radiusFactor = Mix(RestingDiscRadiusFactor, PulseDiscRadiusFactor, s);

                var up2 = Math.Abs(Vector3.Dot(dir, Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up;
                var right = Vector3.Normalize(Vector3.Cross(up2, dir));
                var upv = Vector3.Cross(dir, right);
                var center = dir * dist;                 // camera-relative (eye at origin)
                float half = warp.Radius * radiusFactor;

                var corners = new[]
                {
                    new VertexPositionTexture(center - right * half - upv * half, new Vector2(0, 1)),
                    new VertexPositionTexture(center + right * half - upv * half, new Vector2(1, 1)),
                    new VertexPositionTexture(center - right * half + upv * half, new Vector2(0, 0)),
                    new VertexPositionTexture(center + right * half + upv * half, new Vector2(1, 0)),
                };
                var indices = new short[] { 0, 1, 2, 2, 1, 3 };

                using var effect = new BasicEffect(_gd)
                {
                    TextureEnabled = true,
                    Texture = e.Target,
                    LightingEnabled = false,
                    Alpha = 1f,                           // per-vertex alpha carries opacity (below)
                    VertexColorEnabled = true,
                    View = view,
                    Projection = projection,
                    World = Matrix.Identity,
                };
                // Per-vertex alpha: radial edge-fade × the (approach/pulse) opacity.
                _gd.BlendState = BlendState.AlphaBlend;
                var fadeCorners = corners
                    .Select((c, i) =>
                    {
                        var (cx, cy) = i switch
                        {
                            0 => (0f, 1f), 1 => (1f, 1f), 2 => (0f, 0f), _ => (1f, 0f),
                        };
                        float a = EdgeFade(cx, cy) * opacity;
                        return new VertexPositionColorTexture(c.Position, new Color((byte)255, (byte)255, (byte)255, (byte)(255 * a)), c.TextureCoordinate);
                    })
                    .ToArray();
                foreach (var pass in effect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    _gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, fadeCorners, 0, fadeCorners.Length, indices, 0, 2);
                }

                // P585 — lens-flare burst at the funnel centre while the pulse is
                // strong, the centre is on screen, and it is not occluded (P547 depth).
                if (s >= PulseFlareThreshold)
                    DrawFlare(view, projection, center, dist, s, flareOccluded, vp);
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

    /// <summary>
    /// P585 — screen-space lens-flare burst from the funnel centre: an additive
    /// soft halo plus cross-streaks along the screen axes, sized by the pulse
    /// strength <paramref name="s"/>. Only drawn when the projected centre lies
    /// inside the viewport and <paramref name="flareOccluded"/> reports nothing
    /// closer than the aleph (the P547 depth). Uses a self-contained orthographic
    /// pass and only touches the blend state (restored after).
    /// </summary>
    private void DrawFlare(Matrix view, Matrix projection, Vector3 center,
        float dist, float s, Func<int, int, float, bool> flareOccluded, Viewport vp)
    {
        // Project the (camera-relative) funnel centre to screen space.
        var p = Vector4.Transform(new Vector4(center, 1f), view * projection);
        if (p.W <= 0f) return;                                  // behind the camera
        int w = vp.Width, h = vp.Height;
        float sx = (p.X / p.W * 0.5f + 0.5f) * w;
        float sy = (0.5f - p.Y / p.W * 0.5f) * h;
        if (sx < 0 || sx > w || sy < 0 || sy > h) return;       // off screen → no flare
        float viewDepth = -(Vector3.Transform(center, view).Z); // positive view depth
        if (flareOccluded((int)sx, (int)sy, viewDepth)) return; // hidden behind geometry

        var prevBlend = _gd.BlendState;
        var prevSampler = _gd.SamplerStates[0];
        _gd.BlendState = BlendState.Additive;
        _gd.SamplerStates[0] = SamplerState.LinearClamp;

        using var effect = new BasicEffect(_gd)
        {
            TextureEnabled = true,
            Texture = _flareTexture,
            VertexColorEnabled = true,
            LightingEnabled = false,
            View = Matrix.Identity,
            Projection = Matrix.CreateOrthographicOffCenter(0, w, h, 0, 0, 1),
            World = Matrix.Identity,
        };

        float half = FlareHaloHalfPx * s;                        // halo grows with the pulse
        var bright = new Color((byte)255, (byte)255, (byte)255, (byte)(255 * s));

        DrawQuad(effect, _gd, new Vector2(sx, sy), half, half, bright, 0f);

        // Cross-streaks along the screen axes (long, thin).
        float streakHalfX = FlareHaloHalfPx * FlareStreakRatio * s;
        float streakHalfY = FlareHaloHalfPx * 0.28f * s;
        DrawQuad(effect, _gd, new Vector2(sx, sy), streakHalfX, streakHalfY, bright, 0f);
        DrawQuad(effect, _gd, new Vector2(sx, sy), streakHalfY, streakHalfX, bright, 0f);

        _gd.BlendState = prevBlend;
        _gd.SamplerStates[0] = prevSampler;
    }

    private static void DrawQuad(BasicEffect effect, GraphicsDevice gd,
        Vector2 center, float halfW, float halfH, Color color, float angle)
    {
        float c = MathF.Cos(angle), sn = MathF.Sin(angle);
        static Vector2 Rot(Vector2 d, float c, float s) => new(d.X * c - d.Y * s, d.X * s + d.Y * c);
        var tl = Rot(new Vector2(-halfW, -halfH), c, sn);
        var tr = Rot(new Vector2(+halfW, -halfH), c, sn);
        var bl = Rot(new Vector2(-halfW, +halfH), c, sn);
        var br = Rot(new Vector2(+halfW, +halfH), c, sn);
        var verts = new[]
        {
            new VertexPositionColorTexture(new Vector3(center + tl, 0), color, new Vector2(0, 0)),
            new VertexPositionColorTexture(new Vector3(center + tr, 0), color, new Vector2(1, 0)),
            new VertexPositionColorTexture(new Vector3(center + bl, 0), color, new Vector2(0, 1)),
            new VertexPositionColorTexture(new Vector3(center + br, 0), color, new Vector2(1, 1)),
        };
        var indices = new short[] { 0, 1, 2, 2, 1, 3 };
        foreach (var pass in effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, verts, 0, verts.Length, indices, 0, 2);
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
        if (_cache.Remove(destination, out var e)) e.Dispose();
    }

    public void Dispose()
    {
        foreach (var e in _cache.Values) e.Dispose();
        _cache.Clear();
        _flareTexture.Dispose();
    }
}
