using System;
using System.Collections.Generic;
using Allegiance.ClientCore.State;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XnaVec = Microsoft.Xna.Framework.Vector3;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P573/P579/P834 Technique A — real-time raymarched volumetric explosions (DesktopGL).
///
/// Each native sub-volume (see <see cref="VolumetricExplosionSampler"/>) is a
/// camera-facing billboard quad; <c>Volumetric.fx</c> intersects the view ray
/// with the bounding sphere (early-out + <c>clip</c> outside it — P579, no
/// square edges), then marches sampling animated ALU fBm density, shaded by a
/// temperature ramp tinted by the native type. P579 coverage LOD: the step
/// count goes by on-screen COVERAGE (big on-screen → FEWER, longer steps; small
/// → more steps is fine), and <see cref="VolumeBudget.AllocateLevels(IReadOnlyList{VolumeBudget.VolumeCost}, float)"/>
/// caps the total per-frame pixel-steps, degrading the LARGEST-coverage volumes
/// first.
///
/// P834 (near-cost) changes:
/// <list type="bullet">
/// <item><b>Resolution-aware policy.</b> Coverage is measured as a FRACTION of
/// the viewport height (not absolute px), the per-frame budget is per screen
/// pixel (<see cref="VolumeBudget.MaxPixelStepsFor"/>), and a new
/// <see cref="VolumeLod.Level.Minimal"/> 32-step level exists so a
/// screen-filling / camera-inside volume can actually be made affordable.</item>
/// <item><b>Quarter-res pass.</b> Volumes covering ≥ 75 % of the screen height
/// render into a QUARTER-RESOLUTION target (1/16 the marched pixels) and
/// composite up additively with the same path as the half-res pass. Half of a
/// 4K/5K2K frame was still &gt; 1080p, which is why the old half-res-only ladder
/// stalled when the camera got close.</item>
/// <item><b>Camera inside / near.</b> The shader clamps the march to the visible
/// depth range — start at the near plane, stop at the opaque scene's linear
/// depth (see <see cref="VolumetricGeometry.ClampMarchRange"/>, mirrored in
/// <c>Volumetric.fx</c>). With the eye inside the bounding sphere the ray–sphere
/// root t0 is 0, so P579 marched the whole chord (and everything behind hulls).</item>
/// <item><b>GPU-agnostic adaptive budget</b> (<see cref="VolumeBudgetController"/>)
/// scales the budget from measured frame time, bounded [0.25, 1] and smooth, so
/// a slower GPU degrades instead of stalling.</item>
/// </list>
///
/// Optional P547 depth-soft intersection melts the volume onto hulls/rocks/stations.
///
/// Positions are camera-relative (<see cref="VolumetricGeometry.CameraRelative"/>)
/// — the game's view matrix keeps the eye at the origin; the P573 spike's
/// omission of this subtract is the "no explosions" bug found live.
///
/// State pinning (P568 lesson): rasterizer (Cull None), blend (additive) and
/// depth (read-only) are set for the draw and RESTORED afterward.
/// </summary>
public sealed class VolumetricRenderer : IDisposable
{
    /// <summary>Per-volume vertex: corner position + the quad's own attributes
    /// (repeated across the quad), matching <c>Volumetric.fx</c>'s VS_IN.</summary>
    private struct VolumeVertex : IVertexType
    {
        public XnaVec Position;
        public XnaVec Center;
        public float Radius;
        public XnaVec Tint;
        public float Age;
        public float Life;
        public float Seed;

        public static readonly VertexDeclaration Declaration = new(
            new VertexElement(0,  VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
            new VertexElement(12, VertexElementFormat.Vector3, VertexElementUsage.TextureCoordinate, 0),
            new VertexElement(24, VertexElementFormat.Single,  VertexElementUsage.TextureCoordinate, 1),
            new VertexElement(28, VertexElementFormat.Vector3, VertexElementUsage.TextureCoordinate, 2),
            new VertexElement(40, VertexElementFormat.Single,  VertexElementUsage.TextureCoordinate, 3),
            new VertexElement(44, VertexElementFormat.Single,  VertexElementUsage.TextureCoordinate, 4),
            new VertexElement(48, VertexElementFormat.Single,  VertexElementUsage.TextureCoordinate, 5));

        VertexDeclaration IVertexType.VertexDeclaration => Declaration;
    }

