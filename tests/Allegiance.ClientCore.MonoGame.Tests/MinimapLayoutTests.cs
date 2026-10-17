using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NumericsVector2 = System.Numerics.Vector2;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>P715 regression: bottom-row names must never enter the caption band.</summary>
public sealed class MinimapLayoutTests
{
    // A real MonoGame SpriteFont measurement path without a graphics device.
    // Deliberately generous glyph metrics also exercise fonts wider than HUD.
    private static readonly SpriteFont Font = CreateFont();

    private static SpriteFont CreateFont()
    {
        var chars = Enumerable.Range(32, 95).Select(c => (char)c).ToList();
        return new SpriteFont(null!, chars.Select(_ => new Rectangle(0, 0, 11, 23)).ToList(),
            chars.Select(_ => new Rectangle(0, 0, 11, 23)).ToList(), chars,
            23, 0f, chars.Select(_ => new Vector3(0, 11, 0)).ToList(), '?');
    }

    private static NumericsVector2 Measure(string text)
    {
        var size = Font.MeasureString(text);
        return new(size.X, size.Y);
    }

    private static MinimapLayout Build(int width, int height, string currentName = "Omega", bool currentAtBottom = false)
    {
        StarmapLayout.ClusterSite[] sites =
        [
            new(1, currentName, 0, currentAtBottom ? 100 : 0),
            new(2, "Bug Nebula", -25, 100),
            new(3, "Tellus", 25, 100),
            new(4, "Hidden non-neighbour", 0, -100),
        ];
        ClusterInfo.WarpEdge[] edges = [new(1, 2), new(3, 1)];
        return MinimapLayout.Build(sites, edges, 1, width, height, Measure);
    }

    [Theory]
    [InlineData(1280, 720, false)]
    [InlineData(1920, 1080, false)]
    [InlineData(2560, 1440, false)]
    [InlineData(1280, 720, true)]
    [InlineData(1920, 1080, true)]
    [InlineData(2560, 1440, true)]
    public void Sector_labels_stay_in_map_area_clear_of_footer(int width, int height, bool currentAtBottom)
    {
        var mini = Build(width, height, currentAtBottom: currentAtBottom);
        // Current sector first, then neighbours by id. In the default fixture all
        // three fit; when the current sector and both neighbours sit on one
        // cramped bottom row (currentAtBottom), the lowest-priority neighbour
        // may legitimately be dropped rather than overlap (P715b fallback), so
        // only the current label is mandatory.
        Assert.Contains(mini.SectorLabels, l => l.IsCurrent);
        Assert.All(mini.SectorLabels, l => Assert.Contains(l.Text, new[] { "Omega", "Bug Nebula", "Tellus" }));
        if (!currentAtBottom)
            Assert.Equal(new[] { "Omega", "Bug Nebula", "Tellus" }, mini.SectorLabels.Select(l => l.Text));
        Assert.Equal(mini.FooterArea.Y, mini.MapArea.Bottom);
        Assert.True(mini.MapArea.Height < mini.Panel.Height);
        foreach (var label in mini.SectorLabels)
        {
            AssertContains(mini.MapArea, label.Rectangle);
            Assert.True(label.Rectangle.Bottom <= mini.FooterArea.Y,
                $"{label.Text} overlaps footer: {label.Rectangle} / {mini.FooterArea}");
        }
        // P715b: no two labels may overlap, whatever slots they settled into.
        for (int i = 0; i < mini.SectorLabels.Count; i++)
        for (int j = i + 1; j < mini.SectorLabels.Count; j++)
            Assert.False(Intersects(mini.SectorLabels[i].Rectangle, mini.SectorLabels[j].Rectangle),
                $"{mini.SectorLabels[i].Text} overlaps {mini.SectorLabels[j].Text}");
        foreach (short id in mini.Map.Ids)
        {
            Assert.True(mini.Map.TryGetPoint(id, out var point));
            Assert.InRange(point.Y, mini.MapArea.Y + MinimapLayout.Border, mini.MapArea.Bottom - MinimapLayout.Border);
            Assert.Equal(id, StarmapHitTest.HitTest(mini.Map, point));
        }
        mini.Map.TryGetPoint(1, out var current);
        Assert.True(mini.SectorLabels.Single(l => l.IsCurrent).Rectangle.Bottom <= current.Y - 8f + 0.01f);
        // Each neighbour label must keep clear of its own dot/ring — it may sit
        // above or below (adaptive) but never cover the dot.
        foreach (short id in new short[] { 2, 3 })
        {
            string want = id == 2 ? "Bug Nebula" : "Tellus";
            if (!mini.SectorLabels.Any(l => l.Text == want)) continue; // dropped neighbour
            var nb = mini.SectorLabels.Single(l => l.Text == want);
            mini.Map.TryGetPoint(id, out var dot);
            Assert.False(nb.Rectangle.X < dot.X + 6f && nb.Rectangle.Right > dot.X - 6f
                && nb.Rectangle.Y < dot.Y + 6f && nb.Rectangle.Bottom > dot.Y - 6f,
                $"{nb.Text} covers its dot at {dot}");
        }
    }

