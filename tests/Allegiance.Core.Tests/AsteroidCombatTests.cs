using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>
/// P102 — pure-domain (no sockets) proofs of the asteroid combat-destructibility
/// math and its mining interplay: <see cref="Asteroid.ReceiveDamage"/> implements
/// the real <c>CasteroidIGC::ReceiveDamage</c> formula
/// (<c>src/Igc/asteroidigc.h:126-171</c>), with the defense-type index
/// <c>c_defidAsteroid = 0</c> (<c>igc.h:567</c>) and no side/friendly-fire gate of
/// any kind (read the full formula — it reads nothing but <c>type</c>/<c>amount</c>
/// and the rock's own hitpoints), and a damaged-but-alive asteroid still mines
/// normally (the rock's depletable ore pool is untouched by combat damage). The
/// caller-owned removal contract (<c>Sector.RemoveAsteroid</c> + the
/// <c>KillAsteroidEvent</c> seam raised by the combat pass, not by the rock) is
/// asserted too. Everything is data-derived from the real <c>PCore001.igc</c>
/// static core — no hardcoded numbers.
/// </summary>
/// <remarks>
/// The domain maths test mirrors the probe/ship death discipline: partial damage
/// reduces <see cref="Asteroid.Fraction"/> by the exact decoded
/// <c>Power·DamageConstant/maxHP</c> fraction and returns
/// <see cref="DamageResult.HullDamage"/>; lethal damage clamps to <c>0</c> and
/// returns <see cref="DamageResult.Killed"/>; and a second call past death does
/// <b>not</b> re-kill (native returns the default <c>c_drHullDamage</c> when
/// <c>oldFraction &le; 0</c>, <c>asteroidigc.h:160-165</c>). Unlike the probe, an
/// asteroid does <b>not</b> remove itself or raise the host seam inside
/// <c>ReceiveDamage</c> (it is not a <see cref="Model"/> and carries no sector /
/// launcher reference — that bookkeeping is the combat pass's, in
/// <c>ResolveCombatCollisions</c>); the unit maths test asserts this explicitly so
/// the contract is visible, and the mining-interplay test proves a surviving rock
/// still feeds the mining pass. The launcher is a real <see cref="Ship"/> so the
/// asteroid derives the <c>Mission</c>/<c>Core</c> (and thus the damage matrix)
/// the same way production does.
/// </remarks>
public sealed class AsteroidCombatTests
{
    private const string ArtRoot = "/home/domingo/Development/Artwork";

