using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private bool _browserKeyHelp;
    private HelpManualModel? _browserHelpManual;
    private HelpOverlayRenderer? _browserHelpRenderer;
    private KeyboardState _browserUiPrevious;
    public bool KeyHelpOpen => _browserKeyHelp;
    public bool ReferenceKey(string[] codes)
    {
        var keys = BrowserKeyboard.State(codes);
        return ActionDown(keys, "Help") || ActionDown(keys, "OpenKeyBindings") || ActionDown(keys, "ScreenShot");
    }
    public void ToggleBrowserKeyHelp()
    {
        _browserKeyHelp = !_browserKeyHelp;
        if (_browserKeyHelp)
        {
            _browserHelpManual ??= new();
            _browserHelpManual.GoTo("hlpControls");
        }
    }
    public bool CloseBrowserKeyHelp()
    {
        if (!_browserKeyHelp) return false;
        _browserKeyHelp = false; return true;
    }
    private bool UpdateBrowserKeyHelp(KeyboardState keys)
    {
        var map = KeyBindingStore.Instance.Map;
        bool Edge(string action) => map.IsDownMostSpecific(keys, action) && !map.IsDownMostSpecific(_browserUiPrevious, action);
        bool Press(Keys key) => keys.IsKeyDown(key) && !_browserUiPrevious.IsKeyDown(key);
        bool wasOpen = _browserKeyHelp;
        if (Edge("Help") || Edge("OpenKeyBindings")) ToggleBrowserKeyHelp();
        else if (_browserKeyHelp)
        {
            if (Press(Keys.Escape)) _browserKeyHelp = false;
            else if (Press(Keys.Right) || Press(Keys.PageDown)) _browserHelpManual!.MoveTopicSelection(1);
            else if (Press(Keys.Left) || Press(Keys.PageUp)) _browserHelpManual!.MoveTopicSelection(-1);
            else if (Press(Keys.Back)) _browserHelpManual!.Back();
            else if (Press(Keys.Up)) _browserHelpManual!.ScrollBy(-1, 12);
            else if (Press(Keys.Down)) _browserHelpManual!.ScrollBy(1, 12);
        }
        _browserUiPrevious = keys;
        return wasOpen || _browserKeyHelp;
    }
    private void DrawBrowserKeyboardHud()
    {
        if (_browserKeyHelp)
        {
            _browserHelpRenderer ??= new(_spriteBatch, _whitePixel, _hudSmall, _hudBig);
            _browserHelpRenderer.Draw(GraphicsDevice.Viewport, _browserHelpManual);
        }
        else if (Flying && _browserSelectedTarget is { } target)
        {
            _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
            var text = target.Name + (_browserAutoTarget is not null ? "  AUTOPILOT" : "");
            _spriteBatch.DrawString(_hudSmall, text, new Vector2(20, 20), Color.LightGoldenrodYellow);
            _spriteBatch.End();
        }
    }
}