    private readonly Effect _effect;
    private readonly RasterizerState _cullNone;
    private readonly BlendState _additive;
    private readonly BlendState _addOneOne;
    private readonly DepthStencilState _depthRead;

    // Effect parameters (all set from code every frame — GL ignores defaults).
    private readonly EffectParameter _pWV, _pWVP, _pInvViewport, _pNear, _pInvRange;
    private readonly EffectParameter _pEye, _pLight, _pTime, _pDepth, _pBrightness;
    /// <summary>P834b — camera forward axis uniform; null with a pre-P834b
    /// compiled shader (see the constructor).</summary>
    private readonly EffectParameter? _pViewForward;

    private readonly XnaVec[] _quadCorners = { new(-1,-1,0), new(1,-1,0), new(-1,1,0), new(1,1,0) };
    private readonly short[] _quadIndices = { 0,1,2, 1,3,2 };

    /// <summary>P579 — additive-brightness scale applied in the shader
    /// (<c>Brightness</c> effect parameter). Calibrated in the REAL window
    /// against the Enhanced fireball (see the P579 report): at 30 the fighter
    /// death (150/600/1500 u) reads as bright as or brighter than Enhanced
    /// (mean luminance 5.4 vs 5.2, peak saturated in both) while the dense-
    /// combat blown-out stays ≪ the ≤20 % P546 budget (0.10 % vs Enhanced's
    /// 1.45 %). Overridable via <c>ALLEGIANCE_VOL_BRIGHTNESS</c>.</summary>
    public const float BrightnessDefault = 30f;

    /// <summary>P579 — a volume whose projected diameter (px, at the 1440p
    /// reference height) is at least this is rendered at half resolution (¼ the
    /// fill) to tame the near screen-filling blast's fill rate. Costs a little
    /// sharpness on the very biggest volumes; the far/small ones stay full-res.
    /// P834 — this is now a FRACTION of the viewport height (240/1440 ≈ 16.7 %),
    /// so the same on-screen slice is treated the same at 4K/5K2K; volumes at
    /// ≥ 75 % of the height upgrade further to quarter-res
    /// (<see cref="VolumeScreenPolicy.QuarterResMinFraction"/>).</summary>
    public const float NearHalfResMinPx = 240f;

    private readonly float _halfResMinFraction;

    private float _brightness;
    private RenderTarget2D? _halfRes;
    private int _halfResW, _halfResH;
    private RenderTarget2D? _quarterRes;
    private int _quarterResW, _quarterResH;
    private SpriteBatch? _scaleBatch;

    // Per-render-scale, per-LOD vertex buckets (index = (int)VolumeLod.Level).
    private readonly List<VolumeVertex>[] _fullBuckets = NewBuckets();
    private readonly List<VolumeVertex>[] _halfBuckets = NewBuckets();
    private readonly List<VolumeVertex>[] _quarterBuckets = NewBuckets();

    private readonly VolumeBudgetController _budget = new();

    /// <summary>P834 — the adaptive budget controller (bounded scale driven by
    /// measured frame time). The game feeds it every frame; exposed for the
    /// P834 harness/tests.</summary>
    public VolumeBudgetController Budget => _budget;

    /// <summary>P834 diagnostics (the cost harness logs these next to the GPU
    /// time; inert for gameplay).</summary>
    public int LastVolumeCount { get; private set; }
    public int LastFullResVolumes { get; private set; }
    public int LastHalfResVolumes { get; private set; }
    public int LastQuarterResVolumes { get; private set; }
    public float LastPixelSteps { get; private set; }
    public float LastBudget { get; private set; }

