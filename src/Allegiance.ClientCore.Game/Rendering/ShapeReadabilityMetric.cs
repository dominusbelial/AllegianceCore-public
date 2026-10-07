using System;
using Allegiance.Core.Collision;
using Allegiance.Core.Rendering;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P564 — a pure, GPU-free readability metric that turns a rendered object into a
/// small set of "does its SHAPE read?" numbers. Domingo's report ("everything reads
/// as a flat silhouette") is a loss of shading contrast, so the metric measures how
/// much the object's brightness varies — spatially (over its rendered pixels) and
/// geometrically (with its surface normals). Keeping it pure lets the readability
/// TABLE (per object / view / sector, before vs after) be reproduced headless and
/// unit-tested without a graphics device.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Pixel stats</b> (<see cref="ComputePixelStats"/>): over the object's
/// non-background pixels — the luminance mean, the standard deviation (how much the
/// shading varies at all), the 10th–90th percentile spread (robust to a few clipped
/// hot/faint pixels), and the near-black fraction (&lt; luminance 25) that says a hull
/// is crushing toward invisible.</item>
/// <item><b>Normal-vs-brightness correlation</b> (<see cref="ComputeNormalBrightness"/>):
/// the (area-weighted) Pearson correlation between each triangle's key-light
/// Lambertian term <c>max(dot(N, toward), 0)</c> and its lit brightness under the SAME
/// <see cref="SectorLighting"/> the real renderer applies. A high positive value means
/// brightness genuinely tracks surface orientation — i.e. the light reveals the shape,
/// not a flat wash. <see cref="LambertVariation"/> is the standard deviation of that
/// Lambertian term, the underlying shading contrast the correlation builds on.</item>
/// </list>
/// </remarks>
public static class ShapeReadabilityMetric
{
    /// <summary>Near-black luminance threshold (0..255), matching P530's
    /// "<c>&lt;</c> luminance 25" readability reading.</summary>
    public const byte NearBlackLuma = 25;

    /// <summary>The full readability reading for one object/view/sector.</summary>
    public readonly record struct Readability(
        double MeanLuminance,
        double LuminanceStdDev,
        double Spread10_90,
        double NearBlackFraction,
        double NormalBrightnessCorrelation,
        double LambertVariation);

    /// <summary>
    /// Luminance statistics over the object's non-background pixels.
    /// <paramref name="rgba"/> is tightly packed RGBA bytes, row-major, W×H; a pixel
    /// belongs to the object when any channel differs from the background
    /// (<paramref name="bgR"/>/<paramref name="bgG"/>/<paramref name="bgB"/>) by more
    /// than <paramref name="tolerance"/> (accounting for blend/AA edges). Returns the
    /// pixel-based part of the reading (normal-based fields left 0).
    /// </summary>
    public static Readability ComputePixelStats(
        byte[] rgba, int w, int h, byte bgR, byte bgG, byte bgB, int tolerance = 6)
    {
        if (rgba.Length < w * h * 4) throw new ArgumentException("rgba too small for WxH");
        var vals = new List<double>(w * h);
        for (int i = 0; i < w * h; i++)
        {
            int b = i * 4;
            int r = rgba[b], g = rgba[b + 1], bl = rgba[b + 2];
            if (Math.Abs(r - bgR) <= tolerance && Math.Abs(g - bgG) <= tolerance
                && Math.Abs(bl - bgB) <= tolerance)
                continue;
            vals.Add(0.299 * r + 0.587 * g + 0.114 * bl);
        }
        if (vals.Count == 0)
            return new Readability(0, 0, 0, 0, 0, 0);

        vals.Sort();
        int n = vals.Count;
        double sum = 0;
        foreach (var v in vals) sum += v;
        double mean = sum / n;
        double varSum = 0;
        foreach (var v in vals) varSum += (v - mean) * (v - mean);
        double sd = Math.Sqrt(varSum / n);

        int lo = (int)(0.10 * (n - 1)), hi = (int)(0.90 * (n - 1));
        double spread = vals[hi] - vals[lo];
        int nearBlack = 0;
        foreach (var v in vals) if (v < NearBlackLuma) nearBlack++;

        return new Readability(
            MeanLuminance: mean,
            LuminanceStdDev: sd,
            Spread10_90: spread,
            NearBlackFraction: (double)nearBlack / n,
            NormalBrightnessCorrelation: 0,
            LambertVariation: 0);
    }

