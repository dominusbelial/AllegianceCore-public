using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.MonoGame.Diagnostics;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Rendering.Jet;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.MonoGame.Tests.Skins;

/// <summary>
/// P1041 — Jet story 7/9: the amber variant (P972 slice 3E). Pins
/// <list type="bullet">
/// <item>the <c>jet.amber.skin.json</c> variant: same skin/renderer id, full palette,
/// kept OUT of the installed-skin list (so the F-key never cycles it);</item>
/// <item>the persisted <see cref="HudSettings.JetPalette"/> (<c>green</c>/<c>amber</c>,
/// unknown → green) separate from the skin id, and the Options row rule;</item>
/// <item>the atomic palette swap (<see cref="JetPaletteSwitcher"/>) and the toast text;</item>
/// <item>the training preference restore including the palette;</item>
/// <item>the colour inventory: every Jet scene draws only the variant's palette (plus
/// the stated native side-identity colour), in BOTH variants, with identical geometry.</item>
/// </list>
/// <para>Headless: drawing goes through a recording <see cref="IJetVectorSurface"/>, so no
/// graphics device is needed; the alarms/layout classes are the proof tool's job
/// (<c>tools/hud2-proof --jet</c>).</para>
/// </summary>
[Collection("Training")]
public sealed class JetAmberPaletteTests(ITestOutputHelper output)
{
    private static readonly HudSkinDefinition GreenDefinition =
        HudSkinResolver.Catalog.TryGet(HudSkinIds.Jet)!;
    private static readonly HudSkinDefinition AmberDefinition =
        HudSkinResolver.Catalog.TryGetVariant(HudSkinIds.Jet, JetPaletteIds.Amber)!;

    // ── the variant definition ────────────────────────────────────────────
    [Fact]
    public void Amber_is_a_palette_variant_of_the_jet_skin_not_a_fourth_skin()
    {
        Assert.NotNull(AmberDefinition);
        // It shares the skin's renderer/layout id (P972 §9 3E) and is a variant token.
        Assert.Equal(HudSkinIds.Jet, AmberDefinition.Id);
        Assert.Equal(JetPaletteIds.Amber, AmberDefinition.Variant);
        Assert.Equal(3, new HudSkinCatalog().Installed.Count);
        Assert.Null(new HudSkinCatalog().TryGet(HudSkinIds.Jet)!.Variant);
        Assert.Null(new HudSkinCatalog().TryGetVariant(HudSkinIds.Jet, JetPaletteIds.Green));
        Assert.Equal(AmberDefinition.Id, new HudSkinCatalog().TryGetVariant(HudSkinIds.Jet, "AMBER")!.Id);
    }

    [Fact]
    public void Amber_swaps_the_entire_palette_with_the_proposed_p972_values()
    {
        HudSkinPalette green = GreenDefinition.Palette!;
        HudSkinPalette amber = AmberDefinition.Palette!;
        Assert.Equal("#79FFC0", green.Primary);
        Assert.Equal("#3DA87B", green.Dim);
        Assert.Equal("#C9FFE3", green.Hot);
        // Every role moves — the amber variant is not an alert-only swap (P972 §4.1).
        Assert.Equal("#FFD280", amber.Primary);
        Assert.Equal("#B98A45", amber.Dim);
        Assert.Equal("#FFF0C2", amber.Hot);
        Assert.Equal("#120F08", amber.Plate);
        Assert.Equal("#0B0908", amber.Keyline);
        Assert.NotEqual(green.Primary, amber.Primary);
        Assert.NotEqual(green.Dim, amber.Dim);
        Assert.NotEqual(green.Hot, amber.Hot);
        Assert.NotEqual(green.Plate, amber.Plate);
        Assert.NotEqual(green.Keyline, amber.Keyline);
    }

    [Fact]
    public void A_variant_without_a_palette_or_with_a_raw_token_is_rejected_in_full()
    {
        var catalog = new HudSkinCatalog();
        Assert.False(catalog.TryAdd(
            "{ \"Schema\": \"hud-skin-1\", \"Id\": \"jet\", \"Name\": \"bad\", \"Version\": \"1\", " +
            "\"Variant\": \"amber\", \"Defaults\": {\"SelectionBracket\":true,\"RadarDeclutter\":true,\"Level\":\"Tactical\"} }"));
        Assert.False(catalog.TryAdd(
            "{ \"Schema\": \"hud-skin-1\", \"Id\": \"jet\", \"Name\": \"bad\", \"Version\": \"1\", " +
            "\"Variant\": \" Amber \", \"Defaults\": {\"SelectionBracket\":true,\"RadarDeclutter\":true,\"Level\":\"Tactical\" }, " +
            "\"Palette\": {\"Primary\":\"#FFD280\",\"Dim\":\"#B98A45\",\"Hot\":\"#FFF0C2\",\"Plate\":\"#120F08\",\"Keyline\":\"#0B0908\"} }"));
    }

