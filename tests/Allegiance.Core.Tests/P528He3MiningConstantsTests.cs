using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P528 — He3 economy native constants. The port replaces its three
/// test-convenience stand-ins (rate ×100, capacity 150, price 5) with the
/// native formulas fed by the loaded core's <c>FloatConstants</c>:
/// <list type="bullet">
/// <item><b>Rate</b> — <c>EffectiveMiningRate()</c> = the side's
/// <c>c_gaMiningRate</c> factor (GA baseline 1.0 → ≈ 1 ore/s × tech; the
/// retired <c>Sector.MiningRate = 100</c> base is gone).</item>
/// <item><b>Capacity</b> — <c>EffectiveMineCapacity()</c> = loaded core
/// <c>FloatConstants[c_fcidCapacityHe3 = 7]</c> (fallback 90) × the side's
/// <c>c_gaMiningCapacity</c>.</item>
/// <item><b>Price</b> — <c>EffectiveOreCreditPerUnit()</c> = loaded core
/// <c>FloatConstants[c_fcidValueHe3 = 8]</c> (fallback 80) × the side's
/// <c>c_gaMiningYield</c>.</item>
/// </list>
/// These three are the task's failing-first proofs: with a GA of 1.0 a ship
/// mines at 1 ore/s (not 100), its capacity equals the core's
/// <c>FloatConstants[7]</c>, and the sale proceeds equal ore × core
/// <c>FloatConstants[8]</c> — all three fail against the pre-P528 code
/// (100/s, 150, 5).
/// </summary>
public class P528He3MiningConstantsTests
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

    /// <summary>A started single-side mission whose side carries identity GA
    /// (or the given factors), so the effective values are exactly the base ×
    /// the specified multiplier.</summary>
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

    private static float Fc(Mission m, int idx) =>
        m.Core.Constants.FloatConstants is { } fc && fc.Length > idx ? fc[idx] : float.NaN;

    /// <summary>⭐ With GA 1.0 (identity), a ship mines at 1 ore/s — the native
    /// base — not the retired ×100 stand-in.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Mining_rate_with_identity_GA_is_1_ore_per_second()
    {
        Require.Artwork();
        var m = NewMission();
        var hull = m.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var ship = new Ship(9500, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity)
        { Mission = m, SideId = m.Sides[0].Id };

        Assert.Equal(1.0f, ship.EffectiveMiningRate(), 5);
        // (accessor-level failing-first proof; the physical per-tick accrual at
        // this rate is exercised by the seam-compressed tests in MiningTests.)
    }

    /// <summary>⭐ Capacity equals the loaded core's <c>FloatConstants[7]</c> ×
    /// the side's <c>c_gaMiningCapacity</c>.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Capacity_equals_core_FloatConstants7_times_GA_capacity()
    {
        Require.Artwork();
        float baseCap = Fc(NewMission(), 7);
        Assert.NotEqual(150f, baseCap);   // sanity: the corpus core is not the old stand-in

        var mIdentity = NewMission();
        var identity = ShipOn(mIdentity);
        Assert.Equal(baseCap, identity.EffectiveMineCapacity(), 3);              // core[7] × 1.0

        var mGa = NewMission((GlobalAttributeSet.MiningCapacity, 2.5f));
        Assert.Equal(baseCap * 2.5f, ShipOn(mGa).EffectiveMineCapacity(), 3);    // core[7] × 2.5
    }

    /// <summary>⭐ Sale proceeds equal ore × core <c>FloatConstants[8]</c> × the
    /// side's <c>c_gaMiningYield</c> — the native <c>PaydayEvent</c> He3 sale.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Sale_proceeds_equal_ore_times_core_FloatConstants8_times_GA_yield()
    {
        Require.Artwork();
        float baseValue = Fc(NewMission(), 8);
        Assert.NotEqual(5f, baseValue);   // sanity: the corpus core is not the old stand-in

        var mGa = NewMission((GlobalAttributeSet.MiningYield, 1.5f));
        var ship = ShipOn(mGa);
        float perUnit = ship.EffectiveOreCreditPerUnit();
        Assert.Equal(baseValue * 1.5f, perUnit, 3);          // core[8] × 1.5

        const float ore = 40f;
        Assert.Equal(ore * baseValue * 1.5f, ore * perUnit, 3);   // 40 ore sold → proceeds
        Console.WriteLine($"P528-PRICE: core[8] {baseValue:F0} × GA yield 1.5 = {perUnit:F2}/u; 40 ore → {ore * perUnit:F0}");
    }

    private static Ship ShipOn(Mission m)
    {
        var hull = m.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
        return new Ship(9599, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity)
        { Mission = m, SideId = m.Sides[0].Id };
    }
}
