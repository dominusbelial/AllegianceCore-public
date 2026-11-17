using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Allegiance.ClientCore.MonoGame.Rendering;

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

    private void DrawIntroMain(float w, float h, Color titleCol, Color dimCol, Color textCol)
    {
        DrawIntroTitle(w, "ALLEGIANCE", "Choose how to play  -  Single Player or Multiplayer", titleCol, dimCol);

        float cx = w / 2f;
        var lay = IntroLayout.Compute(w, h, IntroMainLabels.Length);
        float y = lay.Rows.Length > 0 ? lay.Rows[0].Y + 4f : h * 0.24f;
        float rowH = lay.RowHeight;
        float rowScale = lay.RowTextScale;
        for (int i = 0; i < IntroMainLabels.Length; i++)
        {
            string label = IntroMainLabels[i];
            var sz = _hudSmall.MeasureString(label);
            bool sel = i == _introIndex;
            if (sel)
            {
                _spriteBatch.Draw(_whitePixel, new Rectangle((int)lay.Rows[i].X, (int)lay.Rows[i].Y,
                        (int)lay.Rows[i].Width, (int)lay.Rows[i].Height),
                    new Color(0.08f, 0.35f, 0.33f, 0.55f));
            }
            _spriteBatch.DrawString(_hudSmall, label, new Vector2(cx - sz.X * rowScale / 2f, y + 2f),
                sel ? titleCol : textCol, 0, Vector2.Zero, rowScale, SpriteEffects.None, 0);
            y += rowH;
        }

        // P946 — pilot name entry (native remembered the last character name,
        // introscreen.cpp:1604-1608; the field itself is the port's addition).
        // The field shows a highlight box + caret while it owns typed text, so
        // the screen makes its focus visible. With an identity account the
        // label is the verified callsign and Tab does nothing, so the field is
        // not shown as focused and typed keys are not invited.
        bool tabAllowed = _identityScreen.Account is null;
        bool pilotFocus = _introEditingPilot && tabAllowed;
        string pilotText = _pilotBuf.ToString();
        var nameLabel = _identityScreen.PilotLabel(pilotText);
        var nsz = _hudSmall.MeasureString(nameLabel);
        var field = IntroLayout.PilotField(w, h);
        if (pilotFocus)
        {
            float fieldW = MathF.Min(w - 40f, MathF.Max(field.Width, nsz.X + 28f));
            _spriteBatch.Draw(_whitePixel,
                new Rectangle((int)(cx - fieldW / 2f), (int)field.Y, (int)fieldW, (int)field.Height),
                new Color(0.10f, 0.42f, 0.39f, 0.65f));
            _spriteBatch.Draw(_whitePixel,
                new Rectangle((int)(cx - fieldW / 2f), (int)field.Bottom, (int)fieldW, 2), titleCol);
        }
        float textX = cx - nsz.X / 2f;
        _spriteBatch.DrawString(_hudSmall, nameLabel, new Vector2(textX, lay.PilotY),
            pilotFocus ? titleCol : dimCol);
        if (pilotFocus)
        {
            // A real caret bar after the typed name: the "_" glyph sits on the
            // text baseline, exactly where the field's underline is painted, so
            // an explicit bar is what makes the insertion point visible.
            float caretX = textX + _hudSmall.MeasureString("Pilot: " + pilotText).X;
            _spriteBatch.Draw(_whitePixel, new Rectangle((int)caretX, (int)lay.PilotY + 1, 2, 17), titleCol);
        }

        var identityLine = _identityScreen.ApprovalLine;
        var identitySize = _hudSmall.MeasureString(identityLine);
        float identityScale = Math.Min(1f, (w - 40f) / Math.Max(1f, identitySize.X));
        _spriteBatch.DrawString(_hudSmall, identityLine, new Vector2((w - identitySize.X * identityScale) / 2f, lay.IdentityY),
            titleCol, 0, Vector2.Zero, identityScale, SpriteEffects.None, 0);

        // P946 — one line that says what the screen accepts: the arrows always
        // move the menu, Enter selects, Tab switches typed-text focus (only
        // when an account does not) and Esc leaves. The footer sits below the
        // pilot field, so it never enters the button column.
        string hint = IntroScreenModel.MainHint(tabAllowed);
        var fsz = _hudSmall.MeasureString(hint);
        float hintScale = Math.Min(1f, (w - 40f) / Math.Max(1f, fsz.X));
        _spriteBatch.DrawString(_hudSmall, hint, new Vector2((w - fsz.X * hintScale) / 2f, lay.FooterY),
            dimCol, 0, Vector2.Zero, hintScale, SpriteEffects.None, 0);
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
