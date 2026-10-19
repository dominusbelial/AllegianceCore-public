using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>Build a padded signed-distance atlas once from the shipped glyphs.
/// Rasterize the distance field at the requested physical size, once per
/// size change; the one-pixel coverage edge is independent of DPI. No runtime font or platform dependency.</summary>
public sealed class HudSdfFont : IDisposable
{
    private const int Radius = 6;
    private readonly SpriteFont _font;
    private readonly float _sourceScale;
    private readonly GraphicsDevice _device;
    private readonly Color[] _distances;
    private readonly int _width, _height;
    private readonly Dictionary<float, Texture2D> _sizes = new();
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
            bool Inside(int x, int y) => x >= Radius && y >= Radius && x < bounds.Width + Radius && y < bounds.Height + Radius
                && pixels[(bounds.Y + y - Radius) * readable.Width + bounds.X + x - Radius].A >= 128;
            for (int y = 0; y < gh; y++)
                for (int x = 0; x < gw; x++)
                {
                    bool inside = Inside(x, y);
                    float nearest = Radius;
                    for (int dy = -Radius; dy <= Radius; dy++)
                        for (int dx = -Radius; dx <= Radius; dx++)
                        {
                            float distance = MathF.Sqrt(dx * dx + dy * dy);
                            if (distance < nearest && Inside(x + dx, y + dy) != inside) nearest = distance;
                        }
                    byte value = (byte)Math.Clamp(128 + (inside ? 1 : -1) * (nearest - .5f) * 127 / Radius, 0, 255);
                    atlas[(oy + y) * width + ox + x] = new Color(value, value, value, value);
                }
            _glyphs.Add(character, (glyph, new(ox, oy, gw, gh)));
        }
        _distances = atlas; _width = width; _height = height;
    }
    public void Draw(SpriteBatch batch, string text, Vector2 position, Color color, float scale)
    {
        var atlas = AtScale(scale * _sourceScale);
        float x = 0, y = 0; bool first = true;
        foreach (char raw in text)
        {
            if (raw == '\r') continue;
            if (raw == '\n') { x = 0; y += _font.LineSpacing; first = true; continue; }
            char c = _glyphs.ContainsKey(raw) ? raw : _font.DefaultCharacter ?? '?';
            if (!_glyphs.TryGetValue(c, out var entry)) continue;
            var g = entry.Glyph;
            x += first ? Math.Max(g.LeftSideBearing, 0) : _font.Spacing + g.LeftSideBearing;
            first = false;
            var point = position + new Vector2(x + g.Cropping.X - Radius * _sourceScale, y + g.Cropping.Y - Radius * _sourceScale) * scale;
            var src = new Rectangle((int)MathF.Round(entry.Source.X * scale * _sourceScale), (int)MathF.Round(entry.Source.Y * scale * _sourceScale),
                Math.Max(1, (int)MathF.Round(entry.Source.Width * scale * _sourceScale)), Math.Max(1, (int)MathF.Round(entry.Source.Height * scale * _sourceScale)));
            batch.Draw(atlas, point, src, color);
            x += g.Width + g.RightSideBearing;
        }
    }
    private Texture2D AtScale(float scale)
    {
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
        var texture = new Texture2D(_device, width, height); texture.SetData(data); _sizes.Add(scale, texture); return texture;
    }
    public void ResetRasterCache() { foreach (var texture in _sizes.Values) texture.Dispose(); _sizes.Clear(); }
    public void Dispose() { foreach (var texture in _sizes.Values) texture.Dispose(); _sizes.Clear(); }
}
