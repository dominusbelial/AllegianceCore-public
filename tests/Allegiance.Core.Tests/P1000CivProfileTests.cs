using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Allegiance.Core.Model;
using Allegiance.Core.Strategy;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

public sealed class P1000CivProfileTests
{
    private static readonly Lazy<StaticCore> Loaded = new(() => StaticCore.Load(File()));
    private static IgcFile File() => IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc"));
    private static StaticCore Core() { Require.Artwork(); return Loaded.Value; }

    [SkippableTheory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(18)] [InlineData(25)] [InlineData(35)] [InlineData(101)] [InlineData(201)]
    public void Every_loaded_civ_has_immutable_specs_and_legal_routes(short civ)
    {
        var core = Core(); var p = CivProfile.Get(core, civ); var tree = new ResearchTree(core, civ);
        Assert.Same(p, CivProfile.Get(core, civ));
        Assert.Equal(core.Civilizations[civ].Name, p.Name);
        Assert.Equal(64, p.CoreIdentity.Length);
        Assert.Equal(core.HullTypes.Count, p.Hulls.Length);
        Assert.Equal(tree.Nodes.Count, p.Prerequisites.Length);
        Assert.NotEmpty(p.Routes);
        Assert.All(p.Routes, route =>
        {
            var state = CivProfile.Replay(tree, route.Steps);
            Assert.False(state.CanApply(tree.Find(route.Target.Kind, route.Target.Id)));
            Assert.Equal(route.Funding, route.Steps.Sum(k => (long)tree.Find(k.Kind, k.Id).Price));
            var ga = core.Civilizations[civ].GasBaseAttributes.ToArray();
            foreach (var step in route.Steps.Where(k => k.Kind == "develop"))
                for (int i = 0; i < ga.Length; i++) ga[i] *= tree.Find(step.Kind, step.Id).Attributes[i];
            Assert.Equal(ga, route.Attributes);
        });
        Assert.All(p.Hulls, h => { Assert.Equal(5, h.CargoSlots); Assert.True(h.Crew >= 1); });
        Assert.All(p.Parts, part =>
        {
            Assert.Equal(DataConstantsIGC.DefidMax, part.DamageAgainstDefense.Length);
            Assert.Equal(DataConstantsIGC.DefidMax, part.BlastDamageAgainstDefense.Length);
        });
        Assert.Equal(20, p.DamageMatrix.Length);
        var choice = CivPortfolio.Choose(core, p, [CivProfile.Get(core, civ == 4 ? (short)18 : (short)4)], 1000);
        Assert.NotNull(choice.Primary);
        Assert.NotNull(choice.Counter);
        Assert.InRange(choice.Alternatives.Length, 1, 3);
        var plan = CivPortfolio.ComparisonPlan(tree, choice, "PCore001");
        ResearchPlanCatalog.Validate(tree, "PCore001", plan);
        Assert.True(choice.Primary.Spikes.Sum(s => s.Crew * s.Ships) <= choice.Assumptions.Pilots);
        Assert.True(choice.Primary.Spikes.Sum(s => s.Price * s.Ships) <= choice.Assumptions.Funding);
        var actual = ActualSide(tree, choice.Primary.Route.Steps);
        var combined = choice.Primary.Route.Steps.Concat(choice.Counter.Route.Steps).Distinct().ToArray();
        _ = ActualSide(tree, combined);
        Assert.All(choice.Primary.Spikes, spike =>
        {
            Assert.True(spike.Ships > 0 && spike.Crew <= choice.Assumptions.CrewPerShip);
            Assert.True(spike.Ships * spike.Crew <= choice.Assumptions.Pilots);
            Assert.True(double.IsFinite(spike.Power) && spike.Power > 0);
            var hull = core.HullTypes[spike.Hull];
            Assert.Contains(actual.Mission!.GetStations(), station => station.Type.StationTypeId == spike.Station);
            var local = core.StationTypes[spike.Station].TtbmLocal;
            if (spike.Duty != "miner") Assert.True(TechTreeBitMask.RequiredTechsBuyable(hull.TtbmRequired, local, actual.Techs));
            foreach (var fitted in spike.Parts)
            {
                var part = tree.Parts.Single(part => part.PartId == fitted.Id);
                Assert.True(hull.CanMountPart(part, fitted.Mount));
                Assert.True(TechTreeBitMask.RequiredTechsBuyable(part.TtbmRequired, local, actual.Techs));
            }
        });
    }

