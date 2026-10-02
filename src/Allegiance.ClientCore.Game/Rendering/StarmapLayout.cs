using System.Numerics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P249 — whole-mission starmap layout. Maps the map-maker's AUTHORED 2-D
/// cluster layout coordinates (<c>DataClusterIGC.ScreenX/ScreenY</c> — the
/// exact fields native's <c>sectormap.cpp</c> uses, <c>IclusterIGC::GetScreenX/
/// GetScreenY</c>, <c>clusterIGC.h:369-371</c>) into a screen-space panel via
/// the same min/max normalization native performs in
/// <c>CalculateScreenMinAndMax</c> (<c>sectormap.cpp:743-833</c>) and the same
/// point mapping as native's <c>GetClusterPoint</c> (<c>sectormap.cpp:1310-1316</c>).
///
/// This class is deliberately PURE — <c>System.Numerics.Vector2</c> only, no
/// MonoGame types on its surface — so it is headless-unit-testable (the
/// established pure-logic discipline of this client, e.g. P240's
/// <c>SmoothedOrientation</c>). The companion <see cref="StarmapRenderer"/>
/// consumes the resulting screen points to draw the overlay.
/// </summary>
public sealed class StarmapLayout
{
    /// <summary>One cluster's authored screen coordinate plus its display name.</summary>
    public readonly record struct ClusterSite(short Id, string Name, float ScreenX, float ScreenY);

    private readonly Dictionary<short, Vector2> _points;
    private readonly Dictionary<short, string> _names;

    private StarmapLayout(Dictionary<short, Vector2> points, Dictionary<short, string> names,
        float minX, float maxX, float minY, float maxY)
    {
        _points = points;
        _names = names;
        MinX = minX;
        MaxX = maxX;
        MinY = minY;
        MaxY = maxY;
    }

    /// <summary>Computed layout x-bounds of the authored coords (post fudge/aspect fit).</summary>
    public float MinX { get; }
    public float MaxX { get; }
    public float MinY { get; }
    public float MaxY { get; }

    /// <summary>The number of clusters the layout was built from (0 if the input was empty).</summary>
    public int Count => _points.Count;

    /// <summary>The on-screen point (in viewport px) for a cluster id, or null if the
    /// id was not part of the laid-out set.</summary>
    public bool TryGetPoint(short id, out Vector2 point) => _points.TryGetValue(id, out point);

    /// <summary>The display name for a cluster id, or empty if unknown (never null).</summary>
    public bool TryGetName(short id, out string name)
    {
        if (_names.TryGetValue(id, out var n))
        {
            name = n ?? "";
            return true;
        }
        name = "";
        return false;
    }

    /// <summary>
    /// Fit the authored <c>ScreenX/ScreenY</c> coordinates into the given panel
    /// rectangle (viewport px), replicating native's <c>CalculateScreenMinAndMax</c>
    /// (sectormap.cpp:743-833):
    /// <list type="number">
    /// <item>min/max of the authored x/y over every site;</item>
    /// <item>a border fudge <c>c_bfr = 0.1 * max(xrange, yrange, 0.0001)</c> expanded
    /// on all sides (sectormap.cpp:777-783);</item>
    /// <item>aspect-ratio preservation into the usable panel area (minus
    /// <c>border</c> on each side) by growing the narrower axis (sectormap.cpp:791-826).</item>
    /// </list>
    /// Points are then mapped like native's <c>GetClusterPoint</c> (sectormap.cpp:1310-1316):
    /// <c>x = panelX + border + (ScreenX-minX)/(maxX-minX)*(panelW-2*border)</c>, likewise y.
    /// Handles a degenerate single-cluster / zero-extent set by centering it.
    /// </summary>
    public static StarmapLayout Fit(
        IReadOnlyList<ClusterSite> sites,
        float panelX, float panelY, float panelW, float panelH, float border)
    {
        var points = new Dictionary<short, Vector2>();
        var names = new Dictionary<short, string>();
        if (sites.Count == 0)
            return new StarmapLayout(points, names, 0f, 0f, 0f, 0f);

        // Min/max of the authored coords.
        float minX = sites[0].ScreenX, maxX = sites[0].ScreenX;
        float minY = sites[0].ScreenY, maxY = sites[0].ScreenY;
        foreach (var s in sites)
        {
            minX = MathF.Min(minX, s.ScreenX);
            maxX = MathF.Max(maxX, s.ScreenX);
            minY = MathF.Min(minY, s.ScreenY);
            maxY = MathF.Max(maxY, s.ScreenY);
            names[s.Id] = s.Name ?? "";
        }

        // Border fudge (native c_bfr).
        float c_bfr = 0.1f * MathF.Max(0.0001f, MathF.Max(maxX - minX, maxY - minY));
        minX -= c_bfr;
        maxX += c_bfr;
        minY -= c_bfr;
        maxY += c_bfr;

        float xRange = maxX - minX;
        float yRange = maxY - minY;

        // Usable panel area (inside the border).
        float usableW = MathF.Max(1f, panelW - 2f * border);
        float usableH = MathF.Max(1f, panelH - 2f * border);
        float desiredAspect = usableW / usableH;

        // Aspect-ratio preservation: grow the narrower axis to match the panel.
        float aspect = (xRange <= 1e-6f || yRange <= 1e-6f) ? desiredAspect : xRange / yRange;
        if (aspect < desiredAspect)
        {
            float grow = (desiredAspect / aspect - 1f) * xRange;
            minX -= grow / 2f;
            maxX += grow / 2f;
            xRange = maxX - minX;
        }
        else if (aspect > desiredAspect)
        {
            float grow = (aspect / desiredAspect - 1f) * yRange;
            minY -= grow / 2f;
            maxY += grow / 2f;
            yRange = maxY - minY;
        }

        float scaleX = usableW / MathF.Max(xRange, 1e-6f);
        float scaleY = usableH / MathF.Max(yRange, 1e-6f);

        // Guard against a perfectly degenerate all-identical set: center it.
        if (xRange <= 1e-6f && yRange <= 1e-6f)
        {
            var center = new Vector2(panelX + panelW / 2f, panelY + panelH / 2f);
            foreach (var s in sites) points[s.Id] = center;
            return new StarmapLayout(points, names, minX, maxX, minY, maxY);
        }

        foreach (var s in sites)
        {
            float px = panelX + border + (s.ScreenX - minX) * scaleX;
            float py = panelY + border + (s.ScreenY - minY) * scaleY;
            points[s.Id] = new Vector2(px, py);
        }

        return new StarmapLayout(points, names, minX, maxX, minY, maxY);
    }
}
