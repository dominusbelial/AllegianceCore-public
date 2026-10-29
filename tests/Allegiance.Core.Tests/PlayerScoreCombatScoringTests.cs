using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using PlayerScore = global::Allegiance.Server.PlayerScore;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P215 (player-scoring Stage 1) — pure, server-free unit tests of the
/// <see cref="PlayerScore"/> combat math and counters: the ported
/// <see cref="PlayerScore.AdjustCombatRating"/> (native
/// <c>PlayerScoreObject::AdjustCombatRating</c>, <c>common.cpp:2706-2717</c>)
/// and the <see cref="PlayerScore.AddKill"/>/<see cref="PlayerScore.AddDeath"/>
/// counters it wires. These run in parallel (no LiveNetwork collection needed —
/// nothing here boots a server or opens a socket).
/// </summary>
public class PlayerScoreCombatRatingTests
{
    // The real IGC core constants the mission reads for the rating update:
    // c_fcidRatingAdd = 28, c_fcidRatingDivide = 29 (igc.h:92-93), which
    // PCore001.igc bakes as 1.25f / 100.0f (c_crAdd/c_crDivide,
    // missionigc.cpp:2719-2720).
    private const float RatingAdd = 1.25f;
    private const float RatingDivide = 100f;

    /// <summary>⭐ The ported formula reproduces native's exact update. With
    /// equal ratings the two-object update is perfectly symmetric: the winner
    /// gains exactly what the loser loses, and the numbers match the hand-derived
    /// <c>k = (1.25 + 0.5 - 0.5)/100 = 0.0125</c> result
    /// (<c>killer = 0.5·(1−0.0125)+0.0125 = 0.50625</c>,
    /// <c>killee = 0.5·(1−0.0125) = 0.49375</c>).</summary>
    [Fact]
    public void Adjust_combat_rating_matches_native_formula_exactly()
    {
        var killer = new PlayerScore { CombatRating = 0.5f };
        var killee = new PlayerScore { CombatRating = 0.5f };

        PlayerScore.AdjustCombatRating(RatingAdd, RatingDivide, killer, killee);

        Assert.Equal(0.50625f, killer.CombatRating, 5);
        Assert.Equal(0.49375f, killee.CombatRating, 5);
        // Symmetry: the winner's gain equals the loser's loss, magnitude-wise.
        Assert.Equal(killer.CombatRating - 0.5f, 0.5f - killee.CombatRating, 5);
    }

    /// <summary>⭐ The classic Elo asymmetry, asserted quantitatively (not just
    /// "something changed"): a lower-rated killer beating a higher-rated killee
    /// (the upset) gains <b>far</b> more rating than the higher-rated favourite
    /// beating the lower-rated underdog — even though both are one-vs-one wins
    /// over the same 0.8-rating gap.</summary>
    [Fact]
    public void Lower_rated_killer_beating_higher_rated_killee_gains_more_than_the_reverse()
    {
        // Upset: the 0.1-rated killer beats the 0.9-rated killee.
        var upsetKiller = new PlayerScore { CombatRating = 0.1f };
        var upsetKillee = new PlayerScore { CombatRating = 0.9f };
        PlayerScore.AdjustCombatRating(RatingAdd, RatingDivide, upsetKiller, upsetKillee);
        float upsetGain = upsetKiller.CombatRating - 0.1f;

        // Reverse: the 0.9-rated favourite beats the 0.1-rated underdog.
        var favKiller = new PlayerScore { CombatRating = 0.9f };
        var favKillee = new PlayerScore { CombatRating = 0.1f };
        PlayerScore.AdjustCombatRating(RatingAdd, RatingDivide, favKiller, favKillee);
        float favGain = favKiller.CombatRating - 0.9f;

        // Hand-derived: upset k=(1.25+0.9-0.1)/100=0.0205 → gain=0.0205·(1-0.1)
        // = 0.01845; favourite k=(1.25+0.1-0.9)/100=0.0045 → gain=0.0045·(1-0.9)
        // = 0.00045.
        Assert.Equal(0.01845f, upsetGain, 5);
        Assert.Equal(0.00045f, favGain, 5);
        Assert.True(upsetGain > favGain,
            $"upset gain {upsetGain} must exceed favourite gain {favGain}");
    }

    /// <summary>⭐ The neutral combat-rating seed is 0.0f (native
    /// <c>PlayerScoreObject</c> ctor <c>m_fCombatRating(0.0f)</c> at igc.h:5510
    /// and <c>PersistPlayerScoreObject::Reset()</c> at igc.h:5453).</summary>
    [Fact]
    public void Combat_rating_defaults_to_native_neutral_seed_zero()
    {
        var ps = new PlayerScore();
        Assert.Equal(0f, ps.CombatRating);
        Assert.Equal(0, ps.Kills);
        Assert.Equal(0, ps.Deaths);
    }

