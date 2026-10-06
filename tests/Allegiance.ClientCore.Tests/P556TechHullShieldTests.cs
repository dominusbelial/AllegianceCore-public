using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Tools.McpSpawner;
using Boh = Allegiance.Tools.McpSpawner.BotManager;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P556 — failing-first UNIT tests for tech levels that change SURVIVABILITY the
/// native way: tech N advances the scenario HULL along <c>SuccessorHullId</c>
/// by <c>floor(N/2)</c> steps (the native hull-upgrade progression —
/// <c>IstationIGC::GetSuccessor</c>, src/Igc/stationIGC.h:420; applied at
/// src/Igc/shipIGC.h:1004), and resolves parts from the RESULTING hull's native
/// <c>PreferredPartsTypes</c>, each advanced N steps along <c>SuccessorPartID</c>
/// (src/Igc/partTypeIGC.cpp:35-37, walked at stationIGC.h:415, applied at
/// shipIGC.h:1036), keeping only what <c>CanMountPart</c> allows — including the
/// SHIELD chain, now mountable after P555. Tech 0 stays exactly the scenario
/// hull's native preferred loadout (with <c>Sm Shield 1</c>).
///
/// Ground truth (PCore001.igc, the representative core): Adv Scout (13, 225 HP)
/// → successor Hvy Scout (90, 250 HP, SuccessorHullId = -1 / chain end). Shield
/// chain 21 (Sm Shield 1, 150/2.0) → 40 (Sm Shield 2, 180/3.0) → 42 (Sm Shield
/// 3, 225/3.75); gun chain 33 (PW Gat Gun 1) → 71 (PW Gat Gun 2) → 72 (PW Gat
/// Gun 3). Verified live against the corpus (see the P556 report).
/// </summary>
public class P556TechHullShieldTests
{
    private static StaticCore Core() => Catalog.RepresentativeCore();

    /// <summary>The Adv Scout → Hvy Scout successor chain in the representative
    /// core is exactly the native-data ground truth the P556 hull rule ports:
    /// Adv Scout 13's successor is Hvy Scout 90, whose own successor is NA (-1),
    /// i.e. the chain ENDS at 90 (so floor(N/2) beyond tech 2 clamps to Hvy
    /// Scout). Documents the corpus assumption the other tests rely on.</summary>
    [Fact]
    public void AdvScout_SuccessorChain_IsHvyScout90_EndingThere()
    {
        var core = Core();
        Assert.Equal((short)13, core.HullTypes[13].HullId);
        Assert.Equal("Adv Scout", core.HullTypes[13].Name);
        Assert.Equal(90, core.HullTypes[13].SuccessorHullId);
        Assert.Equal("Hvy Scout", core.HullTypes[90].Name);
        Assert.Equal(-1, core.HullTypes[90].SuccessorHullId);   // chain end
    }

    /// <summary>Tech 0 flies the SCENARIO hull unchanged (floor(0/2) = 0 steps)
    /// with its native preferred loadout — now INCLUDING Sm Shield 1 after P555
    /// (the unshielded-first-contact problem P556 fixes).</summary>
    [Fact]
    public void Tech0_FliesScenarioHull_WithNativePreferredLoadout_IncludingSmShield1()
    {
        Assert.Equal((short)13, Catalog.EffectiveHullAtLevel(13, 0).HullId);   // Adv Scout
        var tl = Catalog.NativePreferredLoadout(13, "Adv Scout", 0);
        Assert.Equal((short)13, tl.HullId);
        Assert.Equal("Adv Scout", tl.HullName);
        Assert.Equal(225f, tl.HitPoints);
        Assert.Contains(tl.Parts, p => p.PartId == 33);   // PW Gat Gun 1
        Assert.Contains(tl.Parts, p => p.PartId == 21);   // Sm Shield 1 — the fix's payoff
    }

    /// <summary>Tech 2 advances the HULL floor(2/2) = 1 step to the successor
    /// (Hvy Scout 90, 250 HP) and resolves parts from ITS native preferred parts,
    /// advanced 2 steps each — so the gun becomes PW Gat Gun 3 (72) and the
    /// shield Sm Shield 3 (42). Survivability now changes with tech.</summary>
    [Fact]
    public void Tech2_FliesSuccessorHull_AndPartsAdvancedTwoSteps()
    {
        Assert.Equal((short)90, Catalog.EffectiveHullAtLevel(13, 2).HullId);   // Hvy Scout
        var tl = Catalog.NativePreferredLoadout(13, "Adv Scout", 2);
        Assert.Equal((short)90, tl.HullId);
        Assert.Equal("Hvy Scout", tl.HullName);
        Assert.Equal(250f, tl.HitPoints);
        Assert.Contains(tl.Parts, p => p.PartId == 72);   // PW Gat Gun 3 (advanced 2)
        Assert.Contains(tl.Parts, p => p.PartId == 42);   // Sm Shield 3 (advanced 2)
        Assert.DoesNotContain(tl.Parts, p => p.PartId == 33);   // base gun superseded
        Assert.DoesNotContain(tl.Parts, p => p.PartId == 21);   // base shield superseded
    }

