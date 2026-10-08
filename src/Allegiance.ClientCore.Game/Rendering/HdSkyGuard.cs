using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P577 — pure, unit-testable helpers shared by the HD sky tier: the native-id
/// → HD-asset naming rules (environment index from <c>globeN</c>, planet stem
/// from <c>nebplntNNbmp</c>) and the per-entry readability guard (brightness
/// ceiling + saturation clamp) of P577 scope item 3. No GPU/IO dependencies so
/// the rules are testable headlessly.
/// </summary>
public static class HdSkyGuard
{
    /// <summary>Default maximum sky luminance (0-255) after the cap.</summary>
    public const byte DefaultLumaCap = 225;

    /// <summary>Default saturation ceiling (chroma fraction of max).</summary>
    public const float DefaultSatCap = 0.62f;

    /// <summary>Extract the environment index from a native poster name
    /// (<c>"globe3"</c> → 3, <c>"globe10"</c> → 10). Returns 0 when the name
    /// carries no positive digit (then no HD environment exists).</summary>
    public static int EnvironmentIndex(string poster)
    {
        if (string.IsNullOrEmpty(poster)) return 0;
        int num = 0;
        bool any = false;
        foreach (char ch in poster)
            if (char.IsAsciiDigit(ch)) { num = num * 10 + (ch - '0'); any = true; }
        return any && num > 0 ? num : 0;
    }

    /// <summary>Strip the <c>"bmp"</c> suffix from a native planet poster name so
    /// it becomes the HD asset stem (<c>"nebplnt06bmp"</c> → <c>"nebplnt06"</c>).</summary>
    public static string PlanetStem(string planetName)
        => planetName.EndsWith("bmp", StringComparison.OrdinalIgnoreCase)
            ? planetName[..^3]
            : planetName;

    /// <summary>
    /// The per-entry readability guard applied to each HD sky texture on load:
    /// pixels whose luminance exceeds <paramref name="lumaCap"/> are scaled
    /// down preserving hue, and pixels whose saturation exceeds
    /// <paramref name="satCap"/> are partially desaturated toward the ceiling.
    /// Transparent pixels are untouched. Returns true when any pixel changed.
    /// </summary>
    public static bool ApplyCap(Color[] data, byte lumaCap = DefaultLumaCap,
        float satCap = DefaultSatCap)
    {
        bool changed = false;
        float capL = lumaCap / 255f;
        for (int i = 0; i < data.Length; i++)
        {
            var c = data[i];
            if (c.A == 0) continue;
            float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
            float luma = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            float maxc = Math.Max(r, Math.Max(g, b));
            float minc = Math.Min(r, Math.Min(g, b));
            if (maxc > 0f)
            {
                // HSV saturation clamp to the ceiling (exact, unlike a linear
                // deviation blend which converges to a higher value than the cap).
                float v = maxc;
                float s = (v - minc) / v;
                if (s > satCap)
                {
                    (r, g, b) = Desaturate(r, g, b, maxc, minc, v, satCap);
                    changed = true;
                }
            }
            if (luma > capL)
            {
                float scale = capL / luma;
                r *= scale; g *= scale; b *= scale;
                changed = true;
            }
            data[i] = new Color(
                (byte)Math.Clamp(r * 255f, 0, 255),
                (byte)Math.Clamp(g * 255f, 0, 255),
                (byte)Math.Clamp(b * 255f, 0, 255),
                c.A);
        }
        return changed;
    }

    /// <summary>Reduce a pixel's saturation to <paramref name="satCap"/> (HSV)
    /// keeping its hue and value, returning the recalculated RGB.</summary>
    private static (float r, float g, float b) Desaturate(
        float r, float g, float b, float maxc, float minc, float v, float satCap)
    {
        float d = maxc - minc;
        float h;
        if (d == 0f) h = 0f;
        else if (maxc == r) h = 60f * (((g - b) / d) % 6f);
        else if (maxc == g) h = 60f * (((b - r) / d) + 2f);
        else h = 60f * (((r - g) / d) + 4f);
        if (h < 0f) h += 360f;

        float s = satCap;
        float C = v * s;
        float x = C * (1f - Math.Abs((h / 60f % 2f) - 1f));
        float m = v - C;
        (float rr, float gg, float bb) = h switch
        {
            < 60 => (C, x, 0f),
            < 120 => (x, C, 0f),
            < 180 => (0f, C, x),
            < 240 => (0f, x, C),
            < 300 => (x, 0f, C),
            _ => (C, 0f, x),
        };
        return (rr + m, gg + m, bb + m);
    }
}
