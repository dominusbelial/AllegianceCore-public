using Allegiance.ClientCore.MonoGame;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P576/P588 — the sky tier + aleph-glimpse LAUNCH defaults: the sky tier
/// defaults to <b>black</b> (P588 — the new DEFAULT, per Domingo's dislike of
/// the dome's "stitched fabric" seams), black draws NO nebula dome, the native
/// and HD tiers remain opt-in, and the aleph destination-glimpse is OFF by
/// default (a non-native flourish).
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
    public void AlephGlimpse_CanBeSwitchedOn()
    {
        var o = new Game1.LaunchOptions { AlephGlimpse = true };
        Assert.True(o.AlephGlimpse);
    }

    [Fact]
    public void SkyTier_DefaultsToBlack()
    {
        // P588 — the default sky is now black (point stars + native planet, no
        // dome). Failing-first: before P588 the default was "native".
        var o = new Game1.LaunchOptions();
        Assert.Equal("black", o.SkyTier);
    }

    [Fact]
    public void SkyTier_DefaultResolvesToBlack()
    {
        // P588 — an unset/unknown sky tier must resolve to the default, black.
        Assert.Equal("black", Game1.LaunchOptions.EffectiveSkyTier(null));
        Assert.Equal("black", Game1.LaunchOptions.EffectiveSkyTier(""));
        Assert.Equal("black", Game1.LaunchOptions.EffectiveSkyTier("garbage"));
    }

    [Fact]
    public void SkyTier_EachTierCanBeSelected()
    {
        Assert.Equal("black", Game1.LaunchOptions.EffectiveSkyTier("black"));
        Assert.Equal("native", Game1.LaunchOptions.EffectiveSkyTier("native"));
        Assert.Equal("hd", Game1.LaunchOptions.EffectiveSkyTier("hd"));
        // case-insensitive input yields the canonical lowercase tier
        Assert.Equal("hd", Game1.LaunchOptions.EffectiveSkyTier("HD"));
        Assert.Equal("black", Game1.LaunchOptions.EffectiveSkyTier("BLACK"));
    }

    [Fact]
    public void BlackSky_DrawsNoDome()
    {
        // P588 — the whole point of the black tier: no nebula dome (the seams
        // Domingo disliked). Native and HD keep their domes.
        Assert.False(Game1.LaunchOptions.SkyTierDrawsDome("black"));
        Assert.True(Game1.LaunchOptions.SkyTierDrawsDome("native"));
        Assert.True(Game1.LaunchOptions.SkyTierDrawsDome("hd"));
    }
}