    [Theory]
    [InlineData(1280, 720, "Omega")]
    [InlineData(1920, 1080, "Omega")]
    [InlineData(2560, 1440, "Omega")]
    [InlineData(1280, 720, "An exceptionally long current sector name")]
    [InlineData(1920, 1080, "An exceptionally long current sector name")]
    [InlineData(2560, 1440, "An exceptionally long current sector name")]
    public void Measured_footer_and_long_names_fit_inside_borders(int width, int height, string currentName)
    {
        var mini = Build(width, height, currentName);
        foreach (var label in mini.FooterLabels.Concat(mini.SectorLabels))
        {
            Assert.True(label.Rectangle.Width <= mini.Panel.Width - 2f * MinimapLayout.Border);
            Assert.Equal(Measure(label.Text).X, label.Rectangle.Width);
            Assert.Equal(Measure(label.Text).Y, label.Rectangle.Height);
            Assert.InRange(label.Rectangle.X, mini.Panel.X + MinimapLayout.Border,
                mini.Panel.Right - MinimapLayout.Border - label.Rectangle.Width);
        }
        foreach (var label in mini.FooterLabels) AssertContains(mini.FooterArea, label.Rectangle);
        Assert.Equal((short)-1, StarmapHitTest.HitTest(mini.Map,
            new NumericsVector2(mini.Panel.X + mini.Panel.Width / 2f, mini.FooterArea.Y + mini.FooterArea.Height / 2f)));
        Assert.Contains(mini.FooterLabels, l => l.Text.Contains("[TAB] EXPAND", StringComparison.Ordinal));
    }

    [Fact]
    public void Footer_uses_full_then_short_caption_then_two_lines_as_measured()
    {
        static MinimapLayout WithGlyphWidth(float glyphWidth) => MinimapLayout.Build(
            [new(1, "Omega", 0, 0)], [], 1, 1920, 1080,
            text => new(text.Length * glyphWidth, 23f));
        Assert.StartsWith("MINIMAP", Assert.Single(WithGlyphWidth(6).FooterLabels).Text);
        Assert.StartsWith("YOU:", Assert.Single(WithGlyphWidth(11).FooterLabels).Text);
        var split = WithGlyphWidth(16);
        Assert.Equal(new[] { "YOU: Omega", "[TAB] EXPAND" }, split.FooterLabels.Select(l => l.Text));
        Assert.True(split.FooterLabels[0].Rectangle.Bottom < split.FooterLabels[1].Rectangle.Y);
        Assert.True(split.FooterArea.Height > WithGlyphWidth(6).FooterArea.Height);
    }

    [Fact]
    public void Missing_current_sector_uses_question_mark_without_labelling_unrelated_sectors()
    {
        foreach (short? current in new short?[] { null, 99 })
        {
            var mini = MinimapLayout.Build([new(1, "Alpha", 0, 0)], [], current, 1280, 720, Measure);
            Assert.Empty(mini.SectorLabels);
            Assert.Contains(mini.FooterLabels, l => l.Text.Contains("YOU: ?", StringComparison.Ordinal));
        }
    }

    private static void AssertContains(MinimapLayout.Bounds outer, MinimapLayout.Bounds inner)
    {
        Assert.True(inner.X >= outer.X && inner.Y >= outer.Y && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom,
            $"{inner} must be inside {outer}");
    }

