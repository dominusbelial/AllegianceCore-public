using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

/// <summary>P1078 review (live, 2026-11-24): a carried-forward player receives the
/// next match's MISSIONDEF but no fresh SIDE_ROSTER. Native re-applies every side's
/// civilization from each MISSIONDEF (clintlib/appmsg.cpp:2410-2424 ->
/// Igc/missionigc.cpp:3751-3757), so the side's lifepod hull and civ follow the new
/// match instead of the previous one.</summary>
public sealed class P1078MissionDefCivilizationTests
{
    [Fact]
    public void Next_match_missiondef_replaces_each_sides_civilization_and_keeps_random()
    {
        using var session = new ConnectionSession();
        P718InterfaceMessageTests.Seed(session);   // roster: side 0 civ 1, side 1 civ 3
        P718InterfaceMessageTests.Feed(session, new ChangeTeamCiv { SideId = 0, CivId = 201, Random = true });

        P718InterfaceMessageTests.Feed(session, new MissionDef
        {
            Stage = MissionStageMessage.StageNotStarted, NTems = 2, RgCivId = [1, 25, 0, 0, 0, 0],
        });

        Assert.Equal(new ClientWorld.SideCivilization(1, true), session.World.SideCivilizations[0]);
        Assert.Equal(new ClientWorld.SideCivilization(25, false), session.World.SideCivilizations[1]);
        Assert.Equal((short)1, session.World.SideRoster!.Sides.Single(s => s.SideId == 0).CivId);
        Assert.Equal((short)25, session.World.SideRoster.Sides.Single(s => s.SideId == 1).CivId);
        Assert.False(session.World.SideCivilizations.ContainsKey(2));
    }

    [Fact]
    public void A_missiondef_without_teams_leaves_civilizations_alone()
    {
        using var session = new ConnectionSession();
        P718InterfaceMessageTests.Seed(session);
        P718InterfaceMessageTests.Feed(session, new ChangeTeamCiv { SideId = 0, CivId = 5, Random = false });

        P718InterfaceMessageTests.Feed(session, new MissionDef { Stage = MissionStageMessage.StageStarted });

        Assert.Equal(new ClientWorld.SideCivilization(5, false), session.World.SideCivilizations[0]);
    }
}
