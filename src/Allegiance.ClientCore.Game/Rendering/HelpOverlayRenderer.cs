using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P257 — draws the H keybinding-reference overlay: a large translucent panel
/// over the current scene listing every current client binding, organised into
/// the logical groups from <see cref="KeyBindingTable"/> (movement, weapons,
/// targeting/autopilot, camera/view, chat, lobby) laid out by
/// <see cref="HelpOverlayLayout"/> into balanced columns.
///
/// Conventions mirror <see cref="StarmapRenderer"/>: it begins/ends its own
/// <see cref="SpriteBatch"/> session (so it can render over the 3D scene or into
/// an offscreen capture), draws a translucent panel + teal border, and keeps
/// every drawn string pure ASCII (the HUD spritefonts bake ASCII 32..126 only).
/// It is drawn last in the frame, so it is fully legible on top of the HUD and
/// starmap; toggling it on/off is independent of every other overlay flag, so
/// the prior view (starmap, HUD, chat log) resumes unchanged when it closes.
///
/// P1064 measures each Controls page against both axes, scales its row pitch
/// with its text, and reserves two rows below the tallest column. The result
/// is cached until the live bindings, selected page or viewport change.
/// </summary>
public sealed class HelpOverlayRenderer
{
    private readonly SpriteBatch _sb;
    private readonly Texture2D _white;
    private readonly SpriteFont _small;
    private readonly SpriteFont _big;
    private IReadOnlyList<KeyBindingTable.Group>? _cachedGroups;
    private HelpOverlayLayout.ControlTable? _cachedTable;
    private float _cachedWidth, _cachedHeight;

    private static readonly Color PanelBg = new(0.017f, 0.036f, 0.046f, 0.93f);
    private static readonly Color PanelBorder = new(0.16f, 0.42f, 0.40f);
    private static readonly Color HeaderColor = new(0.38f, 0.90f, 0.84f);
    private static readonly Color GroupTitleColor = new(0.44f, 0.92f, 0.86f);
    private static readonly Color KeyColor = new(1.00f, 0.90f, 0.55f);
    private static readonly Color DescColor = new(0.88f, 0.95f, 0.94f);
    private static readonly Color NoteColor = new(0.58f, 0.74f, 0.72f);

    public HelpOverlayRenderer(SpriteBatch spriteBatch, Texture2D whitePixel,
        SpriteFont small, SpriteFont big)
    {
        _sb = spriteBatch;
        _white = whitePixel;
        _small = small;
        _big = big;
    }

    /// <summary>Draw the help panel for the current viewport.</summary>
    public void Draw(Viewport viewport)
    {
        Draw(viewport, null);
    }

