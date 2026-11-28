using System.IO;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P1107 — <c>visuals.compactHud</c>, the compact Modern HUD. Pure geometry and
/// model rules only (no GraphicsDevice), so every case runs in the Fast tier; the
/// rendered before/after evidence lives in
/// <c>tools/p1107-compact-hud-proof</c> and
/// <c>docs/reports/COMPACT_MODERN_HUD_P1107.md</c>.
/// <para><b>Text metrics.</b> <see cref="LineSpacing"/> and <see cref="Advance"/> are
/// measured from the shipped Modern HUD font by the proof harness
/// (<c>P1107 FONT</c> line: one line of the HUD's small text is 22 px at HUD scale 1,
/// and the widest observed glyph advance is 13.9 px, the "%" glyph). Using them here
/// keeps the census independent of any GPU while still bounding the real glyphs.</para>
/// </summary>
[Trait("Tier", "Fast")]
public sealed class P1107CompactHudTests
{
    /// <summary>Line height of the Modern HUD's small text at HUD scale 1.</summary>
    private const float LineSpacing = 22f;

    /// <summary>Conservative per-character advance of the Modern HUD's small text at
    /// HUD scale 1 (the widest glyph, "%", measures 13.9 px).</summary>
    private const float Advance = 13.9f;

    private static readonly (int Width, int Height)[] Resolutions = [(1280, 720), (1920, 1080), (5120, 2160)];
    private static readonly float[] HudSizes = [.5f, 1f, 2f];

    // ── item 5: the flag is one table row, default OFF, and the off path is the identity
    [Fact]
    public void Flag_off_returns_todays_geometry_exactly()
    {
        // The values the Modern renderer used before P1107 (ModernHudRenderer.cs):
        // 32 px ring radius at scale 1, 2 px stroke, 6 px glow, 8 px label gap, and
        // ModernHudScale.Cluster unchanged.
        Assert.Equal(32f, CompactHudLayout.RingRadius(1f, compact: false));
        Assert.Equal(64f, CompactHudLayout.RingRadius(2f, compact: false));
        Assert.Equal(2f, CompactHudLayout.StrokeWidth(1f, compact: false));
        Assert.Equal(6f, CompactHudLayout.GlowWidth(1f, compact: false));
        Assert.Equal(2f, CompactHudLayout.TrackWidth(1f, compact: false));
        Assert.Equal(8f, CompactHudLayout.LabelGap(1f, compact: false));
        var spread = ModernHudScale.Cluster(1f);
        Assert.Equal(spread, CompactHudLayout.Spread(1f, compact: false));
        Assert.Equal(1f, CompactHudLayout.SecondaryScale(1f, LineSpacing, compact: false));
        Assert.Equal(1.5f, CompactHudLayout.SecondaryScale(1.5f, LineSpacing, compact: false));
        Assert.Equal(1f, CompactHudLayout.PercentScale(1f, 60f, 16f, 24f, 4f, compact: false));

        // …and the compact values are the ones the brief asks for: ~75 % rings,
        // thinner, tighter to the label, one font step down and a narrower cluster.
        Assert.Equal(24f, CompactHudLayout.RingRadius(1f, compact: true));   // 48 px outer diameter
        Assert.True(CompactHudLayout.RingRadius(1f, true) / CompactHudLayout.RingRadius(1f, false) is >= .70f and <= .75f);
        Assert.True(CompactHudLayout.StrokeWidth(1f, true) < CompactHudLayout.StrokeWidth(1f, false));
        Assert.True(CompactHudLayout.LabelGap(1f, true) < CompactHudLayout.LabelGap(1f, false));
        Assert.True(CompactHudLayout.Spread(1f, true) < spread);
        // The layout reservation covers every painted arc pixel (radius + glow/2).
        Assert.True(CompactHudLayout.RingExtent(1f, true) >=
            CompactHudLayout.RingRadius(1f, true) + CompactHudLayout.GlowWidth(1f, true) / 2f);
        Assert.Equal(35f, CompactHudLayout.RingExtent(1f, compact: false));
        Assert.True(CompactHudLayout.SecondaryScale(1f, LineSpacing, true) < 1f);
    }

