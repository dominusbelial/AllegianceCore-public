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
        0f, 0, 0f, System.Array.Empty<WeaponStatusRow>(), false, 0);

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
    [InlineData(GaugeKind.Thrust, -159f, 6f)]   // (161,246)-(320,240)
    [InlineData(GaugeKind.Fuel, 41f, 6f)]       // (361,246)
    [InlineData(GaugeKind.Ammo, -173f, -135f)]  // (147,105)
    [InlineData(GaugeKind.Energy, 52f, -135f)]  // (372,105)
    [InlineData(GaugeKind.Hull, -105f, -60f)]   // ArmorImage (215,180)
    [InlineData(GaugeKind.Shield, -20f, -60f)]  // ShieldImage (300,180)
    public void Console_offsets_match_DefaultCenterHudOn_minus_320_240(GaugeKind kind, float x, float y)
    {
        // The 640x480 pane's top-left is at centre-(320,240) (dialog.mdl:5397),
        // so each DefaultCenterHudOn TranslateImage point minus (320,240) is the
        // gauge's position relative to centre (dialog.mdl:2610-2615).
        var off = ClassicGaugeTable.Spec(kind).ConsoleOffset;
        Assert.Equal(new Vector2(x, y), off);
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
            var kind = (GaugeKind)(i & 5);
            var spec = ClassicGaugeTable.Spec(kind);
            _ = ClassicGaugeTable.RevealSource(spec.SourceRect, spec.Reveal, (i % 100) / 100f);
            _ = ClassicGaugeTable.UseRedFill((i & 1) != 0, spec);
        }
        long allocated = System.GC.GetAllocatedBytesForCurrentThread() - before;
        // Spec/RevealSource/UseRedFill all return structs/bools — zero heap.
        Assert.True(allocated < 8192,
            $"per-frame classic gauge path allocated {allocated} B over 200k iterations");
    }
}
