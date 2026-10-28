using System.Collections.Generic;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P864d — the TEAM SCREEN's team-lobby pool: native lists the lobby side
/// ("Not on a team", <c>clintlib/clintlib.h:571</c>) as a team row with its
/// member count (<c>WinTrek/teamscreen.cpp:378-386</c>) and draws that side's
/// members by name with a per-player status (<c>:470-510</c>), and the team
/// leader accepts a pending join request from the same list
/// (<c>:2998-3013</c>). The port derives the members from the normal
/// player-info traffic — <see cref="ClientWorld.TeamLobbyMembers"/> reads the
/// ship entities the server's <c>PLAYERINFO(10)</c>/<c>EXPORT(26)</c>/
/// <c>QUIT_SIDE(126)</c>/<c>JOIN_SIDE(125)</c> messages establish — and both
/// overlays (side-select = the team screen before admission, ready-check = the
/// team lobby) are shared by the Classic and Modern HUD styles, so these pure
/// layout tests cover both styles.
/// </summary>
public class P864dTeamLobbyScreenTests
{
    private const short MySide = 1;
    private const short Me = 300;
    private static readonly object Gate = new();   // stand-in for the session's world-read lock

    private static SideRoster Roster(params (short id, int players, string name)[] sides) =>
        new()
        {
            Sides = sides
                .Select(s => new SideRoster.SideRecord(s.id, CivId: 0, (short)s.players, s.name))
                .ToArray(),
        };

    private static ServerShipUpdate PoseFor(short id) =>
        ServerShipUpdate.FromValues(
            id, stateM: 0, new ControlData(0f, 0f, 0f, 0f), time: 1u,
            Vector3.Zero, Orientation.Identity, Vector3.Zero,
            turnRates: new[] { 0f, 0f, 0f },
            hullFrac: 1f, shieldFrac: 1f, ammoFrac: 1f, fuelFrac: 1f,
            energyFrac: 1f, oreFrac: 0f, power: 0f);

