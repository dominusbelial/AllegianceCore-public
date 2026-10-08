using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P577 — the HD sky tier's pure, headless rules: native-id → HD-asset naming
/// (environment index from <c>globeN</c>, planet stem from <c>nebplntNNbmp</c>)
/// and the per-entry readability guard (brightness ceiling + saturation clamp)
/// that keeps ships/projectiles/HUD readable over the HD sky (scope item 3).
/// These exercise brand-new functionality — the P576 "today's code" state had
/// no HD sky tier at all.
/// </summary>
public class HdSkyGuardTests
{
    // ---- naming: native id -> HD asset -----------------------------------
    [Theory]
    [InlineData("globe3", 3)]
    [InlineData("globe10", 10)]
    [InlineData("globe1", 1)]
    [InlineData("", 0)]
    [InlineData("none", 0)]
    public void EnvironmentIndex_ParsesGlobeNumber(string poster, int expected)
        => Assert.Equal(expected, HdSkyGuard.EnvironmentIndex(poster));

    [Theory]
    [InlineData("nebplnt06bmp", "nebplnt06")]
    [InlineData("nebplnt43bmp", "nebplnt43")]
    [InlineData("nebplnt12bmp", "nebplnt12")]
    public void PlanetStem_StripsBmpSuffix(string name, string expected)
        => Assert.Equal(expected, HdSkyGuard.PlanetStem(name));

    // ---- readability guard: luminance cap preserves hue -------------------
    [Fact]
    public void ApplyCap_ScalesOverbrightPixel_DownToCeiling_PreservingHue()
    {
        var data = new[] { new Color(255, 240, 220, 255) };   // luma 241.7 > 225
        bool changed = HdSkyGuard.ApplyCap(data, 225, 0.62f);
        Assert.True(changed);
        float r = data[0].R, g = data[0].G, b = data[0].B;
        float luma = 0.2126f * r + 0.7152f * g + 0.0722f * b;
        Assert.True(luma <= 225f + 0.501f, $"luma {luma} exceeded the cap");
        // hue preserved: channel ordering unchanged
        Assert.True(r >= g && g >= b, "overbright scaling must preserve hue ordering");
        Assert.Equal(255, data[0].A);
    }

    [Fact]
    public void ApplyCap_LeavesAlreadyDark_Untouched()
    {
        var pixel = new Color(30, 40, 50, 255);
        var data = new[] { pixel };
        bool changed = HdSkyGuard.ApplyCap(data, 225, 0.62f);
        Assert.False(changed, "a dark, low-saturation pixel must not change");
        Assert.Equal(pixel, data[0]);
    }

    [Fact]
    public void ApplyCap_Desaturates_HighSaturationPixel_TowardCeiling()
    {
        // Fully-saturated magenta: (255,0,255) — sat = 1 > 0.62.
        var data = new[] { new Color(255, 0, 255, 255) };
        bool changed = HdSkyGuard.ApplyCap(data, 225, 0.62f);
        Assert.True(changed);
        float r = data[0].R, g = data[0].G, b = data[0].B;
        float maxc = Math.Max(r, Math.Max(g, b));
        float minc = Math.Min(r, Math.Min(g, b));
        float sat = maxc > 0 ? (maxc - minc) / maxc : 0f;
        Assert.True(sat <= 0.62f + 0.02f, $"saturation {sat:F3} above ceiling");
        Assert.True(g > 0, "desaturation must pull the channel toward grey (g rises from 0)");
    }

    [Fact]
    public void ApplyCap_TransparentPixel_IsUntouched()
    {
        var pixel = new Color(255, 255, 255, 0);
        var data = new[] { pixel };
        bool changed = HdSkyGuard.ApplyCap(data, 225, 0.62f);
        Assert.False(changed);
        Assert.Equal(pixel, data[0]);
    }
}
