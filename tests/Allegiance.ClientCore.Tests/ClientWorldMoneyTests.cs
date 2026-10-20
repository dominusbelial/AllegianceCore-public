using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P442 — pure (construct-then-Apply) coverage of the client's per-player
/// money derivation. The MonoGame client had no money concept at all; P442 adds
/// per-ship-id wallets to <see cref="ClientWorld"/> fed by the real wire codecs
/// (SET_MONEY(115) absolute, MONEY_CHANGE(60) delta, AUTODONATE(116) gifts and
/// PAYDAY(114) credits) with exactly the native appmsg.cpp semantics. These are
/// deterministic — no sockets, no pumps — so the exact per-message rules and
/// the "donor's own wallet decremented exactly once" guarantee are pinned here;
/// the real-server loopback companions are in <see cref="ClientWalletLiveTests"/>.
/// </summary>
public class ClientWorldMoneyTests
{
    private const short MyId = 812;
    private const short Teammate = 900;
    private const short Other = 951;

    private static DataHullTypeIGC MakeHull() => new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 550f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.5f, 4.5f, 4.5f],
        TurnTorques: [5000f, 5000f, 5000f],
        Thrust: 52500f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 1000f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 801, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 818f, HardpointOffset: 640,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);

    /// <summary>A world where <c>MyShipId</c> is already assigned (the live
    /// server always assigns it before money traffic), via the same
    /// SET_CLUSTER idiom the DockLoadout tests use.</summary>
    private static ClientWorld WorldWithMyShip(short myShipId)
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster
        {
            SectorId = 900,
            Ship = ServerShipUpdate.FromModel(
                myShipId,
                new Ship(myShipId, MakeHull(), Vector3.Zero, Vector3.Zero, Orientation.Identity),
                880u),
            Cookie = 0x80,
        });
        return world;
    }

    // ── SET_MONEY + MyMoney null-until-known ────────────────────────────

    [Fact]
    public void MyMoney_IsNull_UntilTheLocalShipsSetMoneyArrives()
    {
        var world = WorldWithMyShip(MyId);
        // No SET_MONEY for the local ship yet -> unknown, NOT 0.
        Assert.Null(world.MyMoney);

        world.Apply(new SetMoney { Money = 5000, ShipId = MyId });
        Assert.Equal(5000, world.MyMoney);
    }

    [Fact]
    public void MyMoney_IsNull_WhenMyShipIsNotAssignedYet()
    {
        var world = new ClientWorld();
        world.Apply(new SetMoney { Money = 9000, ShipId = Teammate });
        Assert.Null(world.MyMoney);          // no ship of ours -> unknown
        Assert.Equal(9000, world.WalletByShipId[Teammate]);   // per-ship still tracked
    }

    [Fact]
    public void SetMoney_SetsThatShipsWalletAbsolutely()
    {
        var world = WorldWithMyShip(MyId);
        world.Apply(new SetMoney { Money = 1111, ShipId = MyId });
        world.Apply(new SetMoney { Money = 2222, ShipId = Teammate });
        world.Apply(new SetMoney { Money = 3333, ShipId = MyId });   // last write wins (absolute)
        Assert.Equal(3333, world.MyMoney);
        Assert.Equal(2222, world.WalletByShipId[Teammate]);
    }

    // ── MONEY_CHANGE ────────────────────────────────────────────────────

    [Fact]
    public void MoneyChange_BucketSpend_DecrementsDonorsWalletExactlyOnce()
    {
        // The P198 bucket-donation spend broadcast is sidFrom == sidTo == donor
        // (Server.Economy.cs BroadcastBucketDonateSpendSideWide). Native skips
        // it when sidFrom == my ship (already applied locally); P442's
        // server-authoritative client MUST apply it — exactly once, no double.
        var world = WorldWithMyShip(MyId);
        world.Apply(new SetMoney { Money = 1000, ShipId = MyId });

        world.Apply(new MoneyChange { DMoney = -300, SidTo = MyId, SidFrom = MyId });
        Assert.Equal(700, world.MyMoney);   // once, not twice
    }

    [Fact]
    public void MoneyChange_PurchaseBroadcast_SidFromNa_DebitsBuyer()
    {
        // P441's buy broadcast is sidFrom = NA (server-initiated shape). The
        // buyer (our ship) is debited by dMoney.
        var world = WorldWithMyShip(MyId);
        world.Apply(new SetMoney { Money = 1000, ShipId = MyId });

        world.Apply(new MoneyChange { DMoney = -250, SidTo = MyId, SidFrom = -1 /* NA */ });
        Assert.Equal(750, world.MyMoney);
    }

    [Fact]
    public void MoneyChange_BucketRefund_SidFromNa_CreditsDonorAlone()
    {
        // P198 refund MONEY_CHANGE(+excess, sidTo = donor, sidFrom = NA) is
        // sent to the donor ALONE; our wallet recovers the excess.
        var world = WorldWithMyShip(MyId);
        world.Apply(new SetMoney { Money = 1000, ShipId = MyId });

        world.Apply(new MoneyChange { DMoney = 120, SidTo = MyId, SidFrom = -1 });
        Assert.Equal(1120, world.MyMoney);
    }

    [Fact]
    public void MoneyChange_PlayerToPlayerDonation_TransfersBothWallets()
    {
        // sidFrom = A (us), sidTo = B: our wallet drops by dMoney, B rises.
        var world = WorldWithMyShip(MyId);
        world.Apply(new SetMoney { Money = 1000, ShipId = MyId });
        world.Apply(new SetMoney { Money = 0400, ShipId = Teammate });

        world.Apply(new MoneyChange { DMoney = 200, SidTo = Teammate, SidFrom = MyId });
        Assert.Equal(800, world.MyMoney);
        Assert.Equal(600, world.WalletByShipId[Teammate]);
    }

    [Fact]
    public void MoneyChange_DoesNotDoubleCountWhenDonorIsUs_AndRecipientDiffers()
    {
        // Regression guard for the interplay the brief flags: a player-initiated
        // spend where sidFrom == sidTo == us (bucket) must net exactly -dMoney;
        // and a cross-ship one where sidFrom == us nets -dMoney on us AND the
        // recipient +dMoney, never twice on either.
        var world = WorldWithMyShip(MyId);
        world.Apply(new SetMoney { Money = 1000, ShipId = MyId });
        world.Apply(new SetMoney { Money = 0000, ShipId = Teammate });

        world.Apply(new MoneyChange { DMoney = -400, SidTo = MyId, SidFrom = MyId }); // bucket spend
        Assert.Equal(600, world.MyMoney);

        world.Apply(new MoneyChange { DMoney = 150, SidTo = Teammate, SidFrom = MyId }); // donate to mate
        Assert.Equal(450, world.MyMoney);
        Assert.Equal(150, world.WalletByShipId[Teammate]);
    }

    // ── AUTODONATE relationship + one-time gift ─────────────────────────

    [Fact]
    public void AutoDonate_OwnGift_CreditsTarget_AndDoesNotDebitSelfTwice()
    {
        var world = WorldWithMyShip(MyId);
        world.Apply(new SetMoney { Money = 1000, ShipId = MyId });
        world.Apply(new SetMoney { Money = 0500, ShipId = Teammate });

        // The local donor zeroed its own wallet at send time (ZeroOwnWallet /
        // teampane.cpp:1241), so the relay must NOT debit it again
        // (native pshipBy != m_ship, appmsg.cpp:1248-1263).
        world.ZeroOwnWallet();
        world.Apply(new AutoDonateUpdate { SidDonateBy = MyId, SidDonateTo = Teammate, Amount = 300 });
        Assert.Equal(Teammate, world.AutoDonateTargetByShipId[MyId]);
        Assert.Equal(0, world.MyMoney);                   // stays zeroed — no double debit
        Assert.Equal(800, world.WalletByShipId[Teammate]); // target credited
    }

    [Fact]
    public void AutoDonate_AnotherDonor_IsDebitedOnTheRelay()
    {
        var world = WorldWithMyShip(MyId);
        world.Apply(new SetMoney { Money = 1000, ShipId = MyId });
        world.Apply(new SetMoney { Money = 0500, ShipId = Teammate });

        // A DIFFERENT donor (not us) has no local zeroing, so the relay debits
        // it (native pshipBy != m_ship).
        world.Apply(new AutoDonateUpdate { SidDonateBy = Teammate, SidDonateTo = MyId, Amount = 300 });
        Assert.Equal(1300, world.MyMoney);                  // target (us) credited
        Assert.Equal(200, world.WalletByShipId[Teammate]);  // other donor debited
    }

    [Fact]
    public void AutoDonate_RefusedRequest_RefundsTheLocalShip()
    {
        var world = WorldWithMyShip(MyId);
        world.Apply(new SetMoney { Money = 0, ShipId = MyId });

        // appmsg.cpp:1265-1270: our request was denied — refund the amount.
        world.Apply(new AutoDonateUpdate { SidDonateBy = MyId, SidDonateTo = -1 /* NA */, Amount = 250 });
        Assert.Equal(250, world.MyMoney);
    }

    [Fact]
    public void ZeroOwnWallet_SetsTheLocalWalletToZero()
    {
        var world = WorldWithMyShip(MyId);
        world.Apply(new SetMoney { Money = 250, ShipId = MyId });
        world.ZeroOwnWallet();
        Assert.Equal(0, world.MyMoney);
    }

    [Fact]
    public void AutoDonate_NoTarget_ClearsRelationship()
    {
        var world = WorldWithMyShip(MyId);
        world.Apply(new AutoDonateUpdate { SidDonateBy = MyId, SidDonateTo = Teammate, Amount = 0 });
        Assert.Equal(Teammate, world.AutoDonateTargetByShipId[MyId]);

        world.Apply(new AutoDonateUpdate { SidDonateBy = MyId, SidDonateTo = -1 /* NA */, Amount = 0 });
        Assert.False(world.AutoDonateTargetByShipId.ContainsKey(MyId));
    }

    // ── PAYDAY routing via auto-donate ──────────────────────────────────

    private static ClientWorld PaydayWorld(short roleShip)
    {
        // One side with L (leader, no target), D1/D2 (donate to L) and N
        // (no target). Every role client seeds the SAME four players + their
        // relationships (what SyncMoneyStateToJoiner sends each peer).
        var world = WorldWithMyShip(roleShip);
        short L = 1, D1 = 2, D2 = 3, N = 4;
        foreach (var s in new[] { L, D1, D2, N })
            world.Apply(new SetMoney { Money = 100, ShipId = s });
        world.Apply(new AutoDonateUpdate { SidDonateBy = D1, SidDonateTo = L, Amount = 0 });
        world.Apply(new AutoDonateUpdate { SidDonateBy = D2, SidDonateTo = L, Amount = 0 });
        world.Apply(new AutoDonateUpdate { SidDonateBy = L, SidDonateTo = -1, Amount = 0 });
        world.Apply(new AutoDonateUpdate { SidDonateBy = N, SidDonateTo = -1, Amount = 0 });
        return world;
    }

    [Fact]
    public void Payday_LeaderGrowsByDMoneyTimesDonorsPlusSelf_DonorsFlat_NonDonorGrowsOnce()
    {
        var leader = PaydayWorld(1);
        const int d = 50;
        leader.Apply(new Payday { DMoney = d });

        // Leader receives its own share + each donor's share = d * (donors + 1).
        Assert.Equal(100 + d * 3, leader.MyMoney);
        // Donors stay flat (their share went to the leader).
        Assert.Equal(100, leader.WalletByShipId[2]);
        Assert.Equal(100, leader.WalletByShipId[3]);
        // Non-donor receives its own share exactly once.
        Assert.Equal(100 + d, leader.WalletByShipId[4]);
    }

    [Fact]
    public void Payday_DonorClient_KeepsItsOwnFlat()
    {
        var donor = PaydayWorld(2);
        donor.Apply(new Payday { DMoney = 50 });
        Assert.Equal(100, donor.MyMoney);            // D1's share went to the leader
        Assert.Equal(250, donor.WalletByShipId[1]);  // ...and the leader's total converges in D1's view too
    }

    [Fact]
    public void Payday_NonDonorClient_GrowsByExactlyItsShare()
    {
        var nonDonor = PaydayWorld(4);
        nonDonor.Apply(new Payday { DMoney = 50 });
        Assert.Equal(150, nonDonor.MyMoney);         // N keeps its own share
    }

    [Fact]
    public void Reset_ClearsMoneyAndRelationships()
    {
        var world = WorldWithMyShip(MyId);
        world.Apply(new SetMoney { Money = 1000, ShipId = MyId });
        world.Apply(new AutoDonateUpdate { SidDonateBy = MyId, SidDonateTo = Teammate, Amount = 0 });
        Assert.NotNull(world.MyMoney);

        world.Reset();
        Assert.Null(world.MyMoney);
        Assert.Empty(world.WalletByShipId);
        Assert.Empty(world.AutoDonateTargetByShipId);
    }
}
