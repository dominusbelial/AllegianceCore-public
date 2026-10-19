using System;
using System.Collections.Generic;
using Allegiance.ClientCore.State;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XnaVec = Microsoft.Xna.Framework.Vector3;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P573 Technique B — displaced-sphere fireballs (SM3.0 reference: Steven
/// Craeynest's "Easy volumetric explosion in Unity3D"). Each native sub-volume
/// (see <see cref="VolumetricExplosionSampler"/>) is drawn as a unit icosphere
/// whose vertices are displaced along their normals by ALU value-noise in the
/// VERTEX shader (<c>DisplacedSphere.fx</c>), coloured from a 1D ramp by
/// displacement and faded by clip-threshold erosion — OPAQUE, so it writes
/// depth and needs no alpha sorting. A mesh LOD (162/642/2562 verts) trades
/// silhouette fidelity against on-screen size; the same volume cap as A applies.
///
/// State pinning (P568 lesson): Cull None (sphere seen from both sides),
/// opaque blend, depth read+WRITE are set and restored.
/// </summary>
public sealed class DisplacedSphereRenderer : IDisposable
{
    private readonly Effect _effect;
    private readonly RasterizerState _cullNone;
    // Icosphere LOD meshes: subdiv 2/3/4 => ~162/642/2562 verts.
    private readonly VertexPositionNormalTexture[] _l0, _l1, _l2;
    private readonly short[] _idx0, _idx1, _idx2;

    private readonly EffectParameter _pWVP, _pCenter, _pRadius, _pAge, _pLife, _pSeed;
    private readonly EffectParameter _pTime, _pTint, _pDisp, _pFreq, _pClip;

    public DisplacedSphereRenderer(Effect effect)
    {
        _effect = effect;
        _cullNone = new RasterizerState { CullMode = CullMode.None };
        (_l0, _idx0) = Build(IcosphereGenerator.Generate(2));
        (_l1, _idx1) = Build(IcosphereGenerator.Generate(3));
        (_l2, _idx2) = Build(IcosphereGenerator.Generate(4));
        _pWVP = Param("WorldViewProj"); _pCenter = Param("Center"); _pRadius = Param("Radius");
        _pAge = Param("Age"); _pLife = Param("Life"); _pSeed = Param("Seed");
        _pTime = Param("Time"); _pTint = Param("Tint"); _pDisp = Param("DispStrength");
        _pFreq = Param("Frequency"); _pClip = Param("ClipThreshold");
    }

    private static (VertexPositionNormalTexture[], short[]) Build(IcosphereMesh mesh)
    {
        var verts = new VertexPositionNormalTexture[mesh.VertexCount];
        for (int i = 0; i < mesh.VertexCount; i++)
            verts[i] = new VertexPositionNormalTexture(mesh.Vertices[i], mesh.Vertices[i], Vector2.Zero);
        var idx = new short[mesh.Indices.Length];
        for (int i = 0; i < idx.Length; i++) idx[i] = (short)mesh.Indices[i];
        return (verts, idx);
    }

    private EffectParameter Param(string n) => _effect.Parameters[n];

    public bool IsUsable =>
        _pWVP is not null && _pCenter is not null && _pRadius is not null && _pAge is not null
        && _pLife is not null && _pSeed is not null && _pTime is not null && _pTint is not null
        && _pDisp is not null && _pFreq is not null && _pClip is not null
        && _effect.Techniques["DisplacedSphere"] is not null;

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

        // Gather sub-volumes (native 1:1) + enforce the shared volume cap.
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

        var eye = new SimVec(cameraEye.X, cameraEye.Y, cameraEye.Z);
        var kept = VolumeBudget.TrimNearest(all, eye);
        float pxPerUnit = gd.Viewport.Height * projection.M22;

        _pWVP.SetValue(vp);
        _pTime.SetValue(time);

        // Technique is a single pass — set blend/depth/raster ONCE for the loop.
        gd.BlendState = BlendState.Opaque;      // opaque: no alpha sorting
        gd.RasterizerState = _cullNone;         // P568 — Cull None
        gd.DepthStencilState = DepthStencilState.Default;  // depth read + WRITE
        _effect.Techniques["DisplacedSphere"].Passes[0].Apply();

        for (int i = 0; i < kept.Count; i++)
        {
            var u = kept[i].Unit;
            float dx = u.Center.X - eye.X, dy = u.Center.Y - eye.Y, dz = u.Center.Z - eye.Z;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            float diamPx = Math.Max(1f, u.PeakRadius * pxPerUnit / Math.Max(1e-4f, dist));
            int lod = diamPx < 48 ? 0 : diamPx < 140 ? 1 : 2;
            var (verts, idx) = lod switch { 0 => (_l0, _idx0), 1 => (_l1, _idx1), _ => (_l2, _idx2) };

            float age = Math.Max(0f, u.Elapsed - u.Born);
            float fullLife = Math.Max(0.042f, u.Life);
            float tn = age / fullLife;
            // P582b — growth/cool run on the FILM clock (u.FilmLife) so the
            // fireball peaks at native film cadence; the EROSION keeps using the
            // FULL life (film + smoke tail) so the surface holds through the film
            // and erodes over the tail exactly as P582 specified. Two separate
            // clocks, two responsibilities.
            float filmClock = Math.Max(0.042f, u.FilmLife > 0f ? u.FilmLife : u.Life);

            var crc = VolumetricGeometry.CameraRelative(u.Center, eye);   // camera-relative (view eye at origin)
            _pCenter.SetValue(new XnaVec(crc.X, crc.Y, crc.Z));
            _pRadius.SetValue(u.PeakRadius);
            _pAge.SetValue(age);
            _pLife.SetValue(filmClock);
            _pSeed.SetValue(u.Seed);
            _pTint.SetValue(new XnaVec(u.Tint.X, u.Tint.Y, u.Tint.Z));
            _pDisp.SetValue(0.22f);
            _pFreq.SetValue(1.7f / Math.Max(0.01f, u.PeakRadius));
            // Erosion (P582): HOLD the full fireball through the native film
            // phase (explosion.cpp:187-189 keeps every film frame visible, so
            // the fireball must not erode mid-film), then smoothstep-erode over
            // the smoke tail. Old code eroded LINEARLY from birth
            // (tn*1.25-0.15), clipping most of the surface by ~40-50% of life
            // — the "displaced explosion disappears too fast" bug. clip stays
            // below the shader's [0,1] displacement through the film (nothing
            // clipped → full coverage), then rises past 1 by end of life.
            float filmFrac = fullLife > 0f ? Math.Clamp(u.FilmLife / fullLife, 0f, 1f) : 0f;
            _pClip.SetValue(ErodeClip(tn, filmFrac));

            gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, verts, 0, verts.Length,
                idx, 0, idx.Length / 3);
        }

        // Restore pinned state (P568).
        gd.BlendState = BlendState.Opaque;
        gd.RasterizerState = RasterizerState.CullCounterClockwise;
        gd.DepthStencilState = DepthStencilState.Default;
    }

    /// <summary>
    /// P582 — the erosion clip threshold for a displaced sub-volume at fraction
    /// <paramref name="tn"/> (age/life) of its total life, holding the FULL
    /// fireball through its native film phase (<paramref name="filmFrac"/> =
    /// filmLife/life) and then eroding over the smoke tail with a smoothstep
    /// (eased) curve. Pure + unit-testable. Values are fed to the shader's
    /// <c>ClipThreshold</c>: below the [0,1] displacement range nothing is
    /// clipped (full surface), past 1 everything is clipped (faded out).
    /// </summary>
    public static float ErodeClip(float tn, float filmFrac)
    {
        filmFrac = Math.Clamp(filmFrac, 0f, 1f);
        // Through the film: tn <= filmFrac -> erodeT 0 -> clip stays at the
        // birth floor (-0.15, nothing clipped). After the film: ramp over the
        // remaining (1-filmFrac) share with a smoothstep ease.
        float erodeT = tn <= filmFrac ? 0f : (tn - filmFrac) / Math.Max(1e-6f, 1f - filmFrac);
        float ease = erodeT * erodeT * (3f - 2f * erodeT);   // smoothstep
        return -0.15f + ease * 1.25f;
    }

    /// <summary>P582 — rough surface-COVERAGE estimate for a clip threshold,
    /// modelling the shader's [0,1] displacement as approximately uniform:
    /// the fraction of the surface surviving <c>clip(i.Disp - ClipThreshold)</c>
    /// is <c>clamp(1 - clip, 0, 1)</c>. Used by the failing-first erosion test
    /// to assert the fireball holds ≥ 90% coverage through the film phase.</summary>
    public static float CoverageAt(float clip) => Math.Clamp(1f - clip, 0f, 1f);

    /// <summary>
    /// P582b — pre-warm the displaced-sphere shader at LOAD so the first real
    /// explosion causes no hitch (DesktopGL/MojoShader compiles the technique
    /// lazily on first Apply). Forces the compile with a degenerate draw.
    /// Idempotent; non-fatal on failure.
    /// </summary>
    public void Preload(GraphicsDevice gd)
    {
        try
        {
            var tech = _effect.Techniques["DisplacedSphere"];
            if (tech is null) return;
            _effect.CurrentTechnique = tech;
            var pass = tech.Passes[0];
            pass.Apply();
            gd.DrawUserPrimitives(PrimitiveType.TriangleStrip, _l0, 0, _l0.Length - 2);
            Console.WriteLine("[displaced] pre-warmed shader");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[displaced] pre-warm failed (non-fatal): {ex.Message}");
        }
    }

    public void Dispose()
    {
        // The Effect is owned by the ShaderAssetCache — not disposed here.
        _cullNone.Dispose();
    }
}
