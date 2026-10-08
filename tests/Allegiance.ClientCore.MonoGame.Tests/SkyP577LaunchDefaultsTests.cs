using Allegiance.ClientCore.MonoGame;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P577 — the HD sky tier's LAUNCH defaults: Native stays the default sky until
/// Domingo approves the comparison sheets (task scope: "Native stays the
/// default until Domingo approves"); the global sky intensity defaults to 1.0
/// and is clamped to [0,2]; the low-res (Android) asset set defaults off.
/// </summary>
public class SkyP577LaunchDefaultsTests
{
    [Fact]
    public void SkyTier_DefaultsToNative()
        => Assert.Equal("native", new Game1.LaunchOptions().SkyTier);

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
