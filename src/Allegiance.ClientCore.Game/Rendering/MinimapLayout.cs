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
        //
        // P715b — labels are placed with deterministic collision avoidance.
        // Deterministic ORDER: the current sector first (it has priority), then
        // neighbours by ascending id, so placement is stable frame to frame.
        var namedSites = sites
            .Where(s => s.Id == currentSectorId || neighbours.Contains(s.Id))
            .Where(s => !string.IsNullOrEmpty(s.Name))
            .OrderByDescending(s => s.Id == currentSectorId)
            .ThenBy(s => s.Id)
            .ToArray();
        float labelHeight = namedSites.Select(s => measure(s.Name).Y).DefaultIfEmpty(0f).Max();
        float nameReserve = MathF.Min(labelHeight + 8f,
            MathF.Max(0f, (mapArea.Height - 2f * Border - 1f) / 2f));
        var map = StarmapLayout.Fit(sites, px, py + nameReserve,
            pw, mapArea.Height - 2f * nameReserve, Border);

        // Higher-priority label rectangles already accepted, in placement order.
        var placed = new List<Bounds>();
        var labels = new List<Label>();
        foreach (var site in namedSites)
        {
            bool isCurrent = site.Id == currentSectorId;
            if (!map.TryGetPoint(site.Id, out var point)) continue;
            string text = FitText(site.Name, textWidth, measure);
            var size = measure(text);
            if (PickLabelSlot(point, size, site.Id, isCurrent, mapArea, placed, sites, map) is { } rect)
            {
                labels.Add(new(text, rect, isCurrent));
                placed.Add(rect);
            }
        }
        return new()
        {
            Panel = new(px, py, pw, ph), MapArea = mapArea, FooterArea = footerArea,
            Map = map, SectorLabels = labels, FooterLabels = footerLabels,
        };
    }

    /// <summary>
    /// P715b — choose a collision-free slot for one sector label, or return
    /// null to drop the label. Candidates are above / below / left / right of
    /// the dot, tried in a fixed priority (the current sector prefers above,
    /// neighbours prefer below) so placement is deterministic. A candidate is
    /// accepted only if it fits inside the map area (never the footer band),
    /// overlaps no already-placed higher-priority label, and covers no other
    /// site's dot/ring. The current sector's label always wins: if nothing fits
    /// it falls back to the traditional above-dot placement, clamped in.
    /// Low-priority neighbours are dropped rather than overlapping.
    /// </summary>
    private static Bounds? PickLabelSlot(
        System.Numerics.Vector2 point, System.Numerics.Vector2 size, short ownId, bool isCurrent,
        Bounds mapArea, IReadOnlyList<Bounds> placed,
        IReadOnlyList<StarmapLayout.ClusterSite> sites, StarmapLayout map)
    {
        const float gap = 8f;          // distance from the dot to the label edge
        const float dotClearance = 7f; // keep-clear half-extent around a dot/ring
        float cx = point.X, cy = point.Y, w = size.X, h = size.Y;

        // [above, below, left, right]
        Bounds[] cand =
        [
            new(cx - w / 2f, cy - gap - h, w, h),
            new(cx - w / 2f, cy + gap, w, h),
            new(cx - gap - w, cy - h / 2f, w, h),
            new(cx + gap, cy - h / 2f, w, h),
        ];
        // Preferred slot first: current above, neighbours below; the others are
        // only tried when the preferred side is blocked.
        int[] order = isCurrent ? [0, 1, 2, 3] : [1, 0, 2, 3];

        foreach (int i in order)
        {
            // Clamp both axes into the map area (never the footer band), then
            // reject only genuine overlaps — mirrors P715's "clamp up off the
            // footer" behaviour while adding collision avoidance.
            var rect = ClampVert(ClampHoriz(cand[i], mapArea), mapArea);
            if (placed.Any(p => Intersects(rect, p))) continue;
            if (CoversADot(rect, ownId, sites, map, dotClearance)) continue;
            return rect;
        }

        if (isCurrent)
        {
            var fb = ClampHoriz(cand[0], mapArea);
            return ClampVert(fb, mapArea); // current always shows, clamped in
        }
        return null; // drop the lowest-priority neighbour rather than overlap
    }

    private static Bounds ClampHoriz(Bounds r, Bounds mapArea)
    {
        float left = mapArea.X + Border;
        float right = mapArea.Right - Border - r.Width;
        float x = right >= left ? Math.Clamp(r.X, left, right) : left;
        return new(x, r.Y, r.Width, r.Height);
    }

    private static Bounds ClampVert(Bounds r, Bounds mapArea)
    {
        float top = mapArea.Y + Border;
        float bottom = mapArea.Bottom - Border - r.Height;
        float y = bottom >= top ? Math.Clamp(r.Y, top, bottom) : top;
        return new(r.X, y, r.Width, r.Height);
    }

    private static bool Intersects(Bounds a, Bounds b) =>
        a.X < b.Right && a.Right > b.X && a.Y < b.Bottom && a.Bottom > b.Y;

    /// <summary>True when the label rectangle overlaps another site's dot/ring.</summary>
    private static bool CoversADot(Bounds rect, short ownId,
        IReadOnlyList<StarmapLayout.ClusterSite> sites, StarmapLayout map, float c)
    {
        foreach (var s in sites)
        {
            if (s.Id == ownId) continue;
            if (!map.TryGetPoint(s.Id, out var p)) continue;
            if (Intersects(rect, new Bounds(p.X - c, p.Y - c, 2f * c, 2f * c)))
                return true;
        }
        return false;
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
