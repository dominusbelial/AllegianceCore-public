using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering.Skins;

/// <summary>Prebound flight composition. The caller invokes the three phases at
/// the existing HUD, command and post-starmap positions; no sprite pass spans a
/// phase. Screen chrome and widget/token extraction belong to later slices.</summary>
public interface IHudSkinRenderer : IDisposable
{
    string Id { get; }
    IReadOnlySet<string>? Coverage { get; }
    void Draw(HudModel model, in HudDrawFrame frame);
    void DrawCommand(HudModel model);
    void DrawConsole(HudModel model);
    void SetRadarIcons(RadarIconTextures? icons);
    Rectangle DrawOverlayText(string text, Vector2 position, Color color);
}

/// <summary>Value-only frame inputs, borrowed for this draw. The preview is a
/// shared render target, never owned or disposed by a skin.</summary>
public readonly record struct HudDrawFrame(Viewport Viewport, Texture2D? TargetModelPreview, bool SelectionBracket);