    // ── the persisted variant ─────────────────────────────────────────────
    [Theory]
    [InlineData(null, JetPaletteIds.Green)]
    [InlineData("", JetPaletteIds.Green)]
    [InlineData("  ", JetPaletteIds.Green)]
    [InlineData("amber", JetPaletteIds.Amber)]
    [InlineData("AMBER", JetPaletteIds.Amber)]
    [InlineData(" Green ", JetPaletteIds.Green)]
    [InlineData("purple", JetPaletteIds.Green)]
    [InlineData("0", JetPaletteIds.Green)]
    public void Palette_ids_normalize_with_green_as_the_fallback(string? raw, string expected)
        => Assert.Equal(expected, JetPaletteIds.Normalize(raw));

    [Fact]
    public void Palette_toggles_between_the_two_variants()
    {
        Assert.Equal(JetPaletteIds.Amber, JetPaletteIds.Next(JetPaletteIds.Green));
        Assert.Equal(JetPaletteIds.Green, JetPaletteIds.Next(JetPaletteIds.Amber));
        Assert.Equal(JetPaletteIds.Amber, JetPaletteIds.Next("nonsense"));
    }

    [Fact]
    public void The_palette_is_persisted_separately_from_the_skin_and_round_trips()
    {
        string path = TempHudPath();
        var settings = new HudSettings { Skin = HudSkinIds.Jet, JetPalette = JetPaletteIds.Amber };
        settings.Save(path);
        var loaded = HudSettings.Load(path, existingPlayer: true);
        Assert.Equal(HudSkinIds.Jet, loaded.Skin);
        Assert.Equal(JetPaletteIds.Amber, loaded.JetPalette);
        // The skin id is untouched by the variant, and vice versa.
        loaded.Skin = HudSkinIds.Modern;
        Assert.Equal(JetPaletteIds.Amber, loaded.JetPalette);
    }

    [Fact]
    public void A_missing_or_unknown_persisted_palette_falls_back_to_green()
    {
        string missing = TempHudPath();
        File.WriteAllText(missing, "{ \"Skin\": \"jet\" }");
        Assert.Equal(JetPaletteIds.Green, HudSettings.Load(missing, true).JetPalette);

        string unknown = TempHudPath();
        File.WriteAllText(unknown, "{ \"Skin\": \"jet\", \"JetPalette\": \"violet\" }");
        Assert.Equal(JetPaletteIds.Green, HudSettings.Load(unknown, true).JetPalette);

        // P1041 — an unknown SKIN still falls back to Modern (the existing rule) and the
        // palette keeps its stored value (variants do not follow the skin fallback).
        string both = TempHudPath();
        File.WriteAllText(both, "{ \"Skin\": \"no-such-skin\", \"JetPalette\": \"amber\" }");
        var fallback = HudSettings.Load(both, true);
        Assert.Equal(HudSkinIds.Modern, fallback.Skin);
        Assert.Equal(JetPaletteIds.Amber, fallback.JetPalette);
    }

    [Fact]
    public void The_switch_toast_names_the_palette_for_jet_and_the_plain_skin_elsewhere()
    {
        Assert.Equal("SKIN: Jet / amber", HudSkinNotice.Format("Jet", JetPaletteIds.Amber));
        Assert.Equal("SKIN: Jet / green", HudSkinNotice.Format("Jet", JetPaletteIds.Green));
        Assert.Equal("SKIN: Modern", HudSkinNotice.Format("Modern", null));
        Assert.Equal("SKIN: Classic", HudSkinNotice.Format("Classic", null));
    }

    [Fact]
    public void The_options_variant_row_exists_only_for_an_active_gated_jet_skin()
    {
        Assert.True(JetPaletteRow.Visible(HudSkinIds.Jet, jetEnabled: true));
        // hud.jetSkin off: the selector is HIDDEN (not a disabled row); the stored value stays.
        Assert.False(JetPaletteRow.Visible(HudSkinIds.Jet, jetEnabled: false));
        Assert.False(JetPaletteRow.Visible(HudSkinIds.Classic, jetEnabled: true));
        Assert.False(JetPaletteRow.Visible(HudSkinIds.Modern, jetEnabled: true));
        Assert.Equal("Jet palette: green", JetPaletteRow.Label(null));
        Assert.Equal("Jet palette: amber", JetPaletteRow.Label(JetPaletteIds.Amber));
    }

