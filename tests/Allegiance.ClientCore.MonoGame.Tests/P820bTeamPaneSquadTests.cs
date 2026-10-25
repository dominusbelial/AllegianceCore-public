using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P820b — the team pane shows a side's squad (relayed like native SET_TEAM_INFO
/// squadID, fsmission.cpp:1584-1590) on its side header, in the shared layout so
/// it appears in both HUD styles. Headless (no graphics device).
/// </summary>
public sealed class P820bTeamPaneSquadTests
{
    private static ScoreRosterEntry Entry(short side, string name) =>
        new(name, side, 0, 0, 0, 0);

    [Fact]
    public void Side_header_shows_the_squad_when_one_is_resolved()
    {
        var lines = TeamPaneLayout.Build(
            [Entry(0, "PilotA")], [],
            new Dictionary<string, TeamPaneLayout.ShipBrief>(),
            null, null, null, false, "PilotA",
            sideSquadName: sideId => sideId == 0 ? "Iron Harbor" : null);
        var header = lines.First(l => l.Kind == TeamPaneLayout.LineKind.SideHeader);
        Assert.Contains("Iron Harbor", header.Text);
    }

    [Fact]
    public void Side_header_omits_the_squad_when_none_exists()
    {
        var lines = TeamPaneLayout.Build(
            [Entry(0, "PilotA")], [],
            new Dictionary<string, TeamPaneLayout.ShipBrief>(),
            null, null, null, false, "PilotA");   // no resolver at all
        var header = lines.First(l => l.Kind == TeamPaneLayout.LineKind.SideHeader);
        Assert.DoesNotContain("[", header.Text);
    }
}
