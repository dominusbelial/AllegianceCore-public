using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Rendering.Jet;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Tests.Skins;

/// <summary>
/// P1042 (P972 slice 1F, §6.1) — the screen-chrome boundary's guarantees, device-free:
/// <list type="bullet">
/// <item>every intro command row is drawn at the rectangle hit testing resolves;</item>
/// <item>every hangar verb (native pane or port-only Buy/Default) has ONE rectangle for
/// draw and hit, at 800×480, 1080p and 4K;</item>
/// <item>every loadout V2 leaf hits itself (its draw bounds ARE its hit bounds);</item>
/// <item>the registry publishes Classic/Modern/Jet by id and gates Jet behind
/// <c>hud.jetSkin</c> (nothing is visible with the flag off);</item>
/// <item>the Jet chrome draws the same rectangles in green and amber and fits its plates
/// at 800×480;</item>
/// <item>a Jet screen frame draws with 0 bytes of allocation.</item>
/// </list>
/// </summary>
public sealed class P1042ScreenBoundaryTests
{
    public static TheoryData<int, int> Resolutions => new()
    {
        { 800, 480 },
        { 1920, 1080 },
        { 3840, 2160 },
    };

    private static IntroMainFrame Frame(int w, int h, int selected = 0)
    {
        var labels = IntroScreenModel.MainLabels;
        var layout = IntroLayout.Compute(w, h, labels.Length);
        return new IntroMainFrame(new Viewport(0, 0, w, h),
            "ALLEGIANCE", "Choose how to play  -  Single Player or Multiplayer",
            labels, layout, selected,
            "Pilot:  (unverified)", "", true, IntroLayout.PilotField(w, h), 0f,
            "Not signed in", 1f, IntroScreenModel.MainHint(true), 1f,
            new ScreenInks(Color.Gold, Color.White, Color.Gray, Color.Orange));
    }

    private static HangarScreenFrame Hangar(int w, int h, bool developments, HangarAction hover = HangarAction.None)
        => new(new Viewport(0, 0, w, h), hover, new HangarLaunchAnimation(), 0, developments,
            new ScreenInks(Color.Gray, Color.White, Color.Gray, Color.Orange));

    private static LoadoutV2Layout LoadoutLayout(int w, int h, float uiScale = 1f)
        => LoadoutV2Layout.Compute(w, h, uiScale, hullRowCount: 12, slotRowCount: 10, offerRowCount: 0, statCount: 8);

    // ── Intro ───────────────────────────────────────────────────────────────
    [Fact]
    public void Intro_action_ids_are_the_thirteen_native_commands_in_menu_order()
    {
        Assert.Equal(13, IntroScreenModel.MainLabels.Length);
        for (int i = 0; i < IntroScreenModel.MainLabels.Length; i++)
            Assert.Equal(IntroScreenModel.Activate(i).ToString(), ScreenActions.IntroRow(i));
        Assert.Equal(IntroCommand.None, IntroScreenModel.Activate(-1));
        Assert.Equal(IntroCommand.None, IntroScreenModel.Activate(13));
    }

    [Theory]
    [MemberData(nameof(Resolutions))]
    public void Intro_every_row_draws_at_its_hit_rectangle(int w, int h)
    {
        foreach (int selected in new[] { 0, 6, 12 })
        {
            var frame = Frame(w, h, selected);
            for (int i = 0; i < frame.Labels.Count; i++)
            {
                var drawn = ScreenHitGeometry.IntroRow(frame, i);
                Assert.Equal(frame.Layout.Rows[i], drawn);
                Assert.Equal(i, ScreenHitGeometry.IntroRowAt(frame, drawn.CenterX, drawn.CenterY));
                Assert.Equal(IntroScreenModel.Activate(i),
                    ScreenHitGeometry.IntroCommandAt(frame, drawn.CenterX, drawn.CenterY));
            }
            // A point between rows (or outside the column) hits nothing: the columns are
            // disjoint, so a click can never resolve two commands.
            Assert.Equal(-1, ScreenHitGeometry.IntroRowAt(frame, -4f, 12f));
        }
    }

    [Theory]
    [MemberData(nameof(Resolutions))]
    public void Intro_pilot_field_hit_is_its_resolved_box(int w, int h)
    {
        var frame = Frame(w, h);
        var field = frame.PilotField;
        Assert.True(ScreenHitGeometry.IntroPilotFieldHit(frame, field.CenterX, field.CenterY));
        Assert.False(ScreenHitGeometry.IntroPilotFieldHit(frame, field.X - 1f, field.CenterY));
        Assert.False(ScreenHitGeometry.IntroPilotFieldHit(frame, field.CenterX, field.Bottom));
    }

