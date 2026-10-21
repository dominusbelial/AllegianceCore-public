using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// HUD-14 — the native layer-model classic centre gauges. These tests pin
/// the pure <see cref="ClassicGaugeTable"/> rules headlessly: the exact
/// <c>GaugeImageRect</c> reveal source rect per gauge at 0 / 0.5 / 1 (with
/// native integer truncation), the reveal direction and DefaultCenterHudOn
/// console offsets transcribed from dialog.mdl, the red damage-flash switch
/// condition, the % numeral flag (default OFF = native), and that the per-frame
/// gauge path (Spec lookup + RevealSource) allocates nothing.
/// </summary>
public class Hud14ClassicGaugeTests
{
    private static HudFrame Frame => new(0.8f, 0.6f, 0.4f, 0.3f, 0.2f, 1f,
        120f, -1f, false, false, true, 0, false, 0f, false, false,
        0f, 0f, System.Array.Empty<WeaponStatusRow>(), false, 0);

    // ── 1. Reveal source rect equals the native GaugeImageRect rule ────────

    [Theory]
    [InlineData(GaugeKind.Hull)] [InlineData(GaugeKind.Shield)]
    [InlineData(GaugeKind.Energy)] [InlineData(GaugeKind.Ammo)]
    [InlineData(GaugeKind.Fuel)] [InlineData(GaugeKind.Thrust)]
    public void Reveal_source_is_empty_at_zero_and_full_rect_at_one(GaugeKind kind)
    {
        var spec = ClassicGaugeTable.Spec(kind);
        var empty = ClassicGaugeTable.RevealSource(spec.SourceRect, spec.Reveal, 0f);
        var full = ClassicGaugeTable.RevealSource(spec.SourceRect, spec.Reveal, 1f);
        // value 0 → no rows revealed (native GaugeImageRect with value 0);
        // value 1 → the whole native rect band (NOT the whole 128px bitmap).
        Assert.Equal(0, empty.Height);
        Assert.Equal(spec.SourceRect, full);
        Assert.Equal(spec.SourceRect.Width, full.Width);
    }

    [Theory]
    [InlineData(GaugeKind.Hull, 40, 64, 48, 41)]    // (40,23,48,82) h=82 @0.5 → bottom 41
    [InlineData(GaugeKind.Shield, 40, 64, 48, 41)]
    [InlineData(GaugeKind.Energy, 30, 64, 69, 46)]  // (30,18,69,92) h=92 @0.5 → bottom 46
    [InlineData(GaugeKind.Ammo, 29, 64, 69, 46)]    // (29,18,69,92) h=92 @0.5 → bottom 46
    [InlineData(GaugeKind.Thrust, 15, 10, 95, 54)]  // (15,10,95,108) h=108 @0.5 → TOP 54 (JustifyTop)
    [InlineData(GaugeKind.Fuel, 17, 10, 96, 54)]    // (17,10,96,108) h=108 @0.5 → TOP 54
    public void Reveal_source_at_half_is_the_justified_half_band(GaugeKind kind,
        int expX, int expY, int expW, int expH)
    {
        var spec = ClassicGaugeTable.Spec(kind);
        var atHalf = ClassicGaugeTable.RevealSource(spec.SourceRect, spec.Reveal, 0.5f);
        Assert.Equal(new Rectangle(expX, expY, expW, expH), atHalf);
    }

    [Theory]
    [InlineData(GaugeKind.Hull, 0.099f, 8)]   // (int)(82*0.098…) → 8 (native int truncation)
    [InlineData(GaugeKind.Thrust, 0.345f, 37)] // (int)(108*0.345) = (int)37.26 = 37
    public void Reveal_source_uses_native_integer_truncation(GaugeKind kind, float value, int expH)
    {
        var spec = ClassicGaugeTable.Spec(kind);
        var src = ClassicGaugeTable.RevealSource(spec.SourceRect, spec.Reveal, value);
        Assert.Equal(expH, src.Height);
        // The revealed band sticks to the justified edge.
        if (spec.Reveal == GaugeReveal.Top) Assert.Equal(spec.SourceRect.Y, src.Y);
        else Assert.Equal(spec.SourceRect.Bottom - expH, src.Y);
    }

