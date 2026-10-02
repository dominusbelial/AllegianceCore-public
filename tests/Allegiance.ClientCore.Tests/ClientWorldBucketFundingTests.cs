using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P487 — the client's per-side/per-bucket funding snapshot from
/// <c>BUCKET_STATUS(65)</c>, the data source of the investment pane's progress
/// bars. Deterministic (construct-then-Apply, no sockets), pinning: the exact
/// money + build-time it stores per (side, bucket); independence across sides and
/// buckets; and that applying bucket-status <b>never touches the wallet</b> — the
/// investment flow's no-double-debit guarantee (the server's MONEY_CHANGE(60) is
/// the only debit; the pane just sends BUCKET_DONATE).
/// </summary>
public class ClientWorldBucketFundingTests
{
    [Fact]
    public void BucketStatus_StoresMoneyAndTime_PerSideAndBucket()
    {
        var world = new ClientWorld();

        world.Apply(new BucketStatus { SideId = 1, IBucket = 5, MoneyTotal = 1200, TimeTotal = 7300 });

        var f = world.BucketFundingOf(1, 5);
        Assert.NotNull(f);
        Assert.Equal(1200, f!.Value.InvestedMoney);
        Assert.Equal(7300, f.Value.InvestedTimeMs);
        Assert.Single(world.BucketFundingFor(1));
        Assert.Null(world.BucketFundingOf(1, 99));   // unknown bucket
        Assert.Null(world.BucketFundingOf(2, 5));    // different side
        Assert.Empty(world.BucketFundingFor(2));     // side with no status yet
    }

    [Fact]
    public void BucketStatus_UpdatesInPlace_AndSeparatesSides()
    {
        var world = new ClientWorld();

        world.Apply(new BucketStatus { SideId = 1, IBucket = 5, MoneyTotal = 100, TimeTotal = 1 });
        world.Apply(new BucketStatus { SideId = 1, IBucket = 5, MoneyTotal = 200, TimeTotal = 2 });   // re-broadcast
        world.Apply(new BucketStatus { SideId = 2, IBucket = 5, MoneyTotal = 999, TimeTotal = 999 });

        Assert.Equal(200, world.BucketFundingOf(1, 5)!.Value.InvestedMoney);  // last wins
        Assert.Equal(999, world.BucketFundingOf(2, 5)!.Value.InvestedMoney);
        Assert.Single(world.BucketFundingFor(1));
        Assert.Single(world.BucketFundingFor(2));
    }

    [Fact]
    public void BucketStatus_DoesNotTouchTheWallet()
    {
        // Establish a wallet for the local ship (SET_CLUSTER seats MyShipId, then
        // SET_MONEY sets the absolute wallet), then apply a bucket donation's
        // status and assert the wallet is UNCHANGED: the pane's Invest does not
        // locally debit — the server's MONEY_CHANGE is the sole wallet writer.
        var world = new ClientWorld();
        const short me = 42;
        world.Apply(new SetCluster
        {
            SectorId = 7,
            Ship = ServerShipUpdate.FromModel(
                me,
                new Ship(me, MakeHull(), Vector3.Zero, Vector3.Zero, Orientation.Identity),
                99u),
            Cookie = 1,
        });
        world.Apply(new SetMoney { ShipId = me, Money = 5000 });
        Assert.Equal(5000, world.MyMoney);

        world.Apply(new BucketStatus { SideId = 3, IBucket = 2, MoneyTotal = 9000, TimeTotal = 733 });

        Assert.Equal(5000, world.MyMoney);   // wallet untouched by bucket status
        Assert.Equal(9000, world.BucketFundingOf(3, 2)!.Value.InvestedMoney);
    }

    private static DataHullTypeIGC MakeHull() => new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 550f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.5f, 4.5f, 4.5f],
        TurnTorques: [5000f, 5000f, 5000f],
        Thrust: 52500f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 1000f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 741, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 758f, HardpointOffset: 740,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);
}