    private static Side ActualSide(ResearchTree tree, IEnumerable<StrategyNode> route)
    {
        var mission = Mission.Assemble(tree.Core, MissionMap.FromLists(
            [DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = 0 }], [], [], []),
            [new SideSpec(0, "P1000", tree.Civ.CivilizationId)], new NullIgcSite(), research: tree.Settings);
        var side = mission.Sides[0]; var state = new ResearchState(tree); short id = 1;
        void Place(short type) => mission.PlaceStation(new Station(DataStationIGC.Decode(new byte[DataStationIGC.Size])
            with { StationId = id++, StationTypeId = type, SideId = 0, ClusterId = 0, Position = [0, 0, 0] }, tree.Core.StationTypes[type]), mission.Sectors[0]);
        Place(tree.Civ.InitialStationTypeId);
        foreach (var key in route)
        {
            var bucket = Assert.Single(side.Buckets, b => key.Kind switch
            {
                "develop" => b.Development?.DevelopmentId == key.Id,
                "station" => b.StationType?.StationTypeId == key.Id,
                "drone" => b.DroneType?.DroneTypeId == key.Id,
                _ => false
            });
            Assert.True(side.CanBuy(bucket), $"actual side cannot buy {key}");
            state.Apply(CivProfile.Step(tree, key));
            if (key.Kind == "develop") { side.DevelopmentTechs = state.DevelopmentTechs; side.UpgradeStationsToSuccessors(); }
            if (key.Kind == "station") Place(key.Id);
        }
        return side;
    }

    [SkippableFact]
    public void Choice_repeats_with_opponent_permutations_and_never_mutates_core_or_uses_random()
    {
        var core = Core(); var p = CivProfile.Get(core, 18);
        var before = JsonSerializer.Serialize(new { core.Civilizations, core.HullTypes, core.Developments });
        var a = CivPortfolio.Choose(core, p, [CivProfile.Get(core, 4), CivProfile.Get(core, 5)], 1000);
        var b = CivPortfolio.Choose(core, p, [CivProfile.Get(core, 5), CivProfile.Get(core, 4)], 1000);
        Assert.Equal(JsonSerializer.Serialize(a), JsonSerializer.Serialize(b));
        var c = CivPortfolio.Choose(core, p, [CivProfile.Get(core, 4), CivProfile.Get(core, 5)], 1001);
        Assert.NotEqual(a.InputHash, c.InputHash);
        Assert.Equal(before, JsonSerializer.Serialize(new { core.Civilizations, core.HullTypes, core.Developments }));
        Assert.All(a.Alternatives, candidate => Assert.True(candidate.Score >= a.Alternatives.Max(c => c.Score) * .8));
    }

    [SkippableFact]
    public void Settings_and_same_version_modded_core_have_distinct_cache_entries_and_no_identity_branch()
    {
        var core = Core(); var original = CivProfile.Get(core, 18);
        var file = File(); var body = file.Body.ToArray();
        var record = file.Records.Single(r => r.Type == IgcObjectType.Civilization &&
            DataCivilizationIGC.Decode(body.AsSpan(r.PayloadOffset, r.Size)).CivilizationId == 18);
        var span = body.AsSpan(record.PayloadOffset, record.Size);
        BinaryPrimitives.WriteInt16LittleEndian(span[262..], 418);
        span.Slice(8, 25).Clear(); Encoding.ASCII.GetBytes("Modded civilization").CopyTo(span[8..]);
        var modded = StaticCore.Load(new IgcFile { Wrapper = file.Wrapper, Version = file.Version, Body = body,
            Records = file.Records.Reverse().ToArray(), BodyLength = file.BodyLength, BytesConsumed = file.BytesConsumed });
        var p = CivProfile.Get(modded, 418);
        Assert.Equal("Modded civilization", p.Name);
        Assert.NotEqual(original.CoreIdentity, p.CoreIdentity);
        Assert.Equal(original.Routes.Select(r => string.Join(',', r.Steps)), p.Routes.Select(r => string.Join(',', r.Steps)));
        var off = CivProfile.Get(core, 18, new() { AllowDevelopments = false });
        Assert.Empty(off.Routes);
        Assert.NotSame(original, off);
        Assert.Null(CivPortfolio.Choose(core, off, [], 1).Primary);
        var closed = CivProfile.Get(core, 18, new() { AllowTacticalPath = false });
        Assert.NotEqual(original.Settings, closed.Settings);
        Assert.NotEqual(original.StartingTechs, closed.StartingTechs);
    }

    [SkippableFact]
    public void Actual_opponents_can_change_the_counter_route()
    {
        var core = Core(); var profile = CivProfile.Get(core, 18); var tree = new ResearchTree(core, 18);
        var initialWeapon = tree.Parts.First(p => p.Type is DataPartTypeIGC.Weapon w && core.ProjectileTypes[w.ProjectileTypeId].Power > 0
            && profile.StartingPurchases.Any(u => u.Kind == "part" && u.Id == p.PartId));
        int firstDamage = core.ProjectileTypes[((DataPartTypeIGC.Weapon)initialWeapon.Type).ProjectileTypeId].DamageType & 31;
        var closure = new ResearchState(tree);
        foreach (var step in tree.Generate("PCore001").Steps) closure.Apply(step);
        int otherDamage = tree.Parts.Where(p => p.Type is DataPartTypeIGC.Weapon && closure.Unlocks.Any(u => u.Kind == "part" && u.Id == p.PartId))
            .Select(p => core.ProjectileTypes[((DataPartTypeIGC.Weapon)p.Type).ProjectileTypeId])
            .Where(p => p.Power > 0 && (p.DamageType & 31) != firstDamage).Select(p => p.DamageType & 31).Last();
        var opposingHulls = new Dictionary<short, byte>();
        foreach (short civ in new short[] { 4, 5 })
            foreach (var h in CivProfile.Get(core, civ).Hulls.Where(h => h.FixedGuns > 0
                && CivProfile.Get(core, civ).StartingPurchases.Any(u => u.Kind == "hull" && u.Id == h.Id)))
                opposingHulls[h.Id] = (byte)(civ == 4 ? 10 : 11);
        var file = File(); var body = file.Body.ToArray();
        foreach (var r in file.Records)
        {
            if (r.Type == IgcObjectType.HullType)
            {
                var h = DataHullTypeIGC.Decode(body.AsSpan(r.PayloadOffset, r.Size));
                if (!opposingHulls.TryGetValue(h.HullId, out var defense)) continue;
                body[r.PayloadOffset + 458] = defense;
                BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(r.PayloadOffset + 452), 1_000_000);
            }
            if (r.Type == IgcObjectType.Constants)
                for (int damage = 0; damage < 20; damage++)
                    for (int defense = 10; defense <= 11; defense++)
                        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(r.PayloadOffset + DataConstantsIGC.DamageOffset + (damage * 20 + defense) * 4),
                            damage == (defense == 10 ? firstDamage : otherDamage) ? 20 : 0);
        }
        var modded = StaticCore.Load(new IgcFile { Wrapper = file.Wrapper, Version = file.Version, Body = body,
            Records = file.Records, BodyLength = file.BodyLength, BytesConsumed = file.BytesConsumed });
        var own = CivProfile.Get(modded, 18);
        var a = CivPortfolio.Choose(modded, own, [CivProfile.Get(modded, 4)], 0);
        var b = CivPortfolio.Choose(modded, own, [CivProfile.Get(modded, 5)], 0);
        Assert.NotNull(a.Counter); Assert.NotNull(b.Counter);
        Assert.NotEqual(a.Counter.Id, b.Counter.Id);
        Assert.NotEqual(a.Counter.ShipGain, b.Counter.ShipGain);
    }

    [SkippableFact]
    public void Native_damage_flags_preserve_matrix_effectiveness_in_modded_cores()
    {
        var core = Core(); var file = File(); var body = file.Body.ToArray();
        foreach (var record in file.Records.Where(r => r.Type == IgcObjectType.ProjectileType)) body[record.PayloadOffset + 74] |= 0x40;
        foreach (var record in file.Records.Where(r => r.Type == IgcObjectType.MissileType)) body[record.PayloadOffset + 516] |= 0x40;
        var modded = StaticCore.Load(new IgcFile { Wrapper = file.Wrapper, Version = file.Version, Body = body,
            Records = file.Records, BodyLength = file.BodyLength, BytesConsumed = file.BytesConsumed });
        var original = CivProfile.Get(core, 18); var changed = CivProfile.Get(modded, 18);
        Assert.NotEqual(original.CoreIdentity, changed.CoreIdentity);
        for (int i = 0; i < original.Parts.Length; i++)
        {
            Assert.Equal(original.Parts[i].DamageAgainstDefense.ToArray(), changed.Parts[i].DamageAgainstDefense.ToArray());
            Assert.Equal(original.Parts[i].BlastDamageAgainstDefense.ToArray(), changed.Parts[i].BlastDamageAgainstDefense.ToArray());
        }
        var first = CivPortfolio.Evaluate(new ResearchTree(core, 18), [], [CivProfile.Get(core, 4)], new());
        var second = CivPortfolio.Evaluate(new ResearchTree(modded, 18), [], [CivProfile.Get(modded, 4)], new());
        Assert.Equal(first.Select(s => s.Power), second.Select(s => s.Power));
    }

    [SkippableFact]
    public void Provider_graph_retains_all_alternatives_and_individual_station_masks()
    {
        var core = Core(); var profile = CivProfile.Get(core, 4); var tree = new ResearchTree(core, 4);
        foreach (var node in profile.Prerequisites)
        {
            var expected = tree.Nodes.Where(p => Enumerable.Range(0, 50).Any(b =>
                (node.Required[b] & (p.Effects[b] | p.Local[b])) != 0)).Select(CivProfile.Key);
            Assert.Equal(expected, node.Providers);
        }
        Assert.Contains(profile.Prerequisites, n => n.Providers.Length > 1);
        Assert.All(profile.Stations, station => Assert.Equal(core.StationTypes[station.Id].TtbmLocal, station.Local));
        Assert.Contains(profile.Routes.GroupBy(r => r.Target), g => g.Count() > 1);
    }
}
