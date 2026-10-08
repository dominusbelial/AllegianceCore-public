using System;
using System.Collections.Generic;
using Allegiance.ClientCore.State;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XnaVec = Microsoft.Xna.Framework.Vector3;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P573/P579 Technique A — real-time raymarched volumetric explosions (DesktopGL).
///
/// Each native sub-volume (see <see cref="VolumetricExplosionSampler"/>) is a
/// camera-facing billboard quad; <c>Volumetric.fx</c> intersects the view ray
/// with the bounding sphere (early-out + <c>clip</c> outside it — P579, no
/// square edges), then marches sampling animated ALU fBm density, shaded by a
/// temperature ramp tinted by the native type. P579 coverage LOD: the step
/// count goes by on-screen COVERAGE (big on-screen → FEWER, longer steps; small
/// → more steps is fine), and <see cref="VolumeBudget.AllocateLevels"/> caps the
/// total per-frame pixel-steps, degrading the LARGEST-coverage volumes first.
/// P579 fill-rate: NEAR+LARGE volumes (projected diameter ≥
/// <see cref="NearHalfResMinPx"/>) render into a HALF-RESOLUTION render target
/// (¼ the pixels → 4× the fill) and composite up additively — the task's
/// "half-resolution for near volumes" lever, essential for a screen-filling
/// blast. Optional P547 depth-soft intersection melts the volume onto
/// hulls/rocks/stations.
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

    /// <summary>P579 — a volume whose projected diameter (px) is at least this
    /// is rendered at half resolution (¼ the fill) to tame the near screen-
    /// filling blast's fill rate. Costs a little sharpness on the very biggest
    /// volumes; the far/small ones stay full-res.</summary>
    public const float NearHalfResMinPx = 240f;
    private readonly float _nearHalfResMinPx;

    private float _brightness;
    private RenderTarget2D? _halfRes;
    private int _halfResW, _halfResH;
    private SpriteBatch? _halfResBatch;

    public VolumetricRenderer(Effect effect)
    {
        _effect = effect;
        _nearHalfResMinPx = double.TryParse(System.Environment.GetEnvironmentVariable("ALLEGIANCE_VOL_HALFRESPX"),
            System.Globalization.CultureInfo.InvariantCulture, out var hrp) && hrp > 0 ? (float)hrp : NearHalfResMinPx;
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
            ColorSourceBlend = Blend.One,   // raw-add the half-res accumulation
            ColorDestinationBlend = Blend.One,
            AlphaSourceBlend = Blend.One,
            AlphaDestinationBlend = Blend.One,
        };
        _depthRead = DepthStencilState.DepthRead;
        _pWV = Param("WorldView"); _pWVP = Param("WorldViewProj");
        _pInvViewport = Param("InvViewportSize"); _pNear = Param("Near");
        _pInvRange = Param("InvRange"); _pEye = Param("EyePos");
        _pLight = Param("LightDir"); _pTime = Param("Time"); _pDepth = Param("DepthTexture");
        _pBrightness = Param("Brightness");
    }

    private EffectParameter Param(string n) => _effect.Parameters[n];

    /// <summary>Whether the shader has every parameter and at least the Med technique
    /// (if not, the tier falls back — the caller checks before enabling Volumetric).</summary>
    public bool IsUsable =>
        _pWV is not null && _pWVP is not null && _pInvViewport is not null && _pNear is not null
        && _pInvRange is not null && _pEye is not null && _pLight is not null && _pTime is not null
        && _pDepth is not null && _pBrightness is not null
        && _effect.Techniques[VolumeLod.TechniqueName(VolumeLod.Level.Med)] is not null;

    /// <summary>Optional forced LOD level. When set, every unit renders at this
    /// level (bypasses coverage LOD and the budget) — used by the cost harness to
    /// measure the LOD/budget delta. Null = normal coverage LOD + budget.</summary>
    public VolumeLod.Level? ForcedLevel { get; set; }

    /// <summary>Per-frame pixel-step budget override. Null = the shipped
    /// <see cref="VolumeBudget.MaxPixelSteps"/>. Set to <see cref="float.MaxValue"/>
    /// to disable the budget (measure the unfiltered worst case).</summary>
    public float? BudgetOverride { get; set; }

    public void Draw(
        IReadOnlyList<ExplosionEntity> explosions,
        short ownSectorId,
        XnaVec cameraEye,
        Matrix view,
        Matrix projection,
        float time,
        SoftParticleContext soft)
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
            float elapsed = Math.Max(0f, _SecsSince(e.ReceivedLocalMs));
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

        // ---- P579: coverage LOD + per-frame pixel/step budget ---------------
        var vols = new (float DiameterPx, float AreaPx)[kept.Count];
        var dists = new float[kept.Count];
        for (int i = 0; i < kept.Count; i++)
        {
            var u = kept[i].Unit;
            float dx = u.Center.X - eye.X, dy = u.Center.Y - eye.Y, dz = u.Center.Z - eye.Z;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            dists[i] = dist;
            float rPx = u.PeakRadius * pxPerUnit / Math.Max(1e-4f, dist);
            float diamPx = Math.Max(1f, rPx * 2f);
            vols[i] = (diamPx, diamPx * diamPx);
        }

        VolumeLod.Level[] levels;
        if (ForcedLevel is { } forced)
        {
            levels = new VolumeLod.Level[kept.Count];
            Array.Fill(levels, forced);
        }
        else
        {
            levels = VolumeBudget.AllocateLevels(vols, BudgetOverride ?? VolumeBudget.MaxPixelSteps);
        }

        // ---- Lay quads, splitting near+lARGE (half-res) from far+small ------
        var farBuckets = new List<VolumeVertex>[4];
        var nearBuckets = new List<VolumeVertex>[4];
        for (int i = 0; i < 4; i++) { farBuckets[i] = new List<VolumeVertex>(); nearBuckets[i] = new List<VolumeVertex>(); }

        for (int i = 0; i < kept.Count; i++)
        {
            var u = kept[i].Unit;
            var center = VolumetricGeometry.CameraRelative(u.Center, eye);   // camera-relative
            int li = (int)levels[i];
            bool near = vols[i].DiameterPx >= _nearHalfResMinPx;
            var target = near ? nearBuckets[li] : farBuckets[li];

            float age = Math.Max(0f, u.Elapsed - u.Born);
            float r = u.PeakRadius;
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
                    Life = u.Life,
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
        _pLight.SetValue(XnaVec.Normalize(new XnaVec(0.35f, 0.6f, -0.72f)));
        _pTime.SetValue(time);
        _pBrightness.SetValue(_brightness);
        _pDepth.SetValue(soft.Depth ?? (_farDepth ??= _MakeFarDepth(gd)));

        var fullInv = new Vector2(1f / gd.Viewport.Width, 1f / gd.Viewport.Height);

        // ---- Far+small volumes: full-res into the current target ------------
        RenderBuckets(gd, farBuckets, fullInv);

        // ---- Near+large volumes: half-res pass + additive upscale composite --
        bool anyNear = false;
        for (int li = 0; li < 4; li++) if (nearBuckets[li].Count > 0) { anyNear = true; break; }
        if (anyNear)
        {
            var savedViewport = gd.Viewport;
            var hr = EnsureHalfRes(gd);
            var halfInv = new Vector2(1f / hr.Width, 1f / hr.Height);
            gd.SetRenderTarget(hr);
            gd.Viewport = new Viewport(0, 0, hr.Width, hr.Height);
            gd.Clear(Color.Transparent);
            RenderBuckets(gd, nearBuckets, halfInv);
            gd.SetRenderTarget(null);
            gd.Viewport = savedViewport;

            // Composite the half-res accumulation up, raw-additive (One, One).
            (_halfResBatch ??= new SpriteBatch(gd)).Begin(SpriteSortMode.Deferred, _addOneOne);
            _halfResBatch.Draw(hr, new Rectangle(0, 0, savedViewport.Width, savedViewport.Height), Color.White);
            _halfResBatch.End();
            gd.Viewport = savedViewport;
        }

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

    /// <summary>Draw every bucket's quads with the given inv-viewport (full- or
    /// half-res), one technique per bucket. Blend/raster/depth are set per
    /// bucket exactly as before.</summary>
    private void RenderBuckets(GraphicsDevice gd, List<VolumeVertex>[] buckets, Vector2 invViewport)
    {
        _pInvViewport.SetValue(invViewport);
        for (int li = 0; li < 4; li++)
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

    private Texture2D? _farDepth;

    private static Texture2D _MakeFarDepth(GraphicsDevice gd)
    {
        var t = new Texture2D(gd, 1, 1, false, SurfaceFormat.Color);
        t.SetData(new[] { Color.White });
        return t;
    }

    private static float _SecsSince(long receivedLocalMs)
        => (float)(Environment.TickCount64 - receivedLocalMs) / 1000f;

    public void Dispose()
    {
        // NOTE: the Effect is NOT disposed here — it is owned by the
        // ShaderAssetCache (shared with other renderers) and released there.
        _cullNone.Dispose();
        _additive.Dispose();
        _addOneOne.Dispose();
        _farDepth?.Dispose();
        _halfRes?.Dispose();
        _halfResBatch?.Dispose();
    }
}
