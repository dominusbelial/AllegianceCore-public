using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using PlayerScore = global::Allegiance.Server.PlayerScore;

namespace Allegiance.Core.Tests;

/// <summary>
/// P265 — the live end-to-end proof that the periodic in-mission scoreboard
/// broadcast <c>ScoreRosterUpdate</c>(209) reaches a real connected peer and
/// carries each player's real P193 <c>PlayerScore</c> kills/deaths with the
/// right names and sides.
///
/// <para><b>Deterministic by design.</b> The visual "real combat event" proof —
/// a REAL two-party kill feeding the overlay's numbers — lives in the out-of-
/// repo offscreen harness (/tmp/p265verify), which drives the same fight-hull
/// combat the documented <c>LiveCombatWireTests</c> uses and screenshots the
/// rendered overlay. This suite test instead exercises the WIRE path
/// deterministically: it seats two real peers on real sides, writes distinct
/// real P193 kills/deaths into their PlayerScore (exactly as P194's
/// <c>GameOverPlayersWireTests</c> seeds Rank/Score to prove the wire), and
/// asserts the periodic roster broadcast carries them. The real-combat →
/// PlayerScore direction is already the proven territory of P215's own suite;
/// splitting it this way avoids importing the environmentally-flaky real-combat
/// timing into a suite that must stay clean (only the documented TurretGunner
/// flake is expected).</para>
///
/// <para><b>Coverage:</b> (a) the broadcast fires periodically to every peer;
/// (b) both real-side players appear with their OWN correct side id and real
/// Kills/Deaths; (c) the roster is whole-mission (both sides visible).</para>
/// </summary>
[Collection("LiveNetwork")]
public sealed class ScoreRosterLiveTests
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
            new Gm { Name = "Score Roster Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 919u, DwCookie = 0x919u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 9100000000000000000ul, DrmHash = "scoreroster",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static PlayerScore PlayerScoreOf(Srv server, FltBot bot) =>
        server.Ships.First(s => s.Ship.Id == bot.ShipAssignment!.Ship.ShipId).PlayerScore;

    /// <summary>⭐ The periodic roster broadcast reaches a real connected peer
    /// carrying each player's real P193 Kills/Deaths with the right name and own
    /// side id, for BOTH real sides — the wire the MonoGame scoreboard reads.</summary>
    [Fact]
    public void Periodic_roster_broadcasts_real_player_scores_with_sides()
    {
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: bypass ready-up

        using var a = new FltBot();
        using var b = new FltBot();
        a.Connect(server.BoundPort, "Ace");
        a.SendLogonRequest(Request("Ace"));
        b.Connect(server.BoundPort, "Bee");
        b.SendLogonRequest(Request("Bee"));
        int got = PumpUntil(server, () => a.ShipAssignment is not null && b.ShipAssignment is not null, 4000);
        Assert.True(got < 4000, "both bots must seat");
        var aceShip = server.Ships.First(s => s.Ship.Id == a.ShipAssignment!.Ship.ShipId).Ship;
        var beeShip = server.Ships.First(s => s.Ship.Id == b.ShipAssignment!.Ship.ShipId).Ship;
        Assert.True(aceShip.SideId is not null && beeShip.SideId is not null,
            "seated bots must be on real sides");

        // Seed distinct real P193 scores so the wire provably carries them (P194
        // convention: seed PlayerScore, assert the wire). Kills/Deaths mutate via
        // AddKill/AddDeath in real combat (P215); here we write them directly.
        PlayerScoreOf(server, a).Kills = 3;
        PlayerScoreOf(server, a).Deaths = 1;
        PlayerScoreOf(server, b).Kills = 0;
        PlayerScoreOf(server, b).Deaths = 2;

        a.ResetScoreRosterBroadcasts();
        b.ResetScoreRosterBroadcasts();

        // Wait for a roster that carries Ace's seeded kill (proves post-seed freshness).
        int seen = PumpUntil(server, () =>
            a.ScoreRosterBroadcasts.Any(r => r.Entries.Any(e => e.CharacterName == "Ace" && e.Kills == 3))
            && b.ScoreRosterBroadcasts.Any(r => r.Entries.Any(e => e.CharacterName == "Ace" && e.Kills == 3)), 500);
        Assert.True(seen < 500, "no post-seed ScoreRosterUpdate reaching both peers within ~25 s");

        var roster = a.ScoreRosterBroadcasts.Last(r => r.Entries.Any(e => e.CharacterName == "Ace" && e.Kills == 3));

        var ace = roster.Entries.Single(e => e.CharacterName == "Ace");
        var bee = roster.Entries.Single(e => e.CharacterName == "Bee");

        short aceSide = server.Ships.First(s => s.Ship.Id == a.ShipAssignment!.Ship.ShipId).Ship.SideId!.Value;
        short beeSide = server.Ships.First(s => s.Ship.Id == b.ShipAssignment!.Ship.ShipId).Ship.SideId!.Value;

        // Ace's own side, real kills/deaths.
        Assert.Equal(aceSide, ace.SideId);
        Assert.Equal((short)3, ace.Kills);
        Assert.Equal((short)1, ace.Deaths);
        // Bee's own (opposite) side, real kills/deaths — both sides visible.
        Assert.Equal(beeSide, bee.SideId);
        Assert.Equal((short)0, bee.Kills);
        Assert.Equal((short)2, bee.Deaths);
        // Whole-mission: the two peers are on distinct sides, both present.
        Assert.NotEqual(aceSide, beeSide);
        Assert.Contains(roster.Entries, e => e.CharacterName == "Bee");

        Console.WriteLine($"P265-LIVE: periodic roster -> Ace[{ace.SideId}] k={ace.Kills} d={ace.Deaths}, " +
                          $"Bee[{bee.SideId}] k={bee.Kills} d={bee.Deaths}, {roster.Entries.Length} player(s) to both peers");
    }
}
