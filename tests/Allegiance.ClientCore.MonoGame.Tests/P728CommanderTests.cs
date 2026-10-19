using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame.Tests;

public class P728CommanderTests
{
    private static InvestmentBucket View(Bucket b) => new(b.Id, b.ItemType, b.Name, b.Price, b.TimeToBuild,
        b.HullIdDrone, b.DroneType?.DroneTypeId ?? -1, b.Development?.DevelopmentId ?? -1, b.StationType?.StationTypeId ?? -1);

    [SkippableFact]
    public void Investment_buyability_matches_authoritative_side_for_every_civilization_and_tech_change()
    {
        Require.Artwork();
        var core = CatalogCore();
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(ExternalPaths.Resolve(ExternalResourceKind.Artwork), "m2xspiderw.igc")));
        int checkedBuckets = 0;
        foreach (var civ in core.Civilizations.Values)
        {
            var mission = Mission.Assemble(core, map, [new SideSpec(0, "human", civ.CivilizationId)], new NullIgcSite());
            var side = mission.Sides[0];
            var stations = mission.Sectors.SelectMany(s => s.StationRuntimes).Where(s => s.SideId == 0).Select(s => s.Type).ToArray();
            var initial = side.DevelopmentTechs;
            foreach (var mask in new[] { initial, core.Developments.Values.Aggregate(initial, (mask, d) => mask.Or(TechTreeBitMask.FromBytes(d.TtbmEffects))) })
            {
                side.DevelopmentTechs = mask;
                foreach (var b in side.Buckets)
                {
                    Assert.Equal(side.CanBuy(b), InvestmentTechGates.CanBuy(core, View(b), mask, stations));
                    checkedBuckets++;
                }
            }
        }
        Assert.True(checkedBuckets > 1000);
    }

    private static StaticCore CatalogCore() => StaticCore.Load(IgcReader.ParseFile(Path.Combine(ExternalPaths.Resolve(ExternalResourceKind.Artwork), "PCore001.igc")));

    [Fact]
    public void Native_predecessors_hide_later_attribute_levels_until_completion_and_show_all_preserves_gates()
    {
        var gas = Enumerable.Repeat(1f, 25).ToArray(); gas[0] = 1.1f;
        var first = new DataDevelopmentIGC(100, 10, "", "", "speed 1", "", 0, new byte[50], new byte[50], gas, 10, 0);
        var second = first with { DevelopmentId = 11, Price = 200, Name = "speed 2" };
        var developments = new Dictionary<short, DataDevelopmentIGC> { [10] = first, [11] = second };
        InvestmentBucket Def(DataDevelopmentIGC d, short id) => new(id, BucketItemType.BitDevelopment, d.Name, d.Price, d.TimeToBuild, -1, -1, d.DevelopmentId, -1);
        var defs = new[] { Def(first, 2), Def(second, 3) };
        var predecessors = InvestmentTechGates.Predecessors(developments, defs);
        Assert.Equal((short)2, predecessors[3]);
        var current = defs[1];
        var previous = defs[0];
        var before = new InvestmentPaneModel(defs, null, true, true, 100000, predecessors: predecessors);
        Assert.DoesNotContain(current, before.Items);
        var funded = new Dictionary<short, ClientWorld.BucketFunding> { [previous.Id] = new(previous.Price, previous.TimeToBuildSec * 1000L) };
        var after = new InvestmentPaneModel(defs, funded, true, true, 100000, predecessors: predecessors);
        Assert.Contains(current, after.Items);
        var locked = new InvestmentPaneModel(defs, null, true, true, 100000, showAll: true, canBuy: _ => false, predecessors: predecessors);
        Assert.Contains(current, locked.Items);
        Assert.False(locked.CanInvest(current));
    }

    [Theory]
    [InlineData(-1, false)] [InlineData(0, true)] [InlineData(1, true)]
    [InlineData(2, false)] [InlineData(3, false)] [InlineData(4, false)] [InlineData(5, false)] [InlineData(255, false)]
    public void Native_tabs_hide_internal_buckets_and_disabled_paths_even_with_show_all(int group, bool visible)
    {
        var settings = new ResearchSettings { AllowSupremacyPath = false, AllowTacticalPath = false,
            AllowExpansionPath = false, AllowShipyardPath = false };
        Assert.Equal(visible, InvestmentTechGates.VisibleGroup(group, settings));
        if (group is >= 0 and <= 5) Assert.True(InvestmentTechGates.VisibleGroup(group, new()));
    }

    [Fact]
    public void Hiding_completed_rows_keeps_the_cursor_and_displayed_selection_consistent()
    {
        var a = new InvestmentBucket(1, BucketItemType.BitDevelopment, "a", 100, 1, -1, -1, 10, -1);
        var b = a with { Id = 2, DevelopmentId = 11 };
        var model = new InvestmentPaneModel([a, b], new Dictionary<short, ClientWorld.BucketFunding> { [2] = new(100, 1000) },
            true, true, 1000, cursor: 99, hideCompleted: true);
        Assert.Equal(0, model.Cursor);
        Assert.Equal(a, model.Current);
        Assert.Single(InvestmentLayout.Build(model), r => r.Kind == ShopLayout.Kind.Item && r.Highlight);
    }

    [Fact]
    public void Drone_cap_uses_exact_drone_identity_when_two_types_share_a_hull()
    {
        var a = new InvestmentBucket(1, BucketItemType.BitDroneType, "a", 100, 1, 501, 10, -1, -1);
        var b = a with { Id = 2, DroneTypeId = 11 };
        var model = new InvestmentPaneModel([a, b], null, true, true, 1000,
            droneCountByTypeId: new Dictionary<short, int> { [10] = 4 });
        Assert.False(model.CanInvest(a));
        Assert.True(model.CanInvest(b));
    }

    [SkippableFact]
    public void Docked_shop_refreshes_unlocked_offers_and_wallet_without_discarding_pending_loadout()
    {
        Require.Artwork();
        var core = CatalogCore();
        var catalog = ShopCatalog.FromCore(core)!;
        var hulls = catalog.Hulls.Where(h => h.GroupId >= 0).Take(2).ToArray();
        Assert.Equal(2, hulls.Length);
        var pending = new[] { new ExpandedPartData(1, 1, 0, 255) };
        var shop = new ShopModel(catalog with { Hulls = [hulls[0]] }, hulls[0].HullId, pending, 0);
        Assert.DoesNotContain(shop.BuyableHulls, h => h.HullId == hulls[1].HullId);
        var parts = shop.ChosenParts.ToArray();
        shop.RefreshOffers(catalog with { Hulls = hulls }, 100000);
        Assert.Contains(shop.BuyableHulls, h => h.HullId == hulls[1].HullId);
        Assert.Equal(parts, shop.ChosenParts);
        Assert.Equal(100000, shop.MyMoney);
        shop.RefreshMoney(123);
        Assert.Equal(123, shop.MyMoney);
    }

    private static ClientWorld FundingWorld()
    {
        var w = new ClientWorld();
        w.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.FromValues(10, 0, default, 0, Vector3.Zero, Orientation.Identity, Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1) });
        foreach (short id in new short[] { 10, 11, 12 }) w.Apply(new ExportShip { ShipId = id, SideId = id == 12 ? (short)1 : (short)0, CharacterName = $"p{id}" });
        w.Apply(new ScoreRosterUpdate { Entries = [new("p10", 0, 0, 0, 0, 0, 0), new("p11", 0, 0, 0, 0, 0, 0), new("p12", 1, 0, 0, 0, 0, 0)] });
        w.Apply(new ExportShip { ShipId = -1, SideId = 0 });
        w.Apply(new SetMoney { ShipId = 10, Money = 250 });
        return w;
    }

    [Fact]
    public void Funding_controls_gift_native_amounts_toggle_only_for_leader_and_obey_rebinding_and_suppression()
    {
        var w = FundingWorld();
        var input = new InvestmentFundingInput();
        var map = new ActionMap(InputActionCatalog.Definitions);
        Assert.Equal(100, input.Step(new(Keys.D), map, w, true).Gift!.MoneyGiven);
        Assert.Null(input.Step(new(Keys.D), map, w, true).Gift);
        input.Step(default, map, w, true);
        var large = input.Step(new(Keys.E), map, w, true).Gift!;
        Assert.Equal(250, large.MoneyGiven); Assert.Equal(11, large.ShipID); Assert.Equal(250, w.MyMoney);
        Assert.Null(input.Step(new(Keys.X), map, w, true).Auto);
        input.Step(default, map, w, true);
        w.Apply(new SetTeamLeader { SideId = 0, ShipId = 10 });
        Assert.Equal(-1, input.Step(new(Keys.X), map, w, true).Auto!.Amount);
        Assert.Equal(250, input.Step(new(Keys.A), map, w, true).Auto!.Amount);
        var keep = input.Step(new(Keys.C), map, w, true).Auto!;
        Assert.Equal(-1, keep.SidDonateTo); Assert.Equal(0, keep.Amount);
        map.SetBinding("InvestDonate", new(Keys.F18, InputModifiers.None), force: true);
        input.Step(new(Keys.F18), map, w, false);
        Assert.Null(input.Step(new(Keys.F18), map, w, true).Gift);
        input.Step(default, map, w, true);
        Assert.Equal(11, input.Step(new(Keys.F18), map, w, true).Gift!.ShipID);
    }

    [Fact]
    public void Constructor_build_intention_waits_for_remote_arrival_and_cancels_on_new_order_or_side()
    {
        var world = FundingWorld();
        world.Apply(new ExportAsteroid { Asteroid = new DataAsteroidIGC(1, [0, 0, 0], [0, 1, 0], [0, 0, 1], [0, 0, 0],
            1, 1, 0x20, 30, 100, 100, "", "", "", 3, "Si1", 255) });
        var queue = new CommanderBuildOrders();
        Assert.Equal(OrderChange.CidGoto, queue.Begin(world, 11, 30));
        Assert.Empty(queue.Tick(world));
        world.Apply(new ShipStatusMessage { ShipId = 11, SectorId = 3, StationId = -1, ParentId = -1, State = ShipStatusMessage.StateFlying });
        Assert.Equal(new[] { ((short)11, (short)30) }, queue.Tick(world));
        Assert.Empty(queue.Tick(world));
        Assert.Equal(OrderChange.CidBuild, queue.Begin(world, 11, 30));
        world.Apply(new ShipStatusMessage { ShipId = 11, SectorId = 1, StationId = -1, ParentId = -1, State = ShipStatusMessage.StateFlying });
        queue.Begin(world, 11, 30); queue.Cancel(11);
        world.Apply(new ShipStatusMessage { ShipId = 11, SectorId = 3, StationId = -1, ParentId = -1, State = ShipStatusMessage.StateFlying });
        Assert.Empty(queue.Tick(world));
        world.Apply(new ShipStatusMessage { ShipId = 11, SectorId = 1, StationId = -1, ParentId = -1, State = ShipStatusMessage.StateFlying });
        queue.Begin(world, 11, 30);
        world.Apply(new JoinSide { ShipId = 10, SideId = 1 });
        Assert.Empty(queue.Tick(world));
    }

    [Theory]
    [InlineData(false, (int)Game1.TargetKind.Ship, OrderChange.CidAttack)]
    [InlineData(true, (int)Game1.TargetKind.Ship, OrderChange.CidDefend)]
    [InlineData(false, (int)Game1.TargetKind.Station, OrderChange.CidAttack)]
    [InlineData(true, (int)Game1.TargetKind.Station, OrderChange.CidGoto)]
    [InlineData(false, (int)Game1.TargetKind.Warp, OrderChange.CidGoto)]
    public void Combat_pilots_receive_native_command_chat_defaults(bool friendly, int kind, short command)
    {
        var order = Game1.CommandOrderResolver.ResolveChat(DataHullTypeIGC.HabmFighter, Game1.TargetKind.Ship, (Game1.TargetKind)kind, 123, friendly);
        Assert.Equal(command, order!.Value.CommandId);
        Assert.Equal(123, order.Value.ObjectId);
    }
}
