using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P820c — <c>FM_CS_SET_TEAM_INFO(15)</c> decodes and lands in
/// <see cref="ClientWorld"/> (the exact dispatch path
/// <see cref="Session.ConnectionSession"/> uses: <c>SetTeamInfo.Decode</c> then
/// <c>ClientWorld.Apply</c>), so the client stores each side's squad and the
/// team pane can show it (the P820c fix-4 relay, Server.Squads.RelaySideSquad;
/// native client appmsg.cpp:2541-2550). A NA squadID clears the side's squad.
/// </summary>
public class ClientWorldSetTeamInfoTests
{
    /// <summary>A decoded SET_TEAM_INFO with a squad makes
    /// <see cref="ClientWorld.SideSquadName"/> return that squad's name.</summary>
    [Fact]
    public void Decoded_SetTeamInfo_records_the_sides_squad()
    {
        var world = new ClientWorld();
        var msg = new SetTeamInfo { SquadId = 7, SideId = 1, SideName = "Iron Harbor" };
        var decoded = SetTeamInfo.Decode(msg.Encode());   // exactly what ConnectionSession dispatches
        world.Apply(decoded);

        Assert.Equal("Iron Harbor", world.SideSquadName(1));
        Assert.Null(world.SideSquadName(0));   // an unreported side has no squad
    }

    /// <summary>A NA squadID clears the side's squad (native sends squadID=NA
    /// when a side has no squad, SetSideSquad → SetSideName, fsmission.cpp:1584).</summary>
    [Fact]
    public void Na_squadId_clears_the_sides_squad()
    {
        var world = new ClientWorld();
        world.Apply(new SetTeamInfo { SquadId = 7, SideId = 1, SideName = "Iron Harbor" });
        Assert.Equal("Iron Harbor", world.SideSquadName(1));

        world.Apply(new SetTeamInfo { SquadId = -1, SideId = 1, SideName = "Alpha" });   // NA — no squad
        Assert.Null(world.SideSquadName(1));
    }

    /// <summary>The squad id in the wire is opaque to the client (native's int
    /// SquadID); only its name (carried in SideName, native's non-lock squad-game
    /// rule) is shown. A different squad id with a name just updates the name.</summary>
    [Fact]
    public void Different_squad_id_updates_the_name()
    {
        var world = new ClientWorld();
        world.Apply(new SetTeamInfo { SquadId = 7, SideId = 2, SideName = "Red Dawn" });
        world.Apply(new SetTeamInfo { SquadId = 99, SideId = 2, SideName = "Helix Collective" });

        Assert.Equal("Helix Collective", world.SideSquadName(2));
    }
}
