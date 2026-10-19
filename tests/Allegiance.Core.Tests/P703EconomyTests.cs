using Allegiance.Core.Model;
using Allegiance.Core.MapGen;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Allegiance.Server;

namespace Allegiance.Core.Tests;

public sealed class P703EconomyTests
{
    [Theory]
    [InlineData(BucketItemType.BitStationType)]
    [InlineData(BucketItemType.BitDroneType)]
    public void Production_buckets_reset_and_can_be_purchased_again(BucketItemType kind)
    {
        var site = new RecordingIgcSite();
        var bucket = new Bucket(site, 1, kind, 100, 1, "repeatable", "native ForceComplete");
        for (int i = 0; i < 3; i++)
        {
            bucket.Update(i * 2);
            Assert.Equal(100, bucket.AddMoney(100));
            bucket.Update(i * 2 + 1);
            Assert.False(bucket.Completed);
            Assert.Equal(0, bucket.InvestedMoney);
            Assert.Equal(0, bucket.InvestedMilliseconds);
        }
        Assert.Equal(3, site.Calls.Count(c => c.StartsWith("BucketCompleted(")));
        Assert.Equal(3, site.Calls.Count(c => c.StartsWith("BucketChangeEvent(Emptied")));
        var dev = new Bucket(site, 2, BucketItemType.BitDevelopment, 100, 1, "once", "");
        dev.Update(1); dev.AddMoney(100); dev.Update(2);
        Assert.True(dev.Completed); Assert.Equal(100, dev.InvestedMoney);
    }

