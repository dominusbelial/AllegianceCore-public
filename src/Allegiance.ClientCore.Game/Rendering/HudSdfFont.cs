using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>Build a padded signed-distance atlas once from the shipped glyphs.
/// Rasterize the distance field at the requested physical size, once per
/// size change; the one-pixel coverage edge is independent of DPI. No runtime font or platform dependency.</summary>
public sealed class HudSdfFont : IDisposable
{
    private const int Radius = 6;
    // The nearest opposite-coverage texel defines the signed distance. Visit
    // neighbours in distance order so a Fast render proof can build the real
    // atlas without taking a square root for every candidate of every texel.
    private static readonly (int X, int Y, float Distance)[] Neighbours =
        (from y in Enumerable.Range(-Radius, Radius * 2 + 1)
         from x in Enumerable.Range(-Radius, Radius * 2 + 1)
         let squared = x * x + y * y
         where squared > 0 && squared < Radius * Radius
         orderby squared
         select (x, y, MathF.Sqrt(squared))).ToArray();
    private readonly SpriteFont _font;
    private readonly float _sourceScale;
    private readonly GraphicsDevice _device;
    private readonly Color[] _distances;
    private readonly int _width, _height;
    private sealed record Raster(Texture2D Texture, Dictionary<char, RasterGlyph> Glyphs);
    private readonly record struct RasterGlyph(SpriteFont.Glyph Metrics, Rectangle Source, Rectangle Ink);
    private readonly Dictionary<float, Raster> _sizes = new();
    private readonly Dictionary<char, (SpriteFont.Glyph Glyph, Rectangle Source)> _glyphs = new();
    public HudSdfFont(GraphicsDevice device, SpriteFont font, float sourceScale = 1)
    {
        _font = font; _device = device; _sourceScale = sourceScale;
        var glyphs = font.GetGlyphs();
        int cellW = glyphs.Values.Max(g => g.BoundsInTexture.Width) + Radius * 2;
        int cellH = glyphs.Values.Max(g => g.BoundsInTexture.Height) + Radius * 2;
        int width = cellW * 16, height = cellH * ((glyphs.Count + 15) / 16);
        // Render to Color first: shipped SpriteFonts can have compressed textures.
        var previous = device.GetRenderTargets();
        using var readable = new RenderTarget2D(device, font.Texture.Width, font.Texture.Height, false, SurfaceFormat.Color, DepthFormat.None);
        using (var batch = new SpriteBatch(device))
        {
            device.SetRenderTarget(readable); device.Clear(Color.Transparent);
            batch.Begin(blendState: BlendState.Opaque, samplerState: SamplerState.PointClamp);
            batch.Draw(font.Texture, Vector2.Zero, Color.White); batch.End();
        }
        device.SetRenderTargets(previous);
        var pixels = new Color[readable.Width * readable.Height]; readable.GetData(pixels);
        var atlas = new Color[width * height];
        int index = 0;
        foreach (var (character, glyph) in glyphs)
        {
            var bounds = glyph.BoundsInTexture;
            int ox = index % 16 * cellW, oy = index / 16 * cellH; index++;
            int gw = bounds.Width + Radius * 2, gh = bounds.Height + Radius * 2;
            // An extra empty border makes every neighbour lookup in-bounds.
            int stride = gw + 2 * Radius;
            var coverage = new bool[stride * (gh + 2 * Radius)];
            for (int y = 0; y < bounds.Height; y++)
                for (int x = 0; x < bounds.Width; x++)
                    coverage[(y + 2 * Radius) * stride + x + 2 * Radius] =
                        pixels[(bounds.Y + y) * readable.Width + bounds.X + x].A >= 128;
            for (int y = 0; y < gh; y++)
                for (int x = 0; x < gw; x++)
                {
                    int indexInMask = (y + Radius) * stride + x + Radius;
                    bool inside = coverage[indexInMask];
                    float nearest = Radius;
                    foreach (var n in Neighbours)
                    {
                        if (coverage[indexInMask + n.Y * stride + n.X] == inside) continue;
                        nearest = n.Distance;
                        break;
                    }
                    byte value = (byte)Math.Clamp(128 + (inside ? 1 : -1) * (nearest - .5f) * 127 / Radius, 0, 255);
                    atlas[(oy + y) * width + ox + x] = new Color(value, value, value, value);
                }
            _glyphs.Add(character, (glyph, new(ox, oy, gw, gh)));
        }
        _distances = atlas; _width = width; _height = height;
    }
    public void Draw(SpriteBatch batch, string text, Vector2 position, Color color, float scale)
        => GlyphRun(batch, text, position, color, scale);

