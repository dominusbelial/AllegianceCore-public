using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="SectorInfoPaneLayout"/> (P272) — the starmap
/// info pane's content/selection logic. No graphics device (same discipline as
/// <see cref="StarmapLayoutTests"/>). They pin the contract native's
/// <c>SectorInfoPane</c> (<c>sectormap.cpp:139-538</c>) establishes:
/// special-asteroid eligibility excludes buildable-only rocks, then grouping
/// keys on the full capability mask (sectormap.cpp:296-305). The helium figure sums only the <c>c_aabmMineHe3 (0x01)</c>
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
    public void Buildable_bit_distinguishes_special_groups_with_the_same_resource()
    {
        // Native filters with ~Buildable but uses the original aabm as the
        // map key. Resource-only and resource|Buildable have separate icons.
        var d = SectorInfoPaneLayout.Build("X",
            [Rock(0x01, 100), Rock(0x09, 200), Rock(0x09, 300), Rock(0x08), Rock(0)]);

        Assert.Equal(5, d.TotalAsteroids);
        Assert.Equal(600f, d.HeliumOre);
        Assert.Collection(d.Groups,
            group => { Assert.Equal((short)0x09, group.Mask); Assert.Equal(2, group.Count); },
            group => { Assert.Equal((short)0x01, group.Mask); Assert.Equal(1, group.Count); });
        Assert.All(d.Groups, group => Assert.Equal("He3", group.Label));
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

    /// <summary>Build a client-world-shaped asteroid dictionary for the
    /// mining/visibility data-flow tests (P752): one He3 rock (id 1, aabm 0x01,
    /// OreMax 1000) and one gold rock (id 2, aabm 0x04, OreMax 500) in cluster
    /// 7, plus a He3 rock in another cluster that must be ignored.</summary>
    private static System.Collections.Generic.Dictionary<short, Allegiance.Core.DataAsteroidIGC> TestAsteroids() => new()
    {
        [1] = new Allegiance.Core.DataAsteroidIGC(
            Signature: 0f, Position: new float[3], Up: new float[3], Forward: new float[3],
            Rotation: new float[4], Ore: 1000f, OreMax: 1000f, AabmCapabilities: 0x01,
            AsteroidId: 1, HitPoints: 1f, Radius: 1, ModelName: "", TextureName: "", IconName: "",
            ClusterId: 7, Name: "", Fraction: 0),
        [2] = new Allegiance.Core.DataAsteroidIGC(
            Signature: 0f, Position: new float[3], Up: new float[3], Forward: new float[3],
            Rotation: new float[4], Ore: 500f, OreMax: 500f, AabmCapabilities: 0x04,
            AsteroidId: 2, HitPoints: 1f, Radius: 1, ModelName: "", TextureName: "", IconName: "",
            ClusterId: 7, Name: "", Fraction: 0),
        [3] = new Allegiance.Core.DataAsteroidIGC(
            Signature: 0f, Position: new float[3], Up: new float[3], Forward: new float[3],
            Rotation: new float[4], Ore: 400f, OreMax: 400f, AabmCapabilities: 0x01,
            AsteroidId: 3, HitPoints: 1f, Radius: 1, ModelName: "", TextureName: "", IconName: "",
            ClusterId: 9, Name: "", Fraction: 0),
    };

    /// <summary>⭐ P752 — the pane's helium figure is the SIDE-KNOWN ore: a live
    /// ore fraction (ASTEROIDS_UPDATE/ASTEROID_MINED) scaled by OreMax, never
    /// the static EXPORT value, and 0 for a rock the side has never been told
    /// about (native GetOreSeenBySide, asteroidigc.h:280-285; the pane's feed is
    /// DrawStarmap's Sample() call, Game1.Rendering.cs).</summary>
    [Fact]
    public void Sample_UsesLiveFractionTimesOreMax_AndZeroForUnseenRocks()
    {
        // Half-mined He3 rock: 0.5 x 1000 = 500 known ore.
        Assert.Equal(500f, SectorInfoPaneLayout.Sample(0x01, 1000f, 0.5f).Ore);
        // Never-seen rock: 0, NOT the static export ore.
        Assert.Equal(0f, SectorInfoPaneLayout.Sample(0x01, 1000f, null).Ore);
        // Fully drained rock.
        Assert.Equal(0f, SectorInfoPaneLayout.Sample(0x01, 1000f, 0f).Ore);
    }

    /// <summary>⭐ P752 — the mining/visibility-aware data flow: drain one of two
    /// known He3 rocks via the real wire feeds (ASTEROIDS_UPDATE then
    /// ASTEROID_MINED) and the pane's helium sum falls with it — while an
    /// unseen He3 rock contributes nothing even though the static EXPORT
    /// carries ore.</summary>
    [Fact]
    public void HeliumSum_FollowsLiveMining_AndIgnoresUnseenStaticOre()
    {
        var asteroids = TestAsteroids();
        var live = new System.Collections.Generic.Dictionary<short, Allegiance.ClientCore.State.AsteroidLiveState>();

        float Sum() => SectorInfoPaneLayout.Build("C",
            SectorInfoPaneLayout.SamplesForCluster(asteroids, live, 7), null).HeliumOre;

        // Nothing seen yet: both He3 rocks contribute 0 (side-known ore only).
        Assert.Equal(0f, Sum());

        // The side's ASTEROIDS_UPDATE batch reports rock 1 at 100% and rock 2
        // (gold) at 40% — only rock 1 counts toward helium.
        live[1] = new Allegiance.ClientCore.State.AsteroidLiveState(1f, 1f);
        live[2] = new Allegiance.ClientCore.State.AsteroidLiveState(0.4f, 0.4f);
        Assert.Equal(1000f, Sum());

        // ASTEROID_MINED: rock 1 drops to a quarter — the sum tracks it live.
        live[1] = new Allegiance.ClientCore.State.AsteroidLiveState(0.25f, 0.25f);
        Assert.Equal(250f, Sum());

        // A He3 rock in ANOTHER cluster never leaks into this pane.
        live[3] = new Allegiance.ClientCore.State.AsteroidLiveState(1f, 1f);
        Assert.Equal(250f, Sum());
    }

    /// <summary>⭐ P752 — the collapsed (small) view hides the second column —
    /// the helium figure and the special-asteroid groups — exactly as native's
    /// <c>if (m_pimageBkgnd == m_pimageLargeBkgnd)</c> guard
    /// (sectormap.cpp:495-515); the sector name and the asteroid total stay.
    /// </summary>
    [Fact]
    public void CollapsedView_HidesHeliumAndGroups_KeepsNameAndTotal()
    {
        var samples = new[]
        {
            new SectorInfoPaneLayout.AsteroidSample(0x01, 500f),
            new SectorInfoPaneLayout.AsteroidSample(0x09, 0f),
        };
        var expanded = SectorInfoPaneLayout.Build("Alpha", samples, 2, expanded: true, warningText: null);
        var collapsed = SectorInfoPaneLayout.Build("Alpha", samples, 2, expanded: false, warningText: null);

        Assert.Contains("He3 ore: 500", expanded.DisplayLines);
        Assert.DoesNotContain("He3 ore: 500", collapsed.DisplayLines);
        Assert.Contains("Asteroids: 2", collapsed.DisplayLines);
        Assert.Equal("Alpha Sector", collapsed.DisplayLines[0]);
        // The aggregation itself is unchanged — only the display set shrinks.
        Assert.Equal(500f, collapsed.HeliumOre);
        Assert.Equal(2, collapsed.Groups.Count);
    }

    /// <summary>⭐ P752 — the mouse-over cluster's warning text rides the pane
    /// data (native draws it at the pane origin, sectormap.cpp:517-534).</summary>
    [Fact]
    public void WarningText_RidesThePaneData()
    {
        var pane = SectorInfoPaneLayout.Build("Alpha", Array.Empty<SectorInfoPaneLayout.AsteroidSample>(),
            null, expanded: true, warningText: "Station at risk");
        Assert.Equal("Station at risk", pane.WarningText);
        Assert.Null(SectorInfoPaneLayout.Build("Alpha", Array.Empty<SectorInfoPaneLayout.AsteroidSample>(),
            null, expanded: true, warningText: null).WarningText);
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
