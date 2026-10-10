using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P467b — the ship-durability half of the side-global-attribute (GA) wiring:
/// a side's <see cref="GlobalAttributeSet"/> now scales a ship's max energy,
/// max armor (hull hit points), max shield strength and shield regeneration
/// through four new effective getters that reuse P446's/P467's shared side
/// resolution (<c>SideGlobalAttributes()</c>, identity when sideless). Native
/// wraps these maxima in GA multipliers at <c>MyHullType::GetMaxEnergy</c>
/// (<c>shipIGC.cpp:4561-4564</c>), <c>MyHullType::GetHitPoints</c>
/// (<c>shipIGC.cpp:4569-4573</c>), the shield's <c>GetMaxStrength</c> /
/// <c>GetRegeneration</c> (<c>shieldIGC.h:106-117</c>). Fractions stay the
/// stored state, so a GA change rescales absolute values without touching the
/// hull/shield fraction — exactly native.
/// <para>Each test is data-derived from the real PCore001 static core and
/// compares the GA-boosted outcome to the identity (1.0) baseline to isolate
/// the multiplier. Every test failed on the pre-P467b tree first (recorded in
/// the P467B report): before wiring, the boosted and identity outcomes were
/// identical, so the ratio/difference assertions below failed.</para>
/// </summary>
public class P467bSideGaScaleShipDurabilityTests
{
    private static readonly Lazy<StaticCore> Core = new(() =>
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

    private static readonly Lazy<MissionMap> Map = new(() =>
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

    private static float Dtm(StaticCore core, byte dmg, byte def) => core.Constants.DamageConstants[dmg][def];

    /// <summary>Build a started single-side mission whose sole side carries the
    /// given GA factors (everything else identity 1.0 — the P446/P467 recipe).</summary>
    private static Mission NewMission(params (int idx, float val)[] factors)
    {
        var civ = Core.Value.Civilizations.Values.OrderBy(c => c.CivilizationId).First();
        var m = Mission.Assemble(Core.Value, Map.Value, [new SideSpec(0, "Team 1", civ.CivilizationId)], new NullIgcSite());
        m.Stage = MissionStage.Started;
        var arr = new float[GlobalAttributeSet.Count];
        Array.Fill(arr, 1f);
        foreach (var (idx, val) in factors) arr[idx] = val;
        m.Sides[0].GlobalAttributes = new GlobalAttributeSet(arr);
        return m;
    }

    /// <summary>A hull that can fight (the P467 Fight recipe).</summary>
    private static readonly Lazy<DataHullTypeIGC> FightHull = new(() =>
        Core.Value.HullTypes.Values.First(h => h.MaxEnergy > 0 && h.MaxAmmo > 0
                                                && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed));

    /// <summary>A hull with the highest energy RechargeRate (so the test can fill
    /// the pool to the ceiling in a modest number of sim ticks).</summary>
    private static readonly Lazy<DataHullTypeIGC> RechargeHull = new(() =>
    {
        DataHullTypeIGC best = default;
        foreach (var h in Core.Value.HullTypes.Values)
            if (h.MaxEnergy > 0 && h.RechargeRate > 0
                && (best.HullId == 0 || h.RechargeRate > best.RechargeRate))
                best = h;
        if (best.HullId == 0)
            throw new InvalidOperationException("no rechargeable hull in representative core");
        return best;
    });

    /// <summary>A ship on the mission's sole side (so GA resolves) at the origin.</summary>
    private static Ship NewGaShip(Mission m, DataHullTypeIGC hull, Vector3 pos)
        => new(shipId: 8010, hull: hull, position: pos, velocity: Vector3.Zero, orientation: Orientation.Identity)
        { Mission = m, SideId = m.Sides[0].Id };

    /// <summary>A mountable shield with a positive regen rate plus a damage type
    /// that the shield's defense type responds to (nonzero dtm).</summary>
    private static readonly Lazy<(DataPartTypeIGC Part, DataPartTypeIGC.Shield Shield, byte Dmg)> ShieldPair = new(() =>
    {
        var core = Core.Value;
        foreach (var sp in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Shield))
        {
            var st = (DataPartTypeIGC.Shield)sp.Type;
            if (st.MaxStrength <= 0f || st.RateRegen <= 0f) continue;
            for (byte dmg = 0; dmg < DataConstantsIGC.DmgidMax; dmg++)
                if (Dtm(core, dmg, st.DefenseType) > 0f)
                    return (sp, st, dmg);
        }
        throw new InvalidOperationException("no regen-able shield+damage combo in representative core");
    });

    // ── GA plumbing: indices, identity, sideless ─────────────────────────

    /// <summary>⭐ The four ship-durability attribute indices are the documented
    /// native constants, and a sideless ship / identity side reproduces the raw
    /// values exactly — so the whole durability half is a no-op for the default
    /// (no-side) case.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Durability_indices_match_native_and_identity_is_raw()
    {
        Require.Artwork();
        Assert.Equal(8, GlobalAttributeSet.MaxArmorShip);
        Assert.Equal(9, GlobalAttributeSet.MaxShieldShip);
        Assert.Equal(10, GlobalAttributeSet.ShieldRegenerationShip);
        Assert.Equal(13, GlobalAttributeSet.MaxEnergy);

        // Sideless ship: every effective getter reproduces the raw hull/shield value.
        var hull = FightHull.Value;
        var sideless = new Ship(8200, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        Assert.Equal(hull.MaxEnergy, sideless.EffectiveMaxEnergy(), 6);
        Assert.Equal(hull.HitPoints, sideless.EffectiveMaxHitPoints(), 6);

        // Identity side: getters equal raw too (multiply by 1.0).
        var m = NewMission();
        var gaship = NewGaShip(m, hull, Vector3.Zero);
        m.PlaceShip(gaship, m.Sectors[0]);
        Assert.Equal(hull.MaxEnergy, gaship.EffectiveMaxEnergy(), 6);
        Assert.Equal(hull.HitPoints, gaship.EffectiveMaxHitPoints(), 6);
    }

    // ── MaxArmorShip: hull durability ────────────────────────────────────

    /// <summary>An armor-hit recipe: a hull + damage type whose identity single
    /// hit lands a survivable ~0.2 hull-fraction drop.</summary>
    private static readonly Lazy<(DataHullTypeIGC Hull, byte Dmg, float Amount)> ArmorHit = new(() =>
    {
        var core = Core.Value;
        foreach (var hull in core.HullTypes.Values.OrderBy(h => h.HitPoints))
        {
            if (hull.HitPoints <= 0f) continue;
            for (byte dmg = 0; dmg < DataConstantsIGC.DmgidMax; dmg++)
            {
                float dtm = Dtm(core, dmg, hull.DefenseType);
                if (dtm <= 0f) continue;
                float amount = 0.2f * hull.HitPoints / dtm;   // identity drop = 0.2
                return (hull, dmg, amount);
            }
        }
        throw new InvalidOperationException("no armor-hit combo in representative core");
    });

    /// <summary>Apply the same absolute <paramref name="amount"/> of
    /// <paramref name="dmg"/> to a fresh full-hull ship on a side whose
    /// <c>MaxArmorShip</c> is <paramref name="gaMul"/>, and return the hull
    /// fraction it lost. Native: <c>_fraction -= amount·dtmArmor / GetHitPoints()</c>
    /// (<c>shipIGC.cpp:1232</c>, <c>GetHitPoints()</c> = raw·GA).</summary>
    private static float ArmorDrop(DataHullTypeIGC hull, byte dmg, float amount, float gaMul)
    {
        var m = NewMission((GlobalAttributeSet.MaxArmorShip, gaMul));
        var ship = NewGaShip(m, hull, Vector3.Zero);
        m.PlaceShip(ship, m.Sectors[0]);
        float f0 = ship.Fraction;
        ship.ReceiveDamage(dmg, amount, m.LastUpdate, Vector3.Zero, Vector3.Zero, null);
        return f0 - ship.Fraction;
    }

    /// <summary>⭐ A side with <c>MaxArmorShip = 1.5</c>: the SAME absolute damage
    /// removes a smaller hull fraction, scaling the damage denominator by 1.5×
    /// (native <c>GetHitPoints() · c_gaMaxArmorShip</c>). Pre-fix both missions
    /// used the raw denominator, so the drops were identical and the ratio
    /// assertion failed.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void MaxArmorShip_scales_the_damage_denominator()
    {
        Require.Artwork();
        var (hull, dmg, amount) = ArmorHit.Value;
        float idDrop = ArmorDrop(hull, dmg, amount, gaMul: 1f);
        float boostDrop = ArmorDrop(hull, dmg, amount, gaMul: 1.5f);
        Assert.Equal(0.2f, idDrop, 3);                       // identity: raw denominator
        Assert.InRange(boostDrop / idDrop, 1f / 1.5f - 0.06f, 1f / 1.5f + 0.06f);   // ~0.667
        Console.WriteLine($"P467B-ARMOR: identity drop {idDrop:F4}; GA1.5 drop {boostDrop:F4}; ratio {boostDrop / idDrop:F3} (expect ~0.667)");
    }

    // ── MaxShieldShip: absorption capacity ───────────────────────────────

    /// <summary>Apply the same hit to a fresh full shield on a side whose
    /// <c>MaxShieldShip</c> is <paramref name="gaMul"/>; returns the shield
    /// fraction consumed (hull must stay untouched — the hit is fully absorbed).
    /// Native: <c>absorbed = amount·dtmShield / GetMaxStrength()</c>
    /// (<c>shieldIGC.cpp:155-156</c>, <c>GetMaxStrength()</c> = raw·GA).</summary>
    private static float ShieldAbsorptionDrop(DataPartTypeIGC.Shield shield, byte dmg, float gaMul, out float hullDrop)
    {
        var m = NewMission((GlobalAttributeSet.MaxShieldShip, gaMul));
        var ship = NewGaShip(m, FightHull.Value, Vector3.Zero);
        m.PlaceShip(ship, m.Sectors[0]);
        ship.MountPart(ShieldPair.Value.Part, amount: 1, mountId: 0, fractionMounted: 255);   // arms to 1.0
        Assert.Equal(1f, ship.ShieldFraction, 5);
        float amount = 0.5f * shield.MaxStrength / Dtm(Core.Value, dmg, shield.DefenseType);  // identity absorbed = 0.5
        float sf0 = ship.ShieldFraction, hf0 = ship.Fraction;
        ship.ReceiveDamage(dmg, amount, m.LastUpdate, Vector3.Zero, Vector3.Zero, null);
        hullDrop = hf0 - ship.Fraction;
        return sf0 - ship.ShieldFraction;
    }

    /// <summary>⭐ A side with <c>MaxShieldShip = 2</c> raises absorption capacity:
    /// the same hit consumes HALF the shield fraction (native
    /// <c>GetMaxStrength() · c_gaMaxShieldShip</c>) while the hull still takes no
    /// damage. Pre-fix both missions used the raw <c>MaxStrength</c>, so the
    /// drops were identical and the ratio assertion failed.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void MaxShieldShip_raises_shield_absorption_capacity()
    {
        Require.Artwork();
        var (_, shield, dmg) = ShieldPair.Value;
        float idDrop = ShieldAbsorptionDrop(shield, dmg, gaMul: 1f, out float idHullDrop);
        float boostDrop = ShieldAbsorptionDrop(shield, dmg, gaMul: 2f, out float boostHullDrop);
        Assert.Equal(0f, idHullDrop, 5);       // fully absorbed in both
        Assert.Equal(0f, boostHullDrop, 5);
        Assert.InRange(boostDrop / idDrop, 0.40f, 0.60f);   // ~0.5
        Console.WriteLine($"P467B-SHLD: identity shield drop {idDrop:F4}; GA2 drop {boostDrop:F4}; ratio {boostDrop / idDrop:F3} (expect ~0.5)");
    }

    // ── ShieldRegenerationShip: regen speed ──────────────────────────────

    /// <summary>Deplete a fresh shield to 0.3, then run one second of regen
    /// (<c>UpdateShield</c> via reflection; native <c>shieldIGC.cpp:88</c>:
    /// <c>fraction += GetRegeneration()·dt / GetMaxStrength()</c>). Returns the
    /// fraction gained. With only the regen GA set, the gain is exactly
    /// <c>GA × rawRegen/maxStrength</c>.</summary>
    private static float RegenGain(DataPartTypeIGC.Shield shield, float gaMul)
    {
        var m = NewMission((GlobalAttributeSet.ShieldRegenerationShip, gaMul));
        var ship = NewGaShip(m, FightHull.Value, Vector3.Zero);
        m.PlaceShip(ship, m.Sectors[0]);
        ship.MountPart(ShieldPair.Value.Part, amount: 1, mountId: 0, fractionMounted: 255);
        const float depleted = 0.3f;
        typeof(Ship).GetField("_shieldFraction", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ship, depleted);
        typeof(Ship).GetMethod("UpdateShield", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ship, new object[] { 1.0f });
        return ship.ShieldFraction - depleted;
    }

    /// <summary>⭐ A side with <c>ShieldRegenerationShip = 2</c> regenerates the
    /// shield twice as fast (native <c>GetRegeneration() · c_gaShieldRegenerationShip</c>).
    /// Pre-fix both missions regenerated at the raw rate, so the gains were
    /// identical and the ratio assertion failed.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ShieldRegenerationShip_speeds_regeneration()
    {
        Require.Artwork();
        var (_, shield, _) = ShieldPair.Value;
        float idGain = RegenGain(shield, gaMul: 1f);
        float boostGain = RegenGain(shield, gaMul: 2f);
        Assert.True(idGain > 1e-5f, $"identity regen must be measurable, got {idGain:E2}");
        Assert.InRange(boostGain / idGain, 1.8f, 2.2f);
        Console.WriteLine($"P467B-REGEN: identity gain {idGain:E3}; GA2 gain {boostGain:E3}; ratio {boostGain / idGain:F3} (expect ~2)");
    }

    // ── MaxEnergy: ceiling + recharge clamp ──────────────────────────────

    /// <summary>⭐ A side with <c>MaxEnergy = 2</c> raises the energy ceiling to
    /// 2× the hull's raw max, and P461's in-flight recharge clamps to that
    /// effective ceiling (native <c>GetMaxEnergy()</c>, <c>shipIGC.cpp:299-302</c>) —
    /// not the raw hull max. The wire's energy FRACTION stays sane (near-full,
    /// in [0,1]) because its denominator is the effective ceiling too
    /// (native <c>pfractions->SetEnergy(m_myHullType.GetMaxEnergy(), m_energy)</c>,
    /// <c>shipIGC.h:1124</c>). Pre-fix the clamp used the raw ceiling, so energy
    /// topped out at <c>hull.MaxEnergy</c> (half the GA ceiling) and this
    /// assertion failed.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void MaxEnergy_raises_the_ceiling_and_recharge_clamps_to_it()
    {
        Require.Artwork();
        var hull = RechargeHull.Value;
        var m = NewMission((GlobalAttributeSet.MaxEnergy, 2f));
        var ship = new Ship(8301, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity)
        { Mission = m, SideId = m.Sides[0].Id };
        m.PlaceShip(ship, m.Sectors[0]);

        float raw = hull.MaxEnergy;
        float eff = ship.EffectiveMaxEnergy();
        Assert.Equal(raw * 2f, eff, 3);          // the ceiling itself is GA-scaled

        ship.Energy = 1f;                        // near-empty, recharge gate satisfied (RechargeRate>0)
        double t = m.LastUpdate;
        for (int i = 0; i < 60000 && ship.Energy < eff - 0.1f; i++) { t += 0.01; m.Update(t); }
        Assert.Equal(eff, ship.Energy, 2);       // clamped to the EFFECTIVE ceiling (2× raw)
        Assert.InRange(ship.Energy, raw * 1.99f, raw * 2.010f);   // exceeds the raw hull max

        var u = ServerShipUpdate.FromModel(ship.Id, ship, (uint)m.LastUpdate);
        Assert.InRange(u.EnergyFraction, 0.98f, 1.0f);            // fraction stays sane against the doubled ceiling
        Console.WriteLine($"P467B-ENERGY: raw max {raw:F0}; effective (GA2) {eff:F0}; recharge clamps to {ship.Energy:F0}; wire frac {u.EnergyFraction:F3}");
    }
}
