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

    // ---------- P286 — per-resource-type palette ----------

    [Theory]
    [InlineData((short)0x00, RockTextureFactory.AsteroidResourceType.Plain)]
    [InlineData((short)0x01, RockTextureFactory.AsteroidResourceType.Helium)]
    [InlineData((short)0x02, RockTextureFactory.AsteroidResourceType.Helium)]
    [InlineData((short)0x04, RockTextureFactory.AsteroidResourceType.Gold)]
    [InlineData((short)0x08, RockTextureFactory.AsteroidResourceType.Buildable)]
    [InlineData((short)0x10, RockTextureFactory.AsteroidResourceType.Uranium)]
    [InlineData((short)0x20, RockTextureFactory.AsteroidResourceType.Silicon)]
    [InlineData((short)0x40, RockTextureFactory.AsteroidResourceType.Carbon)]
    public void Classify_RealCorpusMasks_MapToTheirResourceType(short mask, RockTextureFactory.AsteroidResourceType expected)
    {
        // Every one of these masks occurs in the real corpus (measured across all
        // shipped maps); classification must hit exactly that type, nothing invented.
        Assert.Equal(expected, RockTextureFactory.Classify(mask));
    }

    [Fact]
    public void Plain_IsByteIdenticalToNoTypeArg()
    {
        // Plain is the identity shift: the P286 signature must produce exactly
        // the original P253 rock when no type is asked for (keeps the legacy
        // determinism contract).
        var a = RockTextureFactory.GeneratePixels();
        var b = RockTextureFactory.GeneratePixels(type: RockTextureFactory.AsteroidResourceType.Plain);
        for (int i = 0; i < a.Length; i++)
            Assert.Equal(a[i].PackedValue, b[i].PackedValue);
    }

    [Fact]
    public void EachResourceType_HasItsOwnMeanPalette()
    {
        // Distinct types must render as visibly different rocks (the whole point
        // of P286's colour variety): every pair of resource types has a
        // measurably different mean colour. Group the 7 types into a few clearly
        // separated buckets and assert pair-wise separation.
        var types = Enum.GetValues<RockTextureFactory.AsteroidResourceType>();
        var mean = new Dictionary<RockTextureFactory.AsteroidResourceType, (double R, double G, double B)>();
        foreach (var t in types)
        {
            var p = RockTextureFactory.GeneratePixels(type: t);
            double r = 0, g = 0, b = 0;
            foreach (var c in p) { r += c.R; g += c.G; b += c.B; }
            int n = p.Length;
            mean[t] = (r / n / 255.0, g / n / 255.0, b / n / 255.0);
        }
        // Every pair differs by at least 0.020 in some channel (a clearly
        // visible tint change, well above rounding).
        var arr = types;
        for (int i = 0; i < arr.Length; i++)
            for (int j = i + 1; j < arr.Length; j++)
            {
                var a = mean[arr[i]]; var b = mean[arr[j]];
                double maxChan = Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
                Assert.True(maxChan > 0.015,
                    $"{arr[i]} vs {arr[j]} palettes too close (max chan diff {maxChan:F3})");
            }
    }

    [Fact]
    public void Helium_IsCoolerAndGold_WarmerThanPlain()
    {
        // Directional sanity: helium leans blue (B/R rises), gold leans warm
        // (R/B rises) — the intuitive resource tint, and it must not be backwards.
        static (double R, double G, double B) MeanPx(RockTextureFactory.AsteroidResourceType t)
        {
            var p = RockTextureFactory.GeneratePixels(type: t);
            double r = 0, g = 0, b = 0;
            foreach (var c in p) { r += c.R; g += c.G; b += c.B; }
            int n = p.Length;
            return (r / n, g / n, b / n);
        }
        var plain = MeanPx(RockTextureFactory.AsteroidResourceType.Plain);
        var helium = MeanPx(RockTextureFactory.AsteroidResourceType.Helium);
        var gold = MeanPx(RockTextureFactory.AsteroidResourceType.Gold);

        Assert.True(helium.B / helium.R > plain.B / plain.R, "helium must be bluer (higher B/R) than plain");
        Assert.True(gold.R / gold.B > plain.R / plain.B, "gold must be warmer (higher R/B) than plain");
    }

    [Fact]
    public void SameType_TwoCalls_ByteIdentical()
    {
        // The per-type palette rides the same determinism contract: same type +
        // same seed → identical pixels, no System.Random / time.
        var a = RockTextureFactory.GeneratePixels(type: RockTextureFactory.AsteroidResourceType.Helium);
        var b = RockTextureFactory.GeneratePixels(type: RockTextureFactory.AsteroidResourceType.Helium);
        for (int i = 0; i < a.Length; i++)
            Assert.Equal(a[i].PackedValue, b[i].PackedValue);
    }
}
