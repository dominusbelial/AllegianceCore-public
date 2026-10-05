namespace Allegiance.Core.Rendering;

/// <summary>
/// P529 — minimal 32-bit RGBA pixel (R,G,B,A each 0-255) that lets the rock
/// mip-map generation live in <c>Allegiance.Core</c> — pure and headlessly
/// testable, with no MonoGame/GPU dependency. The MonoGame renderer converts
/// its <c>Color[]</c> base planes to/from this only at the texture-upload
/// boundary.
/// </summary>
public readonly record struct Rgba32(byte R, byte G, byte B, byte A);

/// <summary>
/// P529 — box-filter mip-map chain generation for the rock textures, factored
/// into <c>Allegiance.Core</c> so the anti-alias fix is provable headlessly
/// (<c>LevelCount &gt; 1</c>, correct averaging, terminates at 1×1). The
/// renderer builds a <c>Texture2D(gd,w,h,mipmap:true)</c> and uploads each
/// level's plane with <c>SetData(level, …)</c>.
/// </summary>
public static class MipmapChain
{
    /// <summary>
    /// Number of mip levels for a width×height texture: the larger dimension is
    /// halved (floored) each step until 1, so a 256×256 rock has 9 levels and a
    /// 2048×2048 real surface has 12 — matching <c>Texture2D.LevelCount</c> for
    /// square power-of-two textures (the only sizes the rock pipeline uses).
    /// </summary>
    public static int LevelCount(int width, int height)
    {
        int dim = Math.Max(width, height);
        return (int)MathF.Floor(MathF.Log2(dim)) + 1;
    }

    /// <summary>The next level's dimensions (halved, floored to at least 1).</summary>
    public static void NextLevelSize(int width, int height, out int nextWidth, out int nextHeight)
    {
        nextWidth = Math.Max(1, width / 2);
        nextHeight = Math.Max(1, height / 2);
    }

    /// <summary>
    /// Box-filter a full RGBA plane into the next mip level: each destination
    /// pixel is the component-wise average of the up-to-2×2 source pixels in its
    /// block (edges take only the pixels present for non-even sizes). Alpha is
    /// averaged like the colours. The result's dimensions are
    /// <see cref="NextLevelSize"/> of the input.
    /// </summary>
    public static Rgba32[] Downsample(ReadOnlySpan<Rgba32> src, int width, int height)
    {
        NextLevelSize(width, height, out int dw, out int dh);
        var dst = new Rgba32[dw * dh];
        for (int dy = 0; dy < dh; dy++)
        {
            for (int dx = 0; dx < dw; dx++)
            {
                long r = 0, g = 0, b = 0, a = 0;
                int n = 0;
                for (int sy = dy * 2; sy < dy * 2 + 2 && sy < height; sy++)
                {
                    for (int sx = dx * 2; sx < dx * 2 + 2 && sx < width; sx++)
                    {
                        var p = src[sy * width + sx];
                        r += p.R; g += p.G; b += p.B; a += p.A;
                        n++;
                    }
                }
                dst[dy * dw + dx] = new Rgba32((byte)(r / n), (byte)(g / n), (byte)(b / n), (byte)(a / n));
            }
        }
        return dst;
    }
}
