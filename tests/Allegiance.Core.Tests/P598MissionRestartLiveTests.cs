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
/// P598 — after a game ends with players still connected, the rebuilt
/// (rotated) NotStarted mission must auto-start; before the fix it sat in
/// NotStarted forever (everyone stuck) until a server restart.
/// <para>
/// Mechanism (see the report): native <c>ProcessGameOver</c> resets only the
/// SIDE aggregate readiness (<c>fsmission.cpp:3501</c>) and PRESERVES each
/// player's individual ready flag (the "set everyone unready" block at
/// <c>fsmission.cpp:3468-3481</c> is commented out — ready is an AFK flag).
/// P480's migration wrongly cleared the per-player ready flag; P598 preserves
/// it and re-derives the side aggregates (native <c>CheckForSideAllReady</c>,
/// <c>fsmission.cpp:5498-5529</c>) so a side whose every connected peer was
/// ready comes back ready and the mission auto-starts via
/// <c>TryAutoStart</c>. Native's separate unattended restart
/// (<c>bAutoRestart</c>, <c>fsmission.cpp:3539-3546</c>) is ported behind
/// <see cref="Gm.AutoRestart"/> + <see cref="Gm.RestartCountdownSeconds"/>.
/// </para>
/// [Collection("LiveNetwork")] keeps this suite sequential with the others.
/// </summary>
[Collection("LiveNetwork")]
public class P598MissionRestartLiveTests
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
            new Gm { Name = "P598 Restart", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static Cfg ConfigAutoRestart() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        LiveSettings = new Ls { AutoNextMatch = new() { Enabled = false } },
        Games =
        [
            new Gm { Name = "P598 AutoRestart", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"],
                     AutoRestart = true, RestartCountdownSeconds = 2 },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 981u, DwCookie = 0x981u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9830000000000000000ul, DrmHash = "p598",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static void Flush(Srv server) => PumpUntil(server, () => false, 60);

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

    /// <summary>Drive a real #draw → OfferDraw → AcceptDraw → game-over chain
    /// with the given 4-bot setup (two on each side), exactly as P480/
    /// MissionRotation tests do.</summary>
    private static void DrawToGameOver(Srv server, FltBot b0, FltBot b1, FltBot a0, FltBot a1)
    {
        b0.SendChat("#draw");                                  // Bravo leader proposes
        int offer = PumpUntil(server,
            () => b1.Ballots.Any(x => x.OtInitiator == (short)Allegiance.Core.IgcObjectType.Ship), 800);
        Assert.True(offer < 800, "Bravo teammate did not receive the offer-draw ballot");
        var offerBallot = b1.Ballots.Single(x => x.OtInitiator == (short)Allegiance.Core.IgcObjectType.Ship);
        b1.SendVote(offerBallot.BallotId, true);

        int acc = PumpUntil(server,
            () => a0.Ballots.Any(x => x.OtInitiator == (short)Allegiance.Core.IgcObjectType.Side)
               && a1.Ballots.Any(x => x.OtInitiator == (short)Allegiance.Core.IgcObjectType.Side), 800);
        Assert.True(acc < 800, "Alpha bots did not receive the accept-draw ballot");
        a0.SendVote(a0.Ballots.Single(x => x.OtInitiator == (short)Allegiance.Core.IgcObjectType.Side).BallotId, true);
        a1.SendVote(a1.Ballots.Single(x => x.OtInitiator == (short)Allegiance.Core.IgcObjectType.Side).BallotId, true);

        int ended = PumpUntil(server, () => a0.GameOvers.Count >= 1, 800);
        Assert.True(ended < 800, "game-over did not fire after the accept-draw passed");
    }

    /// <summary>⭐ The P598 regression, failing-first on today's code: two real
    /// sides, all peers ready (they readied to start the first game), a real
    /// game-over rotates the mission — and the rebuilt NotStarted mission
    /// AUTO-STARTS on its own (Starting → Started) because the migrated peers
    /// come back with their preserved ready state and the sides are re-derived
    /// ready. Before the fix the migration cleared the ready flags and nothing
    /// re-readied them, so the mission sat NotStarted forever (the 221 bug).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Rotated_mission_auto_starts_from_preserved_ready_after_game_over()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());

        short side0 = server.Missions[0].Sides[0].Id;
        short side1 = server.Missions[0].Sides[1].Id;

        using var a0 = new FltBot();
        using var a1 = new FltBot();
        using var b0 = new FltBot();
        using var b1 = new FltBot();
        short a0Id = Seat(server, a0, "PA0");
        short a1Id = Seat(server, a1, "PA1");
        short b0Id = Seat(server, b0, "PB0");
        short b1Id = Seat(server, b1, "PB1");
        Assert.True(server.TryJoinSide(ShipOf(server, a0Id), side0));   // A0 leader Alpha
        Assert.True(server.TryJoinSide(ShipOf(server, a1Id), side0));
        Assert.True(server.TryJoinSide(ShipOf(server, b0Id), side1));   // B0 leader Bravo
        Assert.True(server.TryJoinSide(ShipOf(server, b1Id), side1));

        var bots = new (FltBot Bot, short Side)[] { (a0, side0), (a1, side0), (b0, side1), (b1, side1) };
        var oldIds = bots.Select(x => x.Bot.ShipAssignment!.Ship.ShipId).ToHashSet();

        // Every peer readies to start the FIRST game (the ready-up auto-start).
        foreach (var (b, _) in bots)
            b.SendPlayerReady(true, b.ShipAssignment!.Ship.ShipId);
        int firstStarted = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Started, 2000);
        Assert.True(firstStarted < 2000, "first game did not start after the ready-up");

        var oldMission = server.Missions[0];
        foreach (var (b, _) in bots) b.ResetGameOvers();

        // Real game-over → rotation → migration.
        DrawToGameOver(server, b0, b1, a0, a1);
        Assert.NotSame(oldMission, server.Missions[0]);

        // ⭐ The fix: the rebuilt mission auto-starts WITHOUT any re-ready
        // (migrated peers kept their ready flags; sides re-derived ready). It
        // fires so promptly that the stage may already be Starting here.
        int reStarting = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Starting, 800);
        Assert.True(reStarting < 800, "rotated mission did NOT auto-start (Starting) after game-over — P598 bug");
        int reStarted = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Started, 2000);
        Assert.True(reStarted < 2000, "rotated mission did not reach Started via the auto-start countdown");
        Assert.Equal(MissionStage.Started, server.Missions[0].Stage);

        // Everyone stayed connected and seated on their original side.
        foreach (var (bot, side) in bots)
        {
            Assert.True(bot.Connected, "a migrated bot disconnected during the auto-restart");
            var ship = server.Ships.First(s => s.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Ship;
            Assert.True(ReferenceEquals(ship.Mission, server.Missions[0]));
            Assert.Equal(side, ship.SideId);
        }
    }

    /// <summary>⭐ New joiners after a rotation still start normally: the first
    /// wave after a game-over — the migrated peers' preserved ready auto-start
    /// PLUS a freshly-joined wave — plays without a restart. Drives TWO
    /// rotations in a row (and a new joiner on each) to also exercise repeated
    /// rotation, and asserts no peer-slot leak (server.Ships stays at the
    /// connected count, not growing every rotation).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void New_wave_after_rotation_enters_and_three_rotations_run_without_leak()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());

        short side0 = server.Missions[0].Sides[0].Id;
        short side1 = server.Missions[0].Sides[1].Id;

        using var a0 = new FltBot();
        using var a1 = new FltBot();
        using var b0 = new FltBot();
        using var b1 = new FltBot();
        Seat(server, a0, "QA0"); Seat(server, a1, "QA1");
        Seat(server, b0, "QB0"); Seat(server, b1, "QB1");
        Assert.True(server.TryJoinSide(ShipOf(server, a0.ShipAssignment!.Ship.ShipId), side0));
        Assert.True(server.TryJoinSide(ShipOf(server, a1.ShipAssignment!.Ship.ShipId), side0));
        Assert.True(server.TryJoinSide(ShipOf(server, b0.ShipAssignment!.Ship.ShipId), side1));
        Assert.True(server.TryJoinSide(ShipOf(server, b1.ShipAssignment!.Ship.ShipId), side1));

        var core = new (FltBot Bot, short Side)[] { (a0, side0), (a1, side0), (b0, side1), (b1, side1) };

        for (int rotation = 1; rotation <= 3; rotation++)
        {
            // Ready to start this wave (works whether or not preserved-ready
            // already fired — re-readying a ready peer is a no-op).
            foreach (var (b, _) in core)
                b.SendPlayerReady(true, b.ShipAssignment!.Ship.ShipId);
            int started = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Started, 2000);
            Assert.True(started < 2000, $"wave {rotation}: mission did not start after ready-up");

            var oldMission = server.Missions[0];
            foreach (var (b, _) in core) { b.ResetGameOvers(); b.ResetBallotSignals(); }

            // Game-over → rotation.
            DrawToGameOver(server, b0, b1, a0, a1);
            Assert.NotSame(oldMission, server.Missions[0]);

            if (rotation == 1)
            {
                // A fresh wave joins after the rotation and (with the migrated
                // peers' ready) the game starts — new joiners are not stuck.
                using var c = new FltBot();
                Seat(server, c, $"QC{rotation}");
                Assert.True(server.TryJoinSide(ShipOf(server, c.ShipAssignment!.Ship.ShipId), side0));
                c.SendPlayerReady(true, c.ShipAssignment!.Ship.ShipId);
            }

            // The rotated mission reaches Started again (auto-start from the
            // migrated peers' preserved ready, plus any fresh wave readying up).
            int reStarted = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Started, 3000);
            Assert.True(reStarted < 3000, $"rotation {rotation}: rebuilt mission did not reach Started");

            // No peer-slot leak: server.Ships still holds exactly the 4 core
            // connected peers (plus any transient fresh wave, which here is
            // only present on rotation 1 and disposed at loop end).
            Assert.True(server.Ships.Count() >= 4, "lost a peer across rotations");
            Assert.True(server.Ships.Count() <= 5, "peers accumulated across rotations (leak)");
        }

        // After three rotations, the core peers are all still connected and
        // seated on their original sides of the (latest) mission.
        foreach (var (bot, side) in core)
        {
            Assert.True(bot.Connected, "a bot disconnected across the three rotations");
            var ship = server.Ships.First(s => s.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Ship;
            Assert.Equal(side, ship.SideId);
        }
    }

    /// <summary>⭐ The ported native bAutoRestart: with GameConfig.AutoRestart
    /// on (and a short RestartCountdownSeconds), a game-over rotation's rebuilt
    /// mission auto-starts on the timer even when NO peer is ready (native's
    /// unattended restart, fsmission.cpp:3539-3546) — isolating the config
    /// path from the ready-up path.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void AutoRestart_config_restarts_rotated_mission_without_ready_up()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(ConfigAutoRestart());

        short side0 = server.Missions[0].Sides[0].Id;
        short side1 = server.Missions[0].Sides[1].Id;

        using var a0 = new FltBot();
        using var a1 = new FltBot();
        using var b0 = new FltBot();
        using var b1 = new FltBot();
        Seat(server, a0, "AA0"); Seat(server, a1, "AA1");
        Seat(server, b0, "AB0"); Seat(server, b1, "AB1");
        Assert.True(server.TryJoinSide(ShipOf(server, a0.ShipAssignment!.Ship.ShipId), side0));
        Assert.True(server.TryJoinSide(ShipOf(server, a1.ShipAssignment!.Ship.ShipId), side0));
        Assert.True(server.TryJoinSide(ShipOf(server, b0.ShipAssignment!.Ship.ShipId), side1));
        Assert.True(server.TryJoinSide(ShipOf(server, b1.ShipAssignment!.Ship.ShipId), side1));

        // Force the first game into Started without any ready (so the second
        // rotation's ready-path cannot have fired earlier — the timer must be
        // what restarts it).
        server.Missions[0].Stage = MissionStage.Started;
        var oldMission = server.Missions[0];

        foreach (var (b, _) in new (FltBot Bot, short Side)[] { (a0, side0), (a1, side0), (b0, side1), (b1, side1) })
            b.ResetGameOvers();

        // Real game-over → rotation. NOBODY readies up.
        DrawToGameOver(server, b0, b1, a0, a1);
        Assert.NotSame(oldMission, server.Missions[0]);

        // The AutoRestart timer fires the start countdown (no ready-up needed).
        int reStarting = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Starting, 2000);
        Assert.True(reStarting < 2000, "AutoRestart config did not begin the start countdown after game-over");
        int reStarted = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Started, 2000);
        Assert.True(reStarted < 2000, "AutoRestart config mission did not reach Started");
        Assert.Equal(MissionStage.Started, server.Missions[0].Stage);
    }
}