    // ── Hangar ──────────────────────────────────────────────────────────────
    [Theory]
    [MemberData(nameof(Resolutions))]
    public void Hangar_every_action_draw_and_hit_are_the_same_rectangle(int w, int h)
    {
        foreach (bool developments in new[] { true, false })
        {
            var frame = Hangar(w, h, developments);
            foreach (var action in HangarControls.Actions(developments))
            {
                var rect = ScreenHitGeometry.HangarRect(w, h, action);
                Assert.Equal(HangarScreen.Rect(w, h, action), rect);
                Assert.Equal(action, ScreenHitGeometry.HangarHit(frame, rect.Center.X, rect.Center.Y));
                Assert.True(rect.Width > 0 && rect.Height > 0, $"{action} has an empty rect at {w}x{h}");
                Assert.True(rect.X >= 0 && rect.Y >= 0 && rect.Right <= w && rect.Bottom <= h,
                    $"{action} rect {rect} falls outside {w}x{h}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Resolutions))]
    public void Hangar_keeps_eight_native_panes_plus_the_two_port_verbs(int w, int h)
    {
        Assert.Equal(8, HangarScreen.Controls.Count);
        var withDevelopments = HangarControls.Actions(true).ToList();
        Assert.Equal(10, withDevelopments.Count);
        Assert.Contains(HangarAction.Buy, withDevelopments);
        Assert.Contains(HangarAction.Default, withDevelopments);
        Assert.Equal(9, HangarControls.Actions(false).Count());
        Assert.DoesNotContain(HangarAction.Invest, HangarControls.Actions(false));

        // With developments off, the Invest pane's own rectangle cannot resolve Invest.
        var frame = Hangar(w, h, false);
        var invest = HangarScreen.Rect(w, h, HangarAction.Invest);
        Assert.NotEqual(HangarAction.Invest, ScreenHitGeometry.HangarHit(frame, invest.Center.X, invest.Center.Y));
    }

    // ── Loadout ─────────────────────────────────────────────────────────────
    public static TheoryData<int, int, float> LoadoutSizes => new()
    {
        { 800, 480, 0.75f },
        { 1920, 1080, 1f },
        { 3840, 2160, 1.5f },
    };

    [Theory]
    [MemberData(nameof(LoadoutSizes))]
    public void Loadout_every_leaf_hits_itself(int w, int h, float scale)
    {
        var layout = LoadoutLayout(w, h, scale);
        var frame = new LoadoutScreenFrame(new Viewport(0, 0, w, h), [], null, null, 0f, 0f, false,
            null, false, null, layout, HudStyle.Modern, null, null,
            new ScreenInks(Color.Gold, Color.White, Color.Gray, Color.Orange));

        Assert.NotEmpty(layout.Elements);
        foreach (var element in layout.Elements)
        {
            var hit = ScreenHitGeometry.LoadoutV2Hit(frame, element.Bounds.CenterX, element.Bounds.CenterY);
            Assert.NotNull(hit);
            Assert.Equal(element.Kind, hit!.Value.Kind);
            Assert.Equal(element.Index, hit.Value.Index);
            Assert.Equal(element.Bounds.X, hit.Value.Bounds.X);
            Assert.Equal(element.Bounds.Y, hit.Value.Bounds.Y);
            Assert.Equal(element.Bounds.Width, hit.Value.Bounds.Width);
            Assert.Equal(element.Bounds.Height, hit.Value.Bounds.Height);
            Assert.True(element.Bounds.X >= 0 && element.Bounds.Y >= 0
                && element.Bounds.Right <= w && element.Bounds.Bottom <= h + LoadoutV2Layout.DockedFooterHeight,
                $"{element.Kind} bounds {element.Bounds} outside {w}x{h}");
        }
    }

    [Theory]
    [MemberData(nameof(LoadoutSizes))]
    public void Loadout_every_action_button_is_present_and_hit_testable(int w, int h, float scale)
    {
        var layout = LoadoutLayout(w, h, scale);
        foreach (var kind in new[] { LoadoutV2Layout.Kind.Default, LoadoutV2Layout.Kind.Revert,
                     LoadoutV2Layout.Kind.Buy, LoadoutV2Layout.Kind.Launch,
                     LoadoutV2Layout.Kind.Back, LoadoutV2Layout.Kind.Undock })
        {
            var element = layout.Elements.FirstOrDefault(e => e.Kind == kind);
            Assert.True(layout.Elements.Any(e => e.Kind == kind), $"{kind} missing at {w}x{h}");
            Assert.Equal(kind, layout.HitTest(element.Bounds.CenterX, element.Bounds.CenterY)!.Value.Kind);
        }
    }

    [Theory]
    [MemberData(nameof(LoadoutSizes))]
    public void Loadout_every_text_budget_stays_inside_the_viewport(int w, int h, float scale)
    {
        var layout = LoadoutLayout(w, h, scale);
        foreach (var element in layout.Elements)
        {
            var t = element.TextBounds;
            Assert.True(t.X >= 0f && t.Y >= 0f && t.Right <= w && t.Bottom <= h,
                $"{element.Kind} text budget {t} outside {w}x{h}");
        }
    }

    // ── Registry / switching ────────────────────────────────────────────────
    [Fact]
    public void Screen_registry_publishes_by_skin_id_and_gates_jet_behind_the_flag()
    {
        using var classic = new StubScreen(HudSkinIds.Classic);
        using var modern = new StubScreen(HudSkinIds.Modern);
        using var jet = new StubScreen(HudSkinIds.Jet);
        using var registry = new ScreenSkinRegistry(classic, modern, jet);

        Assert.Same(modern, registry.Active);              // default before the first publish
        Assert.True(registry.Publish(HudSkinIds.Classic, jetEnabled: true));
        Assert.Same(classic, registry.Active);
        Assert.False(registry.Publish(HudSkinIds.Classic, jetEnabled: false)); // no change

        // The gate: a gated-off Jet resolves to Modern (never Jet), and an unknown id
        // does the same — the exact rule the HUD skin switcher applies.
        Assert.True(registry.Publish(HudSkinIds.Jet, jetEnabled: false));
        Assert.Same(modern, registry.Active);
        Assert.False(registry.Publish(HudSkinIds.Jet, jetEnabled: false));  // already modern
        Assert.Same(modern, registry.Resolve(HudSkinIds.Jet, jetEnabled: false));
        Assert.Same(jet, registry.Resolve(HudSkinIds.Jet, jetEnabled: true));
        Assert.True(registry.Publish("  JET ", jetEnabled: true));
        Assert.Same(jet, registry.Active);
        Assert.True(registry.Publish("unknown-skin", jetEnabled: true));
        Assert.Same(modern, registry.Active);
        Assert.Equal(3, registry.Installed.Count);
    }

    [Fact]
    public void Screen_registry_without_jet_installs_only_classic_and_modern()
    {
        using var classic = new StubScreen(HudSkinIds.Classic);
        using var modern = new StubScreen(HudSkinIds.Modern);
        using var registry = new ScreenSkinRegistry(classic, modern);
        Assert.Equal(2, registry.Installed.Count);
        Assert.Same(modern, registry.Resolve(HudSkinIds.Jet, jetEnabled: true));
    }

    [Theory]
    [MemberData(nameof(Resolutions))]
    public void Switching_the_screen_chrome_leaves_the_model_and_geometry_untouched(int w, int h)
    {
        var before = Frame(w, h, selected: 4);
        using var classic = new StubScreen(HudSkinIds.Classic);
        using var modern = new StubScreen(HudSkinIds.Modern);
        using var jet = new StubScreen(HudSkinIds.Jet);
        using var registry = new ScreenSkinRegistry(classic, modern, jet);
        registry.Publish(HudSkinIds.Jet, jetEnabled: true);
        var after = Frame(w, h, selected: before.Selected);

        Assert.Equal(before.Selected, after.Selected);
        Assert.Equal(before.Labels, after.Labels);
        Assert.Equal(before.Layout.RowHeight, after.Layout.RowHeight);
        Assert.Equal(before.Layout.PilotY, after.Layout.PilotY);
        Assert.Equal(before.Layout.IdentityY, after.Layout.IdentityY);
        for (int i = 0; i < before.Layout.Rows.Length; i++)
            Assert.Equal(before.Layout.Rows[i], after.Layout.Rows[i]);
    }

    // ── Jet chrome ──────────────────────────────────────────────────────────
    [Theory]
    [MemberData(nameof(Resolutions))]
    public void Jet_intro_chrome_fits_every_row_plate(int w, int h)
    {
        var frame = Frame(w, h, selected: 2);
        var painter = new JetVectorPainter(new NullSurface(), Color.Green, Color.DarkGreen,
            Color.White, Color.Black, Color.Black);
        float band = JetScreenChrome.Band(painter);
        for (int i = 0; i < frame.Layout.Rows.Length; i++)
        {
            var row = frame.Layout.Rows[i];
            var box = new RectangleF(row.X, row.Y, row.Width, row.Height);
            var inner = JetScreenChrome.Inner(box, band);
            Assert.True(inner.Width > 0f && inner.Height > 0f, $"row {i} plate has no inner area at {w}x{h}");
            // A representative single-line ink box (the HUD small font at its own scale):
            // the fitted scale must keep it inside the plate, never clipped.
            var ink = new Vector2(200f, 20f);
            float fitted = JetScreenChrome.FitScale(ink, box, frame.Layout.RowTextScale, band);
            Assert.True(JetScreenChrome.Fits(ink, box, fitted, band), $"row {i} text spills at {w}x{h}");
            Assert.True(fitted <= frame.Layout.RowTextScale + 0.0001f, "Jet never enlarges native row text");
        }
    }

    [Fact]
    public void Jet_intro_and_hangar_chrome_geometry_is_identical_in_green_and_amber()
    {
        var settings = new HudSettings { Skin = HudSkinIds.Jet };
        var green = new RecordingSurface();
        var amber = new RecordingSurface();
        using var greenChrome = new JetScreenSkinRenderer(green, null, null, null, settings, null, JetPaletteIds.Green);
        using var amberChrome = new JetScreenSkinRenderer(amber, null, null, null, settings, null, JetPaletteIds.Amber);

        var intro = Frame(1920, 1080, selected: 3);
        greenChrome.DrawIntro(intro);
        amberChrome.DrawIntro(intro);
        var hangar = Hangar(1920, 1080, developments: true, hover: HangarAction.Loadout);
        greenChrome.DrawHangar(hangar);
        amberChrome.DrawHangar(hangar);

        Assert.Equal(green.Controls, amber.Controls);
        Assert.Equal(green.Fills, amber.Fills);
        Assert.Equal(13, green.Controls.Count(c => c.Family == ScreenActions.Intro));
        Assert.Equal(10, green.Controls.Count(c => c.Family == ScreenActions.Hangar));
    }

    [Fact]
    public void Jet_screen_draw_allocates_nothing_per_frame()
    {
        var settings = new HudSettings { Skin = HudSkinIds.Jet };
        var surface = new NullSurface();
        using var chrome = new JetScreenSkinRenderer(surface, null, null, null, settings, null);
        var intro = Frame(1920, 1080, selected: 5);
        var hangar = Hangar(1920, 1080, developments: true, hover: HangarAction.Launch);
        for (int i = 0; i < 8; i++) { chrome.DrawIntro(intro); chrome.DrawHangar(hangar); }  // warm statics
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 240; i++) { chrome.DrawIntro(intro); chrome.DrawHangar(hangar); }
        long used = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, used);
    }

    private sealed class StubScreen(string id) : IScreenSkinRenderer
    {
        public string Id => id;
        public bool HangarArtAvailable => false;
        public void DrawIntro(in IntroMainFrame frame) { }
        public void DrawHangar(in HangarScreenFrame frame) { }
        public void DrawLoadout(in LoadoutScreenFrame frame) { }
        public void DrawIntroView(in IntroViewFrame frame) { }
        public void DrawCareer(in CareerScreenFrame frame) { }
        public void Dispose() { }
    }

    /// <summary>A device-free Jet surface for geometry/allocation tests: measures a
    /// deterministic 8 px-per-character advance and records control boxes.</summary>
    private sealed class RecordingSurface : IJetVectorSurface, IScreenRectAudit
    {
        public readonly List<(string Family, string Id, float X, float Y, float W, float H)> Controls = new();
        public readonly List<(float X, float Y, float W, float H)> Fills = new();
        public void Control(string family, string id, RectangleF drawn)
            => Controls.Add((family, id, drawn.X, drawn.Y, drawn.Width, drawn.Height));
        public void Plate(string family, string id, RectangleF box) { }
        public void Content(string family, string id, RectangleF box, string text) => Contents.Add((id, text, box));
        public readonly List<(string Id, string Text, RectangleF Box)> Contents = new();
        public void Begin() { }
        public void End() { }
        public void Line(Vector2 a, Vector2 b, float width, Color color) { }
        public void Fill(Vector2 min, Vector2 size, Color color) => Fills.Add((min.X, min.Y, size.X, size.Y));
        public void Text(string text, Vector2 position, bool large, float scale, Color color) { }
        public Vector2 Measure(string text, bool large, float scale)
            => new(text.Length * 8f * scale, 18f * scale);
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
}