    [Fact]
    public void Flag_row_is_default_off_and_persists()
    {
        var definition = VisualFlags.Find(VisualFlags.CompactHudKey);
        Assert.NotNull(definition);
        Assert.Equal(VisualFlagKind.Toggle, definition!.Kind);
        Assert.True(definition.DefaultOn);   // default ON since 2026-11-28 (Domingo)
        Assert.False(string.IsNullOrWhiteSpace(definition.Label));
        Assert.False(string.IsNullOrWhiteSpace(definition.Description));
        // The Visuals options page is table-driven: the row must be in the table.
        Assert.Contains(VisualFlags.Definitions, d => d.Key == VisualFlags.CompactHudKey);

        Assert.True(new VisualOptions().CompactHud);
        Assert.False(CompactHudLayout.Enabled(null));   // no settings object: pre-P1107 layout
        var options = new VisualOptions();
        options.SetToggle(VisualFlags.CompactHudKey, false);
        Assert.False(options.CompactHud);
        Assert.False(CompactHudLayout.Enabled(options));
        options.SetToggle(VisualFlags.CompactHudKey, true);
        Assert.True(options.CompactHud);
        Assert.True(CompactHudLayout.Enabled(options));

        // Persisted as one boolean in the profile's `visuals` group and read back.
        var json = System.Text.Json.JsonSerializer.Serialize(options);
        Assert.Contains("\"compactHud\":true", json);
        var restored = System.Text.Json.JsonSerializer.Deserialize<VisualOptions>(json)!;
        Assert.True(restored.CompactHud);
        // A legacy profile without the key resolves to the table default (on).
        var legacy = System.Text.Json.JsonSerializer.Deserialize<VisualOptions>("{\"radialVisibility\":true}")!;
        Assert.True(legacy.CompactHud);
        Assert.Contains("compactHud=on", new VisualOptions().Describe());
    }

    // ── item 2: one font step smaller, with a stated minimum rendered height
    [Fact]
    public void Secondary_text_is_one_font_step_and_honours_the_minimum_height()
    {
        // One step: 11/14 of the authored 14 px font (= the native smVerdana →
        // medVerdana 1/1.25 relationship).
        Assert.Equal(11f / 14f, CompactHudLayout.SecondaryScale(1f, LineSpacing, true), 5);
        Assert.True(CompactHudLayout.TextHeight(LineSpacing, CompactHudLayout.SecondaryScale(1f, LineSpacing, true)) >=
            CompactHudLayout.MinTextHeightPx, "secondary text at HUD size 100 % must clear the 11 px floor");
        Assert.True(CompactHudLayout.MeetsMinimumTextHeight(LineSpacing,
            CompactHudLayout.SecondaryScale(1f, LineSpacing, true)));

        // The floor never drops a line below 11 px where the HUD itself is that big,
        // and never inflates the secondary text above the primary size on a small HUD.
        foreach (float scale in new[] { .5f, .65f, 1f, 1.5f, 2f, 3f, 4f })
        {
            float secondary = CompactHudLayout.SecondaryScale(scale, LineSpacing, true);
            Assert.True(secondary <= scale, $"secondary {secondary} must never exceed the primary {scale}");
            float height = CompactHudLayout.TextHeight(LineSpacing, secondary);
            if (CompactHudLayout.TextHeight(LineSpacing, scale) >= CompactHudLayout.MinTextHeightPx)
                Assert.True(height >= CompactHudLayout.MinTextHeightPx,
                    $"scale {scale}: secondary height {height} fell under the stated {CompactHudLayout.MinTextHeightPx} px floor");
        }
        // HUD size 50 % is the documented boundary: the compact step yields (the line
        // would be 8.6 px) and secondary text keeps the primary size instead of
        // shrinking below the floor.
        Assert.Equal(.5f, CompactHudLayout.SecondaryScale(.5f, LineSpacing, true));
        Assert.Equal(CompactHudLayout.MinTextHeightPx,
            CompactHudLayout.TextHeight(LineSpacing, CompactHudLayout.SecondaryScale(.5f, LineSpacing, true)), 3);
    }

