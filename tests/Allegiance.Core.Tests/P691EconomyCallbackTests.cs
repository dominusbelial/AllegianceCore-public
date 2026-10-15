using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Host = Allegiance.Server.ServerHost;

namespace Allegiance.Core.Tests;

public sealed class P691EconomyCallbackTests
{
    private static StaticCore Core() => StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
    private static byte[] Mask(byte bits)
    {
        var result = new byte[TechTreeBitMask.ByteCount]; result[0] = bits; return result;
    }
    private static Mission World(Host site, StaticCore core) => Mission.Assemble(core,
        MissionMap.FromLists([DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = 0 }], [], [], []),
        [new SideSpec(0, "A", core.Civilizations.Keys.First()), new SideSpec(1, "B", core.Civilizations.Keys.First())], site);
    private static Station Base(short id, short side, byte effects) => new(
        DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
        { StationId = id, SideId = side, ClusterId = 0, Position = [123, 456, 789], Name = "Base" },
        DataStationTypeIGC.Decode(new byte[DataStationTypeIGC.Size]) with
        { StationTypeId = id, TtbmEffects = Mask(effects), MaxArmorHitPoints = 100, MaxShieldHitPoints = 100 });

    // Igc/bucketigc.h:44-73: notify after money/time clamp, only on an integer change.
    [Fact]
    public void Progress_notifies_on_integer_changes_after_the_funding_clamp_and_completion()
    {
        var host = new Host(); var bucket = new Bucket(host, 1, BucketItemType.BitDevelopment, 100, 10, "research", "");
        var seen = new List<(BucketChange Kind, int Percent)>();
        host.BucketChanged += (kind, b) => seen.Add((kind, b.PercentComplete));
        host.BucketCompletedHook = (b, _) => seen.Add((BucketChange.Terminated, ((Bucket)b).PercentComplete));
        bucket.Update(1); Assert.Empty(seen); // unfunded time does not accrue
        bucket.AddMoney(50); seen.Clear();
        bucket.Update(1.05); Assert.Empty(seen);
        bucket.Update(1.15); Assert.Equal([(BucketChange.PercentCompleteChange, 1)], seen);
        bucket.Update(100); Assert.Equal((BucketChange.PercentCompleteChange, 50), seen.Last());
        int count = seen.Count; bucket.Update(101); Assert.Equal(count, seen.Count);
        bucket.AddMoney(50); seen.Clear(); bucket.Update(106);
        Assert.Equal(BucketChange.Terminated, seen[0].Kind); // completion precedes progress callback
        Assert.Equal((BucketChange.PercentCompleteChange, 100), seen.Last());
        Assert.True(bucket.Completed); seen.Clear(); bucket.Update(200); Assert.Empty(seen);
    }

    // Igc/bucketigc.h:274-299; bucketigc.cpp:87-101.
    [SkippableFact, Trait("Requires", "Artwork")]
    public void Money_snapshot_empty_and_termination_callbacks_observe_updated_state_and_live_owner()
    {
        Require.Artwork(); var core = Core(); var host = new Host();
        var bucket = new Bucket(host, 7, BucketItemType.BitDevelopment, 100, 10, "research", "");
        var side = new Side(0, "A", core.Civilizations.Values.First(), [bucket]);
        var changes = new List<BucketChange>();
        host.BucketChanged += (kind, b) =>
        {
            Assert.Same(side, b.Side); Assert.Contains(b, side.Buckets); changes.Add(kind);
            if (kind == BucketChange.Emptied) { Assert.Equal(0, b.InvestedMoney); Assert.Equal(0, b.InvestedMilliseconds); }
        };
        Assert.Equal(100, bucket.AddMoney(150));
        Assert.Equal(0, bucket.AddMoney(1)); // native still notifies on capped zero donation
        bucket.SetTimeAndMoney(1250, 30);
        Assert.Equal(1250, bucket.InvestedMilliseconds); Assert.Equal(30, bucket.InvestedMoney);
        bucket.SetEmpty(); bucket.Terminate(); bucket.Terminate(); bucket.Update(999);
        Assert.Equal([BucketChange.MoneyChange, BucketChange.MoneyChange, BucketChange.MoneyChange,
            BucketChange.Emptied, BucketChange.Terminated], changes);
        Assert.Empty(side.Buckets); Assert.Null(bucket.Side);
    }

    // FedSrv/FedSrv.CPP:11327-11340; Igc/sideigc.h:384-450.
    [SkippableFact, Trait("Requires", "Artwork")]
    public void Typed_completion_stocks_hulls_and_parts_separately_per_side_and_type()
    {
        Require.Artwork(); var core = Core(); var host = new Host(); var mission = World(host, core);
        var a = mission.Sides[0]; var b = mission.Sides[1];
        var hull = core.HullTypes.Values.First() with { HullId = 123 };
        var part = core.PartTypes.Values.First() with { PartId = 123 };
        IIgcSite site = host;
        site.HullTypeCompleted(a, hull); site.HullTypeCompleted(a, hull); site.PartTypeCompleted(a, part);
        Assert.Equal(2, a.GetStockpile(IgcObjectType.HullType, 123));
        Assert.Equal(1, a.GetStockpile(IgcObjectType.PartType, 123));
        Assert.Equal(0, b.GetStockpile(IgcObjectType.HullType, 123));
        Assert.Empty(mission.Sectors[0].Models.OfType<Ship>()); // completion is inventory, never a spawn
        Assert.Equal(1, a.RemoveFromStockpile(IgcObjectType.HullType, 123, 1));
        Assert.Equal(1, a.RemoveFromStockpile(IgcObjectType.HullType, 123, 100));
        Assert.Equal(0, a.RemoveFromStockpile(IgcObjectType.HullType, 123, 1));
        Assert.Equal(0, a.GetStockpile(IgcObjectType.HullType, 123));
    }

    // Igc/launchertypeigc.h:130-142: GetAmount(NULL) uses the authored amount, not ship capacity.
    [SkippableFact, Trait("Requires", "Artwork")]
    public void Launcher_completion_uses_loaded_amount_for_raw_and_mountable_forms()
    {
        Require.Artwork(); var core = Core(); var host = new Host(); var side = World(host, core).Sides[0];
        var launcher = core.Launchers.Values.First(l => l.Amount > 1 && core.TryBuildExpendableLauncher(l.PartId, out _));
        Assert.True(core.TryBuildExpendableLauncher(launcher.PartId, out var part));
        IIgcSite site = host; site.PartTypeCompleted(side, launcher); site.PartTypeCompleted(side, part);
        Assert.Equal(2 * launcher.Amount, side.GetStockpile(IgcObjectType.PartType, launcher.PartId));
        var pack = core.PartTypes.Values.First(p => p.Type is DataPartTypeIGC.Pack { Amount: > 1 });
        site.PartTypeCompleted(side, pack);
        Assert.Equal(1, side.GetStockpile(IgcObjectType.PartType, pack.PartId)); // packs are not launchers
    }

    // Igc/bucketigc.h:243-256. Native catalog loops remain disabled (sideigc.cpp:257-311).
    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(true)] [InlineData(false)]
    public void Explicit_stock_bucket_invokes_typed_callback_resets_and_can_be_purchased_again(bool hull)
    {
        Require.Artwork(); var core = Core(); var host = new Host(); var h = core.HullTypes.Values.First();
        var p = core.PartTypes.Values.First();
        var bucket = new Bucket(host, 1, hull ? BucketItemType.BitHullType : BucketItemType.BitPartType,
            100, 10, "stock", "", hullType: hull ? h : null, partType: hull ? null : p);
        var side = new Side(0, "A", core.Civilizations.Values.First(), [bucket]);
        for (int purchase = 1; purchase <= 2; purchase++)
        {
            bucket.AddMoney(100); bucket.Update(purchase * 10);
            Assert.Equal(purchase, side.GetStockpile(hull ? IgcObjectType.HullType : IgcObjectType.PartType, hull ? h.HullId : p.PartId));
            Assert.Equal(0, bucket.InvestedMoney); Assert.Equal(0, bucket.InvestedMilliseconds); Assert.False(bucket.Completed);
        }
    }

    // Igc/sideigc.h:142-168: shared providers suppress false mask changes.
    [SkippableFact, Trait("Requires", "Artwork")]
    public void Initial_runtime_stations_notify_each_distinct_tech_gain_before_the_station_event()
    {
        Require.Artwork(); var core = Core(); var host = new Host();
        var first = core.StationTypes.Values.First(t => !TechTreeBitMask.FromBytes(t.TtbmEffects).IsEmpty);
        var firstMask = TechTreeBitMask.FromBytes(first.TtbmEffects);
        var second = core.StationTypes.Values.First(t => !TechTreeBitMask.FromBytes(t.TtbmEffects).IsSubsetOf(firstMask));
        var masks = new List<TechTreeBitMask>(); var order = new List<string>();
        host.BuildingTechChanged += side => { masks.Add(side.BuildingTechs); order.Add("tech"); };
        host.StationTypeChanged += station =>
        {
            Assert.Contains(station, station.Mission!.Sectors[0].StationRuntimes);
            order.Add("station");
        };
        Mission.Assemble(core, MissionMap.FromLists(
            [DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = 0 }], [],
            [Base(1, 0, 0).Data with { StationTypeId = first.StationTypeId },
             Base(2, 0, 0).Data with { StationTypeId = second.StationTypeId }], []),
            [new SideSpec(0, "A", core.Civilizations.Keys.First())], host);
        Assert.Equal(["tech", "station", "tech", "station"], order);
        Assert.Equal([firstMask, firstMask.Or(TechTreeBitMask.FromBytes(second.TtbmEffects))], masks);
    }

    // Igc/sideigc.h:142-168: shared providers suppress false mask changes.
    [SkippableFact, Trait("Requires", "Artwork")]
    public void Building_tech_callbacks_follow_registration_and_only_last_provider_removal_changes_mask()
    {
        Require.Artwork(); var host = new Host(); var mission = World(host, Core()); var sector = mission.Sectors[0];
        var masks = new List<TechTreeBitMask>();
        host.BuildingTechChanged += side => masks.Add(side.BuildingTechs);
        var first = Base(1, 0, 0x80); var second = Base(2, 0, 0x80);
        mission.PlaceStation(first, sector); mission.PlaceStation(second, sector);
        Assert.Single(masks); Assert.Equal(TechTreeBitMask.FromBytes(Mask(0x80)), masks[0]);
        sector.RemoveStation(first); Assert.Single(masks);
        sector.RemoveStation(second); Assert.Equal(2, masks.Count); Assert.True(masks.Last().IsEmpty);
        sector.RemoveStation(second); Assert.Equal(2, masks.Count);
    }

    // Igc/stationIGC.cpp:210-276: updated type and side techs precede type notification.
    [SkippableFact, Trait("Requires", "Artwork")]
    public void Station_type_change_preserves_identity_pose_and_damage_and_notifies_in_native_order()
    {
        Require.Artwork(); var host = new Host(); var mission = World(host, Core()); var sector = mission.Sectors[0];
        var station = Base(1, 0, 0x80); mission.PlaceStation(station, sector);
        station.Capture(1); station.Capture(0);
        Assert.Equal(0.25f, station.ArmorFraction);
        var before = station.Data; float armor = station.ArmorFraction, shield = station.ShieldFraction;
        var order = new List<string>();
        host.BuildingTechChanged += _ => order.Add("tech");
        host.StationTypeChanged += changed =>
        {
            Assert.Same(station, changed); Assert.Equal((short)2, changed.Type.StationTypeId);
            Assert.Equal((short)2, sector.Stations.Single().StationTypeId);
            Assert.Equal(TechTreeBitMask.FromBytes(Mask(0x40)), mission.Sides[0].BuildingTechs);
            order.Add("station");
        };
        station.SetBaseStationType(station.Type with { StationTypeId = 2, TtbmEffects = Mask(0x40), MaxArmorHitPoints = 200 });
        Assert.Equal(["tech", "station"], order);
        Assert.Equal(before.Position, station.Data.Position); Assert.Equal(before.Forward, station.Data.Forward);
        Assert.Equal(before.StationId, station.StationId); Assert.Equal(armor, station.ArmorFraction); Assert.Equal(shield, station.ShieldFraction);
        Assert.Equal(200, station.MaxArmor);
        order.Clear(); station.SetBaseStationType(station.Type); Assert.Equal(["station"], order);
    }

    // Igc/sideigc.h:266-287: old owner's tech loss and new owner's gain follow SetSide.
    [SkippableFact, Trait("Requires", "Artwork")]
    public void Capture_notifies_both_owners_with_the_post_capture_building_mask()
    {
        Require.Artwork(); var host = new Host(); var mission = World(host, Core()); var station = Base(1, 0, 0x80);
        mission.PlaceStation(station, mission.Sectors[0]); var seen = new List<(short, bool)>();
        host.BuildingTechChanged += side => seen.Add((side.Id, side.BuildingTechs.IsEmpty));
        station.Capture(1);
        Assert.Equal([(0, true), (1, false)], seen.Select(s => ((int)s.Item1, s.Item2)));
        Assert.Equal(1, mission.Sectors[0].Stations.Single().SideId);
    }
}
