using Allegiance.Core.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// HUD-21 — the GPU half of the classic target pane's 3-D model preview. Owns
/// ONE render target (created lazily on first use, disposed with the game) and
/// renders an eligible target's native mesh into it with the scope-1 matrices,
/// lit by the <see cref="TargetModelPreview.Lighting"/> headlight. The render
/// happens BEFORE the frame's colour target is bound (called from
/// <c>Game1.Rendering.cs</c> right after <c>PrepareSceneGeometry</c>), so the
/// save/switch/restore is lossless (nothing has been drawn to the colour
/// target yet). See <see cref="AlephGlimpseRenderer.EnsureRendered"/> for the
/// RT precedent and <see cref="RenderTargetGuard"/> (P582b).
/// </summary>
/// <remarks>
/// The render target is 256×256 ≥ the largest pane scale 96·2.1 = 201.6 px
/// (the 2.1 clamp of <see cref="TargetPaneLayout.Scale"/>), with
/// <see cref="DepthFormat.Depth24"/> and a <see cref="SurfaceFormat.Color"/>
/// alpha channel (the pane composites it with alpha blend over the HUD). Each
/// frame with an eligible target: set the RT, clear transparent with depth 1,
/// draw the model with the native renderer's own blend/depth state, then restore
/// the CALLER's colour target (passed in — HUD-21b removed the
/// <c>GetRenderTargets()</c> snapshot, which allocates a new array every call
/// and whose cached copy went stale). The caller keeps exactly one rebind.
/// </remarks>
public sealed class TargetModelPreviewRenderer : IDisposable
{
    /// <summary>Render-target size in texels (≥ 96·2.1 = 201.6 px).</summary>
    public const int Size = 256;

    private readonly GraphicsDevice _gd;
    private readonly NativeShipRenderer _ship;
    private readonly NativeStationRenderer _station;
    private readonly NativeAsteroidRenderer _asteroid;
    private RenderTarget2D? _rt;

    public TargetModelPreviewRenderer(
        GraphicsDevice gd,
        NativeShipRenderer ship,
        NativeStationRenderer station,
        NativeAsteroidRenderer asteroid)
    {
        _gd = gd;
        _ship = ship;
        _station = station;
        _asteroid = asteroid;
    }

    /// <summary>The last rendered preview, or null before the first render.</summary>
    public Texture2D? Target => _rt;

    private RenderTarget2D EnsureTarget()
    {
        if (_rt is null)
            _rt = new RenderTarget2D(_gd, Size, Size, false, SurfaceFormat.Color, DepthFormat.Depth24);
        return _rt;
    }

    private void Begin()
    {
        var gd = _gd;
        gd.SetRenderTarget(EnsureTarget());
        gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.Transparent, 1f, 0);
        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
    }

    /// <summary>Restore the frame's colour target — exactly the one the caller
    /// will draw the scene into (<c>null</c> = the backbuffer). No
    /// <c>GetRenderTargets()</c> here: the steady path allocates nothing.</summary>
    private void End(RenderTarget2D? restoreTo)
    {
        var gd = _gd;
        gd.SetRenderTarget(restoreTo);
        // P582b trip-wire: verify the excursion restored the colour target
        // (DEBUG-only, compiled out in Release). Nothing is lost — the caller
        // re-clears the colour target before its first draw.
        RenderTargetGuard.AssertColourTargetRestored(gd, restoreTo, "HUD-21 target model preview");
        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
    }

    public void RenderShip(NativeShipRenderer.NativeShipGeometry geo, Texture2D? texture,
        in Matrix world, in Matrix view, in Matrix proj, float nativeScale, SectorLighting lighting,
        RenderTarget2D? restoreTo)
    {
        Begin();
        _ship.Draw(geo, texture, world, view, proj, 1f, nativeScale, lighting);
        End(restoreTo);
    }

    public void RenderStation(NativeStationRenderer.NativeStationGeometry geo, Texture2D? texture,
        in Matrix world, in Matrix view, in Matrix proj, float nativeScale, SectorLighting lighting,
        RenderTarget2D? restoreTo)
    {
        Begin();
        _station.Draw(geo, texture, world, view, proj, nativeScale, lighting);
        End(restoreTo);
    }

    public void RenderAsteroid(NativeShipRenderer.NativeShipGeometry geo, Texture2D? texture,
        in Matrix world, in Matrix view, in Matrix proj, float nativeScale, SectorLighting lighting,
        RenderTarget2D? restoreTo)
    {
        Begin();
        _asteroid.Draw(geo, texture, world, view, proj, nativeScale, lighting);
        End(restoreTo);
    }

    public void Dispose()
    {
        _rt?.Dispose();
        _rt = null;
    }
}