    [Fact]
    public void Arc_gauges_use_the_bright_fill_stem_not_the_faint_back()
    {
        // THE core fix: the old loader used GaugeTextureSource.MdlStemFor
        // ("ThrustGaugebmp" = the faint BACK) as the sole revealed texture, so
        // thrust/fuel/ammo/energy showed only the outline. Native reveals the
        // bright *2 fill (dialog.mdl:2183-2189 thrustgauge2bmp etc.); the back
        // is a separate full-value layer. Pin the split so a regression cannot
        // collapse the two back into one again.
        foreach (var kind in new[] { GaugeKind.Thrust, GaugeKind.Fuel, GaugeKind.Ammo, GaugeKind.Energy })
        {
            var spec = ClassicGaugeTable.Spec(kind);
            Assert.False(string.IsNullOrEmpty(spec.FillStem));
            Assert.NotEqual(spec.BackStem, spec.FillStem);
            Assert.EndsWith("2bmp", spec.FillStem);   // *_gauge2bmp = the bright fill
        }
        // Hull/shield reveal the green fill (hullgaugebmp), not the back (hullgauge2bmp).
        Assert.Equal("HullGaugebmp", ClassicGaugeTable.Spec(GaugeKind.Hull).FillStem);
        Assert.Equal("hullgauge2bmp", ClassicGaugeTable.Spec(GaugeKind.Hull).BackStem);
        Assert.Equal("ShieldGaugebmp", ClassicGaugeTable.Spec(GaugeKind.Shield).FillStem);
        Assert.Equal("shieldgauge2bmp", ClassicGaugeTable.Spec(GaugeKind.Shield).BackStem);
    }

    // ── 2. Reveal direction + console offsets match native dialog.mdl ──────

    [Fact]
    public void Reveal_direction_matches_native_justification()
    {
        // Native JustifyTop (dialog.mdl:2173 THRUST, :2226 FUEL); the rest
        // JustifyBottom (:2278 AMMO, :2331 ENERGY, :2404 hull, :2474 SHIELD).
        Assert.Equal(GaugeReveal.Top, ClassicGaugeTable.Spec(GaugeKind.Thrust).Reveal);
        Assert.Equal(GaugeReveal.Top, ClassicGaugeTable.Spec(GaugeKind.Fuel).Reveal);
        Assert.Equal(GaugeReveal.Bottom, ClassicGaugeTable.Spec(GaugeKind.Ammo).Reveal);
        Assert.Equal(GaugeReveal.Bottom, ClassicGaugeTable.Spec(GaugeKind.Energy).Reveal);
        Assert.Equal(GaugeReveal.Bottom, ClassicGaugeTable.Spec(GaugeKind.Hull).Reveal);
        Assert.Equal(GaugeReveal.Bottom, ClassicGaugeTable.Spec(GaugeKind.Shield).Reveal);
    }

    [Theory]
    [InlineData(GaugeKind.Thrust, -159f, -6f)]    // console (161,246): 240-246=-6 (y-up → y-down)
    [InlineData(GaugeKind.Fuel, 41f, -6f)]        // (361,246)
    [InlineData(GaugeKind.Ammo, -173f, 135f)]     // (147,105): 240-105=135
    [InlineData(GaugeKind.Energy, 52f, 135f)]     // (372,105)
    [InlineData(GaugeKind.Hull, -105f, 60f)]      // ArmorImage (215,180): 240-180=60
    [InlineData(GaugeKind.Shield, -20f, 60f)]     // ShieldImage (300,180)
    public void Console_offsets_match_DefaultCenterHudOn_centre_relative(GaugeKind kind, float x, float y)
    {
        // HUD-14b — native places each gauge in the 640x480 DefaultCenterHudOn
        // pane by a TranslateImage point measured in y-UP from the bottom-left
        // (DrawImage3D lower-left origin, engine/context.cpp:552); the pane's
        // top-left is at centre-(320,240) (dialog.mdl:5397). The centre-relative
        // offset in screen y-DOWN is X = consoleX-320, Y = 240-consoleY — so the
        // arc gauges sit ABOVE centre (y-up 246 > 240) and AMMO/ENERGY BELOW.
        var off = ClassicGaugeTable.Spec(kind).ConsoleOffset;
        Assert.Equal(new Vector2(x, y), off);
    }

    // ── 6. HUD-14b: y-up console → screen y-down placement ────────────────

