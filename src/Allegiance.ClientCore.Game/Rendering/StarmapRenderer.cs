using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.Core;
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

    // P762 — native sector-map artwork (sectormap.cpp:1056-1099), decoded from
    // the same .mdl texture pipeline the rest of the HUD/art uses. Null when
    // the caller supplies no asset source (tests/headless), in which case the
    // old primitive shapes remain as a graceful fallback.
    private readonly MdlTexture2D? _icons;

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

    // P374 — threat-icon palette + the flashing selected-outline cyan. Distinct
    // colours per severity tier (native blits a distinct BMP per tier,
    // sectormap.cpp:1055-1082 — sectorwarning/sectorcapital/sectorbomber/
    // sectorminer/sectorcombat); exact icon ART is not available to this port,
    // so each tier gets a distinct colour + primitive shape instead (documented
    // simplification in the P374 report).
    private static readonly Color ThreatRed = new(1.00f, 0.06f, 0.06f);      // station threatened (>= cw13)
    private static readonly Color ThreatCapital = new(1.00f, 0.10f, 0.80f);  // capital ship (>= cw10)
    private static readonly Color ThreatBomber = new(1.00f, 0.62f, 0.08f);    // bomber (>= cw8)
    private static readonly Color ThreatMiner = new(0.95f, 0.92f, 0.15f);     // miner/builder threatened (>= cw5)
    private static readonly Color ThreatCombat = new(0.30f, 0.82f, 1.00f);    // general combat (>= cw4)
    private static readonly Color HighlightFlash = new(0.03f, 0.98f, 0.98f);  // cyan flashing selected outline

    // P493 — the "known enemy here" marker colour: a distinct solid red so it is
    // never confused with the P374 flashing threat tiers or the P460 ownership
    // rings. Drawn as a small offset cross beside the cluster dot.
    private static readonly Color EnemyKnown = new(0.98f, 0.16f, 0.10f);

    public StarmapRenderer(SpriteBatch spriteBatch, Texture2D whitePixel, SpriteFont small,
        GraphicsDevice? graphicsDevice = null, IAssetSource? assets = null)
    {
        _sb = spriteBatch;
        _white = whitePixel;
        _small = small;
        if (graphicsDevice is not null && assets is not null)
            _icons = new MdlTexture2D(graphicsDevice, assets);
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
        short? selectedSectorId,
        Func<short, short>? warnings,
        bool flashOn,
        Viewport viewport,
        float panelW, float panelH,
        IReadOnlyDictionary<short, SectorMapQueries.ClusterOwners>? owners = null,
        Func<short, Color?>? sideColor = null,
        IReadOnlyDictionary<short, int>? knownEnemySectors = null,
        IReadOnlySet<short>? highlightedClusters = null,
        short? chatClusterId = null)
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

            // P374 — the warning text for the SELECTED sector, surfaced on the
            // starmap's info area (the port's keyboard-only client has no mouse
            // hover, so the selected sector is the hover-equivalent; native
            // draws GetClusterWarningText on hover, sectormap.cpp:523-534).
            if (selectedSectorId is { } sel && warnings is not null
                && layout.TryGetName(sel, out var selName) && selName.Length > 0)
            {
                short cw = warnings(sel);
                string wtxt = StarmapThreatPresentation.WarningText(cw);
                if (wtxt.Length > 0)
                {
                    var wsz = _small.MeasureString(wtxt);
                    _sb.DrawString(_small, wtxt,
                        new Vector2(panelX + border, panelY + 10f + tsz.Y + 3f), ThreatRed);
                    // Flashing highlight label so the eye finds the warned sector.
                    var hsz = _small.MeasureString(selName);
                    _sb.DrawString(_small, selName,
                        new Vector2(panelX + border + wsz.X + 10f, panelY + 10f + tsz.Y + 3f),
                        flashOn ? ThreatRed : TitleDim);
                }
            }

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
                // P762b — native's chat-cluster and selection artwork
                // (sectormap.cpp:1094-1099): the player's CHAT CLUSTER
                // (GetChatCluster(), not the commander's HIGHLIGHT_CLUSTER
                // set) gets sectorhighlightbmp; otherwise the selected sector
                // gets sectorselbmp. The chat test wins, exactly native's
                // if/else chain.
                var overlay = StarmapThreatPresentation.OverlayFor(site.Id, chatClusterId, selectedSectorId);
                if (StarmapThreatPresentation.OverlayAssetStem(overlay) is { } overlayStem)
                    DrawSectorBitmap(overlayStem, pt, 1f);

                // P762b — restore P374's flashing cyan sectorEmpty outline for
                // the commander's highlighted clusters on the OFF flash phase
                // (pCluster->GetHighlight() && !m_bFlashFrame,
                // sectormap.cpp:1043-1046). This is the HIGHLIGHT_CLUSTER
                // concept; it is deliberately separate from the chat-cluster
                // overlay above.
                if (StarmapThreatPresentation.FlashingOutline(highlightedClusters, site.Id, flashOn))
                    DrawFlashingOutline(pt);
                // P460 — the ownership rings, drawn FIRST (underneath) so the
                // cluster icon and the on-phase threat icon always stay visible
                // on top (native draws its owner highlight AFTER the warning
                // blit, sectormap.cpp:1059-1093 then 1172-1190; the brief asks
                // the port to keep rings from hiding those).
                DrawOwnership(pt, site.Id, owners, sideColor, mini: false);
                DrawClusterIcon(pt, isYou);

                // P374 — flashing threat icon, per severity (native
                // sectormap.cpp:1055-1082), drawn ONLY on the on-flash phase.
                if (flashOn && warnings is not null)
                {
                    short cw = warnings(site.Id);
                    if (cw > ClusterWarnings.NoThreat)
                        DrawThreatIcon(pt, cw);
                }

                // P493 — a "known enemy here" marker for every sector whose
                // last-known enemy ship positions came in via cross-side
                // SHIP_STATUS(90) (see SectorMapQueries.KnownEnemySectors).
                if (knownEnemySectors is not null && knownEnemySectors.TryGetValue(site.Id, out var ecount) && ecount > 0)
                    DrawEnemyMarker(pt, ecount, mini: false);

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
        Func<short, short>? warnings,
        bool flashOn,
        Viewport viewport,
        IReadOnlyDictionary<short, SectorMapQueries.ClusterOwners>? owners = null,
        Func<short, Color?>? sideColor = null,
        IReadOnlyDictionary<short, int>? knownEnemySectors = null,
        IReadOnlySet<short>? highlightedClusters = null,
        short? chatClusterId = null)
    {
        if (sites.Count == 0)
            return;   // nothing to draw until the join burst populates the graph

        var mini = MinimapLayout.Build(sites, edges, currentSectorId,
            viewport.Width, viewport.Height, text =>
            {
                var size = _small.MeasureString(text);
                return new NumericsVec2(size.X, size.Y);
            });
        var panel = mini.Panel;
        var layout = mini.Map;
        float px = panel.X, py = panel.Y, pw = panel.Width, ph = panel.Height;

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
                }
                else
                {
                    // P460 — ownership rings first (underneath) so the compact
                    // threat markers (drawn later) stay visible on top.
                    DrawOwnership(pt, site.Id, owners, sideColor, mini: true);
                    DrawRing(new Vector2(pt.X, pt.Y), 4f, 1.1f, RingNormal);
                    FillRect(pt.X - 1.4f, pt.Y - 1.4f, 2.8f, 2.8f, DotNormal);
                }
                // P762b — the compact map uses the same chat-cluster overlay
                // rule as the full pane (sectormap.cpp:1094-1099); the
                // compact view has no selection bitmap.
                if (chatClusterId == site.Id)
                    DrawSectorBitmap("sectorhighlightbmp", pt, 0.55f);
                if (StarmapThreatPresentation.FlashingOutline(highlightedClusters, site.Id, flashOn))
                    DrawRing(new Vector2(pt.X, pt.Y), 8f, 2f, HighlightFlash);
                // P374 — the compact view flashes a small per-severity threat
                // marker on the on-flash phase (native's closed sector-info
                // pane shows the same warnings, sectormap.cpp:1055-1082).
                if (flashOn && warnings is not null)
                {
                    short cw = warnings(site.Id);
                    if (cw > ClusterWarnings.NoThreat)
                        DrawThreatIcon(pt, cw, 0.55f);
                }
                // P493 — the compact mini-map also shows known enemy sectors,
                // small.
                if (knownEnemySectors is not null && knownEnemySectors.TryGetValue(site.Id, out var ecount) && ecount > 0)
                    DrawEnemyMarker(pt, ecount, mini: true);
            }

            foreach (var label in mini.SectorLabels)
                _sb.DrawString(_small, label.Text, new Vector2(label.Rectangle.X, label.Rectangle.Y),
                    label.IsCurrent ? YouName : NameDim);
            foreach (var label in mini.FooterLabels)
                _sb.DrawString(_small, label.Text, new Vector2(label.Rectangle.X, label.Rectangle.Y), TitleDim);
        }
        finally
        {
            _sb.End();
        }
    }

    /// <summary>
    /// P460 — draw the sector-ownership rings for one cluster, if it has an
    /// owner. The owner gets a solid-coloured ring at a radius just outside the
    /// cluster dot; a secondary owner (runner-up) gets a thinner, slightly inner
    /// ring, mirroring native's <c>m_pimageOwnerHighlight</c> /
    /// <c>m_pimageSecondaryOwnerHighlight</c> blits in the two sides' colours
    /// (sectormap.cpp:1172-1190). <paramref name="mini"/> scales the radii down
    /// for the compact minimap. Called BEFORE the cluster icon/threat so the
    /// rings sit underneath and never hide the P374 icons or current highlight.
    /// </summary>
    private void DrawOwnership(NumericsVec2 c, short siteId,
        IReadOnlyDictionary<short, SectorMapQueries.ClusterOwners>? owners,
        Func<short, Color?>? sideColor, bool mini)
    {
        if (owners is null || sideColor is null)
            return;
        if (!owners.TryGetValue(siteId, out var own))
            return;
        var v = new Vector2(c.X, c.Y);
        if (mini)
        {
            if (own.Secondary is { } sec && sideColor(sec) is { } secCol)
                DrawRing(v, 4.6f, 1.0f, secCol);
            if (sideColor(own.Owner) is { } ownCol)
                DrawRing(v, 5.6f, 1.4f, ownCol);
        }
        else
        {
            if (own.Secondary is { } sec2 && sideColor(sec2) is { } secCol2)
                DrawRing(v, 8.2f, 1.3f, secCol2);
            if (sideColor(own.Owner) is { } ownCol2)
                DrawRing(v, 10.6f, 2.0f, ownCol2);
        }
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

    /// <summary>P374/P762b — the flashing cyan sector-outline (native's
    /// <c>sectorEmpty</c> surface tinted <c>Color::Cyan()</c>) drawn on the
    /// OFF-flash phase for a cluster in the commander's HIGHLIGHT_CLUSTER set
    /// (<c>GetHighlight() &amp;&amp; !m_bFlashFrame</c>, sectormap.cpp:1043-1046;
    /// the port draws primitive rings as its stand-in for the surface blit).</summary>
    private void DrawFlashingOutline(NumericsVec2 c)
    {
        var v = new Vector2(c.X, c.Y);
        DrawRing(v, 17f, 2.2f, HighlightFlash);
        DrawRing(v, 13f, 1.2f, HighlightFlash);
    }

    /// <summary>
    /// P374 — the flashing per-severity threat icon, drawn ONLY on the on-flash
    /// phase. Each tier gets a distinct colour + primitive shape standing in for
    /// native's distinct <c>*WarningBmp</c> blits (sectormap.cpp:1055-1082):
    /// station-threatened → red triangle, capital → magenta diamond, bomber →
    /// orange disc, miner/builder-threatened → yellow square, general combat →
    /// light-blue cross. <paramref name="scale"/> shrinks the marker for the
    /// compact mini-map.
    /// </summary>
    private void DrawThreatIcon(NumericsVec2 c, short warning, float scale = 1f)
    {
        var v = new Vector2(c.X, c.Y);
        float r = 9f * scale;
        // P752 — the tier cascade lives in the pure, unit-tested
        // StarmapThreatPresentation (native sectormap.cpp:1066-1082); the
        // renderer only maps tier -> shape/colour.
        string? stem = StarmapThreatPresentation.AssetStem(
            StarmapThreatPresentation.IconTier(warning));
        if (stem is not null && DrawSectorBitmap(stem, c, scale)) return;
        // Fallback only when the artwork is unavailable.
        switch (StarmapThreatPresentation.IconTier(warning))
        {
            case StarmapThreatPresentation.Tier.Station: DrawTriangle(v, r, ThreatRed); break;
            case StarmapThreatPresentation.Tier.Capital: DrawDiamond(v, r, ThreatCapital); break;
            case StarmapThreatPresentation.Tier.Bomber: DrawDisc(v, r, ThreatBomber); break;
            case StarmapThreatPresentation.Tier.Miner: DrawSquare(v, r, ThreatMiner); break;
            case StarmapThreatPresentation.Tier.Combat: DrawCross(v, r, ThreatCombat); break;
        }
    }

    /// <summary>
    /// P762 — blit one native sector-map bitmap centred on <paramref name="c"/>.
    /// Returns false when the texture pipeline is unavailable/missing so the
    /// caller can fall back to a primitive shape.
    /// </summary>
    private bool DrawSectorBitmap(string stem, NumericsVec2 c, float scale)
    {
        var tex = _icons?.Get(stem);
        if (tex is null) return false;
        _sb.Draw(tex, new Vector2(c.X - tex.Width * scale * 0.5f, c.Y - tex.Height * scale * 0.5f),
            null, Color.White, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        return true;
    }

    private void DrawTriangle(Vector2 c, float r, Color color)
    {
        var p0 = c + new Vector2(0f, -r);
        var p1 = c + new Vector2(-r * 0.87f, r * 0.55f);
        var p2 = c + new Vector2(r * 0.87f, r * 0.55f);
        DrawFilledPoly(new[] { p0, p1, p2 }, color);
    }

    private void DrawDiamond(Vector2 c, float r, Color color)
    {
        var p0 = c + new Vector2(0f, -r);
        var p1 = c + new Vector2(r, 0f);
        var p2 = c + new Vector2(0f, r);
        var p3 = c + new Vector2(-r, 0f);
        DrawFilledPoly(new[] { p0, p1, p2, p3 }, color);
    }

    private void DrawSquare(Vector2 c, float r, Color color) =>
        FillRect(c.X - r, c.Y - r, 2f * r, 2f * r, color);

    private void DrawDisc(Vector2 c, float r, Color color)
    {
        const int segments = 28;
        for (int i = 0; i < segments; i++)
        {
            float a0 = i * MathHelper.TwoPi / segments;
            float a1 = (i + 1) * MathHelper.TwoPi / segments;
            var p0 = c + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * r;
            var p1 = c + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * r;
            DrawLine(p0, p1, 2.4f * r / segments * 6f, color);
        }
    }

    private void DrawCross(Vector2 c, float r, Color color)
    {
        DrawLine(c + new Vector2(-r, 0f), c + new Vector2(r, 0f), 3f, color);
        DrawLine(c + new Vector2(0f, -r), c + new Vector2(0f, r), 3f, color);
    }

    /// <summary>P493 — a small solid-red "known enemy here" cross beside the
    /// cluster dot, for sectors whose last-known enemy ship positions the client
    /// received via cross-side <c>SHIP_STATUS(90)</c>. Offset diagonally
    /// (upper-left of the dot) so it never collides with the cluster name below
    /// or the current-sector highlight; <paramref name="count"/> (the number of
    /// known enemy ships in that sector) nudges the size up slightly. Drawn
    /// always-on (no flash phase) — an enemy whose last-known position we hold
    /// is persistent information, unlike the P374 transient threat blink.</summary>
    private void DrawEnemyMarker(NumericsVec2 c, int count, bool mini)
    {
        var v = new Vector2(c.X, c.Y) + (mini ? new Vector2(-7f, -7f) : new Vector2(-10f, -10f));
        float r = (mini ? 3.2f : 4.6f) + MathF.Min(count - 1, 3) * (mini ? 0.8f : 1.2f);
        DrawLine(v + new Vector2(-r, 0f), v + new Vector2(r, 0f), mini ? 2.2f : 3f, EnemyKnown);
        DrawLine(v + new Vector2(0f, -r), v + new Vector2(0f, r), mini ? 2.2f : 3f, EnemyKnown);
    }

    /// <summary>Fill a convex polygon with a solid colour by fan-triangulation
    /// from its centroid (convex triangles, safe under AlphaBlend).</summary>
    private void DrawFilledPoly(Vector2[] pts, Color color)
    {
        var cen = Vector2.Zero;
        foreach (var p in pts) cen += p;
        cen /= pts.Length;
        for (int i = 0; i < pts.Length; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Length];
            DrawTriangleFill(cen, a, b, color);
        }
    }

    private void DrawTriangleFill(Vector2 a, Vector2 b, Vector2 c_, Color color)
    {
        // Scanline-ish: draw concentric rings of points near the centroid so the
        // solid fills under the thin-line raster. Cheap and adequate for a small
        // starmap marker.
        float area = MathF.Abs((b.X - a.X) * (c_.Y - a.Y) - (c_.X - a.X) * (b.Y - a.Y));
        float rmax = MathF.Sqrt(area);
        int steps = Math.Max(2, (int)(rmax * 0.7f));
        for (int i = 1; i <= steps; i++)
        {
            float t = (float)i / steps;
            var p0 = Lerp(a, b, t);
            var p1 = Lerp(a, c_, t);
            DrawLine(p0, p1, 1.0f, color);
        }
    }

    private static Vector2 Lerp(Vector2 a, Vector2 b, float t) => a + (b - a) * t;


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
