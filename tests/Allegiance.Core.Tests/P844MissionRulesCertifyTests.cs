using System.Reflection;
using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Lobby.Career;
using Allegiance.Server;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Srv = global::Allegiance.Server.Server;
using FltBot = global::Allegiance.Bot.Bot;
using Lmr = global::Allegiance.Server.LobbyMatchReport;
using Lmre = global::Allegiance.Server.LobbyMatchResultEntry;
using Lgr = global::Allegiance.Server.LobbyGameResult;
using Ltr = global::Allegiance.Server.LobbyTeamResult;

/// <summary>
/// P844 — certification tests for the "FedSrv mission rules (event-driven):
/// game over, results, victory checks" batch (SIM-0522, SIM-0524..0534). Each
/// test pins a native body branch the row certification found divergent and
/// fixed, or (where the row was already equal) the row's main contract.
/// Native bodies: FedSrv/fsmission.cpp:2691-3899.
/// </summary>
[Collection("LiveNetwork")]
public class P844MissionRulesCertifyTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int teams = 2) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P844 Test", Map = "XXV_simple.igc", Teams = teams, CivIds = [1, 3, 1], SideNames = ["Alpha", "Bravo", "Charlie"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "P844ABCDEFGHIJ", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 844u, DwCookie = 0x844u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8440000000000000000ul, DrmHash = "p844",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static short Seat(Srv server, FltBot bot, string name)
    {
        bot.Connect(server.BoundPort, name);
        bot.SendLogonRequest(Request(name));
        int seat = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(seat < 4000, $"{name} did not seat");
        return bot.ShipAssignment!.Ship.ShipId;
    }

    private static Ship ShipOf(Srv server, short shipId) =>
        server.Ships.First(s => s.Ship.Id == shipId).Ship;

    private static Mission Assembly()
    {
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "XXV_simple.igc")));
        var sides = new[]
        {
            new SideSpec(0, "Alpha", 1),
            new SideSpec(1, "Bravo", 3),
        };
        var m = Mission.Assemble(core, map, sides, new RecordingIgcSite());
        m.Stage = MissionStage.Started;
        return m;
    }

    // ── SIM-0532 / SIM-0534 — STAGE_STARTED-only gates ─────────────────────

    /// <summary>P844 — CheckForVictoryByKills gates on
    /// <c>STAGE_STARTED == GetStage()</c> ONLY (fsmission.cpp:3751) — there is
    /// no Starting allowance (unlike the station-kill recompute at :3575-3578).
    /// Before the fix the port recomputed during STAGE_STARTING and a seeded
    /// kill count crowned a winner before the game began.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Kills_win_never_fires_in_starting_stage()
    {
        Require.Artwork();
        var m = Assembly();
        m.Stage = MissionStage.Starting;
        m.Goals.TeamKills = 1;
        m.Sides[0].Kills = 5;                       // >= nGoalTeamKills

        var (w, _) = m.RecomputeFlagsAndKills();
        Assert.Null(w);                             // fsmission.cpp:3751 gate
        Console.WriteLine("P844-KILLS-STAGE: STAGE_STARTING mission never wins by kills (fsmission.cpp:3751)");
    }

    /// <summary>P844 — CheckForVictoryByFlags gates on STAGE_STARTED only
    /// (fsmission.cpp:3864), for BOTH its legs: real flags (fsmission.cpp:3883)
    /// and the team-lobby artifact sentinel (fsmission.cpp:3890). Before the
    /// fix the port recomputed both during STAGE_STARTING.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Flags_and_artifact_wins_never_fire_in_starting_stage()
    {
        Require.Artwork();
        var m = Assembly();
        m.Stage = MissionStage.Starting;
        m.Goals.Flags = 1;
        m.Sides[0].Flags = 5;
        var (wf, _) = m.RecomputeFlagsAndKills();
        Assert.Null(wf);                            // fsmission.cpp:3864 gate

        m.Goals.Flags = 0;
        m.Goals.Artifacts = 1;
        m.Sides[0].Flags = 0;
        m.Sides[0].Artifacts = 5;
        var (wa, _) = m.RecomputeProsperityAndArtifacts();
        Assert.Null(wa);                            // fsmission.cpp:3864 gate (artifact leg)
        Console.WriteLine("P844-FLAGS-STAGE: STAGE_STARTING mission never wins by flags or artifacts (fsmission.cpp:3864)");
    }

    /// <summary>P844 — main-contract pin for SIM-0532/SIM-0534: a STARTED
    /// mission still wins by kills and by flags/artifacts exactly as native.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Started_mission_still_wins_by_kills_flags_artifacts()
    {
        Require.Artwork();
        var m = Assembly();
        m.Goals.TeamKills = 10;
        m.Sides[1].Kills = 10;
        var (wk, _) = m.RecomputeFlagsAndKills();
        Assert.Equal((short)1, wk!.Id);             // fsmission.cpp:3756

        m.Goals.TeamKills = 0;
        m.Sides[1].Kills = 0;
        m.Goals.Flags = 3;
        m.Sides[0].Flags = 3;
        var (wfl, _) = m.RecomputeFlagsAndKills();
        Assert.Equal((short)0, wfl!.Id);            // fsmission.cpp:3883

        m.Goals.Flags = 0;
        m.Sides[0].Flags = 0;
        m.Goals.Artifacts = 2;
        m.Sides[1].Artifacts = 2;
        var (wa, _) = m.RecomputeProsperityAndArtifacts();
        Assert.Equal((short)1, wa!.Id);             // fsmission.cpp:3890
        Console.WriteLine("P844-GOALS: Started mission wins by kills/flags/artifacts as native");
    }

    // ── SIM-0533 — allies override + AllowEmptyTeams gate ──────────────────

    /// <summary>P844 — native's allies override (fsmission.cpp:3820-3856): the
    /// pairwise test holds iff the winner carries an alliance group and every
    /// ACTIVE side shares it. Inactive sides are skipped (native's
    /// <c>GetActiveF()</c> loop); a side in a different group or with no group
    /// breaks it; a winner with no group never qualifies (:5397).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void AllActiveSidesAllied_matches_native_override()
    {
        Require.Artwork();
        var m = Assembly();
        m.Sides[0].AllyGroup = 5;
        m.Sides[1].AllyGroup = 5;
        Assert.True(Srv.AllActiveSidesAllied(m, m.Sides[0]));   // one group, all active
        Assert.True(Srv.AllAlliedWinner(m) is not null);

        m.Sides[1].AllyGroup = null;                            // an active un-allied side breaks it
        Assert.False(Srv.AllActiveSidesAllied(m, m.Sides[0]));
        Assert.Null(Srv.AllAlliedWinner(m));

        m.Sides[1].AllyGroup = 5;
        m.Sides[1].Active = false;                              // inactive sides are skipped
        m.Sides[1].AllyGroup = 9;
        Assert.True(Srv.AllActiveSidesAllied(m, m.Sides[0]));

        m.Sides[0].AllyGroup = null;                            // :5397 — winner without a group
        Assert.False(Srv.AllActiveSidesAllied(m, m.Sides[0]));
        Console.WriteLine("P844-ALLY-OVERRIDE: pairwise test matches fsmission.cpp:3820-3856");
    }

    /// <summary>P844 — live allies override: three sides; 0 and 1 are ONE
    /// alliance, 2 un-allied. When side 2 is eliminated (resign), the game is
    /// OVER and the alliance wins — before the fix the two allied survivors
    /// played on forever because the tally saw two populated sides.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Allied_survivors_win_when_third_side_is_eliminated()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config(teams: 3));
        using var a = new FltBot();   // Alpha (side 0)
        using var b = new FltBot();   // Bravo  (side 1)
        using var c = new FltBot();   // Charlie (side 2) — will resign
        Seat(server, a, "PA1");
        Seat(server, b, "PB1");
        Seat(server, c, "PC1");
        var mission = server.Missions[0];
        mission.Stage = MissionStage.Started;
        short side0 = mission.Sides[0].Id, side1 = mission.Sides[1].Id, side2 = mission.Sides[2].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, a.ShipAssignment!.Ship.ShipId), side0));
        Assert.True(server.TryJoinSide(ShipOf(server, b.ShipAssignment!.Ship.ShipId), side1));
        Assert.True(server.TryJoinSide(ShipOf(server, c.ShipAssignment!.Ship.ShipId), side2));

        // P190 alliance: sides 0 and 1 in one group (the same state
        // UpdateAlliances writes from CHANGE_ALLIANCE(196), fsmission.cpp:5638+).
        mission.Sides[0].AllyGroup = 5;
        mission.Sides[1].AllyGroup = 5;
        a.ResetGameOvers();

        // Charlie resigns → sole voter auto-yes → DeactivateSide(side 2) →
        // CheckForVictoryByInactiveSidesAfterDeactivation → allies override.
        ShipChar(c, "#resign");

        int over = PumpUntil(server, () => mission.Stage == MissionStage.Over, 900);
        Assert.True(over < 900, "allied survivors must win when the un-allied side is eliminated (fsmission.cpp:3820-3856)");
        var go = Assert.Single(a.GameOvers);
        Assert.True(go.SideWinner == side0 || go.SideWinner == side1, $"winner must be an alliance member, got {go.SideWinner}");
        Assert.Contains("&", go.GameoverMessage);   // allied name list, fsmission.cpp:5396-5412
        Console.WriteLine($"P844-ALLY-LIVE: alliance (sides {side0},{side1}) won after side {side2} resigned: \"{go.GameoverMessage}\"");
    }

    /// <summary>P844 — negative control: with NO alliance between the
    /// survivors the override must not fire — the game continues (the
    /// pre-fix behaviour, correct for un-allied sides).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Unallied_survivors_keep_playing_when_third_side_is_eliminated()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config(teams: 3));
        using var a = new FltBot();
        using var b = new FltBot();
        using var c = new FltBot();
        Seat(server, a, "UA1");
        Seat(server, b, "UB1");
        Seat(server, c, "UC1");
        var mission = server.Missions[0];
        mission.Stage = MissionStage.Started;
        Assert.True(server.TryJoinSide(ShipOf(server, a.ShipAssignment!.Ship.ShipId), mission.Sides[0].Id));
        Assert.True(server.TryJoinSide(ShipOf(server, b.ShipAssignment!.Ship.ShipId), mission.Sides[1].Id));
        Assert.True(server.TryJoinSide(ShipOf(server, c.ShipAssignment!.Ship.ShipId), mission.Sides[2].Id));

        ShipChar(c, "#resign");
        int over = PumpUntil(server, () => mission.Stage == MissionStage.Over, 400);
        Assert.True(over >= 400, "two un-allied survivors must keep playing (no override without an alliance)");
        Assert.True(mission.Sides[0].Active && mission.Sides[1].Active, "survivor sides stay active");
        Console.WriteLine("P844-ALLY-NEGATIVE: un-allied survivors keep playing after the third side resigns");
    }

    /// <summary>P844 — native's AllowEmptyTeams gate (fsmission.cpp:3808-3811):
    /// a STAGE_STARTING mission with empty teams allowed never ends via the
    /// win-by-inactive-sides check, even with zero populated sides. Before the
    /// fix the check drew the mission Over. Control: the same call on a STARTED
    /// mission does end it (draw).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Starting_mission_with_empty_teams_never_ends_by_inactive_sides()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        var mission = server.Missions[0];
        mission.AllowEmptyTeams = true;

        mission.Stage = MissionStage.Starting;
        InvokeVictoryCheck(server, mission);
        Assert.NotEqual(MissionStage.Over, mission.Stage);   // fsmission.cpp:3808-3811

        mission.Stage = MissionStage.Started;                // control: Started draws
        InvokeVictoryCheck(server, mission);
        Assert.Equal(MissionStage.Over, mission.Stage);
        Console.WriteLine("P844-AET-GATE: STAGE_STARTING + bAllowEmptyTeams never ends by inactive sides");
    }

    private static void InvokeVictoryCheck(Srv server, Mission mission)
    {
        var mi = typeof(Srv).GetMethod("CheckForVictoryByInactiveSides",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(mi);
        mi!.Invoke(server, [mission]);
    }

    /// <summary>Send a chat line through the bot's chat path (mirrors the
    /// ballot-suite resign flow).</summary>
    private static void ShipChar(FltBot bot, string text) => bot.SendChat(text);

    // ── SIM-0524 / SIM-0525 / SIM-0526 — the results report contract ───────

    /// <summary>P844 — the game-level row (native CQGameResults,
    /// fsmission.cpp:2757-2836), the per-side rows (CQTeamResults,
    /// fsmission.cpp:2845-2915) and the extended per-player columns
    /// (CQPlayerResults, fsmission.cpp:2969-2986) survive the wire: the
    /// server's source-generated (AOT-safe) serialization of
    /// <see cref="LobbyMatchReport"/> deserializes into the lobby's
    /// <see cref="CareerContracts.MatchReport"/> contract with Game, Teams and
    /// the new player columns intact — the same binding the in-process lobby's
    /// /results endpoint performs.</summary>
    [Fact]
    public void Results_report_carries_game_team_and_player_facts_over_the_wire()
    {
        var report = new Lmr(
            "match-p844-1",
            [
                new Lmre("acct-1", 2, 500f, 120f, 3, 1, 1f, 0f, 30, true, false, false, false, true,
                    Ejections: 2, Rescues: 4, Flags: 3, Artifacts: 1, TechsRecovered: 5,
                    WarpsSpotted: 7, AsteroidsSpotted: 11, TimePlayedSeconds: 1817f),
            ],
            Game: new Lgr("P844 Test", 1, "Bravo", 1234.5f,
                GoalConquest: true, GoalCountdown: false, GoalTeamKills: false,
                GoalProsperity: false, GoalArtifacts: false, GoalFlags: false,
                GoalConquestPercentage: 90, GoalCountdownLength: 0f, GoalTeamKillsCount: 0,
                GoalProsperityMoney: 0f, GoalArtifactsCount: 0, GoalFlagsCount: 0),
            Teams:
            [
                new Ltr(0, "Alpha", "GTW", 4, 2, 1, 5, 1, 0, 0, 40, 0),
                new Ltr(1, "Bravo", "IC", 5, 3, 2, 4, 0, 3, 1, 60, 1),
            ]);

        string json = JsonSerializer.Serialize(report, ServerJsonContext.Default.LobbyMatchReport);
        // The lobby binds case-insensitively (ASP.NET Core's default HttpJson
        // options, Program.cs ConfigureHttpJsonOptions) — mirror that here.
        var back = JsonSerializer.Deserialize<CareerContracts.MatchReport>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(back);
        Assert.Equal("match-p844-1", back!.MatchId);
        // SIM-0524 — the game-level row.
        Assert.NotNull(back.Game);
        Assert.Equal("P844 Test", back.Game!.GameName);
        Assert.Equal((short)1, back.Game.WinnerSideId);
        Assert.Equal("Bravo", back.Game.WinnerName);
        Assert.Equal(1234.5f, back.Game.DurationSeconds);
        Assert.True(back.Game.GoalConquest);
        Assert.Equal((short)90, back.Game.GoalConquestPercentage);
        // SIM-0525 — the per-side rows.
        Assert.NotNull(back.Teams);
        Assert.Equal(2, back.Teams!.Count);
        Assert.Equal("Bravo", back.Teams[1].Name);
        Assert.Equal("IC", back.Teams[1].Faction);
        Assert.Equal((byte)60, back.Teams[1].ConquestPercent);
        // SIM-0526 — the extended per-player columns.
        var p = Assert.Single(back.Players);
        Assert.Equal(2, p.Ejections);
        Assert.Equal(4, p.Rescues);
        Assert.Equal(3, p.Flags);
        Assert.Equal(1, p.Artifacts);
        Assert.Equal(5, p.TechsRecovered);
        Assert.Equal(7, p.WarpsSpotted);
        Assert.Equal(11, p.AsteroidsSpotted);
        Assert.Equal(1817f, p.TimePlayedSeconds);
        Console.WriteLine("P844-REPORT: game row + team rows + extended player columns survive the wire (SIM-0524/0525/0526)");
    }

    // ── P844b — SIM-0531 allies override in the conquest recompute + SIM-0533 draw edge ──

    /// <summary>P844b/SIM-0531 — native's allies-override tail for
    /// <c>CheckForVictoryByStationKill</c> (fsmission.cpp:3719-3743): when every
    /// ACTIVE side is one alliance group, the alliance wins even though no side
    /// reached the conquest/territory threshold. The un-allied control proves the
    /// tally itself is NULL in this setup, so only the override can produce the
    /// winner (the positive leg fails on master — no override existed).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Conquest_recompute_allied_sides_win_by_override()
    {
        Require.Artwork();
        var m = Assembly();
        short side0 = m.Sides[0].Id, side1 = m.Sides[1].Id;
        // Opt the conquest win condition in (threshold 100: no side wins unless it
        // monopolises every flag station, so the tally is NULL here — verified by
        // the control below). Territory stays disabled (sentinel 100).
        m.Goals.ConquestPercentage = 100;
        Assert.True(m.ConquestEnabled);

        // Control: no alliance → the conquest/territory tally yields no winner.
        m.Sides[0].AllyGroup = null;
        m.Sides[1].AllyGroup = null;
        Assert.Null(m.RecomputeConquestAndTerritory(null).Winner);

        // Allied → the override crowns the alliance (fsmission.cpp:3719-3743).
        m.Sides[0].AllyGroup = 5;
        m.Sides[1].AllyGroup = 5;
        var (winner, _) = m.RecomputeConquestAndTerritory(null);
        Assert.NotNull(winner);
        Assert.True(winner!.Id == side0 || winner.Id == side1,
            $"the alliance member must win by the override, got side {winner.Id}");
        Console.WriteLine($"P844b-CONQ-ALLY: conquest recompute crowned alliance member side {winner.Id} by the override");
    }

    /// <summary>P844b/SIM-0533 — the shared pure helper matches native's double
    /// loop (fsmission.cpp:3833-3855 / :3719-3743, Igc/igc.h:4297-4305) for the
    /// corners the tally path leans on: zero active → null; a SINGLE active side
    /// (even un-allied) → that side (native's only pair is itself, so
    /// <c>bAllAllies</c> stays true); two un-allied → null; one group → a member.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void AllianceOverrideWinner_single_active_side_or_one_group_wins()
    {
        Require.Artwork();
        var m = Assembly();
        m.Sides[0].Active = false;
        m.Sides[1].Active = false;
        Assert.Null(Mission.AllianceOverrideWinner(m));                       // zero active

        m.Sides[0].Active = true;
        m.Sides[1].Active = false;
        Assert.Same(m.Sides[0], Mission.AllianceOverrideWinner(m));           // lone active side

        m.Sides[1].Active = true;
        m.Sides[0].AllyGroup = null;
        m.Sides[1].AllyGroup = null;
        Assert.Null(Mission.AllianceOverrideWinner(m));                       // two un-allied

        m.Sides[0].AllyGroup = 5;
        m.Sides[1].AllyGroup = 5;
        var w = Mission.AllianceOverrideWinner(m);
        Assert.True(w is not null && (w.Id == m.Sides[0].Id || w.Id == m.Sides[1].Id));   // one group
        Console.WriteLine("P844b-ALLY-WINNER: AllianceOverrideWinner matches native single/all-group semantics");
    }

    /// <summary>P844b/SIM-0533 — the draw edge: when NO side has players, native's
    /// override still crowns the alliance (the tally is NULL but the double loop
    /// runs, fsmission.cpp:3820-3856). The port previously drew. The observer bot
    /// sits on the TEAM LOBBY (no real side), so the tally sees zero populated
    /// sides while a connected peer still receives GAME_OVER and exposes the winner.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Allied_zero_populated_sides_win_instead_of_draw()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        var mission = server.Missions[0];
        mission.Stage = MissionStage.Started;
        mission.Sides[0].AllyGroup = 5;
        mission.Sides[1].AllyGroup = 5;

        using var a = new FltBot();
        a.Connect(server.BoundPort, "ObsA");
        a.SendLogonRequest(Request("ObsA"));
        int got = PumpUntil(server, () => a.ShipAssignment is not null, 4000);
        Assert.True(got < 4000, "observer must seat");
        // Force the observer onto the TEAM LOBBY (no real side) so the
        // win-by-inactive-sides tally sees ZERO populated sides, while the
        // connected peer still receives GAME_OVER (seating auto-assigns a real
        // side, which would make it populated).
        server.Ships.First(s => s.Ship.Id == a.ShipAssignment!.Ship.ShipId).Ship.SideId = null;
        a.ResetGameOvers();

        InvokeVictoryCheck(server, mission);
        int seen = PumpUntil(server, () => a.GameOvers.Count > 0, 400);
        Assert.True(seen < 400, "allied zero-populated must declare game over (alliance wins, not draw)");
        var go = Assert.Single(a.GameOvers);
        short s0 = mission.Sides[0].Id, s1 = mission.Sides[1].Id;
        Assert.True(go.SideWinner == s0 || go.SideWinner == s1,
            $"alliance must win instead of draw, got {go.SideWinner}");
        Assert.Contains("&", go.GameoverMessage);   // allied name list, fsmission.cpp:5396-5412
        Console.WriteLine($"P844b-EMPTY-ALLY: zero populated allied sides -> side {go.SideWinner} won, not a draw");
    }

    /// <summary>P844b/SIM-0533 — negative control for the draw edge: with NO
    /// alliance between the active sides and zero populated sides, native declares
    /// a draw ("All sides were destroyed", fsmission.cpp:5388) — the override must
    /// not fabricate a winner.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Unallied_zero_populated_sides_draw()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        var mission = server.Missions[0];
        mission.Stage = MissionStage.Started;

        using var a = new FltBot();
        a.Connect(server.BoundPort, "ObsB");
        a.SendLogonRequest(Request("ObsB"));
        int got = PumpUntil(server, () => a.ShipAssignment is not null, 4000);
        Assert.True(got < 4000, "observer must seat");
        // TEAM LOBBY (no real side) — see Allied_zero_populated_sides_win_instead_of_draw.
        server.Ships.First(s => s.Ship.Id == a.ShipAssignment!.Ship.ShipId).Ship.SideId = null;
        a.ResetGameOvers();

        InvokeVictoryCheck(server, mission);
        int seen = PumpUntil(server, () => a.GameOvers.Count > 0, 400);
        Assert.True(seen < 400, "zero-populated un-allied must still declare game over as a draw");
        var go = Assert.Single(a.GameOvers);
        Assert.Equal(GameOver.NA, go.SideWinner);    // draw
        Assert.Contains("destroyed", go.GameoverMessage);
        Console.WriteLine("P844b-EMPTY-UNALLIED: zero populated un-allied sides -> draw (\"All sides were destroyed\")");
    }
}