    [Fact]
    public void Gauge_percentage_fits_inside_the_compact_ring()
    {
        // P1107 review fix — the fit is the glyph INK box against the ring's inner edge
        // on the DIAGONAL (the ring is a circle): the review screenshots showed the
        // text box crossing the stroke even when the width alone fitted.
        float radius = CompactHudLayout.RingRadius(1f, true);
        float glow = CompactHudLayout.GlowWidth(1f, true);
        float inner = radius - glow / 2f - CompactHudLayout.PercentPadding;
        // A width that fits on the centre line but whose corners do not: 2·inner − 2 wide
        // and 14 px tall must shrink (the old width-only rule kept it at full size).
        float w = 2f * inner - 2f, h = 14f;
        Assert.False(CompactHudLayout.InkFitsRing(w, h, 1f, radius, glow));
        float fitted = CompactHudLayout.PercentScale(1f, w, h, radius, glow, true);
        Assert.True(fitted < 1f);
        Assert.True(CompactHudLayout.InkFitsRing(w, h, fitted, radius, glow));
        // The widest reading ("100%") fits after the reduction, at every HUD size.
        foreach (float scale in new[] { .5f, 1f, 1.5f, 2f })
        {
            float r = CompactHudLayout.RingRadius(scale, true), g = CompactHudLayout.GlowWidth(scale, true);
            float s = CompactHudLayout.PercentScale(scale, 46f, 15f, r, g, true);
            Assert.True(s <= scale);
            Assert.True(CompactHudLayout.InkFitsRing(46f, 15f, s, r, g) || s == .6f * scale,
                $"scale {scale}: 100% ink at {s} crosses the ring");
        }
        // A short reading whose ink corners already clear keeps today's size.
        Assert.Equal(1f, CompactHudLayout.PercentScale(1f, 30f, 12f, radius, glow, true));
        // Nothing ever grows.
        Assert.True(CompactHudLayout.PercentScale(1f, 10f, 5f, radius, glow, true) <= 1f);
    }

    // ── item 3 + 6: the compact cluster clears the centre and the HUD furniture
    [Fact]
    public void Compact_rings_and_labels_clear_the_centre_and_the_hud_furniture()
    {
        foreach (var (width, height) in Resolutions)
        foreach (float size in HudSizes)
        {
            var census = Census(width, height, size);
            string where = $"{width}x{height} HUD {size:0.##}";
            var centre = new Rectangle((int)(width / 2f - 8), (int)(height / 2f - 8), 16, 16);
            // Every own ring clears the crosshair, the lead indicator, the speed
            // readout and the kill-bonus badge.
            foreach (var ring in census.Rings)
            {
                Assert.False(ring.Bounds.Intersects(centre), $"{where}: ring {ring.Id} over the crosshair");
                foreach (var (id, rect) in census.Fixed)
                    Assert.False(ring.Bounds.Intersects(rect), $"{where}: ring {ring.Id} {ring.Bounds} hits {id} {rect}");
                foreach (var other in census.Rings.Where(r => r.Id != ring.Id).Concat(census.Labels))
                    Assert.False(ring.Bounds.Intersects(other.Bounds), $"{where}: ring {ring.Id} {ring.Bounds} hits {other.Id} {other.Bounds}");
                Assert.True(new Rectangle(0, 0, width, height).Contains(ring.Bounds), $"{where}: ring {ring.Id} off screen");
            }
            // Labels never touch another gauge's ring or another label.
            foreach (var label in census.Labels)
            {
                Assert.False(label.Bounds.Intersects(centre), $"{where}: label {label.Id} over the crosshair");
                foreach (var (id, rect) in census.Fixed)
                    Assert.False(label.Bounds.Intersects(rect), $"{where}: label {label.Id} {label.Bounds} hits {id} {rect}");
                foreach (var other in census.Labels.Where(l => l.Id != label.Id))
                    Assert.False(label.Bounds.Intersects(other.Bounds), $"{where}: label {label.Id} hits {other.Id}");
                foreach (var ring in census.Rings)
                    Assert.False(label.Bounds.Intersects(ring.Bounds), $"{where}: label {label.Id} hits ring {ring.Id}");
            }
            // The own status column (SIG / kill-bonus / autopilot / …) clears the
            // rings and the fixed furniture too.
            foreach (var status in census.Status)
            {
                Assert.False(status.Bounds.Intersects(centre), $"{where}: status {status.Id} over the crosshair");
                foreach (var (id, rect) in census.Fixed)
                {
                    // The kill-bonus badge IS one of the status elements (it is listed
                    // separately so the rings can be tested against it).
                    if (id == "kill-bonus badge" && status.Id == "status:Kill bonus") continue;
                    Assert.False(status.Bounds.Intersects(rect), $"{where}: status {status.Id} hits {id}");
                }
                foreach (var ring in census.Rings)
                    Assert.False(status.Bounds.Intersects(ring.Bounds), $"{where}: status {status.Id} hits ring {ring.Id}");
            }
            // Targets: whatever the renderer picks (native pane content, or its own
            // one-row crowded layout) stays clear of the rings, the labels and the
            // HUD panels.
            foreach (var target in census.Target)
            {
                foreach (var ring in census.Rings)
                    Assert.False(target.Bounds.Intersects(ring.Bounds), $"{where}: target {target.Id} hits ring {ring.Id}");
                foreach (var label in census.Labels)
                    Assert.False(target.Bounds.Intersects(label.Bounds), $"{where}: target {target.Id} hits label {label.Id}");
                foreach (var (id, rect) in census.Panels)
                    Assert.False(target.Bounds.Intersects(rect), $"{where}: target {target.Id} hits {id} {rect}");
            }
        }
    }

