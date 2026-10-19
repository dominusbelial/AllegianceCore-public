using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.Tests;

public sealed class SC5CloakResearchTests(ITestOutputHelper output)
{
    [SkippableTheory]
    [InlineData((short)25, "plan_a", 20)]
    [InlineData((short)25, "plan_b", 19)]
    [InlineData((short)3, "plan_a", 23)]
    [InlineData((short)3, "plan_b", 16)]
    public void Rig_plans_reach_and_buy_cloaks_early_and_keep_them_at_full_research(short civ, string name, int prefix)
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        var tree = new ResearchTree(core, civ);
        var plan = ResearchPlanCatalog.Read(Path.Combine(AppContext.BaseDirectory,
            "showcase-plans", "PCore001", $"civ{civ}-{name}.json"));
        ResearchPlanCatalog.Validate(tree, "PCore001", plan);
        var state = new ResearchState(tree);
        short home = tree.Civ.InitialStationTypeId;
        var target = core.StationTypes[core.Civilizations[(short)(civ == 25 ? 3 : 25)].InitialStationTypeId];
        Assert.Contains(state.Unlocks, p => p.Kind == "part" && p.Id == 38); // Sig Cloak 1 is already researched.
        var starting = TechLoadoutSelector.Select(core, civ, Showcase.HullRole.Scout, state.Techs, home);
        Assert.NotNull(starting);
        Assert.DoesNotContain(starting.Parts, p => TechLoadoutSelector.Part(core, p.PartId).Type is DataPartTypeIGC.Cloak);
        Assert.Null(TechLoadoutSelector.Select(core, civ, Showcase.HullRole.Bomber, state.Techs, home));

        double serialSeconds = 0;
        for (int i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            state.Apply(step);
            serialSeconds += tree.Find(step.Action, step.Id).Seconds / 8d;
            if (i + 1 != prefix && i + 1 != plan.Steps.Count) continue;
            if (i + 1 == prefix) Assert.InRange(serialSeconds, 1, 870);
            var layer = BotManager.PickTechLoadout(new(civ, home, state.Techs, 1_000_000),
                Showcase.HullRole.Fighter, defensiveMines: true);
            Assert.NotNull(layer);
            Assert.Contains(layer.Parts, p => Showcase.IsMineLauncher(core, p.PartId));
            foreach (var role in new[] { Showcase.HullRole.Scout, Showcase.HullRole.Bomber })
            {
                var selected = TechLoadoutSelector.Select(core, civ, role, state.Techs, home, target, budget: 1_000_000);
                Assert.NotNull(selected);
                Assert.Equal(role, Showcase.ClassifyHull(core.HullTypes[selected.HullId]));
                Assert.True(TechLoadoutSelector.Legal(core, civ, state.Techs, home, selected.HullId, selected.Parts), selected.Reason);
                var cloak = Assert.Single(selected.Parts, p => TechLoadoutSelector.Part(core, p.PartId).Type is DataPartTypeIGC.Cloak);
                Assert.Equal(0, cloak.MountId);
                Assert.True(core.HullTypes[selected.HullId].CanMountPart(TechLoadoutSelector.Part(core, cloak.PartId), 0));
                Assert.InRange(selected.Price, 0, 1_000_000);
                Assert.Contains(selected.Parts, p => TechLoadoutSelector.Part(core, p.PartId).Type is DataPartTypeIGC.Weapon);
                var repeat = TechLoadoutSelector.Select(core, civ, role, state.Techs, home, target, budget: 1_000_000)!;
                Assert.Equal(selected.HullId, repeat.HullId);
                Assert.Equal(selected.Parts, repeat.Parts);
                Assert.Null(TechLoadoutSelector.Select(core, civ, role, state.Techs, home, target, budget: -1));
                if (role == Showcase.HullRole.Bomber)
                {
                    Assert.NotNull(selected.Strike);
                    Assert.True(selected.Strike.Volley.Hull > 0);
                }
                output.WriteLine($"civ={civ} plan={name} step={i + 1} serialBucketSecondsAt8={serialSeconds}: {selected.Reason}");
            }
        }
    }
}
