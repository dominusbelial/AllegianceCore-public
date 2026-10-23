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
/// P480 — after a real game-over rotation, the finished mission's still-connected
/// peers are MIGRATED into the fresh NotStarted mission (native Vacate() +
/// Reset(true), fsmission.cpp:2604-2622), not left orphaned: each stays connected
/// on the same side id with a fresh ship, receives MISSION_STAGE NotStarted and
/// the new world's EXPORTs, and can ready up so the next game starts — all
/// without reconnecting. P439 kept connected peers orphaned (they froze in the
/// old mission and had to reconnect); P480 re-seats them via the existing
/// join-burst path (<c>CompleteJoinAndLaunch</c>) so their client state matches
/// a fresh joiner of a NotStarted mission.
/// [Collection("LiveNetwork")] keeps this suite sequential with the others.
/// </summary>
[Collection("LiveNetwork")]
public class P480RotationKeepsPlayersLiveTests
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
            new Gm { Name = "Rotation Keep Players", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 941u, DwCookie = 0x941u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9430000000000000000ul, DrmHash = "keep",
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

    /// <summary>
    /// ⭐ 2 sides × 2 Bots, a real #draw → OfferDraw → AcceptDraw → game-over
    /// chain (all four voting yes), then asserts every Bot is MIGRATED into the
    /// fresh NotStarted mission: still connected, on its ORIGINAL side id in the
    /// NEW mission, re-sent MISSION_STAGE NotStarted and the new world's EXPORTs,
    /// then all four ready up and the new game actually starts (countdown →
    /// Started) — with NO reconnect anywhere.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Rotation_migrates_connected_peers_and_new_game_starts_without_reconnect()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());

        short side0 = server.Missions[0].Sides[0].Id;
        short side1 = server.Missions[0].Sides[1].Id;

        // 4 bots: two per side.
        using var a0 = new FltBot();
        using var a1 = new FltBot();
        using var b0 = new FltBot();
        using var b1 = new FltBot();
        short a0Id = Seat(server, a0, "KA0");
        short a1Id = Seat(server, a1, "KA1");
        short b0Id = Seat(server, b0, "KB0");
        short b1Id = Seat(server, b1, "KB1");
        Assert.True(server.TryJoinSide(ShipOf(server, a0Id), side0));   // A0 leader of Alpha
        Assert.True(server.TryJoinSide(ShipOf(server, a1Id), side0));
        Assert.True(server.TryJoinSide(ShipOf(server, b0Id), side1));   // B0 leader of Bravo
        Assert.True(server.TryJoinSide(ShipOf(server, b1Id), side1));

        var bots = new (FltBot Bot, short Side)[] { (a0, side0), (a1, side0), (b0, side1), (b1, side1) };
        var oldIds = bots.Select(x => x.Bot.ShipAssignment!.Ship.ShipId).ToHashSet();

        Flush(server);
        var oldMission = server.Missions[0];
        var leadersBefore = oldMission.Sides.ToDictionary(s => s.Id,
            s => bots.Single(p => p.Bot.ShipAssignment!.Ship.ShipId == s.LeaderShipId).Bot);
        var ownerBefore = oldMission.MissionOwnerSideId;
        server.Missions[0].Stage = MissionStage.Started;   // a draw needs a started game

        // Reset accumulators so we only measure post-rotation signals.
        foreach (var (b, _) in bots)
        {
            b.ResetGameOvers();
            b.ResetMissionStages();
            b.ResetWorldExports();
            b.ResetBallotSignals();
        }

        // --- Drive a real #draw → AcceptDraw → game-over chain, all four voting ---
        b0.SendChat("#draw");   // Bravo leader proposes a draw

        // OfferDraw ballot is voted by Bravo (b0 auto-yes; b1 must agree).
        int offer = PumpUntil(server,
            () => b1.Ballots.Any(x => x.OtInitiator == (short)Allegiance.Core.IgcObjectType.Ship), 800);
        Assert.True(offer < 800, "Bravo teammate did not receive the offer-draw ballot");
        var offerBallot = b1.Ballots.Single(x => x.OtInitiator == (short)Allegiance.Core.IgcObjectType.Ship);
        b1.SendVote(offerBallot.BallotId, true);

        // AcceptDraw ballot is mission-wide; Alpha's two bots must both agree.
        int acc = PumpUntil(server,
            () => a0.Ballots.Any(x => x.OtInitiator == (short)Allegiance.Core.IgcObjectType.Side)
               && a1.Ballots.Any(x => x.OtInitiator == (short)Allegiance.Core.IgcObjectType.Side), 800);
        Assert.True(acc < 800, "Alpha bots did not receive the accept-draw ballot");
        a0.SendVote(a0.Ballots.Single(x => x.OtInitiator == (short)Allegiance.Core.IgcObjectType.Side).BallotId, true);
        a1.SendVote(a1.Ballots.Single(x => x.OtInitiator == (short)Allegiance.Core.IgcObjectType.Side).BallotId, true);

        // Game-over fires (and with it the rotation + migration).
        int ended = PumpUntil(server, () => a0.GameOvers.Count >= 1, 800);
        Assert.True(ended < 800, "game-over did not fire after the accept-draw passed");

        // Rotation happened: a different, pristine NotStarted mission object.
        Assert.NotSame(oldMission, server.Missions[0]);
        var newMission = server.Missions[0];
        Assert.Equal(MissionStage.NotStarted, newMission.Stage);

        // --- Assert every Bot was MIGRATED, not orphaned ---
        foreach (var (bot, side) in bots)
        {
            Assert.True(bot.Connected, "bot must still be connected after the rotation");

            // The migration's re-join burst lands on each bot asynchronously (its
            // own receive thread). Wait for it to fully arrive BEFORE asserting,
            // so the test is robust to scheduling latency under full-suite load:
            // the NotStarted stage, the new world's cluster/station EXPORTs, and a
            // fresh (non-old) ship id from the re-issued SET_CLUSTER.
            int landed = PumpUntil(server,
                () => bot.MissionStages.Any(s => s.Stage == (int)MissionStage.NotStarted)
                   && bot.ShipAssignment is not null && !oldIds.Contains(bot.ShipAssignment!.Ship.ShipId)
                   && bot.ClusterExports.Count >= newMission.Sectors.Count
                   && bot.StationExports.Count >= 1,
                2000);
            Assert.True(landed < 2000, $"bot did not fully re-join the new mission (NotStarted stage / new-world EXPORTs / fresh ship id)");

            // (1) MISSION_STAGE NotStarted re-received after the rotation.
            Assert.Contains(bot.MissionStages, s => s.Stage == (int)MissionStage.NotStarted);

            // (2) Each Bot received the NEW world's EXPORTs (clusters + stations).
            // P797: clusters are still whole-mission (native fsmission.cpp:4170),
            // but stations are now PER SIDE — native exports one only when the
            // peer's side has seen it (fsmission.cpp:4215-4222) — so the proof is
            // "the new world's own stations arrived", not "all of them".
            Assert.True(
                bot.ClusterExports.Count >= newMission.Sectors.Count,
                $"bot got {bot.ClusterExports.Count} cluster exports, expected >= {newMission.Sectors.Count}");
            Assert.NotEmpty(bot.StationExports);
            var botSide = server.Ships.First(x => x.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Ship.SideId;
            Assert.All(bot.StationExports, st => Assert.True(
                newMission.Sectors.SelectMany(x => x.StationRuntimes)
                    .Any(rt => rt.StationId == st.Station.StationId && rt.SeenBySide(botSide!.Value)),
                $"station {st.Station.StationId} was exported to a side that had not seen it"));

            // (3) The bot's ship is seated on its ORIGINAL side in the NEW mission.
            short newId = bot.ShipAssignment!.Ship.ShipId;
            var ship = server.Ships.First(s => s.Ship.Id == newId).Ship;
            Assert.True(ReferenceEquals(ship.Mission, newMission), "bot's ship must belong to the NEW mission");
            Assert.Equal(side, ship.SideId);
        }

        // P728: placement must not pre-join every replacement ship before
        // the first-player election; both native leaders and owner survive.
        foreach (var (side, leader) in leadersBefore)
            Assert.Equal(leader.ShipAssignment!.Ship.ShipId, newMission.Sides.Single(s => s.Id == side).LeaderShipId);
        Assert.Equal(ownerBefore, newMission.MissionOwnerSideId);

        // --- All four ready up and the new game starts WITHOUT reconnecting ---
        foreach (var (bot, _) in bots)
        {
            short newId = bot.ShipAssignment!.Ship.ShipId;
            bot.SendPlayerReady(true, newId);
        }
        int starting = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Starting, 800);
        Assert.True(starting < 800, "new mission did not reach Starting after all four readied up");
        int started = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Started, 2000);
        Assert.True(started < 2000, "new mission did not reach Started after the countdown elapsed");
        Assert.Equal(MissionStage.Started, server.Missions[0].Stage);

        // Still connected throughout — none of the four ever reconnected.
        foreach (var (bot, _) in bots)
            Assert.True(bot.Connected, "a bot disconnected somewhere during the ready-up/start");
    }
}
