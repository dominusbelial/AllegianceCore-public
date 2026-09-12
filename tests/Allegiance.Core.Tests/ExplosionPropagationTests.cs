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
/// P96 — the real explosion/blast-radius system and mine destructibility. A
/// generic <see cref="Explosion"/> type
/// (<see cref="Sector.ResolveExplosion"/>) distributes squared-distance
/// falloff damage to every ship and mine in blast range (native
/// <c>clusterIGC.cpp:362-467</c>), and a mine inside another's blast takes
/// <see cref="Mine.ReduceStrength"/> (native <c>mineigc.h:170-181</c>) which
/// can destroy it below the <c>0.25f</c> threshold. The prior P95
/// single-target mine hit now triggers a real <see cref="Explosion"/> so the
/// blast propagates to other ships <em>and mines</em> — closing mine
/// destructibility.
/// </summary>
/// <remarks>
/// Proofs here are three kinds, mirroring <see cref="MineCombatTests"/>' own
/// split: a <b>live loopback</b> 3-bot / 2-side test (two real deployed mines,
/// one cross-side ship in the blast, one same-side observer — the real
/// detonation blast reaches the second mine and the ship), and several
/// <b>domain</b> tests that call <see cref="Sector.ResolveExplosion"/> directly
/// with controlled geometry for exact falloff / shield / friendly-fire /
/// accumulated-destruction numbers. Everything is data-derived from
/// <c>PCore001.igc</c>, reusing the exact combo-selection recipes the combat
/// and mine tests already use (no hardcoded damage values).
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

    // ══ LIVE: real mine detonation → blast reaches a second mine + a ship ══

    /// <summary>⭐ Real loopback server, two real deployed mines close enough
    /// that one's detonation blast reaches the other and a cross-side ship.
    /// The mine that detonates is consumed; the second mine's fraction drops by
    /// the exact computed falloff-attenuated amount (a real
    /// <see cref="Mine.ReduceStrength"/> effect, not just the trigger's own
    /// removal); the ship in the blast takes the exact falloff-adjusted damage.
    /// The same-side observer parked inside the blast takes nothing (existing
    /// friendly-fire gate).</summary>
    [Fact]
    public void Live_mine_detonation_blast_reduces_a_second_mine_and_damages_ships_in_range()
    {
        var (mineType, hull, _) = CrossHit.Value;
        float endurance = mineType.Endurance;
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

        // Mine A at the shooter's origin; mine B 70 units behind it (well
        // within A's blast radius so A's detonation reaches B).
        var a = shooter.DeployMine(mineType, now0);
        shooter.Position = new Vector3(0f, 0f, -70f);
        var b = shooter.DeployMine(mineType, now0);
        short aId = a.Id, bId = b.Id;
        Assert.Contains(a, a.OwnerSector.Models);
        Assert.Contains(b, b.OwnerSector.Models);

        // Geometry: A at origin, B at (0,0,-70), cross-side enemy at (0,0,50)
        // (surface distance d = 50 − ½·hullLen > 0 → real falloff, not the
        // overlap degeneracy), same-side observer at (0,0,40) (also in blast).
        // distance from enemy to B = 120 > MineRadius(100)+shipLen ⇒ B never
        // detonates independently; only A fires.
        float shipRad = 0.5f * hull.Length;
        enemy.Position = new Vector3(0f, 0f, 50f);
        observer.Position = new Vector3(0f, 0f, 40f);
        float enemyStart = enemy.Fraction, obsStart = observer.Fraction;

        // Predicted blast math (native clusterIGC.cpp:420-429):
        //   ship d = |A−ship| − shipRad ; amount = Power·f² when d>0.
        float dEnemy = 50f - shipRad;
        Assert.True(dEnemy > 0f, $"want a real falloff (d>0), got d={dEnemy}");
        float fEnemy = 1f - dEnemy / mineType.MineRadius;
        float enemyAmount = mineType.Power * fEnemy * fEnemy;
        float dtm = Dtm(seat.Core, mineType.DamageType, hull.DefenseType);
        float enemyDrop = enemyAmount * dtm / hull.HitPoints;   // fraction delta

        // Second mine B: point-like (see ResolveExplosion), so d_B = |A−B| = 70;
        // same falloff; ReduceStrength → fraction *= exp(−amount/endurance).
        float dB = 70f;
        float fB = 1f - dB / mineType.MineRadius;
        float bAmount = mineType.Power * fB * fB;
        float expectedBFraction = MathF.Exp(-bAmount / endurance);
        Assert.True(expectedBFraction < 1f, "second mine's predicted fraction must be a real (measurable) reduction");

        // Arm fully, then the cross-side enemy triggers mine A. On the first
        // tick that fires, A consumes itself, its blast resolves immediately,
        // damaging the enemy and reducing B.
        PumpUntil(seat.Server, () => enemy.Fraction < enemyStart - 0.005f, 2000);
        Assert.True(enemy.Fraction < enemyStart, "armed mine A must detonate on the cross-side ship");

        // (1) ship hull fraction drops by the EXACT falloff-adjusted amount.
        Assert.Equal(1f - enemyDrop, enemy.Fraction, 3);

        // (2) the SECOND mine's fraction measurably decreased (real ReduceStrength).
        Assert.True(b.Fraction < 1f, "second mine must lose strength from the first mine's blast");
        Assert.Equal(expectedBFraction, b.Fraction, 3);

        // The detonated mine A is gone; B is still present (not yet destroyed).
        Assert.DoesNotContain(a, a.OwnerSector.Models);
        Assert.Contains(b, b.OwnerSector.Models);

        // (5) friendly-fire gate: the same-side observer inside the same blast
        // took nothing.
        Assert.Equal(obsStart, observer.Fraction, 3);

        // A's destroy reaches the same-side peer; B's existence is intact.
        PumpUntil(seat.Server, () => seat.ObserverBot.MineDestroyeds.Any(d => d.MineId == aId), 4000);
        Assert.Contains(seat.ObserverBot.MineDestroyeds, d => d.MineId == aId);
        Assert.DoesNotContain(seat.ObserverBot.MineDestroyeds, d => d.MineId == bId);

        Console.WriteLine($"P96-LIVE: enemy hull {enemyStart:F2}→{enemy.Fraction:F3} (expect {1f - enemyDrop:F3}, d={dEnemy:F1}); " +
                          $"mineB {1.0f:F2}→{b.Fraction:F3} (expect {expectedBFraction:F3}, d_B={dB:F1}); " +
                          $"observer {obsStart:F2} unharmed (friendly gate)");
    }

    // ══ DOMAIN: precise falloff — center vs edge, two different numbers ══

    /// <summary>⭐ Exact squared-distance falloff, asserted as computed
    /// numbers: a ship overlapping the blast centre (surface distance
    /// <c>d ≤ 0</c>) takes the full <c>Amount</c>, a ship whose surface sits at
    /// <c>d = ½·radius</c> takes <c>Amount·(½)²</c> — two distinct, precisely
    /// predicted fractions through the shared <see cref="Ship.ReceiveDamage"/>
    /// path. Confirms the <c>f = 1 − d/radius; amount *= f·f</c> formula
    /// (<c>clusterIGC.cpp:424-429</c>) is real, not a constant.</summary>
    [Fact]
    public void Falloff_is_real_a_ship_at_the_blast_edge_takes_less_than_one_at_the_centre()
    {
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
    [Fact]
    public void Shielded_ship_in_blast_absorbs_like_a_weapon_hit()
    {
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
    [Fact]
    public void Explosion_respects_the_existing_friendly_fire_gate()
    {
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
    [Fact]
    public void Accumulated_blasts_push_a_mine_below_threshold_and_it_is_destroyed_once()
    {
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