    [SkippableFact]
    public void Join_catalog_tracks_runtime_asteroid_creation_and_removal()
    {
        Require.Artwork();
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "m2xspiderw.igc")));
        var mission = Mission.Assemble(core, map, [], new NullIgcSite());
        var sector = mission.Sectors.First(s => s.Mineable.Count > 0);
        var old = sector.Mineable[0];
        Assert.True(sector.RemoveAsteroid(old));
        Assert.DoesNotContain(sector.Asteroids, a => a.AsteroidId == old.Data.AsteroidId);
        var added = new Asteroid(old.Data with { AsteroidId = 31000 });
        mission.PlaceAsteroid(added, sector);
        Assert.Contains(sector.Asteroids, a => a.AsteroidId == 31000);
        Assert.Contains(added, sector.Mineable);
        Assert.True(sector.RemoveAsteroid(added));
        Assert.DoesNotContain(sector.Asteroids, a => a.AsteroidId == 31000);
    }

    private static (Mission Mission, Side Side, RecordingIgcSite Site) ResearchWorld()
    {
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
        var map = MissionMapGenerator.Generate(new MapGenParams { Teams = 2, CivIds = [1, 2] }, core, 0);
        var site = new RecordingIgcSite();
        var mission = Mission.Assemble(core, map, [new SideSpec(0, "A", 1), new SideSpec(1, "B", 2)], site);
        return (mission, mission.Sides[0], site);
    }

    [Trait("Tier", "Live")] // Expensive corpus/scenario or collision-work budget; docs/testing/TIERS.md.
    [SkippableFact]
    public void Lost_base_empties_funded_research_before_it_can_finish_and_rebuild_requires_new_money()
    {
        Require.Artwork();
        var (mission, side, site) = ResearchWorld();
        var tree = new ResearchTree(mission.Core, side.Civilization.CivilizationId);
        var state = new ResearchState(tree);
        var sector = mission.Sectors.First(s => s.StationRuntimes.Any(st => st.SideId == side.Id));
        Station? branch = null;
        short id = 20000;
        foreach (var step in tree.Generate("PCore001").Steps)
        {
            state.Apply(step);
            if (step.Action != "station") continue;
            var template = sector.Stations[0] with { StationId = id++, StationTypeId = step.Id };
            var placed = new Station(template, mission.Core.StationTypes[step.Id]);
            mission.PlaceStation(placed, sector);
            if (step.Name is "Supremacy" or "Tactical" or "Expansion" or "Shipyard") { branch = placed; break; }
        }
        Assert.NotNull(branch);
        side.DevelopmentTechs = state.DevelopmentTechs;
        sector.RemoveStation(branch);
        var lostTechs = side.Techs;
        mission.PlaceStation(branch, sector);
        var research = side.Buckets.First(b => b.Development is { } dev && dev.DevelopmentId != Mission.TeamMoneyDevelopmentId && side.CanBuy(b)
            && !TechTreeBitMask.FromBytes(dev.TtbmRequired).IsSubsetOf(lostTechs)
            && !DevelopmentLogic.IsObsolete(dev, side.DevelopmentTechs));
        side.Update(1); research.AddMoney(research.Price); research.Update(2);
        Assert.True(research.InvestedMoney > 0);
        sector.RemoveStation(branch); Assert.False(side.CanBuy(research)); side.Update(3);
        Assert.Equal(0, research.InvestedMoney); Assert.Equal(0, research.InvestedMilliseconds);
        Assert.False(research.Completed);
        Assert.DoesNotContain(site.Calls, c => c.StartsWith("BucketCompleted("));
        mission.PlaceStation(branch, sector); research.AddMoney(research.Price);
        side.Update(4 + research.TimeToBuild);
        Assert.True(research.Completed);
        Assert.Single(site.Calls, c => c.StartsWith("BucketCompleted("));
    }

    [SkippableFact]
    public void Obsolete_station_bucket_transfers_paid_money_and_time_to_its_successor()
    {
        Require.Artwork(); var (mission, side, _) = ResearchWorld();
        var bucket = side.Buckets.First(b => b.StationType is { } st && side.CanBuy(b)
            && mission.Core.StationTypes.ContainsKey(st.SuccessorStationTypeId));
        var successor = mission.Core.StationTypes[bucket.StationType!.Value.SuccessorStationTypeId];
        var future = side.Buckets.Single(b => b.StationType?.StationTypeId == successor.StationTypeId);
        side.Update(1);
        int paid = bucket.Price / 2;
        bucket.AddMoney(paid); side.Update(2);
        long time = bucket.InvestedMilliseconds;
        side.DevelopmentTechs = side.DevelopmentTechs.Or(TechTreeBitMask.FromBytes(successor.TtbmRequired));
        side.Update(3);
        Assert.Equal(0, bucket.InvestedMoney); Assert.Equal(0, bucket.InvestedMilliseconds);
        Assert.Equal(paid, future.InvestedMoney);
        Assert.True(future.InvestedMilliseconds >= time);
    }

    [SkippableFact]
    public void Obsolete_development_is_marked_complete_without_reapplying_completion_effects()
    {
        Require.Artwork(); var (_, side, site) = ResearchWorld();
        var bucket = side.Buckets.First(b => b.Development is { } dev && dev.DevelopmentId != Mission.TeamMoneyDevelopmentId && side.CanBuy(b)
            && DevelopmentLogic.IsTechOnly(dev) && !DevelopmentLogic.IsObsolete(dev, side.DevelopmentTechs));
        side.Update(1); bucket.AddMoney(1);
        side.DevelopmentTechs = side.DevelopmentTechs.Or(TechTreeBitMask.FromBytes(bucket.Development!.Value.TtbmEffects));
        side.Update(2);
        Assert.True(bucket.Completed);
        Assert.Equal(bucket.Price, bucket.InvestedMoney);
        Assert.Equal((long)bucket.TimeToBuild * 1000, bucket.InvestedMilliseconds);
        Assert.DoesNotContain(site.Calls, c => c.StartsWith("BucketCompleted("));
    }

    [SkippableTheory]
    [InlineData(1)]
    [InlineData(50)]
    public void Multiplier_scales_only_periodic_accrual_and_one_is_native(double multiplier)
    {
        Require.Artwork();
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
        var mission = Mission.Assemble(core, MissionMap.FromLists([], [], [], []),
            [new SideSpec(0, "team", 1)], new NullIgcSite());
        mission.ShowcaseIncomeMultiplier = multiplier;
        var side = mission.Sides[0];
        mission.Stage = MissionStage.Started;
        mission.ApplyStartingMoneyAndInitialPayday(1);
        long initial = side.Money;
        double interval = mission.SecondsBetweenPaydays;
        mission.Update(1); mission.Update(1 + interval);
        long expected = (long)(mission.IncomePerPayday * (1f + side.Civilization.IncomeMoney));
        Assert.Equal(initial + (long)(expected * multiplier), side.Money);
        Assert.Equal(1, new ShowcaseConfig().IncomeMultiplier);
        foreach (double invalid in new[] { 0, -1, double.NaN, double.PositiveInfinity, 10001 })
            Assert.Throws<InvalidDataException>(() => new ShowcaseConfig { IncomeMultiplier = invalid }.Validate());
    }
}