    // ── the atomic swap ───────────────────────────────────────────────────
    [Fact]
    public void A_palette_request_is_published_only_at_the_boundary_and_persists()
    {
        string path = TempHudPath();
        var settings = new HudSettings { Skin = HudSkinIds.Jet, JetPalette = JetPaletteIds.Green };
        using var palettes = new JetPaletteSwitcher(settings,
            new(GreenDefinition, new Stub(HudSkinIds.Jet)), new(AmberDefinition, new Stub(HudSkinIds.Jet)));

        Assert.Equal(JetPaletteIds.Green, palettes.Active.Id);
        Assert.True(palettes.Request(JetPaletteIds.Amber));
        // Queued, NOT published: the frame still draws the green palette.
        Assert.Equal(JetPaletteIds.Green, palettes.Active.Id);
        Assert.Equal(JetPaletteIds.Green, settings.JetPalette);

        Assert.True(palettes.ApplyPending());
        Assert.Equal(JetPaletteIds.Amber, palettes.Active.Id);
        Assert.Equal(JetPaletteIds.Amber, settings.JetPalette);
        // Applying again is a no-op (no second toast, no second save).
        Assert.False(palettes.ApplyPending());

        settings.Save(path);
        Assert.Equal(JetPaletteIds.Amber, HudSettings.Load(path, true).JetPalette);

        // Unknown variants are refused and leave the active choice intact.
        Assert.False(palettes.Request("violet"));
        Assert.False(palettes.ApplyPending());
        Assert.Equal(JetPaletteIds.Amber, palettes.Active.Id);
    }

    [Fact]
    public void The_f_key_skin_cycle_never_cycles_the_palette()
    {
        var settings = new HudSettings { Skin = HudSkinIds.Jet, JetPalette = JetPaletteIds.Amber };
        using var palettes = new JetPaletteSwitcher(settings,
            new(GreenDefinition, new Stub(HudSkinIds.Jet)), new(AmberDefinition, new Stub(HudSkinIds.Jet)));
        using var skins = new HudSkinSwitcher(settings, new Stub(HudSkinIds.Classic), new Stub(HudSkinIds.Modern),
            new JetPaletteRenderer(palettes), jetEnabled: () => true);

        Assert.Equal(HudSkinIds.Jet, skins.Active.Renderer.Id);
        skins.Cycle();                                  // Jet → Classic
        Assert.True(skins.ApplyPending());
        Assert.Equal(HudSkinIds.Classic, skins.Active.Renderer.Id);
        Assert.Equal(JetPaletteIds.Amber, palettes.Active.Id);     // palette untouched
        Assert.Equal(JetPaletteIds.Amber, settings.JetPalette);

        skins.Cycle(); skins.Cycle();                   // Classic → Modern → Jet
        Assert.True(skins.ApplyPending());
        Assert.Equal(HudSkinIds.Jet, skins.Active.Renderer.Id);
        Assert.Equal(JetPaletteIds.Amber, palettes.Active.Id);     // still the chosen variant
    }

    [Fact]
    public void The_tag_names_the_palette_and_the_variant_is_what_the_toast_reports()
    {
        using var green = Jet(new Recorder(), GreenDefinition);
        using var amber = Jet(new Recorder(), AmberDefinition);
        Assert.Equal(JetPaletteIds.Green, green.Palette);
        Assert.Equal(JetHudRenderer.TagText, green.TagLabel);
        Assert.Equal(JetPaletteIds.Amber, amber.Palette);
        Assert.Equal(JetHudRenderer.TagTextAmber, amber.TagLabel);
        Assert.Equal("JET / AMBER - WIP", amber.TagLabel);
    }

    // ── colour inventory + identical geometry ─────────────────────────────
    /// <summary>
    /// P1041 — every Jet scene draws only its palette (the five roles, their documented
    /// alpha steps, the Dim→Hot lead ramp) plus the one stated fixed colour: the native
    /// side-identity colour of the console/tips (consoledata.cpp:99-107, dialog.mdl:26,4722).
    /// The converse holds for green: no amber-only colour appears either.
    /// </summary>
    [Fact]
    public void Every_jet_scene_uses_only_the_active_palette_in_both_variants()
    {
        int tagged = 0;
        foreach ((string name, HudModel model) in Scenes())
        {
            var green = new Recorder();
            var amber = new Recorder();
            using (var jet = Jet(green, GreenDefinition)) Draw(jet, model);
            using (var jet = Jet(amber, AmberDefinition)) Draw(jet, model);

            Color? side = SideColour(model);
            int greenOff = green.Colours.Count(c => !PaletteAllows(GreenDefinition.Palette!, c, side));
            int amberOff = amber.Colours.Count(c => !PaletteAllows(AmberDefinition.Palette!, c, side));
            Assert.True(greenOff == 0, $"{name}: {greenOff} green off-palette colour(s): "
                + string.Join(", ", green.Colours.Where(c => !PaletteAllows(GreenDefinition.Palette!, c, side)).Take(4)));
            Assert.True(amberOff == 0, $"{name}: {amberOff} amber off-palette colour(s): "
                + string.Join(", ", amber.Colours.Where(c => !PaletteAllows(AmberDefinition.Palette!, c, side)).Take(4)));
            // The tag itself (when the scene draws one): green draws only the green
            // label, amber only the amber one.
            if (green.Texts.Contains(JetHudRenderer.TagText) || amber.Texts.Contains(JetHudRenderer.TagText))
                tagged++;
            Assert.DoesNotContain(JetHudRenderer.TagTextAmber, green.Texts);
            Assert.DoesNotContain(JetHudRenderer.TagText, amber.Texts);
        }
    }

