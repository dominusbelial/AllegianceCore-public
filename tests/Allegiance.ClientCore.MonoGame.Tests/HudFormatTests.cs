using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="HudFormat"/> (and the plain
/// <see cref="HudColor"/> it returns). The status-colour ramp, the throttle
/// mapping and the percent formatter are exactly the non-trivial logic the
/// HUD's requirements call for unit coverage on — a straight "fraction *
/// barWidth" one-liner would not earn a test, but a colour ramp / threshold
/// function does. No graphics device involved.
/// </summary>
public class HudFormatTests
{
    // ── Status colour ramp ────────────────────────────────────────────────

    [Fact]
    public void StatusColor_AtZero_IsRedDominant()
    {
        var c = HudFormat.StatusColor(0f);
        Assert.True(c.R > c.G, $"expected red-dominant at 0, got {c}");
        Assert.True(c.R > c.B, $"expected red-dominant at 0, got {c}");
    }

    [Fact]
    public void StatusColor_AtOne_IsTheTealAccent()
    {
        var c = HudFormat.StatusColor(1f);
        Assert.Equal(HudColor.TealAccent, c);
    }

    [Fact]
    public void StatusColor_AtWarnThreshold_IsAmber()
    {
        // Amber = strong R and G, weak B (yellow-ish); must be distinct from
        // both the red (0) and teal (1) endpoints.
        var c = HudFormat.StatusColor(0.5f);
        Assert.True(c.R >= 0.99f, $"expected hot R at warn, got {c}");
        Assert.True(c.G > c.B, $"expected yellow G>B at warn, got {c}");
        Assert.True(c.B < 0.3f, $"expected suppressed B at warn, got {c}");
    }

    [Fact]
    public void StatusColor_GreenChannel_RisesMonotonicallyAcrossFractions()
    {
        // The ramp must progress smoothly red → amber → teal; green is the
        // cleanest monotone to assert (rises the whole way, no plateau).
        float prev = float.MinValue;
        foreach (float f in new[] { 0f, 0.1f, 0.3f, 0.5f, 0.7f, 0.9f, 1f })
        {
            var c = HudFormat.StatusColor(f);
            Assert.True(c.G > prev, $"green must rise with fraction; at {f} got {c.G} <= {prev}");
            prev = c.G;
        }
    }

    [Theory]
    [InlineData(-5f, 0f)]
    [InlineData(2f, 1f)]
    [InlineData(-0.002f, 0f)]
    [InlineData(1.003f, 1f)]
    public void StatusColor_OutOfRangeInput_ClampsInto01(float input, float expectedEqual)
    {
        Assert.Equal(HudFormat.StatusColor(expectedEqual), HudFormat.StatusColor(input));
    }

    [Fact]
    public void StatusColor_CustomWarnThreshold_ShiftsTheAmberPoint()
    {
        // With warnBelow = 0.25 the amber anchor sits at 0.25; 0.5 is therefore
        // halfway amber→teal and greener than the default-threshold 0.5.
        var defaultHalf = HudFormat.StatusColor(0.5f, warnBelow: 0.5f);
        var customHalf = HudFormat.StatusColor(0.5f, warnBelow: 0.25f);
        Assert.True(customHalf.G > defaultHalf.G, "lower warn threshold should make 0.5 greener");
    }

    // ── Throttle axis mapping ─────────────────────────────────────────────

    [Theory]
    [InlineData(-1f, 0)]   // idle
    [InlineData(-0.5f, 25)]
    [InlineData(0f, 50)]
    [InlineData(0.499f, 75)]
    [InlineData(1f, 100)]  // full forward
    public void ThrottlePercent_MapsAxisSpanToPercent(float axis, int expected)
    {
        Assert.Equal(expected, HudFormat.ThrottlePercent(axis));
    }

    [Theory]
    [InlineData(-3f, 0)]
    [InlineData(2f, 100)]
    public void ThrottlePercent_OutOfRangeAxis_ClampsToEndpoints(float axis, int expected)
    {
        Assert.Equal(expected, HudFormat.ThrottlePercent(axis));
    }

    // ── Percent formatter ─────────────────────────────────────────────────

    [Theory]
    [InlineData(0f, "0")]
    [InlineData(0.042f, "4")]
    [InlineData(0.418f, "42")]
    [InlineData(0.501f, "50")]
    [InlineData(0.994f, "99")]
    [InlineData(1f, "100")]
    public void FormatPercent_RoundsToWholePercent(float fraction, string expected)
    {
        Assert.Equal(expected, HudFormat.FormatPercent(fraction));
    }

    [Fact]
    public void FormatPercent_OutOfRange_ClampsToBounds()
    {
        Assert.Equal("0", HudFormat.FormatPercent(-0.4f));
        Assert.Equal("100", HudFormat.FormatPercent(3f));
    }

    // ── Speed formatter ───────────────────────────────────────────────────

    [Theory]
    [InlineData(0f, "0")]
    [InlineData(412.498f, "412")]
    [InlineData(998.599f, "999")]
    [InlineData(1234f, "1234")]
    public void FormatSpeed_RoundsToWholeUnits(float speed, string expected)
    {
        Assert.Equal(expected, HudFormat.FormatSpeed(speed));
    }

    [Fact]
    public void FormatSpeed_NeverNegative()
    {
        Assert.Equal("0", HudFormat.FormatSpeed(-50f));
    }

    // ── Native NumberString formatter ─────────────────────────────────────

    [Theory]
    [InlineData(-1f, "0")]     // unseen-speed sentinel: (int)(-1 + 0.5) = 0
    [InlineData(0f, "0")]
    [InlineData(0.4f, "0")]
    [InlineData(0.5f, "1")]
    [InlineData(2.5f, "3")]    // round-half-up, unlike .NET banker's rounding
    [InlineData(412.498f, "412")]
    [InlineData(-0.5f, "0")]   // truncation toward zero
    public void FormatNativeNumber_Uses_Native_Cast_Plus_Half(float v, string expected)
    {
        Assert.Equal(expected, HudFormat.FormatNativeNumber(v));
    }

    // ── Money formatter (P442) ───────────────────────────────────────────

    [Theory]
    [InlineData(0, "$0")]
    [InlineData(7, "$7")]
    [InlineData(12345, "$12345")]     // native does no thousands grouping ("%d")
    [InlineData(1000000, "$1000000")]
    [InlineData(16000, "$16000")]
    public void FormatMoney_PrefixesDollarNoGrouping(int money, string expected)
    {
        Assert.Equal(expected, HudFormat.FormatMoney(money));
    }

    [Fact]
    public void FormatMoney_Negative_IsDefensivelySigned()
    {
        Assert.Equal("-$42", HudFormat.FormatMoney(-42));
    }
}