    private static bool Intersects(MinimapLayout.Bounds a, MinimapLayout.Bounds b) =>
        a.X < b.Right && a.Right > b.X && a.Y < b.Bottom && a.Bottom > b.Y;

    private static void AssertNoLabelOverlaps(MinimapLayout mini, string context)
    {
        for (int i = 0; i < mini.SectorLabels.Count; i++)
        for (int j = i + 1; j < mini.SectorLabels.Count; j++)
            Assert.False(Intersects(mini.SectorLabels[i].Rectangle, mini.SectorLabels[j].Rectangle),
                $"[{context}] {mini.SectorLabels[i].Text} overlaps {mini.SectorLabels[j].Text}: "
                + $"{mini.SectorLabels[i].Rectangle} / {mini.SectorLabels[j].Rectangle}");
    }

    // A neighbour DIRECTLY ABOVE the current sector — the P715b collision case:
    // old code drew the neighbour's name below its dot and the current sector's
    // name above its dot, so they overlapped when the neighbour sat above.
    private static MinimapLayout BuildStacked(int width, int height, bool above)
    {
        StarmapLayout.ClusterSite[] sites =
        [
            new(1, "Omega", 0, 0),
            new(2, "Alpha", 0, above ? -100 : 100),
            new(3, "Beta", 0, above ? 100 : -100),
        ];
        ClusterInfo.WarpEdge[] edges = [new(1, 2), new(1, 3)];
        return MinimapLayout.Build(sites, edges, 1, width, height, Measure);
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    public void Neighbour_directly_above_current_sector_never_overlaps(int width, int height)
    {
        var mini = BuildStacked(width, height, above: true);
        Assert.Contains(mini.SectorLabels, l => l.IsCurrent);
        AssertNoLabelOverlaps(mini, $"above@{width}x{height}");
        // The current sector's own label always survives; both neighbours that
        // fit must not enter the footer.
        foreach (var label in mini.SectorLabels)
            Assert.True(label.Rectangle.Bottom <= mini.FooterArea.Y, $"{label.Text} hits footer");
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    public void Neighbour_directly_below_current_sector_never_overlaps(int width, int height)
    {
        var mini = BuildStacked(width, height, above: false);
        Assert.Contains(mini.SectorLabels, l => l.IsCurrent);
        AssertNoLabelOverlaps(mini, $"below@{width}x{height}");
        foreach (var label in mini.SectorLabels)
            Assert.True(label.Rectangle.Bottom <= mini.FooterArea.Y, $"{label.Text} hits footer");
    }

    // Dense cluster: the m2xspiderw/Alderan-style 6-sector hexagon around the
    // current sector. All six are direct neighbours, so six labels plus the
    // current-sector label compete for space around tightly-packed dots.
    private static MinimapLayout BuildHexagon(int width, int height)
    {
        const float r = 100f;
        StarmapLayout.ClusterSite[] sites =
        [
            new(1, "Omega", 0, 0),
            new(2, "North", 0, -r),
            new(3, "Ne", r * 0.866f, -r * 0.5f),
            new(4, "Se", r * 0.866f, r * 0.5f),
            new(5, "South", 0, r),
            new(6, "Sw", -r * 0.866f, r * 0.5f),
            new(7, "Nw", -r * 0.866f, -r * 0.5f),
        ];
        ClusterInfo.WarpEdge[] edges =
        [
            new(1, 2), new(1, 3), new(1, 4), new(1, 5), new(1, 6), new(1, 7),
        ];
        return MinimapLayout.Build(sites, edges, 1, width, height, Measure);
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    public void Dense_hexagon_cluster_never_overlaps_labels(int width, int height)
    {
        var mini = BuildHexagon(width, height);
        Assert.Contains(mini.SectorLabels, l => l.IsCurrent);
        AssertNoLabelOverlaps(mini, $"hexagon@{width}x{height}");
        foreach (var label in mini.SectorLabels)
        {
            AssertContains(mini.MapArea, label.Rectangle);
            Assert.True(label.Rectangle.Bottom <= mini.FooterArea.Y, $"{label.Text} hits footer");
        }
    }
}