    [Fact]
    public void Amber_keeps_every_measured_box_identical_to_green()
    {
        foreach ((string name, HudModel model) in Scenes())
        {
            var green = new Recorder();
            var amber = new Recorder();
            using (var jet = Jet(green, GreenDefinition)) Draw(jet, model);
            using (var jet = Jet(amber, AmberDefinition)) Draw(jet, model);
            string[] g = green.Geometry(excludeTag: true);
            string[] a = amber.Geometry(excludeTag: true);
            Assert.Equal(g.Length, a.Length);
            for (int i = 0; i < g.Length; i++)
                Assert.True(string.Equals(g[i], a[i], StringComparison.Ordinal),
                    $"{name}: geometry line {i} differs: '{g[i]}' vs '{a[i]}'");
            // The tag is the ONE stated difference (its text names the variant).
            output.WriteLine($"{name}: {g.Length} geometry lines identical; "
                + $"tag green '{green.TagRun()}' amber '{amber.TagRun()}'");
        }
    }

    /// <summary>
    /// P1041 — the amber renderer is a second preloaded instance, so drawing it must cost
    /// the same zero managed bytes per frame the green one does (the smoke gate's 0 B/frame
    /// in BOTH palettes). Same shape as the P1040 console allocation test: warm the caches,
    /// then measure a steady window.
    /// </summary>
    [Fact]
    public void Amber_draw_allocates_zero_bytes_per_frame()
    {
        long bytes = MeasureAmberDraw();
        Assert.Equal(0, bytes);
    }

    [Theory]
    [InlineData(JetAllocationWindow.LargestCycle)]
    [InlineData(JetAllocationWindow.MeasuredFrames - 1)]
    public void Amber_window_catches_a_periodic_allocation(int period)
        => Assert.True(MeasureAmberDraw(period) > 0);

    private static object? _probeSink;

