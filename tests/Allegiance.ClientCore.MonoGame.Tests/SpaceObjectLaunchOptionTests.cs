using Allegiance.ClientCore.MonoGame;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P589 — the black-sky space-object launch toggles default to ON (moons,
/// distant galaxies/nebulae cut-outs, and the deep-field galaxy + Little-Red-Dot
/// field are all shown by default in the black sky, per Domingo's art
/// direction), and each can be switched off via its <c>--… off</c> flag.
/// Brand-new options (no pre-P589 field existed), so these pin the new
/// default-on behaviour and would fail if the default ever regressed to off.
/// </summary>
public class SpaceObjectLaunchOptionTests
{
    [Fact]
    public void Moons_DefaultToOn()
    {
        var o = new Game1.LaunchOptions();
        Assert.True(o.MoonsOn, "moons must default ON in the black sky (--moons off to disable)");
    }

    [Fact]
    public void Moons_CanBeDisabled()
    {
        var o = new Game1.LaunchOptions { MoonsOn = false };
        Assert.False(o.MoonsOn);
    }

    [Fact]
    public void DistantObjects_DefaultToOn()
    {
        var o = new Game1.LaunchOptions();
        Assert.True(o.DistantObjectsOn, "distant galaxies/nebulae must default ON (--distant-objects off to disable)");
    }

    [Fact]
    public void DistantObjects_CanBeDisabled()
    {
        var o = new Game1.LaunchOptions { DistantObjectsOn = false };
        Assert.False(o.DistantObjectsOn);
    }

    [Fact]
    public void Galaxies_DefaultToOn()
    {
        var o = new Game1.LaunchOptions();
        Assert.True(o.GalaxiesOn, "the deep-field galaxy + LRD field must default ON (--galaxies off to disable)");
    }

    [Fact]
    public void Galaxies_CanBeDisabled()
    {
        var o = new Game1.LaunchOptions { GalaxiesOn = false };
        Assert.False(o.GalaxiesOn);
    }
}
