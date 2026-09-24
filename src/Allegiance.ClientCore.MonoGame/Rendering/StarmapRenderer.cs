using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NumericsVec2 = System.Numerics.Vector2;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P249 — draws the whole-mission starmap overlay: every sector (cluster) in
/// the mission positioned by its AUTHORED <c>ScreenX/ScreenY</c> layout
/// coordinate (fit into the panel by <see cref="StarmapLayout"/>), a line for
/// every warp edge between two sectors, and the player's CURRENT sector
/// highlighted so they always know where they are (the point of the whole
/// feature — Domingo's \"traveling is confusing\" complaint).
///
/// Scope discipline (per brief): this renders ONLY clusters, warp edges, and
/// the current-sector highlight. Station/asteroid icons, ownership coloring
/// and ripcord markers are explicitly later-scope follow-ups and are NOT drawn
/// here.
///
/// Like <see cref="HudRenderer"/>, this begins/ends its own
/// <see cref="SpriteBatch"/> session so it can be invoked over the 3D scene
/// (or into an offscreen capture target) at any point in the draw order.
/// </summary>
public sealed class StarmapRenderer
{
    private readonly SpriteBatch _sb;
    private readonly Texture2D _white;
    private readonly SpriteFont _small;

    // Palette — the teal HUD accent family for consistency with the cockpit
    // gauges; the CURRENT sector jumps to a bright high-contrast gold so the
    // pilot's eye lands on it instantly.
    private static readonly Color PanelBg = new(0.020f, 0.050f, 0.052f, 0.84f);
    private static readonly Color PanelBorder = new(0.16f, 0.42f, 0.40f);
    private static readonly Color TitleDim = new(0.55f, 0.72f, 0.70f);
    private static readonly Color EdgeColor = new(0.42f, 0.62f, 0.58f, 0.70f);
    private static readonly Color DotNormal = new(0.32f, 0.55f, 0.52f, 0.95f);
    private static readonly Color RingNormal = new(0.46f, 0.78f, 0.72f);
    private static readonly Color NameDim = new(0.66f, 0.80f, 0.78f);
    private static readonly Color YouFill = new(1.00f, 0.86f, 0.34f);
    private static readonly Color YouRing = new(1.00f, 0.95f, 0.62f);
    private static readonly Color YouName = new(1.00f, 0.94f, 0.72f);

    public StarmapRenderer(SpriteBatch spriteBatch, Texture2D whitePixel, SpriteFont small)
    {
        _sb = spriteBatch;
        _white = whitePixel;
        _small = small;
    }