    [Theory]
    [InlineData(GaugeKind.Thrust, true, false)]  // above centre, left of centre
    [InlineData(GaugeKind.Fuel, true, true)]     // above centre, right of centre
    [InlineData(GaugeKind.Ammo, false, false)]   // below centre, left of centre
    [InlineData(GaugeKind.Energy, false, true)]  // below centre, right of centre
    public void Arc_gauge_group_sits_in_its_native_quadrant(GaugeKind kind, bool above, bool right)
    {
        // HUD-14's y-down misreading put THRUST/FUEL below centre and
        // AMMO/ENERGY above (the "X" splay). With the corrected y-up console
        // offsets the groups land where native intends: THRUST/FUEL above the
        // reticle, AMMO/ENERGY below, mirrored left/right.
        var spec = ClassicGaugeTable.Spec(kind);
        var centre = new Vector2(960f, 540f);
        var anchor = ClassicGaugeLayout.Anchor(centre, spec, ClassicGaugeLayout.PaneScale);
        Assert.True(above ? anchor.Y < centre.Y : anchor.Y > centre.Y,
            $"{kind} should be {(above ? "above" : "below")} centre");
        Assert.True(right ? anchor.X > centre.X : anchor.X < centre.X,
            $"{kind} should be to the {(right ? "right" : "left")} of centre");
    }

    [Fact]
    public void Each_arc_band_rings_the_reticle_in_its_quadrant()
    {
        // The six bands' centres must ring the view centre — not splay into an
        // X. Compute each revealed band's on-screen centre (fraction 1).
        var centre = new Vector2(1920f / 2f, 1080f / 2f);
        var expected = new (GaugeKind kind, bool above, bool right)[]
        {
            (GaugeKind.Thrust, true, false),
            (GaugeKind.Fuel, true, true),
            (GaugeKind.Ammo, false, false),
            (GaugeKind.Energy, false, true),
        };
        foreach (var (kind, above, right) in expected)
        {
            var spec = ClassicGaugeTable.Spec(kind);
            var anchor = ClassicGaugeLayout.Anchor(centre, spec, ClassicGaugeLayout.PaneScale);
            var band = ClassicGaugeLayout.RevealDest(spec, 1f, anchor, ClassicGaugeLayout.PaneScale);
            var bc = new Vector2(band.Center.X, band.Center.Y);
            Assert.True(above ? bc.Y < centre.Y : bc.Y > centre.Y,
                $"{kind} band centre should be {(above ? "above" : "below")} centre");
            Assert.True(right ? bc.X > centre.X : bc.X < centre.X,
                $"{kind} band centre should be to the {(right ? "right" : "left")} of centre");
        }
    }

    // ── 7. HUD-14b: arc back band aligns with the fill band ───────────────

