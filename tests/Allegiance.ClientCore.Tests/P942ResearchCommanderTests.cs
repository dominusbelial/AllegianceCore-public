using Allegiance.Core.Model;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

public sealed class P942ResearchCommanderTests
{
    [SkippableTheory]
    [InlineData("authored", false)]
    [InlineData("generated", true)]
    public void Authored_order_and_custom_plans_named_generated_keep_their_explicit_production_step(string name, bool truncate)
    {
        Require.Artwork();
        var tree = new ResearchTree(Catalog.RepresentativeCore(), 18);
        var plan = tree.Generate("PCore001", name);
        if (truncate) plan.Steps = plan.Steps.Take(2).ToList();
        var initial = tree.Settings.StartingTechs(tree.Civ);
        var commander = new ResearchCommander(0, "p942-authored", tree, plan, initial, []);
        var first = plan.Steps[0];
        var world = new ResearchCommander.Snapshot(1, true, 1_000_000, initial,
            [tree.Civ.InitialStationTypeId], [], [], [], new Dictionary<short, ResearchCommander.Funding>());
        commander.Tick(world);
        world = world with { Time = 2, CompletedDevelopments = [first.Id],
            Developments = initial.Or(TechTreeBitMask.FromBytes(tree.Core.Developments[first.Id].TtbmEffects)) };
        commander.Tick(world); commander.Tick(world with { Time = 3 });
        Assert.Equal("drone", commander.Telemetry!.Action);
        Assert.Equal(plan.Steps[1].Name, commander.Telemetry.Name);
    }

    [SkippableTheory]
    [InlineData(18)]
    [InlineData(4)]
    [InlineData(5)]
    public void Generated_commander_funds_buyable_research_before_optional_production(short civ)
    {
        Require.Artwork();
        var tree = new ResearchTree(Catalog.RepresentativeCore(), civ);
        var plan = tree.Generate("PCore001");
        var initial = tree.Settings.StartingTechs(tree.Civ);
        var commander = new ResearchCommander(0, "p942", tree, plan, initial, []);
        var world = new ResearchCommander.Snapshot(1, true, 1_000_000, initial,
            [tree.Civ.InitialStationTypeId], [], [], [], new Dictionary<short, ResearchCommander.Funding>());
        var first = plan.Steps[0];
        Assert.Equal("develop", first.Action);
        commander.Tick(world);
        world = world with { Time = 2, CompletedDevelopments = [first.Id],
            Developments = initial.Or(TechTreeBitMask.FromBytes(tree.Core.Developments[first.Id].TtbmEffects)) };
        commander.Tick(world);
        var commands = commander.Tick(world with { Time = 3 });
        Assert.Equal("develop", commander.Telemetry!.Action);
        var buckets = BucketCatalog.Describe(tree.Core, initial, GlobalAttributeSet.FromArray(tree.Civ.GasBaseAttributes));
        var funded = Assert.Single(commands, c => c.Kind == "donate");
        Assert.Equal(BucketItemType.BitDevelopment, buckets.Single(b => b.Id == funded.Id).ItemType);
    }
}
