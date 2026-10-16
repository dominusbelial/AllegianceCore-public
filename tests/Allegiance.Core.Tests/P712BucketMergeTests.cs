using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Host = Allegiance.Server.ServerHost;

namespace Allegiance.Core.Tests;

public sealed class P712BucketMergeTests
{
    private static byte[] Mask(byte bits)
    {
        var result = new byte[TechTreeBitMask.ByteCount];
        result[0] = bits;
        return result;
    }

    private static DataDevelopmentIGC Development(short id, byte effects = 0, byte required = 0, bool techOnly = false) =>
        DataDevelopmentIGC.Decode(new byte[DataDevelopmentIGC.Size]) with
        {
            DevelopmentId = id, TtbmEffects = Mask(effects), TtbmRequired = Mask(required),
            Gas = Enumerable.Repeat(techOnly ? 1f : 2f, GlobalAttributeSet.Count).ToArray(),
        };

    private static (Mission Mission, Side Side, Host Site) World(Func<Host, Bucket[]> buckets)
    {
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
        var site = new Host();
        var mission = Mission.Assemble(core, MissionMap.FromLists(
            [DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = 0 }], [], [], []), [], site);
        var side = new Side(0, "merge regression", core.Civilizations.Values.First(), buckets(site))
        { Mission = mission, DevelopmentTechs = TechTreeBitMask.Empty };
        return (mission, side, site);
    }

    // Igc/sideigc.h:43-49,194-204,629-633; bucketigc.cpp:87-103.
    [SkippableTheory]
    [InlineData(0)] // removing a previous entry used to shift the current index
    [InlineData(1)] // removing the completing entry must not skip its successor
    [InlineData(3)] // removing a future entry must prevent its update
    public void Development_completion_adjusts_obsolete_research_and_termination_never_skips_a_survivor(int victim)
    {
        Require.Artwork();
        var (_, side, site) = World(host =>
        [
            new(host, 1, BucketItemType.BitDevelopment, 100, 20, "previous", "", development: Development(1)),
            new(host, 2, BucketItemType.BitDevelopment, 100, 1, "completing", "", development: Development(2, 2, techOnly: true)),
            new(host, 3, BucketItemType.BitDevelopment, 100, 1, "obsolete", "", development: Development(3, 2, techOnly: true)),
            new(host, 4, BucketItemType.BitDevelopment, 100, 1, "removed future", "", development: Development(4)),
            new(host, 5, BucketItemType.BitDevelopment, 100, 20, "survivor", "", development: Development(5)),
        ]);
        var original = side.Buckets.ToArray();
        side.Update(0);
        foreach (var bucket in original) bucket.AddMoney(100);
        var completed = new List<short>();
        site.BucketCompletedHook = (b, _) =>
        {
            completed.Add(((Bucket)b).Id);
            Assert.Same(original[1], b);
            side.DevelopmentTechs = side.DevelopmentTechs.Or(TechTreeBitMask.FromBytes(Mask(2)));
        };
        site.BucketChanged += (kind, b) =>
        {
            if (kind != BucketChange.MoneyChange || !ReferenceEquals(b, original[2])) return;
            // This callback runs inside AdjustBuckets, after the development callback.
            Assert.True(b.Completed);
            original[victim].Terminate();
            original[3].Terminate();
        };
        side.Update(1);
        Assert.Equal([(short)2], completed);
        Assert.True(original[2].Completed);
        Assert.Equal(1000, original[2].InvestedMilliseconds);
        Assert.Equal(100, original[2].InvestedMoney);
        Assert.Null(original[victim].Side);
        Assert.Null(original[3].Side);
        Assert.Equal(0, original[3].InvestedMilliseconds);
        Assert.Equal(1000, original[4].InvestedMilliseconds);
        if (victim != 0) Assert.Equal(1000, original[0].InvestedMilliseconds);
        side.Update(2);
        Assert.Equal([(short)2], completed);
        Assert.Equal(2000, original[4].InvestedMilliseconds);
    }

    // Igc/sideigc.h:160-168,635-636: reconcile building-tech loss in the same pass too.
    [SkippableFact]
    public void Development_completion_that_removes_a_base_empties_later_research_before_completion()
    {
        Require.Artwork();
        var (mission, side, site) = World(host =>
        [
            new(host, 1, BucketItemType.BitDevelopment, 100, 1, "completion", "", development: Development(1)),
            new(host, 2, BucketItemType.BitDevelopment, 100, 1, "needs base", "", development: Development(2, required: 4)),
            new(host, 3, BucketItemType.BitDevelopment, 100, 20, "survivor", "", development: Development(3)),
        ]);
        var station = new Station(DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
            { StationId = 1, SideId = 0, ClusterId = 0 },
            DataStationTypeIGC.Decode(new byte[DataStationTypeIGC.Size]) with
            { TtbmEffects = Mask(4), MaxArmorHitPoints = 100 });
        mission.PlaceStation(station, mission.Sectors[0]);
        side.Update(0);
        var original = side.Buckets.ToArray();
        foreach (var bucket in original) bucket.AddMoney(100);
        var completed = new List<short>();
        site.BucketCompletedHook = (b, _) =>
        {
            completed.Add(((Bucket)b).Id);
            mission.Sectors[0].RemoveStation(station);
        };
        site.BucketChanged += (kind, b) =>
        {
            if (kind == BucketChange.Emptied && ReferenceEquals(b, original[1])) b.Terminate();
        };
        side.Update(1);
        Assert.Equal([(short)1], completed);
        Assert.Equal(0, original[1].InvestedMoney);
        Assert.Equal(0, original[1].InvestedMilliseconds);
        Assert.False(original[1].Completed);
        Assert.Null(original[1].Side);
        Assert.Equal(1000, original[2].InvestedMilliseconds);
        side.Update(2);
        Assert.Equal([(short)1], completed);
        Assert.Equal(2000, original[2].InvestedMilliseconds);
    }

    // Igc/bucketigc.h:175-178,274-299: snapshots derive completion from time;
    // SetEmpty and a partial replacement both allow a subsequent real purchase.
    [Fact]
    public void Funding_snapshots_and_empty_share_one_completion_state_without_firing_effects()
    {
        var site = new Host();
        var bucket = new Bucket(site, 1, BucketItemType.BitDevelopment, 100, 1, "snapshot", "");
        int completions = 0;
        site.BucketCompletedHook = (_, _) => completions++;
        bucket.SetTimeAndMoney(1000, 100);
        Assert.True(bucket.Completed);
        bucket.Update(1);
        Assert.Equal(0, completions);
        bucket.SetTimeAndMoney(0, 100);
        Assert.False(bucket.Completed);
        bucket.Update(2);
        Assert.True(bucket.Completed);
        Assert.Equal(1, completions);
        bucket.SetEmpty();
        Assert.False(bucket.Completed);
        bucket.AddMoney(100);
        bucket.Update(3);
        Assert.Equal(2, completions);
    }
}