    /// <summary>
    /// The mesh-based normal-vs-brightness correlation and Lambertian variation for a
    /// set of triangles under the given sector lighting. Each triangle contributes an
    /// area-weighted sample: <c>x</c> = key-light Lambertian <c>max(dot(N, toward),0)</c>
    /// (how strongly the face turns toward the key light), <c>y</c> = its lit brightness
    /// <c>ambient + keyCol·x + altCol·max(dot(N,−toward),0)</c> — the exact terms
    /// <see cref="SectorLightingEffect"/> sets on a <c>BasicEffect</c>. The Pearson r
    /// between x and y says whether the light reveals the geometry; a flat wash gives
    /// r → 0 (and low <see cref="LambertVariation"/>).
    /// </summary>
    public static (double Correlation, double LambertVariation) ComputeNormalBrightness(
        float[] positions, float[] normals, int[] indices, SectorLighting lighting)
    {
        Vector3 toward = Normalize(-ToCore(lighting.KeyDirection));
        Vector3 altToward = Normalize(-ToCore(lighting.AltDirection));
        double keyR = lighting.KeyColor.X, keyG = lighting.KeyColor.Y, keyB = lighting.KeyColor.Z;
        double altR = lighting.AltColor.X, altG = lighting.AltColor.Y, altB = lighting.AltColor.Z;
        double ambR = lighting.Ambient.X, ambG = lighting.Ambient.Y, ambB = lighting.Ambient.Z;

        double sw = 0, sxl = 0, syl = 0, sxx = 0, syy = 0, sxy = 0;
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            int ia = indices[t], ib = indices[t + 1], ic = indices[t + 2];
            if (ia < 0 || ib < 0 || ic < 0) continue;
            var nm = AvgNormal(normals, ia, ib, ic);
            var area = TriArea(positions, ia, ib, ic);
            if (area <= 1e-12f) area = 1e-3f;

            double lamKey = Math.Max(Dot(nm, toward), 0);
            double lamAlt = Math.Max(Dot(nm, altToward), 0);
            double bright = (ambR + keyR * lamKey + altR * lamAlt) * 0.299
                          + (ambG + keyG * lamKey + altG * lamAlt) * 0.587
                          + (ambB + keyB * lamKey + altB * lamAlt) * 0.114;

            double w = area, x = lamKey, y = bright;
            sw += w; sxl += w * x; syl += w * y; sxx += w * x * x; syy += w * y * y; sxy += w * x * y;
        }
        if (sw <= 0) return (0, 0);
        double mx = sxl / sw, my = syl / sw;
        double cov = sxy / sw - mx * my;
        double vx = sxx / sw - mx * mx, vy = syy / sw - my * my;
        double r = (vx > 1e-12 && vy > 1e-12) ? cov / (Math.Sqrt(vx) * Math.Sqrt(vy)) : 0;
        return (r, Math.Sqrt(Math.Max(0, vx)));
    }

    private static Vector3 AvgNormal(float[] n, int ia, int ib, int ic)
    {
        var a = new Vector3(n[3 * ia], n[3 * ia + 1], n[3 * ia + 2]);
        var b = new Vector3(n[3 * ib], n[3 * ib + 1], n[3 * ib + 2]);
        var c = new Vector3(n[3 * ic], n[3 * ic + 1], n[3 * ic + 2]);
        var m = (a + b + c) / 3f;
        var l = m.Length();
        return l < 1e-9f ? Vector3.UnitZ : m / l;
    }

    private static float TriArea(float[] p, int ia, int ib, int ic)
    {
        var a = new Vector3(p[3 * ia], p[3 * ia + 1], p[3 * ia + 2]);
        var b = new Vector3(p[3 * ib], p[3 * ib + 1], p[3 * ib + 2]);
        var c = new Vector3(p[3 * ic], p[3 * ic + 1], p[3 * ic + 2]);
        var ab = b - a;
        var ac = c - a;
        float cx = ab.Y * ac.Z - ab.Z * ac.Y;
        float cy = ab.Z * ac.X - ab.X * ac.Z;
        float cz = ab.X * ac.Y - ab.Y * ac.X;
        return MathF.Sqrt(cx * cx + cy * cy + cz * cz) * 0.5f;
    }

    private static Vector3 ToCore(Allegiance.Core.Collision.Vector3 v) => new(v.X, v.Y, v.Z);
    private static Vector3 Normalize(Vector3 v) { var l = v.Length(); return l < 1e-9f ? Vector3.UnitZ : v / l; }
    private static float Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    /// <summary>Minimal immutable float3 used by the pure math (no XNA dependency).</summary>
    private readonly struct Vector3
    {
        public readonly float X, Y, Z;
        public Vector3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public static Vector3 UnitZ => new(0, 0, 1);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vector3 operator -(Vector3 a) => new(-a.X, -a.Y, -a.Z);
        public static Vector3 operator *(Vector3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);
        public static Vector3 operator /(Vector3 a, float s) => new(a.X / s, a.Y / s, a.Z / s);
        public float Length() => MathF.Sqrt(X * X + Y * Y + Z * Z);
    }
}
