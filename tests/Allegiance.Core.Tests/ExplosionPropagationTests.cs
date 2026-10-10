using Allegiance.Core;
using Allegiance.Core.Collision;
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
/// P96 (updated by P452) — the real explosion/blast-radius system and mine
/// destructibility. A generic <see cref="Explosion"/> type
/// (<see cref="Sector.ResolveExplosion"/>) distributes squared-distance
/// falloff damage to every ship and mine in blast range (native
/// <c>clusterIGC.cpp:362-467</c>), and a mine inside another's blast takes
/// <see cref="Mine.ReduceStrength"/> (native <c>mineigc.h:170-181</c>) which
/// can destroy it below the <c>0.25f</c> threshold.
/// </summary>
/// <remarks>
/// P452 removed the one path that used to <em>source</em> a real blast from a
/// mine: mines are now persistent attrition fields (<c>mineigc.h:68-145</c>)
/// and never create an area <see cref="Explosion"/>. The generic blast system
/// itself (weapon/missile-sourced) and its propagation into mines via
/// <see cref="Mine.ReduceStrength"/> are unchanged and remain the concern of
/// this file's domain tests; the former live <em>mine-detonation blast-cascade</em>
/// proof is converted to the P452 semantics (two armed fields, no cascade).
/// Everything is data-derived from <c>PCore001.igc</c>, reusing the exact
/// combo-selection recipes the combat and mine tests already use (no hardcoded
/// damage values).
/// </remarks>
[Collection("LiveNetwork")]
public sealed class ExplosionPropagationTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore RepresentativeCore()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static float Dtm(StaticCore core, byte dmg, byte def) => core.Constants.DamageConstants[dmg][def];

    // ── combat combo selection (verbatim from MineCombatTests) ──

    private static readonly Lazy<(DataMineTypeIGC Mine, DataHullTypeIGC Hull, float ExpectedDelta)> CrossHit = new(() =>
    {
        var core = RepresentativeCore();
        foreach (var m in core.MineTypes.Values
            .Where(mm => mm.MineRadius > 0f && mm.Power > 0f && mm.Lifespan >= 2.5f)
            .OrderBy(mm => mm.Power))
        {
            foreach (var hull in core.HullTypes.Values.OrderBy(h => h.HitPoints))
            {
                float dtm = Dtm(core, m.DamageType, hull.DefenseType);
                if (dtm <= 0f) continue;
                float delta = m.Power * dtm / hull.HitPoints;
                if (delta >= 0.035f && delta <= 0.730f)
                    return (m, hull, delta);
            }
        }
        throw new InvalidOperationException("no cross-side (mine, hull) combo in representative core");
    });

    private static readonly Lazy<(DataMineTypeIGC Mine, DataPartTypeIGC ShieldPart, DataPartTypeIGC.Shield ShieldType, float ExpectedAbsorbed, DataHullTypeIGC Hull, float DtmHull)> ShieldCombo = new(() =>
    {
        var core = RepresentativeCore();
        foreach (var m in core.MineTypes.Values
            .Where(mm => mm.Power > 0f && mm.Lifespan >= 2.5f)
            .OrderBy(mm => mm.Power))
        {
            foreach (var sp in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Shield))
            {
                var st = (DataPartTypeIGC.Shield)sp.Type;
                if (st.MaxStrength <= 0f) continue;
                float dtmShield = Dtm(core, m.DamageType, st.DefenseType);
                if (dtmShield <= 0f) continue;
                float absorbed = m.Power * dtmShield / st.MaxStrength;
                if (absorbed < 0.2f || absorbed > 0.45f) continue;
                foreach (var hull in core.HullTypes.Values.OrderBy(h => h.HitPoints))
                {
                    float dtmHull = Dtm(core, m.DamageType, hull.DefenseType);
                    if (dtmHull > 0f && m.Power * dtmHull < hull.HitPoints)
                        return (m, sp, st, absorbed, hull, dtmHull);
                }
            }
        }
        throw new InvalidOperationException("no mine-shield-hull combo in representative core");
    });

    // ── domain harness (a started mission with one sector + counting site) ──

    private sealed class CountingSite : IIgcSite
    {
        public int MineDestroys { get; private set; }
        public void MineDestroyedEvent(Mine m) => MineDestroys++;
    }

    private static MissionMap SomeMap()
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count > 0) return map;
        }
        throw new InvalidOperationException("no non-empty map found in corpus");
    }

    private static (Mission Mission, Sector Sector, CountingSite Site) Domain()
    {
        var core = RepresentativeCore();
        var site = new CountingSite();
        var mission = Mission.Assemble(core, SomeMap(),
            [new SideSpec(0, "Team", core.Civilizations.Values.OrderBy(c => c.CivilizationId).First().CivilizationId)], site);
        mission.Stage = MissionStage.Started;
        return (mission, mission.Sectors[0], site);
    }

    private static Ship PlaceShip(Mission mission, Sector sector, DataHullTypeIGC hull, Vector3 pos, short side, ref short id)
    {
        var ship = new Ship(id++, hull, pos, Vector3.Zero, Orientation.Identity);
        if (side != 0) ship.SideId = side;
        mission.PlaceShip(ship, sector);
        return ship;
    }

    // ── live loopback harness (verbatim from MineCombatTests) ───────────────

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Explosion Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 944u, DwCookie = 0x944u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 88100000000000000ul, DrmHash = "explosion",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private sealed class Seated : IDisposable
    {
        public Srv Server = null!;
        public Ship Shooter = null!, Enemy = null!, Observer = null!;
        public FltBot ShooterBot = null!, EnemyBot = null!, ObserverBot = null!;
        public StaticCore Core = null!;
        public void Dispose()
        {
            foreach (var b in new[] { ShooterBot, EnemyBot, ObserverBot }) b?.Dispose();
            Server?.Dispose();
        }
    }

    private static Seated SeatThree(float gap, DataHullTypeIGC hull)
    {
        var seat = new Seated();
        var core = RepresentativeCore();
        seat.Core = core;
        seat.Server = new Srv();
        int placed = 0;
        seat.Server.ShipSpawner = (_, mission, id) =>
        {
            var sector = mission.Sectors[0];
            var ship = placed switch
            {
                0 => new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity),
                1 => new Ship(id, hull, new Vector3(0f, 0f, -gap), Vector3.Zero, Orientation.Identity),
                _ => new Ship(id, hull, new Vector3(0f, 860f, 940f), Vector3.Zero, Orientation.Identity),
            };
            if (mission.Sides.Count > 0)
                ship.SideId = mission.Sides[placed % mission.Sides.Count].Id;   // round-robin A,B,A
            mission.PlaceShip(ship, sector);
            placed++;
            return (ship, sector);
        };
        seat.Server.Boot(Config());
        foreach (var __m in seat.Server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable mission; bypass ready-up
        var port = seat.Server.BoundPort;

        var bots = new[] { new FltBot(), new FltBot(), new FltBot() };
        string[] names = { "p96_shooter", "p96_enemy", "p96_observer" };
        try
        {
            for (int i = 0; i < bots.Length; i++) { bots[i].Connect(port, names[i]); bots[i].SendLogonRequest(Request(names[i])); }
            PumpUntil(seat.Server, () => bots.All(b => b.ShipAssignment is not null), 4000);
            Assert.True(bots.All(b => b.ShipAssignment is not null), "all three bots must be assigned a ship");

            foreach (var bot in bots)
            {
                var ship = seat.Server.Ships.First(s => s.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Ship;
                if (ship.Position.Length() < 1f) { seat.Shooter = ship; seat.ShooterBot = bot; }
                else if (MathF.Abs(ship.Position.Z + gap) < 2f) { seat.Enemy = ship; seat.EnemyBot = bot; }
                else { seat.Observer = ship; seat.ObserverBot = bot; }
            }
            Assert.NotNull(seat.Shooter); Assert.NotNull(seat.Enemy); Assert.NotNull(seat.Observer);
            Assert.True(seat.Shooter.SideId != seat.Enemy.SideId, "enemy must be cross-side to the shooter");
            Assert.True(seat.Shooter.SideId == seat.Observer.SideId, "observer must be same-side to the shooter");
            seat.Shooter.Velocity = Vector3.Zero;
            seat.Enemy.Velocity = Vector3.Zero;
            seat.Observer.Velocity = Vector3.Zero;
            return seat;
        }
        catch
        {
            foreach (var b in bots) b.Dispose();
            seat.Server.Dispose();
            throw;
        }
    }

    // ══ LIVE: two real deployed minefields, no blast cascade (P452) ══

    /// <summary>⭐ Real loopback server, two real deployed armed minefields close
    /// enough that the OLD P96 semantics would have let one detonation blast
    /// reach the other. Under P452 (mines are persistent attrition fields) the
    /// cross-side moving ship crossing field A is chewed — field A wears and
    /// persists — while the neighbouring field B stays completely untouched (a
    /// mine never blasts, so there is no cascade).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Two_armed_mines_do_not_blast_cascade_a_moving_cross_side_ship_wears_only_the_field_it_crosses()
    {
        Require.Artwork();
        var (mineType, hull, _) = CrossHit.Value;
        using var seat = SeatThree(100f, hull);
        var shooter = seat.Shooter;
        var enemy = seat.Enemy;
        var observer = seat.Observer;

        // Observer must already hold the shooter's EXPORT for the destroy
        // broadcast to reach it (P94 gate).
        PumpUntil(seat.Server, () => seat.ObserverBot.Exports.Any(e => e.ShipId == shooter.Id), 8000);
        Assert.Contains(seat.ObserverBot.Exports, e => e.ShipId == shooter.Id);

        var mission = shooter.Mission!;
        double now0 = mission.LastUpdate;

        // P452 conversion of the old P96 blast-cascade live test: mines are now
        // persistent attrition fields, not bombs, so a mine's wear NEVER reaches a
        // neighbouring mine. Mine A at the origin, mine B 70 units behind it; both
        // armed. The cross-side ship is driven through A only; B must stay untouched.
        var a = shooter.DeployMine(mineType, now0);
        shooter.Position = new Vector3(0f, 5210f, 945f);   // move the launcher clear of its own fields
        var b = shooter.DeployMine(mineType, now0);
        b.Position = new Vector3(0f, 0f, -70f);            // (relocated; a mine is static)
        short aId = a.Id, bId = b.Id;
        Assert.Contains(a, a.OwnerSector.Models);
        Assert.Contains(b, b.OwnerSector.Models);

        // Cross-side enemy parked inside A's field and MOVING (+y — away from the
        // launcher and B, so no ship-ship muddle). Same-side observer far clear
        // (friendliness is proven elsewhere; keeps the maths clean).
        observer.Position = new Vector3(0f, 5110f, 5170f);
        observer.Velocity = Vector3.Zero;
        enemy.Position = new Vector3(0f, 0f, 50f);         // inside A (radius ~100+shipRad), 120 from B
        enemy.Velocity = new Vector3(0f, 80f, 0f);         // moving — eligible for attrition
        float enemyStart = enemy.Fraction, fracA0 = a.Fraction, fracB0 = b.Fraction;

        // Arm fully; A chews the moving cross-side enemy while B (the neighbour)
        // stays untouched — the P452 inversion of the old blast-cascade.
        PumpUntil(seat.Server, () => mission.LastUpdate >= now0 + Mine.ArmTimeSeconds + 0.3, 800);
        PumpUntil(seat.Server, () => enemy.Fraction < enemyStart - 0.002f, 600);
        Assert.True(enemy.Fraction < enemyStart, "moving cross-side ship must take attrition from field A");

        // A persists (not consumed) but is worn; B is NOT reduced (no blast cascade).
        Assert.Contains(a, a.OwnerSector.Models);
        Assert.True(a.Fraction < fracA0, "field A must be worn by the crossing");
        Assert.Equal(fracB0, b.Fraction, 3);
        Assert.Contains(b, b.OwnerSector.Models);
        Assert.DoesNotContain(seat.ObserverBot.MineDestroyeds, d => d.MineId == aId);
        Assert.DoesNotContain(seat.ObserverBot.MineDestroyeds, d => d.MineId == bId);

        Console.WriteLine($"P452-2MINE: cross-side mover {enemyStart:F2}→{enemy.Fraction:F3}; " +
                          $"field A {fracA0:F2}→{a.Fraction:F3} (persists); field B {fracB0:F2}→{b.Fraction:F3} (untouched, no blast cascade)");
    }

    // ══ DOMAIN: precise falloff — center vs edge, two different numbers ══

    /// <summary>⭐ Exact squared-distance falloff, asserted as computed
    /// numbers: a ship overlapping the blast centre (surface distance
    /// <c>d ≤ 0</c>) takes the full <c>Amount</c>, a ship whose surface sits at
    /// <c>d = ½·radius</c> takes <c>Amount·(½)²</c> — two distinct, precisely
    /// predicted fractions through the shared <see cref="Ship.ReceiveDamage"/>
    /// path. Confirms the <c>f = 1 − d/radius; amount *= f·f</c> formula
    /// (<c>clusterIGC.cpp:424-429</c>) is real, not a constant.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Falloff_is_real_a_ship_at_the_blast_edge_takes_less_than_one_at_the_centre()
    {
        Require.Artwork();
        var (mineType, hull, _) = CrossHit.Value;
        var (mission, sector, _) = Domain();
        float dtm = Dtm(RepresentativeCore(), mineType.DamageType, hull.DefenseType);

        short id = 951;
        // Blast centred at the origin with the mine's Power/Radius.
        var explosion = new Explosion(mineType.DamageType, mineType.Power, mineType.MineRadius, mission.LastUpdate, Vector3.Zero, Launcher: null);

        // Centre ship: overlaps the blast centre → surface distance d ≤ 0 → full.
        var centre = PlaceShip(mission, sector, hull, Vector3.Zero, side: 1, ref id);
        // Edge ship: its surface sits at d = ½·Radius → f = ½ → amount × ¼.
        float shipRad = 0.5f * hull.Length;
        float surfDist = 0.5f * mineType.MineRadius;
        float edgePosZ = surfDist + shipRad;
        var edge = PlaceShip(mission, sector, hull, new Vector3(0f, 0f, edgePosZ), side: 2, ref id);

        sector.ResolveExplosion(explosion, mission.LastUpdate);

        float centreDrop = mineType.Power * dtm / hull.HitPoints;
        float edgeDrop = (mineType.Power * 0.25f) * dtm / hull.HitPoints;
        Assert.Equal(1f - centreDrop, centre.Fraction, 4);
        Assert.Equal(1f - edgeDrop, edge.Fraction, 4);
        Assert.True(centre.Fraction < edge.Fraction, "centre ship must take MORE damage than the edge ship");
        Assert.NotEqual(1f - centreDrop, 1f - edgeDrop);

        Console.WriteLine($"P96-FALLOFF: centre {centre.Fraction:F4} (Δ{centreDrop:F4}), edge {edge.Fraction:F4} (Δ{edgeDrop:F4}) — distinct falloff numbers");
    }

    // ══ DOMAIN: shield absorption via the reused ReceiveDamage path ══

    /// <summary>⭐ A shielded ship in blast range absorbs through the <em>same</em>
    /// <see cref="Ship.ReceiveDamage"/> path as any weapon/mine hit — the
    /// CombatShieldTests "SmallHit" recipe (fresh full shield absorbs the
    /// computed <c>Power·dtmShield/MaxStrength</c>, hull untouched) applied to a
    /// blast centred on the ship. Proves the explosion path shares, not
    /// reimplements, the damage pipeline.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Shielded_ship_in_blast_absorbs_like_a_weapon_hit()
    {
        Require.Artwork();
        var (mineType, shieldPart, shieldType, expectedAbsorbed, hull, _) = ShieldCombo.Value;
        var (mission, sector, _) = Domain();
        short id = 961;

        var shieldTarget = PlaceShip(mission, sector, hull, Vector3.Zero, side: 1, ref id);
        shieldTarget.MountPart(shieldPart, amount: 1, mountId: 0, fractionMounted: 255);   // armed full
        Assert.Equal(1.0f, shieldTarget.ShieldFraction, 3);
        float hullStart = shieldTarget.Fraction;

        var explosion = new Explosion(mineType.DamageType, mineType.Power, mineType.MineRadius, mission.LastUpdate, Vector3.Zero, Launcher: null);
        sector.ResolveExplosion(explosion, mission.LastUpdate);

        Assert.Equal(1f - expectedAbsorbed, shieldTarget.ShieldFraction, 3);
        Assert.Equal(hullStart, shieldTarget.Fraction, 3);                  // hull untouched
        Assert.False(shieldTarget.Dead);
        Console.WriteLine($"P96-SHIELD: absorbed {expectedAbsorbed:F3} (shield 1.0→{shieldTarget.ShieldFraction:F3}, hull unchanged)");
    }

    // ══ DOMAIN: friendly-fire gate for explosion-sourced damage ══

    /// <summary>⭐ The existing friendly-fire gate holds for explosion-sourced
    /// damage: with a <c>Ship</c> launcher of side A in blast range, a same-side
    /// target (side A) takes nothing while a cross-side target (side B) is hit
    /// with falloff — no new gate invented, the same one
    /// <see cref="Sector.ResolveCombatCollisions"/>/<see cref="Sector.ResolveMineDetonations"/>
    /// enforce.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Explosion_respects_the_existing_friendly_fire_gate()
    {
        Require.Artwork();
        var (mineType, hull, _) = CrossHit.Value;
        var (mission, sector, _) = Domain();
        short id = 971;

        var launcher = PlaceShip(mission, sector, hull, new Vector3(0f, 0f, 700f), side: 1, ref id);   // owner, clear of blast
        var friendly = PlaceShip(mission, sector, hull, new Vector3(0f, 0f, 30f), side: 1, ref id);    // same side, in blast
        var hostile = PlaceShip(mission, sector, hull, new Vector3(0f, 0f, 30f), side: 2, ref id);     // cross side, in blast

        float shipRad = 0.5f * hull.Length;
        float dHostile = 30f - shipRad;
        float fHostile = 1f - dHostile / mineType.MineRadius;
        float hostileDrop = (mineType.Power * fHostile * fHostile) * Dtm(RepresentativeCore(), mineType.DamageType, hull.DefenseType) / hull.HitPoints;

        var explosion = new Explosion(mineType.DamageType, mineType.Power, mineType.MineRadius, mission.LastUpdate, Vector3.Zero, Launcher: launcher);
        sector.ResolveExplosion(explosion, mission.LastUpdate);

        Assert.Equal(1f, friendly.Fraction, 4);                 // same-side untouched
        Assert.Equal(1f - hostileDrop, hostile.Fraction, 4);    // cross-side hit with falloff
        Console.WriteLine($"P96-FRIENDLY: same-side {friendly.Fraction:F4} untouched; cross-side {hostile.Fraction:F4} (Δ{hostileDrop:F4})");
    }

    // ══ DOMAIN: accumulated blasts destroying a mine below 0.25 ══

    /// <summary>⭐ Accumulated blasts push a mine below the <c>0.25f</c>
    /// threshold and it is actually destroyed: removed from the sector and
    /// <see cref="IIgcSite.MineDestroyedEvent"/> raised exactly once. Exercises
    /// <see cref="Sector.ResolveExplosion"/> repeatedly (the integration path),
    /// with the exact exponential-decay fraction asserted after each blast.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Accumulated_blasts_push_a_mine_below_threshold_and_it_is_destroyed_once()
    {
        Require.Artwork();
        var (mineType, hull, _) = CrossHit.Value;
        var (mission, sector, site) = Domain();
        float endurance = mineType.Endurance;
        short id = 981;

        var holder = PlaceShip(mission, sector, hull, new Vector3(0f, 0f, 990f), side: 1, ref id);  // launcher, clear of blast
        var mine = new Mine(id++, sector, mineType, Vector3.Zero, mission.LastUpdate, holder);
        sector.Add(mine);
        Assert.Contains(mine, sector.Models);

        // Blast centred on the mine → surface distance ≤ 0 → full Amount every time.
        var explosion = new Explosion(mineType.DamageType, mineType.Power, mineType.MineRadius, mission.LastUpdate, Vector3.Zero, Launcher: holder);

        // Each blast: fraction *= exp(−Power/endurance) = exp(−400/2000) = 0.818731.
        float perBlast = MathF.Exp(-mineType.Power / endurance);
        int destroysSeen = 0;
        for (int k = 1; k <= 8 && sector.Models.Contains(mine); k++)
        {
            sector.ResolveExplosion(explosion, mission.LastUpdate);
            float expected = MathF.Pow(perBlast, k);
            if (expected >= 0.25f)
            {
                Assert.True(sector.Models.Contains(mine), $"mine still present after {k} blasts");
                Assert.Equal(expected, mine.Fraction, 4);
            }
            else
            {
                // This is the blast that pushed it below threshold → destroyed.
                destroysSeen++;
            }
        }

        Assert.Equal(1, destroysSeen);                                  // destroyed exactly once
        Assert.Equal(1, site.MineDestroys);                             // one MineDestroyedEvent, exactly once
        Assert.DoesNotContain(mine, sector.Models);                     // removed from the sector
        Assert.True(mine.Fraction < 0.25f, $"fraction {mine.Fraction} must be below 0.25");
        Console.WriteLine($"P96-ACCUMULATE: {mine.Id} destroyed after accumulated blasts (fraction {mine.Fraction:F4}, event exactly once)");
    }
}
