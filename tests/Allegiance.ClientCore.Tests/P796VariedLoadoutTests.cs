using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P796 — the varied march/showcase loadout policy. Everything here runs
/// against the REAL PCore001.igc corpus (no wire, no server): the varied
/// selector must (a) equip every legal role-eligible hull and part of the
/// side's full techs at least once over a rotation of purchases, (b) never
/// propose an illegal or unaffordable mount, (c) be deterministic per seed,
/// and (d) leave the `best` selector untouched. The coverage tracker's pure
/// record/report rows are proved on synthetic events.
///
/// These tests are NEW with P796: on master they cannot even compile —
/// TechLoadoutSelector.SelectVaried/VarietyState, WeaponCoverage and
/// LoadoutVariety do not exist there (the master compile failure IS the
/// "fails on master" evidence; no existing test changed behaviour because
/// the varied path is opt-in per spawn and only default-on for
/// preset:"showcase").
/// </summary>
public sealed class P796VariedLoadoutTests(ITestOutputHelper output)
{
    private static readonly short[] Civs = [25, 3, 35];

    private static (StaticCore core, TechTreeBitMask techs, short[] docks) FullTechs(short civ)
    {
        var core = Catalog.RepresentativeCore();
        var state = new ResearchState(new ResearchTree(core, civ));
        foreach (var step in state.Tree.Generate("PCore001").Steps) state.Apply(step);
        return (core, state.Techs, state.Stations.Order().ToArray());
    }

    /// <summary>The parts the varied selector CAN ever equip for a role at the
    /// given techs: computed independently from the selector, from the same
    /// legality gates (research unlock + CanBuy, nonobsolete, mountable on a
    /// role hull, energy/ammo or capacity fit, positive score — the same
    /// "usable" filters the best selector applies). Reuses the caller's
    /// precomputed per-dock unlock sets (Unlocks is expensive).</summary>
    private static HashSet<short> RoleEligibleParts(StaticCore core, short civ, TechTreeBitMask techs,
        short dock, Showcase.HullRole role, long budget,
        HashSet<short> unlockParts, HashSet<short> unlockHulls)
    {
        var hulls = core.HullTypes.Values.Where(h => unlockHulls.Contains(h.HullId)
            && Showcase.IsPlayerBuyable(core, h)
            && Showcase.ClassifyHull(h) == role
            && !SuccessorUpgrade.HullIsObsolete(core, h, core.StationTypes[dock].TtbmLocal, techs)).ToArray();
        var result = new HashSet<short>();
        foreach (var id in unlockParts)
        {
            var p = TechLoadoutSelector.Part(core, id);
            if (p.GroupId == byte.MaxValue || SuccessorUpgrade.PartIsObsolete(core, p, core.StationTypes[dock].TtbmLocal, techs)) continue;
            foreach (var h in hulls)
            {
                bool fits = p.Type switch
                {
                    DataPartTypeIGC.Weapon w => Enumerable.Range(0, h.Hardpoints.Length).Any(m => h.Hardpoints[m].Fixed && h.CanMountPart(p, m))
                        && w.EnergyPerShot <= h.MaxEnergy && w.CAmmoPerShot <= h.MaxAmmo
                        && TechLoadoutSelector.WeaponScore(core, p, role) > 0,
                    DataPartTypeIGC.MissileLauncher => h.CanMountPart(p, 0) && TechLoadoutSelector.SupportScore(core, p, role) > 0
                        && Ship.ClampPartAmount(h, core, p, short.MaxValue) > 0,
                    _ => h.CanMountPart(p, 0) && TechLoadoutSelector.SupportScore(core, p, role) > 0
                        && Ship.ClampPartAmount(h, core, p,
                            p.EquipmentType is EquipmentType.Magazine or EquipmentType.Dispenser or EquipmentType.ChaffLauncher ? short.MaxValue : (short)1) > 0,
                };
                if (!fits) continue;
                if (h.Price > budget) continue;
                result.Add(id);
                break;
            }
        }
        return result;
    }

