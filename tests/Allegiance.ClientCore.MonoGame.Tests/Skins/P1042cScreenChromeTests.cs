using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Rendering.Jet;
using Allegiance.ClientCore.MonoGame.Rendering.Screens;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Tests.Skins;

/// <summary>
/// P1042c — the in-game families behind <see cref="IScreenSkinRenderer"/>:
/// <list type="bullet">
/// <item>every panel control row is drawn at the frame's resolved band (draw == hit);</item>
/// <item>the selection is shape+ink and green/amber draw identical geometry;</item>
/// <item>the leaderboard/starmap/game-over chrome frames the rectangles it is given;</item>
/// <item>the chrome allocates nothing per frame;</item>
/// <item>item 0b: the title-vs-tab rule flags the classic title geometry (measured with the
/// real HUD fonts by <c>tools/p1042-screen-proof --pre-title</c>) and clears the fitted Jet title.</item>
/// </list>
/// The frames are built by hand here (the production builders' equality with the classic
/// painters is the proof tool's decoded-pixel gate, §3 of the report).
/// </summary>
public sealed class P1042cScreenChromeTests
{
    public static TheoryData<int, int> Resolutions => new()
    {
        { 800, 480 },
        { 1920, 1080 },
        { 3840, 2160 },
    };

    /// <summary>A side-select-shaped frame: panel + one backdrop fill + four control rows.</summary>
    private static ScreenPanelFrame SideSelectFrame(int w, int h)
    {
        float panelW = MathF.Min(w * 0.72f, 700f), panelH = MathF.Min(h * 0.60f, 300f);
        float x = (w - panelW) / 2f, y = (h - panelH) / 2f;
        var fills = new List<ScreenFill> { new(new RectangleF(x, y, panelW, panelH), Color.Black, "panel") };
        var rows = new List<PanelTextRow>
        {
            new("", "SELECT SIDE", new Vector2(x + 24f, y + 24f),
                new RectangleF(x + 24f, y + 24f, 200f, 40f), true, 1f, Color.Gold,
                ScreenInkRole.Title, false, false, panelW - 48f),
        };
        for (int i = 0; i < 3; i++)
        {
            float ry = y + 80f + i * 26f;
            var band = new RectangleF(x + 24f, ry, panelW - 48f, 24f);
            rows.Add(new("sideselect.row", (i == 1 ? "> " : "  ") + "Side " + i,
                new Vector2(band.X, ry), band, false, 1f, i == 1 ? Color.Green : Color.White,
                i == 1 ? ScreenInkRole.Highlight : ScreenInkRole.Body, i == 1, true, band.Width));
        }
        return new ScreenPanelFrame(new Viewport(0, 0, w, h), PanelScreenFrames.SideSelect,
            new RectangleF(x, y, panelW, panelH), fills, rows,
            new ScreenInks(Color.Gold, Color.White, Color.Gray, Color.Orange));
    }

    [Theory]
    [MemberData(nameof(Resolutions))]
    public void Panel_every_control_row_draws_at_its_resolved_band(int w, int h)
    {
        var frame = SideSelectFrame(w, h);
        var surface = new RecordingSurface();
        using var chrome = new JetScreenSkinRenderer(surface, null, null, null,
            new HudSettings { Skin = HudSkinIds.Jet }, null, JetPaletteIds.Green);
        chrome.DrawPanel(frame);

        Assert.Equal(3, surface.Controls.Count);
        int index = 0;
        for (int i = 0; i < frame.Rows.Count; i++)
        {
            if (!frame.Rows[i].Control) continue;
            var control = surface.Controls[index];
            Assert.Equal(ScreenActions.PanelSideSelect, control.Family);
            Assert.Equal(ScreenActions.PanelRow(ScreenActions.PanelSideSelect, index), control.Id);
            var row = frame.Rows[i];
            Assert.Equal(row.Rect.X, control.X, 2);
            Assert.Equal(row.Rect.Y, control.Y, 2);
            Assert.Equal(row.Rect.Width, control.W, 2);
            Assert.Equal(row.Rect.Height, control.H, 2);
            Assert.True(row.Rect.X >= 0 && row.Rect.Y >= 0
                && row.Rect.X + row.Rect.Width <= w && row.Rect.Y + row.Rect.Height <= h,
                $"row {i} leaves the {w}x{h} viewport");
            index++;
        }
    }

