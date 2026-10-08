using Allegiance.Core;
using Allegiance.ClientCore.MonoGame.Assets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P576-B — the optional, NON-native "aleph destination glimpse": a faint view
/// of the DESTINATION sector's sky (P576-A's seeded stars + environment dome +
/// planet) shown inside each visible aleph's funnel centre, so you get a peek
/// at where the warp leads. Default OFF (a `--aleph-glimpse on` flourish);
/// every rule that governs the sky itself is the native port from
/// <see cref="NativeSkyRenderer"/> — this class only adds the "render the
/// destination sky into a small cached target, then composite it faintly into
/// the funnel" step.
///
/// Cost is bounded: one low-res <see cref="RenderTarget2D"/> (rendered once,
/// cached until the destination sector changes) per destination, and at most
/// <see cref="MaxGlimpses"/> warps are drawn (nearest-first; sub-threshold
/// on-screen funnels are skipped).
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

    /// <summary>Centre opacity of the glimpse (task: faint 15-35%; we use the
    /// upper bound so it reads clearly against the bright funnel).</summary>
    public const float CentreOpacity = 0.32f;

    private readonly GraphicsDevice _gd;
    private readonly IAssetSource _assets;
    private bool _hd;

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
    }

    /// <summary>P577 — render the glimpse in the <b>HD</b> tier (NASA star
    /// cubemap + HD nebula/planet) instead of native when the active sky tier
    /// is HD. Set once at startup; the glimpse then matches the arriving sky.</summary>
    public bool SkyIsHd { get => _hd; set => _hd = value; }

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
                // P577 — the glimpse renders the SAME tier as the arriving sky:
                // HD (NASA cubemap + HD nebula/planet) or native.
                Sky = _hd
                    ? new HdSkyRenderer(_gd, _assets)
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
        // empty sky show as transparent for the faint composite.
        e.Sky.Draw(view, proj, Vector3.Zero);
        _gd.SetRenderTargets(savedTargets);

        e.Dirty = false;
    }

    /// <summary>
    /// Draw a faint destination-sky glimpse into each qualifying aleph's funnel
    /// centre. <paramref name="warps"/> are the current sector's warps with a
    /// resolvable destination; the caller supplies the current sector id, the
    /// destination-cluster lookup, and the flight camera.
    /// </summary>
    public void Draw(
        IReadOnlyList<(DataWarpIGC Warp, DataClusterIGC Dest, float DistSq)> warps,
        Vector3 cameraEye,
        Matrix view,
        Matrix projection,
        Func<short, DataClusterIGC> resolve)
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
            _gd.BlendState = BlendState.AlphaBlend;
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
                float worldHalf = warp.Radius * 1.0f;   // funnel mouth radius
                float screenRadius = (worldHalf / dist) * halfVP * focus;
                if (screenRadius < MinScreenRadiusPx) continue;

                EnsureRendered(dest.ClusterId, dest, dir);

                if (!_cache.TryGetValue(dest.ClusterId, out var e) || e.Target is null)
                    continue;

                // Composite the destination-sky target as a camera-facing quad at
                // the warp, sized to the funnel mouth, faint (CentreOpacity at the
                // centre, 0 at the edge via the target's own transparent surround).
                var up2 = Math.Abs(Vector3.Dot(dir, Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up;
                var right = Vector3.Normalize(Vector3.Cross(up2, dir));
                var upv = Vector3.Cross(dir, right);
                var center = dir * dist;                 // camera-relative (eye at origin)
                float half = worldHalf;

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
                    Alpha = CentreOpacity,           // faint glimpse (15-35%)
                    VertexColorEnabled = true,
                    View = view,
                    Projection = projection,
                    World = Matrix.Identity,
                };
                // Per-vertex alpha: full at the centre of the target, 0 around the
                // rim, so the glimpse fades out before the funnel wall.
                var vc = new Color(255, 255, 255, 255);
                var fadeCorners = corners
                    .Select((c, i) =>
                    {
                        var (cx, cy) = i switch
                        {
                            0 => (0f, 1f), 1 => (1f, 1f), 2 => (0f, 0f), _ => (1f, 0f),
                        };
                        float a = EdgeFade(cx, cy);
                        return new VertexPositionColorTexture(c.Position, new Color((byte)255, (byte)255, (byte)255, (byte)(255 * a)), c.TextureCoordinate);
                    })
                    .ToArray();
                foreach (var pass in effect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    _gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, fadeCorners, 0, fadeCorners.Length, indices, 0, 2);
                }
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
    }
}