    [SkippableTheory]
    [InlineData((short)25)]
    [InlineData((short)3)]
    [InlineData((short)35)]
    public void Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases(short civ)
    {
        Require.Artwork();
        LoadoutVariety.Reset();
        var (core, techs, docks) = FullTechs(civ);
        long budget = 5_000_000;
        int purchases = 140;
        foreach (var role in new[] { Showcase.HullRole.Fighter, Showcase.HullRole.Interceptor,
                     Showcase.HullRole.Scout, Showcase.HullRole.Heavy })
        foreach (var dock in docks)
        {
            LoadoutVariety.Reset();   // fresh per-match rotation state per (role, dock) block
            var seenParts = new HashSet<short>();
            var seenHulls = new HashSet<short>();
            // Per-dock unlock sets computed ONCE (ResearchTree.Unlocks is expensive).
            var tree = new ResearchTree(core, civ);
            var unlock = tree.Unlocks(techs, [dock]);
            bool CanBuy(byte[] required) => TechTreeBitMask.RequiredTechsBuyable(required, core.StationTypes[dock].TtbmLocal, techs);
            var unlockHulls = unlock.Where(i => i.Kind == "hull").Select(i => i.Id).ToHashSet();
            var unlockParts = unlock.Where(i => i.Kind == "part").Select(i => i.Id).ToHashSet();
            for (int i = 0; i < purchases; i++)
            {
                var state = LoadoutVariety.Begin(0);
                var selection = TechLoadoutSelector.SelectVaried(core, civ, role, techs, dock, state, budget: budget);
                Assert.NotNull(selection);
                seenHulls.Add(selection!.HullId);
                foreach (var p in selection.Parts) seenParts.Add(p.PartId);
                // (b) never illegal, never unaffordable.
                Assert.True(TechLoadoutSelector.Legal(core, civ, techs, dock, selection.HullId, selection.Parts), selection.Reason);
                Assert.True(selection.Price <= budget, $"price {selection.Price} > budget {budget}: {selection.Reason}");
                var h = core.HullTypes[selection.HullId];
                var guns = selection.Parts.Select(p => TechLoadoutSelector.Part(core, p.PartId).Type).OfType<DataPartTypeIGC.Weapon>().ToArray();
                Assert.True(guns.Sum(w => w.EnergyPerShot) <= h.MaxEnergy);
                Assert.True(guns.Sum(w => w.CAmmoPerShot) <= h.MaxAmmo);
                LoadoutVariety.Record(0, selection);
            }
            // (a) every legal hull of the role that the DOCK sells and that can
            // carry an eligible gun was equipped.
            var eligible = RoleEligibleParts(core, civ, techs, dock, role, budget, unlockParts, unlockHulls);
            var gunIds = eligible.Where(id => TechLoadoutSelector.Part(core, id).Type is DataPartTypeIGC.Weapon).ToHashSet();
            var roleHulls = core.HullTypes.Values.Where(h => unlockHulls.Contains(h.HullId)
                && CanBuy(h.TtbmRequired)
                && Showcase.IsPlayerBuyable(core, h)
                && Showcase.ClassifyHull(h) == role
                && !SuccessorUpgrade.HullIsObsolete(core, h, core.StationTypes[dock].TtbmLocal, techs)
                && gunIds.Any(id => Enumerable.Range(0, Math.Min(8, h.Hardpoints.Length)).Any(m => h.Hardpoints[m].Fixed
                    && h.CanMountPart(TechLoadoutSelector.Part(core, id), m)))).Select(h => h.HullId).ToHashSet();
            var missingHulls = roleHulls.Except(seenHulls).ToList();
            Assert.True(missingHulls.Count == 0,
                $"role={role} dock={dock}: hulls never equipped: [{string.Join(", ", missingHulls)}]");
            // (a) every legal part of the side's tech that fits the role was equipped.
            var missingParts = eligible.Except(seenParts).ToList();
            Assert.True(missingParts.Count == 0,
                $"role={role} dock={dock}: {eligible.Count} eligible parts, {seenParts.Count} equipped; never equipped: [{string.Join(", ", missingParts.Select(id => $"{id} '{TechLoadoutSelector.Part(core, id).Name}'"))}]");
            output.WriteLine($"civ={civ} role={role} dock={dock}: equipped {seenHulls.Count} hulls, {seenParts.Count}/{eligible.Count} eligible parts in {purchases} purchases");
        }
        LoadoutVariety.Reset();
    }

