using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P268 — draws the docked/hangar loadout screen: a dark fullscreen backdrop
/// with a translucent panel listing the current ship, the buyable shop list and
/// the pending loadout, laid out by <see cref="ShopLayout"/>.
///
/// <para>Conventions mirror <see cref="ScoreboardOverlayRenderer"/> /
/// <see cref="HelpOverlayRenderer"/>: it begins/ends its own
/// <see cref="SpriteBatch"/> session, draws a translucent panel with a coloured
/// border, and every drawn string is pure ASCII (produced by
/// <see cref="ShopLayout"/>). Drawn as the docked replacement view (over the
/// stationary ship's scene).</para>
///
/// <para><b>Scrolling.</b> The full real core offers ~447 hulls + parts, far more
/// than fits a 1080p panel, so the contiguous shop-item run is rendered as a
/// <em>viewport window</em> centred on the cursor row while the fixed rows
/// around it (title, current ship, shop header, pending loadout) stay pinned.
/// This keeps the pending-loadout feedback always visible while navigating a
/// long list.</para>
/// </summary>
public sealed class DockedScreenRenderer
{
    private readonly SpriteBatch _sb;
    private readonly Texture2D _white;
    private readonly SpriteFont _small;
    private readonly SpriteFont _big;

    private static readonly Color Backdrop = new(0.010f, 0.014f, 0.019f, 0.985f);
    private static readonly Color PanelBg = new(0.030f, 0.042f, 0.058f, 0.96f);
    private static readonly Color PanelBorder = new(0.90f, 0.62f, 0.22f);
    private static readonly Color TitleColor = new(1.00f, 0.85f, 0.45f);
    private static readonly Color SectionColor = new(0.55f, 0.85f, 1.00f);
    private static readonly Color InfoColor = new(0.95f, 0.97f, 0.98f);
    private static readonly Color ItemColor = new(0.88f, 0.93f, 0.92f);
    private static readonly Color HighlightColor = new(0.62f, 1.00f, 0.80f);
    private static readonly Color MutedColor = new(0.45f, 0.48f, 0.52f);
    private static readonly Color HintColor = new(0.62f, 0.66f, 0.70f);

    private const float Padding = 22f;
    private const float LineGap = 2f;
    private const int MaxVisibleItems = 22;   // viewport window for the shop list

    public DockedScreenRenderer(SpriteBatch spriteBatch, Texture2D whitePixel,
        SpriteFont small, SpriteFont big)
    {
        _sb = spriteBatch;
        _white = whitePixel;
        _small = small;
        _big = big;
    }

    /// <summary>Draw the docked screen for the given <see cref="ShopLayout"/> rows.</summary>
    public void Draw(Viewport viewport, IReadOnlyList<ShopLayout.Row> rows)
    {
        float w = viewport.Width;
        float h = viewport.Height;

        // Split the contiguous shop-item run (Kind.Item) out so we can viewport it.
        int itemStart = -1, itemEnd = -1;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Kind == ShopLayout.Kind.Item)
            {
                if (itemStart < 0) itemStart = i;
                itemEnd = i;
            }
        }
        int cursorItem = -1;
        if (itemStart >= 0)
        {
            for (int i = itemStart; i <= itemEnd; i++)
                if (rows[i].Highlight) { cursorItem = i; break; }
        }
        int itemCount = itemStart < 0 ? 0 : itemEnd - itemStart + 1;

        float lineH = _small.LineSpacing + LineGap;

        // Height cost of every line at a given item-window size `vis` (items are
        // the only variable part). Used to pick the largest item window that lets
        // the WHOLE panel (title..current ship..shop header..items..pending) fit.
        float HeightFor(int vis)
        {
            float y = 0f;
            for (int i = 0; i < rows.Count; i++)
            {
                if (i >= itemStart && i <= itemEnd) { if (vis-- > 0) y += lineH; else break; }
                else y += rows[i].Kind == ShopLayout.Kind.Blank ? LineGap : lineH;
            }
            return 2f * Padding + y - LineGap;
        }

        int vis = Math.Min(MaxVisibleItems, itemCount);
        float maxHeight = h * 0.94f;
        while (vis > 0 && HeightFor(vis) > maxHeight) vis--;
        vis = Math.Max(1, vis);

        // Centre the window on the cursor row (or the first item if no cursor).
        int from = itemStart < 0 ? 0 : itemStart;
        int to = itemEnd < 0 ? -1 : itemEnd;
        if (itemStart >= 0)
        {
            int baseIdx = cursorItem >= 0 ? cursorItem : itemStart;
            from = Math.Clamp(baseIdx - vis / 2, itemStart, Math.Max(itemStart, itemEnd - vis + 1));
            to = from + vis - 1;
        }

        // Panel width fits the widest row; height fits the visible lines exactly.
        float maxLine = 0f;
        foreach (var r in rows)
            if (r.Kind != ShopLayout.Kind.Blank)
                maxLine = MathF.Max(maxLine, _small.MeasureString(r.Text).X);
        float panelW = MathF.Min(maxLine + 2f * Padding, w * 0.96f);
        float panelH = HeightFor(vis);

        float panelX = 26f;
        float panelY = MathF.Max(12f, (h - panelH) / 2f);

        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            FillRect(0, 0, w, h, Backdrop);
            FillRect(panelX, panelY, panelW, panelH, PanelBg);
            OutlineRect(panelX, panelY, panelW, panelH, PanelBorder);
            FillRect(panelX, panelY, panelW, 3f, PanelBorder);   // top accent edge

            float y = panelY + Padding;
            for (int i = 0; i < rows.Count; i++)
            {
                // Skip rows outside the item viewport.
                if (i >= itemStart && i <= itemEnd && (i < from || i > to)) continue;
                var r = rows[i];
                if (r.Kind == ShopLayout.Kind.Blank) { y += LineGap; continue; }
                Color c = ColorFor(r);
                Draw(r.Text, new Vector2(panelX + Padding, y), c);
                y += lineH;
            }
        }
        finally
        {
            _sb.End();
        }
    }

    private static Color ColorFor(ShopLayout.Row r) => r.Kind switch
    {
        ShopLayout.Kind.Title => TitleColor,
        ShopLayout.Kind.Hint => HintColor,
        ShopLayout.Kind.Section => SectionColor,
        ShopLayout.Kind.Item => r.Highlight ? HighlightColor : ItemColor,
        ShopLayout.Kind.Muted => MutedColor,
        _ => InfoColor,
    };

    private void Draw(string text, Vector2 pos, Color color) =>
        _sb.DrawString(_small, text, pos, color, 0f, Vector2.Zero, 1f,
            SpriteEffects.None, 0f);

    private void FillRect(float x, float y, float w, float h, Color color) =>
        _sb.Draw(_white, new Rectangle((int)MathF.Round(x),
            (int)MathF.Round(y), (int)MathF.Round(w), (int)MathF.Round(h)), color);

    private void OutlineRect(float x, float y, float w, float h, Color color)
    {
        FillRect(x, y, w, 1f, color);
        FillRect(x, y + h - 1f, w, 1f, color);
        FillRect(x, y, 1f, h, color);
        FillRect(x + w - 1f, y, 1f, h, color);
    }
}
