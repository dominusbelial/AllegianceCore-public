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
    /// (opaque scene + particles, excluding HUD). P984 — a HALF-FLOAT target
    /// (RGBA16F) when <see cref="HdrActive"/>, else the native RGBA8 one.</summary>
    public RenderTarget2D? SceneRT { get; private set; }

    /// <summary>P984 — the bloom composite target of the HDR path when FSR is not
    /// upscaling (the scene + glow are combined here at full scene resolution,
    /// then the single output transform reads it). Null unless
    /// <see cref="HdrActive"/>.</summary>
    public RenderTarget2D? HdrCompositeRT { get; private set; }

    /// <summary>P984 — the <c>visuals.space.hdrPipeline</c> flag asked for the
    /// float scene target (before the format was proven on this device).</summary>
    public bool HdrRequested { get; private set; }

    /// <summary>P984 — the float scene target really exists, so the HDR pass list
    /// (linear PBR variant + one output transform) is in force. False when the
    /// flag is off OR the backend refused <see cref="SurfaceFormat.HalfVector4"/>;
    /// the pipeline then runs the native 8-bit path unchanged.</summary>
    public bool HdrActive { get; private set; }

    /// <summary>P984 — the single output transform every HDR frame ends with
    /// (neutral or ACES). Null when <c>Tonemap.ogl.mgfxo</c> failed to load, which
    /// also disables <see cref="HdrActive"/>.</summary>
    public HdrOutputTransform? Output { get; }

    /// <summary>Why the HDR path was refused (one log line), or null.</summary>
    public string? HdrFallbackReason { get; private set; }

    private int _width = 1, _height = 1;
    private bool _hdrShadersLogged;

    public VfxPostProcessor(GraphicsDevice gd, ShaderAssetCache shaders)
    {
        _gd = gd;
        _shaders = shaders;
        LinearDepthEffect = shaders.Load("Content/Shaders/LinearDepth.ogl.mgfxo");
        var softEff = shaders.Load("Content/Shaders/SoftParticle.ogl.mgfxo");
        Soft = softEff is null ? null : new SoftParticleRenderer(softEff);
        var bloomEff = shaders.Load("Content/Shaders/Bloom.ogl.mgfxo");
        Bloom = bloomEff is null ? null : new BloomPostProcessor(gd, bloomEff);
        var tonemapEff = shaders.Load("Content/Shaders/Tonemap.ogl.mgfxo");
        Output = tonemapEff is null ? null : new HdrOutputTransform(gd, tonemapEff);
        SetSize(gd.PresentationParameters.BackBufferWidth, gd.PresentationParameters.BackBufferHeight);
    }

    /// <summary>P984 — request/withdraw the float HDR scene target. Recreates the
    /// targets when the setting changes (the flag is resolved once at startup, so
    /// this normally runs before the first frame).</summary>
    public void SetHdr(bool requested)
    {
        if (requested == HdrRequested) return;
        HdrRequested = requested;
        SetSize(_width, _height);
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
        => new(DepthRT!, near, far, LinearDepth.SoftnessFor(near, far));

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
        _width = w; _height = h;
        DepthRT?.Dispose();
        SceneRT?.Dispose();
        HdrCompositeRT?.Dispose();
        HdrCompositeRT = null;
        // Depth target: RGBA8 (universal on GLES/Android) + a depth buffer so
        // the opaque depth pass hides backfaces and keeps the NEAREST surface.
        // P984 — the linear-depth target is a DEPTH carrier, not colour: it stays
        // RGBA8 on both paths (LinearDepth.fx packs 0..1 into 8 bits by design).
        DepthRT = new RenderTarget2D(_gd, w, h, false, SurfaceFormat.Color, DepthFormat.Depth24);
        // P582b — SceneRT (bloom's colour target) must PRESERVE contents across
        // the P579 volumetric half-res excursion mid-frame, else the bloom'd
        // opaque scene is wiped by a near+large blast (the black-world bug).
        HdrActive = false;
        HdrFallbackReason = null;
        bool transformUsable = Output is { IsUsable: true };
        // The float target is used ONLY when the transform can grade it: a float
        // scene with no output transform would display washed-out/over-bright, so
        // that case falls back to the native 8-bit path instead.
        bool hdrFormat = HdrRequested && transformUsable;
        SurfaceFormat format = HdrOutput.SceneFormat(hdrFormat);
        if (HdrRequested && !transformUsable)
        {
            // A float scene with no output transform would display an un-graded,
            // over-bright frame: refuse the HDR path instead (one log line).
            if (!_hdrShadersLogged)
                Console.WriteLine("[client] visuals.space.hdrPipeline: output transform (Tonemap.ogl.mgfxo) "
                    + "unavailable - staying on the native 8-bit scene path");
            _hdrShadersLogged = true;
        }
        try
        {
            SceneRT = new RenderTarget2D(_gd, w, h, false, format, DepthFormat.Depth24,
                0, RenderTargetUsage.PreserveContents);
            if (hdrFormat)
            {
                HdrCompositeRT = new RenderTarget2D(_gd, w, h, false, format, DepthFormat.None,
                    0, RenderTargetUsage.PreserveContents);
                HdrActive = true;
            }
        }
        catch (Exception exception) when (hdrFormat)
        {
            // P984 — the backend refused RGBA16F: fall back to the native 8-bit
            // path and say so ONCE (never render nothing).
            SceneRT?.Dispose();
            HdrCompositeRT?.Dispose();
            HdrCompositeRT = null;
            SceneRT = new RenderTarget2D(_gd, w, h, false, SurfaceFormat.Color, DepthFormat.Depth24,
                0, RenderTargetUsage.PreserveContents);
            HdrFallbackReason = $"{exception.GetType().Name}: {exception.Message}";
            Console.WriteLine($"[client] visuals.space.hdrPipeline: {format} render target unavailable "
                + $"({HdrFallbackReason}) - falling back to the native 8-bit scene path");
        }
        Bloom?.SetSize(w, h, HdrActive);
    }

    public void Dispose()
    {
        Soft?.Dispose();
        Bloom?.Dispose();
        Output?.Dispose();
        DepthRT?.Dispose();
        SceneRT?.Dispose();
        HdrCompositeRT?.Dispose();
    }
}
