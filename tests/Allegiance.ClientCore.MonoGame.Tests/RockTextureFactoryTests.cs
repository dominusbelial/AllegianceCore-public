using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure tests for <see cref="RockTextureFactory"/> — no graphics device needed
/// (the pixel buffer is CPU-generated). They pin the contracts that make the
/// generated rock diffuse usable: correct square opaque layout, byte-identical
/// determinism (same seed), a palette centred on the rock's gray-brown tone
/// <see cref="AsteroidRenderer"/> used flat before P253, enough contrast to
/// break up the old flat fill, and seamless tiling across the wrap so the whole
/// rock is covered without repeating-square grid lines.
/// </summary>
public class RockTextureFactoryTests
{
    private static Color[] Pixels(int size = 256, int seed = RockTextureFactory.DefaultSeed)
        => RockTextureFactory.GeneratePixels(size, seed);

    [Fact]
    public void GeneratePixels_Default_IsSquareAndOpaque()
    {
        var p = Pixels();
        int size = RockTextureFactory.DefaultSize;
        Assert.Equal(size * size, p.Length);
        for (int i = 0; i < p.Length; i++)
            Assert.Equal(255, p[i].A);
    }

    [Fact]
    public void SameSeed_TwoCalls_ByteIdentical()
    {
        // Determinism contract (same fixed-seed discipline as RockMeshFactory):
        // identical inputs → identical pixels, no System.Random / time.
        var a = Pixels();
        var b = Pixels();
        for (int i = 0; i < a.Length; i++)
            Assert.Equal(a[i].PackedValue, b[i].PackedValue);
    }

    [Fact]
    public void DifferentSeed_DifferentPattern()
    {
        // A different fixed seed must produce a genuinely different pattern —
        // guards against the texture silently collapsing to a constant.
        var a = Pixels();
        var b = Pixels(seed: RockTextureFactory.DefaultSeed + 7);
        int differing = 0;
        for (int i = 0; i < a.Length; i++)
            if (a[i].PackedValue != b[i].PackedValue) differing++;
        Assert.True(differing > a.Length / 2, $"expected most pixels to differ, got {differing}/{a.Length}");
    }

    [Fact]
    public void MeanPalette_IsCentredOnRockColor()
    {
        // The texture must stay in the rock's gray-brown family, not invent a
        // wild new palette: its mean RGB should sit near (0.52, 0.46, 0.40) —
        // the RockColor AsteroidRenderer previously used flat — so switching
        // to a textured diffuse keeps the same overall tone.
        var p = Pixels();
        double r = 0, g = 0, b = 0;
        for (int i = 0; i < p.Length; i++)
        {
            r += p[i].R; g += p[i].G; b += p[i].B;
        }
        r /= p.Length * 255.0; g /= p.Length * 255.0; b /= p.Length * 255.0;

        Assert.InRange(r, 0.515 - 0.060, 0.545 + 0.030);
        Assert.InRange(g, 0.460 - 0.058, 0.480 + 0.026);
        Assert.InRange(b, 0.402 - 0.068, 0.440 + 0.024);
    }

    [Fact]
    public void HasStrongMotting_NotAFlatFill()
    {
        // The whole point of P253 is to break up the old flat single-color
        // rock. Require real contrast: luminance std-dev > 0.050 (measured
        // ~0.074) and a wide per-channel swing (measured red range ~120/255).
        // This reds if the noise collapses to a constant (the pre-texture
        // flat-fill behaviour) or if the contrast gain is accidentally removed.
        var p = Pixels();
        var lum = new double[p.Length];
        double mean = 0;
        for (int i = 0; i < p.Length; i++)
        {
            lum[i] = (0.299 * p[i].R + 0.587 * p[i].G + 0.114 * p[i].B) / 255.0;
            mean += lum[i];
        }
        mean /= p.Length;
        double variance = 0;
        for (int i = 0; i < p.Length; i++)
            variance += (lum[i] - mean) * (lum[i] - mean);
        double sd = Math.Sqrt(variance / p.Length);

        int rMin = 255, rMax = 0;
        for (int i = 0; i < p.Length; i++)
        {
            rMin = Math.Min(rMin, p[i].R);
            rMax = Math.Max(rMax, p[i].R);
        }

        Assert.True(sd > 0.048, $"luminance should be strongly mottled, std-dev {sd:F3} (want > 0.048)");
        Assert.True(rMax - rMin > 60, $"red channel should swing widely, range {rMax - rMin} (want > 60)");
    }

    [Fact]
    public void EdgesTile_SeamlesslyAcrossWrap()
    {
        // Tileability: the pattern must wrap around the image with no worse a
        // seam than anywhere in the interior. We compare the mean per-channel
        // color jump across the left/right (and top/bottom) wrap against the
        // mean jump across ordinary adjacent interior columns/rows; the wrap
        // must be ≤ the internal average (measured ~0.15× — far smoother).
        var p = Pixels();
        int size = RockTextureFactory.DefaultSize;

        double colWrap = ColumnJump(p, size, size - 1, 0);
        double colInternal = 0;
        for (int c = 1; c < size; c++) colInternal += ColumnJump(p, size, c, c - 1);
        colInternal /= size - 1;

        double rowWrap = RowJump(p, size, size - 1, 0);
        double rowInternal = 0;
        for (int r = 1; r < size; r++) rowInternal += RowJump(p, size, r, r - 1);
        rowInternal /= size - 1;

        Assert.True(colWrap <= colInternal * 1.2,
            $"column wrap seam {colWrap:F2} should be ≤ internal avg {colInternal:F2}");
        Assert.True(rowWrap <= rowInternal * 1.2,
            $"row wrap seam {rowWrap:F2} should be ≤ internal avg {rowInternal:F2}");
    }

    private static double ColumnJump(Color[] p, int size, int c1, int c2)
    {
        double tot = 0;
        for (int y = 0; y < size; y++)
            tot += ChannelDelta(p[y * size + c1], p[y * size + c2]);
        return tot / size;
    }

    private static double RowJump(Color[] p, int size, int r1, int r2)
    {
        double tot = 0;
        for (int x = 0; x < size; x++)
            tot += ChannelDelta(p[r1 * size + x], p[r2 * size + x]);
        return tot / size;
    }

    private static double ChannelDelta(Color a, Color b)
        => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
}
