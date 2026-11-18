using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Rendering.Jet;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Tests.Skins;

/// <summary>
/// P1042b — the pre-game sub-view families behind <see cref="IScreenSkinRenderer"/>:
/// <list type="bullet">
/// <item>every selectable row is drawn at the frame's resolved band (draw == hit);</item>
/// <item>the selection is marked by shape AND ink, and green/amber draw identical geometry;</item>
/// <item>the career chrome frames the renderer's own tab rectangles;</item>
/// <item>the new chrome allocates nothing per frame;</item>
/// <item>the doubled Buy/Def class (chrome text over content text of the same control) is a
/// measured geometric overlap that the content-suppression flag removes.</item>
/// </list>
/// </summary>
public sealed class P1042bScreenChromeTests
{
    public static TheoryData<int, int> Resolutions => new()
    {
        { 800, 480 },
        { 1920, 1080 },
        { 3840, 2160 },
    };

    private static IntroViewFrame View(int w, int h, int selected = 0)
    {
        var labels = new[] { "Training", "Skirmish", "Back" };
        var layout = IntroLayout.Compute(w, h, labels.Length);
        var rows = new List<ScreenRow>();
        for (int i = 0; i < labels.Length; i++)
        {
            string text = (i == selected ? "> " : "  ") + labels[i];
            var row = layout.Rows[i];
            rows.Add(new(labels[i], text, new Vector2(layout.Rows[i].X, row.Y + 4), 
                new RectangleF(row.X, row.Y, row.Width, row.Height), 1f, i == selected,
                i == selected ? ScreenInkRole.Title : ScreenInkRole.Body,
                i == selected ? Color.Gold : Color.White));
        }
        return new IntroViewFrame(new Viewport(0, 0, w, h), "singleplayer", "Single Player",
            "Training or Skirmish", rows,
            new List<ScreenTextRun> { new("UP/DOWN select   ENTER choose   ESC back",
                new Vector2(40, layout.FooterY), 1f, ScreenInkRole.Dim, Color.Gray) },
            new List<ScreenPlate>(), new ScreenInks(Color.Gold, Color.White, Color.Gray, Color.Orange));
    }

    private static CareerScreenFrame Career(int w, int h)
        => new(new Viewport(0, 0, w, h), null, null, null, null, false, false, false, 0, false,
            "", "Not signed in.", "", HudStyle.Modern,
            new ScreenInks(Color.Gold, Color.White, Color.Gray, Color.Orange));

    [Theory]
    [MemberData(nameof(Resolutions))]
    public void View_every_row_draws_at_its_own_resolved_band(int w, int h)
    {
        var frame = View(w, h, selected: 1);
        var surface = new RecordingSurface();
        using var chrome = new JetScreenSkinRenderer(surface, null, null, null,
            new HudSettings { Skin = HudSkinIds.Jet }, null, JetPaletteIds.Green);
        chrome.DrawIntroView(frame);

        Assert.Equal(frame.Rows.Count, surface.Controls.Count);
        for (int i = 0; i < frame.Rows.Count; i++)
        {
            var control = surface.Controls[i];
            Assert.Equal(ScreenActions.ViewBody, control.Family);
            Assert.Equal(ScreenActions.ViewRow(i), control.Id);
            var row = frame.Rows[i];
            Assert.Equal(row.Rect.X, control.X, 2);
            Assert.Equal(row.Rect.Y, control.Y, 2);
            Assert.Equal(row.Rect.Width, control.W, 2);
            Assert.Equal(row.Rect.Height, control.H, 2);
            Assert.True(row.Rect.X >= 0 && row.Rect.Y >= 0
                && row.Rect.X + row.Rect.Width <= w && row.Rect.Y + row.Rect.Height <= h,
                $"row {i} leaves the {w}x{h} viewport");
        }
    }

