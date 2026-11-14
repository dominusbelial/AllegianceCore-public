using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

public class LoadoutV2LayoutTests
{
    private static readonly (int Width, int Height)[] Sizes =
    [
        (1280, 720), (1600, 900), (1920, 1080), (2560, 1440),
        (3840, 2160), (3440, 1440), (1280, 800),
    ];

    public static IEnumerable<object[]> Viewports =>
        from size in Sizes
        from style in new[] { HudStyle.Classic, HudStyle.Modern }
        select new object[] { size.Width, size.Height, style };

    [Theory]
    [MemberData(nameof(Viewports))]
    public void Geometry_properties_hold_across_scales_counts_and_scrolls(int width, int height, HudStyle style)
    {
        // Both styles consume precisely this layout; palette never affects hit geometry.
        var random = new Random(1013);
        foreach (float size in new[] { .5f, .75f, 1f, 1.5f, 2f })
        foreach (float density in new[] { 1f, 2f })
        for (int trial = 0; trial < 12; trial++)
        {
            var settings = new HudSettings { Size = size, Style = style };
            float scale = ModernHudScale.Core(density, settings.Size);
            int hulls = random.Next(0, 150), slots = random.Next(1, 50), offers = random.Next(0, 200);
            int open = trial == 0 ? -1 : random.Next(slots);
            var layout = LoadoutV2Layout.Compute(width, height, scale, hulls, slots, offers, open,
                random.Next(-10, 200), random.Next(-10, 200), random.Next(-10, 200));
            CheckProperties(layout);
        }
    }

    private static void CheckProperties(LoadoutV2Layout layout)
    {
        foreach (var region in layout.Regions) Inside(region, layout.Viewport);
        NoOverlaps(layout.Regions);
        NoOverlaps(layout.Elements.Select(e => e.Bounds).ToArray());
        Inside(layout.HullViewport, layout.HullList);
        Inside(layout.SlotViewport, layout.SlotList);
        Assert.Equal(84f, layout.Footer.Height);
        foreach (var element in layout.Elements)
        {
            Inside(element.Bounds, layout.Viewport);
            Inside(element.TextBounds, element.Bounds);
            Assert.True(element.Bounds.Bottom <= layout.Footer.Y + .001f);
            Assert.Equal(element, layout.HitTest(element.Bounds.CenterX, element.Bounds.CenterY));
            Assert.Equal(element, DockedTouchZones.LoadoutV2HitTest(layout,
                element.Bounds.CenterX, element.Bounds.CenterY));
            if (element.Kind == LoadoutV2Layout.Kind.Hull) Inside(element.Bounds, layout.HullViewport);
            if (element.Kind == LoadoutV2Layout.Kind.Slot) Inside(element.Bounds, layout.SlotViewport);
            if (element.Kind == LoadoutV2Layout.Kind.Offer)
            {
                Inside(element.Bounds, layout.OfferViewport);
                Assert.Equal(layout.ExpandedSlot, element.SlotIndex);
                Assert.True(element.Bounds.Bottom <= layout.StatsBar.Y);
                Assert.True(element.Bounds.Bottom <= layout.ButtonRow.Y);
            }
        }
        Assert.Same(layout.Elements, DockedTouchZones.LoadoutV2Zones(layout));
        if (layout.OfferCapacity > 0)
        {
            Inside(layout.OfferViewport, layout.SlotViewport);
            var owner = Assert.Single(layout.Elements, e =>
                e.Kind == LoadoutV2Layout.Kind.Slot && e.Index == layout.ExpandedSlot);
            Assert.True(layout.OfferViewport.Y >= owner.Bounds.Bottom);
        }
        Assert.Null(layout.HitTest(-1, 0));
        Assert.Null(layout.HitTest(layout.Viewport.Right, 1));
        Assert.Null(layout.HitTest(0, layout.Viewport.Bottom));
        Assert.Null(layout.HitTest(layout.Footer.CenterX, layout.Footer.CenterY));
    }

    private static void Inside(TouchRect inner, TouchRect outer)
    {
        const float epsilon = .002f;
        Assert.True(inner.Width >= 0 && inner.Height >= 0, $"negative size: {inner}");
        Assert.True(inner.X >= outer.X - epsilon && inner.Y >= outer.Y - epsilon
            && inner.Right <= outer.Right + epsilon && inner.Bottom <= outer.Bottom + epsilon,
            $"{inner} escapes {outer}");
    }

