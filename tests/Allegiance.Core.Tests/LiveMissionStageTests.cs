using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P309 — the join burst no longer lies about <c>MISSION_STAGE(37)</c>. Real
/// loopback proof over an in-process server + real TCP bots:
///   1. A peer joining a genuinely pre-game (NotStarted) mission is told the
///      real stage (NotStarted), NOT the old hardcoded Started — and has not
///      received any Started yet.
///   2. The ready-up auto-start (countdown) broadcasts MISSION_STAGE(Started)
///      to every already-joined peer the moment the mission truly starts
///      (native CFSMission::SetStage, fsmission.cpp:1759-1762 / 1976 / 2239).
///   3. A late joiner into an already-Started mission is told Started at join
///      (the real current stage, so the pre-existing late-join path is intact).
/// The bot <see cref="FltBot.MissionStages"/> accumulator captures each
/// MISSION_STAGE(37) in arrival order; stages are the wire AGCGameStage values
/// (NotStarted=0, Starting=1, Started=2) which equal the MissionStage enum.
/// </summary>
[Collection("LiveNetwork")]
public class LiveMissionStageTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Stage Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 887u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9530000000000000000ul, DrmHash = "stagestage",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static void Seat(Srv server, FltBot bot, string name)
    {
        bot.Connect(server.BoundPort, name);
        bot.SendLogonRequest(Request(name));
        int n = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(n < 4000, $"{name} must seat");
    }

    /// <summary>⭐ A peer joining a fresh, not-yet-started mission is told the
    /// REAL (NotStarted) stage in the join burst — the wire value captured by
    /// the bot — and has NOT been told Started. Reverting the join burst to the
    /// old hardcoded Started makes this fail (first received stage would be 2).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Join_while_mission_not_started_receives_non_started_stage()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());

        using var a = new FltBot();
        Seat(server, a, "Pre");

        int got = PumpUntil(server, () => a.MissionStages.Any(), 800);
        Assert.True(got < 800, "bot must receive a MISSION_STAGE at join");

        // The mission is genuinely pre-game at this point.
        Assert.Equal(MissionStage.NotStarted, server.Missions[0].Stage);

        var first = a.MissionStages[0];
        Console.WriteLine($"P309-WIRE-JOIN: bot 'Pre' received MISSION_STAGE stage={first.Stage} ({(MissionStage)first.Stage}) at join while mission stage={server.Missions[0].Stage}");
        Assert.Equal(MissionStageMessage.StageNotStarted, first.Stage);

        // …and it has NOT been lied to with a Started yet (mission not started).
        Assert.DoesNotContain(a.MissionStages, m => m.Stage == MissionStageMessage.StageStarted);
    }

    /// <summary>⭐ Ready-up auto-start broadcasts MISSION_STAGE(Started) to the
    /// already-joined peers the moment the mission truly starts (countdown
    /// completes). Before both sides ready, neither has a Started; after the
    /// transition, both have one. Removing the CompleteStartCountdown broadcast
    /// makes the Started assertion fail.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Ready_up_auto_start_broadcasts_started_stage_to_already_joined_peers()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());

        using var a = new FltBot();
        using var b = new FltBot();
        Seat(server, a, "AA");
        Seat(server, b, "BB");

        int seat = PumpUntil(server,
            () => a.MissionStages.Any() && b.MissionStages.Any(), 800);
        Assert.True(seat < 800, "both bots must receive the join-stage");

        // Pre-game: each got NotStarted at join, no Started anywhere.
        Assert.Equal(MissionStage.NotStarted, server.Missions[0].Stage);
        Assert.Contains(a.MissionStages, m => m.Stage == MissionStageMessage.StageNotStarted);
        Assert.Contains(b.MissionStages, m => m.Stage == MissionStageMessage.StageNotStarted);
        Assert.DoesNotContain(a.MissionStages, m => m.Stage == MissionStageMessage.StageStarted);
        Assert.DoesNotContain(b.MissionStages, m => m.Stage == MissionStageMessage.StageStarted);

        // One side ready alone is not enough to start.
        a.SendPlayerReady(true, a.ShipAssignment!.Ship.ShipId);
        int ra = PumpUntil(server, () => a.TeamReadys.Count >= 1, 800);
        Assert.True(ra < 800, "no TEAM_READY after A readied");
        Assert.Equal(MissionStage.NotStarted, server.Missions[0].Stage);
        Assert.DoesNotContain(a.MissionStages, m => m.Stage == MissionStageMessage.StageStarted);

        // Second side readies → countdown begins (Starting) then completes →
        // Started broadcast to BOTH already-joined peers.
        b.SendPlayerReady(true, b.ShipAssignment!.Ship.ShipId);
        int both = PumpUntil(server,
            () => server.Missions[0].Stage == MissionStage.Started
                  && a.MissionStages.Any(m => m.Stage == MissionStageMessage.StageStarted)
                  && b.MissionStages.Any(m => m.Stage == MissionStageMessage.StageStarted), 800);
        Assert.True(both < 800, "auto-start did not broadcast MISSION_STAGE(Started) to both peers");

        foreach (var bot in new[] { a, b })
        {
            string seq = string.Join(",", bot.MissionStages.Select(m => $"{(MissionStage)m.Stage}"));
            Console.WriteLine($"P309-WIRE-START: bot '{bot.ShipAssignment!.Ship.ShipId}' MISSION_STAGE sequence = [{seq}]");
            Assert.Contains(bot.MissionStages, m => m.Stage == MissionStageMessage.StageStarted);
        }
    }

    /// <summary>A peer joining an already-Started mission is told Started at
    /// join (the real current stage) — the late-join path stays intact with the
    /// real-stage change.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Late_joiner_into_started_mission_receives_started_stage()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());

        using var a = new FltBot();
        Seat(server, a, "First");

        // A single occupied side ready → auto-start → countdown → Started.
        a.SendPlayerReady(true, a.ShipAssignment!.Ship.ShipId);
        int started = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Started, 800);
        Assert.True(started < 800, "mission must reach Started");

        // Late joiner now.
        using var b = new FltBot();
        Seat(server, b, "Late");

        int got = PumpUntil(server, () => b.MissionStages.Any(), 800);
        Assert.True(got < 800, "late joiner must receive a MISSION_STAGE at join");
        Assert.Equal(MissionStage.Started, server.Missions[0].Stage);

        var first = b.MissionStages[0];
        Console.WriteLine($"P309-WIRE-LATEJOIN: late bot 'Late' received MISSION_STAGE stage={first.Stage} ({(MissionStage)first.Stage}) at join into a Started mission");
        Assert.Equal(MissionStageMessage.StageStarted, first.Stage);
    }
}