    /// <summary>⭐ <see cref="PlayerScore.AddKill"/>/<see cref="PlayerScore.AddDeath"/>
    /// increment the corresponding counters — the port of native
    /// <c>m_cKills++</c>/<c>m_cDeaths++</c> (igc.h:5819/5837).</summary>
    [Fact]
    public void Add_kill_and_add_death_increment_the_right_counters()
    {
        var ps = new PlayerScore();
        ps.AddKill(); ps.AddKill();
        ps.AddDeath();
        Assert.Equal(2, ps.Kills);
        Assert.Equal(1, ps.Deaths);
    }
}

/// <summary>
/// P215 (player-scoring Stage 1) — live proof that
/// <see cref="Srv.BroadcastKillShip"/> (the server's <c>KillShipEvent</c> → kill
/// broadcast handler) really mutates the two participants' <see cref="PlayerScore"/>
/// counters and combat ratings, following native's <c>FedSrv.CPP</c> kill
/// handling. Mirrors <see cref="LiveCombatWireTests"/>' harness (real in-process
/// <see cref="Srv"/>, real <see cref="FltBot"/> peers over loopback TCP, a
/// <see cref="Srv.ShipSpawner"/> seam injecting a fightable non-lifepod hull on
/// DISTINCT sides so the cross-side kill-credit gate fires).
/// [Collection("LiveNetwork")] keeps this suite sequential against the other
/// live suites (same reason as <see cref="LiveCombatWireTests"/>).
/// </summary>
[Collection("LiveNetwork")]
public class LiveCombatScoringTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // A fightable (energy+ammo+fixed hardpoint), NON-lifepod hull + cheapest
    // real-damage weapon — the P215 variant of LiveCombatWireTests' Fight pick,
    // additionally excluding lifepod hulls so the port's faithful `!bLifepod`
    // kill-credit gate is satisfied (native refuses to credit killing an eject
    // pod, FedSrv.CPP:11811).
    private static readonly Lazy<(DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon)> Fight = new(() =>
    {
        var core = RepresentativeCore();
        foreach (var hull in core.HullTypes.Values
                     .Where(h => !h.HasCapability(DataHullTypeIGC.HabmLifepod))
                     .Where(h => h.MaxEnergy > 0 && h.MaxAmmo > 0 && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed)
                     .OrderBy(h => h.HullId))
        {
            foreach (var p in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
                         .Select(p => (DataPartTypeIGC.Weapon)p.Type)
                         .Where(w => w.Dispersion == 0f && w.EnergyPerShot <= hull.MaxEnergy)
                         .OrderBy(w => w.EnergyPerShot))
            {
                if (core.ProjectileTypes.TryGetValue(p.ProjectileTypeId, out var pt) && pt.Power > 0f)
                    return (hull, p);
            }
        }
        throw new InvalidOperationException("no fightable non-lifepod hull+weapon in the representative core");
    });

    private static StaticCore RepresentativeCore()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Test Mission", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest SampleRequest(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 926u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 9270000000000000000ul, DrmHash = "pscore215",
    };

    private const float TargetZ = -180f;

    /// <summary>Inject a spawner that seats the two peers on DISTINCT sides
    /// (shooter/origin → side 0, target/−z → side 1) with the non-lifepod fight
    /// hull, so a cross-side kill actually credits (native refuses same-side
    /// kill credit, FedSrv.CPP:11811-11821). The default LiveCombatWireTests
    /// spawner leaves SideId null, which would trip the port's
    /// <c>attacker.SideId != victim.SideId</c> gate.</summary>
    private static void InjectScoredFightSpawn(Srv server)
    {
        var placedShooter = false;
        server.ShipSpawner = (core, mission, id) =>
        {
            var (hull, weapon) = Fight.Value;
            Vector3 pos = !placedShooter ? new Vector3(0, 0, 0) : new Vector3(0, 0, TargetZ);
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity);
            mission.PlaceShip(ship, mission.Sectors[0]);
            ship.SideId = (short)(placedShooter ? 1 : 0);
            if (!placedShooter)
            {
                ship.Energy = hull.MaxEnergy * 100f;
                ship.MountWeapon(weapon);
            }
            placedShooter = true;
            return (ship, mission.Sectors[0]);
        };
    }

    /// <summary>Seat two real <see cref="FltBot"/> peers and return their ship
    /// ids resolved by spawn position (the origin ship is the shooter/attacker
    /// on side 0; the −z ship is the target on side 1) — robust to which socket
    /// the server drains first, exactly like <see cref="LiveCombatWireTests"/>.
    /// Caller is responsible for disposing the returned <see cref="Srv"/> and
    /// both <see cref="FltBot"/>s.</summary>
    private static (Srv server, short shooterId, short targetId, FltBot shooter, FltBot target) SeatTwoPeers()
    {
        var server = new Srv();
        InjectScoredFightSpawn(server);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        var a = new FltBot();
        var b = new FltBot();
        a.Connect(server.BoundPort, "SHOOTER"); b.Connect(server.BoundPort, "TARGET");
        a.SendLogonRequest(SampleRequest("SHOOTER")); b.SendLogonRequest(SampleRequest("TARGET"));
        int got = PumpUntil(server, () => a.ShipAssignment is not null && b.ShipAssignment is not null, 4000);
        Assert.True(got < 4000, "both bots must seat");

        var aSet = a.ShipAssignment!; var bSet = b.ShipAssignment!;
        bool aAtOrigin = aSet.Ship.Position.Z > -1f;
        short shooterId = (aAtOrigin ? aSet : bSet).Ship.ShipId;
        short targetId = (aAtOrigin ? bSet : aSet).Ship.ShipId;
        var shooter = aAtOrigin ? a : b;
        var target = aAtOrigin ? b : a;
        return (server, shooterId, targetId, shooter, target);
    }

    private static PlayerScore ScoreOf(Srv server, short shipId) =>
        server.Ships.First(s => s.Ship.Id == shipId).PlayerScore;

    // BroadcastKillShip is private; tests invoke it directly (the established
    // "testing private members" pattern) to deterministically exercise the
    // kill-death/rating wiring for the edge cases (bot participant, self-kill,
    // environmental) that are impractical to stage through live projectile
    // combat. The two-peer real-combat case (below) drives the real kill path.
    private static readonly MethodInfo? BroadcastKillShip = typeof(Srv).GetMethod(
        "BroadcastKillShip", BindingFlags.Instance | BindingFlags.NonPublic);

    private static void FireKill(Srv server, Ship victim, Ship? launcher, float amount)
    {
        BroadcastKillShip!.Invoke(server, new object?[]
        {
            1000.0, victim, launcher, amount, new Vector3(0, 0, 0), new Vector3(0, 0, 0),
        });
    }

    /// <summary>⭐ Real end-to-end kill: two real bots, real projectile combat,
    /// a real death — the shooter's <c>PlayerScore</c> gains a kill and a
    /// combat-rating bump, the victim's gains a death (and, from a 0 seed, stays
    /// at 0 rating). Verifies the wiring updates BOTH sides by the right amounts
    /// (native FedSrv.CPP:11819-11821 AddKill + AdjustCombatRating, and
    /// FedSrv.CPP:12026-12032 AddDeath).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Real_two_peer_kill_updates_both_player_scores()
    {
        Require.Artwork();
        var seat = SeatTwoPeers();
        using (var server = seat.server)
        using (var shooter = seat.shooter)
        using (var target = seat.target)
        {
            short shooterId = seat.shooterId;
            short targetId = seat.targetId;
            short effectiveTarget = target.ShipAssignment!.Ship.ShipId;

            // Force a REAL death (not an ejection) for the non-lifepod victim:
            // this port faithfully follows native's `bKilled` gate, so a
            // non-lifepod player ship that takes lethal damage EJECTS and
            // survives unless EjectSurvivor fails. Placing the victim on a
            // side that is NOT in the mission (Server.cs:10774-10778, the
            // "side not in mission sides — cannot eject" guard) deterministically
            // fails the eject and turns the damage into a real death — the only
            // real-combat configuration that yields BOTH a death AND cross-side
            // kill credit in this port. The victim is still a real peer (a
            // PlayerScore is keyed by ownership, not side), so both sides score.
            var targetShip = server.Ships.First(s => s.Ship.Id == targetId).Ship;
            targetShip.SideId = 774;

            shooter.CommandControls(ControlData.Neutral, ShipControlState.AllWeapons);
            target.CommandControls(ControlData.Neutral, 0);

            int ticks = 0;
            const int MaxTicks = 900;
            while (ticks < MaxTicks && !target.Kills.Any(k => k.ShipId == effectiveTarget))
            {
                server.TickSim();
                ticks++;
                Thread.Sleep(2);
            }
            Assert.True(ticks < MaxTicks, "no KILL_SHIP observed by the real Bot within budget");

            var shooterScore = ScoreOf(server, shooterId);
            var victimScore = ScoreOf(server, targetId);

            Assert.Equal(1, shooterScore.Kills);
            Assert.Equal(0, shooterScore.Deaths);
            Assert.Equal(1, victimScore.Deaths);
            Assert.Equal(0, victimScore.Kills);

            // Both started at the 0.0f neutral seed; after one kill the winner
            // gets k=(1.25+0-0)/100=0.0125, the loser (1-0.0125)·0 = 0.
            Assert.Equal(0.0125f, shooterScore.CombatRating, 5);
            Assert.Equal(0f, victimScore.CombatRating);

            Console.WriteLine($"P215-LIVE: real kill: shooter {shooterId} Kills={shooterScore.Kills} " +
                              $"CR={shooterScore.CombatRating:R}; victim {targetId} Deaths={victimScore.Deaths} " +
                              $"CR={victimScore.CombatRating:R} (after {ticks} ticks)");
        }
    }

    /// <summary>⭐ A player killing an UNOWNED (bot) ship: the player banks the
    /// kill counter but no death is recorded anywhere and no rating swings
    /// (the bot has no PlayerScore/rating holder — the task's stated
    /// simplification of native). Must not crash.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Player_killing_an_unowned_bot_ship_only_credits_the_player()
    {
        Require.Artwork();
        var seat = SeatTwoPeers();
        using (var server = seat.server)
        using (var shooter = seat.shooter)
        using (var target = seat.target)
        {
            short shooterId = seat.shooterId;
            var shooterLive = server.Ships.First(s => s.Ship.Id == shooterId);
            var mission = shooterLive.Ship.Mission!;
            var sector = shooterLive.Ship.Sector!;

            // An ownerless NPC ship on the OTHER side (side 1), with the same
            // non-lifepod fight hull, so the cross-side kill-credit gate passes.
            var (hull, _) = Fight.Value;
            var npc = new Ship((short)940, hull, new Vector3(0, 0, TargetZ), Vector3.Zero, Orientation.Identity)
            { SideId = 1 };
            mission.PlaceShip(npc, sector);

            FireKill(server, npc, shooterLive.Ship, amount: -1f);

            var shooterScore = ScoreOf(server, shooterId);
            Assert.Equal(1, shooterScore.Kills);
            Assert.Equal(0, shooterScore.Deaths);
            Assert.Equal(0f, shooterScore.CombatRating);   // no rating swing: bot victim has no rating
        }
    }

    /// <summary>⭐ An unowned (bot) ship killing a player: the player's death is
    /// counted, but no kill credit and no rating swing (the killer has no
    /// PlayerScore). Must not crash — the "only whichever side is a real peer"
    /// behaviour.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Bot_killing_a_player_only_counts_the_victim_death()
    {
        Require.Artwork();
        var seat = SeatTwoPeers();
        using (var server = seat.server)
        using (var shooter = seat.shooter)
        using (var target = seat.target)
        {
            short targetId = seat.targetId;
            var targetLive = server.Ships.First(s => s.Ship.Id == targetId);
            var mission = targetLive.Ship.Mission!;
            var sector = targetLive.Ship.Sector!;

            var (hull, _) = Fight.Value;
            var npc = new Ship((short)941, hull, new Vector3(0, 0, 0), Vector3.Zero, Orientation.Identity)
            { SideId = 0 };   // the attacker NPC on the OTHER side (shooter side 0)
            mission.PlaceShip(npc, sector);

            FireKill(server, targetLive.Ship, npc, amount: -1f);

            var victimScore = ScoreOf(server, targetId);
            Assert.Equal(1, victimScore.Deaths);
            Assert.Equal(0, victimScore.Kills);
            Assert.Equal(0f, victimScore.CombatRating);   // no rating swing: bot killer has no rating
        }
    }

    /// <summary>⭐ Suicide and environmental death count the death but never
    /// touch combat rating or grant kill credit (native FedSrv.CPP:11811-11821
    /// requires a cross-side launcher; self-kill is same-side and an
    /// environmental death has no launcher).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Self_kill_and_environmental_death_do_not_touch_combat_rating()
    {
        Require.Artwork();
        var seat = SeatTwoPeers();
        using (var server = seat.server)
        using (var shooter = seat.shooter)
        using (var target = seat.target)
        {
            short shooterId = seat.shooterId;
            short targetId = seat.targetId;
            var shooterShip = server.Ships.First(s => s.Ship.Id == shooterId).Ship;
            var targetShip = server.Ships.First(s => s.Ship.Id == targetId).Ship;

            // Self-kill: launcher == victim.
            FireKill(server, shooterShip, shooterShip, amount: -1f);
            var selfKiller = ScoreOf(server, shooterId);
            Assert.Equal(1, selfKiller.Deaths);
            Assert.Equal(0, selfKiller.Kills);
            Assert.Equal(0f, selfKiller.CombatRating);

            // Environmental death: launcher == null.
            FireKill(server, targetShip, null, amount: -1f);
            var envVictim = ScoreOf(server, targetId);
            Assert.Equal(1, envVictim.Deaths);
            Assert.Equal(0, envVictim.Kills);
            Assert.Equal(0f, envVictim.CombatRating);
        }
    }
}
