using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

[Trait("Requires", "Artwork")]
public sealed class P821RoleResolutionTests
{
    [SkippableFact]
    public void Every_civ_degrades_by_requested_duty_scores_with_legal_affordable_loadouts()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        foreach (var civ in core.Civilizations.Values)
        {
            var tree = new ResearchTree(core, civ.CivilizationId);
            var state = new ResearchState(tree);
            short dock = civ.InitialStationTypeId;
            foreach (var role in Enum.GetValues<Showcase.HullRole>())
            {
                var exact = TechLoadoutSelector.Select(core, civ.CivilizationId, role, state.Techs, dock);
                var picked = BotManager.PickTechLoadout(new(civ.CivilizationId, dock, state.Techs, 1_000_000), role);
                Assert.NotNull(picked);
                Assert.True(TechLoadoutSelector.Legal(core, civ.CivilizationId, state.Techs, dock, picked.HullId, picked.Parts), picked.Reason);
                Assert.InRange(picked.Price, 0, 1_000_000);
                if (exact is not null) { Assert.Equal(exact.HullId, picked.HullId); continue; }
                var duty = role is Showcase.HullRole.Bomber or Showcase.HullRole.Heavy ? Showcase.HullRole.Fighter : role;
                // Independent oracle: at starting tech each small-craft class
                // has one legal hull. Compare their actual loadout candidates,
                // scored for the requested duty, never label-order precedence.
                var candidates = new[] { Showcase.HullRole.Fighter, Showcase.HullRole.Scout, Showcase.HullRole.Interceptor }
                    .Select(r => TechLoadoutSelector.Select(core, civ.CivilizationId, r, state.Techs, dock))
                    .Where(s => s is not null).Select(s => core.HullTypes[s!.HullId]).ToArray();
                // P821b: a combat duty considers Scouts only when no combat-class hull is legal.
                var combat = candidates.Where(h => TechLoadoutSelector.Classify(h) is not Showcase.HullRole.Scout).ToArray();
                if (duty != Showcase.HullRole.Scout && combat.Length > 0) candidates = combat;
                double Score(DataHullTypeIGC h) => duty switch
                {
                    Showcase.HullRole.Scout => h.ScannerRange * h.Speed,
                    Showcase.HullRole.Interceptor => h.HitPoints * h.Speed,
                    _ => h.HitPoints * Math.Max(1, (int)h.MaxFixedWeapons)
                };
                Assert.Equal(candidates.Max(Score), Score(core.HullTypes[picked.HullId]));
                Assert.Contains($"nearest duty={duty}", picked.Reason);
                Assert.Null(BotManager.PickTechLoadout(new(civ.CivilizationId, dock, state.Techs, -1), role));
                Assert.Null(BotManager.PickTechLoadout(new(civ.CivilizationId, dock, state.Techs, 1_000_000), role, allowRoleFallback: false));
            }
        }
    }

    [SkippableFact]
    public void Missing_fighter_uses_interceptor_strength_and_varied_fallback_stays_on_combat_hulls()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        var state = new ResearchState(new(core, 25));
        short dock = state.Tree.Civ.InitialStationTypeId;
        var best = BotManager.PickTechLoadout(new(25, dock, state.Techs, 1_000_000), Showcase.HullRole.Fighter)!;
        Assert.Equal(Showcase.HullRole.Interceptor, TechLoadoutSelector.Classify(core.HullTypes[best.HullId]));
        // No global variety state: exercise a deterministic local rotation.
        var hullUse = new Dictionary<short, int>();
        var partUse = new Dictionary<short, int>();
        var seen = new HashSet<short>();
        for (int ordinal = 0; ordinal < 20; ordinal++)
        {
            var variety = new TechLoadoutSelector.VarietyState(0, 821, ordinal, hullUse, partUse);
            var a = TechLoadoutSelector.SelectVaried(core, 25, Showcase.HullRole.Fighter, state.Techs, dock, variety,
                preferMines: true, fallbackPool: TechLoadoutSelector.FallbackPool.Combat)!;
            var b = TechLoadoutSelector.SelectVaried(core, 25, Showcase.HullRole.Fighter, state.Techs, dock, variety,
                preferMines: true, fallbackPool: TechLoadoutSelector.FallbackPool.Combat)!;
            Assert.Equal(a.HullId, b.HullId); Assert.Equal(a.Parts, b.Parts);
            Assert.True(TechLoadoutSelector.Legal(core, 25, state.Techs, dock, a.HullId, a.Parts));
            seen.Add(a.HullId);
            hullUse[a.HullId] = hullUse.GetValueOrDefault(a.HullId) + 1;
            foreach (var p in a.Parts) partUse[p.PartId] = partUse.GetValueOrDefault(p.PartId) + 1;
        }
        // Iron Coalition's only combat-class hull at start is its Lt Interceptor; the
        // rotation must not put fighter duty on the Scout (P821b).
        Assert.Equal(best.HullId, Assert.Single(seen));
    }

    [SkippableFact]
    public void Missing_combat_roles_never_fly_a_scout_while_a_combat_hull_is_legal()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        foreach (var civ in core.Civilizations.Values)
        {
            var state = new ResearchState(new(core, civ.CivilizationId));
            short dock = civ.InitialStationTypeId;
            bool combatLegal = new[] { Showcase.HullRole.Fighter, Showcase.HullRole.Interceptor }
                .Any(r => TechLoadoutSelector.Select(core, civ.CivilizationId, r, state.Techs, dock) is not null);
            if (!combatLegal) continue;
            foreach (var role in new[] { Showcase.HullRole.Fighter, Showcase.HullRole.Interceptor, Showcase.HullRole.Bomber, Showcase.HullRole.Heavy })
            {
                var best = BotManager.PickTechLoadout(new(civ.CivilizationId, dock, state.Techs, 1_000_000), role)!;
                Assert.NotEqual(Showcase.HullRole.Scout, TechLoadoutSelector.Classify(core.HullTypes[best.HullId]));
                for (int ordinal = 0; ordinal < 12; ordinal++)
                {
                    var variety = new TechLoadoutSelector.VarietyState(0, 821, ordinal, new Dictionary<short, int>(), new Dictionary<short, int>());
                    foreach (var pool in TechLoadoutSelector.FallbackPools(TechLoadoutSelector.FallbackDuty(role)))
                    {
                        if (TechLoadoutSelector.SelectVaried(core, civ.CivilizationId, TechLoadoutSelector.FallbackDuty(role),
                                state.Techs, dock, variety, preferMines: true, fallbackPool: pool) is not { } v) continue;
                        Assert.NotEqual(Showcase.HullRole.Scout, TechLoadoutSelector.Classify(core.HullTypes[v.HullId]));
                        break;
                    }
                }
            }
        }
    }

    [SkippableTheory]
    [InlineData((short)25, "Fighter")]
    [InlineData((short)3, "Interceptor")]
    public void Newly_buyable_exact_role_supersedes_fallback_at_next_purchase(short civ, string roleName)
    {
        Require.Artwork();
        var role = Enum.Parse<Showcase.HullRole>(roleName);
        var core = Catalog.RepresentativeCore();
        var state = new ResearchState(new(core, civ));
        Assert.Null(TechLoadoutSelector.Select(core, civ, role, state.Techs, state.Tree.Civ.InitialStationTypeId));
        var plan = ResearchPlanCatalog.Read(Path.Combine(AppContext.BaseDirectory, "showcase-plans", "PCore001", $"civ{civ}-plan_a.json"));
        foreach (var step in plan.Steps)
        {
            state.Apply(step);
            foreach (short dock in state.Stations)
            {
                var exact = TechLoadoutSelector.Select(core, civ, role, state.Techs, dock);
                if (exact is null) continue;
                var picked = BotManager.PickTechLoadout(new(civ, dock, state.Techs, 1_000_000), role)!;
                Assert.Equal(exact.HullId, picked.HullId);
                Assert.DoesNotContain("temporary", picked.Reason);
                return;
            }
        }
        Assert.Fail("The real authored plan never unlocked the requested role");
    }
}
