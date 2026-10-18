using Allegiance.Core.MapGen;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;
using Cfg = Allegiance.Server.ServerConfig;
using Game = Allegiance.Server.GameConfig;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class P702ResearchTests
{
    private static StaticCore Load() => StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AllegianceCore.slnx"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("repository root absent");
        }
    }

    [SkippableFact]
    public void Stock_tree_has_four_paths_garrison_successors_and_scaled_research()
    {
        Require.Artwork();
        var core = Load();
        Assert.Equal(10, core.Civilizations.Count);
        foreach (var id in new short[] { 25, 3 })
        {
            var tree = new ResearchTree(core, id);
            var plan = tree.Generate("PCore001");
            var stations = plan.Steps.Where(s => s.Action == "station").Select(s => s.Name).ToArray();
            foreach (var branch in new[] { "Supremacy", "Tactical", "Expansion", "Shipyard", "Garrison (Str)" })
                Assert.Contains(branch, stations);
            Assert.Contains(plan.Steps, s => s.Action == "drone");
            Assert.Contains(plan.Steps, s => s.Name == "Supremacy (Adv)");
            var baseType = core.StationTypes[tree.Civ.InitialStationTypeId];
            Assert.True(baseType.SuccessorStationTypeId >= 0);
            var state = new ResearchState(tree);
            Assert.Contains(state.Unlocks, u => u.Kind == "hull");
            Assert.Contains(state.Unlocks, u => u.Kind == "part" && u.Role == "Weapon");
            foreach (var n in tree.Nodes.Where(n => n.Kind == "develop"))
            {
                var dev = core.Developments[n.Id];
                Assert.Equal((int)(dev.Price * tree.Civ.GasBaseAttributes[GlobalAttributeSet.DevelopmentCost]), n.Price);
                Assert.Equal((uint)(dev.TimeToBuild * tree.Civ.GasBaseAttributes[GlobalAttributeSet.DevelopmentTime]), n.Seconds);
            }
            var disabled = new ResearchTree(core, id, new ResearchSettings
            {
                AllowShipyardPath = false, AllowExpansionPath = false,
                AllowTacticalPath = false, AllowSupremacyPath = false,
            }).Generate("PCore001");
            Assert.DoesNotContain(disabled.Steps, s => s.Action == "station" && new[] { "Shipyard", "Supremacy", "Tactical", "Expansion" }.Contains(s.Name));
        }
    }

    [SkippableFact]
    public void Mission_seeds_native_bits_before_catalog_and_no_development_mode_uses_no_dev_mask()
    {
        Require.Artwork();
        var core = Load();
        var civ = core.Civilizations.Values.First();
        var empty = MissionMap.FromLists([], [], [], []);
        for (int mask = 0; mask < 16; mask++)
        {
            var settings = new ResearchSettings { AllowShipyardPath = (mask & 1) != 0,
                AllowExpansionPath = (mask & 2) != 0, AllowTacticalPath = (mask & 4) != 0,
                AllowSupremacyPath = (mask & 8) != 0 };
            var mission = Mission.Assemble(core, empty, [new SideSpec(4, "test", civ.CivilizationId)], new NullIgcSite(), research: settings);
            var bytes = (byte[])civ.TtbmBaseTechs.Clone();
            for (int b = 0; b < 4; b++) if ((mask & (1 << b)) != 0) bytes[0] |= (byte)(0x40 >> b);
            var side = mission.Sides.Single();
            Assert.Equal(bytes, side.DevelopmentTechs.ToBytes());
            side.DevelopmentTechs = core.ComputeRequiredTechUnion();
            Assert.Equal(bytes, side.InitialTechs.ToBytes());
            Assert.Equal(BucketCatalog.Describe(core, TechTreeBitMask.FromBytes(bytes), GlobalAttributeSet.FromArray(civ.GasBaseAttributes)).Count,
                mission.Sides.Single().Buckets.Count);
        }
        var noDev = Mission.Assemble(core, empty, [new SideSpec(0, "test", civ.CivilizationId)], new NullIgcSite(),
            research: new ResearchSettings { AllowDevelopments = false });
        Assert.Equal(civ.TtbmNoDevTechs, noDev.Sides.Single().DevelopmentTechs.ToBytes());
        Assert.Empty(noDev.Sides.Single().Buckets);
        Assert.False(noDev.AllowDevelopments);
    }

    [SkippableFact]
    [Trait("Tier", "Live")]
    public void Every_authored_and_generated_civ_plan_validates_and_auto_falls_back_for_other_races()
    {
        Require.Artwork();
        var core = Load();
        var directory = Path.Combine(Root, "data/showcase-plans");
        var authored = ResearchPlanCatalog.ValidateDirectory(core, "PCore001", directory);
        foreach (var id in new short[] { 25, 3 })
            foreach (var name in new[] { "plan_a", "plan_b" })
                Assert.Contains(authored, p => p.CivId == id && p.Name == name);
        foreach (var civ in core.Civilizations.Values)
        {
            var tree = new ResearchTree(core, civ.CivilizationId);
            var plan = ResearchPlanCatalog.Select(core, "PCore001", civ.CivilizationId, directory);
            ResearchPlanCatalog.Validate(tree, "PCore001", plan.Plan);
            Assert.NotEmpty(plan.Plan.Steps);
            if (!authored.Any(p => p.CivId == civ.CivilizationId)) Assert.Contains("no authored plan", plan.Message);
        }
        // A second matchup, independent of the showcase pair, and a future three-side tuple.
        foreach (var tuple in new[] { new[] { 1, 2 }, new[] { 4, 35, 101 } })
        {
            var map = MissionMapGenerator.Generate(new MapGenParams { Teams = tuple.Length, CivIds = tuple }, core, 0);
            var mission = Mission.Assemble(core, map, tuple.Select((id, side) => new SideSpec((short)side, "test", (short)id)).ToArray(), new NullIgcSite());
            foreach (var side in mission.Sides)
                ResearchPlanCatalog.Validate(new ResearchTree(core, side.Civilization.CivilizationId), "PCore001",
                    ResearchPlanCatalog.Select(core, "PCore001", side.Civilization.CivilizationId, directory).Plan);
        }
    }

    [SkippableFact]
    public void Invalid_order_identity_rock_and_expected_unlock_fail_at_offending_step()
    {
        Require.Artwork();
        var tree = new ResearchTree(Load(), 25);
        var plan = tree.Generate("PCore001");
        var late = plan.Steps.Last(s => s.Action == "station");
        var bad = tree.Generate("PCore001");
        bad.Steps = [late];
        Assert.Contains("step 1", Assert.Throws<InvalidDataException>(() => ResearchPlanCatalog.Validate(tree, "PCore001", bad)).Message);
        plan.Steps[0].Name = "stale name";
        Assert.Contains("step 1", Assert.Throws<InvalidDataException>(() => ResearchPlanCatalog.Validate(tree, "PCore001", plan)).Message);
        plan = tree.Generate("PCore001");
        plan.Steps.First(s => s.Action == "station").AsteroidClass = 0;
        Assert.Contains("asteroid", Assert.Throws<InvalidDataException>(() => ResearchPlanCatalog.Validate(tree, "PCore001", plan)).Message);
        plan = tree.Generate("PCore001");
        plan.Steps[0].ExpectedUnlocks.Add(new("hull", short.MaxValue, "nonexistent", "invalid"));
        Assert.Contains("expected unlock", Assert.Throws<InvalidDataException>(() => ResearchPlanCatalog.Validate(tree, "PCore001", plan)).Message);
        plan = tree.Generate("PCore001"); plan.CoreVersion++;
        Assert.Throws<InvalidDataException>(() => ResearchPlanCatalog.Validate(tree, "PCore001", plan));
        plan = tree.Generate("PCore001"); plan.Steps.Insert(0, new ResearchStep { Action = "wait", WaitSeconds = -1 });
        Assert.Contains("step 1", Assert.Throws<InvalidDataException>(() => ResearchPlanCatalog.Validate(tree, "PCore001", plan)).Message);
    }

    [SkippableTheory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(3, false)]
    public void Boot_uses_configured_miners_and_selects_plans_for_actual_side_civs(int miners, bool developments)
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(new Cfg
        {
            ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0,
            Games = [new Game { Name = "P702", Teams = 2, CivIds = [1, 2], SideNames = ["A", "B"],
                InitialMinersPerTeam = miners, AllowDevelopments = developments, ResearchPlanDirectory = Path.Combine(Root, "data/showcase-plans"),
                MapGen = new Allegiance.Server.MapGenConfig { Type = "SingleRing" } }],
        });
        var mission = Assert.Single(server.Missions);
        foreach (var side in mission.Sides)
        {
            Assert.Equal(developments ? miners : 0, mission.Sectors.SelectMany(s => s.Models.OfType<Ship>())
                .Count(s => s.SideId == side.Id && s.PilotType == Ship.PilotTypeMiner));
            Assert.Equal(side.Civilization.CivilizationId, mission.ResearchPlans[side.Id].CivId);
        }
        Assert.Throws<InvalidDataException>(() => new ResearchSettings { InitialMinersPerTeam = -1 }.Validate());
    }
}
