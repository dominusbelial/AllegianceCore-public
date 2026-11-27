using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

public readonly record struct HudTextBox(string Text, Rectangle Bounds, bool FixedAnchor = false);

/// <summary>Per-frame measured text placement shared by both skins. Fixed
/// instruments reserve their zones first. Projected labels yield to instruments;
/// subsequent notices yield to labels. No string-length estimates.</summary>
public sealed class HudTextLayout
{
    private readonly HudObstacleTiles _obstacles = new();
    private readonly List<HudTextBox> _text = new();
    private int _width, _height;
    public IReadOnlyList<HudTextBox> TextBoxes => _text;

    public void Reset(int width, int height)
    {
        _width = width;
        _height = height;
        _text.Clear();
        _obstacles.Reset(width, height);
    }

    public void Reserve(Rectangle zone) => _obstacles.Add(zone, blocksInstruments: false);
    // Actual target glyphs and geometry also exclude the own-ship text, which
    // normally ignores its own approximate gauge reservation.
    internal void ReserveInstrument(Rectangle zone) => _obstacles.Add(zone, blocksInstruments: true);

    public Rectangle Place(string text, Vector2 measured, Vector2 desired, bool instrument = false, bool fixedAnchor = false)
        => PlaceMeasured(text, new((int)MathF.Round(desired.X), (int)MathF.Round(desired.Y),
            (int)MathF.Ceiling(measured.X), (int)MathF.Ceiling(measured.Y)), instrument, fixedAnchor, block: false);

    /// <summary>Place and reserve a pre-measured multi-line radar label.</summary>
    public Rectangle PlaceBlock(string text, Rectangle desired)
        => PlaceMeasured(text, desired, instrument: false, fixedAnchor: false, block: true);

    private Rectangle PlaceMeasured(string text, Rectangle desired, bool instrument, bool fixedAnchor, bool block)
    {
        int width = Math.Min(_width - 8, Math.Max(1, desired.Width));
        int height = Math.Min(_height - 8, Math.Max(1, desired.Height));
        int x = Math.Clamp(desired.X, 4, Math.Max(4, _width - width - 4));
        int y = Math.Clamp(desired.Y, 4, Math.Max(4, _height - height - 4));
        var anchor = new Rectangle(x, y, width, height);
        if (fixedAnchor) return Record(text, anchor, fixedAnchor: true);

        // Keep the native X anchor wherever possible; alternate downward/upward
        // rows before looking in an adjacent column.
        for (int column = 0; column < 7; column++)
        {
            int cx = Math.Clamp(x + ((column + 1) / 2) * (column % 2 == 0 ? -1 : 1) * (width + 8), 4, Math.Max(4, _width - width - 4));
            for (int row = 0; row < _height / Math.Max(1, height + 4) * 2 + 2; row++)
            {
                int cy = y + ((row + 1) / 2) * (row % 2 == 0 ? -1 : 1) * (height + 4);
                if (cy < 4 || cy + height > _height - 4) continue;
                var candidate = new Rectangle(cx, cy, width, height);
                if (_obstacles.BlockingBottom(candidate, instrument).HasValue) continue;
                if (block) Reserve(candidate);
                return Record(text, candidate);
            }
        }
        // Overflow overlaps at the clamped native anchor (radarimage.cpp:1284-1311).
        return Record(text, anchor);
    }

    private Rectangle Record(string text, Rectangle box, bool fixedAnchor = false)
    {
        _text.Add(new(text, box, fixedAnchor));
        var padded = box;
        padded.Inflate(2, 2);
        _obstacles.Add(padded, blocksInstruments: true);
        return box;
    }

    // Keep a stack of related instruments together. Search the preferred column
    // first, jumping to the bottom of each obstacle instead of skipping a whole
    // panel height (which can miss the only usable strip on a small window).
    internal Rectangle PlacePanel(Rectangle desired, List<Rectangle> parts)
    {
        int right = Math.Max(4, _width - 4 - desired.Width);
        int bottom = Math.Max(4, _height - 4 - desired.Height);
        int x = Math.Clamp(desired.X, 4, right);
        int y = Math.Clamp(desired.Y, 4, bottom);
        var panel = new Rectangle(x, y, desired.Width, desired.Height);
        // The old glass can extend behind an unrelated label even when all
        // three row elements are clear. Keep that layout pixel-for-pixel.
        if (NextPanelY(panel, parts, instrument: false) == y) return panel;
        // Once a move is necessary, clear the backing plate as well, so it
        // cannot dim a gauge or let another label appear inside SYSTEMS.
        parts.Clear();
        parts.Add(new(0, 0, desired.Width, desired.Height));
        // If every column is crowded, give already placed text precedence over
        // the approximate zone reservations before falling back to the anchor.
        for (int pass = 0; pass < 2; pass++)
        for (int cx = x; cx >= 4; cx = Math.Max(4, cx - 16))
        {
            panel.X = cx;
            for (int sweep = 0; sweep < 2; sweep++)
            {
                int end = sweep == 0 ? bottom : y - 1;
                for (panel.Y = sweep == 0 ? y : 4; panel.Y <= end;)
                {
                    int next = NextPanelY(panel, parts, instrument: pass != 0);
                    if (next == panel.Y) return panel;
                    panel.Y = next;
                }
            }
            if (cx == 4) break;
        }
        return new(x, y, desired.Width, desired.Height);
    }

    private int NextPanelY(Rectangle panel, List<Rectangle> parts, bool instrument)
    {
        int next = panel.Y;
        foreach (var part in parts)
        {
            var bounds = part;
            bounds.Offset(panel.Location);
            if (_obstacles.BlockingBottom(bounds, instrument, part.Y) is int y)
                next = Math.Max(next, y);
        }
        return next;
    }
}
