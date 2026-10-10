using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P478 — the economy half of the side-global-attribute (GA) wiring: a
/// development bucket's money cost and build time are scaled at <b>creation</b>
/// by the owning side's GA factors <c>c_gaDevelopmentCost</c> (23) and
/// <c>c_gaDevelopmentTime</c> (24) — the port of native
/// <c>CbucketIGC::Initialize</c> (<c>src/Igc/bucketigc.cpp:70-74</c>), which
/// multiplies <c>m_price</c> by <c>GetGlobalAttributeSet().GetAttribute(
/// c_gaDevelopmentCost)</c> and <c>m_timeToBuild</c> by
/// <c>GetAttribute(c_gaDevelopmentTime)</c> inside
/// <c>if (buyable->GetObjectType() == OT_development)</c>.
/// <para><b>Which buckets native scales (the fidelity cut asserted here):</b>
/// only <c>OT_development</c> buckets, and within them the
/// <c>c_didTeamMoney</c> development (id 1) is exempt — so ship-type,
/// drone-type and station-type buckets stay at their raw core price/time, and
/// the team-money dev bucket stays raw too.</para>
/// <para>PCore001 has <b>no development</b> whose <c>Gas[23]/Gas[24]</c> differ
/// from 1.0 (measured by scanning all 305 developments), so there is no
/// development that raises/lowers research cost or time; the required synthetic
/// non-1.0 test is the primary assertion, and a real-civ test (whose base
/// <c>GasBaseAttributes[23]/[24]</c> vary widely) exercises the production
/// <c>Mission.Assemble</c> path.</para>
/// </summary>
public class P478SideGaDevelopmentCostTimeTests
{
    private static readonly Lazy<StaticCore> Core = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            if (!System.IO.Path.GetFileName(path).Equals("PCore001.igc", System.StringComparison.OrdinalIgnoreCase))
                continue;
            var file = IgcReader.ParseFile(path);
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            return StaticCore.Load(file);
        }
        throw new InvalidOperationException("PCore001 not found in corpus");
    });

    private static readonly Lazy<MissionMap> Map = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            if (!System.IO.Path.GetFileName(path).Equals("XXV_simple.igc", System.StringComparison.OrdinalIgnoreCase))
                continue;
            var file = IgcReader.ParseFile(path);
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count == 0) continue;
            return map;
        }
        throw new InvalidOperationException("XXV_simple.igc not found in corpus");
    });

    /// <summary>A civ-base GA with the given cost/time factors at indices 23/24,
    /// everything else identity 1.0 — the synthetic non-1.0 multiplier the task
    /// mandates.</summary>
    private static GlobalAttributeSet CivGa(float costMul, float timeMul)
    {
        var arr = new float[GlobalAttributeSet.Count];
        Array.Fill(arr, 1f);
        arr[GlobalAttributeSet.DevelopmentCost] = costMul;
        arr[GlobalAttributeSet.DevelopmentTime] = timeMul;
        return new GlobalAttributeSet(arr);
    }

    private static IReadOnlyList<Bucket> ForSide(float costMul, float timeMul)
    {
        var civ = Core.Value.Civilizations.Values.OrderBy(c => c.CivilizationId).First();
        return BucketCatalog.ForSide(Core.Value, new NullIgcSite(),
            TechTreeBitMask.FromBytes(civ.TtbmBaseTechs), CivGa(costMul, timeMul));
    }

    // ── GA plumbing: indices + identity ────────────────────────────────

    /// <summary>⭐ The two economy indices are the documented native constants,
    /// and an identity GA reproduces the raw development price/time exactly —
    /// so the economy half is a no-op for a default (all-1.0) side.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Economy_indices_match_native_and_identity_is_raw()
    {
        Require.Artwork();
        Assert.Equal(23, GlobalAttributeSet.DevelopmentCost);
        Assert.Equal(24, GlobalAttributeSet.DevelopmentTime);

        var buckets = ForSide(costMul: 1f, timeMul: 1f);
        var devBuckets = buckets.Where(b => b.ItemType == BucketItemType.BitDevelopment).ToList();
        Assert.NotEmpty(devBuckets);
        foreach (var b in devBuckets)
        {
            Assert.Equal(b.Development!.Value.Price, b.Price);
            Assert.Equal(b.Development!.Value.TimeToBuild, b.TimeToBuild);
        }
    }

    // ── DevelopmentCost / DevelopmentTime scale development buckets ─────

    /// <summary>⭐ A side whose GA has <c>DevelopmentCost = 1.5</c> and
    /// <c>DevelopmentTime = 2.0</c>: every non-team-money development bucket's
    /// <c>Price</c> is 1.5× the raw core price and its <c>TimeToBuild</c> 2.0×
    /// the raw build time — baked at creation (bucketigc.cpp:70-74). Pre-fix the
    /// buckets carried the raw price/time, so this ratio assertion failed.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Development_cost_and_time_scale_development_buckets_at_creation()
    {
        Require.Artwork();
        var buckets = ForSide(costMul: 1.5f, timeMul: 2.0f);
        var devBuckets = buckets.Where(b => b.ItemType == BucketItemType.BitDevelopment).ToList();
        Assert.NotEmpty(devBuckets);
        int checkedCount = 0;
        foreach (var b in devBuckets)
        {
            if (b.Development!.Value.DevelopmentId == Mission.TeamMoneyDevelopmentId)
                continue;                                  // c_didTeamMoney is exempt (native)
            Assert.Equal((int)(b.Development!.Value.Price * 1.5f), b.Price);
            Assert.Equal((uint)(b.Development!.Value.TimeToBuild * 2.0f), b.TimeToBuild);
            checkedCount++;
        }
        Assert.True(checkedCount > 0, "no non-team-money development bucket to assert");
        Console.WriteLine($"P478-DEV: scaled {checkedCount} development buckets (cost×1.5, time×2.0)");
    }

    /// <summary>⭐ The <c>c_didTeamMoney</c> development (id 1) is exempt from
    /// the GA scaling (native bucketigc.cpp:70: <c>!= c_didTeamMoney</c>) — the
    /// prosperity/economic-win shortcut is priced by the core, not by research
    /// GAs. Guarded (the dev may be absent/obsolete in this port's catalog).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void TeamMoney_development_is_exempt_from_scaling()
    {
        Require.Artwork();
        var buckets = ForSide(costMul: 1.5f, timeMul: 2.0f);
        var tm = buckets.FirstOrDefault(b => b.Development?.DevelopmentId == Mission.TeamMoneyDevelopmentId);
        if (tm is null)
        {
            Console.WriteLine("P478-TEAMMONEY: c_didTeamMoney not a live bucket (obsolete/absent) — exemption vacuously holds");
            return;
        }
        Assert.Equal(tm.Development!.Value.Price, tm.Price);           // NOT ×1.5
        Assert.Equal(tm.Development!.Value.TimeToBuild, tm.TimeToBuild); // NOT ×2.0
    }

    /// <summary>⭐ Native scales only <c>OT_development</c> buckets — the
    /// ship-type, drone-type and station-type buckets keep their raw core
    /// price/time (bucketigc.cpp:66-75). Pre-fix/fidelity guard.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Non_development_buckets_are_not_scaled()
    {
        Require.Artwork();
        var buckets = ForSide(costMul: 1.5f, timeMul: 2.0f);

        var ship = buckets.First(b => b.ItemType == BucketItemType.BitShipType);
        Assert.Equal(BucketCatalog.SyntheticPrice, ship.Price);
        Assert.Equal(BucketCatalog.SyntheticTimeToBuild, ship.TimeToBuild);

        int droneChecked = 0, stationChecked = 0;
        foreach (var b in buckets)
        {
            if (b.DroneType is not null)
            {
                Assert.Equal(b.DroneType.Value.Price, b.Price);
                Assert.Equal(b.DroneType.Value.TimeToBuild, b.TimeToBuild);
                droneChecked++;
            }
            else if (b.StationType is not null)
            {
                Assert.Equal(b.StationType.Value.Price, b.Price);
                Assert.Equal(b.StationType.Value.TimeToBuild, b.TimeToBuild);
                stationChecked++;
            }
        }
        Console.WriteLine($"P478-NONDEV: ship raw; {droneChecked} drone-type, {stationChecked} station-type buckets all raw (not GA-scaled)");
    }

    // ── Real-civ production path through Mission.Assemble ───────────────

    /// <summary>
    /// ⭐ End-to-end through the real <c>Mission.Assemble</c> path with a real
    /// PCore001 civ whose base <c>GasBaseAttributes[23]/[24]</c> are non-1.0 —
    /// <b>Bios</b> (civ 35, cost 0.5, time 5.0). The assembled side's
    /// development buckets must carry the civ-base-scaled price/time. This is
    /// the strongest available E2E, because no PCore001 <em>development</em>
    /// changes these two GAs (measured); the civ base is the only real source of
    /// a non-1.0 multiplier for them.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Real_civ_base_GA_scales_development_buckets_through_mission_assemble()
    {
        Require.Artwork();
        var civ = Core.Value.Civilizations.Values.First(c => c.CivilizationId == 35);   // Bios
        Assert.NotEqual(1f, civ.GasBaseAttributes[GlobalAttributeSet.DevelopmentCost]);
        Assert.NotEqual(1f, civ.GasBaseAttributes[GlobalAttributeSet.DevelopmentTime]);

        var m = Mission.Assemble(Core.Value, Map.Value,
            [new SideSpec(0, "Team 1", civ.CivilizationId)], new NullIgcSite());
        var side = m.Sides[0];
        Assert.Equal(35, side.Civilization.CivilizationId);

        float costMul = civ.GasBaseAttributes[23], timeMul = civ.GasBaseAttributes[24];
        int checkedCount = 0;
        foreach (var b in side.Buckets.Where(b => b.ItemType == BucketItemType.BitDevelopment))
        {
            if (b.Development!.Value.DevelopmentId == Mission.TeamMoneyDevelopmentId)
                continue;
            Assert.Equal((int)(b.Development!.Value.Price * costMul), b.Price);
            Assert.Equal((uint)(b.Development!.Value.TimeToBuild * timeMul), b.TimeToBuild);
            checkedCount++;
        }
        Assert.True(checkedCount > 0, "no non-team-money development bucket on the Bios side");
        Console.WriteLine($"P478-CIV: Bios costMul={costMul} timeMul={timeMul}; {checkedCount} dev buckets scaled through Mission.Assemble");
    }
}
