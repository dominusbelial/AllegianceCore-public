using System;
using System.Collections.Generic;
using Allegiance.Core.Model;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P487 — pure coverage of <see cref="InvestmentPaneModel"/>: the client's port
/// of native <c>PurchasesPaneImpl::CanInvest</c> (loadout.cpp:1095-1142), the
/// Invest / partial-invest amounts (OnInvest :807-826, OnPartial :883-914), the
/// Show-all / Hide-completed list filter (:1004-1006), and that Invest never
/// locally debits a wallet (the amounts are handed back for the host to send via
/// BUCKET_DONATE; the server's MONEY_CHANGE is the debit). No graphics device.
/// </summary>
public class InvestmentPaneModelTests
{
    private static InvestmentBucket Dev(short id, int price, uint ttb = 30) =>
        new(id, BucketItemType.BitDevelopment, $"dev{id}", price, ttb, -1, -1, id, -1);

    private static InvestmentBucket Drone(short id, int price, short hullId, uint ttb = 40) =>
        new(id, BucketItemType.BitDroneType, $"drone{id}", price, ttb, hullId, id, -1, -1);

    private static InvestmentBucket Station(short id, int price, short stid, uint ttb = 60) =>
        new(id, BucketItemType.BitStationType, $"st{id}", price, ttb, -1, -1, -1, stid);

    private static InvestmentPaneModel Model(
        IReadOnlyList<InvestmentBucket> defs,
        bool missionInProgress = true,
        bool hasSide = true,
        int? money = 5000,
        IReadOnlyDictionary<short, int>? droneCounts = null,
        int maxDrones = 4,
        Func<short, bool>? stationCanBuild = null,
        IReadOnlyDictionary<short, ClientWorld.BucketFunding>? funding = null,
        bool showAll = false,
        bool hideCompleted = true) =>
        new(defs, funding, missionInProgress, hasSide, money, droneCounts, maxDrones,
            stationCanBuild ?? (_ => true), showAll, hideCompleted);

    // ── CanInvest gates ────────────────────────────────────────────────────

    [Fact]
    public void CanInvest_RequiresMissionInProgress()
    {
        var b = Dev(1, 1000);
        var m = Model([b], missionInProgress: false);
        Assert.False(m.CanInvest(b));
    }

    [Fact]
    public void CanInvest_RequiresASide()
    {
        var b = Dev(1, 1000);
        var m = Model([b], hasSide: false);
        Assert.False(m.CanInvest(b));
    }

    [Fact]
    public void CanInvest_RequiresPositiveMoney()
    {
        var b = Dev(1, 1000);
        Assert.False(Model([b], money: 0).CanInvest(b));
        Assert.False(Model([b], money: -5).CanInvest(b));
        Assert.True(Model([b], money: 1).CanInvest(b));
    }

    [Fact]
    public void CanInvest_CompletedBucket_Refused()
    {
        var b = Dev(1, 1000, ttb: 30);
        var funding = new Dictionary<short, ClientWorld.BucketFunding> { [b.Id] = new(1000, 30_000) };
        Assert.True(Model([b], funding: funding).Completed(b));
        Assert.False(Model([b], funding: funding).CanInvest(b));
    }

    [Fact]
    public void CanInvest_DroneCapReached_Refused()
    {
        var b = Drone(2, 1500, hullId: 501);
        var atCap = Model([b], droneCounts: new Dictionary<short, int> { [501] = 4 }, maxDrones: 4);
        var belowCap = Model([b], droneCounts: new Dictionary<short, int> { [501] = 3 }, maxDrones: 4);
        Assert.False(atCap.CanInvest(b));
        Assert.True(belowCap.CanInvest(b));
    }

    [Fact]
    public void CanInvest_StationWithoutBuildSpot_Refused()
    {
        var b = Station(3, 2000, stid: 7);
        var noSpot = Model([b], stationCanBuild: _ => false);
        var withSpot = Model([b], stationCanBuild: _ => true);
        Assert.False(noSpot.CanInvest(b));
        Assert.True(withSpot.CanInvest(b));
    }

    [Fact]
    public void CanInvest_NormalBucket_True()
    {
        var b = Dev(1, 1000);
        Assert.True(Model([b]).CanInvest(b));
    }

    // ── Invest / partial amounts ───────────────────────────────────────────

    [Fact]
    public void InvestAmount_IsMinOfRemainingAndWallet()
    {
        // Wallet 5000 vs remaining 700 → invests 700 (all that's left owed).
        var b = Dev(1, 1000);
        var funding = new Dictionary<short, ClientWorld.BucketFunding> { [b.Id] = new(300, 0) };
        Assert.Equal(700, Model([b], money: 5000, funding: funding).InvestAmount(b));
        // Wallet 200 vs remaining 700 → invests only the wallet.
        Assert.Equal(200, Model([b], money: 200, funding: funding).InvestAmount(b));
    }

