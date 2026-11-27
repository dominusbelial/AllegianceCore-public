using Microsoft.Xna.Framework;

using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

// Frozen copy of HudTextLayout at 28debb3c for the differential test.
internal sealed class LinearHudTextLayout
{
    private readonly List<Rectangle> _reserved = new();
    private readonly List<Rectangle> _instruments = new();
    private readonly List<HudTextBox> _text = new();
    private int _width, _height;
    public IReadOnlyList<HudTextBox> TextBoxes => _text;
    public void Reset(int width, int height)
    { _width = width; _height = height; _reserved.Clear(); _instruments.Clear(); _text.Clear(); }
    public void Reserve(Rectangle zone) => _reserved.Add(zone);
    // Actual target glyphs and geometry also exclude the own-ship text, which
    // normally ignores its own approximate gauge reservation.
    internal void ReserveInstrument(Rectangle zone) { _instruments.Add(zone); Reserve(zone); }
    public Rectangle Place(string text, Vector2 measured, Vector2 desired, bool instrument = false, bool fixedAnchor = false)
    {
        int width = Math.Min(_width - 8, Math.Max(1, (int)MathF.Ceiling(measured.X)));
        int height = Math.Min(_height - 8, Math.Max(1, (int)MathF.Ceiling(measured.Y)));
        int x = Math.Clamp((int)MathF.Round(desired.X), 4, Math.Max(4, _width - width - 4));
        int y = Math.Clamp((int)MathF.Round(desired.Y), 4, Math.Max(4, _height - height - 4));
        if (fixedAnchor)
        {
            var fixedBox = new Rectangle(x, y, width, height);
            _text.Add(new(text, fixedBox, FixedAnchor: true));
            return fixedBox;
        }
        var candidate = new Rectangle(x, y, width, height);
        // Keep the native X anchor wherever possible; alternate downward/upward
        // rows before looking in an adjacent column. Every accepted box is tested.
        for (int column = 0; column < 7; column++)
        {
            int cx = Math.Clamp(x + ((column + 1) / 2) * (column % 2 == 0 ? -1 : 1) * (width + 8), 4, Math.Max(4, _width - width - 4));
            for (int row = 0; row < _height / Math.Max(1, height + 4) * 2 + 2; row++)
            {
                int cy = y + ((row + 1) / 2) * (row % 2 == 0 ? -1 : 1) * (height + 4);
                if (cy < 4 || cy + height > _height - 4) continue;
                candidate = new(cx, cy, width, height);
                if (_text.Any(t => Padded(t.Bounds).Intersects(candidate))) continue;
                if ((instrument ? _instruments : _reserved).Any(r => r.Intersects(candidate))) continue;
                _text.Add(new(text, candidate));
                return candidate;
            }
        }
        // Overflow (a crowded battle fills every free zone) falls back to the
        // desired anchor, overlapping like native, which never de-overlaps HUD
        // text (radarimage.cpp:1284-1311). Throwing here crashed the client.
        return Overflow(text, x, y, width, height);
    }

    /// <summary>Place a pre-measured multi-line block (radar labels, which stack
    /// name/range text and health bars). Unlike <see cref="Place"/> the block is
    /// also reserved so later notices/text yield to it, and it is recorded so the
    /// overlap check still sees it.</summary>
    public Rectangle PlaceBlock(string text, Rectangle desired)
    {
        int width = Math.Min(_width - 8, Math.Max(1, desired.Width));
        int height = Math.Min(_height - 8, Math.Max(1, desired.Height));
        int x = Math.Clamp(desired.X, 4, Math.Max(4, _width - width - 4));
        int y = Math.Clamp(desired.Y, 4, Math.Max(4, _height - height - 4));
        var candidate = new Rectangle(x, y, width, height);
        for (int column = 0; column < 7; column++)
        {
            int cx = Math.Clamp(x + ((column + 1) / 2) * (column % 2 == 0 ? -1 : 1) * (width + 8), 4, Math.Max(4, _width - width - 4));
            for (int row = 0; row < _height / Math.Max(1, height + 4) * 2 + 2; row++)
            {
                int cy = y + ((row + 1) / 2) * (row % 2 == 0 ? -1 : 1) * (height + 4);
                if (cy < 4 || cy + height > _height - 4) continue;
                candidate = new(cx, cy, width, height);
                if (_text.Any(t => Padded(t.Bounds).Intersects(candidate))) continue;
                if (_reserved.Any(r => r.Intersects(candidate))) continue;
                _text.Add(new(text, candidate));
                _reserved.Add(candidate);
                return candidate;
            }
        }
        return Overflow(text, x, y, width, height);   // see Place
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
        if (NextPanelY(panel, parts, _reserved) == y) return panel;
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
                    int next = NextPanelY(panel, parts, pass == 0 ? _reserved : _instruments);
                    if (next == panel.Y) return panel;
                    panel.Y = next;
                }
            }
            if (cx == 4) break;
        }
        return new(x, y, desired.Width, desired.Height);
    }

    private int NextPanelY(Rectangle panel, List<Rectangle> parts, List<Rectangle> zones)
    {
        int next = panel.Y;
        foreach (var part in parts)
        {
            var bounds = part;
            bounds.Offset(panel.Location);
            foreach (var box in _text)
            {
                var obstacle = Padded(box.Bounds);
                if (obstacle.Intersects(bounds)) next = Math.Max(next, obstacle.Bottom - part.Y);
            }
            foreach (var obstacle in zones)
                if (obstacle.Intersects(bounds)) next = Math.Max(next, obstacle.Bottom - part.Y);
        }
        return next;
    }

    /// <summary>The no-free-zone fallback: the clamped desired box, recorded
    /// (so <see cref="TextBoxes"/> stays complete) but not reserved.</summary>
    private Rectangle Overflow(string text, int x, int y, int width, int height)
    {
        var box = new Rectangle(x, y, width, height);
        _text.Add(new(text, box));
        return box;
    }
    private static Rectangle Padded(Rectangle r) { r.Inflate(2, 2); return r; }
}