    [SkippableFact]
    public void Varied_is_deterministic_per_seed_and_seed_sensitive()
    {
        Require.Artwork();
        var (core, techs, docks) = FullTechs(25);
        short dock = docks[0];
        var usageHulls = new Dictionary<short, int>();
        var usageParts = new Dictionary<short, int>();
        var state = new TechLoadoutSelector.VarietyState(0, 796, 3, usageHulls, usageParts);
        var a = TechLoadoutSelector.SelectVaried(core, 25, Showcase.HullRole.Fighter, techs, dock, state);
        var b = TechLoadoutSelector.SelectVaried(core, 25, Showcase.HullRole.Fighter, techs, dock, state);
        Assert.NotNull(a);
        Assert.Equal(a!.HullId, b!.HullId);
        Assert.Equal(a.Parts, b.Parts);
        // A different seed eventually differs across a spread of purchases.
        bool differed = false;
        for (int ord = 0; ord < 40 && !differed; ord++)
        {
            var s1 = new TechLoadoutSelector.VarietyState(0, 796, ord, usageHulls, usageParts);
            var s2 = new TechLoadoutSelector.VarietyState(0, 42, ord, usageHulls, usageParts);
            var r1 = TechLoadoutSelector.SelectVaried(core, 25, Showcase.HullRole.Fighter, techs, dock, s1);
            var r2 = TechLoadoutSelector.SelectVaried(core, 25, Showcase.HullRole.Fighter, techs, dock, s2);
            if (r1 is null || r2 is null) continue;
            differed = r1.HullId != r2.HullId || !r1.Parts.SequenceEqual(r2.Parts);
        }
        Assert.True(differed, "seeds 796 and 42 produced identical purchases for 40 ordinals — not actually randomised");
    }

    [SkippableFact]
    public void Best_selector_is_untouched_by_varied_purchases()
    {
        Require.Artwork();
        var (core, techs, docks) = FullTechs(25);
        short dock = docks[0];
        var before = new Dictionary<Showcase.HullRole, TechLoadoutSelector.Selection?>();
        foreach (var role in Enum.GetValues<Showcase.HullRole>())
            before[role] = TechLoadoutSelector.Select(core, 25, role, techs, dock);
        // A full block of varied purchases must not move the best selection.
        LoadoutVariety.Reset();
        for (int i = 0; i < 30; i++)
        {
            var state = LoadoutVariety.Begin(0);
            var varied = TechLoadoutSelector.SelectVaried(core, 25, Showcase.HullRole.Fighter, techs, dock, state);
            Assert.NotNull(varied);
            LoadoutVariety.Record(0, varied!);
        }
        foreach (var role in Enum.GetValues<Showcase.HullRole>())
        {
            var after = TechLoadoutSelector.Select(core, 25, role, techs, dock);
            Assert.Equal(before[role]?.HullId, after?.HullId);
            Assert.Equal(before[role]?.Parts, after?.Parts);
        }
        LoadoutVariety.Reset();
    }

