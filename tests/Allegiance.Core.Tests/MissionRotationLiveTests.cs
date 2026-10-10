using System.Reflection;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Ls = global::Allegiance.Server.LiveSettingsConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P439 — a finished mission rotates back to a joinable NotStarted pre-game.
/// The port's <c>DeclareGameOver</c>/<c>DeclareDraw</c> used to flip a mission
/// to <see cref="MissionStage.Over"/> permanently and never broadcast
/// <c>MISSION_STAGE(37)</c>, so every later joiner was stuck on a ready-check
/// with nowhere to go. P439 rebuilds a freshly-assembled NotStarted mission at
/// the same <c>_missions</c> index on the shared game-over path (the port's
/// collapsed equivalent of native's <c>ResetMission</c> +
/// <c>GenerateMission</c> pair), so a new joiner lands on a pristine world and
/// can ready up and start normally. Originally the already-connected peers
/// kept their GAME_OVER screen (the P440 follow-up); since P480 they are
/// MIGRATED into the fresh mission instead (native Vacate + Reset(true)),
/// still connected and seated — so a new joiner AND the migrated peers all
/// ready up together to start the next game. This test drives the real
/// <c>#draw</c> → OfferDraw → AcceptDraw → game-over wire chain.
/// [Collection("LiveNetwork")] keeps this suite sequential with the others.
/// </summary>
[Collection("LiveNetwork")]
public class MissionRotationLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Rotation Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    /// <summary>P387/P439 — a debug-mode config (liveSettings.debugMode=true)
    /// so a game-over rotation's rebuilt mission can be shown to auto-start
    /// exactly as a debug mission does at boot. Uses the real m2xspiderw arena
    /// (where P379's debug topology and P382's initial miners operate) so the
    /// rebuild is exercised against the same map the unattended arena uses.</summary>
    private static Cfg ConfigDebug() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        LiveSettings = new Ls { DebugMode = true },
        Games =
        [
            new Gm { Name = "Rotation Debug", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 941u, DwCookie = 0x941u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9430000000000000000ul, DrmHash = "rotate",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static void Flush(Srv server) => PumpUntil(server, () => false, 40);

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

    /// <summary>Destroy one station (by id) in a mission's sector for the
    /// "rebuilt world brings the destroyed station back" pristineness proof.
    /// Goes through the sector's private static/runtime station lists — this
    /// is a test-seam only; the old mission is discarded wholesale by the P439
    /// rotation, so the exact destruction mechanics don't matter, only that the
    /// station is gone from the OLD world before the game ends.</summary>
    private static void DestroyStationForTest(Sector sector, short stationId)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var statics = (List<DataStationIGC>)typeof(Sector).GetField("_stations", flags)!.GetValue(sector)!;
        statics.RemoveAll(s => s.StationId == stationId);
        var runtimes = (List<Station>)typeof(Sector).GetField("_stationRuntimes", flags)!.GetValue(sector)!;
        runtimes.RemoveAll(s => s.StationId == stationId);
    }

    /// <summary>⭐ A real #draw → AcceptDraw → game-over chain, then a brand-new
    /// joiner lands on a fresh NotStarted mission, readies up and the mission
    /// reaches Starting then Started through the real countdown — while the old
    /// peers keep GAME_OVER and their destroyed-station world is replaced by a
    /// pristine rebuild. All four P439 verification assertions.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Finished_mission_rotates_to_fresh_notstarted_and_new_joiner_starts()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());

        using var a = new FltBot();   // Alpha (side0)
        using var b = new FltBot();   // Bravo (side1)
        short aId = Seat(server, a, "RTA");
        short side0 = server.Missions[0].Sides[0].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));   // A leader of Alpha
        short bId = Seat(server, b, "RTB");
        short side1 = server.Missions[0].Sides[1].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, bId), side1));   // B on Bravo

        var oldMission = server.Missions[0];
        server.Missions[0].Stage = MissionStage.Started;

        // Pristine-world baseline, then destroy ONE station in the OLD mission so
        // the rebuilt world provably "brings it back".
        int originalStationCount = oldMission.Sectors.Sum(s => s.Stations.Count);
        var victimSector = oldMission.Sectors.First(s => s.Stations.Count > 0);
        short destroyedStationId = victimSector.Stations.First().StationId;
        DestroyStationForTest(victimSector, destroyedStationId);
        Assert.Equal(originalStationCount - 1, oldMission.Sectors.Sum(s => s.Stations.Count));

        Flush(server);
        a.ResetGameOvers();
        a.ResetMissionStages();

        // Trigger the real end-to-end #draw → offer → accept → game-over chain.
        b.SendChat("#draw");
        int offerSeen = PumpUntil(server, () => b.Ballots.Count >= 1, 800);
        Assert.True(offerSeen < 800, "offer-draw ballot not created");
        int acceptSeen = PumpUntil(server, () => a.Ballots.Count >= 1, 800);
        Assert.True(acceptSeen < 800, "accept-draw ballot not created");
        var accept = a.Ballots.Single(x => x.OtInitiator == (short)Allegiance.Core.IgcObjectType.Side);
        a.SendVote(accept.BallotId, true);

        // Assertion 1 — the OLD peer receives GAME_OVER(62) and then MISSION_STAGE(37) Over.
        int oldPeerGotBoth = PumpUntil(
            server,
            () => a.GameOvers.Count >= 1 && a.MissionStages.Any(s => s.Stage == (int)MissionStage.Over),
            800);
        Assert.True(oldPeerGotBoth < 800, "old peer did not receive GAME_OVER then MISSION_STAGE Over");
        Assert.Single(a.GameOvers);
        Assert.Contains(a.MissionStages, s => s.Stage == (int)MissionStage.Over);

        // Assertion 4 (part 1) — _missions[0] is a DIFFERENT object from the old mission,
        // and it is a pristine NotStarted pre-game.
        Assert.NotSame(oldMission, server.Missions[0]);
        Assert.Equal(MissionStage.NotStarted, server.Missions[0].Stage);

        // Assertion 4 (part 2) — the destroyed station exists again in the pristine rebuild.
        Assert.Equal(originalStationCount, server.Missions[0].Sectors.Sum(s => s.Stations.Count));
        Assert.Contains(
            server.Missions[0].Sectors.SelectMany(s => s.Stations),
            st => st.StationId == destroyedStationId);

        // Assertion 2 — a brand-new Bot joining afterwards receives MISSION_STAGE NotStarted, NOT Over.
        using var c = new FltBot();
        short cId = Seat(server, c, "RTC");
        Assert.Contains(c.MissionStages, s => s.Stage == (int)MissionStage.NotStarted);
        Assert.DoesNotContain(c.MissionStages, s => s.Stage == (int)MissionStage.Over);

        // Assertion 3 — the new joiner readies up and the mission reaches Starting,
        // then Started through the real StartCountdown / CompleteStartCountdown path.
        Assert.True(server.TryJoinSide(ShipOf(server, cId), side0));   // occupy side0 of the NEW mission
        c.SendPlayerReady(true, cId);

        // P480 — the OLD peers a and b were MIGRATED into the fresh mission (still
        // connected, re-seated on their original sides), so they too occupy it and
        // must ready up for the next game. P439's original orphaned-peers premise
        // (they kept GAME_OVER and c could start alone) no longer holds; the
        // fresh-joiner path for c is unchanged, and a+b ready up alongside it so
        // every occupied side of the new mission is aggregate-ready.
        PumpUntil(server,
            () => a.ShipAssignment is not null && a.ShipAssignment!.Ship.ShipId != aId
               && b.ShipAssignment is not null && b.ShipAssignment!.Ship.ShipId != bId, 2000);
        a.SendPlayerReady(true, a.ShipAssignment!.Ship.ShipId);
        b.SendPlayerReady(true, b.ShipAssignment!.Ship.ShipId);

        int starting = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Starting, 800);
        Assert.True(starting < 800, "new mission did not reach Starting after ready-up");
        Assert.Contains(c.MissionStages, s => s.Stage == (int)MissionStage.Starting);

        int started = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Started, 2000);
        Assert.True(started < 2000, "new mission did not reach Started after the countdown elapsed");
        Assert.Equal(MissionStage.Started, server.Missions[0].Stage);
        Assert.Contains(c.MissionStages, s => s.Stage == (int)MissionStage.Started);
    }

    /// <summary>⭐ P439 debug-mode handling (P387 parity): with
    /// <c>liveSettings.debugMode=true</c> the mission auto-starts at boot (P387)
    /// and, when it later reaches game-over, the REBUILT mission gets the same
    /// auto-start treatment — it climbs NotStarted → Starting → Started on its
    /// own, with no human player required, exactly like boot. Proven through a
    /// real #draw → AcceptDraw → game-over chain (draws are not affected by the
    /// P388 win-by-elimination skip, so they remain a legal debug-mode end).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Debug_mode_rebuilt_mission_auto_starts_after_game_over()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(ConfigDebug());

        // P387 — debug mission auto-starts at boot: reaches Started on its own.
        int bootStarted = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Started, 800);
        Assert.True(bootStarted < 800, "debug mission did not auto-start at boot (P387)");

        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "DBA");
        short side0 = server.Missions[0].Sides[0].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));
        short bId = Seat(server, b, "DBB");
        short side1 = server.Missions[0].Sides[1].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, bId), side1));

        var oldMission = server.Missions[0];
        a.ResetGameOvers();

        // Real #draw → offer → accept → game-over (legal in debug mode).
        b.SendChat("#draw");
        int offerSeen = PumpUntil(server, () => b.Ballots.Count >= 1, 800);
        Assert.True(offerSeen < 800, "debug: offer-draw ballot not created");
        int acceptSeen = PumpUntil(server, () => a.Ballots.Count >= 1, 800);
        Assert.True(acceptSeen < 800, "debug: accept-draw ballot not created");
        var accept = a.Ballots.Single(x => x.OtInitiator == (short)Allegiance.Core.IgcObjectType.Side);
        a.SendVote(accept.BallotId, true);
        int ended = PumpUntil(server, () => a.GameOvers.Count >= 1, 800);
        Assert.True(ended < 800, "debug: game-over did not fire");

        // Rotation happened: a different, pristine mission object.
        Assert.NotSame(oldMission, server.Missions[0]);

        // P439 debug parity — the rebuilt mission AUTO-STARTS (no player needed):
        // NotStarted → Starting (countdown begun) → Started (countdown elapsed).
        int rebuiltStarting = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Starting, 800);
        Assert.True(rebuiltStarting < 800, "debug: rebuilt mission did not auto-start (Starting)");
        int rebuiltStarted = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Started, 2000);
        Assert.True(rebuiltStarted < 2000, "debug: rebuilt mission did not reach Started via the auto-start countdown");
        Assert.Equal(MissionStage.Started, server.Missions[0].Stage);
    }
}
