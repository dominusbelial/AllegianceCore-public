using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P752 — pins the starmap hover rule (native <c>SectorMapPane::MouseMove</c>,
/// WinTrek/sectormap.cpp:1413-1461): hovering a cluster selects it AND makes it
/// the mouse-over cluster whose warning text the info pane draws; hovering
/// empty map space reverts the selection to the CHAT cluster (native
/// <c>GetChatCluster()</c>, sectormap.cpp:1446 — not the own ship's sector); the
/// pointer leaving the map entirely changes nothing.
/// </summary>
public class StarmapHoverTests
{
    [Fact]
    public void HoveringACluster_SelectsIt_AndMarksItAsMouseOver()
    {
        var (selected, mouseOver, hoverCue) = StarmapHover.Move(
            pointerInsideMap: true, hitCluster: 12, chatCluster: 3, currentSelected: 3);
        Assert.Equal((short?)12, selected);
        Assert.Equal((short?)12, mouseOver);

        // P764 / SIM-0330 — native plays mouseoverSound only when the hovered
        // cluster differs from the current selection (sectormap.cpp:1440-1443):
        // hovering a NEW cluster cues, hovering the SAME cluster does not.
        Assert.True(hoverCue);
        var (_, _, repeatCue) = StarmapHover.Move(
            pointerInsideMap: true, hitCluster: 12, chatCluster: 3, currentSelected: 12);
        Assert.False(repeatCue);
    }

    [Fact]
    public void HoveringEmptyMapSpace_RevertsSelectionToChatCluster_AndClearsMouseOver()
    {
        // The chat cluster (e.g. the docked command view's viewed sector) can
        // differ from the own ship's status sector; native reverts to the chat
        // cluster (GetChatCluster, sectormap.cpp:1446).
        var (selected, mouseOver, hoverCue) = StarmapHover.Move(
            pointerInsideMap: true, hitCluster: null, chatCluster: 7, currentSelected: 12);
        Assert.Equal((short?)7, selected);
        Assert.Null(mouseOver);
        Assert.False(hoverCue);   // no cluster hovered → no mouseoverSound
    }

    [Fact]
    public void HoveringEmptyMap_WithUnknownChatCluster_KeepsTheCurrentSelection()
    {
        var (selected, mouseOver, hoverCue) = StarmapHover.Move(
            pointerInsideMap: true, hitCluster: null, chatCluster: null, currentSelected: 12);
        Assert.Equal((short?)12, selected);
        Assert.Null(mouseOver);
        Assert.False(hoverCue);
    }

    [Fact]
    public void PointerOutsideTheMap_ChangesNothingExceptTheMouseOver()
    {
        var (selected, mouseOver, hoverCue) = StarmapHover.Move(
            pointerInsideMap: false, hitCluster: null, chatCluster: 3, currentSelected: 12);
        Assert.Equal((short?)12, selected);
        Assert.Null(mouseOver);
        Assert.False(hoverCue);
        // A hit cluster reported while outside the map is ignored entirely.
        var (selected2, mouseOver2, hoverCue2) = StarmapHover.Move(
            pointerInsideMap: false, hitCluster: 9, chatCluster: 3, currentSelected: 12);
        Assert.Equal((short?)12, selected2);
        Assert.Null(mouseOver2);
        Assert.False(hoverCue2);
    }
}

/// <summary>
/// P752 — pins every branch of the starmap's per-sector threat presentation
/// (native sectormap.cpp:1063-1082's threshold cascade and the
/// GetClusterWarningText strings drawn on hover, sectormap.cpp:517-534).
/// </summary>
public class StarmapThreatPresentationTests
{
    /// <summary>Every warning level 0..15 mapped to its native icon tier.</summary>
    public static TheoryData<short, StarmapThreatPresentation.Tier> Levels => new()
    {
        { 0,  StarmapThreatPresentation.Tier.None },    // NoThreat
        { 1,  StarmapThreatPresentation.Tier.None },    // enemy fighter present: NO icon
        { 2,  StarmapThreatPresentation.Tier.None },    // enemy miner present: NO icon
        { 3,  StarmapThreatPresentation.Tier.None },    // enemy builder present: NO icon
        { 4,  StarmapThreatPresentation.Tier.Combat },  // Conflict
        { 5,  StarmapThreatPresentation.Tier.Miner },   // miner at risk
        { 6,  StarmapThreatPresentation.Tier.Miner },   // builder at risk
        { 7,  StarmapThreatPresentation.Tier.Miner },   // carrier at risk
        { 8,  StarmapThreatPresentation.Tier.Bomber },  // enemy bomber spotted
        { 9,  StarmapThreatPresentation.Tier.Bomber },  // enemy carrier spotted
        { 10, StarmapThreatPresentation.Tier.Capital }, // enemy capital ship spotted
        { 11, StarmapThreatPresentation.Tier.Capital }, // enemy assault ship spotted
        { 12, StarmapThreatPresentation.Tier.Capital }, // enemy transport spotted
        { 13, StarmapThreatPresentation.Tier.Station }, // station at risk
        { 14, StarmapThreatPresentation.Tier.Station }, // station at risk by teleport
        { 15, StarmapThreatPresentation.Tier.Station }, // station at risk of capture
    };

    [Theory]
    [MemberData(nameof(Levels))]
    public void EveryWarningLevel_MapsToItsNativeIconTier(short level, StarmapThreatPresentation.Tier expected)
        => Assert.Equal(expected, StarmapThreatPresentation.IconTier(level));

    [Fact]
    public void OutOfRangeLevels_NeverCrash()
    {
        Assert.Equal(StarmapThreatPresentation.Tier.None, StarmapThreatPresentation.IconTier(-1));
        // A bogus high level falls through to the station tier exactly like
        // native's >= cascade would (GetWarning itself never produces one).
        Assert.Equal(StarmapThreatPresentation.Tier.Station, StarmapThreatPresentation.IconTier(99));
        Assert.Equal("", StarmapThreatPresentation.WarningText(-1));
        Assert.Equal("", StarmapThreatPresentation.WarningText(0));
        Assert.Equal("", StarmapThreatPresentation.WarningText(99));
    }

    [Fact]
    public void WarningText_MatchesTheNativeAlertStrings()
    {
        Assert.Equal("", StarmapThreatPresentation.WarningText(0));
        Assert.Equal("Enemy fighter spotted", StarmapThreatPresentation.WarningText(1));
        Assert.Equal("Conflict", StarmapThreatPresentation.WarningText(4));
        Assert.Equal("Miner at risk", StarmapThreatPresentation.WarningText(5));
        Assert.Equal("Enemy bomber spotted", StarmapThreatPresentation.WarningText(8));
        Assert.Equal("Enemy capital ship spotted", StarmapThreatPresentation.WarningText(10));
        Assert.Equal("Station at risk of capture", StarmapThreatPresentation.WarningText(15));
    }

    /// <summary>⭐ The branch-specific presentation proof the commander threat row
    /// requires: the first matching native branch wins is ordered — walking a strictly increasing warning level never decreases
    /// the tier (native's ordered if/else-if chain, sectormap.cpp:1066-1082).
    /// </summary>
    [Fact]
    public void HighestNativeBranchWins_TheCascadeIsOrdered()
    {
        // Walk a strictly increasing level sequence and assert the tier never
        // decreases — the cascade's defining property (native's ordered
        // if/else-if chain, sectormap.cpp:1066-1082).
        var tiers = new System.Collections.Generic.List<StarmapThreatPresentation.Tier>();
        for (short level = 0; level <= 15; level++)
            tiers.Add(StarmapThreatPresentation.IconTier(level));
        for (int i = 1; i < tiers.Count; i++)
            Assert.True(tiers[i] >= tiers[i - 1],
                "tier decreased at level " + i + ": " + tiers[i - 1] + " -> " + tiers[i]);
    }
}
