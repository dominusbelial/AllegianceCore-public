using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private const string BrowserVolumePack = "shader-volumetric";
    private bool _browserVolumeRequested, _browserVolumeFailed, _browserVolumeFallbackLogged;
    private VolumetricRenderer? _browserVolumes;
    private long _browserVolumeSubmissions;
    private int _browserVolumeCount;
    private string? _browserVolumeFailure;

    public void ConfigureBrowserVolumetric(bool enabled) => _browserVolumeRequested = enabled;
    public object VolumetricMetrics => new {
        requested = _browserVolumeRequested, loaded = _browserVolumes is not null,
        usable = _browserVolumes?.IsUsable == true, technique = VolumetricTechnique.RaymarchA.ToString(),
        effectiveQuality = _browserVolumes is null ? "Native" : EffectsQuality.Volumetric.ToString(),
        submissions = _browserVolumeSubmissions, volumes = _browserVolumeCount,
        fullRes = _browserVolumes?.LastFullResVolumes ?? 0, halfRes = _browserVolumes?.LastHalfResVolumes ?? 0,
        quarterRes = _browserVolumes?.LastQuarterResVolumes ?? 0, pixelSteps = _browserVolumes?.LastPixelSteps ?? 0,
        budget = _browserVolumes?.LastBudget ?? 0, failure = _browserVolumeFailure,
    };

    private void EnsureBrowserVolumetric()
    {
        if (!_browserVolumeRequested || _browserVolumeFailed || _browserVolumes is not null) return;
        if (BattlePackExists?.Invoke(BrowserVolumePack) == false)
        { BrowserVolumeFallback("verified effect pack missing"); _browserVolumeFailed = true; return; }
        QueueCombatPack(BrowserVolumePack);
        if (BattlePackReady?.Invoke(BrowserVolumePack) != true) return;
        try
        {
            // ResidentContentManager can only open SHA-256 verified, atomically promoted bytes.
            var effect = Content.Load<Effect>("Shaders/Volumetric");
            var renderer = new VolumetricRenderer(effect);
            // Desktop Game1.Lifecycle.cs:927-960: requested tier + RaymarchA + IsUsable.
            if (!renderer.IsUsable) { renderer.Dispose(); throw new InvalidDataException("effect parameters/Med technique missing"); }
            _browserVolumes = renderer;
            renderer.Preload(GraphicsDevice);
            Console.WriteLine("[browser] Volumetric verified effect loaded (RaymarchA, explicit opt-in)");
        }
        catch (Exception error)
        {
            _browserVolumes?.Dispose(); _browserVolumes = null; _browserVolumeFailed = true;
            BrowserVolumeFallback(error.Message);
        }
    }

    private void BrowserVolumeFallback(string reason)
    {
        _browserVolumeFailure = reason;
        if (_browserVolumeFallbackLogged) return;
        _browserVolumeFallbackLogged = true;
        Console.WriteLine("[browser] Volumetric unavailable; using P1079 native explosions: " + reason);
    }

    private bool DrawBrowserVolumetric(short sector, long now, Vector3 eye, Matrix view, Matrix projection)
    {
        _browserVolumeCount = 0;
        if (_browserVolumes is not { } renderer) return false;
        // Feed the same adaptive budget on quiet frames too, as desktop Update does.
        renderer.ObserveFrame(_flightElapsedSeconds * 1000);
        if (_combatBlasts.Count == 0) return true;
        var targets = GraphicsDevice.GetRenderTargets();
        var viewport = GraphicsDevice.Viewport;
        try
        {
            renderer.Draw(_combatBlasts, sector, eye, view, projection, (float)_nowSeconds, SoftParticleContext.Off, now);
            _browserVolumeCount = renderer.LastVolumeCount;
            if (_browserVolumeCount > 0) _browserVolumeSubmissions += _browserVolumeCount;
            return true;
        }
        catch (Exception error)
        {
            // A lazy WebGL compile/link or target failure must leave a usable native frame.
            if (targets.Length == 0) GraphicsDevice.SetRenderTarget(null); else GraphicsDevice.SetRenderTargets(targets);
            GraphicsDevice.Viewport = viewport;
            GraphicsDevice.BlendState = BlendState.Opaque;
            GraphicsDevice.DepthStencilState = DepthStencilState.Default;
            GraphicsDevice.RasterizerState = RasterizerState.CullCounterClockwise;
            renderer.Dispose(); _browserVolumes = null; _browserVolumeFailed = true;
            BrowserVolumeFallback(error.Message);
            return false;
        }
    }
}
