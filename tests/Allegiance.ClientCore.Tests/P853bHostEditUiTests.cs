using System.Collections.Concurrent;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P853b — the client UI half of live host editing: the side leader's TEAM NAME
/// edit (native <c>TeamSettingsDialogPopup</c>, <c>WinTrek/teamscreen.cpp:856-1104</c>)
/// and the mission owner's GAME SETTINGS form (native <c>NewGameScreen</c>,
/// <c>WinTrek/newgamescreen.cpp:1034-1130</c>), both riding the P853 wire
/// (<c>FM_CS_SET_TEAM_INFO(15)</c> / <c>FM_CS_MISSIONPARAMS(113)</c>).
/// <list type="bullet">
/// <item><b>Visibility</b> — leader-only / owner-only, and the
/// <c>STAGE_NOTSTARTED</c> + <c>bLockTeamSettings</c> + <c>bSquadGame</c> gates
/// the server itself enforces (<c>FedSrv.CPP:7433-7436,7560</c>).</item>
/// <item><b>The form's message</b> — built from the mission's advertised
/// <c>MissionParams</c> (MISSIONDEF), passes the shared
/// <c>MissionParamsMsg.Invalid(true)</c>, and leaves every field it does not
/// model untouched (the server adopts the whole blob, <c>fsmission.cpp:1843</c>).</item>
/// <item><b>An invalid edit</b> shows the <c>Invalid(true)</c> string and plans
/// nothing — the server <c>TrapHackBoot</c>s an invalid sender
/// (<c>FedSrv.CPP:7534</c>).</item>
/// <item><b>A rename</b> sends <c>SET_TEAM_INFO</c> with <c>squadID = NA</c> and
/// the trimmed, 24-character-capped name (<c>teamscreen.cpp:895,1035-1082</c>).</item>
/// </list>
/// </summary>
[Collection("LiveNetwork")]
public sealed class P853bHostEditUiTests
{
    private static void Feed(ConnectionSession session, params IFedMessage[] messages) =>
        ((ITransportSite)session).OnReceive(ConnectionId.Server,
            FedPacket.Pack(messages.Select(m => m.Encode()).ToArray()));

    /// <summary>A full, valid <c>MissionParams</c> as a server advertises it —
    /// with a few fields the form does NOT model (He3 density, treasure rates,
    /// starting money, the artifact percentage, a custom map file) so the
    /// "everything else is preserved" test is meaningful.</summary>
    private static MissionParamsMsg Params(bool lockTeamSettings = false, bool squadGame = false,
        short teams = 2, short lives = 0x7FFF)
    {
        var p = new MissionParamsMsg
        {
            StrGameName = "P853b Lobby",
            SzIgcStaticFile = "PCore001",
            SzCustomMapFile = "",
            VerIgcCore = 12,
            Bits =
            {
                [MissionParamsMsg.BitObjectModelCreated] = true,
                [MissionParamsMsg.BitAllowDevelopments] = true,
                [MissionParamsMsg.BitScoresCount] = true,
                [MissionParamsMsg.BitAllowAlliedRip] = true,
                [MissionParamsMsg.BitLockTeamSettings] = lockTeamSettings,
                [MissionParamsMsg.BitSquadGame] = squadGame,
            },
            NTems = teams,
            IMinRank = -1,
            IMaxRank = 1000,
            NMinPlayersPerTeam = 1,
            NMaxPlayersPerTeam = 100,
            NTotalMaxPlayersPerGame = 0x7FFF,
            ILives = lives,
            IGoalConquestPercentage = 100,
            IGoalTerritoryPercentage = 100,
            IGoalArtifactsPercentage = 100,
            FGoalTeamMoney = 0f,
            DtGameLength = 0f,
            MmMapType = 9,
            // Fields the form does not touch:
            FHe3Density = 2.5f,
            NPlayerSectorTreasureRate = 0.2f / 60f,
            NNeutralSectorTreasureRate = 0.5f / 60f,
            FStartingMoney = 1.25f,
            IMaxImbalance = 3,
            NInitialMinersPerTeam = 2,
            NMaxDronesPerTeam = 6,
            RgCivId = [1, 3, 0, 0, 0, 0],
        };
        return p;
    }