    public VolumetricRenderer(Effect effect)
    {
        _effect = effect;
        float halfPx = double.TryParse(System.Environment.GetEnvironmentVariable("ALLEGIANCE_VOL_HALFRESPX"),
            System.Globalization.CultureInfo.InvariantCulture, out var hrp) && hrp > 0 ? (float)hrp : NearHalfResMinPx;
        _halfResMinFraction = halfPx / VolumeLod.ReferenceHeight;
        _brightness = BrightnessDefault;
        if (double.TryParse(System.Environment.GetEnvironmentVariable("ALLEGIANCE_VOL_BRIGHTNESS"),
                System.Globalization.CultureInfo.InvariantCulture, out var b) && b > 0)
            _brightness = (float)b;
        _cullNone = new RasterizerState { CullMode = CullMode.None };
        _additive = new BlendState
        {
            ColorSourceBlend = Blend.SourceAlpha,
            ColorDestinationBlend = Blend.One,
            AlphaSourceBlend = Blend.SourceAlpha,
            AlphaDestinationBlend = Blend.One,
        };
        _addOneOne = new BlendState
        {
            ColorSourceBlend = Blend.One,   // raw-add the half/quarter-res accumulation
            ColorDestinationBlend = Blend.One,
            AlphaSourceBlend = Blend.One,
            AlphaDestinationBlend = Blend.One,
        };
        _depthRead = DepthStencilState.DepthRead;
        _pWV = Param("WorldView"); _pWVP = Param("WorldViewProj");
        _pInvViewport = Param("InvViewportSize"); _pNear = Param("Near");
        _pInvRange = Param("InvRange"); _pEye = Param("EyePos");
        // P834b — optional on purpose: a pre-P834b compiled shader (an older
        // committed .mgfxo, or the A/B harness measuring master/P834 bytecode)
        // has no ViewForward and must still render with its own clamp. The
        // shipped .fx/.mgfxo pair always matches (P547 staleness test).
        _pViewForward = Param("ViewForward");
        if (_pViewForward is null)
            Console.WriteLine("[volumetric] shader has no ViewForward (pre-P834b bytecode): "
                              + "the near/scene clamp falls back to the shader's own view axis");
        _pLight = Param("LightDir"); _pTime = Param("Time"); _pDepth = Param("DepthTexture");
        _pBrightness = Param("Brightness");
    }

    private EffectParameter Param(string n) => _effect.Parameters[n];

    private static List<VolumeVertex>[] NewBuckets()
    {
        var buckets = new List<VolumeVertex>[VolumeLod.LevelCount];
        for (int i = 0; i < buckets.Length; i++) buckets[i] = new List<VolumeVertex>();
        return buckets;
    }

    /// <summary>Whether the shader has every parameter and at least the Med technique
    /// (if not, the tier falls back — the caller checks before enabling Volumetric).</summary>
    public bool IsUsable =>
        _pWV is not null && _pWVP is not null && _pInvViewport is not null && _pNear is not null
        && _pInvRange is not null && _pEye is not null
        && _pLight is not null && _pTime is not null
        && _pDepth is not null && _pBrightness is not null
        && _effect.Techniques[VolumeLod.TechniqueName(VolumeLod.Level.Med)] is not null;

    /// <summary>Optional forced LOD level. When set, every unit renders at this
    /// level (bypasses coverage LOD and the budget) — used by the cost harness to
    /// measure the LOD/budget delta. Null = normal coverage LOD + budget.</summary>
    public VolumeLod.Level? ForcedLevel { get; set; }

    /// <summary>Per-frame pixel-step budget override. Null = the shipped
    /// resolution-aware <see cref="VolumeBudget.MaxPixelStepsFor"/> × the
    /// adaptive scale. Set to <see cref="float.MaxValue"/> to disable the budget
    /// (measure the unfiltered worst case).</summary>
    public float? BudgetOverride { get; set; }

    /// <summary>P834 — feed the adaptive budget controller one measured frame
    /// time (ms), normally from the game's Update. Bounded + smooth; does nothing
    /// when the value is not finite/positive.</summary>
    public void ObserveFrame(double frameMs) => _budget.Observe(frameMs);