    /// <summary>P757 — draw the help overlay with the manual's topic bar and,
    /// when the current topic is not the Controls reference, the topic's page
    /// text (native HelpPane: nav pane + main page + scroll bars,
    /// effect/help.cpp:210-240). Passing a null manual draws the first Controls
    /// page without a topic bar (legacy callers / capture paths).</summary>
    public void Draw(Viewport viewport, HelpManualModel? manual)
    {
        float w = viewport.Width;
        float h = viewport.Height;

        // A large, near-full-screen reference panel (unlike the starmap's
        // compact pane): the player consults this briefly and closes it.
        float panelW = w * 0.982f;
        // P761 — 0.92 (was 0.90): the catalogue grew past the 90% panel's
        // usable band (the CAMERA / VIEW group alone now needs 1264 px at
        // 1440p — 32px title + 56 rows); 92% still leaves a visible border.
        float panelH = h * 0.92f;
        float panelX = (w - panelW) / 2f;
        float panelY = (h - panelH) / 2f;
        const float padding = HelpOverlayLayout.Padding;

        bool controls = manual is null || HelpManualModel.IsControlsTopic(manual.CurrentTopic);
        var groups = HelpManualModel.ControlsGroups(KeyBindingStore.Instance.Map,
            manual?.CurrentTopic == "hlpControls2" ? "hlpControls2" : "hlpControls");
        var band = HelpOverlayLayout.ContentBand(panelY, panelH, manual is not null);
        float avail = panelW - 2f * padding;
        var table = controls ? ControlsTable(groups, avail, band.Height)
            : new HelpOverlayLayout.ControlTable(1f, [], [], 0f, 0f, band.Height);
        var sections = table.Sections;
        var colWidths = table.ColumnWidths;
        int columns = colWidths.Length;
        float scale = controls ? table.Scale : 1f;
        float scaledGap = HelpOverlayLayout.ColumnGap * scale;

        // Scaled column origins.
        var colX = new float[columns];
        float cx = panelX + padding;
        for (int c = 0; c < columns; c++)
        {
            colX[c] = cx;
            cx += colWidths[c] * scale + scaledGap;
        }

        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
        try
        {
            FillRect(panelX, panelY, panelW, panelH, PanelBg);
            OutlineRect(panelX, panelY, panelW, panelH, PanelBorder);
            FillRect(panelX, panelY, panelW, 2f, PanelBorder);   // top accent edge

            // Header line — P624: generated from the LIVE Help + OpenKeyBindings
            // bindings (KeyBindingStore) so a rebind updates it (the old hard-coded
            // "H TOGGLE" went stale when P623 moved Help to F1). P757: with the
            // manual active, the header also names the navigation.
            var map = KeyBindingStore.Instance.Map;
#if BROWSER
            string header = $"KEYBOARD  ({map.LabelOf("Help")} TOGGLE - LEFT/RIGHT PAGES - ESC BACK)";
#else
            string header = HelpHeader.Line(map.BindingOf("Help").Label, map.BindingOf("OpenKeyBindings").Label);
#endif
            var hs = _small.MeasureString(header) * scale;
            Draw(header, new Vector2((w - hs.X) / 2f, panelY + 8f), HeaderColor, scale);

            // P757 — the manual's topic bar (native nav pane) rides under the
            // header; the Controls topic keeps the full key-reference columns,
            // every other topic shows its page text instead.
            bool showManual = !controls;
            if (manual is not null)
            {
                float navWidth = 0;
                foreach (var t in HelpManualModel.Topics) navWidth += _small.MeasureString(t.Title).X + 34f;
                float navScale = System.MathF.Min(1f, avail / navWidth);
                float tx = panelX + padding;
                foreach (var t in HelpManualModel.Topics)
                {
                    bool cur = t.Id == manual.CurrentTopic;
                    string label = (cur ? "[" : " ") + t.Title + (cur ? "]" : " ");
                    Draw(label, new Vector2(tx, panelY + 30f), cur ? HeaderColor : NoteColor, navScale);
                    tx += _small.MeasureString(t.Title).X * navScale + 34f * navScale;
                }
                if (manual.HistoryDepth > 1)
                    Draw("BACKSPACE: back", new Vector2(panelX + padding, panelY + panelH - 46f), NoteColor, scale);
            }
            if (showManual && manual is not null)
            {
                var topic = manual.Current;
                float y = panelY + 60f;
                for (int i = manual.Scroll; i < topic.Lines.Count; i++)
                {
                    if (y > panelY + panelH - 60f) break;
                    Draw(topic.Lines[i], new Vector2(panelX + padding + 20f, y), DescColor, scale);
                    y += 24f;
                }
            }

            // Sections (the Controls topic's key-reference columns).
            if (!showManual)
            foreach (var section in sections)
            {
                var group = groups[section.GroupIndex];
                float x = colX[section.Column];
                float titleY = band.Top + section.Y * scale;

                Draw(SectionTitle(group, section), new Vector2(x, titleY), GroupTitleColor, scale);

                // Aligned key column: max key label width within this section.
                float keyW = 0f;
                for (int e = section.FirstEntry; e < section.EndEntry; e++)
                {
                    float kw = _small.MeasureString(group.Entries[e].Key).X;
                    if (kw > keyW) keyW = kw;
                }

                float rowY = titleY + (HelpOverlayLayout.RowHeight
                                        + HelpOverlayLayout.TitleGap) * scale;
                for (int e = section.FirstEntry; e < section.EndEntry; e++)
                {
                    var entry = group.Entries[e];
                    Draw(entry.Key, new Vector2(x, rowY), KeyColor, scale);
                    Draw(HelpManualModel.ControlsDescription(entry.Description),
                        new Vector2(x + keyW * scale + 14f * scale, rowY),
                        DescColor, scale);
                    rowY += HelpOverlayLayout.RowHeight * scale;
                }
            }

            // Footer note.
            string note =
                "Most keys are disabled while typing chat or in the quick-chat menu.  "
                + "UP/DOWN topics   BACKSPACE back   PGUP/PGDN scroll   Esc closes.";
            var nsz = _small.MeasureString(note) * scale;
            Draw(note, new Vector2((w - nsz.X) / 2f, panelY + panelH - 24f),
                NoteColor, scale);
        }
        finally
        {
            _sb.End();
        }
    }

    private HelpOverlayLayout.ControlTable ControlsTable(IReadOnlyList<KeyBindingTable.Group> groups,
        float width, float height)
    {
        bool same = _cachedGroups is not null && _cachedGroups.Count == groups.Count
            && _cachedWidth == width && _cachedHeight == height;
        for (int g = 0; same && g < groups.Count; g++)
        {
            var old = _cachedGroups![g];
            var current = groups[g];
            same = old.Title == current.Title && old.Entries.Count == current.Entries.Count;
            for (int e = 0; same && e < current.Entries.Count; e++)
                same = old.Entries[e] == current.Entries[e];
        }
        if (!same)
        {
            _cachedGroups = groups;
            _cachedWidth = width;
            _cachedHeight = height;
            _cachedTable = HelpOverlayLayout.MeasureControls(groups, text => _small.MeasureString(text).X, width, height);
        }
        return _cachedTable!;
    }

    private static string SectionTitle(KeyBindingTable.Group group, HelpOverlayLayout.Section section)
        => section.FirstEntry == 0 ? group.Title : group.Title + " (cont.)";

    private void Draw(string text, Vector2 pos, Color color, float scale) =>
        _sb.DrawString(_small, text, pos, color, 0f, Vector2.Zero, scale,
            SpriteEffects.None, 0f);

    private void FillRect(float x, float y, float w, float h, Color color) =>
        _sb.Draw(_white, new Rectangle((int)System.MathF.Round(x),
            (int)System.MathF.Round(y), (int)System.MathF.Round(w),
            (int)System.MathF.Round(h)), color);

    private void OutlineRect(float x, float y, float w, float h, Color color)
    {
        FillRect(x, y, w, 1f, color);
        FillRect(x, y + h - 1f, w, 1f, color);
        FillRect(x, y, 1f, h, color);
        FillRect(x + w - 1f, y, 1f, h, color);
    }
}
