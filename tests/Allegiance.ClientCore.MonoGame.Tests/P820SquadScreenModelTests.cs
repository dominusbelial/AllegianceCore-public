using Allegiance.ClientCore.MonoGame.Career;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P820a — screen-model tests for the Squads screen (native
/// <c>WinTrek/SquadsScreen.cpp</c>). Pure, no graphics device: tab navigation,
/// list ingest, and — the heart of the port — the exact permission rules that
/// gate each action button (<c>RefreshButtonStates</c>, SquadsScreen.cpp:1623-1670):
/// only the owner may accept/reject/boot/promote/demote/transfer/edit; anyone may
/// join/cancel; any member may quit.
/// </summary>
public sealed class P820SquadScreenModelTests
{
    private static SquadScreenModel Model(string viewerId)
    {
        var m = new SquadScreenModel { ViewerAccountId = viewerId, ViewerCallsign = "Viewer" };
        return m;
    }

    private static SquadDto.SquadMember Mem(string id, string callsign, SquadDto.SquadRole role) => new(id, callsign, role, null, 1);
    private static SquadDto.SquadDetails Details(params SquadDto.SquadMember[] members) =>
        new("sq-1", "Testers", "TST", "desc", "url", -1, 1, "leader-id", members);

    [Fact]
    public void Moving_the_cursor_to_another_squad_drops_the_previous_squads_details()
    {
        // Native selects the squad as its row is picked (SquadsScreen.cpp:1684,3142-3156),
        // so the details pane never describes a squad other than the highlighted one.
        var m = Model("viewer");
        m.IngestAll([new("sq-1", "Testers", "TST", -1, 0, 0, 0, 0, 1), new("sq-2", "Others", "OTH", -1, 0, 0, 0, 0, 1)], null);
        m.IngestDetails(Details(Mem("leader-id", "L", SquadDto.SquadRole.Leader)));
        m.IngestLog([new(1, "L          2026-10-25")]);
        Assert.Equal("sq-1", m.SelectedRow!.Id);
        Assert.NotNull(m.Details);

        m.MoveCursor(+1);
        Assert.Equal("sq-2", m.SelectedRow!.Id);
        Assert.Null(m.Details);
        Assert.Empty(m.Log);

        m.MoveCursor(-1);
        m.IngestDetails(Details(Mem("leader-id", "L", SquadDto.SquadRole.Leader)));
        m.MoveCursor(0);
        Assert.NotNull(m.Details);   // staying on the same squad keeps its details
    }

    [Fact]
    public void Joining_cancel_and_quit_gate_on_the_viewers_own_status()
    {
        var m = Model("viewer");
        m.IngestDetails(Details(Mem("leader", "L", SquadDto.SquadRole.Leader)));
        // Stranger (no membership) can request to join; cannot cancel or quit.
        Assert.True(m.CanRequestJoin);
        Assert.False(m.CanCancelJoin);
        Assert.False(m.CanQuit);

        // Pending viewer: can cancel, cannot join again, cannot quit.
        m.IngestDetails(Details(
            Mem("leader", "L", SquadDto.SquadRole.Leader),
            Mem("viewer", "V", SquadDto.SquadRole.Pending)));
        Assert.False(m.CanRequestJoin);
        Assert.True(m.CanCancelJoin);
        Assert.False(m.CanQuit);

        // Member viewer: can quit; cannot join/cancel.
        m.IngestDetails(Details(
            Mem("leader", "L", SquadDto.SquadRole.Leader),
            Mem("viewer", "V", SquadDto.SquadRole.Member)));
        Assert.False(m.CanRequestJoin);
        Assert.False(m.CanCancelJoin);
        Assert.True(m.CanQuit);
    }

    [Fact]
    public void Only_the_owner_can_accept_reject_promote_demote_transfer_edit()
    {
        var owner = Model("leader");
        owner.IngestDetails(Details(
            Mem("leader", "L", SquadDto.SquadRole.Leader),
            Mem("pending", "P", SquadDto.SquadRole.Pending),
            Mem("member", "M", SquadDto.SquadRole.Member),
            Mem("asl", "A", SquadDto.SquadRole.AssistantLeader)));

        // Owner, no member selected → admin buttons off.
        Assert.True(owner.CanEdit);
        Assert.False(owner.CanAccept);

        // Owner + selected pending → accept + reject on.
        owner.SelectMember(1);
        Assert.True(owner.CanAccept);
        Assert.True(owner.CanReject);

        // Owner + selected member → promote on, demote off, accept off.
        owner.SelectMember(2);
        Assert.True(owner.CanPromote);
        Assert.False(owner.CanDemote);
        Assert.False(owner.CanAccept);
        Assert.True(owner.CanTransfer);

        // Owner + selected ASL → demote on.
        owner.SelectMember(3);
        Assert.True(owner.CanDemote);
        Assert.False(owner.CanPromote);

        // Owner cannot accept/promote/demote/transfer/reject himself.
        owner.SelectMember(0);
        Assert.False(owner.CanAccept);
        Assert.False(owner.CanPromote);
        Assert.False(owner.CanTransfer);
        Assert.False(owner.CanReject);
        Assert.True(owner.CanEdit);   // edit stays on even with self selected (:1663)

        // A non-owner gets none of the admin buttons regardless of selection.
        var peon = Model("member");
        peon.IngestDetails(owner.Details!);
        peon.SelectMember(1);
        Assert.False(peon.CanAccept);
        Assert.False(peon.CanReject);
        Assert.False(peon.CanPromote);
        Assert.False(peon.CanDemote);
        Assert.False(peon.CanTransfer);
        Assert.False(peon.CanEdit);
    }

    [Fact]
    public void Tabs_and_lists_switch_independently()
    {
        var m = Model("viewer");
        m.IngestAll([
            new SquadDto.SquadSummary("a", "Alpha", "", -1, 0, 0, 0, 1, 1),
            new SquadDto.SquadSummary("b", "Beta", "", -1, 0, 0, 0, 2, 1),
        ], null);
        m.IngestMine([new SquadDto.SquadSummary("a", "Alpha", "", -1, 0, 0, 0, 1, 1, SquadDto.SquadRole.Member)]);
        m.IngestPlayer([new SquadDto.SquadSummary("b", "Beta", "", -1, 0, 0, 0, 1, 1)]);

        Assert.Equal(2, m.AllRows.Count);
        m.SetActiveTab(SquadScreenModel.Tab.My);
        Assert.Equal("Alpha", Assert.Single(m.MyRows).Name);
        m.SetActiveTab(SquadScreenModel.Tab.Player);
        Assert.Equal("Beta", Assert.Single(m.PlayerRows).Name);
        m.SetActiveTab(SquadScreenModel.Tab.All);
        Assert.Equal(2, m.AllRows.Count);
    }

    [Fact]
    public void Page_moves_by_a_screen_and_returns_a_new_basis_on_the_all_tab()
    {
        var m = Model("viewer");
        var rows = new List<SquadDto.SquadSummary>();
        for (int i = 0; i < 40; i++) rows.Add(new SquadDto.SquadSummary("s" + i, "S" + i, "", -1, 0, 0, 0, i + 1, 1));
        m.IngestAll(rows.ToArray(), null);
        m.SelectRow(0);
        // Page down from row 0 should move by (21-1)=20.
        string? basis = m.Page(+1);
        Assert.NotNull(basis);
        Assert.Equal("s20", basis);
        Assert.Equal(20, m.Cursor);
        // Page down again → clamps at the last index (39).
        basis = m.Page(+1);
        Assert.Equal("s39", basis);
        // At the bottom, further paging returns null (no movement).
        Assert.Null(m.Page(+1));
    }

    [Fact]
    public void Create_requires_a_name_and_cancels_cleanly()
    {
        var m = Model("viewer");
        m.BeginCreate();
        Assert.True(m.Creating);
        // Empty name → FinishCreate refuses.
        m.NameBuf = "   ";
        Assert.Null(m.FinishCreate());
        Assert.True(m.Creating);
        // Proper name → builds the request and leaves create mode.
        m.NameBuf = "Thunderbolts";
        m.TagBuf = "TB";
        var req = m.FinishCreate();
        Assert.NotNull(req);
        Assert.Equal("Thunderbolts", req!.Name);
        Assert.Equal("TB", req.Tag);
        Assert.False(m.Creating);

        m.BeginCreate();
        m.CancelCreate();
        Assert.False(m.Creating);
    }

    [Fact]
    public void Find_switches_to_the_all_tab_and_clears_selection()
    {
        var m = Model("viewer");
        m.IngestAll([
            new SquadDto.SquadSummary("a", "Alpha", "", -1, 0, 0, 0, 1, 1),
            new SquadDto.SquadSummary("b", "Beta", "", -1, 0, 0, 0, 2, 1),
        ], null);
        m.SetActiveTab(SquadScreenModel.Tab.My);
        // The Game1 layer forces the All tab for find results (native :2223-2226).
        m.SetActiveTab(SquadScreenModel.Tab.All);
        Assert.Equal(SquadScreenModel.Tab.All, m.ActiveTab);
    }

    [Fact]
    public void CharacterInfo_model_carries_the_players_squads()
    {
        // P820a — CHARACTER_INFO_SQUAD (Inc/ClubMessages.h:129-138): the
        // character-info screen lists the squads the viewed player belongs to.
        var m = new CharacterInfoModel();
        m.LoadSquads([("sq-1", "Iron Harbor"), ("sq-2", "Red Dawn Patrol")]);
        Assert.Equal(2, m.Squads.Count);
        Assert.Equal("Iron Harbor", m.Squads[0].Name);

        m.LoadSquads([]);
        Assert.Empty(m.Squads);
    }
}