    public void Draw(
        IReadOnlyList<ExplosionEntity> explosions,
        short ownSectorId,
        XnaVec cameraEye,
        Matrix view,
        Matrix projection,
        float time,
        SoftParticleContext soft, long? nowLocalMs = null)
    {
        if (!IsUsable) return;

        var gd = _effect.GraphicsDevice;
        var vp = view * projection;

        // ---- Gather sub-volumes across all live explosions -----------------
        var all = new List<VolumetricSubUnit>();
        for (int ei = 0; ei < explosions.Count; ei++)
        {
            var e = explosions[ei];
            if (e.ClusterId != ownSectorId) continue;
            ExplosionPreset preset;
            try { preset = ExplosionPresets.For(e.Type); }
            catch (ArgumentOutOfRangeException) { continue; }
            float elapsed = Math.Max(0f, (float)((nowLocalMs ?? Environment.TickCount64) - e.ReceivedLocalMs) / 1000f);
            var pos = new SimVec(e.Position.X, e.Position.Y, e.Position.Z);
            var vel = new SimVec(e.Velocity.X, e.Velocity.Y, e.Velocity.Z);
            all.AddRange(VolumetricExplosionSampler.SampleUnits(preset, elapsed, pos, vel));
        }
        if (all.Count == 0) return;

        // ---- Volume cap (nearest first) ------------------------------------
        var eye = new SimVec(cameraEye.X, cameraEye.Y, cameraEye.Z);
        var kept = VolumeBudget.TrimNearest(all, eye);

        // Camera billboard basis + projected-diameter scale.
        // Right/up = world axes of the view basis = COLUMNS of the lookAt matrix
        // (right = column0, up = column1); rows would be a mixed basis.
        var camRight = new XnaVec(view.M11, view.M21, view.M31);
        var camUp = new XnaVec(view.M12, view.M22, view.M32);
        float pxPerUnit = gd.Viewport.Height * projection.M22;   // diameterPx = radius * pxPerUnit / dist

        // ---- P834: coverage → (render scale, nominal LOD); pixel budget -----
        int viewportW = gd.Viewport.Width, viewportH = gd.Viewport.Height;
        float viewportPixels = Math.Max(1f, (float)viewportW * viewportH);
        var costs = new VolumeBudget.VolumeCost[kept.Count];
        for (int i = 0; i < kept.Count; i++)
        {
            var u = kept[i].Unit;
            float dx = u.Center.X - eye.X, dy = u.Center.Y - eye.Y, dz = u.Center.Z - eye.Z;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            float rPx = u.PeakRadius * pxPerUnit / Math.Max(1e-4f, dist);
            float diamPx = Math.Max(1f, rPx * 2f);
            float fraction = diamPx / Math.Max(1f, viewportH);
            // A volume can never shade more pixels than the frame holds; without
            // the clamp a camera-inside volume reported a 650 M px area and the
            // budget degraded everything (P834).
            float areaPx = Math.Min(diamPx * diamPx, viewportPixels);
            var scale = VolumeScreenPolicy.ScaleForDiameterFraction(fraction, _halfResMinFraction);
            costs[i] = new VolumeBudget.VolumeCost(fraction, areaPx, scale);
        }

        float budget = BudgetOverride ?? VolumeBudget.MaxPixelStepsFor(viewportW, viewportH) * _budget.Scale;
        LastBudget = budget;
        VolumeLod.Level[] levels;
        if (ForcedLevel is { } forced)
        {
            levels = new VolumeLod.Level[kept.Count];
            Array.Fill(levels, forced);
        }
        else
        {
            levels = VolumeBudget.AllocateLevels(costs, budget);
        }
        LastVolumeCount = kept.Count;
        LastPixelSteps = VolumeBudget.TotalPixelSteps(costs, levels);
        LastFullResVolumes = LastHalfResVolumes = LastQuarterResVolumes = 0;

        // ---- Lay quads into full / half / quarter buckets --------------------
        for (int i = 0; i < _fullBuckets.Length; i++)
        {
            _fullBuckets[i].Clear(); _halfBuckets[i].Clear(); _quarterBuckets[i].Clear();
        }

        for (int i = 0; i < kept.Count; i++)
        {
            var u = kept[i].Unit;
            var center = VolumetricGeometry.CameraRelative(u.Center, eye);   // camera-relative
            int li = (int)levels[i];
            var buckets = costs[i].Scale switch
            {
                VolumeRenderScale.Quarter => _quarterBuckets,
                VolumeRenderScale.Half => _halfBuckets,
                _ => _fullBuckets,
            };
            switch (costs[i].Scale)
            {
                case VolumeRenderScale.Quarter: LastQuarterResVolumes++; break;
                case VolumeRenderScale.Half: LastHalfResVolumes++; break;
                default: LastFullResVolumes++; break;
            }
            var target = buckets[li];

            float age = Math.Max(0f, u.Elapsed - u.Born);
            float r = u.PeakRadius;
            // P582b — growth/cool run on the FILM clock (u.FilmLife), not the
            // full fire+smoke-tail life, so the fireball peaks at native film
            // cadence (~0.3-0.5 s for a fighter) instead of swelling across the
            // whole ~3.9 s tail. The shader reads vertex Life as that film clock.
            float shaderLife = u.FilmLife > 0f ? u.FilmLife : u.Life;
            for (int c = 0; c < 4; c++)
            {
                var cn = _quadCorners[c];
                var world = new XnaVec(
                    center.X + camRight.X * cn.X * r + camUp.X * cn.Y * r,
                    center.Y + camRight.Y * cn.X * r + camUp.Y * cn.Y * r,
                    center.Z + camRight.Z * cn.X * r + camUp.Z * cn.Y * r);
                target.Add(new VolumeVertex
                {
                    Position = world,
                    Center = new XnaVec(center.X, center.Y, center.Z),
                    Radius = u.PeakRadius,
                    Tint = new XnaVec(u.Tint.X, u.Tint.Y, u.Tint.Z),
                    Age = age,
                    Life = shaderLife,
                    Seed = u.Seed,
                });
            }
        }

        // ---- Set global parameters (view matrices etc. once) ---------------
        _pWV.SetValue(view);
        _pWVP.SetValue(vp);
        _pNear.SetValue(soft.Near > 0 ? soft.Near : 1f);
        _pInvRange.SetValue(soft.Far > soft.Near ? 1f / (soft.Far - soft.Near) : 1f / 7899f);
        _pEye.SetValue(XnaVec.Zero);
        // P834b — the volume shader clamps its march with the ray's VIEW-AXIS
        // cosine, and the positions it receives are camera-relative WORLD ones,
        // so it needs the camera's forward axis: -zaxis = -(view.M13,M23,M33)
        // (XNA's lookAt stores zaxis = normalize(eye - target) in column 3).
        // VolumetricGeometry.ViewForward derives exactly this; the P834b tests pin
        // that the clamp is orientation-independent through the CPU mirror.
        if (_pViewForward is not null)
        {
            var viewForward = VolumetricGeometry.ViewForward(view.M13, view.M23, view.M33);
            _pViewForward.SetValue(new XnaVec(viewForward.X, viewForward.Y, viewForward.Z));
        }
        _pLight.SetValue(XnaVec.Normalize(new XnaVec(0.35f, 0.6f, -0.72f)));
        _pTime.SetValue(time);
        _pBrightness.SetValue(_brightness);
        _pDepth.SetValue(soft.Depth ?? (_farDepth ??= _MakeFarDepth(gd)));

        var fullInv = new Vector2(1f / viewportW, 1f / viewportH);

        // ---- Small/far volumes: full-res into the current target ------------
        RenderBuckets(gd, _fullBuckets, fullInv);

        // ---- Near/large volumes: half- and quarter-res + additive upsample --
        RenderScaledPass(gd, _halfBuckets, EnsureHalfRes(gd), "volumetric half-res pass");
        RenderScaledPass(gd, _quarterBuckets, EnsureQuarterRes(gd), "volumetric quarter-res pass");

        // ---- Restore pinned state (P568) -----------------------------------
        gd.BlendState = BlendState.Opaque;
        gd.RasterizerState = RasterizerState.CullCounterClockwise;
        gd.DepthStencilState = DepthStencilState.Default;

        // P582 — this draw sampled the linear-depth render target
        // (DepthTexture = soft.Depth). Unbind it from every texture unit so the
        // NEXT frame's depth pass can SetRenderTarget(depthRT) without an
        // OpenGL texture/framebuffer feedback loop (which broke the depth pass
        // and hid the opaque world — see DepthTextureBinding). The 1x1 "far"
        // fallback (not a render target) needs no unbind.
        if (soft.Depth is { } depthRt)
            DepthTextureBinding.UnbindDepth(gd, depthRt);
    }