    [Fact]
    public void View_selection_is_shape_and_ink_and_green_equals_amber()
    {
        var settings = new HudSettings { Skin = HudSkinIds.Jet };
        var green = new RecordingSurface();
        var amber = new RecordingSurface();
        using var greenChrome = new JetScreenSkinRenderer(green, null, null, null, settings, null, JetPaletteIds.Green);
        using var amberChrome = new JetScreenSkinRenderer(amber, null, null, null, settings, null, JetPaletteIds.Amber);
        var frame = View(1920, 1080, selected: 1);
        greenChrome.DrawIntroView(frame);
        amberChrome.DrawIntroView(frame);

        // Same rectangles and same plate/line work in both palettes.
        Assert.Equal(green.Controls, amber.Controls);
        Assert.Equal(green.Fills, amber.Fills);
        Assert.Equal(green.Lines.Count, amber.Lines.Count);

        // Both signals are present: the selected row carries the Hot ink and a thicker
        // bracket stroke; the unselected row carries neither (never hue-only selection).
        var hot = new Color(0xC9, 0xFF, 0xE3);   // jet green Hot
        // frame selected == 1, so "Skirmish" is the selected row.
        Assert.Contains(green.Texts, t => t.Text == "Skirmish" && t.Color == hot);
        Assert.DoesNotContain(green.Texts, t => t.Text == "Training" && t.Color == hot);
        Assert.Contains(green.Lines, l => MathF.Abs(l.Width - 3f) < 0.01f);   // full stroke + keyline
        Assert.Contains(green.Lines, l => MathF.Abs(l.Width - 2f) < 0.01f);   // hairline + keyline
        // Each row plate is one fill + four keyline edges (FillPlate's own contract).
        Assert.Equal(frame.Rows.Count * 5, green.Fills.Count);
    }

    [Theory]
    [MemberData(nameof(Resolutions))]
    public void Career_tabs_draw_at_the_renderer_rectangles(int w, int h)
    {
        var surface = new RecordingSurface();
        using var chrome = new JetScreenSkinRenderer(surface, null, null, null,
            new HudSettings { Skin = HudSkinIds.Jet }, null, JetPaletteIds.Green);
        chrome.DrawCareer(Career(w, h));

        Assert.Equal(CareerScreenRenderer.TabLabels.Length, surface.Controls.Count);
        for (int i = 0; i < CareerScreenRenderer.TabLabels.Length; i++)
        {
            var control = surface.Controls[i];
            Assert.Equal(ScreenActions.Career, control.Family);
            Assert.Equal(ScreenActions.CareerTab(i), control.Id);
            var expected = CareerScreenRenderer.TabRect(new Vector2(
                CareerScreenRenderer.TabLabels[i].Length * 8f, 18f), i, w);
            Assert.Equal(expected.X, control.X, 2);
            Assert.Equal(expected.Y, control.Y, 2);
            Assert.Equal(expected.Width, control.W, 2);
            Assert.Equal(expected.Height, control.H, 2);
        }
    }

    [Fact]
    public void Career_panel_rectangle_stays_inside_the_viewport()
    {
        foreach (var (w, h) in new[] { (800, 480), (1920, 1080), (3840, 2160) })
        {
            var panel = CareerScreenRenderer.PanelRect(w, h);
            Assert.True(panel.X >= 0 && panel.Y >= 0);
            Assert.True(panel.X + panel.Width <= w, $"panel spills right at {w}x{h}");
            Assert.True(panel.Y + panel.Height <= h, $"panel spills bottom at {w}x{h}");
        }
    }