    [Fact]
    public void PartialInvest_IsTenPercentOfTotal_CappedByWallet_NeverCompletes()
    {
        var b = Dev(1, 1000);
        // price 1000 → 10% = 100; wallet 5000, funding 0 → 100 (never completes).
        Assert.Equal(100, Model([b], money: 5000).PartialInvestAmount(b));
        // Tiny wallet caps it below 10%.
        Assert.Equal(30, Model([b], money: 30).PartialInvestAmount(b));
    }

    [Fact]
    public void PartialInvest_LeavesAtLeastOneCr_WhenNearCompletion()
    {
        var b = Dev(1, 1000);
        // Funded 950, remaining 50; 10% = 100 but must not complete: while
        // 950+amount >= 1000 subtract → 100→49 (950+49=999 < 1000).
        var funding = new Dictionary<short, ClientWorld.BucketFunding> { [b.Id] = new(950, 0) };
        Assert.Equal(49, Model([b], money: 5000, funding: funding).PartialInvestAmount(b));
    }

    [Fact]
    public void PartialInvest_WhenOneCrRemaining_ReturnsZero()
    {
        var b = Dev(1, 1000);
        // remaining == 1 → OnPartial refuses to spend (would finish the bucket).
        var funding = new Dictionary<short, ClientWorld.BucketFunding> { [b.Id] = new(999, 0) };
        Assert.Equal(0, Model([b], money: 5000, funding: funding).PartialInvestAmount(b));
    }

    // ── Show-all / Hide-completed filter ───────────────────────────────────

    [Fact]
    public void HideCompleted_DefaultHidesCompletedBuckets_NonCompletedStillShows()
    {
        var done = Dev(1, 1000, ttb: 30);
        var open = Dev(2, 1000, ttb: 30);
        var funding = new Dictionary<short, ClientWorld.BucketFunding>
        {
            [done.Id] = new(1000, 30_000),   // completed
            [open.Id] = new(0, 0),           // unfunded → CanBuy
        };
        var m = Model([done, open], funding: funding);   // hideCompleted defaults on
        var items = m.Items;
        Assert.Single(items);              // only the still-buyable one shows
        Assert.Equal(open.Id, items[0].Id);
    }

    [Fact]
    public void ShowAll_NeedsHideCompletedOff_ToShowCompletedBuckets()
    {
        var b = Dev(1, 1000, ttb: 30);
        var funding = new Dictionary<short, ClientWorld.BucketFunding> { [b.Id] = new(1000, 30_000) }; // completed
        // Native: bDisplay = (CanBuy || ShowAll); if (HideCompleted && Complete) bDisplay=false.
        // So Hide-completed DOMINATES even when Show-all is on...
        var m = Model([b], funding: funding, hideCompleted: true, showAll: true);
        Assert.Empty(m.Items);
        // ...and only Show-all WITH hide-completed off reveals the completed bucket.
        var m2 = Model([b], funding: funding, hideCompleted: false, showAll: true);
        Assert.Single(m2.Items);
    }

    [Fact]
    public void Items_PreservesSynthesisOrder()
    {
        var dev = Dev(2, 1000);
        var drone = Drone(3, 1500, 501);
        var station = Station(4, 2000, 7);
        var m = Model([new InvestmentBucket(1, BucketItemType.BitShipType, "bomber", 5800, 30, 504, -1, -1, -1), dev, drone, station]);
        var items = m.Items;
        Assert.Equal(4, items.Count);
        Assert.Equal(BucketItemType.BitShipType, items[0].ItemType);
        Assert.Equal(BucketItemType.BitDevelopment, items[1].ItemType);
        Assert.Equal(BucketItemType.BitDroneType, items[2].ItemType);
        Assert.Equal(BucketItemType.BitStationType, items[3].ItemType);
    }

    // ── TryInvest / TryPartial routing ─────────────────────────────────────

    [Fact]
    public void TryInvest_HandsBackAmount_ForCursorBucket()
    {
        var b = Dev(1, 1000);
        var m = Model([b]);
        Assert.True(m.TryInvest(out var id, out var amt));
        Assert.Equal(b.Id, id);
        Assert.Equal(1000, amt);   // wallet 5000 >= remaining 1000
    }

    [Fact]
    public void TryInvest_Blocks_WhenNotInvestable()
    {
        var b = Dev(1, 1000);
        var noMoney = Model([b], money: 0);
        Assert.False(noMoney.TryInvest(out _, out _));
    }

    [Fact]
    public void TryPartialInvest_HandsBackPartialAmount()
    {
        var b = Dev(1, 1000);
        var m = Model([b]);
        Assert.True(m.TryPartialInvest(out var id, out var amt));
        Assert.Equal(b.Id, id);
        Assert.Equal(100, amt);    // 10% of 1000
    }
}
