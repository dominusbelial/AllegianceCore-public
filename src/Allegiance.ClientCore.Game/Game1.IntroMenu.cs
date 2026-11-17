using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;

namespace Allegiance.ClientCore.MonoGame;

// Shared production intro drawing and main-menu keys; lifecycle/platform services are selected by each target.
public sealed partial class Game1
{
    private void UpdateIntroMainKeys(bool up, bool down, bool enter, bool tab)
    {
        bool tabAllowed = _identityScreen.Account is null;
        var mainKeys = IntroScreenModel.MainMenuKeys(
            up && !_iUp, down && !_iDown, enter && !_iEnter, tab && !_iTab, tabAllowed);
        if (mainKeys.ToggleFieldFocus) _introEditingPilot = !_introEditingPilot;
        if (mainKeys.SelectionDelta != 0)
            _introIndex = IntroScreenModel.MoveSelection(_introIndex, IntroMainLabels.Length, mainKeys.SelectionDelta);
        if (mainKeys.Activate) ActivateIntroMain();
    }

    private void DrawIntroTitle(float w, string title, string sub, Color titleCol, Color dimCol)
    {
        var tsz = _hudBig.MeasureString(title);
        _spriteBatch.DrawString(_hudBig, title, new Vector2((w - tsz.X) / 2f, 28f), titleCol);
        if (sub.Length > 0)
        {
            var ssz = _hudSmall.MeasureString(sub);
            _spriteBatch.DrawString(_hudSmall, sub, new Vector2((w - ssz.X) / 2f, 82f), dimCol);
        }
    }

    /// <summary>P1042 — the screen-chrome registry (desktop lifecycle initializes it at
    /// <c>LoadContent</c>; the browser intro keeps a classic fallback because it runs the
    /// shared intro partial without the flight/HUD lifecycle). Published in the same
    /// update-boundary call as the HUD skin and the palette.</summary>
    private ScreenSkinRegistry? _screenSkins;
    private IScreenSkinRenderer? _screenFallback;

    /// <summary>P1042 — install the preloaded screen adapters (desktop lifecycle only;
    /// the browser keeps the classic fallback).</summary>
    private void InstallScreenSkins(ScreenSkinRegistry registry) => _screenSkins = registry;

    /// <summary>P1042 — the active screen chrome (a single reference read per frame).</summary>
    private IScreenSkinRenderer ActiveScreen => _screenSkins?.Active
        ?? (_screenFallback ??= new ClassicScreenSkinRenderer(HudSkinIds.Classic,
            new Rendering.Screens.IntroScreenPainter(_spriteBatch, _whitePixel, _hudSmall, _hudBig),
            null, null, null));

    private void DrawIntroMain(float w, float h, Color titleCol, Color dimCol, Color textCol)
    {
        // P1042 — the main menu is drawn by the active screen skin. Classic/Modern
        // forward the untouched pre-boundary drawing (IntroScreenPainter); Jet draws
        // vector chrome over the same rows. The frame carries the resolved
        // IntroLayout result, so drawing and hit testing share one geometry source.
        ActiveScreen.DrawIntro(BuildIntroMainFrame(w, h, titleCol, dimCol, textCol));
    }

    /// <summary>P1042 — the intro main menu as a read-only screen model. Every value is
    /// the model's own; nothing here can move the selection or issue a command.</summary>
    private IntroMainFrame BuildIntroMainFrame(float w, float h, Color titleCol, Color dimCol, Color textCol)
    {
        bool tabAllowed = _identityScreen.Account is null;
        bool pilotFocus = _introEditingPilot && tabAllowed;
        string pilotText = _pilotBuf.ToString();
        var lay = IntroLayout.Compute(w, h, IntroMainLabels.Length);
        var identityLine = _identityScreen.ApprovalLine;
        var identitySize = _hudSmall.MeasureString(identityLine);
        float identityScale = Math.Min(1f, (w - 40f) / Math.Max(1f, identitySize.X));
        string hint = IntroScreenModel.MainHint(tabAllowed);
        var fsz = _hudSmall.MeasureString(hint);
        float hintScale = Math.Min(1f, (w - 40f) / Math.Max(1f, fsz.X));
        return new IntroMainFrame(GraphicsDevice.Viewport,
            "ALLEGIANCE", "Choose how to play  -  Single Player or Multiplayer",
            IntroMainLabels, lay, _introIndex,
            _identityScreen.PilotLabel(pilotText), pilotText, pilotFocus,
            IntroLayout.PilotField(w, h), _hudSmall.MeasureString("Pilot: " + pilotText).X,
            identityLine, identityScale, hint, hintScale,
            new ScreenInks(titleCol, textCol, dimCol, Color.Orange));
    }

    private void DrawIntroChooser(float w, float h, Color titleCol, Color dimCol, Color textCol)
    {
        DrawIntroTitle(w, "Single Player", "Training or Skirmish", titleCol, dimCol);
        var rows = _singlePlayerChooser.Rows;
        var layout = IntroLayout.Compute(w, h, rows.Length);
        for (int i = 0; i < rows.Length; i++)
        {
            var label = (i == _singlePlayerChooser.Selected ? "> " : "  ") + rows[i];
            var size = _hudSmall.MeasureString(label);
            _spriteBatch.DrawString(_hudSmall, label, new Vector2((w - size.X) / 2, layout.Rows[i].Y + 4),
                i == _singlePlayerChooser.Selected ? titleCol : textCol);
        }
        var fsz = _hudSmall.MeasureString("UP/DOWN select   ENTER choose   ESC back");
        _spriteBatch.DrawString(_hudSmall, "UP/DOWN select   ENTER choose   ESC back",
            new Vector2((w - fsz.X) / 2f, layout.FooterY), dimCol);
    }


}
