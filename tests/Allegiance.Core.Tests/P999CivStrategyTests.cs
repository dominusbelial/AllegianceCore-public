using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Allegiance.Tools.CivStrategy;

namespace Allegiance.Core.Tests;

public sealed class P999CivStrategyTests
{
    private static IgcFile File() => IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc"));
    private static StaticCore Core() => StaticCore.Load(File());

    [SkippableTheory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(18)] [InlineData(25)] [InlineData(35)] [InlineData(101)] [InlineData(201)]
    public void Every_profile_is_reachable_in_catalog_and_actual_side_buy_gates(short civ)
    {
        Require.Artwork();
        var core = Core();
        for (int variant = 0; variant < 3; variant++)
        {
            var tree = new ResearchTree(core, civ);
            var result = CivStrategyScorer.Generate(tree, "PCore001", 999, variant);
            ResearchPlanCatalog.Validate(tree, "PCore001", result.Plan);
            var state = new ResearchState(tree);
            var mission = Mission.Assemble(core, MissionMap.FromLists(
                    [DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = 0 }], [], [], []),
                [new SideSpec(0, "P999", civ)], new NullIgcSite());
            var side = mission.Sides[0];
            short stationId = 1;
            void Station(short type) => mission.PlaceStation(new Station(
                DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
                { StationId = stationId++, SideId = 0, ClusterId = 0, Position = [0, 0, 0] },
                core.StationTypes[type]), mission.Sectors[0]);
            Station(tree.Civ.InitialStationTypeId);
            foreach (var step in result.Plan.Steps)
            {
                var node = tree.Find(step.Action, step.Id);
                Assert.True(state.CanApply(node), $"civ {civ}/{variant}: {step.Name}");
                var bucket = Assert.Single(side.Buckets, b => step.Action switch
                {
                    "develop" => b.Development?.DevelopmentId == step.Id,
                    "station" => b.StationType?.StationTypeId == step.Id,
                    "drone" => b.DroneType?.DroneTypeId == step.Id,
                    _ => false
                });
                Assert.True(side.CanBuy(bucket), $"civ {civ}/{variant}: actual side cannot buy {step.Action} {step.Name}");
                state.Apply(step);
                if (step.Action == "develop")
                { side.DevelopmentTechs = state.DevelopmentTechs; side.UpgradeStationsToSuccessors(); }
                if (step.Action == "station") Station(step.Id);
            }
            Assert.Equal(result.Plan.Steps.Count, result.Plan.Steps.Select(s => (s.Action, s.Id)).Distinct().Count());
            Assert.DoesNotContain(tree.Nodes, state.CanApply);
            Assert.Contains(result.Plan.Steps, s => s.Action == "develop");
            Assert.Contains(result.Plan.Steps, s => s.Action == "station");
            Assert.Contains(result.Plan.Steps, s => s.Action == "drone");
            Assert.All(result.Opportunities, o => Assert.True(double.IsFinite(o.Score)));
        }
    }

    [SkippableFact]
    public void Same_seed_repeats_exactly_and_seed_can_change_preferences_without_mutating_inputs()
    {
        Require.Artwork();
        var core = Core();
        string Snapshot() => JsonSerializer.Serialize(new { core.Civilizations, core.HullTypes, core.Developments, core.StationTypes });
        var before = Snapshot();
        var tree = new ResearchTree(core, 18);
        var first = CivStrategyScorer.Generate(tree, "PCore001", 999);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(CivStrategyScorer.Generate(tree, "PCore001", 999)));
        var second = CivStrategyScorer.Generate(tree, "PCore001", 1000);
        Assert.NotEqual(first.Opportunities.Select(o => o.Score), second.Opportunities.Select(o => o.Score));
        Assert.Equal(before, Snapshot());
    }

    [SkippableFact]
    public void Renamed_and_renumbered_modded_civilization_needs_no_known_identity()
    {
        Require.Artwork();
        var file = File();
        var body = file.Body.ToArray();
        foreach (var r in file.Records.Where(r => r.Type == IgcObjectType.Civilization))
        {
            var payload = body.AsSpan(r.PayloadOffset, r.Size);
            var id = BinaryPrimitives.ReadInt16LittleEndian(payload[262..]);
            BinaryPrimitives.WriteInt16LittleEndian(payload[262..], (short)(id + 400));
            payload.Slice(8, 25).Clear();
            Encoding.ASCII.GetBytes($"Modded {id}").CopyTo(payload[8..]);
        }
        var core = StaticCore.Load(new IgcFile { Wrapper = file.Wrapper, Version = file.Version,
            Body = body, Records = file.Records.Reverse().ToArray(), BodyLength = file.BodyLength, BytesConsumed = file.BytesConsumed });
        var result = CivStrategyScorer.Generate(new ResearchTree(core, 418), "Modded", 999, opponentCivs: [404, 405]);
        ResearchPlanCatalog.Validate(new ResearchTree(core, 418), "Modded", result.Plan);
        Assert.Equal(418, result.Plan.CivId);
        Assert.NotEmpty(result.Plan.Steps);
        var original = CivStrategyScorer.Generate(new ResearchTree(Core(), 18), "PCore001", 999, opponentCivs: [4, 5]);
        Assert.Equal(original.Plan.Steps.Select(s => (s.Action,s.Id)), result.Plan.Steps.Select(s => (s.Action,s.Id)));
        Assert.Equal(original.InitialPower, result.InitialPower);
    }

    [SkippableFact]
    public void Disabled_research_produces_no_fictional_plan_and_closed_paths_stay_closed()
    {
        Require.Artwork();
        var core = Core();
        var disabled = new ResearchTree(core, 4, new ResearchSettings { AllowDevelopments = false });
        Assert.Empty(CivStrategyScorer.Generate(disabled, "PCore001", 999).Plan.Steps);
        var tree = new ResearchTree(core, 4, new ResearchSettings
        { AllowExpansionPath = false, AllowSupremacyPath = false, AllowTacticalPath = false, AllowShipyardPath = false });
        var result = CivStrategyScorer.Generate(tree, "PCore001", 999);
        var permitted = tree.ReachableNodes().Select(n => (n.Kind,n.Id)).ToHashSet();
        Assert.All(result.Plan.Steps, s => Assert.Contains((s.Action,s.Id), permitted));
    }

    [SkippableFact]
    public void Development_time_perturbation_changes_costed_opportunities_from_data()
    {
        Require.Artwork();
        var file = File();
        var body = file.Body.ToArray();
        var r = file.Records.Single(r => r.Type == IgcObjectType.Civilization &&
            DataCivilizationIGC.Decode(body.AsSpan(r.PayloadOffset,r.Size)).CivilizationId == 18);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(r.PayloadOffset + 160 + 24*4), 2);
        var modded = StaticCore.Load(new IgcFile { Wrapper = file.Wrapper, Version = file.Version,
            Body = body, Records = file.Records, BodyLength = file.BodyLength, BytesConsumed = file.BytesConsumed });
        var baseline = CivStrategyScorer.Generate(new ResearchTree(Core(),18), "PCore001",999);
        var changed = CivStrategyScorer.Generate(new ResearchTree(modded,18), "PCore001",999);
        var direct = baseline.Opportunities.First(o => o.Kind == "develop" && o.Steps == 1 && o.Seconds > 0);
        var counterpart = changed.Opportunities.Single(o => o.Kind == direct.Kind && o.Id == direct.Id);
        Assert.Equal(direct.Seconds * 2, counterpart.Seconds);
        Assert.NotEqual(direct.Score, counterpart.Score);
    }

    [SkippableFact]
    public void Opponent_defense_changes_reweight_power_without_an_identity_rule()
    {
        Require.Artwork();
        var file = File();
        var body = file.Body.ToArray();
        var r = file.Records.Single(r => r.Type == IgcObjectType.HullType &&
            DataHullTypeIGC.Decode(body.AsSpan(r.PayloadOffset,r.Size)).HullId == 190);
        body[r.PayloadOffset + 458] = 10;
        var modded = StaticCore.Load(new IgcFile { Wrapper = file.Wrapper, Version = file.Version,
            Body = body, Records = file.Records, BodyLength = file.BodyLength, BytesConsumed = file.BytesConsumed });
        var original = CivStrategyScorer.Generate(new ResearchTree(Core(),18),"PCore001",999,opponentCivs:[4]);
        var changed = CivStrategyScorer.Generate(new ResearchTree(modded,18),"PCore001",999,opponentCivs:[4]);
        Assert.Contains((short)10,changed.OpponentDefenses);
        Assert.NotEqual(original.InitialPower.Skirmish,changed.InitialPower.Skirmish);
    }
}
