using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

public readonly record struct HudTextBox(string Text, Rectangle Bounds);

/// <summary>Per-frame measured text placement shared by both skins. Fixed
/// instruments reserve their zones first. Projected labels yield to instruments;
/// subsequent notices yield to labels. No string-length estimates.</summary>
public sealed class HudTextLayout
{
    private readonly List<Rectangle> _reserved = new();
    private readonly List<HudTextBox> _text = new();
    private int _width, _height;
    public IReadOnlyList<HudTextBox> TextBoxes => _text;
    public void Reset(int width, int height)
    { _width = width; _height = height; _reserved.Clear(); _text.Clear(); }
    public void Reserve(Rectangle zone) => _reserved.Add(zone);
    public Rectangle Place(string text, Vector2 measured, Vector2 desired, bool instrument = false)
    {
        int width = Math.Min(_width - 8, Math.Max(1, (int)MathF.Ceiling(measured.X)));
        int height = Math.Min(_height - 8, Math.Max(1, (int)MathF.Ceiling(measured.Y)));
        int x = Math.Clamp((int)MathF.Round(desired.X), 4, Math.Max(4, _width - width - 4));
        int y = Math.Clamp((int)MathF.Round(desired.Y), 4, Math.Max(4, _height - height - 4));
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
                if (!instrument && _reserved.Any(r => r.Intersects(candidate))) continue;
                _text.Add(new(text, candidate));
                return candidate;
            }
        }
        // Overflow must be explicit: silently overlapping text defeats the HUD
        // contract. Normal flight layouts reserve enough room even at 150%.
        throw new InvalidOperationException($"HUD has no free text zone for '{text}' at {_width}x{_height}");
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
        throw new InvalidOperationException($"HUD has no free block zone for '{text}' at {_width}x{_height}");
    }
    private static Rectangle Padded(Rectangle r) { r.Inflate(2, 2); return r; }
}
