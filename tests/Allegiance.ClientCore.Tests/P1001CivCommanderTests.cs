using System.Text.Json;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Strategy;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
public sealed class P1001CivCommanderTests(ITestOutputHelper output)
{
    private static readonly Dictionary<short, (ResearchTree Tree, PortfolioChoice Choice, ResearchPlan Plan)> Plans = new();
    internal static (ResearchTree Tree, PortfolioChoice Choice, ResearchPlan Plan) For(short civ)
    {
        Require.Artwork();
        if (Plans.TryGetValue(civ, out var saved)) return saved;
        var core = Catalog.RepresentativeCore(); var tree = new ResearchTree(core, civ);
        var profile = CivProfile.Get(core, civ);
        var choice = CivPortfolio.Choose(core, profile, new short[] { 18, 4, 5 }.Where(id => id != civ).Select(id => CivProfile.Get(core, id)), 1001);
        var plan = CivExecutionPlan.Build(tree, choice, "PCore001");
        return Plans[civ] = (tree, choice, plan);
    }
    internal static ResearchCommander.Snapshot Initial(ResearchTree tree, int wallet = 1_000_000)
        => new(1, true, wallet, tree.Settings.StartingTechs(tree.Civ), [tree.Civ.InitialStationTypeId], [], [],
            tree.Nodes.Where(n => n.Kind == "station" && n.AsteroidMask > 0).Select((n, i) =>
                new ResearchCommander.Rock((short)(1000 + i), n.AsteroidMask, true, Sector: 1, Ore: 0)).ToArray(),
            new Dictionary<short, ResearchCommander.Funding>());

    [SkippableTheory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(18)] [InlineData(25)] [InlineData(35)] [InlineData(101)] [InlineData(201)]
    public void Every_civ_executes_its_chosen_opening_and_native_legal_parallel_schedule(short civ)
    {
        var (tree, choice, plan) = For(civ);
        Assert.True(CivExecutionPlan.TryRead(plan, out double share, out int opening));
        Assert.Equal(.15, share); Assert.True(opening > 0);
        Assert.Equal(choice.Primary!.Route.Steps, plan.Steps.Take(choice.Primary.Route.Steps.Length).Select(s => new StrategyNode(s.Action, s.Id)).ToArray());
        ResearchPlanCatalog.Validate(tree, "PCore001", plan);
        var commander = new ResearchCommander(0, "test", tree, plan, tree.Settings.StartingTechs(tree.Civ), new());
        var world = Initial(tree); var buckets = BucketCatalog.Describe(tree.Core, world.Developments,
            GlobalAttributeSet.FromArray(tree.Civ.GasBaseAttributes));
        var commands = commander.Tick(world).Where(c => c.Kind == "donate").ToArray();
        Assert.NotEmpty(commands);
        Assert.InRange(commands.Sum(c => c.Amount), 1, world.Wallet - commander.ProtectedReserve);
        foreach (var command in commands)
        {
            var b = buckets.Single(b => b.Id == command.Id);
            var node = tree.Find(b.ItemType == BucketItemType.BitDevelopment ? "develop" : b.ItemType == BucketItemType.BitStationType ? "station" : "drone",
                b.ItemType == BucketItemType.BitDevelopment ? b.DevelopmentId : b.ItemType == BucketItemType.BitStationType ? b.StationTypeId : b.DroneTypeId);
            var techs = world.Developments.Or(TechTreeBitMask.FromBytes(tree.Core.StationTypes[tree.Civ.InitialStationTypeId].TtbmEffects));
            Assert.True(node.Kind == "develop" ? TechTreeBitMask.FromBytes(node.Required).IsSubsetOf(techs)
                : TechTreeBitMask.RequiredTechsBuyable(node.Required, tree.Core.StationTypes[tree.Civ.InitialStationTypeId].TtbmLocal, techs));
            Assert.InRange(command.Amount, 1, b.Price);
        }
        Assert.InRange(commander.PortfolioJobs.Count(j => j.Kind == "research" && j.State is "funding" or "maturing"), 0, ResearchCommander.ParallelResearchLimit);
        // No repeated donations from a stale wallet, even after bucket acks or elapsed time.
        Assert.DoesNotContain(commander.Tick(world), c => c.Kind == "donate");
        world = world with { Time = 1000, FundingByBucket = commands.ToDictionary(c => c.Id, c => new ResearchCommander.Funding(c.Amount, 0)) };
        Assert.DoesNotContain(commander.Tick(world), c => c.Kind == "donate");
        output.WriteLine($"civ={civ} opening={opening} jobs={commands.Length} reserve={commander.ProtectedReserve} funded={commands.Sum(c => c.Amount)}");
    }

