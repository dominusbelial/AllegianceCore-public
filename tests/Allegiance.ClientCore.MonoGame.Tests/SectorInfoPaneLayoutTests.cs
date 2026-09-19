using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="SectorInfoPaneLayout"/> (P272) — the starmap
/// info pane's content/selection logic. No graphics device (same discipline as
/// <see cref="StarmapLayoutTests"/>). They pin the contract native's
/// <c>SectorInfoPane</c> (<c>sectormap.cpp:139-538</c>) establishes:
/// special-asteroid grouping keys on <c>(aabm &amp; ~c_aabmBuildable)</c>
/// (sectormap.cpp:296), the helium figure sums only the <c>c_aabmMineHe3 (0x01)</c>
/// rocks' ore (sectormap.cpp:360-366), and the cluster-selection cycle wraps.
/// </summary>
public class SectorInfoPaneLayoutTests
{
    private static SectorInfoPaneLayout.AsteroidSample Rock(short mask, float ore = 0f) =>
        new(mask, ore);

    [Fact]
    public void EmptyCluster_YieldsZeroAsteroids_NoGroups_NoHelium()
    {
        var d = SectorInfoPaneLayout.Build("Ganymede", []);
        Assert.Equal("Ganymede", d.Name);
        Assert.Equal(0, d.TotalAsteroids);
        Assert.False(d.HasHelium);
        Assert.Equal(0f, d.HeliumOre);
        Assert.Empty(d.Groups);
        // Lines: "<name> Sector", "Asteroids: 0", "He3 ore: 0"
        Assert.Equal(["Ganymede Sector", "Asteroids: 0", "He3 ore: 0"], d.DisplayLines);
    }

    [Fact]
    public void BuildableOnlyRocks_AreCounted_ButFormNoSpecialGroup()
    {
        // 0x08 = buildable only → counted in Total but NOT a special group
        // (native's (aabm & ~c_aabmBuildable) == 0 exclusion, sectormap.cpp:296).
        var d = SectorInfoPaneLayout.Build("Alderan",
            [Rock(0x08), Rock(0x08), Rock(0x08)]);
        Assert.Equal(3, d.TotalAsteroids);
        Assert.False(d.HasHelium);
        Assert.Empty(d.Groups);
    }

    [Fact]
    public void He3Rocks_FormGroup_SumHeliumOre_AndSetHasHelium()
    {
        var d = SectorInfoPaneLayout.Build("Alderan",
            [
                Rock(0x08),                          // buildable only (no group)
                Rock(0x09, ore: 500f),               // He3|Buildable
                Rock(0x09, ore: 700f),               // He3|Buildable
                Rock(0x08),                          // buildable only
            ]);
        Assert.Equal(4, d.TotalAsteroids);
        Assert.True(d.HasHelium);
        Assert.Equal(1200f, d.HeliumOre, 3);         // 500 + 700 over 0x01 rocks only
        var g = Assert.Single(d.Groups);
        Assert.Equal("He3", g.Label);
        Assert.Equal(2, g.Count);
        Assert.Contains("Asteroids: 4", d.DisplayLines);
        Assert.Contains("He3: 2", d.DisplayLines);
        Assert.Contains("He3 ore: 1200", d.DisplayLines);
    }

    [Fact]
    public void DistinctCapabilityMasks_FormSeparateGroups()
    {
        var d = SectorInfoPaneLayout.Build("X",
            [Rock(0x09), Rock(0x0C), Rock(0x09)]);   // He3|B, Gold|B, He3|B
        Assert.Equal(2, d.Groups.Count);
        // Most populous first: He3 x2, then Gold x1.
        Assert.Equal("He3", d.Groups[0].Label);
        Assert.Equal(2, d.Groups[0].Count);
        Assert.Equal("Gold", d.Groups[1].Label);
        Assert.Equal(1, d.Groups[1].Count);
    }

    [Fact]
    public void LotsHe3_IsItsOwnGroup_NotCountedInTheHeliumLine()
    {
        // 0x02 = MineLotsHe3: a distinct special group (its own icon natively)
        // but native's GetHeliumInCluster sums ONLY c_aabmMineHe3 (0x01),
        // so a lots-He3 rock contributes to a group yet adds nothing to the
        // helium ore figure — faithful to sectormap.cpp:360-366.
        var d = SectorInfoPaneLayout.Build("X", [Rock(0x0A, ore: 999f)]);  // LotsHe3|Buildable
        Assert.Equal(1, d.TotalAsteroids);
        Assert.False(d.HasHelium);
        Assert.Equal(0f, d.HeliumOre);
        var g = Assert.Single(d.Groups);
        Assert.Equal("He3-rich", g.Label);
        Assert.Contains("He3-rich: 1", d.DisplayLines);
        Assert.Contains("He3 ore: 0", d.DisplayLines);
    }

    [Fact]
    public void MultiBitMask_JoinsLabels()
    {
        var d = SectorInfoPaneLayout.Build("X",
            [Rock((short)(0x01 | 0x04 | 0x10))]);   // He3|Gold|Special
        var g = Assert.Single(d.Groups);
        Assert.Equal("He3+Gold+Special", g.Label);
        Assert.Contains("He3+Gold+Special: 1", d.DisplayLines);
    }

    [Theory]
    [InlineData(+1)]
    [InlineData(-1)]
    public void NextSelection_AdvancesFromCurrentAndWraps(int delta)
    {
        short[] ids = [1050, 1051, 1052, 1053];
        short? prev = 1051;
        short first = SectorInfoPaneLayout.NextSelection(prev, ids, 1050, delta);
        short expected = delta > 0 ? ids[2] : ids[0];
        Assert.Equal(expected, first);

        // Continue exactly a full lap (ids.Length steps) → must return to first.
        short cur = first;
        for (int i = 0; i < ids.Length; i++)
            cur = SectorInfoPaneLayout.NextSelection(cur, ids, 1050, delta);
        Assert.Equal(first, cur);
    }

    [Fact]
    public void NextSelection_FirstPressDefaultsToThePlayersSector()
    {
        short[] ids = [1050, 1051, 1052];
        // No prior selection → start at the player's current sector (defaultId).
        short first = SectorInfoPaneLayout.NextSelection(null, ids, 1052, +1);
        Assert.Equal(1052, first);
    }

    [Fact]
    public void NextSelection_UnknownDefaultFallsBackToFirstCluster()
    {
        short[] ids = [1050, 1051, 1052];
        // defaultId not in the list → fall back to the first cluster.
        Assert.Equal(1050, SectorInfoPaneLayout.NextSelection(null, ids, 9999, +1));
    }

    [Fact]
    public void NextSelection_EmptyOrderedList_ReturnsDefault()
    {
        Assert.Equal(1050, SectorInfoPaneLayout.NextSelection(null, [], 1050, +1));
    }

    [Fact]
    public void LineOrigins_StackVertically_FromTheOrigin()
    {
        var pts = SectorInfoPaneLayout.LineOrigins(
            new System.Numerics.Vector2(10f, 20f), 18f, 3);
        Assert.Equal(3, pts.Count);
        Assert.Equal(10f, pts[0].X);
        Assert.Equal(20f, pts[0].Y);
        Assert.Equal(20f + 18f, pts[1].Y);
        Assert.Equal(20f + 36f, pts[2].Y);
        Assert.All(pts, p => Assert.Equal(10f, p.X));
    }
}
