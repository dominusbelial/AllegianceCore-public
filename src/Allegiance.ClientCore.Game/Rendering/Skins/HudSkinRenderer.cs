using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering.Skins;

/// <summary>Adapter over the unchanged Classic/Modern drawing implementations.
/// Preserves their original pass order, including Classic's external passes.
/// Owns the renderer's private resources, but borrows all Game1 services/art.</summary>
public sealed class HudSkinRenderer : IHudSkinRenderer
{
    private readonly HudRenderer _drawing;
    private readonly ModernHudRenderer? _modern;
    private readonly HudSkinResources _resources;
    private readonly Action<RadarIconTextures?>? _classicRadarIcons;
    private bool _disposed;

    public HudSkinRenderer(HudRenderer drawing, HudSkinResources resources,
        Action<RadarIconTextures?>? classicRadarIcons = null)
    {
        _drawing = drawing;
        _modern = drawing as ModernHudRenderer;
        _resources = resources;
        _classicRadarIcons = classicRadarIcons;
    }

    public string Id => _modern is null ? HudSkinIds.Classic : HudSkinIds.Modern;
    // Classic has no element instrumentation yet; preserve that distinction.
    public IReadOnlySet<string>? Coverage => _modern?.DrawnElements;

    public void Draw(HudModel model, in HudDrawFrame frame)
    {
        if (_modern is null) _resources.Radar(model);
        _resources.ShipTags(model, _modern is not null);
        _drawing.TargetModelPreview = frame.TargetModelPreview;
        _drawing.SelectionBracket = frame.SelectionBracket;
        _drawing.Draw(model, frame.Viewport);
        if (_modern is null) _resources.PodDeath(model);
    }

    public void DrawCommand(HudModel model) => _resources.Command(model, _modern is null);

    public void DrawConsole(HudModel model)
    {
        if (_modern is not null) return; // Modern composes these in its HUD pass.
        _resources.Chat(model);
        _resources.Cargo(model);
        _resources.FlightParts(model);
    }

    public void SetRadarIcons(RadarIconTextures? icons)
    {
        if (_modern is not null) _modern.SetRadarIcons(icons);
        else _classicRadarIcons?.Invoke(icons);
    }

    public Rectangle DrawOverlayText(string text, Vector2 position, Color color)
        => _drawing.DrawOverlayText(text, position, color);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _modern?.Dispose();
    }
}