    /// <summary>Draw every bucket's quads with the given inv-viewport (full-,
    /// half- or quarter-res), one technique per bucket. Blend/raster/depth are
    /// set per bucket exactly as before.</summary>
    private void RenderBuckets(GraphicsDevice gd, List<VolumeVertex>[] buckets, Vector2 invViewport)
    {
        _pInvViewport.SetValue(invViewport);
        for (int li = 0; li < VolumeLod.LevelCount; li++)
        {
            var verts = buckets[li];
            if (verts.Count == 0) continue;
            string techName = VolumeLod.TechniqueName((VolumeLod.Level)li);
            var tech = _effect.Techniques[techName];
            if (tech is null) continue;
            _effect.CurrentTechnique = tech;

            var arr = verts.ToArray();
            int quads = arr.Length / 4;

            gd.BlendState = _additive;
            gd.RasterizerState = _cullNone;
            gd.DepthStencilState = _depthRead;
            _effect.CurrentTechnique.Passes[0].Apply();

            var quadVerts = new VolumeVertex[4];
            for (int q = 0; q < quads; q++)
            {
                Array.Copy(arr, q * 4, quadVerts, 0, 4);
                gd.DrawUserIndexedPrimitives(
                    PrimitiveType.TriangleList, quadVerts, 0, 4,
                    _quadIndices, 0, 2);
            }
        }
    }