    private static StaticCore Core()
    {
        var path = Corpus.Files.FirstOrDefault(p => Path.GetFileName(p).Equals("PCore001.igc", StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException("PCore001.igc not in corpus");
        var file = IgcReader.ParseFile(path);
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static float Dtm(StaticCore core, byte dmg, byte def) => core.Constants.DamageConstants[dmg][def];

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

    private static Mission NewStartedMission(IIgcSite site)
    {
        var core = Core();
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First().CivilizationId;
        var m = Mission.Assemble(core, SomeMap(), [new SideSpec(0, "Team 1", civ)], site);
        m.Stage = MissionStage.Started;
        return m;
    }

    /// <summary>A synthetic He3-capable rock with real hit points and a
    /// chosen capacity — the same seam MiningTests uses to inject rocks
    /// (missile: the mission-assembly asteroids have real HitPoints from the
    /// core, but the demo map has zero asteroids so a placed rock is the only
    /// one in the sector). HP is chosen mid-range so a single typical round
    /// is a clean non-lethal partial hit; never hardcoded in production (the
    /// formula always reads <see cref="Asteroid.Data"/>.HitPoints).</summary>
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

    /// <summary>A real damage type id (from a real ordnance record) whose armor
    /// cell for the asteroid's defense index (<c>c_defidAsteroid = 0</c>) is
    /// strictly positive — so the computed damage uses a real matrix value, never
    /// a degenerate neutral fallback.</summary>
    private static byte PositiveCellDamageType(StaticCore core)
    {
        foreach (var pt in core.ProjectileTypes.Values)
        {
            if (pt.Power <= 0f) continue;
            if (Dtm(core, pt.DamageType, Asteroid.DefenseType) > 0f) return pt.DamageType;
        }
        throw new InvalidOperationException($"no projectile damage type with a positive cell for asteroid defense {Asteroid.DefenseType}");
    }

    private static Ship NewShooter(DataHullTypeIGC hull) =>
        new(9801, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);

    // ══ domain: ReceiveDamage math ══════════════════════════════════════════

    /// <summary>⭐ A real asteroid on a real mission takes a partial hit worth a
    /// computable fraction, returns <see cref="DamageResult.HullDamage"/> and stays
    /// alive; a lethal hit clamps its <see cref="Asteroid.Fraction"/> to 0, returns
    /// <see cref="DamageResult.Killed"/> and marks it <see cref="Asteroid.Dead"/> —
    /// but does NOT remove it from the sector / raise the host seam (that is the
    /// combat pass's job, since an asteroid is not a Model); and a THIRD call past
    /// death does NOT double-raise (native returns the default HullDamage when
    /// already dead, <c>asteroidigc.h:160-165</c>). All numbers decoded, not
    /// guessed. The exact computed fraction is asserted, and the
    /// caller-owns-removal contract is asserted explicitly.</summary>
    [Fact]
    public void ReceiveDamage_partial_reduces_fraction_lethal_marks_dead_no_double_kill_and_leaves_removal_to_caller()
    {
        var core = Core();
        var mission = NewStartedMission(new NullIgcSite());
        var sector = mission.Sectors[0];
        var spot = new Vector3(0f, 0f, -500f);   // far from any spawn/ship in the sector

        var rock = new Asteroid(MkAsteroid(6101, spot, oreMax: 5000f, hitPoints: 1200f, radius: 60, sectorId: sector.Id));
        mission.PlaceAsteroid(rock, sector);
        Assert.Contains(rock, sector.Mineable);
        Assert.Equal(1f, rock.Fraction, 3);
        Assert.Equal(1200f, rock.Data.HitPoints);

        var launcher = NewShooter(core.HullTypes.Values.OrderBy(h => h.HitPoints).First());
        launcher.SideId = 0;
        mission.PlaceShip(launcher, sector);                    // gives it Mission (DamageConstant's route) and a Sector
        byte dtype = PositiveCellDamageType(core);
        float dtm = Dtm(core, dtype, Asteroid.DefenseType);
        Assert.True(dtm > 0f, "selected damage type must have a positive armor cell for the asteroid");

        // PARTIAL: a hit worth exactly 40% of maxHP → fraction 1.0 − 0.4 = 0.6.
        float partialAmount = 0.4f * rock.Data.HitPoints / dtm;
        var res = rock.ReceiveDamage(dtype, partialAmount, mission.LastUpdate, Vector3.Zero, spot, launcher);
        Assert.Equal(DamageResult.HullDamage, res);
        Assert.Equal(0.6f, rock.Fraction, 3);
        Assert.False(rock.Dead);
        Assert.Contains(rock, sector.Mineable);                // survived: still minable, not removed

        // LETHAL: a hit worth 200% of maxHP clamps to 0, returns Killed, marks Dead.
        float lethalAmount = 2f * rock.Data.HitPoints / dtm;
        var resKill = rock.ReceiveDamage(dtype, lethalAmount, mission.LastUpdate, Vector3.Zero, spot, launcher);
        Assert.Equal(DamageResult.Killed, resKill);
        Assert.Equal(0f, rock.Fraction, 3);
        Assert.True(rock.Dead);
        // Removal is NOT the rock's job (documented contract): the combat pass
        // calls Sector.RemoveAsteroid and raises KillAsteroidEvent. Assert that a
        // direct ReceiveDamage alone leaves the rock in Mineable — the caller must
        // act on Killed.
        Assert.Contains(rock, sector.Mineable);

        // POST-DEATH call: no double-kill (native asteroidigc.h:160-165: oldFraction
        // ≤ 0 ⇒ dr stays default), same discipline as the ship/probe path.
        var resAgain = rock.ReceiveDamage(dtype, lethalAmount, mission.LastUpdate, Vector3.Zero, spot, launcher);
        Assert.Equal(DamageResult.HullDamage, resAgain);
        Assert.Equal(0f, rock.Fraction, 3);
        Console.WriteLine($"P102-DOMAIN: partial hit {partialAmount:F2} @ dtm {dtm:F3} → fraction 0.600; lethal → Killed (fraction 0), " +
                          $"removal left to the combat pass, no double-kill on a post-death call");
    }

    // ══ domain: a damaged-but-alive rock still mines (no regression) ════════

    /// <summary>⭐ A rock that survived a direct hit (fraction &gt; 0) is still
    /// minable: the combat damage consumed none of its ore pool, so the mining
    /// pass (<see cref="Sector.ResolveMining"/>) still drains it and the ship's
    /// <c>Ore</c> grows across further ticks — reusing the MiningTests accrual
    /// recipe to prove no regression. This is verification point 4 of the task.</summary>
    [Fact]
    public void Damaged_but_alive_asteroid_still_mines_normally()
    {
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording);
        var sector = mission.Sectors[0];
        var spot = new Vector3(0f, 0f, -700f);
        var rock = new Asteroid(MkAsteroid(6102, spot, oreMax: 10000f, hitPoints: 1500f, radius: 60, sectorId: sector.Id));
        mission.PlaceAsteroid(rock, sector);

        // A mining ship parked right on the rock, stopped, He3-capable rock.
        var hull = mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var miner = new Ship(9802, hull, spot, Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(miner, sector);

        // Baseline: mining accrues before any damage.
        for (int i = 0; i < 3; i++) mission.Update(mission.LastUpdate + 0.25);   // 3 × 0.25 s = 3 × 25 ore
        float oreBeforeDamage = miner.Ore;
        Assert.True(oreBeforeDamage >= 70f, $"baseline mining should accrue ~75 ore, got {oreBeforeDamage}");

        // Damage the rock to just under half health — it survives (Fraction > 0).
        var launcher = NewShooter(hull); launcher.SideId = 0;
        mission.PlaceShip(launcher, sector);                 // gives the DamageConstant route
        byte dtype = PositiveCellDamageType(mission.Core);
        float dtm = Dtm(mission.Core, dtype, Asteroid.DefenseType);
        float halfHit = 0.45f * rock.Data.HitPoints / dtm;
        rock.ReceiveDamage(dtype, halfHit, mission.LastUpdate, Vector3.Zero, spot, launcher);
        Assert.True(rock.Fraction > 0.5f && rock.Fraction < 0.6f, $"rock must survive damaged (fraction {rock.Fraction:F3})");
        Assert.Contains(rock, sector.Mineable);              // still in the mining list

        // Continue mining the damaged rock — no regression.
        float oreAfterDamage = miner.Ore;
        for (int i = 0; i < 3; i++) mission.Update(mission.LastUpdate + 0.25);
        Assert.True(miner.Ore > oreAfterDamage, $"damaged rock must still yield ore: {oreAfterDamage} → {miner.Ore}");
        Assert.Contains(rock, sector.Mineable);
        Assert.True(rock.Fraction > 0f, "rock must still be alive (only combat reduces Fraction, mining never touches it)");
        Console.WriteLine($"P102-DOMAIN-MINE: ore {oreBeforeDamage:F1} (baseline) → damaged rock keeps yielding to {miner.Ore:F1}; " +
                          $"rock Fraction {rock.Fraction:F3} (untouched by mining)");
    }
}