    /// <summary>A mission with two lobby members, one seated teammate, and the
    /// local player seated on <see cref="MySide"/> — all from real wire applies.</summary>
    private static ClientWorld LobbyWorld()
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster { SectorId = 1, Ship = PoseFor(Me) });   // MyShipId = Me
        world.Apply(new ExportShip { ShipId = -1, SideId = MySide });      // own-side carrier
        world.Apply(new PlayerInfo { ShipId = Me, ISide = MySide, CharacterName = "Me" });
        world.Apply(new PlayerInfo { ShipId = 101, ISide = MySide, CharacterName = "Ripcord" });
        // Two lobby members, learned exactly the way the wire establishes them:
        // a player-info identity carrying SIDE_TEAMLOBBY (-2).
        world.Apply(new PlayerInfo { ShipId = 201, ISide = ClientWorld.SideTeamLobby, CharacterName = "Zulu" });
        world.Apply(new PlayerInfo { ShipId = 202, ISide = ClientWorld.SideTeamLobby, CharacterName = "Bravo" });
        return world;
    }

    [Fact]
    public void TeamLobbyMembers_lists_only_ships_on_the_lobby_side_by_name()
    {
        var members = LobbyWorld().TeamLobbyMembers();

        Assert.Equal(new[] { "Bravo", "Zulu" }, members.Select(m => m.Name).ToArray());
        Assert.All(members, m => Assert.False(m.IsSelf));
        Assert.All(members, m => Assert.Null(m.RequestingSideId));
        // Seated players (ours and a teammate's) are not in the lobby pool.
        Assert.DoesNotContain(members, m => m.Name == "Me");
        Assert.DoesNotContain(members, m => m.Name == "Ripcord");
    }

    [Fact]
    public void TeamLobbyMembers_marks_the_local_player_and_its_pending_request()
    {
        var world = LobbyWorld();
        // QUIT_SIDE parks the local ship on SIDE_TEAMLOBBY (-2) — the exact
        // server message a FlushSides/RandomizeSides park broadcasts.
        world.Apply(new QuitSide { ShipId = Me, Reason = QuitSideReason.FlushSides });
        Assert.Equal(ClientWorld.SideTeamLobby, world.MySideId);
        // The local player asks for side 0 and waits for the leader's answer.
        world.Apply(new PositionReqBroadcast { ShipId = Me, SideId = 0 });

        var members = world.TeamLobbyMembers();
        var self = members.Single(m => m.IsSelf);
        Assert.Equal("Me", self.Name);
        Assert.Equal((short)0, self.RequestingSideId);
        // The other lobby players carry no request.
        Assert.All(members.Where(m => !m.IsSelf), m => Assert.Null(m.RequestingSideId));
    }

    [Fact]
    public void TeamLobbyMembers_drops_a_player_the_moment_JOIN_SIDE_seats_it()
    {
        var world = LobbyWorld();
        Assert.Equal(2, world.TeamLobbyMembers().Count);
        world.Apply(new JoinSide { ShipId = 201, SideId = 0 });   // Zulu accepted
        Assert.Equal(new[] { "Bravo" }, world.TeamLobbyMembers().Select(m => m.Name).ToArray());
    }

    [Fact]
    public void TeamLobbyMembers_is_empty_when_nobody_is_on_no_side()
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster { SectorId = 1, Ship = PoseFor(Me) });
        world.Apply(new PlayerInfo { ShipId = Me, ISide = MySide, CharacterName = "Me" });
        world.Apply(new PlayerInfo { ShipId = 101, ISide = MySide, CharacterName = "Ripcord" });
        Assert.Empty(world.TeamLobbyMembers());
    }

    [Fact]
    public void SideHumanCount_is_the_running_native_count_not_a_snapshot()
    {
        var world = LobbyWorld();

        // Two humans on our side (us + Ripcord); the lobby side answers 0 like
        // native's SideNumPlayers(SIDE_TEAMLOBBY) (clintlib.h:578).
        Assert.Equal(2, world.SideHumanCount(MySide));
        Assert.Equal(0, world.SideHumanCount(ClientWorld.SideTeamLobby));
        Assert.Equal(0, world.SideHumanCount(7));

        // A teammate leaving the side drops the count immediately — the same
        // RemovePlayer decrement native performs (clintlib.cpp:926-929).
        world.Apply(new JoinSide { ShipId = 101, SideId = 0 });
        Assert.Equal(1, world.SideHumanCount(MySide));
        Assert.Equal(1, world.SideHumanCount(0));
    }

    [Fact]
    public void SideHumanCount_is_null_before_the_first_player_info_burst()
    {
        // A peer that has connected but holds no player table yet: the caller
        // must fall back to the SIDE_ROSTER scalar instead of drawing 0.
        Assert.Null(new ClientWorld().SideHumanCount(MySide));
    }

    // ── SideSelectLayout (the team screen before admission) ───────────────────

    [Fact]
    public void SideSelect_lists_lobby_members_by_name_with_native_status()
    {
        var roster = Roster((0, 1, "Alpha"), (1, 0, "Bravo"));
        roster.LobbyPlayerCount = 2;
        var lobby = new[]
        {
            new SideSelectLayout.LobbyMember("Bravo", false, true),
            new SideSelectLayout.LobbyMember("Me", true, false),
        };

        var rows = SideSelectLayout.Build(roster, 0, false, lobbyMembers: lobby);
        foreach (var row in rows)
            foreach (var ch in row.Text)
                Assert.InRange(ch, '\u0020', '\u007e');
        var text = string.Join("\n", rows.Select(r => r.Text));

        Assert.Contains("Not on a team", text);
        Assert.Contains("players: 2", text);
        Assert.Contains("Bravo", text);
        Assert.Contains("Requesting", text);      // native's player-row status
        Assert.Contains("Ready", text);
        Assert.Contains("(you)", text);
        // Nothing in the lobby section is selectable/highlighted.
        Assert.All(rows.Where(r => r.Text.Contains("Bravo")), r => Assert.False(r.Highlight));
        // The lobby is still not a side a client can request.
        Assert.DoesNotContain(ClientWorld.SideTeamLobby, SideSelectLayout.SelectableSideIds(roster));
    }

    [Fact]
    public void SideSelect_shows_native_pick_a_team_prompt_only_for_a_lobby_player()
    {
        var roster = Roster((0, 1, "Alpha"), (1, 0, "Bravo"));
        roster.LobbyPlayerCount = 1;

        var inLobby = string.Join("\n", SideSelectLayout.Build(
            roster, 0, false, lobbyMembers: [new SideSelectLayout.LobbyMember("Me", true, false)],
            amInTeamLobby: true, ownName: "Me").Select(r => r.Text));
        Assert.Contains("YOU NEED TO PICK A TEAM", inLobby);

        var requesting = string.Join("\n", SideSelectLayout.Build(
            roster, 0, false, lobbyMembers: [new SideSelectLayout.LobbyMember("Me", true, true)],
            amInTeamLobby: true, ownName: "Me", ownRequestPending: true).Select(r => r.Text));
        Assert.Contains("YOU ARE REQUESTING TO JOIN A TEAM", requesting);

        var seated = string.Join("\n", SideSelectLayout.Build(
            roster, 0, false, lobbyMembers: [new SideSelectLayout.LobbyMember("Me", true, false)]).Select(r => r.Text));
        Assert.DoesNotContain("YOU NEED TO PICK A TEAM", seated);
    }

    // ── ReadyCheckLayout (the team lobby, where a leader sits) ────────────────

    [Fact]
    public void ReadyCheck_lists_the_lobby_pool_for_every_viewer_and_the_leader_accept_hint()
    {
        IReadOnlyList<ReadyCheckLayout.LobbyRow> lobby = new List<ReadyCheckLayout.LobbyRow>
        {
            new("Bravo", false, "Alpha"),
            new("Zulu", false, null),
        };
        var players = new[] { new ReadyCheckLayout.PlayerRow("you", false, true) };

        var leader = string.Join("\n", ReadyCheckLayout.Build(
            "Alpha", false, players, sideAggregateReady: null, forcedReady: false,
            canForceTeamReady: true, lobby: lobby, canAcceptLobbyRequest: true).Select(r => r.Text));
        Assert.Contains("NOT ON A TEAM  (players: 2)", leader);
        Assert.Contains("Bravo", leader);
        Assert.Contains("Requesting Alpha", leader);
        Assert.Contains("Not Playing", leader);
        Assert.Contains("A  ACCEPT LOBBY REQUEST", leader);

        var member = string.Join("\n", ReadyCheckLayout.Build(
            "Alpha", false, players, sideAggregateReady: null, forcedReady: false,
            canForceTeamReady: false, lobby: lobby).Select(r => r.Text));
        Assert.Contains("NOT ON A TEAM  (players: 2)", member);   // leaders AND everyone else
        Assert.DoesNotContain("ACCEPT LOBBY REQUEST", member);    // only the leader may accept
    }

    [Fact]
    public void ReadyCheck_shows_the_randomize_hint_only_to_the_mission_owner()
    {
        var players = new[] { new ReadyCheckLayout.PlayerRow("you", false, true) };
        var owner = string.Join("\n", ReadyCheckLayout.Build(
            "Alpha", false, players, null, false, false, amMissionOwner: true).Select(r => r.Text));
        var other = string.Join("\n", ReadyCheckLayout.Build(
            "Alpha", false, players, null, false, false).Select(r => r.Text));
        Assert.Contains("R  RANDOMIZE TEAMS", owner);
        Assert.DoesNotContain("RANDOMIZE TEAMS", other);
    }

    [Fact]
    public void ReadyCheck_omits_the_lobby_section_when_nobody_is_waiting()
    {
        var players = new[] { new ReadyCheckLayout.PlayerRow("you", false, true) };
        var text = string.Join("\n", ReadyCheckLayout.Build(
            "Alpha", false, players, null, false, false, lobby: []).Select(r => r.Text));
        Assert.DoesNotContain("NOT ON A TEAM", text);
    }

    // ── ReadyCheckRoster.GatherLobby (the display-input reduction) ────────────

    [Fact]
    public void GatherLobby_names_the_requested_side_and_finds_the_leaders_own_side_request()
    {
        var world = LobbyWorld();
        world.Apply(new SideRoster
        {
            Sides =
            [
                new SideRoster.SideRecord(0, 0, 1, "Alpha"),
                new SideRoster.SideRecord(1, 0, 1, "Bravo"),
            ],
        });
        // Our side's leader is US (SET_TEAM_LEADER for the own ship).
        world.Apply(new SetTeamLeader { ShipId = Me, SideId = MySide });
        // "Zulu" asks for our side: the request the Accept button answers.
        world.Apply(new PositionReqBroadcast { ShipId = 201, SideId = MySide });

        (IReadOnlyList<ReadyCheckLayout.LobbyRow> rows, short? first) result;
        lock (Gate) result = ReadyCheckRoster.GatherLobby(world);

        Assert.Equal(new[] { "Bravo", "Zulu" }, result.rows.Select(r => r.Name).ToArray());
        Assert.Equal("Bravo", result.rows.Single(r => r.Name == "Zulu").RequestingSideName);
        Assert.Null(result.rows.Single(r => r.Name == "Bravo").RequestingSideName);
        Assert.Equal((short)201, result.first);
    }

    [Fact]
    public void GatherLobby_offers_no_accept_when_the_viewer_is_not_the_side_leader()
    {
        var world = LobbyWorld();
        world.Apply(new PositionReqBroadcast { ShipId = 201, SideId = MySide });

        (IReadOnlyList<ReadyCheckLayout.LobbyRow> rows, short? first) result;
        lock (Gate) result = ReadyCheckRoster.GatherLobby(world);

        Assert.Equal(2, result.rows.Count);
        Assert.Null(result.first);   // no SET_TEAM_LEADER for us → not the leader
    }
}