    /// <summary>A client seated on side 0 as its leader, mission NotStarted, with
    /// the server's full params advertisement applied. <paramref name="owner"/>
    /// additionally makes it the mission owner (SET_MISSION_OWNER).</summary>
    private static ConnectionSession JoinedSession(bool owner = false, MissionParamsMsg? advertised = null,
        int assembledSides = 2)
    {
        var session = new ConnectionSession();
        Seat(session, owner, advertised, assembledSides);
        return session;
    }

    /// <summary>The join burst a real server sends (MISSIONDEF with the whole
    /// params block, JOINED_MISSION, SIDE_ROSTER, PLAYERINFO, SET_TEAM_LEADER,
    /// optional SET_MISSION_OWNER, then SET_CLUSTER which assigns MyShipId).</summary>
    private static void Seat(ConnectionSession session, bool owner = false, MissionParamsMsg? advertised = null,
        int assembledSides = 2)
    {
        var p = advertised ?? Params();
        var sides = Enumerable.Range(0, assembledSides)
            .Select(i => new SideRoster.SideRecord((short)i, (short)(i == 0 ? 1 : 3), (short)1,
                i == 0 ? "Alpha" : "Bravo" + i))
            .ToArray();
        Feed(session,
            new MissionDef
            {
                SzIGCStaticFile = "pcore001",
                VerIgcCore = p.VerIgcCore,
                NTems = p.NTems,
                RgCivId = p.RgCivId,
                ILives = p.ILives,
                Params = p,   // P853b — the whole params block rides MISSIONDEF
            },
            new JoinedMission { ShipId = 10 },
            new SideRoster { Sides = sides },
            new PlayerInfo { ShipId = 10, ISide = 0, CharacterName = "Leader" },
            new SetTeamLeader { SideId = 0, ShipId = 10 });
        if (owner)
            Feed(session, new SetMissionOwner { SideId = 0, ShipId = 10 });
        var payload = new byte[ServerShipUpdate.Width];
        payload[0] = 10;   // SET_CLUSTER assigns MyShipId from the pose payload
        var reader = new WireReader(payload);
        session.World.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.DecodeBody(ref reader) });
    }

    // ── the team-name edit ────────────────────────────────────────────────

    /// <summary>Native's edit pane length cap: <c>c_cbName - 1</c> = 24
    /// (<c>teamscreen.cpp:897</c>), the same width the server truncates to
    /// (<c>fsmission.cpp:1569-1572</c>).</summary>
    [Fact]
    public void TeamName_cap_is_native_c_cbName_minus_one()
    {
        Assert.Equal(24, TeamNameEdit.MaxLength);
        Assert.Equal(SetTeamInfo.SideNameCapacity - 1, TeamNameEdit.MaxLength);
        Assert.Equal(-1, TeamNameEdit.NoSquad);
    }

    /// <summary>⭐ Leader-only, and only while the mission is NOT started with team
    /// settings unlocked and in a non-squad game — the same conditions the server
    /// checks before it would accept the message (<c>FedSrv.CPP:7433-7436</c>,
    /// <c>teamscreen.cpp:1592-1594,960-965</c>).</summary>
    [Fact]
    public void TeamName_is_leader_only_and_gated_on_stage_lock_and_squad_game()
    {
        using var session = JoinedSession();
        var world = session.World;
        Assert.True(TeamNameEdit.CanEdit(world));                       // leader, NotStarted, unlocked
        Assert.Null(TeamNameEdit.BlockedReason(world));
        Assert.Equal("Alpha", TeamNameEdit.CurrentName(world));

        // Not the side's leader any more: no affordance at all (native hides the
        // button, teamscreen.cpp:1592-1593).
        Feed(session, new SetTeamLeader { SideId = 0, ShipId = 11 });
        Assert.False(TeamNameEdit.CanEdit(world));
        Assert.Null(TeamNameEdit.BlockedReason(world));                 // not a leader → row is read-only, no reason
        Feed(session, new SetTeamLeader { SideId = 0, ShipId = 10 });
        Assert.True(TeamNameEdit.CanEdit(world));

        // The game started (the server would silently ignore the rename).
        world.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageStarted });
        Assert.False(TeamNameEdit.CanEdit(world));
        Assert.Equal("GAME ALREADY STARTED", TeamNameEdit.BlockedReason(world));
    }

    [Fact]
    public void TeamName_is_refused_when_locked_or_a_squad_game()
    {
        using (var locked = JoinedSession(advertised: Params(lockTeamSettings: true)))
        {
            Assert.False(TeamNameEdit.CanEdit(locked.World));
            // A leader sees WHY (the server ignores the message silently).
            Assert.Equal("TEAM SETTINGS LOCKED", TeamNameEdit.BlockedReason(locked.World));
        }
        using (var squad = JoinedSession(advertised: Params(squadGame: true)))
        {
            Assert.False(TeamNameEdit.CanEdit(squad.World));
            Assert.Equal("SQUAD GAME - NAME COMES FROM THE SQUAD", TeamNameEdit.BlockedReason(squad.World));
        }
    }

    /// <summary>⭐ The rename message: <c>squadID = NA</c> and the typed name
    /// trimmed and capped at 24 characters (<c>teamscreen.cpp:895,1049-1058</c>).</summary>
    [Fact]
    public void TeamName_plan_sends_NA_and_the_trimmed_24_char_name()
    {
        using var session = JoinedSession();
        var world = session.World;

        Assert.True(TeamNameEdit.TryPlan(world, "  Blue Squad  ", out var plan, out var error));
        Assert.Null(error);
        Assert.Equal((short)0, plan.SideId);
        Assert.Equal(-1, plan.SquadId);          // native NA → the RENAME branch
        Assert.Equal("Blue Squad", plan.Name);

        string longName = "  " + new string('X', 40) + "  ";
        Assert.True(TeamNameEdit.TryPlan(world, longName, out var capped, out _));
        Assert.Equal(TeamNameEdit.MaxLength, capped.Name.Length);
        Assert.Equal(new string('X', 24), capped.Name);

        // Control characters never reach the wire.
        Assert.Equal("AB", TeamNameEdit.Sanitize("A\u0001B"));
    }

    /// <summary>⭐ An empty / whitespace edit shows an error and plans NOTHING.</summary>
    [Fact]
    public void TeamName_empty_edit_shows_the_error_and_sends_nothing()
    {
        using var session = JoinedSession();
        Assert.False(TeamNameEdit.TryPlan(session.World, "   ", out _, out var error));
        Assert.Equal("Type a team name first.", error);

        // Not a leader: refused with the permission reason, not the empty-name one.
        Feed(session, new SetTeamLeader { SideId = 0, ShipId = 12 });
        Assert.False(TeamNameEdit.TryPlan(session.World, "Sneaky", out _, out var notLeader));
        Assert.Equal("ONLY THE TEAM LEADER CAN RENAME THE SIDE", notLeader);
    }

    /// <summary>The lobby title follows the server's rename echo (P853
    /// <c>SideNameOverride</c>), so the rename is visible in the pane that issued
    /// it and on every other client.</summary>
    [Fact]
    public void Side_rename_echo_updates_the_lobby_and_pane_name()
    {
        using var session = JoinedSession();
        var world = session.World;
        Feed(session, new SetTeamInfo { SideId = 0, SquadId = -1, SideName = "Blue Squad" });
        Assert.Equal("Blue Squad", TeamNameEdit.CurrentName(world));
        Assert.Equal("Blue Squad", world.SideNameOverride(0));
    }

    // ── the game-settings form ────────────────────────────────────────────

    /// <summary>⭐ Owner-only, and only before the start (native's
    /// <c>bCanChangeSettings</c>, <c>teamscreen.cpp:1585-1589</c>).</summary>
    [Fact]
    public void Settings_form_is_owner_only_and_not_started_only()
    {
        using (var plain = JoinedSession(owner: false))
            Assert.Null(MissionSettingsForm.TryOpen(plain.World));

        using (var owner = JoinedSession(owner: true))
        {
            var form = MissionSettingsForm.TryOpen(owner.World);
            Assert.NotNull(form);
            owner.World.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageStarted });
            Assert.Null(MissionSettingsForm.TryOpen(owner.World));
            Assert.False(form!.TryBuildPlan(owner.World, out _, out var error));
            Assert.Equal("ONLY THE MISSION OWNER CAN CHANGE SETTINGS BEFORE THE START", error);
        }
    }

    /// <summary>⭐ The form's message starts from the mission's current values and
    /// passes <c>MissionParamsMsg.Invalid(true)</c> — the shared Core validator the
    /// server runs before anything else (<c>igc.h:1339-1506</c>,
    /// <c>FedSrv.CPP:7534</c>).</summary>
    [Fact]
    public void Settings_form_builds_params_that_pass_Invalid_from_current_mission_values()
    {
        var advertised = Params(lives: 5, teams: 3);
        using var session = JoinedSession(owner: true, advertised: advertised, assembledSides: 4);
        var world = session.World;
        var form = MissionSettingsForm.TryOpen(world);
        Assert.NotNull(form);

        // The draft IS the advertised block (nothing invented, nothing lost).
        Assert.Equal(advertised.Encode(), form!.Draft.Encode());

        Assert.True(form.TryBuildPlan(world, out var plan, out var error));
        Assert.Null(error);
        Assert.Null(plan.Invalid(bFromClient: true));
        Assert.Equal(MissionParamsMsg.FixedSize, plan.Encode().Length);   // 256 B on the wire
        Assert.Equal(5, plan.ILives);
        Assert.Equal(3, plan.NTems);
        // The two fields the server compares against its own def are echoed as-is
        // (FedSrv.CPP:7545-7548) — a mismatch is a TrapHackBoot.
        Assert.Equal(advertised.NTotalMaxPlayersPerGame, plan.NTotalMaxPlayersPerGame);
        Assert.Equal(advertised[MissionParamsMsg.BitLockGameOpen], plan[MissionParamsMsg.BitLockGameOpen]);
        Assert.False(plan[MissionParamsMsg.BitClubGame]);
    }

    /// <summary>The form models exactly the brief's fields and leaves every other
    /// params cell alone — the server adopts the whole blob
    /// (<c>fsmission.cpp:1843</c>), so a field the client never learned would be
    /// silently zeroed.</summary>
    [Fact]
    public void Settings_form_preserves_the_fields_it_does_not_model()
    {
        var advertised = Params(lives: 3, teams: 2);
        using var session = JoinedSession(owner: true, advertised: advertised);
        var world = session.World;
        var form = MissionSettingsForm.TryOpen(world)!;

        // Edit a few modelled rows.
        form.Move(1);                              // MAX PLAYERS/TEAM
        form.Adjust(form.CursorField, -1);         // native MaxPlayersValues: 100 -> 90
        form.Move(MissionSettingsForm.Fields.Length - 1);   // the last checkbox
        form.Adjust(form.CursorField, +1);          // toggle it
        Assert.True(form.TryBuildPlan(world, out var plan, out var error), error);

        Assert.NotEqual(advertised.NMaxPlayersPerTeam, plan.NMaxPlayersPerTeam);
        // Untouched native fields survive:
        Assert.Equal(advertised.StrGameName, plan.StrGameName);
        Assert.Equal(advertised.SzIgcStaticFile, plan.SzIgcStaticFile);
        Assert.Equal(advertised.VerIgcCore, plan.VerIgcCore);
        Assert.Equal(advertised.FHe3Density, plan.FHe3Density);
        Assert.Equal(advertised.FStartingMoney, plan.FStartingMoney);
        Assert.Equal(advertised.NPlayerSectorTreasureRate, plan.NPlayerSectorTreasureRate);
        Assert.Equal(advertised.NNeutralSectorTreasureRate, plan.NNeutralSectorTreasureRate);
        Assert.Equal(advertised.IGoalArtifactsPercentage, plan.IGoalArtifactsPercentage);
        Assert.Equal(advertised.IMaxImbalance, plan.IMaxImbalance);
        Assert.Equal(advertised.NInitialMinersPerTeam, plan.NInitialMinersPerTeam);
        Assert.Equal(advertised.NMaxDronesPerTeam, plan.NMaxDronesPerTeam);
        Assert.Equal(advertised.RgCivId, plan.RgCivId);
        Assert.Equal(advertised.MmMapType, plan.MmMapType);
        Assert.True(plan[MissionParamsMsg.BitAllowAlliedRip]);
        Assert.True(plan[MissionParamsMsg.BitAllowDevelopments]);
    }

    /// <summary>⭐ An invalid edit shows the <c>Invalid(true)</c> string and plans
    /// NOTHING: the server boots a sender whose params are invalid
    /// (<c>FedSrv.CPP:7534</c>). Native shows the identical message box and never
    /// queues the message (<c>newgamescreen.cpp:1117-1119</c>).</summary>
    [Fact]
    public void Settings_form_invalid_edit_shows_the_Invalid_string_and_sends_nothing()
    {
        using var session = JoinedSession(owner: true);
        var world = session.World;
        var form = MissionSettingsForm.TryOpen(world)!;

        // Turn every winning condition off: Invalid(true) refuses it
        // (igc.h:1349-1352). ConquestPercentValues runs [0,80,90,100], so three
        // LEFT steps take the advertised 100 down to 0.
        for (int i = 0; i < 3; i++) form.Adjust(MissionSettingsForm.Field.Conquest, -1);
        Assert.Equal(0, form.Draft.IGoalConquestPercentage);
        Assert.Equal("You must choose one or more winning conditions.", form.Validate());
        Assert.False(form.TryBuildPlan(world, out _, out var error));
        Assert.Equal("You must choose one or more winning conditions.", error);

        // Native's own additional client check: shrinking teams so that every
        // remaining side is inactive is refused (newgamescreen.cpp:1096-1111).
        using var shrinkSession = JoinedSession(owner: true, advertised: Params(teams: 3), assembledSides: 3);
        var shrinkWorld = shrinkSession.World;
        Feed(shrinkSession,
            new SideInactive { SideId = 0, BActive = false },
            new SideInactive { SideId = 1, BActive = false });
        var shrink = MissionSettingsForm.TryOpen(shrinkWorld)!;
        shrink.CaptureSideActivity(shrinkWorld);
        while (shrink.Draft.NTems > 2)
            shrink.Adjust(MissionSettingsForm.Field.Teams, -1);
        Assert.Equal((short)2, shrink.Draft.NTems);
        Assert.Equal("You can't reduce the number of teams if all remaining teams would be inactive.", shrink.Validate());
    }

    /// <summary>Native's combo stepping over its own value lists
    /// (<c>artwork/newgamescreen.mdl:283-353</c>), with TEAMS capped at the
    /// assembled side count (P853's documented grow clamp: the server cannot
    /// create sides live, <c>MissionParamsMsg</c>/<c>Server.MissionParams.cs</c>).</summary>
    [Fact]
    public void Settings_form_steps_native_values_and_caps_teams_at_the_native_six_slot_maximum()
    {
        using var session = JoinedSession(owner: true, advertised: Params(teams: 2), assembledSides: 4);
        var form = MissionSettingsForm.TryOpen(session.World)!;
        // P871 — growth is live now (InitSide), so the cap is native c_cSidesMax = 6,
        // not the assembled side count.
        Assert.Equal(6, form.MaxTeams);

        Assert.Equal(MissionSettingsForm.Field.Teams, form.CursorField);   // opens on TEAMS
        form.Adjust(MissionSettingsForm.Field.Teams, +1);
        Assert.Equal((short)3, form.Draft.NTems);
        form.Adjust(MissionSettingsForm.Field.Teams, +1);
        Assert.Equal((short)4, form.Draft.NTems);
        form.Adjust(MissionSettingsForm.Field.Teams, +1);
        Assert.Equal((short)5, form.Draft.NTems);
        form.Adjust(MissionSettingsForm.Field.Teams, +1);
        Assert.Equal((short)6, form.Draft.NTems);
        form.Adjust(MissionSettingsForm.Field.Teams, +1);
        Assert.Equal((short)6, form.Draft.NTems);          // capped at the native six slots
        form.Adjust(MissionSettingsForm.Field.Teams, -1);
        Assert.Equal((short)5, form.Draft.NTems);

        // Lives walks native's LivesValues [32767, 0, 1, 2, 3, 4, 9].
        form.Adjust(MissionSettingsForm.Field.Lives, +1);
        Assert.Equal(0, form.Draft.ILives);
        Assert.Equal("0", form.Format(MissionSettingsForm.Field.Lives));
        form.Adjust(MissionSettingsForm.Field.Lives, +1);
        Assert.Equal(1, form.Draft.ILives);

        // Game length is native minutes → params seconds (CountdownTimeValues).
        form.Adjust(MissionSettingsForm.Field.GameLength, +1);
        Assert.Equal(600f, form.Draft.DtGameLength);      // 10 min
        Assert.Equal("10 MIN", form.Format(MissionSettingsForm.Field.GameLength));

        // Checkboxes toggle; their native bit indices are the P853/P856 mapping.
        Assert.Equal(MissionParamsMsg.BitAllowFriendlyFire, MissionSettingsForm.BitFor(MissionSettingsForm.Field.FriendlyFire));
        Assert.Equal(MissionParamsMsg.BitAllowAlliedViz, MissionSettingsForm.BitFor(MissionSettingsForm.Field.AlliedViz));
        Assert.Equal(MissionParamsMsg.BitSquadGame, MissionSettingsForm.BitFor(MissionSettingsForm.Field.SquadGame));
        bool before = form.IsChecked(MissionSettingsForm.Field.FriendlyFire);
        form.Adjust(MissionSettingsForm.Field.FriendlyFire, +1);
        Assert.NotEqual(before, form.IsChecked(MissionSettingsForm.Field.FriendlyFire));

        // Cursor wraps both ways.
        form.Move(-1);
        Assert.Equal(MissionSettingsForm.Fields[^1], form.CursorField);
        form.Move(+1);
        Assert.Equal(MissionSettingsForm.Fields[0], form.CursorField);
    }

    /// <summary>⭐ Both messages cross real TCP exactly once, in the native layouts:
    /// SET_TEAM_INFO(15) with <c>squadID = NA</c>, and MISSIONPARAMS(113) as the
    /// 256-byte params struct the server validates.</summary>
    [Fact]
    public async Task Rename_and_settings_plans_go_out_as_the_native_messages()
    {
        var hostSite = new Site();
        var host = new TcpTransport(hostSite);
        host.Host(0);
        using var session = new ConnectionSession();
        session.Connect(host.BoundPort, "p853b");
        await hostSite.Joined.Task.WaitAsync(TimeSpan.FromSeconds(10));
        for (int i = 0; i < 200 && !session.Connected; i++) await Task.Delay(10);
        Assert.True(session.Connected, "the session transport must be connected before it can send");

        // Seat the peer the way the join burst does: side 0's leader, mission
        // owner, mission NotStarted, the server's whole params advertisement.
        Seat(session, owner: true);

        // The rename: NA + the trimmed, capped name.
        Assert.True(TeamNameEdit.TryPlan(session.World, "  Blue Squad  ", out var rename, out _));
        session.SendSetTeamInfo(rename.SideId, rename.SquadId, rename.Name);
        var teamInfo = SetTeamInfo.Decode(await NextAsync(hostSite, SetTeamInfo.Fmid));
        Assert.Equal((short)0, teamInfo.SideId);
        Assert.Equal(-1, teamInfo.SquadId);
        Assert.Equal("Blue Squad", teamInfo.SideName);

        // The settings form: a valid 256-byte MISSIONPARAMS.
        var form = MissionSettingsForm.TryOpen(session.World);
        Assert.NotNull(form);
        form!.Adjust(MissionSettingsForm.Field.Lives, +1);   // a real edit (unlimited -> 0)
        Assert.True(form.TryBuildPlan(session.World, out var plan, out var error), error);
        session.SendMissionParams(plan);
        var sent = await NextAsync(hostSite, MissionParamsMsg.Fmid);
        Assert.Equal(MissionParamsMsg.FixedSize, sent.Length);
        var decoded = MissionParamsMsg.Decode(sent);
        Assert.Null(decoded.Invalid(bFromClient: true));
        Assert.Equal(0, decoded.ILives);
    }

    /// <summary>⭐ The MISSIONDEF advertisement carries the whole params block, so a
    /// client can start its form from the server's real values (native memcpy's the
    /// misdef, clintlib.cpp:670-673): the flags the port's old advertisement zeroed
    /// — squad game, allied rip, the lock bits — arrive intact, and the wire width
    /// is unchanged at 864.</summary>
    [Fact]
    public void MissionDef_advertises_the_whole_params_block()
    {
        var p = Params(squadGame: true, lockTeamSettings: true, teams: 4, lives: 7);
        p.VerIgcCore = 99;   // the codec's typed view must agree with the block
        p[MissionParamsMsg.BitAllowAlliedRip] = true;
        p[MissionParamsMsg.BitAllowEmptyTeams] = true;
        var def = new MissionDef
        {
            SzIGCStaticFile = "PCore001",
            VerIgcCore = 99,
            NTems = 4,
            RgCivId = [1, 3, 2, 4],
            ILives = 7,
            Params = p,
        };
        var bytes = def.Encode();
        Assert.Equal(MissionDef.FixedSize, bytes.Length);          // no wire change
        var back = MissionDef.Decode(bytes);
        Assert.NotNull(back.Params);
        Assert.True(back.Params![MissionParamsMsg.BitSquadGame]);
        Assert.True(back.Params[MissionParamsMsg.BitLockTeamSettings]);
        Assert.True(back.Params[MissionParamsMsg.BitAllowAlliedRip]);
        Assert.True(back.Params[MissionParamsMsg.BitAllowEmptyTeams]);
        Assert.Equal(4, back.Params.NTems);
        Assert.Equal(7, back.Params.ILives);
        Assert.Equal(2.5f, back.Params.FHe3Density);
        Assert.Equal("PCore001", back.Params.SzIgcStaticFile);      // the P29 reload key survives
        Assert.Equal(99, back.Params.VerIgcCore);
        Assert.Equal(p.Encode(), back.Params.Encode());
        // The typed view still agrees.
        Assert.Equal(7, back.ILives);
        Assert.Equal(4, back.NTems);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private sealed class Site : ITransportSite
    {
        public readonly TaskCompletionSource<ConnectionId> Joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ConcurrentQueue<byte[]> Received = new();
        public readonly SemaphoreSlim Arrived = new(0);
        public void OnPeerJoined(ConnectionId id, string name) => Joined.TrySetResult(id);
        public void OnPeerLeft(ConnectionId id) { }
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId id, ReadOnlySpan<byte> bytes)
        {
            foreach (var message in FedPacket.Unpack(bytes)) Received.Enqueue(message);
            Arrived.Release();
        }
    }

    private static async Task<byte[]> NextAsync(Site host, ushort fmid)
    {
        while (await host.Arrived.WaitAsync(TimeSpan.FromSeconds(10)))
            if (host.Received.TryDequeue(out var message) && message.Length >= 4
                && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(message.AsSpan(2)) == fmid)
                return message;
        throw new TimeoutException($"no fmid {fmid} packet reached the host");
    }
}
