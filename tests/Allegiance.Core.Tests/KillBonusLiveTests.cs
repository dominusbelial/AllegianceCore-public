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
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P278 — Kill Bonus: LIVE proof that the real server kill path mutates a
/// killer ship's per-ship kill-streak counter per native's formula
/// (<c>FedSrv.CPP:11824</c> AddExperience on an eligible kill), that the real
/// damage path scales a hit by the launcher's multiplier, and that destroying
/// the player's OWN pod resets the streak to 0 (<c>FedSrv.CPP:11783-11787</c>).
/// Reuses LiveCombatScoringTests' (P215) harness: real in-process
/// <see cref="Srv"/>, real <see cref="FltBot"/> peers over loopback TCP, and a
/// <see cref="Srv.ShipSpawner"/> seam injecting a fightable non-lifepod hull on
/// DISTINCT sides so cross-side kill credit fires.
/// [Collection("LiveNetwork")] keeps this sequential against the other live
/// suites (same as the P215 live suite).
/// </summary>
[Collection("LiveNetwork")]
public class KillBonusLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

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
        FedsrvVer = LogonRequest.MsgVer, Time = 926u, DwCookie = 0x926u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 9280000000000000000ul, DrmHash = "pscore278",
    };

    private const float TargetZ = -180f;

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

    private static (Srv server, Ship shooterShip, Ship targetShip, FltBot shooter, FltBot target) SeatTwoPeers()
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
        var shooterShip = server.Ships.First(s => s.Ship.Id == shooterId).Ship;
        var targetShip = server.Ships.First(s => s.Ship.Id == targetId).Ship;
        return (server, shooterShip, targetShip, shooter, target);
    }

    private static readonly MethodInfo? BroadcastKillShip = typeof(Srv).GetMethod(
        "BroadcastKillShip", BindingFlags.Instance | BindingFlags.NonPublic);

    private static void FireKill(Srv server, Ship victim, Ship? launcher, float amount)
    {
        BroadcastKillShip!.Invoke(server, new object?[]
        {
            1000.0, victim, launcher, amount, new Vector3(0, 0, 0), new Vector3(0, 0, 0),
        });
    }

    /// <summary>⭐ Several real kills, sequentially, climb the killer ship's KB
    /// counter per the native formula — and the reset-to-0 post-death baseline
    /// means the first kill lands exactly on "1 kill → 10". Each FireKill goes
    /// through the REAL server BroadcastKillShip kill-credit path (native
    /// FedSrv.CPP:11824 AddExperience), against distinct cross-side unowned
    /// non-lifepod NPC victims so the credit gate fires every time.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Several_real_kills_climb_the_shooters_kill_bonus_per_native_formula()
    {
        Require.Artwork();
        var seat = SeatTwoPeers();
        using (var server = seat.server)
        using (var shooter = seat.shooter)
        using (var target = seat.target)
        {
            var shooterShip = seat.shooterShip;
            var mission = shooterShip.Mission!;
            var sector = shooterShip.Sector!;
            var (hull, _) = Fight.Value;

            // A player who just lost their pod is at KB 0 (native reset). From
            // this baseline the first kill → counter 1 → KB 10.
            shooterShip.ResetKillBonus();

            Console.WriteLine($"P278-LIVE: shooter ship {shooterShip.Id} starts at KB {shooterShip.KillBonus:R}");

            for (int i = 0; i < 4; i++)
            {
                var npc = new Ship((short)(950 + i), hull, new Vector3(0, 0, TargetZ), Vector3.Zero, Orientation.Identity)
                { SideId = 1 };                       // cross-side, non-lifepod fight hull
                mission.PlaceShip(npc, sector);

                FireKill(server, npc, shooterShip, amount: -1f);

                float expected = 50f * (i + 1) / (i + 5);   // counter (i+1): 50·c/(c+4)
                Console.WriteLine($"P278-LIVE: after kill #{i + 1}: KB counter={shooterShip.KillBonus:R} " +
                                  $"KB%={shooterShip.KillBonusPercent:R} (expect {expected:R})");
                Assert.Equal(i + 1, (int)shooterShip.KillBonus);
                Assert.Equal(expected, shooterShip.KillBonusPercent, 3);
                Assert.True(shooterShip.KillBonusPercent <= 50f, "never exceeds the cap");
            }
        }
    }

    /// <summary>⭐ A real end-to-end projectile kill (real projectile → real
    /// ReceiveDamage → real KillShipEvent → BroadcastKillShip) bumps the killer's
    /// KB counter by exactly one AND banks the PlayerScore kill — the whole
    /// chain, not the reflection shortcut. Victim parked on a side not in the
    /// mission (Server.cs guard fails the eject) so the non-lifepod death is
    /// real and cross-side credit fires.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Real_end_to_end_projectile_kill_increases_kill_bonus()
    {
        Require.Artwork();
        var seat = SeatTwoPeers();
        using (var server = seat.server)
        using (var shooter = seat.shooter)
        using (var target = seat.target)
        {
            var shooterShip = seat.shooterShip;
            var targetShip = seat.targetShip;
            short shooterId = shooterShip.Id;
            short targetId = targetShip.Id;

            float kbBefore = shooterShip.KillBonus;   // seed 1.0
            targetShip.SideId = 774;                  // force a real death (eject fails)

            shooter.CommandControls(ControlData.Neutral, ShipControlState.AllWeapons);
            target.CommandControls(ControlData.Neutral, 0);

            int ticks = 0;
            const int MaxTicks = 900;
            while (ticks < MaxTicks && !target.Kills.Any(k => k.ShipId == targetId))
            {
                server.TickSim();
                ticks++;
                Thread.Sleep(2);
            }
            Assert.True(ticks < MaxTicks, "no KILL_SHIP observed by the real Bot within budget");

            var killerScore = server.Ships.First(s => s.Ship.Id == shooterId).PlayerScore;
            Assert.Equal(1, killerScore.Kills);
            Assert.Equal(kbBefore + 1f, shooterShip.KillBonus, 3);
            Console.WriteLine($"P278-LIVE: real projectile kill: KB {kbBefore:R} -> {shooterShip.KillBonus:R} " +
                              $"({shooterShip.KillBonusPercent:R}%), PlayerScore.Kills={killerScore.Kills}");
        }
    }

    /// <summary>⭐ Destroying the player's OWN eject pod resets the streak to 0
    /// — native FedSrv.CPP:11783-11787 `if (bKilled) pship->SetExperience(0)`.
    /// A lifepod hull taking a positive-damage kill is bKilled via bLifepod
    /// (the AFS "your pod is destroyed" trigger), so the KB counter drops from a
    /// non-zero streak straight to 0.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Destroying_the_players_own_pod_resets_the_streak_to_zero()
    {
        Require.Artwork();
        var seat = SeatTwoPeers();
        using (var server = seat.server)
        using (var shooter = seat.shooter)
        using (var target = seat.target)
        {
            var shooterShip = seat.shooterShip;
            var enemy = seat.targetShip;

            // Establish a real non-zero streak.
            for (int i = 0; i < 3; i++) shooterShip.AddKillBonus();
            Assert.True(shooterShip.KillBonus > 0f);

            // Turn the shooter into a real Lifepod hull, then destroy that pod
            // with a positive-damage kill — bKilled (bLifepod) → ResetKillBonus.
            var podHull = RepresentativeCore().HullTypes.Values
                .First(h => h.HasCapability(DataHullTypeIGC.HabmLifepod));
            shooterShip.ChangeHull(podHull);

            FireKill(server, shooterShip, enemy, amount: 1.0f);

            Assert.Equal(0f, shooterShip.KillBonus);
            Assert.Equal(0f, shooterShip.KillBonusPercent);
            Assert.Equal(1f, shooterShip.ExperienceMultiplier, 4);
            Console.WriteLine($"P278-LIVE: own pod destroyed -> KB reset to {shooterShip.KillBonus:R} " +
                              $"(multiplier {shooterShip.ExperienceMultiplier:R})");
        }
    }

    /// <summary>⭐ Real mission ships + the real core armor matrix: a real hit's
    /// measured hull-fraction drop at a non-zero KB is exactly ExperienceMultiplier×
    /// the drop at KB 0 (same damage amount, same target, only the launcher KB
    /// changes). Exercises the real damage path with real core data
    /// (shipIGC.cpp:1238-1242 + stationIGC.cpp:142-145), not the no-mission
    /// default.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Real_mission_damage_scales_with_kill_bonus()
    {
        Require.Artwork();
        var seat = SeatTwoPeers();
        using (var server = seat.server)
        using (var shooter = seat.shooter)
        using (var target = seat.target)
        {
            var shooterShip = seat.shooterShip;
            var targetShip = seat.targetShip;

            var mission = shooterShip.Mission!;
            var core = mission.Core;
            var proj = core.ProjectileTypes[Fight.Value.weapon.ProjectileTypeId];
            byte dmgType = proj.DamageType;                   // real weapon damage type
            var tankHull = core.HullTypes.Values.OrderByDescending(h => h.HitPoints).First();
            float amount = tankHull.HitPoints * 0.015f;       // non-saturating 1.5% hit

            float Drop(Ship launcher)
            {
                var t = new Ship((short)9701, tankHull, targetShip.Position, Vector3.Zero, Orientation.Identity);
                mission.PlaceShip(t, shooterShip.Sector!);
                float before = t.Fraction;
                t.ReceiveDamage(dmgType, amount, 0.0, Vector3.Zero, Vector3.Zero, launcher);
                return before - t.Fraction;
            }

            // KB 0 baseline (post-death / fresh launcher).
            var kb0 = new Ship((short)9702, shooterShip.HullType, Vector3.Zero, Vector3.Zero, Orientation.Identity);
            mission.PlaceShip(kb0, shooterShip.Sector!);
            kb0.ResetKillBonus();

            // Non-zero KB launcher (a 4-kill streak → counter 4 → multiplier 1.25).
            var kbHigh = new Ship((short)9703, shooterShip.HullType, Vector3.Zero, Vector3.Zero, Orientation.Identity);
            mission.PlaceShip(kbHigh, shooterShip.Sector!);
            for (int i = 0; i < 4; i++) kbHigh.AddKillBonus();

            float d0 = Drop(kb0);
            Assert.True(d0 > 0f, "real weapon must actually damage the hull");
            float dHi = Drop(kbHigh);

            Assert.Equal(kbHigh.ExperienceMultiplier, dHi / d0, 3);
            Assert.True(dHi > d0, "higher KB must deal more measured real-core damage");
            Console.WriteLine($"P278-LIVE: real-core hit drop KB0={d0:R} vs KB1={dHi:R} " +
                              $"(ratio {dHi / d0:R}, multiplier {kbHigh.ExperienceMultiplier:R})");
        }
    }
}