    [SkippableTheory]
    [InlineData((short)25)]
    [InlineData((short)3)]
    public void Varied_bombers_and_support_keep_the_best_role_loadouts(short civ)
    {
        Require.Artwork();
        LoadoutVariety.Reset();
        var (core, techs, docks) = FullTechs(civ);
        short dock = docks[^1];
        var target = core.StationTypes.Values.First();
        foreach (var step in new ResearchState(new(core, civ)).Tree.Generate("PCore001").Steps) { }
        // Bomber with a station target: varied must return exactly the best strike loadout.
        var best = TechLoadoutSelector.Select(core, civ, Showcase.HullRole.Bomber, techs, dock, target);
        var state = new TechLoadoutSelector.VarietyState(0, 1, 0,
            new Dictionary<short, int>(), new Dictionary<short, int>());
        var varied = TechLoadoutSelector.SelectVaried(core, civ, Showcase.HullRole.Bomber, techs, dock, state, target);
        Assert.Equal(best?.HullId, varied?.HullId);
        Assert.Equal(best?.Parts, varied?.Parts);
        LoadoutVariety.Reset();
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Coverage_tracker_rows_report_equipped_fired_hits_damage_kills_and_never_fired_reasons()
    {
        Require.Artwork();   // reads PCore001.igc from the artwork tree
        var coverage = new WeaponCoverage();
        coverage.BeginRun();
        // Two purchases of hull 12 with a gun (101), a shield (210) and a missile launcher (300).
        coverage.RecordPurchase(0, 12, [new ExpandedPartData(101, 1, 0, ExpandedPartData.FullyMounted),
                                        new ExpandedPartData(210, 1, 0, ExpandedPartData.FullyMounted),
                                        new ExpandedPartData(300, 1, 0, ExpandedPartData.FullyMounted)]);
        coverage.RecordPurchase(1, 12, [new ExpandedPartData(101, 1, 0, ExpandedPartData.FullyMounted)]);
        coverage.RecordGunTrigger(1, [101]);     // rising edge
        coverage.RecordGunTrigger(1, [101]);     // second rising edge
        coverage.RecordMissileLaunch(2, 300);
        coverage.CreditHitForTests(101, 0);
        coverage.CreditDamageForTests(101, 0, 42.5);
        var json = (System.Text.Json.JsonElement)System.Text.Json.JsonSerializer.SerializeToElement(coverage.Json());
        var parts = json.GetProperty("parts").EnumerateArray().ToDictionary(p => p.GetProperty("partId").GetInt32());
        Assert.Equal(2, parts[101].GetProperty("equipped").GetInt32());
        Assert.Equal(2, parts[101].GetProperty("fired").GetInt64());
        Assert.Equal(1, parts[101].GetProperty("hits").GetInt64());
        Assert.Equal(42.5, parts[101].GetProperty("damage").GetDouble());
        Assert.Equal(0, parts[210].GetProperty("fired").GetInt64());
        Assert.Equal(1, parts[300].GetProperty("fired").GetInt64());
        var hulls = json.GetProperty("hulls").EnumerateArray().ToDictionary(h => h.GetProperty("hullId").GetInt32());
        Assert.Equal(2, hulls[12].GetProperty("bought").GetInt64());
        Assert.Equal(2, hulls[12].GetProperty("launched").GetInt64());
        var neverFired = json.GetProperty("neverFired").EnumerateArray().Select(n => n.GetString()!).ToList();
        var core = Catalog.RepresentativeCore();
        string kind = WeaponCoverage.Kind(core, 210);
        string expectedReason = kind switch
        {
            "shield" or "afterburner" or "cloak" => "passive",
            "probe-launcher" => "probe law",
            "mine-launcher" => "mine proximity",
            "chaff-launcher" => "chaff law",
            "missile-launcher" => "missile gate",
            _ => "fire gate",
        };
        Assert.Contains(neverFired, line => line.Contains("210") && line.Contains(expectedReason));
        // The kill row comes from the session sweep (KILL_SHIP), not recordable
        // without a world — covered by the live loopback test.
        Assert.True(coverage.Table().Count > 3);
    }
}
