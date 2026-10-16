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
        Assert.Equal(new[] { "Omega", "Bug Nebula", "Tellus" }, mini.SectorLabels.Select(l => l.Text));
        Assert.Equal(mini.FooterArea.Y, mini.MapArea.Bottom);
        Assert.True(mini.MapArea.Height < mini.Panel.Height);
        foreach (var label in mini.SectorLabels)
        {
            AssertContains(mini.MapArea, label.Rectangle);
            Assert.True(label.Rectangle.Bottom <= mini.FooterArea.Y,
                $"{label.Text} overlaps footer: {label.Rectangle} / {mini.FooterArea}");
        }
        foreach (short id in mini.Map.Ids)
        {
            Assert.True(mini.Map.TryGetPoint(id, out var point));
            Assert.InRange(point.Y, mini.MapArea.Y + MinimapLayout.Border, mini.MapArea.Bottom - MinimapLayout.Border);
            Assert.Equal(id, StarmapHitTest.HitTest(mini.Map, point));
        }
        mini.Map.TryGetPoint(1, out var current);
        Assert.True(mini.SectorLabels.Single(l => l.IsCurrent).Rectangle.Bottom <= current.Y - 8f + 0.01f);
        foreach (short id in new short[] { 2, 3 })
        {
            mini.Map.TryGetPoint(id, out var point);
            Assert.True(mini.SectorLabels[id - 1].Rectangle.Y >= point.Y + 8f - 0.01f);
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
}