    /// <summary>P834 — render a bucket set into a downscaled target and composite
    /// it up raw-additively. The P582b black-world rule is honoured for both the
    /// half- and quarter-res excursions: the ACTUAL previous render targets are
    /// saved and restored (never hardcoded null), the DEBUG guard asserts the
    /// restore is lossless, and the caller's viewport is restored.</summary>
    private void RenderScaledPass(GraphicsDevice gd, List<VolumeVertex>[] buckets, RenderTarget2D target, string context)
    {
        bool any = false;
        for (int li = 0; li < VolumeLod.LevelCount; li++) if (buckets[li].Count > 0) { any = true; break; }
        if (!any) return;

        var savedViewport = gd.Viewport;
        // P582b — capture the ACTUAL render targets this pass temporarily leaves,
        // and restore them (not hardcoded null) when done. P579 did
        // gd.SetRenderTarget(null) here, which (a) dropped the real colour target
        // (SceneRT/capture) and (b) wiped the opaque scene already drawn
        // (DiscardContents backbuffer) — the black world. The backbuffer/SceneRT/
        // _capTarget are now PreserveContents (Game1.cs, VfxPostProcessor,
        // Lifecycle) so the excursion is lossless, and the DEBUG guard asserts the
        // restore rule (identical binding + preserving).
        bool noFix = System.Environment.GetEnvironmentVariable("ALLEGIANCE_BW_NOFIX") == "1";
        var savedTargets = noFix ? Array.Empty<RenderTargetBinding>() : gd.GetRenderTargets();
        gd.SetRenderTarget(target);
        gd.Viewport = new Viewport(0, 0, target.Width, target.Height);
        gd.Clear(Color.Transparent);
        RenderBuckets(gd, buckets, new Vector2(1f / target.Width, 1f / target.Height));
        // Retained A/B: ALLEGIANCE_BW_NOFIX=1 reproduces the ORIGINAL bug
        // (restore to null + DiscardContents backbuffer → opaque scene wiped).
        if (noFix) gd.SetRenderTarget(null);
        else if (savedTargets.Length > 0) gd.SetRenderTargets(savedTargets);
        else gd.SetRenderTarget(null);
        if (!noFix)
            RenderTargetGuard.AssertColourTargetRestored(gd, savedTargets, context);
        gd.Viewport = savedViewport;

        // Composite up, raw-additive (One, One).
        (_scaleBatch ??= new SpriteBatch(gd)).Begin(SpriteSortMode.Deferred, _addOneOne);
        _scaleBatch.Draw(target, new Rectangle(0, 0, savedViewport.Width, savedViewport.Height), Color.White);
        _scaleBatch.End();
        gd.Viewport = savedViewport;
    }