    private long MeasureAmberDraw(int allocationPeriod = 0)
    {
        var surface = new Silent();
        using var jet = Jet(surface, AmberDefinition);
        HudModel model = HudModelAdapter.Create(Frame(), CameraMode.Cockpit,
            velocityMarker: new Vector2(940, 520), flightReference: Reference(0f, 0f, 45f),
            chat: Chat(composing: true, NativeComposeState.Chat), console: Console(vote: true),
            contacts: Contacts(), missionClockSeconds: 125,
            target: new TargetSelectionView(new(1160, 362), false, "Enemy fighter", .6f, .3f, true,
                Distance: 1250f, ClosingSpeed: 43f, SideColor: new HudColor(1f, 0f, 0f), ScreenRadius: 40f),
            lockBracket: new LockBracketView(new(1160, 362), MissileLockFeedback.LockState.Partial, 1.0, .62f, 40f),
            lead: new LeadIndicatorView(new(1200, 380), new(1160, 362), true, .8f, 1.0, Granted: true),
            targetPane: Pane("Enemy fighter"), selectionBracket: true);
        var frame = new HudDrawFrame(new Viewport(0, 0, 1920, 1080), null, false);
        int frameNumber = 0;
        void Draw(int count)
        {
            for (int i = 0; i < count; i++)
            {
                jet.Draw(model, frame);
                jet.DrawCommand(model);
                jet.DrawConsole(model);
                frameNumber++;
                if (allocationPeriod > 0 && frameNumber % allocationPeriod == 0)
                    _probeSink = new byte[16];
            }
        }
        // P1049b — fixed inputs repeat a one-frame steady state; cache capacity
        // is not a frame cycle. Shared window and source audit: JetAllocationWindow.
        Draw(JetAllocationWindow.WarmupFrames);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Draw(JetAllocationWindow.MeasuredFrames);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"P1041 Jet amber: {bytes} B / {JetAllocationWindow.MeasuredFrames} frames = {bytes / (double)JetAllocationWindow.MeasuredFrames} B/frame");
        return bytes;
    }

    // ── training preference restore ───────────────────────────────────────
    [Fact]
    public void A_lesson_started_under_amber_restores_amber_after_a_switch_inside_it()
    {
        using var h = new PaletteTrainingHarness(HudSkinIds.Jet, JetPaletteIds.Amber);
        Assert.Equal(JetPaletteIds.Amber, h.Palettes.Active.Id);
        h.Call("SaveTrainingPreferences");

        // Inside the lesson the pilot picks the green variant and cycles the skin.
        h.Settings.JetPalette = JetPaletteIds.Green;
        h.Call("CommitHudSkinChange");
        Assert.Equal(JetPaletteIds.Green, h.Palettes.Active.Id);
        h.Switcher.Cycle();
        h.Call("CommitHudSkinChange");
        Assert.NotEqual(HudSkinIds.Jet, h.Switcher.Active.Renderer.Id);

        h.Call("ReleaseTraining");
        Assert.Equal(HudSkinIds.Jet, h.Switcher.Active.Renderer.Id);
        Assert.Equal(JetPaletteIds.Amber, h.Palettes.Active.Id);
        Assert.Equal(JetPaletteIds.Amber, h.Settings.JetPalette);
        Assert.Equal(JetPaletteIds.Amber, HudSettings.Load(h.HudPath, true).JetPalette);
    }

    [Fact]
    public void The_palette_is_restored_when_the_flag_stays_on_but_the_lesson_switched_it()
    {
        using var h = new PaletteTrainingHarness(HudSkinIds.Jet, JetPaletteIds.Green);
        h.Call("SaveTrainingPreferences");
        h.Settings.JetPalette = JetPaletteIds.Amber;
        h.Call("CommitHudSkinChange");
        Assert.Equal(JetPaletteIds.Amber, h.Palettes.Active.Id);

        h.Call("ReleaseTraining");
        Assert.Equal(JetPaletteIds.Green, h.Palettes.Active.Id);
        Assert.Equal(JetPaletteIds.Green, h.Settings.JetPalette);
    }

    // ── fixtures ──────────────────────────────────────────────────────────
    private static JetHudRenderer Jet(IJetVectorSurface surface, HudSkinDefinition definition)
        => new(surface, new HudSettings { Skin = HudSkinIds.Jet }, NoopResources, definition);

    private static readonly HudSkinResources NoopResources =
        new(_ => { }, (_, _) => { }, _ => { }, (_, _) => { }, _ => { }, _ => { }, _ => { });

    private static void Draw(JetHudRenderer jet, HudModel model)
    {
        var frame = new HudDrawFrame(new Viewport(0, 0, 1920, 1080), null, false);
        jet.Draw(model, frame);
        jet.DrawCommand(model);
        jet.DrawConsole(model);
    }

    /// <summary>The stated fixed colour: the scene's native side-identity colour, built the
    /// same way the renderer builds it from the shared console model.</summary>
    private static Color? SideColour(HudModel model)
        => model.Context.Console?.SideColor is { } sc ? new Color(sc.R, sc.G, sc.B, sc.A) : null;

    /// <summary>The tool's allowed set (tools/hud2-proof Program.cs PaletteAllows): the
    /// palette roles, their documented alpha steps, the Dim→Hot ramp, and the side colour.</summary>
    private static bool PaletteAllows(HudSkinPalette palette, Color c, Color? side)
    {
        foreach (string hex in new[] { palette.Primary, palette.Dim, palette.Hot, palette.Plate, palette.Keyline })
        {
            if (!HudSkinColor.TryParseHex(hex, out var role)) continue;
            if (Near(c, role.R, role.G, role.B)) return true;
            if (ShadeOf(c, role)) return true;
        }
        if (HudSkinColor.TryParseHex(palette.Dim, out var dim) && HudSkinColor.TryParseHex(palette.Hot, out var hot))
            for (int i = 0; i <= 255; i++)
            {
                float t = i / 255f;
                if (Near(c, (int)(dim.R + (hot.R - dim.R) * t), (int)(dim.G + (hot.G - dim.G) * t),
                    (int)(dim.B + (hot.B - dim.B) * t))) return true;
            }
        if (side is { } s)
        {
            if (Near(c, s.R, s.G, s.B)) return true;
            if (ShadeOf(c, new HudSkinColor(s.R, s.G, s.B))) return true;
        }
        return false;
    }

    /// <summary>A brightness step of a role: <c>Color * f</c>, f in [0.24, 1.02] (plate
    /// backings, radar ring/leader/label steps, unlit bars, tip rows and the continuous
    /// radar range fade).</summary>
    private static bool ShadeOf(Color c, HudSkinColor role)
    {
        int max = Math.Max(role.R, Math.Max(role.G, role.B));
        if (max == 0) return c.R == 0 && c.G == 0 && c.B == 0;
        float f = 0f;
        int count = 0;
        if (role.R == max) { f += c.R / (float)role.R; count++; }
        if (role.G == max) { f += c.G / (float)role.G; count++; }
        if (role.B == max) { f += c.B / (float)role.B; count++; }
        if (count == 0) return false;
        f /= count;
        if (f < 0.24f || f > 1.02f) return false;
        return Math.Abs(c.R - role.R * f) <= 2 && Math.Abs(c.G - role.G * f) <= 2
            && Math.Abs(c.B - role.B * f) <= 2;
    }

    private static bool Near(Color c, int r, int g, int b) =>
        Math.Abs(c.R - r) <= 1 && Math.Abs(c.G - g) <= 1 && Math.Abs(c.B - b) <= 1;

    private static HudFlightReference Reference(float pitch, float roll, float headingDegrees)
    {
        float heading = headingDegrees * MathF.PI / 180f;
        float p = pitch * MathF.PI / 180f;
        float r = roll * MathF.PI / 180f;
        var forward = new Allegiance.Core.Collision.Vector3(
            MathF.Cos(heading) * MathF.Cos(p), MathF.Sin(heading) * MathF.Cos(p), MathF.Sin(p));
        var levelUp = new Allegiance.Core.Collision.Vector3(
            -MathF.Cos(heading) * MathF.Sin(p), -MathF.Sin(heading) * MathF.Sin(p), MathF.Cos(p));
        var orientation = new Orientation();
        orientation.Set(forward, levelUp);
        if (r != 0f) orientation.PostRotate(forward, r);
        return HudFlightReference.FromSim(orientation, forward * 260f, HudFlightReference.SectorUp);
    }

    private static HudFrame Frame(float speed = 260f, float throttle = .6f, float hull = .8f,
        float shield = .6f, float energy = .4f, float fuel = .3f, float ammo = .2f) => new(
        hull, shield, energy, fuel, ammo, 1f, speed, throttle, false, false, true, 0,
        false, 0f, false, false, 0f, 0f, Array.Empty<WeaponStatusRow>(), false, 0);

    private static readonly HudColor SideInk = new(.07f, .95f, .84f);

    private static HudChat Chat(bool composing = false, NativeComposeState state = NativeComposeState.NotComposing)
        => new(new[] { "[TEAM] Pilot: defend the base", "[SECTOR] Miner: ore found" }, 0, "returning",
            composing, 1, "[TEAM] ", null, state, 0f, SideInk,
            new HudColor?[] { SideInk, SideInk }, new[] { true, false }, ChatPaneLayout.DefaultLines);

    private static HudConsoleText Console(float x = 0f, bool tips = true, bool vote = false) => new()
    {
        SectorName = "Alystra", CarrierName = "Pilot Alpha", BoardState = 0,
        AcceptedOrder = "defend Garrison", QueuedOrder = "attack Enemy fighter",
        VoteText = vote ? "Vote: Mutiny against the commander? (Y/N)" : null,
        OrdinaryTips = tips ? new[] { "Welcome to the game", "Alystra: 3 miners", "Payday: $500" } : Array.Empty<string>(),
        CriticalTips = new[] { new HudNotice("Reloading ammo...", true, 1) },
        SideColor = SideInk,
    };

    private static HudTargetPane Pane(string name) => new(
        Present: true, Name: name, TypeDescription: "Fighter", SideName: "Rixian", SideColor: new HudColor(1f, 0f, 0f),
        HullFraction: .6f, ShieldFraction: .3f, Distance: 1250f, Speed: 60f, OreFraction: null,
        KillBonusPercent: 25f, InRange: true, IsShip: true, SectorName: null, TimeToIntercept: "4.2",
        ClosingSpeed: 43f);

    private static HudContact[] Contacts()
    {
        var list = new List<HudContact>(12);
        for (int i = 0; i < 8; i++)
            list.Add(new(HudContactKind.Ship, new HudPoint(600 + i * 30, 400 + i * 12), true, 1f, SideInk,
                i == 0, false, Name: "Fighter " + i.ToString(CultureInfo.InvariantCulture), Range: 800 + i * 120,
                IsEnemy: i % 3 == 0, IsSameSide: i % 3 != 0, Threat: i == 0, ScreenRadius: 30f));
        list.Add(new(HudContactKind.Station, new HudPoint(520, 460), true, 1f, SideInk, false, true,
            Name: "Garrison", Range: 2400, ScreenRadius: 40f));
        list.Add(new(HudContactKind.Asteroid, new HudPoint(700, 360), true, .8f, null, false, false,
            Name: "asteroid 200", Range: 900, Fill: .45f, ScreenRadius: 25f));
        list.Add(new(HudContactKind.Missile, new HudPoint(660, 420), true, 1f, null, false, false,
            Range: 300, Threat: true, ThreatFlash: true, ScreenRadius: 20f));
        list.Add(new(HudContactKind.Treasure, new HudPoint(740, 440), true, 1f, null, false, false,
            Name: "He3", Range: 500, IsTech: true, TreasureUseful: true, ScreenRadius: 20f));
        return list.ToArray();
    }

    private static IEnumerable<(string Name, HudModel Model)> Scenes()
    {
        yield return ("level-flight", HudModelAdapter.Create(Frame(), CameraMode.Cockpit,
            velocityMarker: new Vector2(940, 520), flightReference: Reference(0f, 0f, 45f),
            orderArrows: new(new(.4f, 0f), new(-.4f, 0f), false)));
        yield return ("pitched-banked", HudModelAdapter.Create(Frame(210f, .8f), CameraMode.Cockpit,
            velocityMarker: new(1000, 480), flightReference: Reference(18f, -25f, 210f)));
        yield return ("locked-target", HudModelAdapter.Create(Frame(), CameraMode.Cockpit,
            flightReference: Reference(0f, 0f, 45f),
            target: new TargetSelectionView(new(1160, 362), false, "Enemy fighter", .6f, .3f, true,
                Distance: 1250f, ClosingSpeed: 43f, SideColor: new HudColor(1f, 0f, 0f), ScreenRadius: 40f),
            lockBracket: new LockBracketView(new(1160, 362), MissileLockFeedback.LockState.Partial, 1.0, .62f, 40f),
            lead: new LeadIndicatorView(new(1200, 380), new(1160, 362), true, .8f, 1.0, Granted: true),
            targetPane: Pane("Enemy fighter"), selectionBracket: true));
        yield return ("dense-radar", HudModelAdapter.Create(Frame(), CameraMode.Cockpit,
            contacts: Contacts(), flightReference: Reference(0f, 0f, 45f), selectionBracket: true));
        yield return ("caution-tips-vote", HudModelAdapter.Create(Frame(hull: .1f, shield: .05f, energy: .08f), CameraMode.Cockpit,
            console: Console(vote: true), flightReference: Reference(0f, 0f, 45f),
            notices: new[] { new HudNotice("TRAP: hull critical", true, .5f) }));
        yield return ("console-chat", HudModelAdapter.Create(Frame(), CameraMode.Cockpit,
            chat: Chat(composing: true, NativeComposeState.Chat), console: Console(),
            flightReference: Reference(0f, 0f, 45f), missionClockSeconds: 125));
        yield return ("command-console", HudModelAdapter.Create(Frame(), CameraMode.Command, commandView: true,
            commandPaneSlide: 1f, commandWing: 1, commandRecipientColumn: 2, commandVerb: 1,
            commandLines: new[] { "COMMAND SHIP: Scout" }, chat: Chat(), console: Console(),
            flightReference: Reference(0f, 0f, 45f)));
        yield return ("lifepod", HudModelAdapter.Create(
            Frame(60f, 0f) with { IsAdriftLifepod = true, OxygenSecondsRemaining = 95 }, CameraMode.Cockpit,
            console: Console(tips: false), flightReference: Reference(0f, 0f, 45f)));
        yield return ("ghost", HudModelAdapter.Create(Frame(), CameraMode.Cockpit, ghost: true,
            console: Console(), flightReference: Reference(0f, 0f, 45f)));
        yield return ("turret", HudModelAdapter.Create(Frame(0f, -1f), CameraMode.ExternalTurret, turret: true,
            console: Console(), flightReference: Reference(-20f, 0f, 45f)));
        yield return ("training-callout", HudModelAdapter.Create(Frame(), CameraMode.Cockpit,
            trainingOverlayId: 17, chat: Chat(), console: Console(), flightReference: Reference(0f, 0f, 45f)));
        yield return ("docked", HudModelAdapter.Create(Frame(), CameraMode.Cockpit, docked: true,
            console: Console(), flightReference: Reference(0f, 0f, 45f)));
        yield return ("hidden", HudModelAdapter.Create(Frame(), CameraMode.Cockpit, hidden: true,
            console: Console(), flightReference: Reference(0f, 0f, 45f)));
    }

    private static string TempHudPath()
    {
        string directory = Path.Combine(Path.GetTempPath(), "p1041-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "hud.json");
    }

    /// <summary>Records every colour and the geometry of each submission (the palette and
    /// identical-box audits), merging the keyline/ink double-submission of a run the way the
    /// proof's recorder does.</summary>
    private sealed class Recorder : IJetVectorSurface
    {
        public readonly List<Color> Colours = new();
        public readonly HashSet<string> Texts = new(StringComparer.Ordinal);
        private readonly List<string> _geometry = new();
        private readonly List<(string Text, float X, float Y, float W, float H)> _runs = new();

        public void Begin() { }
        public void End() { }

        public void Line(Vector2 a, Vector2 b, float width, Color color)
        {
            Colours.Add(color);
            _geometry.Add($"S|{F(a.X)},{F(a.Y)},{F(b.X)},{F(b.Y)},{F(width)}");
        }

        public void Fill(Vector2 min, Vector2 size, Color color)
        {
            Colours.Add(color);
            _geometry.Add($"F|{F(min.X)},{F(min.Y)},{F(size.X)},{F(size.Y)}");
        }

        public void Text(string text, Vector2 position, bool large, float scale, Color color)
        {
            Vector2 size = Measure(text, large, scale);
            Colours.Add(color);
            Texts.Add(text);
            for (int i = _runs.Count - 1; i >= 0; i--)
            {
                var run = _runs[i];
                if (run.Text == text && MathF.Abs(run.X - position.X) <= 2.5f && MathF.Abs(run.Y - position.Y) <= 2.5f)
                { _runs[i] = (text, position.X, position.Y, size.X, size.Y); return; }
            }
            _runs.Add((text, position.X, position.Y, size.X, size.Y));
        }

        public Vector2 Measure(string text, bool large, float scale)
            => new(text.Length * (large ? 8f : 6f) * scale, (large ? 16f : 12f) * scale);

        /// <summary>Text geometry plus the recorded strokes/fills in submission order. The tag
        /// run is excluded because item 5 makes its label (and only its label) differ.</summary>
        public string[] Geometry(bool excludeTag)
        {
            var lines = new List<string>(_geometry);
            foreach (var run in _runs)
            {
                if (excludeTag && (run.Text == JetHudRenderer.TagText || run.Text == JetHudRenderer.TagTextAmber)) continue;
                lines.Add($"T|{run.Text}|{F(run.X)},{F(run.Y)},{F(run.W)},{F(run.H)}");
            }
            return lines.ToArray();
        }

        public string TagRun()
        {
            foreach (var run in _runs)
                if (run.Text == JetHudRenderer.TagText || run.Text == JetHudRenderer.TagTextAmber)
                    return $"{run.Text}|{F(run.X)},{F(run.Y)},{F(run.W)},{F(run.H)}";
            return "";
        }

        private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>Allocation-transparent surface for the 0 B/frame gate.</summary>
    private sealed class Silent : IJetVectorSurface
    {
        public void Begin() { }
        public void End() { }
        public void Line(Vector2 a, Vector2 b, float width, Color color) { }
        public void Fill(Vector2 min, Vector2 size, Color color) { }
        public void Text(string text, Vector2 position, bool large, float scale, Color color) { }
        public Vector2 Measure(string text, bool large, float scale)
            => new(text.Length * (large ? 8f : 6f) * scale, (large ? 16f : 12f) * scale);
    }

    /// <summary>A Game1 with both palette variants installed, so the training
    /// snapshot/restore path runs through its own code (the same shape as the P1040
    /// harness, plus <see cref="JetPaletteSwitcher"/>).</summary>
    private sealed class PaletteTrainingHarness : IDisposable
    {
        private readonly string? _oldHud = Environment.GetEnvironmentVariable("ALLEGIANCE_HUD_SETTINGS");
        private readonly string? _oldGraphics = Environment.GetEnvironmentVariable("ALLEGIANCE_GRAPHICS_SETTINGS");
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "p1041-" + Guid.NewGuid().ToString("N"));
        private readonly ConnectionSession _session = new();
        private readonly string _savedMap = KeyBindingStore.Instance.Map.ToJson();
        private readonly InputLayout _savedLayout = KeyBindingStore.Instance.Map.Layout;
        private readonly Func<string, bool>? _savedFilter = KeyBindingStore.Instance.Map.ActionFilter;
        public string HudPath => Path.Combine(_dir, "hud.json");
        public Game1 Game { get; }
        public HudSettings Settings { get; }
        public HudSkinSwitcher Switcher { get; }
        public JetPaletteSwitcher Palettes { get; }

        public PaletteTrainingHarness(string skin, string palette)
        {
            Game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
            GC.SuppressFinalize(Game);
            Settings = new() { Skin = skin, JetPalette = palette };
            Palettes = new(Settings,
                new(GreenDefinition, Jet(new Recorder(), GreenDefinition)),
                new(AmberDefinition, Jet(new Recorder(), AmberDefinition)));
            Switcher = new(Settings, new Stub(HudSkinIds.Classic), new Stub(HudSkinIds.Modern),
                new JetPaletteRenderer(Palettes), jetEnabled: () => true);
            Set("_hudSettings", Settings);
            Set("_hudSkins", Switcher);
            Set("_jetPalettes", Palettes);
            Set("_pilotProfile", new PilotInfoLayout.ProfileEditor());
            Set("_flightGc", new FlightGcLatency(false));
            Set("_upscaling", new UpscalingSettings());
            Set("_resolutionController", new DynamicResolutionController());
            var state = typeof(Game1).GetField("_state", Hidden)!;
            state.SetValue(Game, Enum.Parse(state.FieldType, "Flying"));
            Directory.CreateDirectory(_dir);
            Environment.SetEnvironmentVariable("ALLEGIANCE_HUD_SETTINGS", HudPath);
            Environment.SetEnvironmentVariable("ALLEGIANCE_GRAPHICS_SETTINGS", Path.Combine(_dir, "graphics.json"));
            Set("_session", _session);
            Set("_trainingSounds", new Dictionary<int, Microsoft.Xna.Framework.Audio.SoundEffectInstance>());
        }

        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        public void Set(string name, object? value) => typeof(Game1).GetField(name, Hidden)!.SetValue(Game, value);
        public object? Call(string name, params object[] args)
            => typeof(Game1).GetMethod(name, Hidden)!.Invoke(Game, args);

        public void Dispose()
        {
            Switcher.Dispose();
            _session.Dispose();
            KeyBindingStore.Instance.Map.SetLayout(_savedLayout);
            KeyBindingStore.Instance.Map.ResetToDefaults();
            KeyBindingStore.Instance.Map.ApplyJson(_savedMap);
            KeyBindingStore.Instance.Map.ActionFilter = _savedFilter;
            Environment.SetEnvironmentVariable("ALLEGIANCE_HUD_SETTINGS", _oldHud);
            Environment.SetEnvironmentVariable("ALLEGIANCE_GRAPHICS_SETTINGS", _oldGraphics);
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }
    }

    private sealed class Stub(string id) : IHudSkinRenderer
    {
        public string Id { get; } = id;
        public IReadOnlySet<string>? Coverage => null;
        public void Draw(HudModel model, in HudDrawFrame frame) { }
        public void DrawCommand(HudModel model) { }
        public void DrawConsole(HudModel model) { }
        public void SetRadarIcons(RadarIconTextures? icons) { }
        public Rectangle DrawOverlayText(string text, Vector2 position, Color color) => default;
        public void Dispose() { }
    }
}