    [SkippableFact]
    public void Independent_research_matures_in_parallel_and_acknowledged_income_does_not_stall_jobs()
    {
        var (tree, _, _) = For(18);
        var state = new ResearchState(tree);
        var nodes = tree.Nodes.Where(n => n.Kind == "develop" && state.CanApply(n)).Take(6).ToArray();
        Assert.True(nodes.Length >= 4);
        var steps = new List<ResearchStep>();
        foreach (var n in nodes) { var s = new ResearchStep { Action = n.Kind, Id = n.Id, Name = n.Name }; state.Apply(s); steps.Add(s); }
        var plan = new ResearchPlan { Core = "PCore001", CoreVersion = tree.Core.Version, CivId = 18,
            Name = "civ-strategy/0.15/6/" + new string('A', 64), Steps = steps };
        var commander = new ResearchCommander(0, "test", tree, plan, tree.Settings.StartingTechs(tree.Civ), new());
        var world = Initial(tree);
        var commands = commander.Tick(world).Where(c => c.Kind == "donate").ToArray();
        Assert.Equal(4, commands.Length);
        var funding = commands.ToDictionary(c => c.Id, c => new ResearchCommander.Funding(c.Amount, 0));
        // Income exceeds debits: absolute wallet comparison would deadlock here.
        world = world with { Time = 2, Wallet = 1_100_000, WalletDebited = commands.Sum(c => c.Amount), FundingByBucket = funding };
        Assert.DoesNotContain(commander.Tick(world), c => c.Kind == "donate"); // four maturing jobs already fill the cap
        world = world with { Time = 100000 }; commander.Tick(world);
        Assert.Equal(0, commander.Telemetry!.Step - 1); // no elapsed-time completion
        var first = nodes[0];
        world = world with { CompletedDevelopments = [first.Id], Developments = world.Developments.Or(TechTreeBitMask.FromBytes(first.Effects)) };
        Assert.Contains(commander.Tick(world), c => c.Kind == "donate"); // free job slot becomes available
    }

    [SkippableFact]
    public void Missing_or_consumed_rocks_defer_construction_without_blocking_research_and_repairs()
    {
        var (tree, _, plan) = For(18); var world = Initial(tree) with { Rocks = [] };
        var reservations = new Dictionary<short, short>();
        var commander = new ResearchCommander(0, "test", tree, plan, world.Developments, reservations);
        var commands = commander.Tick(world).Where(c => c.Kind == "donate").ToArray();
        Assert.NotEmpty(commands); Assert.Empty(reservations);
        Assert.Contains(commander.PortfolioJobs, j => j.Kind == "construction" && j.State.Contains("side-observed"));
        var b = BucketCatalog.Describe(tree.Core, world.Developments, GlobalAttributeSet.FromArray(tree.Civ.GasBaseAttributes));
        Assert.All(commands, c => Assert.NotEqual(BucketItemType.BitStationType, b.Single(v => v.Id == c.Id).ItemType));
    }