    [Fact]
    public void Compact_geometry_scales_with_the_hud_size_slider_both_ways()
    {
        // 50 % and 200 % must both produce a legal layout: the ring diameter tracks
        // the slider, the label sits exactly under the ring, and the cluster scales
        // monotonically, so every element grows/shrinks together.
        float previousRadius = 0f, previousSpread = 0f;
        foreach (float size in new[] { .5f, 1f, 2f })
        {
            float scale = ModernHudScale.Core(1f, size, 1080);
            float radius = CompactHudLayout.RingRadius(scale, true);
            float spread = CompactHudLayout.Spread(scale, true);
            Assert.True(MathF.Abs(24f * scale - radius) < .001f, $"radius {radius} does not track HUD scale {scale}");
            Assert.True(radius > previousRadius);
            Assert.True(spread > previousSpread);
            previousRadius = radius;
            previousSpread = spread;
            // The label anchors exactly one gap below the ring's outer edge.
            var census = Census(1920, 1080, size);
            foreach (var ring in census.Rings)
            {
                var label = census.Labels.Single(l => l.Id == "label:" + ring.Id);
                Assert.True(MathF.Abs(ring.Bounds.Bottom + CompactHudLayout.LabelGap(scale, true) - label.Bounds.Top) <= 1f,
                    $"HUD size {size}: label {label.Id} top {label.Bounds.Top} is not one gap under the ring bottom {ring.Bounds.Bottom}");
                Assert.True(Math.Abs(ring.Bounds.Center.X - label.Bounds.Center.X) <= 1,
                    $"HUD size {size}: label {label.Id} is not centred under its ring");
            }
        }
        // The compact spread stays PROPORTIONAL to the ring at every HUD size (the
        // shipped 2.1 band would stop following the rings at 200 % and let them
        // collide), and never falls under the classic small-window floor.
        Assert.Equal(2f * ModernHudScale.ClusterAtReference * CompactHudLayout.ClusterFraction,
            CompactHudLayout.Spread(2f, true), 4);
        Assert.True(CompactHudLayout.Spread(4f, true) > CompactHudLayout.Spread(2f, true));
        Assert.Equal(1.046f, CompactHudLayout.Spread(.1f, true), 4);
    }

    // ── item 4: the target pane's bars
    [Fact]
    public void Aleph_and_out_of_sector_targets_draw_empty_bars()
    {
        // An aleph (warp) has no hull/shield reading at all (TargetPaneModel's Warp
        // branch leaves both null), and native's 0 % is what the pane must show.
        var world = World();
        world.Apply(new ExportWarp
        {
            Warp = new DataWarpIGC(40, 41, 100, "", "", "Aleph", [0, 0, 0], [0, 0, 1], [0, 0, 0], 1, 1),
        });
        var aleph = TargetPaneModel.Build(world, IgcObjectType.Warp, 40);
        Assert.True(aleph.Present);
        Assert.Null(aleph.HullFraction);
        Assert.Null(aleph.ShieldFraction);
        Assert.Equal(0f, CompactHudLayout.BarFill(aleph.HullFraction));
        Assert.Equal(0f, CompactHudLayout.BarFill(aleph.ShieldFraction));

        // An out-of-sector ship: native GetPercentHitPoints returns 0 for the cluster
        // test (consoledata.cpp:202-212), so the same empty bar.
        var elsewhere = World();
        AddShip(elsewhere, 400, OtherSector);
        // Left the sector: native CanSee is false, so GetPercentHitPoints (hull) and the
        // scanner-gated shield reading both fall back to the empty bar.
        elsewhere.Apply(ShipDelete.ForShipLeftSector(400));
        var hidden = TargetPaneModel.Build(elsewhere, IgcObjectType.Ship, 400);

        Assert.Equal(0f, CompactHudLayout.BarFill(hidden.HullFraction));
        Assert.Equal(0f, CompactHudLayout.BarFill(hidden.ShieldFraction));

        // A visible in-sector ship keeps its real fill (the bars are not always empty).
        var near = World();
        AddShip(near, 401, Sector);
        var visible = TargetPaneModel.Build(near, IgcObjectType.Ship, 401);
        Assert.NotNull(visible.HullFraction);
        Assert.Equal(1f, CompactHudLayout.BarFill(visible.HullFraction));
    }