    [Fact]
    public void Panel_selection_is_shape_and_ink_and_green_equals_amber()
    {
        var settings = new HudSettings { Skin = HudSkinIds.Jet };
        var green = new RecordingSurface();
        var amber = new RecordingSurface();
        using var greenChrome = new JetScreenSkinRenderer(green, null, null, null, settings, null, JetPaletteIds.Green);
        using var amberChrome = new JetScreenSkinRenderer(amber, null, null, null, settings, null, JetPaletteIds.Amber);
        var frame = SideSelectFrame(1920, 1080);
        greenChrome.DrawPanel(frame);
        amberChrome.DrawPanel(frame);

        Assert.Equal(green.Controls, amber.Controls);
        Assert.Equal(green.Fills, amber.Fills);
        Assert.Equal(green.Lines.Count, amber.Lines.Count);

        var hot = new Color(0xC9, 0xFF, 0xE3);   // jet green Hot
        Assert.Contains(green.Texts, t => t.Text.StartsWith("> Side 1") && t.Color == hot);
        Assert.DoesNotContain(green.Texts, t => t.Text.StartsWith("  Side 0") && t.Color == hot);
        Assert.Contains(green.Lines, l => MathF.Abs(l.Width - 3f) < 0.01f);   // selected: full stroke
        Assert.Contains(green.Lines, l => MathF.Abs(l.Width - 2f) < 0.01f);   // others: hairline
        // One plate per control row = one fill + four keyline edges.
        Assert.Equal(3 * 5, green.Fills.Count);
    }

    [Theory]
    [MemberData(nameof(Resolutions))]
    public void Scoreboard_chrome_frames_the_frames_bands(int w, int h)
    {
        var rows = new List<ScoreboardLayout.Row>
        {
            new("1", "IronWolf", "S0", "Veteran 2", "12", "4"),
            new("2", "BeltRunner", "S0", "Novice 3", "7", "6"),
        };
        var geometry = ScoreboardLayout.Layout(rows, s => s.Length * 8f, w, h);
        var header = new RectangleF(geometry.Panel.X + 8, geometry.HeaderY, geometry.Panel.Width - 16, geometry.RowHeight);
        var bands = new[]
        {
            new RectangleF(geometry.Panel.X + 8, geometry.HeaderY + geometry.RowHeight, geometry.Panel.Width - 16, geometry.RowHeight),
            new RectangleF(geometry.Panel.X + 8, geometry.HeaderY + 2 * geometry.RowHeight, geometry.Panel.Width - 16, geometry.RowHeight),
        };
        var frame = new ScoreboardFrame(new Viewport(0, 0, w, h), rows, "IronWolf",
            new RectangleF(geometry.Panel.X, geometry.Panel.Y, geometry.Panel.Width, geometry.Panel.Height),
            header, bands, null, new ScreenInks(Color.White, Color.White, Color.Gray, Color.Orange));

        var surface = new RecordingSurface();
        using var chrome = new JetScreenSkinRenderer(surface, null, null, null,
            new HudSettings { Skin = HudSkinIds.Jet }, null, JetPaletteIds.Green);
        chrome.DrawScoreboard(frame);

        Assert.Equal(3, surface.Controls.Count);   // header + two row bands
        Assert.Equal(ScreenActions.ScoreboardHeader, surface.Controls[0].Id);
        Assert.Equal(ScreenActions.ScoreboardRow(0), surface.Controls[1].Id);
        Assert.Equal(header.Y, surface.Controls[0].Y, 2);
        Assert.Equal(bands[0].Y, surface.Controls[1].Y, 2);
        Assert.Equal(bands[1].Y, surface.Controls[2].Y, 2);
    }

