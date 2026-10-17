using System.Numerics;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Measured compact-map text and geometry, shared by DrawMini and headless tests.
/// Port extension: native WinTrek/sectormap.cpp:404-422 places the expand image
/// and selected-sector caption separately; it has no combined keyboard footer.
/// </summary>
public sealed class MinimapLayout
{
    /// <summary>Inset between the panel outline and its contents, in pixels.</summary>
    public const float Border = 8f;

    /// <summary>A screen-space rectangle in viewport pixels.</summary>
    public readonly record struct Bounds(float X, float Y, float Width, float Height)
    {
        /// <summary>Right edge in viewport pixels.</summary>
        public float Right => X + Width;
        /// <summary>Bottom edge in viewport pixels.</summary>
        public float Bottom => Y + Height;
    }

    /// <summary>A measured string with its draw position and current-sector tint.</summary>
    public readonly record struct Label(string Text, Bounds Rectangle, bool IsCurrent = false);

    /// <summary>The outer corner panel.</summary>
    public Bounds Panel { get; private init; }
    /// <summary>The region reserved for map icons and sector names.</summary>
    public Bounds MapArea { get; private init; }
    /// <summary>The bottom band reserved exclusively for the caption.</summary>
    public Bounds FooterArea { get; private init; }
    /// <summary>Authored sector coordinates fitted into the map area.</summary>
    public StarmapLayout Map { get; private init; } = null!;
    /// <summary>Current and directly warp-connected sector names.</summary>
    public IReadOnlyList<Label> SectorLabels { get; private init; } = [];
    /// <summary>Measured caption lines.</summary>
    public IReadOnlyList<Label> FooterLabels { get; private init; } = [];

    /// <summary>
    /// Build compact-map geometry using the renderer's actual font measurement.
    /// Names above/below sector dots and the Tab caption are port UI conventions.
    /// </summary>
    public static MinimapLayout Build(IReadOnlyList<StarmapLayout.ClusterSite> sites,
        IReadOnlyList<ClusterInfo.WarpEdge> edges, short? currentSectorId,
        float viewportWidth, float viewportHeight, Func<string, Vector2> measure)
    {
        var (px, py, pw, ph) = MinimapGeometry.CornerPanel(viewportWidth, viewportHeight);
        float textWidth = pw - 2f * Border;
        string name = currentSectorId is { } id
            ? sites.FirstOrDefault(s => s.Id == id).Name ?? "?" : "?";
        if (name.Length == 0) name = "?";
        string footer = $"MINIMAP  YOU: {name}   [TAB] EXPAND";
        if (measure(footer).X > textWidth) footer = $"YOU: {name}   [TAB] EXPAND";
        string[] lines = measure(footer).X <= textWidth
            ? [footer]
            : [FitText($"YOU: {name}", textWidth, measure), FitText("[TAB] EXPAND", textWidth, measure)];
        const float padding = 4f, lineGap = 2f, mapGap = 4f;
        float footerHeight = lines.Sum(line => measure(line).Y) + (lines.Length - 1) * lineGap
            + 2f * padding + mapGap;
        var mapArea = new Bounds(px, py, pw, ph - footerHeight);
        var footerArea = new Bounds(px, mapArea.Bottom, pw, footerHeight);
        var footerLabels = new List<Label>();
        float footerY = footerArea.Y + mapGap + padding;
        foreach (string line in lines)
        {
            var size = measure(line);
            footerLabels.Add(new(line, new(px + Border, footerY, size.X, size.Y)));
            footerY += size.Y + lineGap;
        }

        var neighbours = new HashSet<short>();
        foreach (var edge in edges)
        {
            if (edge.SourceClusterId == currentSectorId) neighbours.Add(edge.DestClusterId);
            if (edge.DestClusterId == currentSectorId) neighbours.Add(edge.SourceClusterId);
        }
        // Reserve name-height headroom above and below the fitted dot field.
        // The explicit clamp also protects long labels at the horizontal edges.
        var namedSites = sites.Where(s => s.Id == currentSectorId || neighbours.Contains(s.Id))
            .Where(s => !string.IsNullOrEmpty(s.Name)).ToArray();
        float labelHeight = namedSites.Select(s => measure(s.Name).Y).DefaultIfEmpty(0f).Max();
        float nameReserve = MathF.Min(labelHeight + 8f,
            MathF.Max(0f, (mapArea.Height - 2f * Border - 1f) / 2f));
        var map = StarmapLayout.Fit(sites, px, py + nameReserve,
            pw, mapArea.Height - 2f * nameReserve, Border);
        var labels = new List<Label>();
        foreach (var site in namedSites)
        {
            bool isCurrent = site.Id == currentSectorId;
            if (!map.TryGetPoint(site.Id, out var point)) continue;
            string text = FitText(site.Name, textWidth, measure);
            var size = measure(text);
            float x = Math.Clamp(point.X - size.X / 2f, px + Border, px + pw - Border - size.X);
            float y = Math.Clamp(isCurrent ? point.Y - 8f - size.Y : point.Y + 8f,
                mapArea.Y + Border, MathF.Max(mapArea.Y + Border, mapArea.Bottom - Border - size.Y));
            labels.Add(new(text, new(x, y, size.X, size.Y), isCurrent));
        }
        return new()
        {
            Panel = new(px, py, pw, ph), MapArea = mapArea, FooterArea = footerArea,
            Map = map, SectorLabels = labels, FooterLabels = footerLabels,
        };
    }

    // ASCII dots are supported by the HUD sprite font. Measure every candidate
    // rather than assuming fixed glyph widths, including for proportional fonts.
    private static string FitText(string text, float width, Func<string, Vector2> measure)
    {
        if (measure(text).X <= width) return text;
        string suffix = "...";
        while (suffix.Length > 0 && measure(suffix).X > width) suffix = suffix[..^1];
        int length = text.Length;
        while (length > 0 && measure(text[..length] + suffix).X > width) length--;
        return text[..length] + suffix;
    }
}