    private static void NoOverlaps(IReadOnlyList<TouchRect> rectangles)
    {
        for (int i = 0; i < rectangles.Count; i++)
        for (int j = i + 1; j < rectangles.Count; j++)
        {
            var a = rectangles[i];
            var b = rectangles[j];
            float dx = MathF.Min(a.Right, b.Right) - MathF.Max(a.X, b.X);
            float dy = MathF.Min(a.Bottom, b.Bottom) - MathF.Max(a.Y, b.Y);
            Assert.False(dx > .002f && dy > .002f, $"overlap: {a}, {b}");
        }
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public void Picker_inserts_rows_and_scrolls_to_first_middle_and_last_open_slot(int w, int h, HudStyle style)
    {
        float scale = ModernHudScale.Core(1f, new HudSettings { Style = style }.Size);
        foreach (int open in new[] { 0, 15, 39 })
        foreach (int scroll in new[] { int.MinValue, 0, 10, int.MaxValue })
        {
            var l = LoadoutV2Layout.Compute(w, h, scale, 200, 40, 100, open, scroll, scroll, scroll);
            CheckProperties(l);
            var owner = Assert.Single(l.Elements, e => e.Kind == LoadoutV2Layout.Kind.Slot && e.Index == open);
            Assert.Equal(l.SlotViewport.Y + (open - l.SlotScroll) * l.RowPitch, owner.Bounds.Y, 3);
            foreach (var row in l.Elements.Where(e => e.Kind == LoadoutV2Layout.Kind.Slot && e.Index > open))
            {
                Assert.True(row.Bounds.Y >= l.OfferViewport.Bottom - .001f);
                Assert.Equal(l.SlotViewport.Y + (row.Index + l.OfferCapacity - l.SlotScroll) * l.RowPitch,
                    row.Bounds.Y, 3);
            }
            Assert.Equal(Math.Clamp(scroll, 0, l.MaxHullScroll), l.HullScroll);
            Assert.Equal(Math.Clamp(scroll, 0, l.MaxOfferScroll), l.OfferScroll);
            Assert.Equal(Math.Clamp(scroll, l.MinSlotScroll, l.MaxSlotScroll), l.SlotScroll);
            var offers = l.Elements.Where(e => e.Kind == LoadoutV2Layout.Kind.Offer).ToArray();
            Assert.Equal(l.OfferCapacity, offers.Length);
            Assert.Equal(l.OfferScroll, offers[0].Index);
            Assert.Equal(l.OfferScroll + offers.Length - 1, offers[^1].Index);
        }
    }

    [Fact]
    public void Empty_closed_and_invalid_expansion_have_no_phantom_offers()
    {
        foreach (int slots in new[] { 0, 5 })
        foreach (int open in new[] { -2, -1, 0, 5, 100 })
        {
            var l = LoadoutV2Layout.Compute(1280, 720, 1, 0, slots, 0, open,
                int.MaxValue, int.MaxValue, int.MaxValue, statCount: 0);
            CheckProperties(l);
            Assert.DoesNotContain(l.Elements, e => e.Kind is LoadoutV2Layout.Kind.Offer or LoadoutV2Layout.Kind.Hull
                or LoadoutV2Layout.Kind.Stat);
            Assert.Equal(default, l.OfferViewport);
        }
        var closed = LoadoutV2Layout.Compute(1280, 720, 1, 0, 40, 99, -1, slotScroll: int.MaxValue);
        Assert.Equal(0, closed.OfferScroll);
        Assert.Equal(39, closed.Elements.Last(e => e.Kind == LoadoutV2Layout.Kind.Slot).Index);
    }

    [Fact]
    public void Scroll_limits_reach_the_last_hull_and_offer_without_changing_indices()
    {
        var l = LoadoutV2Layout.Compute(1280, 720, 1, 200, 20, 300, 19,
            int.MaxValue, int.MaxValue, int.MaxValue);
        Assert.Equal(199, l.Elements.Last(e => e.Kind == LoadoutV2Layout.Kind.Hull).Index);
        Assert.Equal(299, l.Elements.Last(e => e.Kind == LoadoutV2Layout.Kind.Offer).Index);
        CheckProperties(l);
    }

    [Fact]
    public void Scale_tracks_preference_and_caps_to_window_without_scaling_the_footer()
    {
        var small = LoadoutV2Layout.Compute(1920, 1080, .5f, 20, 20, 20, 0);
        var large = LoadoutV2Layout.Compute(1920, 1080, 1.5f, 20, 20, 20, 0);
        Assert.Equal(3 * small.RowPitch, large.RowPitch);
        Assert.Equal(84f, small.Footer.Height);
        Assert.Equal(84f, large.Footer.Height);
        var wide = LoadoutV2Layout.Compute(3840, 2160, 1, 20, 20, 20);
        Assert.Equal(1, wide.Scale);
        foreach (float scale in new[] { float.NaN, float.PositiveInfinity, -1, 0, 100 })
            CheckProperties(LoadoutV2Layout.Compute(640, 480, scale, 20, 20, 20, 19));
        Assert.Throws<ArgumentOutOfRangeException>(() => LoadoutV2Layout.Compute(0, 720, 1, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => LoadoutV2Layout.Compute(1280, float.NaN, 1, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => LoadoutV2Layout.Compute(1280, 720, 1, -1, 0, 0));
    }

    [Fact]
    public void Hit_testing_uses_half_open_edges_and_leaves_gaps_unclaimed()
    {
        var l = LoadoutV2Layout.Compute(1280, 720, 1, 10, 10, 10, 1);
        var button = l.Elements.First(e => e.Kind == LoadoutV2Layout.Kind.Default);
        Assert.Equal(button, l.HitTest(button.Bounds.X, button.Bounds.Y));
        Assert.Null(l.HitTest(button.Bounds.Right, button.Bounds.CenterY));
        Assert.Null(l.HitTest(button.Bounds.CenterX, button.Bounds.Bottom));
        Assert.Null(l.HitTest(l.HullList.Right + 1, l.HullList.CenterY));
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public void Long_names_and_need_money_labels_fit_their_text_budgets(int w, int h, HudStyle style)
    {
        float scale = ModernHudScale.Core(1f, new HudSettings { Style = style, Size = 2f }.Size);
        var l = LoadoutV2Layout.Compute(w, h, scale, 10, 10, 30, 0);
        string[] names = [new('W', 200), "Need $2147483647", "Heavy Interceptor - Advanced Autocannon II"];
        foreach (var e in l.Elements)
        foreach (string name in names)
        {
            static float Measure(string s) => s.Sum(c => c == 'W' ? 14f : c == '.' ? 3f : 7f);
            string fitted = LoadoutV2Layout.FitText(e, name, Measure, l.Scale);
            Assert.True(Measure(fitted) * l.Scale <= e.TextBounds.Width + .001f);
            if (Measure(name) * l.Scale <= e.TextBounds.Width) Assert.Equal(name, fitted);
            else if (fitted.Length > 0) Assert.EndsWith("...", fitted);
        }
        var tiny = new LoadoutV2Layout.Element(LoadoutV2Layout.Kind.Hull, new(0, 0, 2, 20), new(0, 0, 2, 20));
        Assert.Equal("", LoadoutV2Layout.FitText(tiny, "too wide", s => s.Length * 8));
        Assert.Equal("", LoadoutV2Layout.FitText(tiny, "", s => s.Length * 8));
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public void Real_PCore001_slot_and_offer_counts_fit_without_reimplementing_shop_rules(HudStyle style)
    {
        Require.Artwork();
        var cat = ShopCatalog.TryLoad(ExternalPaths.Resolve(ExternalResourceKind.Artwork));
        Assert.NotNull(cat);
        int checkedSlots = 0;
        foreach (var hull in cat.Hulls.Where(h => h.GroupId >= 0))
        {
            var model = new ShopModel(cat, hull.HullId, Array.Empty<ExpandedPartData>());
            model.SwitchTab(ShopTab.Parts);
            int slots = model.Slots.Count;
            for (int slot = 0; slot < slots; slot++)
            {
                model.MoveCursor(slot - model.Cursor);
                model.Activate();
                Assert.True(model.SlotOpen);
                int offers = model.OpenSlotParts.Count + 1; // shared model's final empty-slot row
                var l = LoadoutV2Layout.Compute(1280, 720,
                    ModernHudScale.Core(1f, new HudSettings { Style = style }.Size),
                    cat.Hulls.Count, slots, offers, slot, int.MaxValue, int.MaxValue, int.MaxValue);
                CheckProperties(l);
                Assert.Equal(offers - 1, l.Elements.Last(e => e.Kind == LoadoutV2Layout.Kind.Offer).Index);
                model.Back();
                checkedSlots++;
            }
        }
        Assert.True(checkedSlots > 0);
    }
}
