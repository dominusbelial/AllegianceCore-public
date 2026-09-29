using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="RadarTargetSelection"/> — the P436 decision
/// of whether a radar contact is the player's own selected target. No graphics
/// device (same discipline as <see cref="RadarBracketLayoutTests"/>). These pin
/// the isolation guarantee the brief asks for: given a contact list + a selected
/// target id, the RIGHT contact is flagged as the highlight and nothing else.
/// </summary>
public class RadarTargetSelectionTests
{
    private static RadarBracketRenderer.RadarContactType T(string kind) => kind switch
    {
        "ship" => RadarBracketRenderer.RadarContactType.Ship,
        "station" => RadarBracketRenderer.RadarContactType.Station,
        _ => RadarBracketRenderer.RadarContactType.Warp,
    };

    [Fact]
    public void Matching_Kind_And_Id_IsSelected()
    {
        Assert.True(RadarTargetSelection.IsSelected(
            T("ship"), 42, T("ship"), 42));
        Assert.True(RadarTargetSelection.IsSelected(
            T("station"), 7, T("station"), 7));
        Assert.True(RadarTargetSelection.IsSelected(
            T("warp"), 3, T("warp"), 3));
    }

    [Fact]
    public void SameType_DifferentId_IsNotSelected()
    {
        Assert.False(RadarTargetSelection.IsSelected(
            T("ship"), 41, T("ship"), 42));
    }

    [Fact]
    public void SameId_DifferentType_IsNotSelected()
    {
        // A station and a ship can share an id number — only the type+id PAIR
        // identifies the target, so a same-id different-kind contact must NOT be
        // flagged (otherwise the highlight could land on the wrong object).
        Assert.False(RadarTargetSelection.IsSelected(
            T("station"), 42, T("ship"), 42));
        Assert.False(RadarTargetSelection.IsSelected(
            T("warp"), 42, T("ship"), 42));
    }

    [Fact]
    public void Null_Selection_FlagsNothing()
    {
        Assert.False(RadarTargetSelection.IsSelected(T("ship"), 42, null, null));
        Assert.False(RadarTargetSelection.IsSelected(T("ship"), 42, T("ship"), null));
        Assert.False(RadarTargetSelection.IsSelected(T("ship"), 42, null, 42));
    }

    /// <summary>The brief's exact isolation guarantee: given a contact LIST plus
    /// a selected target id, exactly the right one is flagged and none of the
    /// others (a mixed bag of same-sector ships/stations/warps).</summary>
    [Fact]
    public void ExactlyTheSelectedId_IsFlagged_AcrossAList()
    {
        const short selectedId = 42;
        var shipIds = new short[] { 11, 42, 73 };
        var stationIds = new short[] { 42 };   // same id, wrong kind — must NOT flag
        var warpIds = new short[] { 199 };

        int flagged = 0;
        foreach (var id in shipIds)
            if (RadarTargetSelection.IsSelected(T("ship"), id, T("ship"), selectedId))
                flagged++;
        // Stations/warps can never match a SHIP selection, whatever the id.
        foreach (var id in stationIds)
            Assert.False(RadarTargetSelection.IsSelected(T("station"), id, T("ship"), selectedId));
        foreach (var id in warpIds)
            Assert.False(RadarTargetSelection.IsSelected(T("warp"), id, T("ship"), selectedId));

        // Exactly one ship in the list is flagged — the selected id — never more.
        Assert.Equal(1, flagged);
    }
}