    [Fact]
    public void Target_bars_are_slim_horizontal_and_clear_of_the_model_preview()
    {
        foreach (var (width, height) in Resolutions)
        foreach (float size in HudSizes)
        {
            float scale = ModernHudScale.Core(1f, size, height);
            float paneScale = TargetPaneLayout.Scale(width, height, scale);
            float x0 = TargetPaneLayout.OriginX(width, height, scale);
            float y0 = TargetPaneLayout.OriginY(width, height, scale);
            float labelScale = CompactHudLayout.SecondaryScale(scale, LineSpacing, true);
            float labelWidth = 6 * Advance * labelScale;
            float labelHeight = LineSpacing * labelScale;
            var hull = CompactHudLayout.TargetBar(false, x0, y0, paneScale, labelWidth, labelHeight);
            var shields = CompactHudLayout.TargetBar(true, x0, y0, paneScale, labelWidth, labelHeight);
            string where = $"{width}x{height} HUD {size:0.##}";
            foreach (var (id, bar) in new[] { ("hull", hull.Bar), ("shield", shields.Bar) })
            {
                Assert.True(bar.Width > bar.Height * 8, $"{where}: {id} bar {bar} is not slim and horizontal");
                Assert.True(bar.Height <= CompactHudLayout.BarHeight * paneScale + 1, $"{where}: {id} bar too tall");
            }
            // The bars flank the native model preview (HUD-21 PaneRect), like native's
            // gauge slots did, and stay inside the pane's native horizontal extent.
            var preview = TargetModelPreview.PaneRect(x0, y0, paneScale);
            Assert.False(hull.Bar.Intersects(preview), $"{where}: hull bar {hull.Bar} over the preview {preview}");
            Assert.False(shields.Bar.Intersects(preview), $"{where}: shield bar {shields.Bar} over the preview {preview}");
            float left = x0 + TargetPaneLayout.LocalLeft * paneScale;
            float right = x0 + TargetPaneLayout.LocalRight * paneScale;
            Assert.InRange(hull.Bar.Left, left, right);
            Assert.InRange(shields.Bar.Right, left, right);
            Assert.True(hull.Bar.Right < shields.Bar.Left, $"{where}: the two bars must not overlap");
        }
    }

