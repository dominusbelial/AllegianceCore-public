using Allegiance.ClientCore.MonoGame.Rendering.Skins;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering.Screens;

/// <summary>
/// P1042 — the intro screen's main-menu drawing, moved verbatim behind the
/// <see cref="IScreenSkinRenderer"/> boundary. Classic and Modern use this painter
/// unchanged (the intro has never had a per-style renderer — P946 says the intro
/// ignores <see cref="HudStyle"/>), so the move is proven by the decoded-pixel
/// goldens, not by assertion.
///
/// <para>Every rectangle comes from <see cref="IntroMainFrame.Layout"/> (the same
/// <see cref="IntroLayout.Compute"/> result a hit test resolves) and every string is
/// the native label from <see cref="IntroScreenModel.MainLabels"/> — nothing is
/// formatted, reordered or re-measured for a skin.</para>
/// </summary>
public sealed class IntroScreenPainter
{
    private readonly SpriteBatch _batch;
    private readonly Texture2D _white;
    private readonly SpriteFont _small;
    private readonly SpriteFont _big;

    /// <summary>Native's selection fill (the teal plate the port has always used).</summary>
    private static readonly Color SelectionPlate = new(0.08f, 0.35f, 0.33f, 0.55f);
    /// <summary>Native's focused field fill and underline (the port's field chrome).</summary>
    private static readonly Color FieldFill = new(0.10f, 0.42f, 0.39f, 0.65f);

    public IntroScreenPainter(SpriteBatch batch, Texture2D white, SpriteFont small, SpriteFont big)
    {
        _batch = batch;
        _white = white;
        _small = small;
        _big = big;
    }

    /// <summary>Draw one intro frame inside the caller's open SpriteBatch session.</summary>
    public void Draw(in IntroMainFrame frame)
    {
        float w = frame.Width, h = frame.Height;
        var titleCol = frame.Inks.Title;
        var dimCol = frame.Inks.Dim;
        var textCol = frame.Inks.Body;

        var tsz = _big.MeasureString(frame.Title);
        _batch.DrawString(_big, frame.Title, new Vector2((w - tsz.X) / 2f, 28f), titleCol);
        if (frame.Subtitle.Length > 0)
        {
            var ssz = _small.MeasureString(frame.Subtitle);
            _batch.DrawString(_small, frame.Subtitle, new Vector2((w - ssz.X) / 2f, 82f), dimCol);
        }

        float cx = w / 2f;
        var lay = frame.Layout;
        float y = lay.Rows.Length > 0 ? lay.Rows[0].Y + 4f : h * 0.24f;
        float rowH = lay.RowHeight;
        float rowScale = lay.RowTextScale;
        for (int i = 0; i < frame.Labels.Count; i++)
        {
            string label = frame.Labels[i];
            var sz = _small.MeasureString(label);
            bool sel = i == frame.Selected;
            if (sel)
            {
                _batch.Draw(_white, new Rectangle((int)lay.Rows[i].X, (int)lay.Rows[i].Y,
                        (int)lay.Rows[i].Width, (int)lay.Rows[i].Height),
                    SelectionPlate);
            }
            _batch.DrawString(_small, label, new Vector2(cx - sz.X * rowScale / 2f, y + 2f),
                sel ? titleCol : textCol, 0, Vector2.Zero, rowScale, SpriteEffects.None, 0);
            y += rowH;
        }

        // P946 — pilot name entry (native remembered the last character name,
        // introscreen.cpp:1604-1608; the field itself is the port's addition).
        bool pilotFocus = frame.PilotFocused;
        var nameLabel = frame.PilotLabel;
        var nsz = _small.MeasureString(nameLabel);
        var field = frame.PilotField;
        if (pilotFocus)
        {
            float fieldW = MathF.Min(w - 40f, MathF.Max(field.Width, nsz.X + 28f));
            _batch.Draw(_white,
                new Rectangle((int)(cx - fieldW / 2f), (int)field.Y, (int)fieldW, (int)field.Height),
                FieldFill);
            _batch.Draw(_white,
                new Rectangle((int)(cx - fieldW / 2f), (int)field.Bottom, (int)fieldW, 2), titleCol);
        }
        float textX = cx - nsz.X / 2f;
        _batch.DrawString(_small, nameLabel, new Vector2(textX, lay.PilotY),
            pilotFocus ? titleCol : dimCol);
        if (pilotFocus)
        {
            // A real caret bar after the typed name: the "_" glyph sits on the
            // text baseline, exactly where the field's underline is painted, so
            // an explicit bar is what makes the insertion point visible.
            float caretX = textX + frame.PilotCaretOffset;
            _batch.Draw(_white, new Rectangle((int)caretX, (int)lay.PilotY + 1, 2, 17), titleCol);
        }

        var identityLine = frame.IdentityLine;
        var identitySize = _small.MeasureString(identityLine);
        float identityScale = frame.IdentityScale;
        _batch.DrawString(_small, identityLine, new Vector2((w - identitySize.X * identityScale) / 2f, lay.IdentityY),
            titleCol, 0, Vector2.Zero, identityScale, SpriteEffects.None, 0);

        // P946 — one line that says what the screen accepts: the arrows always
        // move the menu, Enter selects, Tab switches typed-text focus (only
        // when an account does not) and Esc leaves. The footer sits below the
        // pilot field, so it never enters the button column.
        var fsz = _small.MeasureString(frame.Hint);
        float hintScale = frame.HintScale;
        _batch.DrawString(_small, frame.Hint, new Vector2((w - fsz.X * hintScale) / 2f, lay.FooterY),
            dimCol, 0, Vector2.Zero, hintScale, SpriteEffects.None, 0);
    }
}
