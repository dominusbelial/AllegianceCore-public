using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P547 — the owner of the post-processing + custom-shader render targets and
/// effects for the ENHANCED VFX path:
///   • a <b>linear-depth</b> RenderTarget2D (RGBA8, universal on GLES) filled
///     by the depth pass (ships/asteroids/stations via LinearDepth.fx) and
///     sampled by the soft-particle smoke;
///   • the <b>scene</b> RenderTarget2D the 3D pass renders into when bloom is
///     on (so HUD/UI drawn after the composite never bloom);
///   • a <b>SoftParticleRenderer</b> and a <b>BloomPostProcessor</b>.
/// Everything degrades to null when a shader or target is unavailable, so the
/// callers fall back (soft → near-fade-only; bloom → off) — never render
/// nothing. Targets are recreated on resize via <see cref="SetSize"/>.
/// </summary>
public sealed class VfxPostProcessor : IDisposable
{
    private readonly GraphicsDevice _gd;
    private readonly ShaderAssetCache _shaders;

    /// <summary>The linear-depth effect (LinearDepth.fx) for the depth pass,
    /// or null if it failed to load.</summary>
    public Effect? LinearDepthEffect { get; }

    /// <summary>The soft-particle smoke renderer (SoftParticle.fx), or null if
    /// the shader failed to load.</summary>
    public SoftParticleRenderer? Soft { get; }

    /// <summary>The bloom post-processor (Bloom.fx), or null if unavailable.</summary>
    public BloomPostProcessor? Bloom { get; }

    /// <summary>The linear-depth target (RGBA8). Size tracks the window.</summary>
    public RenderTarget2D? DepthRT { get; private set; }

    /// <summary>The color target the 3D scene renders into when bloom is on
    /// (opaque scene + particles, excluding HUD).</summary>
    public RenderTarget2D? SceneRT { get; private set; }

    public VfxPostProcessor(GraphicsDevice gd, ShaderAssetCache shaders)
    {
        _gd = gd;
        _shaders = shaders;
        LinearDepthEffect = shaders.Load("Content/Shaders/LinearDepth.ogl.mgfxo");
        var softEff = shaders.Load("Content/Shaders/SoftParticle.ogl.mgfxo");
        Soft = softEff is null ? null : new SoftParticleRenderer(softEff);
        var bloomEff = shaders.Load("Content/Shaders/Bloom.ogl.mgfxo");
        Bloom = bloomEff is null ? null : new BloomPostProcessor(gd, bloomEff);
        SetSize(gd.PresentationParameters.BackBufferWidth, gd.PresentationParameters.BackBufferHeight);
    }

    /// <summary>Depth pass usable (effect + target present).</summary>
    public bool DepthReady => LinearDepthEffect is not null && DepthRT is not null;

    /// <summary>Soft particles usable (renderer usable + depth pass ready).</summary>
    public bool SoftReady => Soft is { IsUsable: true } && DepthReady;

    /// <summary>Bloom usable (processor usable + scene target present).</summary>
    public bool BloomReady => Bloom is { IsUsable: true } && SceneRT is not null;

    /// <summary>The per-frame soft-particle context for this frame's camera
    /// near/far. Only valid when <see cref="SoftReady"/>.</summary>
    public SoftParticleContext Context(float near, float far)
        => new(DepthRT!, near, far, LinearDepth.SoftnessNormalized);

    /// <summary>Clear-and-fill colour for the depth target: encoded "far"
    /// (normalized 0.999 — NOT 1.0, which aliases to 0 under the float-RGBA
    /// pack), so pixels with no opaque geometry read as infinitely far and
    /// smoke over sky stays fully visible.</summary>
    public static readonly Color DepthClearFar = LinearDepth.Encode(0.999f);

    /// <summary>(Re)create the depth + scene targets (and bloom targets) at the
    /// given size. Called at construction and on window resize / device reset.</summary>
    public void SetSize(int width, int height)
    {
        int w = Math.Max(1, width), h = Math.Max(1, height);
        DepthRT?.Dispose();
        SceneRT?.Dispose();
        // Depth target: RGBA8 (universal on GLES/Android) + a depth buffer so
        // the opaque depth pass hides backfaces and keeps the NEAREST surface.
        DepthRT = new RenderTarget2D(_gd, w, h, false, SurfaceFormat.Color, DepthFormat.Depth24);
        // P582b — SceneRT (bloom's colour target) must PRESERVE contents across
        // the P579 volumetric half-res excursion mid-frame, else the bloom'd
        // opaque scene is wiped by a near+large blast (the black-world bug).
        SceneRT = new RenderTarget2D(_gd, w, h, false, SurfaceFormat.Color, DepthFormat.Depth24,
            0, RenderTargetUsage.PreserveContents);
        Bloom?.SetSize(w, h);
    }

    public void Dispose()
    {
        Soft?.Dispose();
        Bloom?.Dispose();
        DepthRT?.Dispose();
        SceneRT?.Dispose();
    }
}