    [SkippableFact]
    public void Construction_leases_consumed_sites_and_destroyed_prerequisites_replan_independently()
    {
        var (tree, _, _) = For(1); var state = new ResearchState(tree);
        var route = tree.Generate("PCore001", prioritizeResearch: true).Steps;
        var firstStation = route.FindIndex(s => s.Action == "station");
        Assert.True(firstStation >= 0);
        foreach (var s in route.Take(firstStation)) state.Apply(s);
        var station = route[firstStation];
        var steps = route.Take(firstStation + 1).ToList();
        var plan = new ResearchPlan { Core = "PCore001", CoreVersion = tree.Core.Version, CivId = 1,
            Name = $"civ-strategy/0.15/{steps.Count}/" + new string('B', 64), Steps = steps };
        var reservations = new Dictionary<short, short>();
        var world = Initial(tree) with { Developments = state.DevelopmentTechs,
            CompletedDevelopments = steps.Where(s => s.Action == "develop").Select(s => s.Id).ToHashSet(),
            Rocks = [new(10, station.AsteroidClass!.Value, true, Sector: 9), new(11, station.AsteroidClass.Value, true, Sector: 9)] };
        var commander = new ResearchCommander(0, "test", tree, plan, tree.Settings.StartingTechs(tree.Civ), reservations);
        var funding = commander.Tick(world).Where(c => c.Kind == "donate").ToArray();
        Assert.Single(funding); Assert.Equal((short)0, reservations[10]);
        short drone = tree.Core.StationTypes[station.Id].ConstructionDroneTypeId;
        world = world with { Time = 2, DonationSettled = funding.Sum(c => c.Amount), Units = [new(100, drone, station.Id, true, 1)] };
        Assert.Equal((short)10, Assert.Single(commander.Tick(world), c => c.Kind == "goto").Target);
        world = world with { Time = 2 + ResearchCommander.ConstructionLeaseSeconds };
        Assert.Equal((short)11, Assert.Single(commander.Tick(world), c => c.Kind == "goto").Target);
        Assert.False(reservations.ContainsKey(10));
        world = world with { Time = 200, Rocks = [] };
        Assert.DoesNotContain(commander.Tick(world), c => c.Kind is "build" or "goto" or "donate");
        Assert.Empty(reservations);
        world = world with { Time = 210, Stations = [tree.Civ.InitialStationTypeId, station.Id], Units = [] };
        commander.Tick(world);
        world = world with { Time = 220, Stations = [tree.Civ.InitialStationTypeId],
            Rocks = [new(12, station.AsteroidClass.Value, true, Sector: 1)] };
        Assert.Contains(commander.Tick(world), c => c.Kind == "donate");
        Assert.Contains(commander.PortfolioJobs, j => j.Kind == "repair" && j.Id == station.Id);
    }

    [SkippableFact]
    public void Native_refund_settles_a_rejected_donation_without_time_based_spending()
    {
        var (tree, _, plan) = For(18); var world = Initial(tree);
        var commander = new ResearchCommander(0, "test", tree, plan, world.Developments, new());
        var first = commander.Tick(world).Where(c => c.Kind == "donate").ToArray();
        Assert.NotEmpty(first);
        Assert.DoesNotContain(commander.Tick(world with { Time = 1000 }), c => c.Kind == "donate");
        var retry = commander.Tick(world with { Time = 1001, DonationSettled = first.Sum(c => c.Amount) }).Where(c => c.Kind == "donate").ToArray();
        Assert.NotEmpty(retry);
        Assert.Equal(first.Select(c => c.Id), retry.Select(c => c.Id)); // refunds earn no new tail allowance
        Assert.InRange(retry.Sum(c => c.Amount), 1, world.Wallet - commander.ProtectedReserve);
    }

