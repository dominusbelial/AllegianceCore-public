using Allegiance.ClientCore.MonoGame.Flight;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P1014: draw-only V2 screen. Its pure presentation snapshot contains the text,
/// selection and semantic inks; layout owns all bounds and hover hit targets.
/// Game1 draws the real P519 hull model in ShipPreview before this overlay.
/// </summary>
public sealed class LoadoutV2Renderer(SpriteBatch batch, Texture2D white, SpriteFont small, SpriteFont big) : IDisposable
{
    // Existing ModernHudRenderer cyan/muted/orange; Classic docked screen gold,
    // pale blue and grey. Better/worse reuse the old loadout's fit/no-fit inks.
    public static Color ColorFor(LoadoutV2Ink ink, HudStyle style) => ink switch
    {
        LoadoutV2Ink.Better => new Color(.42f, 1f, .55f),
        LoadoutV2Ink.Worse => new Color(1f, .45f, .42f),
        LoadoutV2Ink.Accent => style == HudStyle.Modern ? new Color(83, 225, 219) : new Color(255, 217, 115),
        LoadoutV2Ink.Muted => style == HudStyle.Modern ? new Color(162, 189, 201) : new Color(158, 168, 179),
        _ => style == HudStyle.Modern ? new Color(224, 236, 238) : new Color(242, 247, 250),
    };