    [Fact]
    public void Arc_back_band_origin_matches_fill_band_origin()
    {
        // HUD-14 drew the arc back as a full 128px texture at BackPoint, so it
        // sat (15,10)·scale away from the fill band. Native draws the back as a
        // GaugeImageRect band at value 1 sharing the fill's rect and point
        // (thrustGaugeBack dialog.mdl:2175-2185 etc.), so at fraction 1 the back
        // band must land exactly on the fill band.
        // HUD-14c/HUD-19c — the back's destination comes from the BACK-DRAW
        // PATH: DrawBack draws into HudRenderer.BackDestination (guarded below),
        // asserted against the fill band and the fill's TranslateImage point.
        var drawBack = MethodSource(SourceFile("Rendering", "HudRenderer.cs"),
            "private void DrawBack(ClassicGaugeSpec spec, Vector2 anchor, float scale)");
        Assert.Contains("var dst = BackDestination(spec, anchor, scale);", drawBack);
        Assert.DoesNotContain("DrawRevealed(", drawBack);
        Assert.DoesNotContain("DrawPlain(", drawBack);
        var renderer = (HudRenderer)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(HudRenderer));
        System.GC.SuppressFinalize(renderer);
        var centre = new Vector2(960f, 540f);
        foreach (var kind in new[] { GaugeKind.Thrust, GaugeKind.Fuel, GaugeKind.Ammo, GaugeKind.Energy })
        {
            var spec = ClassicGaugeTable.Spec(kind);
            var anchor = ClassicGaugeLayout.Anchor(centre, spec, ClassicGaugeLayout.PaneScale);
            var backBand = renderer.BackDestination(spec, anchor, ClassicGaugeLayout.PaneScale);
            var fillBand = ClassicGaugeLayout.RevealDest(spec, 1f, anchor, ClassicGaugeLayout.PaneScale);
            Assert.Equal(fillBand, backBand);
            // Guard the HUD-14 regression itself: the back must be drawn as the
            // native Rect band (width x band-height), NOT stamped as the whole
            // 128px texture (which sat (15,10)·scale away and oversized the art).
            Assert.Equal((int)MathF.Round(spec.SourceRect.Width * ClassicGaugeLayout.PaneScale), backBand.Width);
            Assert.Equal((int)MathF.Round(spec.SourceRect.Height * ClassicGaugeLayout.PaneScale), backBand.Height);
            // The band's origin is the native TranslateImage point, not the
            // texture origin: it must be where the fill band begins.
            float expectX = ClassicGaugeLayout.LayerLeftX(anchor, spec.FillPoint, ClassicGaugeLayout.PaneScale);
            float expectTop = ClassicGaugeLayout.LayerTopY(anchor, spec.FillPoint, spec.SourceRect.Height, ClassicGaugeLayout.PaneScale);
            Assert.Equal((int)MathF.Round(expectX), backBand.X);
            Assert.Equal((int)MathF.Round(expectTop), backBand.Y);
        }
    }

    // ── 8. HUD-14b: single pane scale for offsets and geometry ────────────

    [Fact]
    public void Console_offsets_and_layer_geometry_share_one_scale()
    {
        // HUD-14 scaled console offsets by spread (1.15-2.1) and geometry by
        // 0.72, so the layers drifted apart. The layout helper exposes one
        // PaneScale used for both.
        Assert.Equal(ClassicGaugeLayout.PaneScale, 0.72f);
        // Anchor and a child layer must both multiply by that same scale.
        var spec = ClassicGaugeTable.Spec(GaugeKind.Thrust);
        var centre = new Vector2(960f, 540f);
        var anchor = ClassicGaugeLayout.Anchor(centre, spec, ClassicGaugeLayout.PaneScale);
        Assert.Equal(centre.X + spec.ConsoleOffset.X * ClassicGaugeLayout.PaneScale, anchor.X);
        Assert.Equal(centre.Y + spec.ConsoleOffset.Y * ClassicGaugeLayout.PaneScale, anchor.Y);
    }

    [Fact]
    public void Fill_point_and_source_rect_are_transcribed_per_gauge()
    {
        // Arc gauges place their revealed fill at the rect's own origin
        // (TranslateImage Point equals the GaugeImageRect origin).
        foreach (var kind in new[] { GaugeKind.Thrust, GaugeKind.Fuel, GaugeKind.Ammo, GaugeKind.Energy })
        {
            var spec = ClassicGaugeTable.Spec(kind);
            Assert.Equal(new Vector2(spec.SourceRect.X, spec.SourceRect.Y), spec.FillPoint);
        }
        // Hull/shield place the revealed fill inside their framed group at the
        // native TranslateImage points (dialog.mdl:2427 hull (27,27); :2513 (52,28)).
        Assert.Equal(new Vector2(27, 27), ClassicGaugeTable.Spec(GaugeKind.Hull).FillPoint);
        Assert.Equal(new Vector2(52, 28), ClassicGaugeTable.Spec(GaugeKind.Shield).FillPoint);
        Assert.NotNull(ClassicGaugeTable.Spec(GaugeKind.Hull).FrameStem);
        Assert.NotNull(ClassicGaugeTable.Spec(GaugeKind.Shield).FrameStem);
        Assert.Null(ClassicGaugeTable.Spec(GaugeKind.Thrust).FrameStem);
    }

    // ── 3. Red / flash switch condition ─────────────────────────────────────

    [Fact]
    public void Red_fill_switch_applies_only_to_hull_and_shield_on_flash()
    {
        // The native red variant exists only for hull/shield (SwitchImage on
        // Flash, dialog.mdl:2418-2440 / :2498-2510); the arc gauges have no red.
        foreach (var kind in new[] { GaugeKind.Hull, GaugeKind.Shield })
        {
            Assert.True(ClassicGaugeTable.UseRedFill(true, ClassicGaugeTable.Spec(kind)));
            Assert.False(ClassicGaugeTable.UseRedFill(false, ClassicGaugeTable.Spec(kind)));
        }
        foreach (var kind in new[] { GaugeKind.Thrust, GaugeKind.Fuel, GaugeKind.Ammo, GaugeKind.Energy })
        {
            Assert.False(ClassicGaugeTable.UseRedFill(true, ClassicGaugeTable.Spec(kind)));
        }
    }

    [Fact]
    public void Model_marks_flash_only_on_hull_and_shield()
    {
        var flash = HudModel.Create(Frame with { Flashing = true });
        var calm = HudModel.Create(Frame);
        foreach (var kind in new[] { GaugeKind.Hull, GaugeKind.Shield })
        {
            Assert.True(flash.Gauges.Single(g => g.Kind == kind).DamageFlash);
            Assert.False(calm.Gauges.Single(g => g.Kind == kind).DamageFlash);
            // flash → red variant selected.
            Assert.True(ClassicGaugeTable.UseRedFill(
                flash.Gauges.Single(g => g.Kind == kind).DamageFlash, ClassicGaugeTable.Spec(kind)));
        }
        foreach (var kind in new[] { GaugeKind.Thrust, GaugeKind.Fuel, GaugeKind.Ammo, GaugeKind.Energy })
        {
            Assert.False(flash.Gauges.Single(g => g.Kind == kind).DamageFlash);
        }
    }

    // ── 4. % numeral flag (default OFF = native) ────────────────────────────

    [Fact]
    public void Percentage_numeral_flag_is_off_by_default()
    {
        var settings = new HudSettings();
        settings.Normalize();
        Assert.False(settings.GaugePercentage);
    }

    // ── 5. Allocation: per-frame gauge path allocates nothing ───────────────

    [Fact]
    public void Gauge_spec_and_reveal_path_are_allocation_free()
    {
        static void Warm() { foreach (var k in System.Enum.GetValues<GaugeKind>()) _ = ClassicGaugeTable.Spec(k); }
        Warm();
        long before = System.GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200_000; i++)
        {
            var kind = (GaugeKind)(i % 6);           // HUD-14b — all SIX kinds
            var spec = ClassicGaugeTable.Spec(kind);
            _ = ClassicGaugeTable.RevealSource(spec.SourceRect, spec.Reveal, (i % 100) / 100f);
            _ = ClassicGaugeTable.UseRedFill((i & 1) != 0, spec);
            _ = ClassicGaugeLayout.Anchor(new Vector2(960f, 540f), spec, ClassicGaugeLayout.PaneScale);
            _ = ClassicGaugeLayout.RevealDest(spec, (i % 100) / 100f,
                ClassicGaugeLayout.Anchor(new Vector2(960f, 540f), spec, ClassicGaugeLayout.PaneScale),
                ClassicGaugeLayout.PaneScale);
        }
        long allocated = System.GC.GetAllocatedBytesForCurrentThread() - before;
        // Spec/RevealSource/UseRedFill/Anchor/RevealDest all return
        // structs/bools — zero heap.
        Assert.True(allocated < 8192,
            $"per-frame classic gauge path allocated {allocated} B over 200k iterations");
    }

    [Fact]
    public void GaugeTextures_AllLoaded_allocates_nothing()
    {
        // HUD-14b — AllLoaded is queried every frame from HudRenderer.Draw; it
        // must not call Enum.GetValues (which allocates) each time.
        var gauges = new GaugeTextures();          // no fill layers loaded → false
        Assert.False(gauges.AllLoaded);
        long before = System.GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200_000; i++) _ = gauges.AllLoaded;
        long allocated = System.GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 8192,
            $"AllLoaded allocated {allocated} B over 200k calls");
    }

    // ── 9. HUD-14b: the rendered label follows the gauge's own Label ──────

    [Fact]
    public void Renderer_draws_the_gauges_own_label_so_lifepod_OXY_shows()
    {
        // HUD-14 drew spec.Label and dropped the per-gauge override, so the
        // lifepod's OXY label was lost even though the matrix claimed it.
        // This is a RENDERER-level check (through HudRenderer.ResolveGaugeLabel,
        // the string the label pass draws), not a grep of the model source: the
        // model already labels the pod's fuel gauge "OXY" (HudModel.cs:279), and
        // the renderer must draw that, not the spec's fixed "FUEL".
        var renderer = (HudRenderer)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(HudRenderer));
        System.GC.SuppressFinalize(renderer);
        var fuelSpec = ClassicGaugeTable.Spec(GaugeKind.Fuel);
        Assert.NotEqual("OXY", fuelSpec.Label);          // spec still says FUEL…
        var oxyGauge = new HudGauge(GaugeKind.Fuel, 0.9f, new HudPoint(0, 0), "OXY",
            HudColor.TealAccent, false, true);
        Assert.Equal("OXY", renderer.ResolveGaugeLabel(oxyGauge));   // …but the renderer draws OXY
        var fuelGauge = new HudGauge(GaugeKind.Fuel, 0.9f, new HudPoint(0, 0), "FUEL",
            HudColor.TealAccent, false, true);
        Assert.Equal("FUEL", renderer.ResolveGaugeLabel(fuelGauge));
    }

    // ── 10. HUD-14c: lifepod model → OXY through the renderer; label y-up anchor ──

    [Fact]
    public void Lifepod_model_gives_the_fuel_gauge_the_OXY_label()
    {
        // HUD-14c — build a real lifepod-state HudModel and assert the Fuel
        // gauge the renderer uses is labelled OXY (the old test only checked
        // ResolveGaugeLabel on a hand-built gauge, never the model's pod state).
        var lifepod = HudModel.Create(Frame with { IsAdriftLifepod = true });
        var fuel = lifepod.Gauges.Single(g => g.Kind == GaugeKind.Fuel);
        Assert.Equal("OXY", fuel.Label);
        var renderer = (HudRenderer)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(HudRenderer));
        System.GC.SuppressFinalize(renderer);
        Assert.Equal("OXY", renderer.ResolveGaugeLabel(fuel));
    }

    [Fact]
    public void Label_point_is_text_upper_left_per_native_StringImage()
    {
        // HUD-19c — native StringImage::Render (engine/image.cpp:384-393) does
        // `y -= size.Y();` BEFORE `DrawString(..., Point(x + indent, y), ...)`,
        // and the image bounds are Rect(0, -ysize, fWidth, 0)
        // (engine/image.cpp:353). In y-up image space the text therefore spans
        // [-height, 0] below the TranslateImage point: the point is the text's
        // UPPER-left, so on screen (y-down) the label TOP is anchor.Y −
        // LabelPoint.Y·s with no text-height term (the HUD-14c "minus text
        // height" reading is reverted). The text then extends DOWN from there.
        var anchor = new Vector2(960f, 840f);
        var labelPoint = new Vector2(16f, 135f);
        const float scale = ClassicGaugeLayout.PaneScale;
        float top = ClassicGaugeLayout.LabelTopY(anchor, labelPoint, scale);
        Assert.Equal(840f - 135f * scale, top, 3);
        // Native y-up span of a 20px line hung below the point: [p.y − 20, p.y].
        // Mapped to screen (y_screen = anchor.Y − y_up·s) the TOP is the image
        // y = p.y edge — i.e. exactly LabelTopY.
        const float nativeLineHeight = 20f;
        float nativeTopUp = labelPoint.Y;                       // bounds top = 0 (relative)
        float nativeBottomUp = labelPoint.Y - nativeLineHeight; // y -= size.Y()
        Assert.Equal(anchor.Y - nativeTopUp * scale, top, 3);
        Assert.True(anchor.Y - nativeBottomUp * scale > top);  // text hangs below on screen
        // The renderer uses this helper with no text-height term.
        var drawLabel = MethodSource(SourceFile("Rendering", "HudRenderer.cs"),
            "private void DrawGaugeLabel(HudGauge gauge, ClassicGaugeSpec spec, Vector2 anchor, float scale)");
        Assert.Contains("ClassicGaugeLayout.LabelTopY(anchor, spec.LabelPoint, scale);", drawLabel);
    }

    private static string SourceFile(params string[] relative)
    {
        string root = System.AppContext.BaseDirectory;
        while (!System.IO.Directory.Exists(System.IO.Path.Combine(root, "src", "Allegiance.ClientCore.Game")))
            root = System.IO.Path.GetDirectoryName(root) ?? throw new System.InvalidOperationException("Repository not found");
        var parts = new System.Collections.Generic.List<string> { root, "src", "Allegiance.ClientCore.Game" };
        parts.AddRange(relative);
        return System.IO.File.ReadAllText(System.IO.Path.Combine(parts.ToArray()));
    }

    private static string MethodSource(string source, string signature)
    {
        int start = source.IndexOf(signature, System.StringComparison.Ordinal);
        Assert.True(start >= 0, "missing " + signature);
        int open = source.IndexOf('{', start), depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[start..(i + 1)];
        }
        throw new System.InvalidOperationException("unterminated " + signature);
    }
}
