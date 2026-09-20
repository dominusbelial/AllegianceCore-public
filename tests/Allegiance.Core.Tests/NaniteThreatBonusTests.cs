using System;
using System.Linq;
using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>
/// P299 — the nanite gun's bomber/HTT 2× repair-credit bonus. Native doubles
/// the LAUNCHER's nanite repair credit when it repairs a friendly hull whose
/// BASE hull type carries <c>c_habmThreatToStation</c> — "double points for
/// nanning a bomber/htt" (<c>shipIGC.cpp:1262-1263</c>, fed by
/// <c>AddRepair</c>, <c>shipIGC.h:1882-1884</c>). The hull-RESTORE amount is
/// never doubled — a bomber heals at the same rate as a fighter; only the
/// launcher-side credited "points" double.
///
/// The port previously had <b>no repair-stat infrastructure at all</b> (P277
/// explicitly noted "no repair source exists yet, so it is dead code in
/// practice"); P299 adds the accumulator (<see cref="Ship.Repair"/> /
/// <see cref="Ship.AddRepair"/>) that native's <c>m_repair</c> represents, so
/// this bonus finally has a real value to double. That value is <b>not</b> the
/// direct hull-restore amount — it is the launcher's cumulative "fraction of a
/// hull repaired" stat.
/// </summary>
public class NaniteThreatBonusTests
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

    private const short ThreatBit = DataHullTypeIGC.HabmThreatToStation;

    /// <summary>A real hull that carries the threat bit — a bomber-class hull in
    /// the real corpus (e.g. <c>dn_fadrshbmb</c>, whose Dreg bomber records sit
    /// at <c>habm=0x0A92</c>). Selected by bit, not name: the same model name
    /// recurs in a core with different capability sets (e.g. one
    /// <c>dn_fadrshbmb</c> at 0x0892 without the bit and another at 0x0A92 with
    /// it), so the bit is the only faithful discriminator.</summary>
    private static DataHullTypeIGC ThreatHull() =>
        RepresentativeCore.Value.HullTypes.Values
            .First(h => (h.HabmCapabilities & ThreatBit) != 0);

    /// <summary>A real hull that does NOT carry the threat bit — e.g. a light
    /// scout/fighter record (<c>habm=0x2092</c>). Selected by bit, not name.</summary>
    private static DataHullTypeIGC NonThreatHull() =>
        RepresentativeCore.Value.HullTypes.Values
            .First(h => (h.HabmCapabilities & ThreatBit) == 0);

    private static void DamageHullTo(Ship ship, float fraction)
    {
        var f = typeof(Ship).GetField("_fraction", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Ship._fraction field not found");
        f.SetValue(ship, fraction);
    }

    private static Ship NewShip(short id, DataHullTypeIGC hull, short side) =>
        new(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = side };

    /// <summary>
    /// ⭐ The 2× bonus: repair a threat-class (bomber) hull and a non-threat hull
    /// by the SAME fraction of their (respective) hulls, and the launcher's
    /// accrued repair credit is twice as large for the bomber. All ships are
    /// unplaced, so <c>Ship.DamageConstant</c> returns the neutral 1.0 — the
    /// amount needed to restore a given fraction is exactly
    /// <c>-Δ · maxHP</c> for both, and the credited "fraction of a hull
    /// repaired" is Δ for the non-threat and 2Δ for the bomber, independent of
    /// each hull's size. The <see cref="Ship.Repair"/> accumulator must-fail if
    /// the 2× is removed (it would then be Δ then 2Δ, not 2Δ then 3Δ).
    /// </summary>
    [Fact]
    public void Same_side_threat_target_earns_double_repair_credit()
    {
        var bomber = ThreatHull();
        var fighter = NonThreatHull();
        Assert.True(bomber.HasCapability(ThreatBit), "selected threat hull must carry the bit");
        Assert.False(fighter.HasCapability(ThreatBit), "selected non-threat hull must lack the bit");

        var bomberTarget = NewShip(9001, bomber, side: 0);
        var fighterTarget = NewShip(9002, fighter, side: 0);
        var launcher = NewShip(9003, fighter, side: 0);
        DamageHullTo(bomberTarget, 0.5f);
        DamageHullTo(fighterTarget, 0.5f);

        const float delta = 0.1f;                 // fraction of each hull repaired
        byte type = 0;                            // irrelevant while unplaced (dtmArmor=1.0)
        float bomberAmount = -delta * bomber.HitPoints;
        float fighterAmount = -delta * fighter.HitPoints;

        // Repair the bomber first — unplaced so no clipping (0.5 + 0.1 < 1.0).
        bomberTarget.ReceiveDamage(type, bomberAmount, now: 0.0, Vector3.Zero, Vector3.Zero, launcher);
        Assert.Equal(2f * delta, launcher.Repair, 4);     // bomber: 2 × 0.1

        // Then the non-threat fighter — earns single credit.
        fighterTarget.ReceiveDamage(type, fighterAmount, now: 0.0, Vector3.Zero, Vector3.Zero, launcher);
        Assert.Equal(3f * delta, launcher.Repair, 4);     // + 1 × 0.1

        // The credited stat doubles, but the HULL-RESTORE amount does not: each
        // target really was healed by exactly delta (the bonus is launcher-side
        // points, not a stronger heal).
        Assert.Equal(0.5f + delta, bomberTarget.Fraction, 4);
        Assert.Equal(0.5f + delta, fighterTarget.Fraction, 4);
    }

    /// <summary>
    /// The credit is gated on a FRIENDLY (same-side) target — native awards it
    /// only when <c>pside == launcher-&gt;GetSide()</c> (or allied; this port
    /// collapses to same-side, no alliance concept). A nan round still HEALS an
    /// enemy's hull (P277's faithful wrinkle), but the launcher earns NO repair
    /// credit for it — exactly native, where the side test exists solely to
    /// award the launcher a repair-stat.
    /// </summary>
    [Fact]
    public void Cross_side_repair_heals_hull_but_awards_no_credit()
    {
        var bomber = ThreatHull();
        var enemyTarget = NewShip(9101, bomber, side: 1);   // enemy side
        var launcher = NewShip(9103, bomber, side: 0);
        DamageHullTo(enemyTarget, 0.5f);

        const float delta = 0.1f;
        enemyTarget.ReceiveDamage(0, -delta * bomber.HitPoints, 0.0, Vector3.Zero, Vector3.Zero, launcher);

        Assert.Equal(0.5f + delta, enemyTarget.Fraction, 4);   // hull still healed
        Assert.Equal(0f, launcher.Repair, 4);                  // but no credit (cross-side)
    }

    /// <summary>
    /// A friendly nan round that hits an already-FULL hull restores nothing and
    /// credits nothing (native clips the stat to "the amount that had effect",
    /// <c>shipIGC.cpp:1250-1251</c>) — the bonus can't farm credit by spraying a
    /// topped-up bomber. Guard against the clip producing negative credit.
    /// </summary>
    [Fact]
    public void Repairing_an_already_full_hull_adds_no_credit()
    {
        var bomber = ThreatHull();
        var target = NewShip(9201, bomber, side: 0);
        var launcher = NewShip(9203, bomber, side: 0);
        DamageHullTo(target, 1.0f);                            // already full

        target.ReceiveDamage(0, -0.005f * bomber.HitPoints, 0.0, Vector3.Zero, Vector3.Zero, launcher);

        Assert.Equal(1.0f, target.Fraction, 4);                // still full
        Assert.Equal(0f, launcher.Repair, 4);                  // nothing credited
    }
}