    [SkippableFact]
    public void Hidden_or_nonprogressing_buckets_do_not_take_parallel_slots_or_receive_donations()
    {
        Require.Artwork();
        var file = IgcReader.ParseFile(Path.Combine(Catalog.ArtRoot, "PCore001.igc"));
        var core = StaticCore.Load(file);
        var before = new ResearchTree(core, 18); var state = new ResearchState(before);
        var nodes = before.Nodes.Where(n => n.Kind == "develop" && n.Price > 0 && n.Seconds > 0 && state.CanApply(n)).Take(3).ToArray();
        var body = file.Body.ToArray();
        foreach (var record in file.Records.Where(r => r.Type == IgcObjectType.Development))
        {
            var span = body.AsSpan(record.PayloadOffset, record.Size);
            var id = DataDevelopmentIGC.Decode(span).DevelopmentId;
            if (id == nodes[0].Id) span[261] = 255;
            if (id == nodes[1].Id) System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span, 0);
        }
        core = StaticCore.Load(new IgcFile { Wrapper = file.Wrapper, Version = file.Version, Body = body,
            Records = file.Records, BodyLength = file.BodyLength, BytesConsumed = file.BytesConsumed });
        var tree = new ResearchTree(core, 18);
        var plan = new ResearchPlan { Core = "PCore001", CoreVersion = core.Version, CivId = 18,
            Name = "civ-strategy/0.15/3/" + new string('C', 64),
            Steps = nodes.Select(n => new ResearchStep { Action = n.Kind, Id = n.Id, Name = n.Name }).ToList() };
        var world = Initial(tree);
        var commander = new ResearchCommander(0, "gates", tree, plan, world.Developments, new());
        var commands = commander.Tick(world).Where(c => c.Kind == "donate").ToArray();
        var buckets = BucketCatalog.Describe(core, world.Developments, GlobalAttributeSet.FromArray(tree.Civ.GasBaseAttributes));
        Assert.Equal(nodes[2].Id, buckets.Single(b => b.Id == Assert.Single(commands).Id).DevelopmentId);
        Assert.Equal(2, commander.PortfolioJobs.Count(j => j.State.Contains("group/price/time")));
    }

    [Fact]
    public void Coverage_share_is_bounded_and_runtime_markers_fail_closed()
    {
        foreach (double share in new[] { -.01, .251, double.NaN, double.PositiveInfinity })
            Assert.Throws<InvalidDataException>(() => CivExecutionPlan.ValidateCoverageShare(share));
        Assert.False(CivExecutionPlan.TryRead(new ResearchPlan { Name = "civ-strategy/.15/0/not-a-hash" }, out _, out _));
        Assert.False(CivExecutionPlan.TryRead(new ResearchPlan { Name = "generated" }, out _, out _));
        var marker = new ResearchPlan { Name = "civ-strategy/0.15/0/" + new string('A', 64) };
        Assert.False(BotManager.ResearchTacticsEnabled(new ResearchBriefing { Plan = marker, TechMode = "unlocked" }));
        Assert.True(BotManager.ResearchTacticsEnabled(new ResearchBriefing { Plan = marker, TechMode = "research" }));
        Assert.True(BotManager.ResearchTacticsEnabled(new ResearchBriefing { Plan = new() { Name = "legacy" }, TechMode = "unlocked" }));
    }

    [Fact]
    public void Authoritative_debit_counter_handles_income_and_resets_with_the_world()
    {
        using var cs = new ConnectionSession();
        void Feed(IFedMessage message) => ((ITransportSite)cs).OnReceive(ConnectionId.Server, FedPacket.Pack(message.Encode()));
        // Own identity comes from the normal joined-message path.
        typeof(Allegiance.ClientCore.State.ClientWorld).GetProperty("MyShipId")!.SetValue(cs.World, (short?)7);
        Feed(new SetMoney { ShipId = 7, Money = 1000 });
        Feed(new MoneyChange { SidTo = 7, SidFrom = 7, DMoney = -100 });
        Feed(new MoneyChange { SidTo = 7, SidFrom = -1, DMoney = 200 });
        Assert.Equal(1100, cs.World.MyMoney); Assert.Equal(100, cs.World.OwnWalletDebited);
        cs.World.Reset(); Assert.Equal(0, cs.World.OwnWalletDebited);
    }
}
