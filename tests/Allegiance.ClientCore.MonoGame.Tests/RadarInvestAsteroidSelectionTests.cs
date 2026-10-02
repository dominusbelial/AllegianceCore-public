using Allegiance.Core;
using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P495 — pure coverage of <see cref="RadarInvestAsteroidSelection"/>, the
/// decision of whether a rock is a build spot for the currently-hovered
/// station-type investment (native <c>SetInvestAsteroid</c>,
/// WinTrek/loadout.cpp:1396-1409 — highlight the asteroids whose capability
/// matches the station type's <c>AabmBuild</c>). Kept out of the render loop
/// so the flagging is unit-testable without a graphics device (the same
/// pure-logic-layer discipline as <see cref="RadarTargetSelection"/>).
/// </summary>
public class RadarInvestAsteroidSelectionTests
{
    [Fact]
    public void IsBuildSpot_MatchingCapability_True()
    {
        Assert.True(RadarInvestAsteroidSelection.IsBuildSpot(
            DataAsteroidIGC.AabmBuildable, DataAsteroidIGC.AabmBuildable));
    }

    [Fact]
    public void IsBuildSpot_SubsetMatch_True()
    {
        // A rock carrying extra capability bits still has the build bit set.
        Assert.True(RadarInvestAsteroidSelection.IsBuildSpot(
            DataAsteroidIGC.AabmBuildable,
            DataAsteroidIGC.AabmBuildable | DataAsteroidIGC.AabmMineHe3));
    }

    [Fact]
    public void IsBuildSpot_NoActiveSelection_Null_False()
    {
        Assert.False(RadarInvestAsteroidSelection.IsBuildSpot(null, DataAsteroidIGC.AabmBuildable));
        Assert.False(RadarInvestAsteroidSelection.IsBuildSpot(null, 0));
    }

    [Fact]
    public void IsBuildSpot_NonMatchingCapability_False()
    {
        Assert.False(RadarInvestAsteroidSelection.IsBuildSpot(
            DataAsteroidIGC.AabmBuildable, DataAsteroidIGC.AabmMineHe3));
        Assert.False(RadarInvestAsteroidSelection.IsBuildSpot(
            DataAsteroidIGC.AabmMineHe3, DataAsteroidIGC.AabmBuildable));
    }
}