    public void Draw(LoadoutV2Presentation frame, LoadoutV2Layout layout, HudStyle style,
        LoadoutV2Layout.Element? hover = null, LoadoutV2HardpointMarker? hardpoint = null)
    {
        var accent = ColorFor(LoadoutV2Ink.Accent, style);
        var background = new Color(.03f, .042f, .058f, .98f);
        batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp);
        try
        {
            foreach (var region in new[] { layout.Header, layout.HullList, layout.SlotList,
                         layout.StatsBar, layout.PriceBox })
            {
                Fill(region, background);
                Outline(region, accent * .35f);
            }
            Outline(layout.ShipPreview, accent * .35f);
            if (layout.OfferCapacity > 0) Fill(layout.OfferViewport, new Color(24, 39, 48));
            foreach (var element in layout.Elements)
            {
                var bounds = element.TextBounds;
                float scale = layout.Scale;
                switch (element.Kind)
                {
                    case LoadoutV2Layout.Kind.Title:
                        Text(new(frame.Title, LoadoutV2Ink.Accent), bounds, style, scale, title: true); break;
                    case LoadoutV2Layout.Kind.Money:
                        Text(new(frame.Money), bounds, style, scale, title: true); break;
                    case LoadoutV2Layout.Kind.HullHeading:
                        Text(new("HULLS", LoadoutV2Ink.Accent), bounds, style, scale); break;
                    case LoadoutV2Layout.Kind.SlotHeading:
                        Text(new("EQUIPMENT / INLINE OFFERS", LoadoutV2Ink.Accent), bounds, style, scale); break;
                    case LoadoutV2Layout.Kind.Hull:
                        Row(frame.Hulls[element.Index], element, style, scale, hover); break;
                    case LoadoutV2Layout.Kind.Slot:
                        Row(frame.Slots[element.Index], element, style, scale, hover); break;
                    case LoadoutV2Layout.Kind.Offer:
                        Row(frame.Offers[element.Index], element, style, scale, hover); break;
                    case LoadoutV2Layout.Kind.Stat:
                        Row(frame.Stats[element.Index], element, style, scale, null, stat: true); break;
                    case LoadoutV2Layout.Kind.Price:
                        float line = bounds.Height / frame.Price.Count;
                        for (int i = 0; i < frame.Price.Count; i++)
                            Text(frame.Price[i], new(bounds.X, bounds.Y + line * i, bounds.Width, line), style, scale * .9f);
                        break;
                    case LoadoutV2Layout.Kind.Default:
                    case LoadoutV2Layout.Kind.Revert:
                    case LoadoutV2Layout.Kind.Buy:
                    case LoadoutV2Layout.Kind.Launch:
                        bool hovered = hover == element || frame.ActionFocus == element.Kind;
                        Fill(element.Bounds, hovered ? accent * .23f : background);
                        Outline(element.Bounds, hovered ? accent : accent * .5f);
                        var label = element.Kind switch
                        {
                            LoadoutV2Layout.Kind.Buy => frame.Buy,
                            LoadoutV2Layout.Kind.Launch => frame.Launch,
                            _ => new LoadoutV2Text(element.Kind.ToString()),
                        };
                        Text(label, bounds, style, scale, centered: true);
                        break;
                }
            }
            // P1015: the hint is the live key map (LoadoutV2Input.Hint); the literal
            // is the P1014 text for callers that still mirror the old shop keys.
            var footer = layout.Footer;
            Text(new(frame.Hint ?? "Q/E hull  |  Tab hulls/parts  |  Arrows select  |  Space choose  |  B back  |  L launch",
                LoadoutV2Ink.Muted), new(16, footer.Y + 5, footer.Width - 32, 20), style, .85f * layout.Scale);
            Overflow(layout.HullList, layout.HullScroll, layout.HullCapacity, frame.Hulls.Count, style, layout.Scale);
            Overflow(layout.SlotList, layout.SlotScroll, layout.SlotCapacity - layout.OfferCapacity, frame.Slots.Count, style, layout.Scale);
        }
        finally { batch.End(); }
        if (hardpoint is not null) DrawHardpoint(hardpoint, layout, style);
    }

    private readonly RasterizerState _previewClip = new() { CullMode = CullMode.None, ScissorTestEnable = true };

    public void Dispose() => _previewClip.Dispose();

    private void DrawHardpoint(LoadoutV2HardpointMarker marker, LoadoutV2Layout layout, HudStyle style)
    {
        var device = batch.GraphicsDevice;
        var savedClip = device.ScissorRectangle;
        var savedRasterizer = device.RasterizerState;
        var preview = layout.ShipPreview;
        device.ScissorRectangle = new Rectangle((int)preview.X + 1, (int)preview.Y + 1,
            (int)preview.Width - 2, (int)preview.Height - 2);
        var accent = ColorFor(LoadoutV2Ink.Accent, style) * (marker.Occluded ? .4f : 1f);
        float scale = layout.Scale;
        batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp,
            DepthStencilState.None, _previewClip);
        try
        {
            for (int i = 1; i < marker.Arc.Count; i++)
            {
                if (marker.Arc[i - 1] is { } a && marker.Arc[i] is { } b) Line(a, b, accent * .5f, scale);
                if (i % 12 == 0 && marker.Arc[i] is { } rim) Line(marker.Point, rim, accent * .3f, scale);
            }
            if (marker.AxisEnd is { } end) Line(marker.Point, end, accent * .65f, scale);
            float radius = 8 * scale;
            for (int i = 0; i < 32; i++)
            {
                float a = MathHelper.TwoPi * i / 32, b = MathHelper.TwoPi * (i + 1) / 32;
                Line(marker.Point + radius * new Vector2(MathF.Cos(a), MathF.Sin(a)),
                    marker.Point + radius * new Vector2(MathF.Cos(b), MathF.Sin(b)), accent, 1.6f * scale);
            }
            Fill(new(marker.Point.X - 2 * scale, marker.Point.Y - 2 * scale, 4 * scale, 4 * scale), accent);
            string label = ShopLayout.AsciiSafe(marker.Label + (marker.GunnerName is { Length: > 0 } name ? " / " + name : ""));
            if (marker.Occluded) label += " (behind hull)";
            float labelWidth = MathF.Min(preview.Width - 24 * scale, small.MeasureString(label).X * .8f * scale + 12 * scale);
            float x = Math.Clamp(marker.Point.X + 14 * scale, preview.X + 8 * scale, preview.Right - labelWidth - 8 * scale);
            float y = Math.Clamp(marker.Point.Y - 27 * scale, preview.Y + 8 * scale, preview.Bottom - 54 * scale);
            var bounds = new TouchRect(x, y, labelWidth, 25 * scale);
            Fill(bounds, new Color(8, 12, 16) * .85f);
            Text(new(label, marker.Occluded ? LoadoutV2Ink.Muted : LoadoutV2Ink.Accent), bounds, style, .8f * scale);
            if (marker.IsTurret)
            {
                string guide = marker.ArcLimitKnown ? "Firing arc" : "Aim guide / arc limit unavailable";
                Text(new(guide, LoadoutV2Ink.Muted), new(preview.X + 10 * scale, preview.Bottom - 30 * scale,
                    preview.Width - 20 * scale, 22 * scale), style, .75f * scale);
            }
        }
        finally
        {
            batch.End();
            device.ScissorRectangle = savedClip;
            device.RasterizerState = savedRasterizer;
        }
    }

    private void Line(Vector2 from, Vector2 to, Color color, float thickness)
    {
        var delta = to - from;
        batch.Draw(white, from, null, color, MathF.Atan2(delta.Y, delta.X), new Vector2(0, .5f),
            new Vector2(delta.Length(), thickness), SpriteEffects.None, 0);
    }

    private void Row(LoadoutV2DisplayRow row, LoadoutV2Layout.Element element, HudStyle style,
        float scale, LoadoutV2Layout.Element? hover, bool stat = false)
    {
        var accent = ColorFor(LoadoutV2Ink.Accent, style);
        if (row.Selected || row.Focused || hover == element)
        {
            Fill(element.Bounds, accent * (hover == element ? .22f : .12f));
            if (row.Focused || hover == element) Outline(element.Bounds, accent * .65f);
            if (row.Selected) Fill(new(element.Bounds.X, element.Bounds.Y, 3 * scale, element.Bounds.Height), accent);
        }
        var bounds = new TouchRect(element.TextBounds.X, element.Bounds.Y + 2 * scale,
            element.TextBounds.Width, element.Bounds.Height - 4 * scale);
        float half = bounds.Height / 2;
        Text(row.Main, new(bounds.X, bounds.Y, bounds.Width, half), style, scale * (stat ? .82f : .95f));
        // Per-part changes retain separate colours on the detail line. Fit each
        // segment in the remaining budget using the shared ellipsis helper.
        float detailScale = scale * .78f;
        if (stat)
        {
            // Keep both the value and the docked-ship delta visible. Long
            // numbers/units must not spend the delta's width on an ellipsis.
            float width = small.MeasureString(string.Join("  ", row.Detail.Select(d => d.Text))).X;
            detailScale = MathF.Min(detailScale, MathF.Max(0, bounds.Width - 2) / MathF.Max(1, width));
        }
        float x = bounds.X;
        for (int i = 0; i < row.Detail.Count && x < bounds.Right; i++)
        {
            var segment = row.Detail[i];
            if (i > 0) segment = segment with { Text = "  " + segment.Text };
            float used = Text(segment, new(x, bounds.Y + half, bounds.Right - x, half), style, detailScale);
            x += used;
        }
    }

    private void Overflow(TouchRect region, int offset, int capacity, int total, HudStyle style, float scale)
    {
        if (total <= capacity) return;
        string text = $"{offset + 1}-{Math.Min(total, offset + capacity)} / {total}";
        var size = small.MeasureString(text) * (.68f * scale);
        Text(new(text, LoadoutV2Ink.Muted), new(region.Right - size.X - 12 * scale,
            region.Y + 7 * scale, size.X + 2, 16 * scale), style, .68f * scale);
    }

    private float Text(LoadoutV2Text text, TouchRect bounds, HudStyle style, float scale,
        bool title = false, bool centered = false)
    {
        var font = title ? big : small;
        // Rows never spill vertically, even with a larger font or compact layout.
        scale = MathF.Min(scale, bounds.Height / MathF.Max(1, font.LineSpacing));
        if (scale <= 0 || bounds.Width <= 0) return 0;
        var element = new LoadoutV2Layout.Element(default, bounds, bounds);
        string fitted = LoadoutV2Layout.FitText(element, ShopLayout.AsciiSafe(text.Text),
            s => font.MeasureString(s).X, scale);
        var size = font.MeasureString(fitted) * scale;
        var pos = new Vector2(bounds.X + (centered ? (bounds.Width - size.X) / 2 : 0),
            bounds.Y + (bounds.Height - font.LineSpacing * scale) / 2);
        batch.DrawString(font, fitted, pos, ColorFor(text.Ink, style), 0, Vector2.Zero, scale, SpriteEffects.None, 0);
        return size.X;
    }

    private void Fill(TouchRect rect, Color color) => batch.Draw(white,
        new Rectangle((int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height), color);
    private void Outline(TouchRect r, Color color)
    {
        Fill(new(r.X, r.Y, r.Width, 1), color);
        Fill(new(r.X, r.Bottom - 1, r.Width, 1), color);
        Fill(new(r.X, r.Y, 1, r.Height), color);
        Fill(new(r.Right - 1, r.Y, 1, r.Height), color);
    }
}
