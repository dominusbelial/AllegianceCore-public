using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// P102 — live loopback proof that a real asteroid is a valid direct-hit target of
/// real ordnance in the existing swept collision pass
/// (<see cref="Sector.ResolveCombatCollisions"/>), with the two deliberate
/// behaviors the native formula dictates, plus the broadcast audience:
/// <list type="number">
/// <item>a <b>cross-side</b> missile hit reduces <see cref="Asteroid.Fraction"/>
/// by the exact decoded <c>Power·DamageConstant/maxHP</c> amount (defense index
/// <c>c_defidAsteroid = 0</c>), and the rock survives (stays in
/// <see cref="Sector.Mineable"/>, not destroyed);</item>
/// <item>a <b>same-side (friendly)</b> missile hit damages the asteroid just the
/// same — proving the "asteroids have no side concept at all" finding
/// (<c>asteroidigc.h:126-171</c>) is real and not just asserted in a comment;</item>
/// <item>enough hits to cross <c>Fraction ≤ 0</c> really Killed the rock: it is
/// removed from <see cref="Sector.Mineable"/>, and a real
/// <c>FM_S_ASTEROID_DESTROYED(86)</c> naming that same asteroid id and its sector,
/// with <c>explodeF=true</c>, is broadcast to the WHOLE MISSION — the
/// cross-side enemy bot receives it exactly like the same-side peer, confirming
/// the <c>GetGroupRealSides()</c> audience (<c>FedSrv.CPP:12418</c>), the same
/// settlement P101 reached for <c>WARP_BOMB</c>.</item>
/// </list>
/// Construction mirrors <see cref="ProbeCombatLiveTests"/>' 3-bot / 2-side
/// harness (a guaranteed same-side pair plus a cross-side enemy) but the target is
/// a <b>synthetic</b> asteroid placed in sector 0 via <c>Mission.PlaceAsteroid</c>
/// in the ShipSpawner (the demo map has zero asteroids — the same reason
/// <see cref="MiningTests"/> injects its own rock), and ordnance is real missiles
/// fired via <c>Ship.FireMissile</c> at that rock. Ground truth is read off the
/// live server-side asteroid (<c>Fraction</c>, <c>Sector.Mineable</c> membership)
/// and the wire <see cref="Bot.AsteroidDestroyeds"/> collection on BOTH sides.
/// </summary>
[Collection("LiveNetwork")]
public sealed class AsteroidCombatLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static float Dtm(StaticCore core, byte dmg, byte def) => core.Constants.DamageConstants[dmg][def];

    private static DataAsteroidIGC MkAsteroid(short id, Vector3 pos, float oreMax, float hitPoints, short radius, short sectorId)
    {
        return new DataAsteroidIGC(
            Signature: 1.0f,
            Position: [pos.X, pos.Y, pos.Z],
            Up: [0f, 1f, 0f],
            Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f],
            Ore: oreMax,
            OreMax: oreMax,
            AabmCapabilities: Asteroid.CapMineHe3,
            AsteroidId: id,
            HitPoints: hitPoints,
            Radius: radius,
            ModelName: "roid.mdl",
            TextureName: "",
            IconName: "",
            ClusterId: sectorId,
            Name: $"ROID_{id}",
            Fraction: 0);
    }

    /// <summary>A (missile, asteroid hit-points) pair where one cross-side hit lands
    /// a clean, survivable fraction on the rock with straight-line reach ≥ 90 m (so
    /// we can aim a real impact). Scanned from decoded data: most PCore001 missiles
    /// have a ZERO damage-matrix cell for the asteroid defense index (0); only a few
    /// (e.g. type 39, Power 400 × dtm 1.0) hit a rock at all. The asteroid HP is
    /// chosen to make exactly one hit deal a clear ~40% fraction (survivable, and
    /// numerically asserted to 3 decimals); HP is a synthetic rock (the established
    /// MiningTests seam) and the computed delta is self-consistent with whichever
    /// HP is returned. Never hardcoded in production — the formula always reads
    /// <see cref="Asteroid.Data"/>.HitPoints.</summary>
    private static readonly Lazy<(DataMissileTypeIGC Missile, float HitPoints, float Delta)> Nonlethal = new(() =>
    {
        var core = Core();
        foreach (var m in core.MissileTypes.Values
            .Where(mm => mm.InitialSpeed > 0f && mm.Power > 0f && mm.Lifespan >= 4f && mm.Lifespan <= 8f))
        {
            if (m.InitialSpeed * m.Lifespan < 90f) continue;
            float dtm = Dtm(core, m.DamageType, Asteroid.DefenseType);
            if (dtm <= 0f) continue;
            float hitDmg = m.Power * dtm;
            // HP chosen so a single hit lands ~40% of maxHP: a clear, survivable,
            // numerically-asserted reduction (fraction → 0.6). Round to an integer
            // HP and recompute the exact delta from that HP (self-consistent).
            float hp = MathF.Max(800f, MathF.Round(hitDmg / 0.4f));
            float delta = hitDmg / hp;
            if (delta >= 0.15f && delta <= 0.6f)            // surviving fraction ∈ [0.4, 0.85] — clearly alive, clearly measurable
                return (m, hp, delta);
        }
        throw new InvalidOperationException("no non-lethal (missile, asteroid-HP) combo in representative core");
    });

    /// <summary>A (missile, asteroid hit-points) pair where a SINGLE hit is lethal
    /// (<c>Power·dtm ≥ maxHP</c>) and the missile has reach ≥ 90 m — so one
    /// cross-side shot crosses <c>Fraction ≤ 0</c> and kills the rock outright.</summary>
    private static readonly Lazy<(DataMissileTypeIGC Missile, float HitPoints)> Lethal = new(() =>
    {
        var core = Core();
        foreach (var m in core.MissileTypes.Values
            .Where(mm => mm.InitialSpeed > 0f && mm.Power > 0f && mm.Lifespan >= 4f && mm.Lifespan <= 8f))
        {
            if (m.InitialSpeed * m.Lifespan < 90f) continue;
            float dtm = Dtm(core, m.DamageType, Asteroid.DefenseType);
            if (dtm <= 0f) continue;
            foreach (var hp in new[] { 150f, 300f, 500f, 800f })
                if (m.Power * dtm >= hp)
                    return (m, hp);
        }
        throw new InvalidOperationException("no lethal (missile, asteroid-HP) combo in representative core");
    });

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Asteroid Combat Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 777u, DwCookie = 0x777u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 77100000000000000ul, DrmHash = "asteroidcombat",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>A booted 3-bot / 2-side live server plus the resolved roles and the
    /// single synthetic asteroid (id 6401) placed in sector 0 by the ShipSpawner
    /// (the demo map has zero rocks, so this is the only one in the universe). Slot
    /// 0 = <b>side A</b> at the origin identity (nose → −z); slot 1 = <b>side B</b>
    /// cross-side; slot 2 = <b>side A</b> same-side observer. Tests reposition a
    /// shooter ahead of the rock (along −z) and fire real missiles at it; the rock
    /// lives far enough from the origin spawns that parked miners never interfere.
    /// </summary>
    private sealed class Seated : IDisposable
    {
        public Srv Server = null!;
        public Ship A = null!, B = null!, Teammate = null!;
        public FltBot ABot = null!, BBot = null!, TeammateBot = null!;
        public Asteroid Rock = null!;
        public Sector Sector = null!;
        public StaticCore Core = null!;

        public void Dispose()
        {
            foreach (var b in new[] { ABot, BBot, TeammateBot }) b?.Dispose();
            Server?.Dispose();
        }
    }

    private static Seated SeatThree(float gap, DataHullTypeIGC hull, float hitPoints)
    {
        var seat = new Seated();
        var core = Core();
        seat.Core = core;
        seat.Server = new Srv();

        Vector3 apos = new(0f, 0f, -600f);   // far from the origin spawn, down the −z beam
        int placed = 0;
        seat.Server.ShipSpawner = (_, mission, id) =>
        {
            var sector = mission.Sectors[0];
            seat.Sector = sector;
            if (seat.Rock is null)
            {
                seat.Rock = new Asteroid(MkAsteroid(6401, apos, oreMax: 9000f, hitPoints: hitPoints, radius: 40, sectorId: sector.Id));
                mission.PlaceAsteroid(seat.Rock, sector);
            }
            var ship = placed switch
            {
                0 => new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity),
                1 => new Ship(id, hull, new Vector3(0f, 0f, -gap), Vector3.Zero, Orientation.Identity),
                _ => new Ship(id, hull, new Vector3(0f, 620f, 720f), Vector3.Zero, Orientation.Identity),
            };
            if (mission.Sides.Count > 0)
                ship.SideId = mission.Sides[placed % mission.Sides.Count].Id;   // round-robin A,B,A
            mission.PlaceShip(ship, sector);
            placed++;
            return (ship, sector);
        };
        seat.Server.Boot(Config());
        var port = seat.Server.BoundPort;

        var bots = new[] { new FltBot(), new FltBot(), new FltBot() };
        string[] names = { "p102_a", "p102_b", "p102_teammate" };
        try
        {
            for (int i = 0; i < bots.Length; i++) { bots[i].Connect(port, names[i]); bots[i].SendLogonRequest(Request(names[i])); }
            PumpUntil(seat.Server, () => bots.All(b => b.ShipAssignment is not null), 4000);
            Assert.True(bots.All(b => b.ShipAssignment is not null), "all three bots must be assigned a ship");

            foreach (var bot in bots)
            {
                var ship = seat.Server.Ships.First(s => s.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Ship;
                if (ship.Position.Length() < 1f) { seat.A = ship; seat.ABot = bot; }
                else if (MathF.Abs(ship.Position.Z + gap) < 2f) { seat.B = ship; seat.BBot = bot; }
                else { seat.Teammate = ship; seat.TeammateBot = bot; }
            }
            Assert.NotNull(seat.A); Assert.NotNull(seat.B); Assert.NotNull(seat.Teammate);
            Assert.True(seat.A.SideId != seat.B.SideId, "enemy must be cross-side to the A ship");
            Assert.True(seat.A.SideId == seat.Teammate.SideId, "teammate must be same-side to the A ship");
            seat.A.Velocity = Vector3.Zero;
            seat.B.Velocity = Vector3.Zero;
            seat.Teammate.Velocity = Vector3.Zero;
            return seat;
        }
        catch
        {
            foreach (var b in bots) b.Dispose();
            seat.Server.Dispose();
            throw;
        }
    }

    /// <summary>The distance the firing ship parks behind the asteroid (ahead, in
    /// −z) for a real missile impact, sized to a comfortable fraction of the
    /// missile's reach — and kept outside the rock's mining proximity envelope
    /// (<c>radius + shipHalfLen + 30</c>, roughly 110 for a radius-40 rock) so the
    /// firing ship never accidentally starts mining while shooting.</summary>
    private static float FireGap(DataMissileTypeIGC m)
        => MathF.Min(0.55f * m.InitialSpeed * m.Lifespan, 170f);

    /// <summary>⭐ A real cross-side missile hit on a real asteroid reduces the
    /// rock's <see cref="Asteroid.Fraction"/> by the exact decoded
    /// <c>Power·DamageConstant/maxHP</c> amount (defense index c_defidAsteroid = 0)
    /// — asserted numerically, not just "some damage" — and the rock survives
    /// (still in <see cref="Sector.Mineable"/>, no destroy). The cross-side enemy
    /// fires its own missile at the unowned rock.</summary>
    [Fact]
    public void Cross_side_missile_hits_asteroid_with_exact_computed_damage_and_it_survives()
    {
        var (missile, hitPoints, delta) = Nonlethal.Value;
        float fireGap = FireGap(missile);
        Assert.True(fireGap < missile.InitialSpeed * missile.Lifespan, "firing gap must be inside missile reach");
        using var seat = SeatThree(100f, Core().HullTypes.Values.OrderBy(h => h.HitPoints).First(), hitPoints);
        var a = seat.A;
        var enemy = seat.B;
        var rock = seat.Rock;
        var sector = seat.Sector;

        var mission = a.Mission!;
        Assert.Equal(1f, rock.Fraction, 3);
        Assert.Contains(rock, sector.Mineable);

        // The cross-side enemy repositions ahead of the rock (identity nose → −z)
        // and fires a real missile straight at it.
        enemy.Position = rock.Data.Position is { Length: 3 } p ? new Vector3(p[0], p[1], p[2]) + new Vector3(0f, 0f, fireGap) : Vector3.Zero;
        float f0 = rock.Fraction;
        enemy.FireMissile(missile, mission.LastUpdate);

        PumpUntil(seat.Server, () => rock.Fraction < f0 - 0.030f, 6000);
        Assert.True(rock.Fraction < f0, "cross-side missile must actually hit the asteroid");

        // (1) exact computed fraction: 1 − delta (decoded data).
        Assert.Equal(1f - delta, rock.Fraction, 3);
        // (4) survived: still in its sector's Mineable list, not destroyed.
        Assert.Contains(rock, sector.Mineable);
        Assert.True(!rock.Dead, "rock must survive a single non-lethal hit");
        Console.WriteLine($"P102-CROSS: asteroid {f0:F2}→{rock.Fraction:F3} (expect 1 − {delta:F3}); survived, still in Mineable");
    }

    /// <summary>⭐ A SAME-SIDE (friendly) missile hit damages the asteroid exactly
    /// the same as a cross-side one — proving the "asteroids have no side concept
    /// at all" finding (<c>asteroidigc.h:126-171</c>, no side/friendly-fire gate
    /// anywhere in the formula) is real, not just asserted in a comment. The
    /// same-side teammate ship fires its own missile at the rock and the identical
    /// damage lands. This is deliberately stronger than the probe case (P100), which
    /// still reasoned about an unarmed probe; an asteroid has no SideId whatsoever.</summary>
    [Fact]
    public void Same_side_friendly_missile_damages_the_asteroid_no_friendly_fire_gate()
    {
        var (missile, hitPoints, delta) = Nonlethal.Value;
        float fireGap = FireGap(missile);
        using var seat = SeatThree(100f, Core().HullTypes.Values.OrderBy(h => h.HitPoints).First(), hitPoints);
        var a = seat.A;
        var teammate = seat.Teammate;   // same side A as the a ship ⇒ same side, though the rock has no side anyway
        var rock = seat.Rock;
        var sector = seat.Sector;

        var mission = a.Mission!;
        Assert.Equal(1f, rock.Fraction, 3);

        // The SAME-SIDE teammate fires its own missile at the rock. For a ship this
        // would be gated away and pass through; for an asteroid there is NO gate —
        // the damage must land identically.
        teammate.Position = rock.Data.Position is { Length: 3 } p ? new Vector3(p[0], p[1], p[2]) + new Vector3(0f, 0f, fireGap) : Vector3.Zero;
        float f0 = rock.Fraction;
        teammate.FireMissile(missile, mission.LastUpdate);

        PumpUntil(seat.Server, () => rock.Fraction < f0 - 0.040f, 6000);
        Assert.True(rock.Fraction < f0, "friendly fire must still damage the asteroid");
        Assert.Equal(1f - delta, rock.Fraction, 3);          // identical computed damage
        Assert.Contains(rock, sector.Mineable);
        Assert.True(!rock.Dead, "rock must survive a single non-lethal friendly hit");
        Console.WriteLine($"P102-FRIENDLY: same-side missile still dealt {delta:F3} (asteroid {f0:F2}→{rock.Fraction:F3}); " +
                          $"NO friendly-fire gate for an asteroid (any side damages it)");
    }

    /// <summary>⭐ Enough damage to cross <c>Fraction ≤ 0</c> really Killed the
    /// asteroid: a single lethal cross-side missile removes it from
    /// <see cref="Sector.Mineable"/> (so it stops being minable and vanishes from
    /// future passes), and a real <see cref="AsteroidDestroyed"/>(86) naming that
    /// same asteroid id and its sector, with <c>explodeF=true</c>, is delivered to
    /// the cross-side enemy bot AND the same-side teammate bot — the WHOLE-mission
    /// audience (<c>GetGroupRealSides()</c>, <c>FedSrv.CPP:12418</c>), settling the
    /// broadcast-audience question the same way P101 settled <c>WARP_BOMB</c>.</summary>
    [Fact]
    public void Lethal_hit_kills_asteroid_removes_from_mineable_and_broadcasts_destroy_to_whole_mission()
    {
        var (missile, hitPoints) = Lethal.Value;
        float fireGap = FireGap(missile);
        using var seat = SeatThree(100f, Core().HullTypes.Values.OrderBy(h => h.HitPoints).First(), hitPoints);
        var a = seat.A;
        var enemy = seat.B;
        var rock = seat.Rock;
        var sector = seat.Sector;

        // Clear any pre-join notices and confirm both bots are seated (they must
        // be in the peer map to receive the whole-mission broadcast).
        seat.BBot.ResetAsteroidDestroyeds();
        seat.TeammateBot.ResetAsteroidDestroyeds();
        Assert.Equal(1f, rock.Fraction, 3);
        Assert.Contains(rock, sector.Mineable);

        // One lethal cross-side missile kills it outright.
        enemy.Position = rock.Data.Position is { Length: 3 } p ? new Vector3(p[0], p[1], p[2]) + new Vector3(0f, 0f, fireGap) : Vector3.Zero;
        enemy.FireMissile(missile, a.Mission!.LastUpdate);
        PumpUntil(seat.Server, () => rock.Dead, 9000);
        Assert.True(rock.Dead, "lethal cross-side hit must kill the asteroid");

        // Removed from Mineable (the death contract: stops being minable), fraction clamped to 0.
        Assert.DoesNotContain(rock, sector.Mineable);
        Assert.Equal(0f, rock.Fraction, 3);
        Assert.Equal(sector.Id, rock.Data.ClusterId);   // sanity: the rock's sector id matches the sector it lived in

        // The ASTEROID_DESTROYED(86) broadcast — naming the CORRECT asteroid id and
        // sector, with explodeF=true — reaches BOTH peers: the same-side teammate
        // AND the cross-side enemy. That cross-side arrival is the whole-mission
        // proof (GetGroupRealSides(), FedSrv.CPP:12418).
        PumpUntil(seat.Server, () => seat.BBot.AsteroidDestroyeds.Count > 0 && seat.TeammateBot.AsteroidDestroyeds.Count > 0, 6000);
        Assert.True(seat.BBot.AsteroidDestroyeds.Count > 0, "cross-side enemy must receive ASTEROID_DESTROYED (whole mission)");
        Assert.True(seat.TeammateBot.AsteroidDestroyeds.Count > 0, "same-side teammate must receive ASTEROID_DESTROYED (whole mission)");

        foreach (var d in seat.BBot.AsteroidDestroyeds.Concat(seat.TeammateBot.AsteroidDestroyeds))
        {
            Assert.Equal(rock.Data.AsteroidId, d.AsteroidId);
            Assert.Equal(sector.Id, d.ClusterId);
            Assert.True(d.ExplodeF, "combat death must set explodeF=true");
        }
        Console.WriteLine($"P102-KILL: asteroid {rock.Data.AsteroidId} '{rock.Data.Name}' killed (fraction 1.0→0.0), " +
                          $"removed from Mineable, ASTEROID_DESTROYED(86) at sector {sector.Id} -> BOTH the same-side and cross-side " +
                          $"peer (explodeF=true) [WHOLE MISSION]");
    }
}
