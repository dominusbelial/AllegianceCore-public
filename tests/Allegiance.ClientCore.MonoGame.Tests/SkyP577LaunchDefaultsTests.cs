using Allegiance.ClientCore.MonoGame;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P577/P588 — the HD sky tier's LAUNCH defaults: the global sky intensity
/// defaults to 1.0 and is clamped to [0,2]; the low-res (Android) asset set
/// defaults off; and native/HD remain selectable sky tiers (P588 made black the
/// default, so native is no longer the default — it is an opt-in dome tier).
/// </summary>
public class SkyP577LaunchDefaultsTests
{
    [Fact]
    public void SkyTier_CanBeSetToNative()
    {
        // P588 — native (the P576 dome + planet) stays an opt-in tier.
        var o = new Game1.LaunchOptions { SkyTier = "native" };
        Assert.Equal("native", o.SkyTier);
    }

    [Fact]
    public void SkyIntensity_DefaultsToOne()
        => Assert.Equal(1.0f, new Game1.LaunchOptions().SkyIntensity);

    [Fact]
    public void SkyLowRes_DefaultsToOff()
        => Assert.False(new Game1.LaunchOptions().SkyLowRes);

    [Fact]
    public void SkyIntensity_HdTierCanBeSelected()
    {
        var o = new Game1.LaunchOptions { SkyTier = "hd" };
        Assert.Equal("hd", o.SkyTier);
    }

    [Fact]
    public void SkyIntensity_CanBeSetWithinRange()
    {
        var o = new Game1.LaunchOptions { SkyIntensity = 0.5f };
        Assert.Equal(0.5f, o.SkyIntensity);
    }
}