    [Fact]
    public void Starmap_chrome_frames_the_given_panel_and_info_rectangles()
    {
        var surface = new RecordingSurface();
        using var chrome = new JetScreenSkinRenderer(surface, null, null, null,
            new HudSettings { Skin = HudSkinIds.Jet }, null, JetPaletteIds.Green);
        var vp = new Viewport(0, 0, 1920, 1080);
        var info = new RectangleF(100, 200, 300, 120);
        var expand = new RectangleF(370, 300, 16, 16);
        var frame = new StarmapScreenFrame(vp, [], [], null, null, null, false, 1200, 700,
            null, null, null, null, null, null, null, true,
            new RectangleF(340, 40, 1200, 700), info, expand,
            new ScreenInks(Color.White, Color.White, Color.Gray, Color.Orange));
        chrome.DrawStarmap(frame);
        Assert.Equal(2, surface.Controls.Count);
        Assert.Equal(ScreenActions.StarmapInfo, surface.Controls[0].Id);
        Assert.Equal(info.X, surface.Controls[0].X, 2);
        Assert.Equal(ScreenActions.StarmapExpand, surface.Controls[1].Id);
        Assert.Equal(expand.Width, surface.Controls[1].W, 2);
    }

    [Fact]
    public void Panel_chrome_allocates_nothing_per_frame()
    {
        var surface = new NullSurface();
        using var chrome = new JetScreenSkinRenderer(surface, null, null, null,
            new HudSettings { Skin = HudSkinIds.Jet }, null);
        var frame = SideSelectFrame(1920, 1080);
        for (int i = 0; i < 16; i++) chrome.DrawPanel(frame);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 240; i++) chrome.DrawPanel(frame);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ── item 0b: title vs tab row ────────────────────────────────────────────
    /// <summary>
    /// The classic career title measured with the real HUD faces at 800×480 (the audit's
    /// <c>--pre-title</c> record): <c>[ CHARACTER INFO ]</c> is 432×47 ink at (184,24), the tab
    /// row's top edge is <see cref="CareerScreenRenderer.TabRowTop"/> = 63. The title's bottom
    /// (71) therefore crosses the tab row by 8 px on every resolution (the 1080p record is
    /// 1024×47 at (448,24)). The Jet chrome fits the title into
    /// <see cref="CareerScreenRenderer.TitleTop"/>..TabRowTop-6, which clears the rule.
    /// </summary>
    [Theory]
    [InlineData(800, 432f)]
    [InlineData(1920, 1024f)]
    [InlineData(3840, 2048f)]
    public void Career_title_vs_tab_rule_flags_the_classic_title_and_clears_the_fitted_one(int w, float titleWidth)
    {
        const float gap = 6f;
        const float inkHeight = 47f;
        var classic = new RectangleF((w - titleWidth) / 2f, CareerScreenRenderer.TitleTop, titleWidth, inkHeight);
        var tabs = TabBands(w);
        Assert.True(Crowding(classic, tabs, gap) > 0,
            $"the classic title must crowd the tab row at {w} (title bottom {classic.Y + classic.Height} vs tab top {tabs[0].Y})");

        float band = CareerScreenRenderer.TabRowTop - gap - 1f - CareerScreenRenderer.TitleTop;
        float scale = MathF.Min(1f, band / inkHeight);
        var fitted = new RectangleF((w - titleWidth * scale) / 2f, CareerScreenRenderer.TitleTop,
            titleWidth * scale, inkHeight * scale);
        Assert.Equal(0, Crowding(fitted, tabs, gap));
        Assert.True(fitted.Y + fitted.Height + gap <= CareerScreenRenderer.TabRowTop);
    }

    private static List<RectangleF> TabBands(int w)
    {
        // TabRect needs only the label ink; the audit's recording measures with the real small
        // face (≈8.4 px per glyph), and the rule's clearance is a Y comparison.
        var tabs = new List<RectangleF>();
        for (int i = 0; i < CareerScreenRenderer.TabLabels.Length; i++)
        {
            var ink = new Vector2(CareerScreenRenderer.TabLabels[i].Length * 8.4f, 20f);
            var rect = CareerScreenRenderer.TabRect(ink, i, w);
            tabs.Add(new RectangleF(rect.X, rect.Y, rect.Width, rect.Height));
        }
        return tabs;
    }

    private static int Crowding(RectangleF title, List<RectangleF> tabs, float gap)
    {
        int count = 0;
        foreach (var tab in tabs)
            if (tab.Y - (title.Y + title.Height) < gap) count++;
        return count;
    }

    [Fact]
    public void Career_content_renderer_keeps_the_title_by_default()
    {
        // Classic/Modern must keep their drawing unchanged: the title flag defaults to true.
        var draw = typeof(CareerScreenRenderer).GetMethod(nameof(CareerScreenRenderer.Draw))!;
        var flag = draw.GetParameters().Single(p => p.Name == "drawTitle");
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