    // ── item 5: Classic is untouched by the flag, structurally
    [Fact]
    public void Classic_hud_path_never_reads_the_compact_flag()
    {
        string source = File.ReadAllText(RepoPath("src/Allegiance.ClientCore.Game/Rendering/HudRenderer.cs"));
        Assert.DoesNotContain("CompactHud", source, StringComparison.Ordinal);
        Assert.DoesNotContain("compactHud", source, StringComparison.Ordinal);
        Assert.DoesNotContain("VisualOptions", source, StringComparison.Ordinal);
        // Only the Modern renderer (and its target-pane partial) reads the flag.
        foreach (string file in new[]
                 {
                     "src/Allegiance.ClientCore.Game/Rendering/ModernHudRenderer.cs",
                     "src/Allegiance.ClientCore.Game/Rendering/ModernHudRenderer.TargetPane.cs",
                 })
            Assert.Contains("CompactHud", File.ReadAllText(RepoPath(file)), StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────── census
    private sealed record Element(string Id, Rectangle Bounds);

    private sealed record CensusResult(List<Element> Rings, List<Element> Labels, List<Element> Status,
        List<Element> Target, List<(string Id, Rectangle Bounds)> Fixed, List<(string Id, Rectangle Bounds)> Panels);

    /// <summary>
    /// The composed Modern HUD at one (viewport, HUD size) with <c>visuals.compactHud</c>
    /// on: every own ring/label/status box from the production gauge model and native
    /// anchors, the fixed furniture the brief names (crosshair, lead indicator, speed
    /// readout, kill-bonus badge, minimap, chat, systems panel, centre messages) and the
    /// target pane's content — laid out exactly as the renderer decides it (native pane
    /// anchors, or its one-row crowded layout when that would touch an own element).
    /// </summary>
    private static CensusResult Census(int width, int height, float size)
    {
        float scale = ModernHudScale.Core(1f, size, height);
        float spread = CompactHudLayout.Spread(scale, true);
        float secondary = CompactHudLayout.SecondaryScale(scale, LineSpacing, true);
        float radius = CompactHudLayout.RingRadius(scale, true);
        var model = Model();
        var centre = new Vector2(width / 2f, height / 2f);

        var rings = new List<Element>();
        var labelDesired = new List<(string Id, string Text, Vector2 Desired, Vector2 Measured)>();
        foreach (var gauge in model.Gauges.Where(g => g.Visible))
        {
            var c = centre + new Vector2(gauge.NativeOffset.X, gauge.NativeOffset.Y) * spread;
            int diameter = (int)MathF.Ceiling(2 * radius);
            rings.Add(new Element(gauge.Kind.ToString(),
                new Rectangle((int)(c.X - radius), (int)(c.Y - radius), diameter, diameter)));
            float w = gauge.Label.Length * Advance * secondary;
            float h = LineSpacing * secondary;
            labelDesired.Add(("label:" + gauge.Kind, gauge.Label,
                new Vector2(c.X - w / 2, c.Y + radius + CompactHudLayout.LabelGap(scale, true)),
                new Vector2(w, h)));
        }

        // Own status text (SIG, kill bonus, …) at the tightened native anchors.
        var statusDesired = new List<(string Id, string Text, Vector2 Desired, Vector2 Measured)>();
        foreach (var (id, text) in new[]
                 {
                     ("Kill bonus", HudStringCache.KillBonusBadge(15f)),
                     ("Signature percentage", HudStringCache.Signature(42f)),
                     // NB: AUTOPILOT/VECTOR LOCK/FLAG/EYE are drawn at native ICON
                     // anchors (dialog.mdl:2607-2621) as long words; the port's text
                     // box there already overlaps the gauge reservation before P1107
                     // (unchanged, out of scope — see the report).

                 })
        {
            var p = OwnStatusLayout.ScreenPos(id, width, height, modernScale: scale, spreadOverride: spread);
            statusDesired.Add((id, text, p, new Vector2(text.Length * Advance * secondary, LineSpacing * secondary)));
        }

        // Fixed furniture.
        var fixedElements = new List<(string, Rectangle)>
        {
            // Own speed numeral + its "mps" caption, bottom-centre (HudRenderer).
            ("speed readout", new Rectangle((int)(width / 2f - 60 * scale), (int)(height - 108 * scale),
                (int)(120 * scale), (int)(60 * scale))),
            // The leader/command line under the native chat pane.
            ("leader command line", new Rectangle((int)(width / 2f - 120 * scale), (int)(height - 140 * scale),
                (int)(240 * scale), (int)MathF.Ceiling(LineSpacing * scale))),
            // An ordinary tip line (HudConsoleFormat's bottom-centre band).
            ("centre message", new Rectangle((int)(width / 2f - 160 * scale), (int)(height - 14 * scale - LineSpacing * scale),
                (int)(320 * scale), (int)MathF.Ceiling(LineSpacing * scale))),
        };
        var panels = new List<(string, Rectangle)>();
        var (mx, my, mw, mh) = MinimapGeometry.CornerPanel(width, height);
        panels.Add(("minimap", new Rectangle((int)mx, (int)my, (int)mw, (int)mh)));
        int paneLines = ChatPaneLayout.ConfiguredLines(ChatPaneLayout.DefaultLines);
        panels.Add(("chat pane", new Rectangle(ChatPaneLayout.Left(width), ChatPaneLayout.Top(paneLines),
            ChatPaneLayout.Width, (int)MathF.Ceiling(ChatPaneLayout.NativeFontHeight * (paneLines + 1)))));
        float line = 17f * scale + 5;
        float panelW = Math.Min(width * .34f, 380 * Math.Max(1, scale));
        panels.Add(("systems panel", new Rectangle((int)(width - panelW - 18), (int)Math.Max(150, line * 4),
            (int)panelW, (int)(line * 5))));

        // Text placement: the production HudTextLayout, with the same zones reserved
        // first (rings, HUD panels, the fixed centre furniture). This is exactly what
        // DrawMeasuredText does at runtime, so a label/status that cannot stay at its
        // native anchor is measured where it is really drawn.
        var layout = new HudTextLayout();
        layout.Reset(width, height);
        foreach (var (_, rect) in panels) layout.Reserve(rect);
        foreach (var ring in rings) layout.Reserve(ring.Bounds);
        foreach (var (_, rect) in fixedElements) layout.Reserve(rect);
        var labels = new List<Element>();
        foreach (var (id, text, desired, measured) in labelDesired)
            labels.Add(new Element(id, layout.Place(text, measured, desired, instrument: true)));
        var status = new List<Element>();
        foreach (var (id, text, desired, measured) in statusDesired)
            status.Add(new Element("status:" + id, layout.Place(text, measured, desired, instrument: true)));
        // The kill-bonus badge is one of the status elements; expose it for the rings.
        fixedElements.Add(("kill-bonus badge", status[0].Bounds));

        // Target pane content: name (name + type suffix), the two bars with their
        // ARMOR/SHIELD labels, RANGE/SPEED/TTI and the kill-bonus number.
        float s = TargetPaneLayout.Scale(width, height, scale);
        float x0 = TargetPaneLayout.OriginX(width, height, scale);
        float y0 = TargetPaneLayout.OriginY(width, height, scale);
        float nameWidth = 22 * Advance * scale;      // "Peregrine: Heavy Interceptor", primary size
        var primary = new List<Element>
        {
            new("target:name", new Rectangle((int)(x0 + 320 * s - nameWidth / 2), (int)(y0 - 125 * s - LineSpacing * scale),
                (int)MathF.Ceiling(nameWidth), (int)MathF.Ceiling(LineSpacing * scale))),
            new("target:hull-bar", CompactHudLayout.TargetBar(false, x0, y0, s, 5 * Advance * secondary, LineSpacing * secondary).Bar),
            new("target:shield-bar", CompactHudLayout.TargetBar(true, x0, y0, s, 6 * Advance * secondary, LineSpacing * secondary).Bar),
            new("target:range", new Rectangle((int)(x0 + 400 * s), (int)(y0 - 33 * s - LineSpacing * secondary),
                (int)(12 * Advance * secondary), (int)MathF.Ceiling(LineSpacing * secondary))),
            new("target:speed", new Rectangle((int)(x0 + 242 * s - 10 * Advance * secondary), (int)(y0 - 33 * s - LineSpacing * secondary),
                (int)(10 * Advance * secondary), (int)MathF.Ceiling(LineSpacing * secondary))),
            new("target:tti", new Rectangle((int)(x0 + 242 * s - 10 * Advance * secondary), (int)(y0 - 10 * s - LineSpacing * secondary),
                (int)(10 * Advance * secondary), (int)MathF.Ceiling(LineSpacing * secondary))),
            new("target:kill-bonus", new Rectangle((int)(x0 + 320 * s - 4 * Advance * scale / 2), (int)(y0 - 110 * s),
                (int)(4 * Advance * scale), (int)MathF.Ceiling(LineSpacing * scale))),
        };
        // The renderer falls back to its one-row crowded layout exactly when the native
        // pane content would touch an own element (ModernHudRenderer.TargetIntersectsOwn),
        // so model that same decision and then check whichever layout is drawn.
        var own = rings.Concat(labels).Concat(status).ToList();
        bool crowded = primary.Any(t => own.Any(o => t.Bounds.Intersects(o.Bounds)));
        var target = crowded ? CrowdedTarget(width, height, scale) : primary;
        return new CensusResult(rings, labels, status, target, fixedElements, panels);
    }

    /// <summary>The renderer's own crowded one-row target layout (CompactTarget), whose
    /// three bottom-aligned rows sit above the bottom edge.</summary>
    private static List<Element> CrowdedTarget(int width, int height, float scale)
    {
        float line = LineSpacing * scale;
        float barSlot = 48 * scale;
        float bars = height - 5 - line - 8 * scale - barSlot / 2;
        return
        [
            new("target:name", new Rectangle((int)(width / 2f - 150 * scale), (int)(bars - barSlot / 2 - 8 * scale - line),
                (int)(300 * scale), (int)MathF.Ceiling(line))),
            new("target:detail", new Rectangle((int)(width / 2f - 170 * scale), (int)(height - 5 - line),
                (int)(340 * scale), (int)MathF.Ceiling(line))),
            new("target:hull-bar", new Rectangle((int)(width / 2f - 220 * scale), (int)(bars - 2 * scale),
                (int)barSlot, (int)MathF.Max(1, MathF.Round(CompactHudLayout.BarHeight * scale)))),
            new("target:shield-bar", new Rectangle((int)(width / 2f + 92 * scale), (int)(bars - 2 * scale),
                (int)barSlot, (int)MathF.Max(1, MathF.Round(CompactHudLayout.BarHeight * scale)))),
        ];
    }

    private static Rectangle Centre(Vector2 centre, float width, float height)
        => new((int)(centre.X - width / 2), (int)(centre.Y - height / 2),
            (int)MathF.Ceiling(width), (int)MathF.Ceiling(height));

    private static HudModel Model() => HudModel.Create(new HudFrame(
        HullFraction: .78f, ShieldFraction: .52f, AmmoFraction: .64f, FuelFraction: .83f,
        EnergyFraction: 1f, Power: 1f, Speed: 260f, Throttle: .6f,
        AfterburnerActive: false, Firing: false, GroupFire: true, SelectedMount: 0,
        Flashing: false, WarpFlash: 0f, Mining: false, Cloaked: false,
        KillBonusPercent: 15f, RollDegrees: 0f,
        WeaponSystems: new WeaponStatusRow[] { new(0, "PW Gat Gun 1", true, 16520, 32767, WeaponStatus.Ready) },
        IsAdriftLifepod: false, OxygenSecondsRemaining: 0), new HudContext
    {
        TargetPaneSlide = 1f,
        TargetPane = new HudTargetPane(true, "Peregrine", "Heavy Interceptor", "Red",
            new HudColor(.85f, .30f, .25f, .5f), .78f, .52f, 1250f, 260f, null, 21f, true, true,
            TimeToIntercept: "TTI: infinite"),
        Chat = new HudChat(["line"], 0, "", false, 1, "[TEAM] "),
    });

    // ── world fixtures for the target-pane model
    private const short Sector = 10;
    private const short OtherSector = 11;

    private static ServerShipUpdate Pose(short id) => ServerShipUpdate.FromValues(id, 0,
        new ControlData(0f, 0f, 0f, 1f), 1u, new Allegiance.Core.Collision.Vector3(0f, 0f, 0f),
        Orientation.Identity, new Allegiance.Core.Collision.Vector3(0f, 0f, 0f),
        [0f, 0f, 0f], 1f, 1f, 1f, 1f, 1f, 0f, 1f);

    private static ClientWorld World()
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster { SectorId = Sector, Ship = Pose(1) });
        foreach (var (id, name) in new[] { (Sector, "Alystra"), (OtherSector, "Ganymede") })
            world.Apply(new ExportCluster
            {
                Cluster = new DataClusterIGC(0, [0f, 1f, 0f], 0, 0f, 0f, id, 0, 0, name, "", "", 0, 0, 0, true, false),
            });
        return world;
    }

