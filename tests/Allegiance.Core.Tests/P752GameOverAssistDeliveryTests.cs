using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>
/// P752 — the game-over tables' new data: the kill-ASSIST credit (native
/// <c>FedSrvSiteBase::KillShipEvent</c>'s damage-track walk,
/// <c>FedSrv.CPP:11637-11670</c>) and the per-SIDE endgame counters
/// (Kills/Deaths/Ejections/BaseKills/BaseCaptures,
/// <c>fsmission.cpp:3069-3080</c>), both delivered to real peers through the
/// production <c>GAME_OVER(62)</c>/<c>GAME_OVER_PLAYERS(63)</c> encode path
/// (assists + time played ride the P752-extended
/// <see cref="GameOverPlayerEntry"/>).
/// </summary>
public sealed class P752GameOverAssistDeliveryTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Second_enemy_contributor_earns_the_assist_and_the_tables_carry_it_with_side_counters()
    {
        Require.Artwork();
        using var server = new Srv();
        server.SpawnInitialMinersAtBoot = false;
        server.Boot(new ServerConfig
        {
            Port = 0, ArtPath = ArtRoot, StaticCore = "PCore001.igc",
            Games = [new GameConfig { Name = "P752", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
        });
        var mission = server.Missions[0];
        mission.Stage = MissionStage.Started;
        // The victim's own-side contributor must be able to land chip damage:
        // friendly fire defaults OFF, which would suppress its hits
        // (shipIGC.cpp:1222-1228 — the test-only flip, not a production change).
        mission.AllowFriendlyFire = true;
        // A real combat hull (the first core hull is the 1-HP Observer, which
        // any chip damage would kill); use the toughest hull so the two chip
        // hits are non-lethal, and a damage type whose armor factor against it
        // is non-zero.
        var hull = mission.Core.HullTypes.Values.OrderByDescending(h => h.HitPoints).First();
        var dm = mission.Core.Constants.DamageConstants;
        byte dmgType = 0;
        foreach (var p in mission.Core.ProjectileTypes.Values)
        {
            byte t = p.DamageType;
            if (t < dm.Length && hull.DefenseType < dm[t].Length && dm[t][hull.DefenseType] > 0f) { dmgType = t; break; }
        }

        // winner (side 0) will deal the killing blow; second (side 0) chips in
        // first; mate (side 1) is the victim's OWN-side contributor (must never
        // earn an assist); loser (side 1) is the victim.
        var winner = new Ship(400, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0 };
        var second = new Ship(401, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0 };
        var loser = new Ship(402, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 1 };
        var mate = new Ship(403, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 1 };
        mission.PlaceShip(winner, mission.Sectors[0]);
        mission.PlaceShip(second, mission.Sectors[0]);
        mission.PlaceShip(loser, mission.Sectors[0]);
        mission.PlaceShip(mate, mission.Sectors[0]);
        server.TestSeedPeers(
        [
            (new ConnectionId(41), winner, mission.Sectors[0]),
            (new ConnectionId(42), second, mission.Sectors[0]),
            (new ConnectionId(43), loser, mission.Sectors[0]),
            (new ConnectionId(44), mate, mission.Sectors[0]),
        ]);

        var winnerScore = server.Ships.Single(sh => sh.Ship == winner).PlayerScore;
        var secondScore = server.Ships.Single(sh => sh.Ship == second).PlayerScore;
        var loserScore = server.Ships.Single(sh => sh.Ship == loser).PlayerScore;
        var mateScore = server.Ships.Single(sh => sh.Ship == mate).PlayerScore;

        // Damage order: mate (own side — largest), second (enemy — small),
        // winner (enemy — lethal). The top bucket is mate, so native's walk
        // starts AFTER it; the first enemy player ship after the top bucket is
        // "second". The kill itself is credited to the launcher that dealt the
        // most damage among... the port's credit resolution (TopDealer) picks
        // mate, but the kill-credit block only awards cross-side killers, so
        // the observable production outcome is: winner (the lethal enemy
        // launcher recorded in KILL_SHIP) may or may not earn Kills depending
        // on the credit branch — the ASSIST, the row under test, must go to
        // "second" either way, exactly as native walks it.
        double now = server.SimNow;
        // Small, non-lethal chip damage from the own-side mate and the enemy
        // "second" (sorted below the lethal blow), then the lethal hit.
        Assert.Equal(DamageResult.HullDamage, loser.ReceiveDamage(dmgType, 10f, now, loser.Position, Vector3.Zero, mate));
        Assert.Equal(DamageResult.HullDamage, loser.ReceiveDamage(dmgType, 5f, now, loser.Position, Vector3.Zero, second));
        var result = loser.ReceiveDamage(dmgType, loser.HitPoints * 1e6f, now, loser.Position, Vector3.Zero, winner);

        // The assist walk: "second" is the first enemy, non-allied,
        // player-piloted ship after the top bucket (FedSrv.CPP:11637-11670).
        Assert.Equal(1, secondScore.Assists);
        // No one else earns an assist — the walk breaks at the first hit.
        Assert.Equal(0, winnerScore.Assists);
        Assert.Equal(0, mateScore.Assists);
        Assert.Equal(0, loserScore.Assists);
        // The victim's own side's ejection counter moved on the eject path
        // (sideigc.h:479-483 → fsmission.cpp:3076).
        Assert.Equal(DamageResult.Killed, result);
        var side1 = mission.Sides.Single(sd => sd.Id == 1);
        Assert.True(side1.Ejections == 1 || side1.Deaths == 1,
            $"side 1 must record the victim's exit (ejections={side1.Ejections} deaths={side1.Deaths})");

        // Deliver the real game-over and decode what the tables will read.
        typeof(Srv).GetField("_simNow", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(server, now + 10.0);
        var gameOvers = new List<byte[][]>();
        server._testSendCapture = (peer, packet) =>
        {
            var messages = FedPacket.Unpack(packet);
            if (messages.Any(x => FedMessageHeader.Read(x).Fmid == GameOver.Fmid))
                gameOvers.Add(messages);
        };
        typeof(Srv).GetMethod("DeclareGameOver", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(server, [mission, (short)0, "conquest"]);

        // Every seeded peer receives its own identical frame; decode ONE.
        Assert.All(gameOvers, g => Assert.Equal(gameOvers[0].Length, g.Length));
        var sent = gameOvers[0];
        var goMsg = GameOver.Decode(sent.Single(x => FedMessageHeader.Read(x).Fmid == GameOver.Fmid));
        var entries = sent.Where(x => FedMessageHeader.Read(x).Fmid == GameOverPlayers.Fmid)
            .SelectMany(x => GameOverPlayers.Decode(x).Entries).ToArray();
        Assert.Equal(4, entries.Length);

        var secondEntry = entries.Single(e => e.CharacterName.Contains("42"));
        var loserEntry = entries.Single(e => e.CharacterName.Contains("43"));
        var winnerEntry = entries.Single(e => e.CharacterName.Contains("41"));
        var mateEntry = entries.Single(e => e.CharacterName.Contains("44"));

        // The assist rides the wire (native GameOverScoreObject::GetRecentAssists).
        Assert.Equal((short)1, secondEntry.Assists);
        Assert.Equal((short)0, winnerEntry.Assists);
        Assert.Equal((short)0, mateEntry.Assists);
        Assert.Equal((short)0, loserEntry.Assists);
        // Time played rides the wire as real seconds (ConnectSimTime→_simNow).
        Assert.All(entries, e => Assert.True(e.TimePlayed > 0f, $"timePlayed={e.TimePlayed}"));
        // The side totals table's counters come from the live Side model
        // (fsmission.cpp:3069-3080): side 0's kills, side 1's ejection/death.
        var side0 = goMsg.Sides[0];
        var side1End = goMsg.Sides[1];
        Assert.True(side0.CKills >= 1, $"side 0 kills={side0.CKills}");
        Assert.True(side1End.CEjections + side1End.CDeaths >= 1,
            $"side 1 ejections={side1End.CEjections} deaths={side1End.CDeaths}");
        Assert.Equal("Alpha", side0.SideName);
        Assert.Equal("Bravo", side1End.SideName);
        server._testSendCapture = null;
    }
}