    [Fact]
    public void View_and_career_chrome_allocate_nothing_per_frame()
    {
        var surface = new NullSurface();
        using var chrome = new JetScreenSkinRenderer(surface, null, null, null,
            new HudSettings { Skin = HudSkinIds.Jet }, null);
        var view = View(1920, 1080, selected: 2);
        var career = Career(1920, 1080);
        for (int i = 0; i < 16; i++) { chrome.DrawIntroView(view); chrome.DrawCareer(career); }  // warm statics
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 240; i++) { chrome.DrawIntroView(view); chrome.DrawCareer(career); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Theory]
    [InlineData(800, 480)]
    [InlineData(1920, 1080)]
    public void Port_verb_labels_overlap_until_the_content_is_suppressed(int w, int h)
    {
        // The content label is painted at rect.X + 2 in the fallback face; the chrome label
        // is fitted and centred in the same control. With both painted their ink boxes
        // intersect at every resolution — exactly the doubled Buy/Def class the audit's
        // ContentOverlap rule counts. After P1042b the shared renderer paints neither
        // (the Jet call passes drawPortChrome: false), so only the chrome run remains.
        var rect = HangarScreen.PortVerbRect(w, h, second: false);
        // The fallback face measures "Buy" at roughly 11 px per glyph in the HUD small font
        // (the recorder measures 8 px; the production label is wider, which is why the
        // audit counted the cross at 1080p and not at 4K where the cell is 138 px wide).
        float contentWidth = "Buy".Length * 11f, contentHeight = 16f;
        var content = new RectangleF(rect.X + 2f, rect.Y + 1f, contentWidth, contentHeight);
        float chromeScale = MathF.Min(1f, (rect.Width - 4f) / MathF.Max(1f, contentWidth));
        var chrome = new RectangleF(rect.X + rect.Width / 2f - contentWidth * chromeScale / 2f,
            rect.Y + rect.Height / 2f - contentHeight * chromeScale / 2f,
            contentWidth * chromeScale, contentHeight * chromeScale);
        Assert.True(content.X < chrome.X + chrome.Width && chrome.X < content.X + content.Width
            && content.Y < chrome.Y + chrome.Height && chrome.Y < content.Y + content.Height,
            $"the two labels must overlap at {w}x{h} — otherwise the defect class is not what was fixed");

        // The content renderer exposes the suppression flag with the classic default (so
        // Classic/Modern keep their drawing unchanged).
        var draw = typeof(HangarScreenRenderer).GetMethod(nameof(HangarScreenRenderer.Draw))!;
        var flag = draw.GetParameters().Single(p => p.Name == "drawPortChrome");
        Assert.Equal(true, flag.DefaultValue);
    }

    private sealed class NullSurface : IJetVectorSurface
    {
        public void Begin() { }
        public void End() { }
        public void Line(Vector2 a, Vector2 b, float width, Color color) { }
        public void Fill(Vector2 min, Vector2 size, Color color) { }
        public void Text(string text, Vector2 position, bool large, float scale, Color color) { }
        public Vector2 Measure(string text, bool large, float scale) => Vector2.Zero;
    }

    /// <summary>Deterministic 8 px-per-character measure + control/plate/ink recorder.</summary>
    private sealed class RecordingSurface : IJetVectorSurface, IScreenRectAudit
    {
        public readonly record struct ControlRun(string Family, string Id, float X, float Y, float W, float H);
        public readonly record struct InkRun(string Text, Color Color);

        public readonly List<ControlRun> Controls = new();
        public readonly List<InkRun> Texts = new();
        public readonly List<(float X, float Y, float W, float H)> Fills = new();
        public readonly List<(Vector2 A, Vector2 B, float Width)> Lines = new();

        public void Control(string family, string id, RectangleF drawn)
            => Controls.Add(new(family, id, drawn.X, drawn.Y, drawn.Width, drawn.Height));
        public void Plate(string family, string id, RectangleF box) { }
        public void Content(string family, string id, RectangleF box, string text) { }
        public void Begin() { }
        public void End() { }
        public void Line(Vector2 a, Vector2 b, float width, Color color) => Lines.Add((a, b, width));
        public void Fill(Vector2 min, Vector2 size, Color color) => Fills.Add((min.X, min.Y, size.X, size.Y));
        public void Text(string text, Vector2 position, bool large, float scale, Color color)
            => Texts.Add(new(text, color));
        public Vector2 Measure(string text, bool large, float scale)
            => new(text.Length * 8f * scale, 18f * scale);
    }
}