    private static void AddShip(ClientWorld world, short id, short sector)
    {
        world.Apply(new SingleShipUpdate { OidTarget = id, Ship = Pose(id) });
        world.Apply(new ShipStatusMessage { ShipId = id, SectorId = sector, StationId = -1 });
        world.Apply(new ExportShip { ShipId = id, SideId = 1, CharacterName = $"ship {id}", HullId = 501 });
    }

    private static string RepoPath(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AllegianceCore.slnx")))
            directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("repo"), relative);
    }

    [Fact]
    public void Compact_speed_strings_are_cached_and_match_the_default_formatter()
    {
        // P1107 review fix — the compact speed block runs every frame; its strings come
        // from HudStringCache (the same instance on repeat) and round exactly like
        // HudFormat.FormatSpeed, so both HUD paths show the same number.
        foreach (float v in new[] { 0f, 0.5f, 1.5f, 2.5f, 259.5f, 260.4f, 1234.6f, -3f })
            Assert.Equal(HudFormat.FormatSpeed(v), HudStringCache.SpeedNumber(v));
        Assert.Same(HudStringCache.SpeedNumber(260.2f), HudStringCache.SpeedNumber(259.8f));
        Assert.Same(HudStringCache.SpeedBlock(260.2f, "mps"), HudStringCache.SpeedBlock(260.1f, "mps"));
        Assert.Equal("260\nmps", HudStringCache.SpeedBlock(260f, "mps"));
    }
}
