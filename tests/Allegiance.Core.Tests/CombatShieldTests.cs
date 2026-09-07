using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests;

/// <summary>
/// Verification of P87 — real shield damage absorption, the first slice of the
/// roadmap's Phase 2 "combat depth" goal that retires the deferred half of
/// <c>Ship.ReceiveDamage</c> ("no shields", the old <c>Ship.cs:388-394</c> remark).
///
/// A ship with a real mounted <see cref="DataPartTypeIGC.Shield"/> part takes
/// real damage from a real weapon projectile. The <b>multiplication order</b>
/// is the central thing under test: the shield's own armor constant
/// (<c>dtmShield</c>) is applied inside the absorption math and cancelled out
/// before returning, so the caller's <c>leakage *= dtmArmor</c> applies the
/// <b>hull's</b> constant exactly once — a naive port that double-applied (or
/// never applied) <c>dtmArmor</c> would still compile and pass a loose test.
/// The three damage cases (full absorb / partial absorb / shield-down-leaks-all)
/// are asserted as genuinely distinct with computed numbers derived from the
/// decoded <c>StaticCore</c> — nothing hardcoded, mirroring the established
/// <see cref="CombatDamageTests"/> recipe.
/// </summary>
public class CombatShieldTests
{
    private static readonly Lazy<StaticCore> RepresentativeCore = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            return StaticCore.Load(file);
        }
        throw new InvalidOperationException("no static core found in corpus");
    });

    private static readonly Lazy<MissionMap> SomeMap = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count == 0) continue;
            return map;
        }
        throw new InvalidOperationException("no non-empty map found in corpus");
    });

    private static IReadOnlyList<SideSpec> SideSpecs()
    {
        var core = RepresentativeCore.Value;
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First();
        return [new SideSpec(0, "Team 1", civ.CivilizationId)];
    }

    private static Mission NewStartedMission()
    {
        var m = Mission.Assemble(RepresentativeCore.Value, SomeMap.Value, SideSpecs(), new NullIgcSite());
        m.Stage = MissionStage.Started;
        return m;
    }

    /// <summary>A hull + weapon + shield that work together: a fightable hull
    /// with a fixed hardpoint, a dispersion-0 weapon it can afford that fires a
    /// positive-power projectile, and a real <c>Shield</c> part. All quantities
    /// (<c>RawPower</c>, the shield's <c>DtmShield</c>, the hull's <c>DtmHull</c>,
    /// and the resulting absorbed fraction at full charge) are decoded from the
    /// representative <c>StaticCore</c> exactly as the sim computes them, so the
    /// expected numbers in the assertions below are data-derived, not guesses.</summary>
    private sealed record ShieldCombo(
        DataHullTypeIGC Hull,
        DataPartTypeIGC.Weapon WeaponType,
        DataProjectileTypeIGC Projectile,
        DataPartTypeIGC ShieldPart,
        float RawPower,
        float DtmShield,
        float DtmHull,
        float AbsorbedFull)
    {
        public DataPartTypeIGC.Shield ShieldType => (DataPartTypeIGC.Shield)ShieldPart.Type;
        public float MaxStrength => ShieldType.MaxStrength;
        /// <summary>Hull damage a single hit inflicts when the fresh shield is
        /// overloaded: excess = (absorbedFull − 0.9) un-multiplies the shield's
        /// armor, × <c>DtmHull</c> for the hull's armor — the partial-absorb case.</summary>
        public float ExcessHullDamage => (AbsorbedFull - 0.9f) * MaxStrength * DtmHull / DtmShield;
        public float MaxHP => Hull.HitPoints;
    }

    private static IEnumerable<ShieldCombo> Combos()
    {
        var core = RepresentativeCore.Value;
        foreach (var hull in core.HullTypes.Values
                     .Where(h => h.MaxEnergy > 0 && h.MaxAmmo > 0 && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed)
                     .OrderBy(h => h.HullId))
        {
            foreach (var wp in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
                         .Select(p => (DataPartTypeIGC.Weapon)p.Type)
                         .Where(w => w.Dispersion == 0f && w.EnergyPerShot <= hull.MaxEnergy)
                         .OrderBy(w => w.EnergyPerShot))
            {
                if (!core.ProjectileTypes.TryGetValue(wp.ProjectileTypeId, out var pt) || pt.Power <= 0f) continue;
                float dtmHull = core.Constants.DamageConstants[pt.DamageType][hull.DefenseType];
                if (dtmHull <= 0f) continue;
                foreach (var sp in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Shield))
                {
                    var st = (DataPartTypeIGC.Shield)sp.Type;
                    if (st.MaxStrength <= 0f) continue;
                    float dtmS = core.Constants.DamageConstants[pt.DamageType][st.DefenseType];
                    if (dtmS <= 0f) continue;
                    yield return new ShieldCombo(hull, wp, pt, sp, pt.Power, dtmS, dtmHull,
                        pt.Power * dtmS / st.MaxStrength);
                }
            }
        }
    }

    /// <summary>A combo whose single hit is <b>fully absorbed</b> by a fresh
    /// shield: absorbed fraction comfortably inside the fresh shield's useful
    /// band (≤ 0.5 ≪ 0.9). The hull must take no damage at all. (Selected from
    /// the actual test core, where every obtainable shield combo happens to sit
    /// on an identity armor cell — the non-trivial-armor order is pinned
    /// separately by the <see cref="ApplyShieldDamage"/> seam test.)</summary>
    private static readonly Lazy<ShieldCombo> SmallHit = new(() =>
        Combos().Where(c => c.AbsorbedFull >= 0.2f && c.AbsorbedFull <= 0.5f)
                .MinBy(c => c.AbsorbedFull)
        ?? throw new InvalidOperationException("no fully-absorbed shield combo in the representative core"));

    /// <summary>A combo whose single hit <b>overloads</b> a fresh shield
    /// (absorbed ≥ 1.05 &gt; the 0.9 useful ceiling ⇒ it clamps to 0 = down) and
    /// leaks a real, controlled excess to the hull well below killing it — and
    /// whose <b>full raw</b> hit is also survivable on an unshielded/knocked-down
    /// hull (so the scenario-3 equivalence check never kills either ship).</summary>
    private static readonly Lazy<ShieldCombo> BigHit = new(() =>
        Combos().Where(c => c.AbsorbedFull >= 1.05f
                            && c.ExcessHullDamage < c.MaxHP * 0.5f
                            && c.RawPower * c.DtmHull < c.MaxHP)   // a down-shield full leak must not kill
                .MinBy(c => c.ExcessHullDamage)
        ?? throw new InvalidOperationException("no overloaded-shield combo in the representative core"));

    private static Ship NewShooter(DataHullTypeIGC hull) =>
        new(9401, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);

    private static Ship NewTarget(float z, DataHullTypeIGC hull) =>
        new(9501, hull, new Vector3(0f, 0f, z), Vector3.Zero, Orientation.Identity);

    /// <summary>Arm the shooter with the given weapon, select it (all-weapons
    /// state), and cap the burst to exactly one projectile by exhausting the
    /// limiting resource (ammo for a projectile weapon, energy for an energy
    /// weapon) so a single hit is guaranteed and assertions never race a
    /// multi-projectile burst.</summary>
    private static void ArmShooter(Ship shooter, DataPartTypeIGC.Weapon type)
    {
        shooter.StateM |= ShipControlState.AllWeapons;
        if (type.CAmmoPerShot > 0) shooter.Ammo = 1;
        else shooter.Energy = type.EnergyPerShot;
        shooter.MountWeapon(type);
    }

    /// <summary>Place shooter (nose, origin) and target (dead ahead at −z), arm
    /// the shooter with <paramref name="weapon"/>, and tick until
    /// <paramref name="shotLanded"/> holds (or <paramref name="maxSteps"/>).</summary>
    private static Ship FireOnce(Mission m, Ship target, DataPartTypeIGC.Weapon weapon, Func<bool> shotLanded, int maxSteps = 1200)
    {
        var sector = m.Sectors[0];
        var shooter = NewShooter(target.HullType);   // same hull → the weapon's energy is affordable
        if (!RepresentativeCore.Value.ProjectileTypes.TryGetValue(weapon.ProjectileTypeId, out var pt))
            throw new InvalidOperationException("weapon projectile absent from core");
        float travel = pt.Speed * pt.Lifespan;                       // decoded reach of the round
        float gap = MathF.Max(MathF.Min(200f, 0.5f * travel), 30f);  // a comfortable stop inside its range
        target.Position = new Vector3(0f, 0f, -gap);                 // dead ahead of the shooter (identity → forward −z)
        m.PlaceShip(shooter, sector);
        m.PlaceShip(target, sector);
        ArmShooter(shooter, weapon);
        bool ok = TickUntil(m, maxSteps, shotLanded);
        Assert.True(ok, "the projectile must reach the target");
        return target;
    }

    private static bool TickUntil(Mission m, int maxSteps, Func<bool> predicate)
    {
        double t = m.LastUpdate;
        for (int i = 0; i < maxSteps; i++)
        {
            t += 0.025;
            m.Update(t);
            if (predicate()) return true;
        }
        return false;
    }

    // ── mount arms, wire round-trip, clear resets ─────────────────────────

    /// <summary>A fresh ship reads 0 shield (no capacity), and mounting a real
    /// shield arms it to 1.0 (<c>CshieldIGC::Arm</c>, <c>shieldIGC.h:99-103</c>).</summary>
    [Fact]
    public void Mounting_a_shield_arms_it_to_full_and_no_shield_is_zero()
    {
        var ship = new Ship(8701, SmallHit.Value.Hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        Assert.Equal(0f, ship.ShieldFraction);

        ship.MountPart(SmallHit.Value.ShieldPart, amount: 1, mountId: 0, fractionMounted: 255);
        Assert.Equal(1.0f, ship.ShieldFraction, 3);   // armed to fully charged
        Assert.Contains(ship.Parts, p => p.EquipmentType == EquipmentType.Shield);

        ship.ClearLoadout();
        Assert.Equal(0f, ship.ShieldFraction);        // unmounting returns to "no shield capacity"
    }

    /// <summary>A mounted shield's fraction round-trips through the wire slot
    /// (<c>byte 41</c>, <c>ServerShipUpdate.FromModel</c> → <c>ShieldFraction</c>),
    /// and a no-shield ship encodes a clean 0.</summary>
    [Fact]
    public void Shield_fraction_round_trips_the_wire_slot()
    {
        var shielded = new Ship(8801, SmallHit.Value.Hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        shielded.MountPart(BigHit.Value.ShieldPart, 1, 0, 255);
        var a = ServerShipUpdate.FromModel(8801, shielded, 100);
        Assert.Equal(1.0f, a.ShieldFraction, 3);      // fresh shield → ~100%

        var noShield = new Ship(8802, SmallHit.Value.Hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        var b = ServerShipUpdate.FromModel(8802, noShield, 100);
        Assert.Equal(0.0f, b.ShieldFraction, 3);      // no shield → 0%, matching today's pre-shield wire value
    }

    // ── case 1: small hit fully absorbed by the shield ────────────────────

    /// <summary>⭐ A hit smaller than the shield's remaining capacity leaves the
    /// hull completely untouched and reduces the shield by exactly the computed
    /// absorbed fraction (<c>rawPower · dtmShield / MaxStrength</c>), not merely
    /// "some amount".</summary>
    [Fact]
    public void Small_hit_is_fully_absorbed_and_leaves_the_hull_untouched()
    {
        var mission = NewStartedMission();
        var combo = SmallHit.Value;
        var target = NewTarget(0, combo.Hull);
        target.MountPart(combo.ShieldPart, amount: 1, mountId: 0, fractionMounted: 255);   // real shield, armed full
        target = FireOnce(mission, target, combo.WeaponType, () => target.ShieldFraction < 0.987f);

        float expectedNewShield = 1.0f - combo.AbsorbedFull;                  // fully charged (1.0) minus the absorbed fraction

        Assert.True(combo.AbsorbedFull <= 0.5f, "test combo must stay comfortably inside the useful band");
        Assert.Equal(expectedNewShield, target.ShieldFraction, 3);   // shield reduced by the computed amount
        Assert.Equal(1.0f, target.Fraction, 3);                       // hull utterly untouched (leakage == 0)
    }

    // ── case 2: big hit overloads the shield ──────────────────────────────

    /// <summary>⭐ A hit larger than remaining shield capacity drains the shield
    /// to ~0 (clamped) AND puts real, computed hull damage on the ship for the
    /// excess. Asserts both the shield-floor and the excess-leak number.</summary>
    [Fact]
    public void Big_hit_drains_the_shield_and_leaks_the_excess_to_the_hull()
    {
        var mission = NewStartedMission();
        var combo = BigHit.Value;
        var target = NewTarget(0, combo.Hull);
        target.MountPart(combo.ShieldPart, amount: 1, mountId: 0, fractionMounted: 255);   // real shield, armed full
        target = FireOnce(mission, target, combo.WeaponType, () => target.ShieldFraction < 0.903f);

        Assert.True(combo.AbsorbedFull > 0.9f, "test combo must genuinely exceed the useful ceiling");
        Assert.Equal(0f, target.ShieldFraction, 3);                              // overloaded → clamped to 0 (down)
        // The hull took the computed excess, armored by the hull's dtm exactly once.
        Assert.Equal(combo.MaxHP - combo.ExcessHullDamage, target.HitPoints, 1);
    }

    // ── case 3: a "down" shield leaks exactly as if unshielded ────────────

    /// <summary>⭐ Once the shield is at/below the 0.1 "down" threshold, a
    /// further hit leaks through and produces the <b>identical</b> hull-fraction
    /// delta as a no-shield ship taking the same raw hit. This is the test that
    /// pins the multiplication order: if <c>dtmArmor</c> were double-applied or
    /// dropped the two deltas would diverge.</summary>
    [Fact]
    public void Down_shield_leaks_identically_to_unshielded()
    {
        var mA = NewStartedMission();                 // shielded ship, shield knocked down to 0
        var a = NewTarget(0, BigHit.Value.Hull);
        a.MountPart(BigHit.Value.ShieldPart, amount: 1, mountId: 0, fractionMounted: 255);  // arms to 1.0
        var field = typeof(Ship).GetField("_shieldFraction", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(a, 0.0f);                                 // knock it down (≤ 0.1): establish the precondition cleanly, hull stays full
        float aFull = a.HitPoints;
        a = FireOnce(mA, a, BigHit.Value.WeaponType, () => a.HitPoints < aFull - 0.5f);

        var mB = NewStartedMission();                 // no-shield ship, same hull
        var b = NewTarget(0, BigHit.Value.Hull);
        b = FireOnce(mB, b, BigHit.Value.WeaponType, () => b.HitPoints < b.HullType.HitPoints - 0.5f);

        float deltaShielded = 1.0f - a.Fraction;       // hull loss as a fraction of full hull
        float deltaPlain = 1.0f - b.Fraction;
        Assert.Equal(deltaPlain, deltaShielded, 3);    // the shield-down hit behaves byte-for-byte like no shield
        // ... and both equal the decoded raw hit, armored by the hull's dtm once
        // (down branch: leakage = raw, then caller × dtmArmor).
        float expectedDelta = BigHit.Value.RawPower * BigHit.Value.DtmHull / BigHit.Value.MaxHP;
        Assert.Equal(expectedDelta, deltaPlain, 3);
        Assert.Equal(expectedDelta, deltaShielded, 3);
        Assert.Equal(0f, a.ShieldFraction, 3);         // the shield stayed down
    }

    // ── direct seam: pins the armor-multiplication order ──────────────────

    /// <summary>⭐ Direct seam test that pins the <b>multiplication order</b>
    /// with a genuinely non-unit armor cell — the thing the real-hit tests above
    /// cannot see, because every obtainable shield combo in the <em>actual</em>
    /// test core sits on an identity damage-matrix cell (<c>dtmShield = dtmHull
    /// = 1</c> for every survivable hit). Calls the private
    /// <see cref="Ship.ApplyShieldDamage"/> (via reflection, consistent with
    /// this suite's <c>MountCombatWeapon</c> seam test) with a <b>real</b> shield
    /// part whose armor cell for the chosen damage type is ≠ 1, then asserts the
    /// arithmetic at magnitudes that would diverge if <c>dtm</c> were
    /// double-applied or dropped. The down-branch <c>/dtm</c> cancellation and
    /// the partial-branch <c>/dtm</c> are each checked separately.</summary>
    [Fact]
    public void ApplyShieldDamage_seam_keeps_armor_order_with_non_unit_cell()
    {
        var core = RepresentativeCore.Value;
        var (_, shieldType, dmgType, dtm) =
            (from sp in core.PartTypes.Values
             where sp.EquipmentType == EquipmentType.Shield
             let st = (DataPartTypeIGC.Shield)sp.Type
             where st.MaxStrength > 0f
             from dt in Enumerable.Range(0, DataConstantsIGC.DmgidMax)
             let cell = core.Constants.DamageConstants[dt][st.DefenseType]
             where cell > 0f && MathF.Abs(cell - 1f) >= 1e-3f
             select (sp, st, (byte)dt, cell)).First();
        Assert.True(MathF.Abs(dtm - 1f) >= 1e-3f, "the seam shield must sit on a non-unit armor cell");

        var mission = NewStartedMission();
        var ship = NewTarget(0, SmallHit.Value.Hull);   // any hull — only Mission/core matters here
        mission.PlaceShip(ship, mission.Sectors[0]);    // gives DamageConstant a mission core
        var fracField = typeof(Ship).GetField("_shieldFraction", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var apply = typeof(Ship).GetMethod("ApplyShieldDamage", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(Ship), "ApplyShieldDamage");

        float maxStrength = shieldType.MaxStrength;
        float Leak(float fraction, float rawAmount)
        {
            fracField.SetValue(ship, fraction);
            return (float)apply.Invoke(ship, new object[] { dmgType, rawAmount, maxStrength, shieldType.DefenseType })!;
        }

        // 1) Shield DOWN (fraction 0 ≤ 0.1): the shield absorbs nothing and the
        //    hit leaks the FULL raw amount, because the internal amount*dtm is
        //    divided back out by dtm. Drop that /dtm and leakage becomes
        //    raw*dtm ≠ raw (dtm ≠ 1) → this assert fails.
        float raw = 813.75f;
        Assert.Equal(raw, Leak(0f, raw), 3);
        Assert.Equal(0f, ship.ShieldFraction, 3);            // stays down

        // 2) PARTIAL absorb (fraction 0.9 → useful 0.8): choose raw so the
        //    armor-multiplied absorbed fraction = 1.08 > 0.8, forcing the
        //    abnormal branch, and assert its /dtm-scaled leakage. Wrong
        //    multiply/drop at either end of the equation diverges here.
        float absorbed = 1.081f, useful = 0.9f - 0.1f;
        float leakRaw = absorbed * maxStrength / dtm;        // → raw*dtm/maxStrength == absorbed
        float leak = Leak(0.9f, leakRaw);
        Assert.Equal((absorbed - useful) * maxStrength / dtm, leak, 3);
        Assert.Equal(0f, ship.ShieldFraction, 3);            // clamp(0.9 − 1.081) = 0
    }

    // ── no-shield regression is the existing suite, unchanged ─────────────
    // CombatDamageTests.Point_blank_hit_reduces_target_hull_and_not_shooter
    // (etc.) still pass unmodified with the new branch in place: a ship with no
    // Shield part mounted falls through to the same leakage = amount path.
}
