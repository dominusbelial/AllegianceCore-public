using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

public sealed class P942ResearchOrderingTests
{
    [SkippableTheory]
    [InlineData(18)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(25)]
    [InlineData(35)]
    [InlineData(101)]
    [InlineData(201)]
    public void Generated_research_never_waits_for_a_drone_or_utility_base_while_a_development_is_buyable(short civ)
    {
        Require.Artwork();
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
        var tree = new ResearchTree(core, civ);
        var state = new ResearchState(tree);
        var plan = tree.Generate("PCore001", prioritizeResearch: true);
        var mission = Mission.Assemble(core, MissionMap.FromLists(
                [DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = 0 }], [], [], []),
            [new SideSpec(0, "P942", civ)], new NullIgcSite());
        short stationId = 1;
        void AddStation(short type) => mission.PlaceStation(new Station(
            DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
            { StationId = stationId++, SideId = 0, ClusterId = 0, Position = [0, 0, 0] }, core.StationTypes[type]), mission.Sectors[0]);
        AddStation(tree.Civ.InitialStationTypeId);
        var side = mission.Sides[0];
        foreach (var step in plan.Steps)
        {
            var available = tree.Nodes.Where(state.CanApply).ToArray();
            if (available.Any(n => n.Kind == "develop"))
                Assert.True(step.Action == "develop", $"civ {civ}: {step.Action} {step.Name} blocks a buyable development");
            var bucket = Assert.Single(side.Buckets, b => step.Action switch
            {
                "develop" => b.Development?.DevelopmentId == step.Id,
                "station" => b.StationType?.StationTypeId == step.Id,
                "drone" => b.DroneType?.DroneTypeId == step.Id,
                _ => false,
            });
            // Native sideigc.h:224-280: exercise the actual server buy gate,
            // including individual local masks and in-place successor upgrades.
            Assert.True(side.CanBuy(bucket), $"civ {civ}: server cannot buy {step.Action} {step.Name}");
            state.Apply(step);
            if (step.Action == "develop")
            { side.DevelopmentTechs = state.DevelopmentTechs; side.UpgradeStationsToSuccessors(); }
            if (step.Action == "station") AddStation(step.Id);
        }
        ResearchPlanCatalog.Validate(tree, "PCore001", plan);
        Assert.Contains(plan.Steps, s => s.Action == "drone");
        Assert.Contains(plan.Steps, s => s.Action == "station");
        // Exhaustiveness survives scheduling: every remaining development is
        // either unavailable in this own-civ economy or obsolete, never skipped.
        Assert.DoesNotContain(tree.Nodes, n => n.Kind == "develop" && state.CanApply(n));
    }
}