    /// <summary>
    /// Draw the starmap overlay for the given cluster sites and warp edges.
    /// <paramref name="sites"/> carries each cluster's id, display name and
    /// authored ScreenX/ScreenY (built by the caller from the world graph);
    /// <paramref name="edges"/> is the warp-edge list (source→dest cluster ids);
    /// <paramref name="currentSectorId"/> (may be null before the client owns a
    /// ship) is highlighted distinctly.
    /// </summary>
    public void Draw(
        IReadOnlyList<StarmapLayout.ClusterSite> sites,
        IReadOnlyList<ClusterInfo.WarpEdge> edges,
        short? currentSectorId,
        Viewport viewport,
        float panelW, float panelH)
    {
        if (sites.Count == 0)
            return;   // nothing to draw until the join burst populates the graph

        float w = viewport.Width;
        float h = viewport.Height;
        float panelX = (w - panelW) / 2f;
        float panelY = (h - panelH) / 2f;
        const float border = 26f;

        var layout = StarmapLayout.Fit(sites, panelX, panelY, panelW, panelH, border);

        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            // Translucent panel backdrop + outline.
            FillRect(panelX, panelY, panelW, panelH, PanelBg);
            OutlineRect(panelX, panelY, panelW, panelH, PanelBorder);
            FillRect(panelX, panelY, panelW, 2f, PanelBorder);   // top accent edge

            // Title line.
            string currentName = currentSectorId is { } cur
                && layout.TryGetName(cur, out var cn) && cn.Length > 0
                ? cn : "?";
            string title = $"STARMAP  |  YOU: {currentName}  |  TAB TOGGLE";
            var tsz = _small.MeasureString(title);
            _sb.DrawString(_small, title, new Vector2(panelX + border, panelY + 10f), TitleDim);

            // Warp edges: a line between the two endpoint sectors for every edge.
            foreach (var e in edges)
            {
                if (layout.TryGetPoint(e.SourceClusterId, out var a)
                    && layout.TryGetPoint(e.DestClusterId, out var b))
                    DrawLine(a, b, 1.6f, EdgeColor);
            }

            // Each cluster: ring + dot + name; current sector highlighted.
            foreach (var site in sites)
            {
                if (!layout.TryGetPoint(site.Id, out var pt))
                    continue;

                bool isYou = site.Id == currentSectorId;
                DrawClusterIcon(pt, isYou);

                string name = site.Name;
                var sz = _small.MeasureString(name);
                _sb.DrawString(_small, name,
                    new Vector2(pt.X - sz.X / 2f, pt.Y + 9f),
                    isYou ? YouName : NameDim);
                if (isYou)
                {
                    string you = "YOU";
                    var ysz = _small.MeasureString(you);
                    _sb.DrawString(_small, you,
                        new Vector2(pt.X - ysz.X / 2f, pt.Y - 8f - ysz.Y), YouRing);
                }
            }
        }
        finally
        {
            _sb.End();
        }
    }

    /// <summary>
    /// P360 — draw the persistent, always-visible compact mini-map in the
    /// bottom-right corner of the viewport (native's SectorInfoPane "closed"
    /// view). Same authored-coord maths as the full pane (fits every sector via
    /// <see cref="StarmapLayout.Fit"/> into the corner rectangle from
    /// <see cref="MinimapGeometry.CornerPanel"/>), rendered small: one ring+dot
    /// per sector, one line per warp edge, the CURRENT sector bright gold, and
    /// the sector names of the sectors DIRECTLY connected to the player drawn
    /// dimmed beside their dots so "current + nearby connections at a glance"
    /// is literal. Non-neighbour names are omitted to keep the small pane
    /// uncluttered. A caption exposes the Tab expand binding (the port's
    /// keyboard-centric HUD has no mouse picker — the mouse is the M
    /// virtual-joystick steering axis), matching the full pane's self-
    /// documenting "TAB TOGGLE" header.
    /// </summary>
    public void DrawMini(
        IReadOnlyList<StarmapLayout.ClusterSite> sites,
        IReadOnlyList<ClusterInfo.WarpEdge> edges,
        short? currentSectorId,
        Viewport viewport)
    {
        if (sites.Count == 0)
            return;   // nothing to draw until the join burst populates the graph

        var (px, py, pw, ph) = MinimapGeometry.CornerPanel(viewport.Width, viewport.Height);
        const float border = 8f;
        var layout = StarmapLayout.Fit(sites, px, py, pw, ph, border);

        // Sectors directly connected to the player by a warp — the only ones
        // labelled in the compact view.
        var neighbourIds = new HashSet<short>();
        if (currentSectorId is { } cur)
        {
            foreach (var e in edges)
            {
                if (e.SourceClusterId == cur) neighbourIds.Add(e.DestClusterId);
                if (e.DestClusterId == cur) neighbourIds.Add(e.SourceClusterId);
            }
        }

        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            // Compact translucent panel + outline + top accent edge.
            FillRect(px, py, pw, ph, PanelBg);
            OutlineRect(px, py, pw, ph, PanelBorder);
            FillRect(px, py, pw, 2f, PanelBorder);

            // Warp edges (thinner than the full pane — this is a glance).
            foreach (var e in edges)
            {
                if (layout.TryGetPoint(e.SourceClusterId, out var a)
                    && layout.TryGetPoint(e.DestClusterId, out var b))
                    DrawLine(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y), 1.1f, EdgeColor);
            }

            // One ring+dot per sector; neighbours get a dim name; the current
            // sector gets the bright gold treatment + its name above the dot.
            foreach (var site in sites)
            {
                if (!layout.TryGetPoint(site.Id, out var pt))
                    continue;

                bool isYou = site.Id == currentSectorId;
                if (isYou)
                {
                    DrawRing(new Vector2(pt.X, pt.Y), 7f, 2f, YouRing);
                    DrawRing(new Vector2(pt.X, pt.Y), 4f, 1.2f, YouRing);
                    FillRect(pt.X - 2.2f, pt.Y - 2.2f, 4.4f, 4.4f, YouFill);
                    DrawCenteredLabel(site.Name, pt.X, pt.Y - 14f, YouName);
                }
                else
                {
                    DrawRing(new Vector2(pt.X, pt.Y), 4f, 1.1f, RingNormal);
                    FillRect(pt.X - 1.4f, pt.Y - 1.4f, 2.8f, 2.8f, DotNormal);
                    if (neighbourIds.Contains(site.Id))
                        DrawCenteredLabel(site.Name, pt.X, pt.Y + 8f, NameDim);
                }
            }

            // Caption: current sector + the expand hint (self-documenting).
            string curName = currentSectorId is { } c
                && layout.TryGetName(c, out var cn) && cn.Length > 0 ? cn : "?";
            string foot = $"MINIMAP  YOU: {curName}   [TAB] EXPAND";
            var fsz = _small.MeasureString(foot);
            _sb.DrawString(_small, foot, new Vector2(px + border, py + ph - fsz.Y - 4f), TitleDim);
        }
        finally
        {
            _sb.End();
        }
    }

    /// <summary>Draw a horizontally-centred text label whose vertical top is
    /// <paramref name="cy"/> — used for the small pane's sparse labels.</summary>
    private void DrawCenteredLabel(string text, float cx, float cy, Color color)
    {
        if (string.IsNullOrEmpty(text))
            return;
        var sz = _small.MeasureString(text);
        _sb.DrawString(_small, text, new Vector2(cx - sz.X / 2f, cy), color);
    }

    private void DrawClusterIcon(NumericsVec2 c, bool isYou)
    {
        if (!isYou)
        {
            DrawRing(new Vector2(c.X, c.Y), 7f, 1.6f, RingNormal);
            FillRect(c.X - 2f, c.Y - 2f, 4f, 4f, DotNormal);
            return;
        }

        // Current sector: a larger, bright, double ring — unmistakable.
        DrawRing(new Vector2(c.X, c.Y), 12f, 3f, YouRing);
        DrawRing(new Vector2(c.X, c.Y), 7f, 1.5f, YouRing);
        FillRect(c.X - 3.5f, c.Y - 3.5f, 7f, 7f, YouFill);
    }

    private void DrawRing(Vector2 c, float radius, float thickness, Color color)
    {
        const int segments = 40;
        for (int i = 0; i < segments; i++)
        {
            float a0 = i * MathHelper.TwoPi / segments;
            float a1 = (i + 1) * MathHelper.TwoPi / segments;
            var p0 = c + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * radius;
            var p1 = c + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * radius;
            DrawLine(p0, p1, thickness, color);
        }
    }

    private void FillRect(float x, float y, float w, float h, Color color) =>
        _sb.Draw(_white, new Rectangle((int)MathF.Round(x), (int)MathF.Round(y),
            (int)MathF.Round(w), (int)MathF.Round(h)), color);

    private void OutlineRect(float x, float y, float w, float h, Color color)
    {
        FillRect(x, y, w, 1f, color);
        FillRect(x, y + h - 1f, w, 1f, color);
        FillRect(x, y, 1f, h, color);
        FillRect(x + w - 1f, y, 1f, h, color);
    }

    private void DrawLine(Vector2 a, Vector2 b, float thickness, Color color)
    {
        Vector2 d = b - a;
        float len = d.Length();
        if (len < 0.001f)
            return;
        float angle = MathF.Atan2(d.Y, d.X);
        _sb.Draw(_white, a + d * 0.5f, null, color, angle,
            new Vector2(0.5f, 0.5f), new Vector2(len, thickness), SpriteEffects.None, 0f);
    }
}
