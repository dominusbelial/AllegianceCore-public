using Allegiance.ClientCore.MonoGame;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P576 — the sky tier + aleph-glimpse LAUNCH defaults: native sky is ON by
/// default (it's native), the HD tier is the P577 seam (falls back to native),
/// and the aleph destination-glimpse is OFF by default (a non-native flourish).
/// </summary>
public class SkyLaunchOptionDefaultsTests
{
    [Fact]
    public void AlephGlimpse_DefaultsToOff()
    {
        var o = new Game1.LaunchOptions();
        Assert.False(o.AlephGlimpse, "the aleph destination glimpse must default to OFF (non-native, optional)");
    }

    [Fact]
    public void SkyTier_DefaultsToNative()
    {
        var o = new Game1.LaunchOptions();
        Assert.Equal("native", o.SkyTier);
    }

    [Fact]
    public void AlephGlimpse_CanBeSwitchedOn()
    {
        var o = new Game1.LaunchOptions { AlephGlimpse = true };
        Assert.True(o.AlephGlimpse);
    }
}
