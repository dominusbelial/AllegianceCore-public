using Allegiance.ClientCore.MonoGame;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P576/P588/P607 — the sky tier + aleph-glimpse LAUNCH defaults: the sky tier
/// defaults to <b>black</b> (P588 — the new DEFAULT, per Domingo's dislike of
/// the dome's "stitched fabric" seams), black draws NO nebula dome, the native
/// and HD tiers remain opt-in, and the aleph destination-glimpse / transit
/// flare defaults <b>ON</b> (P607 — Domingo wants the enemy-crossing flare
/// visible out of the box).
/// </summary>
public class SkyLaunchOptionDefaultsTests
{
    [Fact]
    public void AlephGlimpse_DefaultsToOn()
    {
        // P607 — failing-first: before P607 the default was OFF (false).
        var o = new Game1.LaunchOptions();
        Assert.True(o.AlephGlimpse, "the aleph destination glimpse / transit flare must default to ON (P607)");
    }

    [Fact]
    public void AlephGlimpse_CanBeSwitchedOff()
    {
        // An explicit --aleph-glimpse off must be representable (P607).
        var o = new Game1.LaunchOptions { AlephGlimpse = false };
        Assert.False(o.AlephGlimpse);
    }

    [Fact]
    public void AlephGlimpseFlag_OnValuesParseOn()
    {
        // --aleph-glimpse=on / "ON" both resolve ON.
        Assert.Equal(true, AlephGlimpseFlag.Parse("on"));
        Assert.Equal(true, AlephGlimpseFlag.Parse("ON"));
    }

    [Fact]
    public void AlephGlimpseFlag_OffParsesToOff()
    {
        // --aleph-glimpse off / =off parse OFF (P607).
        Assert.Equal(false, AlephGlimpseFlag.Parse("off"));
        Assert.Equal(false, AlephGlimpseFlag.Parse("OFF"));
    }

    [Fact]
    public void AlephGlimpseFlag_AbsentOrGarbageIsNull_DefaultOnAtCaller()
    {
        // A bare --aleph-glimpse with no value (null) or an unrecognised value
        // is reported as null so the caller (Program.cs) warns and falls back
        // to the P607 default, ON. So the effective value of a bare flag / bad
        // value is still on — verified via DefaultOn here.
        Assert.Null(AlephGlimpseFlag.Parse(null));
        Assert.Null(AlephGlimpseFlag.Parse("banana"));
        Assert.True(AlephGlimpseFlag.DefaultOn);
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