    /// <summary>Create (or re-create on resize) the half-resolution target the
    /// near volumes render into. Cleared by the caller each frame.</summary>
    private RenderTarget2D EnsureHalfRes(GraphicsDevice gd)
    {
        int w = Math.Max(1, gd.Viewport.Width / 2);
        int h = Math.Max(1, gd.Viewport.Height / 2);
        if (_halfRes is null || _halfResW != w || _halfResH != h)
        {
            _halfRes?.Dispose();
            _halfRes = new RenderTarget2D(gd, w, h, false, SurfaceFormat.Color, DepthFormat.None);
            _halfResW = w; _halfResH = h;
        }
        return _halfRes;
    }

    /// <summary>P834 — the quarter-resolution target for screen-filling volumes
    /// (1/16 the marched pixels). Created on the same resize rule as the
    /// half-res target.</summary>
    private RenderTarget2D EnsureQuarterRes(GraphicsDevice gd)
    {
        int w = Math.Max(1, gd.Viewport.Width / 4);
        int h = Math.Max(1, gd.Viewport.Height / 4);
        if (_quarterRes is null || _quarterResW != w || _quarterResH != h)
        {
            _quarterRes?.Dispose();
            _quarterRes = new RenderTarget2D(gd, w, h, false, SurfaceFormat.Color, DepthFormat.None);
            _quarterResW = w; _quarterResH = h;
        }
        return _quarterRes;
    }

    private Texture2D? _farDepth;

    private static Texture2D _MakeFarDepth(GraphicsDevice gd)
    {
        var t = new Texture2D(gd, 1, 1, false, SurfaceFormat.Color);
        t.SetData(new[] { Color.White });
        return t;
    }

    /// <summary>
    /// P582b — pre-warm the volumetric shaders and render targets AT LOAD so the
    /// first real explosion causes no hitch. MojoShader/DesktopGL compiles each
    /// technique's passes lazily on first <c>Apply()</c>; forcing that once here
    /// (drawing one degenerate 1px quad so the pipeline is actually bound) moves
    /// the compile off the first explosive frame. Also allocates the half- and
    /// quarter-res targets eagerly (ensure no mid-frame render-target allocation
    /// on first near+large blast). Idempotent; harmless when called before the
    /// first Draw.
    /// </summary>
    public void Preload(GraphicsDevice gd)
    {
        // Allocate the downscaled targets now (guarded by the viewport size) so
        // the first near/large explosion never triggers a mid-frame allocation.
        if (_halfRes is null) EnsureHalfRes(gd);
        if (_quarterRes is null) EnsureQuarterRes(gd);

        // Force-compile every LOD technique: bind a degenerate quad and Apply.
        try
        {
            var savedViewport = gd.Viewport;
            var vp = Matrix.Identity * Matrix.CreateOrthographicOffCenter(0, 1, 0, 1, -1, 1);
            var zero = new VolumeVertex[4];
            for (int li = 0; li < VolumeLod.LevelCount; li++)
            {
                string techName = VolumeLod.TechniqueName((VolumeLod.Level)li);
                var tech = _effect.Techniques[techName];
                if (tech is null) continue;
                _effect.CurrentTechnique = tech;
                for (int p = 0; p < tech.Passes.Count; p++)
                {
                    tech.Passes[p].Apply();
                    // Touch the pipeline with an empty triangle so DesktopGL
                    // actually binds/link-programs the compiled pass.
                    gd.DrawUserPrimitives(PrimitiveType.TriangleStrip, zero, 0, 2);
                }
            }
            gd.Viewport = savedViewport;
            Console.WriteLine("[volumetric] pre-warmed shader LODs + half/quarter-res targets");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[volumetric] pre-warm failed (non-fatal): {ex.Message}");
        }
    }

    public void Dispose()
    {
        // NOTE: the Effect is NOT disposed here — it is owned by the
        // ShaderAssetCache (shared with other renderers) and released there.
        _cullNone.Dispose();
        _additive.Dispose();
        _addOneOne.Dispose();
        _farDepth?.Dispose();
        _halfRes?.Dispose();
        _quarterRes?.Dispose();
        _scaleBatch?.Dispose();
    }
}