    /// <summary>Tech 1 keeps the scenario hull (floor(1/2) = 0 hull steps) but
    /// still advances PARTS 1 step — PW Gat Gun 2 (71) and Sm Shield 2 (40). The
    /// hull gate (floor N/2) and the part gate (N) are independent.</summary>
    [Fact]
    public void Tech1_HullUnchanged_PartsAdvancedOneStep()
    {
        Assert.Equal((short)13, Catalog.EffectiveHullAtLevel(13, 1).HullId);   // hull unchanged
        var tl = Catalog.NativePreferredLoadout(13, "Adv Scout", 1);
        Assert.Equal((short)13, tl.HullId);
        Assert.Contains(tl.Parts, p => p.PartId == 71);   // PW Gat Gun 2
        Assert.Contains(tl.Parts, p => p.PartId == 40);   // Sm Shield 2
        Assert.DoesNotContain(tl.Parts, p => p.PartId == 33);
        Assert.DoesNotContain(tl.Parts, p => p.PartId == 21);
    }

    /// <summary>Tech 4 tries floor(4/2) = 2 hull steps, but Hvy Scout 90 has no
    /// successor (NA), so the chain END is respected and it stays Hvy Scout 90.
    /// Parts likewise clamp to their chain ends (PW Gat Gun 3 / Sm Shield 3) —
    /// so tech 4 == tech 2 for this short scout chain, never a fabricated step.</summary>
    [Fact]
    public void Tech4_ChainEndRespected_HullAndPartsClampToFinal()
    {
        Assert.Equal((short)90, Catalog.EffectiveHullAtLevel(13, 4).HullId);   // clamps at chain end
        var tl = Catalog.NativePreferredLoadout(13, "Adv Scout", 4);
        Assert.Equal((short)90, tl.HullId);
        Assert.Contains(tl.Parts, p => p.PartId == 72);   // gun chain end
        Assert.Contains(tl.Parts, p => p.PartId == 42);   // shield chain end
    }

    /// <summary>The tool's per-side REPORT field — mounted shield name +
    /// strength/regen — comes from the real part subtype: Sm Shield 1 = 150/2.0
    /// at tech 0, Sm Shield 3 = 225/3.75 at tech 2. This is the survivability
    /// number a showcase operator reads to see the tech effect.</summary>
    [Fact]
    public void ShieldStats_ReportsMountedShield_StrengthAndRegen()
    {
        var tl0 = Catalog.NativePreferredLoadout(13, "Adv Scout", 0);
        var tl2 = Catalog.NativePreferredLoadout(13, "Adv Scout", 2);
        var s0 = Catalog.ShieldStats(tl0.Parts);
        var s2 = Catalog.ShieldStats(tl2.Parts);
        Assert.NotNull(s0);
        Assert.NotNull(s2);
        Assert.Equal((short)21, s0!.Value.PartId);
        Assert.Equal("Sm Shield 1", s0.Value.Name);
        Assert.Equal(150f, s0.Value.Strength);
        Assert.Equal(2f, s0.Value.Regen);
        Assert.Equal((short)42, s2!.Value.PartId);
        Assert.Equal("Sm Shield 3", s2.Value.Name);
        Assert.Equal(225f, s2.Value.Strength);
        Assert.Equal(3.75f, s2.Value.Regen);
    }

    /// <summary>The TTK survivability aggregation is pure and fails on a broken
    /// mean: MeanOf returns the arithmetic mean over the recorded first-hit→death
    /// intervals (a single short kill drags a side's mean TTK down, a lone long
    /// one up), and null on an empty run (no kill had a recorded first hit).</summary>
    [Fact]
    public void MeanOf_ComputesArithmeticMean_OrNullWhenEmpty()
    {
        Assert.Equal(5.0, Catalog_MathCheck(3.0, 7.0, 5.0));
        Assert.Null(Catalog_MathCheck());
        Assert.Equal(12.0, Catalog_MathCheck(12.0));
    }

    // Thin indirection so the test reads plainly and stays tied to the real
    // P556 survivability seam (BotManager.MeanOf).
    private static double? Catalog_MathCheck(params double[] xs) => Boh.MeanOf(xs);
}
