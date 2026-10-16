using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
public sealed class P691BucketProgressLiveTests
{
    // Igc/bucketigc.h:64-68,294-299 -> clintlib/clintlib.cpp:2859-2862.
    // The port's client consumes BUCKET_STATUS rather than simulating buckets.
    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(false)] [InlineData(true)]
    public void Integer_progress_and_empty_use_the_native_side_or_prosperity_audience(bool prosperity)
    {
        Require.Artwork(); using var server = new Srv(); server.SpawnInitialMinersAtBoot = false;
        server.Boot(P660NextMatchLiveTests.Config(false));
        using var owner = P660NextMatchLiveTests.Connect(server, "P691-investor", 69101);
        using var enemy = P660NextMatchLiveTests.Connect(server, "P691-enemy", 69102);
        var mission = server.Missions[0]; mission.Stage = MissionStage.Started;
        short sideId = owner.World.MySideId!.Value;
        Assert.NotEqual(sideId, enemy.World.MySideId);
        var side = mission.Sides.Single(s => s.Id == sideId);
        var bucket = side.Buckets.First(b => b.TimeToBuild > 5 && b.Price > 0 && side.CanBuy(b)
            && (b.Development is { DevelopmentId: Mission.TeamMoneyDevelopmentId }) == prosperity);
        server.Ships.Single(s => s.Ship.Id == owner.World.MyShipId).Wallet.Money = bucket.Price;
        owner.SendBucketDonate(bucket.Id, bucket.Price);
        P660NextMatchLiveTests.Pump(server, () => P660NextMatchLiveTests.Read(owner,
            () => owner.World.BucketFundingOf(sideId, bucket.Id) is { InvestedTimeMs: > 1000 }));
        var funded = owner.World.BucketFundingOf(sideId, bucket.Id)!.Value;
        Assert.Equal(bucket.Price, funded.InvestedMoney);
        Assert.InRange(funded.InvestedTimeMs, 1000, bucket.TimeToBuild * 1000);
        bucket.SetEmpty();
        P660NextMatchLiveTests.Pump(server, () => P660NextMatchLiveTests.Read(owner,
            () => owner.World.BucketFundingOf(sideId, bucket.Id) is { InvestedMoney: 0, InvestedTimeMs: 0 }));
        // Drain the enemy's ordered stream before the negative audience assertion.
        enemy.TakePingEchoes(); enemy.SendPing();
        P660NextMatchLiveTests.Pump(server, () => enemy.TakePingEchoes().Count > 0);
        if (prosperity)
            P660NextMatchLiveTests.Pump(server, () => P660NextMatchLiveTests.Read(enemy,
                () => enemy.World.BucketFundingOf(sideId, bucket.Id) is { InvestedMoney: 0, InvestedTimeMs: 0 }));
        else
            Assert.True(P660NextMatchLiveTests.Read(enemy, () => enemy.World.BucketFundingOf(sideId, bucket.Id) is null));
    }
}