    /// <summary>P1107 — the ink box a run of text covers at <paramref name="scale"/>,
    /// computed without touching the GPU (the same maths the batch path uses). Public
    /// for the HUD proofs/tests that measure rendered text height.</summary>
    public Rectangle InkBounds(string text, Vector2 position, float scale)
        => GlyphRun(null, text, position, Color.White, scale);

    private Rectangle GlyphRun(SpriteBatch? batch, string text, Vector2 position, Color color, float scale)
    {
        var atlas = AtScale(scale);
        Rectangle bounds = Rectangle.Empty;
        float x = 0, y = 0; bool first = true;
        foreach (char raw in text)
        {
            if (raw == '\r') continue;
            if (raw == '\n') { x = 0; y += _font.LineSpacing; first = true; continue; }
            if (!atlas.Glyphs.TryGetValue(raw, out var glyph)
                && !atlas.Glyphs.TryGetValue(_font.DefaultCharacter ?? '?', out glyph)) continue;
            var g = glyph.Metrics;
            x += first ? Math.Max(g.LeftSideBearing, 0) : _font.Spacing + g.LeftSideBearing;
            first = false;
            var point = position + new Vector2(x + g.Cropping.X - Radius * _sourceScale, y + g.Cropping.Y - Radius * _sourceScale) * scale;
            if (batch != null) batch.Draw(atlas.Texture, point, glyph.Source, color);
            else if (!glyph.Ink.IsEmpty)
            {
                // Bilinear sampling can cover both neighbouring pixels at a
                // fractional destination. An integral origin is already exact.
                int left = (int)MathF.Floor(point.X) + glyph.Ink.X;
                int top = (int)MathF.Floor(point.Y) + glyph.Ink.Y;
                var ink = new Rectangle(left, top, (int)MathF.Ceiling(point.X) + glyph.Ink.Right - left,
                    (int)MathF.Ceiling(point.Y) + glyph.Ink.Bottom - top);
                bounds = bounds.IsEmpty ? ink : Rectangle.Union(bounds, ink);
            }
            x += g.Width + g.RightSideBearing;
        }
        return bounds;
    }

    private Raster AtScale(float drawScale)
    {
        float scale = drawScale * _sourceScale;
        if (_sizes.TryGetValue(scale, out var existing)) return existing;
        int width = Math.Max(1, (int)MathF.Ceiling(_width * scale)), height = Math.Max(1, (int)MathF.Ceiling(_height * scale));
        var data = new Color[width * height];
        float Sample(int x, int y) => _distances[Math.Clamp(y, 0, _height - 1) * _width + Math.Clamp(x, 0, _width - 1)].A / 255f;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float sx = (x + .5f) / scale - .5f, sy = (y + .5f) / scale - .5f;
                int ix = (int)MathF.Floor(sx), iy = (int)MathF.Floor(sy);
                float tx = sx - ix, ty = sy - iy;
                float top = MathHelper.Lerp(Sample(ix, iy), Sample(ix + 1, iy), tx);
                float bottom = MathHelper.Lerp(Sample(ix, iy + 1), Sample(ix + 1, iy + 1), tx);
                float distance = (MathHelper.Lerp(top, bottom, ty) - .5f) * (Radius * 2) * scale;
                float alpha = Math.Clamp(distance + .5f, 0, 1);
                data[y * width + x] = Color.White * alpha;
            }
        var glyphs = new Dictionary<char, RasterGlyph>(_glyphs.Count);
        foreach (var (c, entry) in _glyphs)
        {
            int Scaled(int pixels) => (int)MathF.Round(pixels * drawScale * _sourceScale);
            var src = new Rectangle(Scaled(entry.Source.X), Scaled(entry.Source.Y),
                Math.Max(1, Scaled(entry.Source.Width)), Math.Max(1, Scaled(entry.Source.Height)));
            int left = src.Width, top = src.Height, right = 0, bottom = 0;
            for (int y = 0; y < src.Height && src.Y + y < height; y++)
                for (int x = 0; x < src.Width && src.X + x < width; x++)
                    if (data[(src.Y + y) * width + src.X + x].A != 0)
                    {
                        left = Math.Min(left, x); top = Math.Min(top, y);
                        right = Math.Max(right, x + 1); bottom = Math.Max(bottom, y + 1);
                    }
            var ink = right > left ? new Rectangle(left, top, right - left, bottom - top) : Rectangle.Empty;
            glyphs.Add(c, new(entry.Glyph, src, ink));
        }
        var texture = new Texture2D(_device, width, height);
        texture.SetData(data);
        var raster = new Raster(texture, glyphs);
        _sizes.Add(scale, raster);
        return raster;
    }
    public void ResetRasterCache() { foreach (var raster in _sizes.Values) raster.Texture.Dispose(); _sizes.Clear(); }
    public void Dispose() => ResetRasterCache();
}
