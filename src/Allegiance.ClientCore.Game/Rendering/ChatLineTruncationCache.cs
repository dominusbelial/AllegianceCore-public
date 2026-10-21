using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// HUD-12b — per-source-string cache for the chat pane's ellipsis truncation.
/// The modern chat pane clipped an overlong line with a per-character loop that
/// built <c>rowText + "..."</c> for every character of every line on every frame
/// (ModernHudRenderer), i.e. hundreds of throw-away strings per frame once a
/// few chat lines are long. The native pane clips the glyph run once, and the
/// source string only changes when a new chat line arrives, so the fitted text
/// is cached by (source string, available pixel width, font scale). A steady
/// frame allocates nothing.
/// </summary>
public sealed class ChatLineTruncationCache
{
    private readonly record struct Key(string Text, int WidthQuarterPixels, int ScaleMilli);

    private const int Limit = 512;
    private readonly Dictionary<Key, string> _cache = new();

    /// <summary>Return the source text, or its longest prefix that fits
    /// <paramref name="maxWidth"/> with a trailing "..." (native clip). The
    /// returned instance is reused for the same source/width/scale.</summary>
    public string Fit(string text, float maxWidth, SpriteFont font, float scale)
    {
        var key = new Key(text, (int)MathF.Round(maxWidth * 4f), (int)MathF.Round(scale * 1000f));
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var fitted = FitSlow(text, maxWidth, font, scale);
        if (_cache.Count >= Limit) _cache.Clear();
        _cache[key] = fitted;
        return fitted;
    }

    // The measuring lambda captures font/scale, and C# allocates a capturing
    // closure at the entry of the method that declares it. Keeping it here means
    // a cache hit in Fit allocates nothing.
    private static string FitSlow(string text, float maxWidth, SpriteFont font, float scale)
        => FitToWidth(text, maxWidth, t => font.MeasureString(t).X * scale);

    /// <summary>Test/headless overload with an explicit measurement function.</summary>
    public string Fit(string text, float maxWidth, Func<string, float> measure)
    {
        var key = new Key(text, (int)MathF.Round(maxWidth * 4f), 1000);
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var fitted = FitToWidth(text, maxWidth, measure);
        if (_cache.Count >= Limit) _cache.Clear();
        _cache[key] = fitted;
        return fitted;
    }

    /// <summary>Longest prefix of <paramref name="text"/> whose ellipsis form fits
    /// the width (binary search; the same result as the old per-character loop,
    /// which also left "..." when not even that fits).</summary>
    public static string FitToWidth(string text, float maxWidth, Func<string, float> measure)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (measure(text) <= maxWidth) return text;
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (measure(text[..mid] + "...") <= maxWidth) lo = mid;
            else hi = mid - 1;
        }
        return text[..lo] + "...";
    }
}
