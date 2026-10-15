using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P441 — server-side BUY_LOADOUT pricing + funds + tech gating. Five LIVE
/// loopback proofs over a real in-process server + real TCP bots, mirroring
/// native FedSrv.CPP:6182-6260 (price the difference in ship value, gate on
/// the buyer's personal wallet, refuse on a station that can't sell the hull,
/// skip tech-gated parts) and native CshipIGC::PurchaseShipLoadout
/// (shipIGC.h:989-1040):
///   (1) a funded buyer buys a paid hull (+ real paid launcher) and is debited
///       exactly ShipValue(new) - ShipValue(old); a same-side teammate receives
///       the side-wide MONEY_CHANGE {dMoney = -cost, sidTo = buyer, sidFrom=NA};
///   (2) an unfunded buyer is refused: ship unchanged, no MONEY_CHANGE;
///   (3a) a hull the docked station can't sell is refused and the ship stays
///       unchanged (even though funded);
///   (3b) a tech-gated part is skipped (FBoughtEverything false) while a
///       sellable part is still mounted;
///   (4) a downgrade refunds the buyer (dMoney > 0).
/// Pricing fixtures buy DOCKED (H-06, FedSrv.CPP:6153-6172), with selected
/// shop records explicitly sellable. Launcher pricing is per clamped round
/// (H-08, magazineigc.h:85-88); downgrade credit is explicitly paid (H-03).
///
/// IMPORTANT harness detail: the two bots seat in NON-deterministic order
/// (async logon), so the spawner's call order does NOT map to buyer-vs-mate.
/// Every test identifies its ships by <c>Bot.ShipAssignment</c> AFTER seating,
/// never by the spawner invocation index.
/// </summary>
[Collection("LiveNetwork")]
public sealed class BuyLoadoutPricingLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int ups = 20) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = ups,
        Games =
        [
            new Gm { Name = "P441 Buy Pricing", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 961u, DwCookie = 0x961u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9199000000000000000ul, DrmHash = "buy441",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>P441 — push every mission's next-payday time to the far future so
    /// the P197 payday credit can't perturb exact wallet assertions during the
    /// (short) test window. Mission.NextPaydayTime is internal → reflection.</summary>
    private static void StopPayday(Srv server)
    {
        var prop = typeof(Mission).GetProperty("NextPaydayTime", BindingFlags.Instance | BindingFlags.NonPublic)
                   ?? throw new InvalidOperationException("Mission.NextPaydayTime property not found");
        foreach (var m in server.Missions)
            prop.SetValue(m, double.MaxValue);
    }

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    /// <summary>An in-process server + the two bots, with their ship ids
    /// resolved from <see cref="Bot.ShipAssignment"/> AFTER seating (the bots
    /// seat in non-deterministic order, so the spawner index is meaningless).</summary>
    private sealed class Harness : IDisposable
    {
        public Srv Server = null!;
        public FltBot Buyer = null!;
        public FltBot Mate = null!;
        public short BuyerId;
        public short MateId;
        public void Dispose() { Server.Dispose(); Buyer.Dispose(); Mate.Dispose(); }
    }

    private static Ship LiveShip(Srv server, short shipId) =>
        server.Ships.First(s => s.Ship.Id == shipId).Ship;

    private static void Fund(Srv server, short shipId, int amount) =>
        server.Ships.First(s => s.Ship.Id == shipId).Wallet.Money = amount;

    private static int WalletOf(Srv server, short shipId) =>
        server.Ships.First(s => s.Ship.Id == shipId).Wallet.Money;

    private static int ValueOf(Srv server, short shipId)
    {
        var ship = LiveShip(server, shipId);
        return ship.RefundableHullValue + ship.Parts.Sum(p => p.FreeSpawn ? 0 : p.Type.Price * (p.EquipmentType is EquipmentType.Magazine or EquipmentType.Dispenser or EquipmentType.ChaffLauncher ? p.Amount : 1));
    }

    private static void ConnectAndIdentify(Harness h)
    {
        h.Buyer = new FltBot(); h.Mate = new FltBot();
        h.Buyer.Connect(h.Server.BoundPort, "buyer"); h.Buyer.SendLogonRequest(Request("buyer"));
        h.Mate.Connect(h.Server.BoundPort, "mate"); h.Mate.SendLogonRequest(Request("mate"));
        int seat = PumpUntil(h.Server, () => h.Buyer.ShipAssignment is not null && h.Mate.ShipAssignment is not null, 6000);
        if (seat >= 6000) throw new InvalidOperationException("buyer/mate did not seat");
        h.BuyerId = h.Buyer.ShipAssignment!.Ship.ShipId;
        h.MateId = h.Mate.ShipAssignment!.Ship.ShipId;
    }

    /// <summary>Resolve the first friendly garrison's station type + side — the
    /// P441 sellable-set context — usable right after Boot.</summary>
    private static (DataStationTypeIGC st, Side side) Garrison(Mission mission)
    {
        var garrison = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
        if (!mission.Core.StationTypes.TryGetValue(garrison.StationTypeId, out var st))
            throw new InvalidOperationException("garrison station type unresolvable");
        var side = mission.Sides.First(s => s.Id == garrison.SideId);
        return (st, side);
    }

    private static bool StationSells(DataStationTypeIGC st, Side side, DataHullTypeIGC hull) =>
        TechTreeBitMask.RequiredTechsBuyable(hull.TtbmRequired, st.TtbmLocal, side.DevelopmentTechs);

    private static bool StationSells(DataStationTypeIGC st, Side side, DataPartTypeIGC part) =>
        TechTreeBitMask.RequiredTechsBuyable(part.TtbmRequired, st.TtbmLocal, side.DevelopmentTechs);

    /// <summary>A sellable hull of the docked garrison (used as the docked
    /// spawn hull for the tech-gate scenarios).</summary>
    private static Func<Mission, short> CheapestSellable => m =>
    {
        var (st, side) = Garrison(m);
        return m.Core.HullTypes.Values.Where(h => StationSells(st, side, h)).OrderBy(h => h.Price).First().HullId;
    };

    /// <summary>Spawn on a FREE hull so the "before" ship value is 0.</summary>
    private static Func<Mission, short> FreeHull =>
        m => m.Core.HullTypes.Values.OrderBy(h => h.HullId).First(h => h.Price == 0).HullId;

    /// <summary>Spawn on the PRICIEST hull so a strictly cheaper hull always
    /// exists for the downgrade test.</summary>
    private static Func<Mission, short> PriciestHull =>
        m => m.Core.HullTypes.Values.Where(h => h.GroupId >= 0).OrderByDescending(h => h.Price).First().HullId;

    /// <summary>Both bots on the friendly side, BOTH DOCKED inside the garrison's
    /// DockRadius (the P441 tech gate runs on a docked buyer). Whichever bot is
    /// the buyer (via ShipAssignment) is docked, so the gate applies.</summary>
    private static Harness BootDocked(Func<Mission, short> chooseHull)
    {
        var h = new Harness { Server = new Srv() };
        h.Server.Boot(Config());
        foreach (var m in h.Server.Missions) m.Stage = MissionStage.Started;
        StopPayday(h.Server);
        short hullId = chooseHull(h.Server.Missions[0]);
        int n = 0;
        h.Server.ShipSpawner = (core, mission, id) =>
        {
            var station = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
            var sector = mission.Sectors.First(s => s.Stations.Contains(station));
            var ship = new Ship(id, core.HullTypes[hullId],
                StationPos(station) + new Vector3(25f + 20f * n++, 0f, 0f), Vector3.Zero, Orientation.Identity)
            { SideId = station.SideId, StationId = station.StationId };
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };
        ConnectAndIdentify(h);
        int dock = PumpUntil(h.Server,
            () => LiveShip(h.Server, h.BuyerId).StationId is not null
               && LiveShip(h.Server, h.MateId).StationId is not null, 8000);
        if (dock >= 8000) throw new InvalidOperationException("buyer/mate never latched docked");
        return h;
    }

    /// <summary>A paid hull, optionally paired with a real paid launcher it
    /// can mount (proving the launcher's real price flows through ShipValue).</summary>
    private static (DataHullTypeIGC hull, DataPartTypeIGC? launcher) PaidHullPair(StaticCore core)
    {
        foreach (var launcherId in core.Launchers.Keys)
        {
            if (!core.TryBuildExpendableLauncher(launcherId, out var lp) || lp.Price <= 0) continue;
            var hull = core.HullTypes.Values
                .Where(h => h.GroupId >= 0 && h.Price > 0 && h.CanMountPart(lp, 0))
                .OrderBy(h => h.Price).FirstOrDefault();
            if (hull.HullId != 0) return (hull, lp);
        }
        var anyPaid = core.HullTypes.Values.Where(h => h.GroupId >= 0 && h.Price > 0).OrderBy(h => h.Price).First();
        return (anyPaid, null);
    }

    private static DataPartTypeIGC FindMountablePart(StaticCore core, DataStationTypeIGC st, Side side, DataHullTypeIGC hull, bool wantSellable)
    {
        // Ordered by PartId for determinism (Dictionary enumeration order varies
        // per process).
        foreach (var pt in core.PartTypes.Values.OrderBy(p => p.PartId))
            if (hull.CanMountPart(pt) && StationSells(st, side, pt) == wantSellable)
                return pt;
        throw new InvalidOperationException(
            $"PCore001 has no {(wantSellable ? "" : "non-")}sellable part mountable on hull {hull.HullId}");
    }

    /// <summary>A sellable WEAPON the hull can mount — a forward gun mounts at
    /// slot 0 on a fighter, so the TechGated scenario reliably grants it.</summary>
    private static DataPartTypeIGC FindSellableMountableWeapon(StaticCore core, DataStationTypeIGC st, Side side, DataHullTypeIGC hull)
    {
        foreach (var pt in core.PartTypes.Values
                     .Where(p => p.EquipmentType == EquipmentType.Weapon).OrderBy(p => p.PartId))
            if (hull.CanMountPart(pt) && StationSells(st, side, pt))
                return pt;
        throw new InvalidOperationException(
            $"PCore001 has no sellable weapon mountable on hull {hull.HullId}");
    }

    // ── (1) funded paid purchase: exact debit + side-wide MONEY_CHANGE ───

    /// <summary>⭐ Scenario 1 — a funded buyer buys a paid hull plus a real paid
    /// launcher (when one exists). The wallet falls by exactly ShipValue(new) -
    /// ShipValue(old); the real launcher price is part of that delta (proving
    /// the launcher's true price, not 0, flows through ShipValue); and a
    /// same-side teammate receives the side-wide MONEY_CHANGE
    /// {dMoney = -cost, sidTo = buyer, sidFrom = NA}.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Funded_Paid_Purchase_Charges_Exact_Delta_And_Broadcasts_SideWide()
    {
        Require.Artwork();
        using var h = BootDocked(FreeHull);
        short buyerId = h.BuyerId;
        Assert.NotNull(LiveShip(h.Server, buyerId).StationId);
        var core = h.Server.Missions[0].Core;

        int oldVal = ValueOf(h.Server, buyerId);   // = free hull's price (0)
        var (hull, launcher) = PaidHullPair(core);
        hull = EconomyLoadoutTestSetup.Sell(h.Server.Missions[0], hull);
        if (launcher is { } lp) launcher = EconomyLoadoutTestSetup.Sell(h.Server.Missions[0], lp);
        int rounds = launcher is { } ammo ? Ship.ClampPartAmount(hull, core, ammo, 5) : 0;
        int launcherPrice = (launcher?.Price ?? 0) * rounds;
        int expectedCost = (hull.Price + launcherPrice) - oldVal;
        Assert.True(expectedCost > 0, $"expected a positive cost, got {expectedCost}");

        const int funded = 1_000_000;
        Fund(h.Server, buyerId, funded);
        h.Buyer.ResetMoneyChanges(); h.Mate.ResetMoneyChanges();
        int preWallet = WalletOf(h.Server, buyerId);   // capture just before the send (payday-safe)

        var parts = launcher is null
            ? Array.Empty<ExpandedPartData>()
            : new[] { new ExpandedPartData(launcher.Value.PartId, 5, 0, ExpandedPartData.FullyMounted) };
        h.Buyer.SendBuyLoadout(hull.HullId, fLaunch: false, parts: parts);

        int seen = PumpUntil(h.Server,
            () => h.Buyer.MoneyChanges.Any(mc => mc.DMoney == -expectedCost && mc.SidTo == buyerId && mc.SidFrom == -1)
               && h.Mate.MoneyChanges.Any(mc => mc.DMoney == -expectedCost && mc.SidTo == buyerId && mc.SidFrom == -1), 4000);
        Assert.True(seen < 4000, $"buyer/mate never saw the {expectedCost} debit MONEY_CHANGE (money={WalletOf(h.Server, buyerId)})");

        Assert.Equal(preWallet - expectedCost, WalletOf(h.Server, buyerId));
        Assert.Equal(oldVal + expectedCost, ValueOf(h.Server, buyerId));   // ship really carries the new value
        Assert.Equal(hull.HullId, LiveShip(h.Server, buyerId).HullType.HullId);
        Assert.Contains(h.Mate.MoneyChanges, mc => mc.DMoney == -expectedCost && mc.SidTo == buyerId && mc.SidFrom == -1);

        Console.WriteLine($"BUY-PRICE-LIVE: funded buyer {buyerId} -> hull {hull.HullId}({hull.Price}){(launcher is null ? "" : $" + launcher {launcher.Value.PartId}({launcherPrice})")} cost {expectedCost}; mate saw MONEY_CHANGE(-{expectedCost}) side-wide");
    }

    // ── (2) unfunded buyer: refused, unchanged, no MONEY_CHANGE ──────────

    /// <summary>⭐ Scenario 2 — a buyer with an empty wallet tries a paid hull.
    /// The purchase is refused: the ship stays exactly as it was (same hull +
    /// value), and NO MONEY_CHANGE reaches anyone (the port's log-and-refuse
    /// soft-fail for native's TrapHackBoot disconnect).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Unfunded_Buyer_Refused_ShipUnchanged_NoMoneyChange()
    {
        Require.Artwork();
        using var h = BootDocked(FreeHull);
        short buyerId = h.BuyerId;
        var core = h.Server.Missions[0].Core;

        short beforeHull = LiveShip(h.Server, buyerId).HullType.HullId;
        int beforeVal = ValueOf(h.Server, buyerId);
        var paidHull = EconomyLoadoutTestSetup.Sell(h.Server.Missions[0], core.HullTypes.Values.Where(hh => hh.GroupId >= 0 && hh.Price > 0).OrderBy(hh => hh.Price).First());   // cost > 0
        Assert.True(paidHull.Price > beforeVal, "need a paid hull strictly above the free spawn hull");

        Assert.Equal(0, WalletOf(h.Server, buyerId));
        h.Buyer.ResetMoneyChanges(); h.Mate.ResetMoneyChanges();

        h.Buyer.SendBuyLoadout(paidHull.HullId, fLaunch: false);
        // Give the server ample ticks to drain/process the refused request; the
        // refusal leaves the ship unchanged, so wait a generous fixed window and
        // assert unchanged + no MONEY_CHANGE.
        for (int i = 0; i < 1500; i++) h.Server.TickSim();

        Assert.Equal(beforeHull, LiveShip(h.Server, buyerId).HullType.HullId);   // ship unchanged
        Assert.Equal(beforeVal, ValueOf(h.Server, buyerId));
        Assert.Equal(0, WalletOf(h.Server, buyerId));                            // no debit
        Assert.Empty(h.Buyer.MoneyChanges);
        Assert.Empty(h.Mate.MoneyChanges);
        Console.WriteLine($"BUY-REFUSED-LIVE: unfunded buyer {buyerId} tried hull {paidHull.HullId}({paidHull.Price}) with 0 credits -> refused, hull {beforeHull} unchanged, no MONEY_CHANGE");
    }

    // ── (3a) unsellable hull: refused (docked) ───────────────────────────

    /// <summary>⭐ Scenario 3a — a DOCKED buyer requests a hull the docked
    /// garrison cannot sell (required techs unmet). The whole purchase is
    /// refused (native's CanBuy abort, shipIGC.h:999-1000) and the ship stays
    /// unchanged, even though the buyer has plenty of money.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Unsellable_Hull_Refused_ShipUnchanged()
    {
        Require.Artwork();
        using var h = BootDocked(CheapestSellable);
        short buyerId = h.BuyerId;
        var ctx = DockCtx(h.Server, buyerId);
        Assert.NotNull(ctx.st); Assert.NotNull(ctx.side);
        var st = ctx.st!.Value; var side = ctx.side!;
        var core = h.Server.Missions[0].Core;

        short beforeHull = LiveShip(h.Server, buyerId).HullType.HullId;
        int beforeVal = ValueOf(h.Server, buyerId);
        var unsellable = core.HullTypes.Values
            .Where(hh => !StationSells(st, side, hh)).OrderBy(hh => hh.HullId).First();

        Fund(h.Server, buyerId, 1_000_000);   // plenty of money — the ONLY gate is tech
        h.Buyer.ResetMoneyChanges();

        h.Buyer.SendBuyLoadout(unsellable.HullId, fLaunch: false);
        for (int i = 0; i < 1500; i++) h.Server.TickSim();

        Assert.Equal(beforeHull, LiveShip(h.Server, buyerId).HullType.HullId);   // refused -> unchanged
        Assert.Equal(beforeVal, ValueOf(h.Server, buyerId));
        Assert.Empty(h.Buyer.MoneyChanges);
        Console.WriteLine($"BUY-TECH-HULL-LIVE: docked buyer {buyerId} tried unsellable hull {unsellable.HullId} -> refused, ship unchanged");
    }

    // ── (3b) tech-gated part: skipped, sellable part still mounted ───────

    /// <summary>⭐ Scenario 3b — a DOCKED buyer requests two parts on the same
    /// hull: one the garrison sells and mounts, one the garrison cannot sell
    /// (tech-gated). The sellable part mounts; the gated part is skipped
    /// (FBoughtEverything false in the ack).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void TechGated_Part_Skipped_Sellable_Part_Still_Mounted()
    {
        Require.Artwork();
        using var h = BootDocked(CheapestSellable);
        short buyerId = h.BuyerId;
        var ctx = DockCtx(h.Server, buyerId);
        Assert.NotNull(ctx.st); Assert.NotNull(ctx.side);
        var st = ctx.st!.Value; var side = ctx.side!;
        var core = h.Server.Missions[0].Core;

        var hull = LiveShip(h.Server, buyerId).HullType;   // keep the same (docked, sellable) hull
        // The sellable part is a WEAPON (mounts reliably at slot 0); the gated
        // part is any mountable-but-unsellable part (it is skipped by the tech
        // gate regardless of its requested mount).
        var sellable = FindSellableMountableWeapon(core, st, side, hull);
        var gated = FindMountablePart(core, st, side, hull, wantSellable: false);
        Assert.NotEqual(sellable.PartId, gated.PartId);

        int oldVal = ValueOf(h.Server, buyerId);
        Fund(h.Server, buyerId, 1_000_000);
        h.Buyer.ResetMoneyChanges(); h.Mate.ResetMoneyChanges();

        var requested = new[]
        {
            new ExpandedPartData(sellable.PartId, 20, 0, ExpandedPartData.FullyMounted),
            new ExpandedPartData(gated.PartId, 20, 1, ExpandedPartData.FullyMounted),
        };
        h.Buyer.SendBuyLoadout(hull.HullId, fLaunch: false, parts: requested);
        int applied = PumpUntil(h.Server,
            () => LiveShip(h.Server, buyerId).Parts.Any(p => p.Type.PartId == sellable.PartId), 6000);
        Assert.True(applied < 6000, "sellable part never mounted server-side (purchase not processed)");

        // The sellable part mounted; the tech-gated part did NOT.
        Assert.Contains(LiveShip(h.Server, buyerId).Parts, p => p.Type.PartId == sellable.PartId);
        Assert.DoesNotContain(LiveShip(h.Server, buyerId).Parts, p => p.Type.PartId == gated.PartId);
        // Hull unchanged; both garrison parts are price-0, so no money moved.
        Assert.Equal(hull.HullId, LiveShip(h.Server, buyerId).HullType.HullId);
        Assert.Equal(1_000_000, WalletOf(h.Server, buyerId));
        Assert.Empty(h.Buyer.MoneyChanges);
        Console.WriteLine($"BUY-TECH-PART-LIVE: docked buyer {buyerId} requested sellable part {sellable.PartId} + gated part {gated.PartId} -> sellable mounted, gated skipped (FBoughtEverything=false)");
    }

    // ── (4) downgrade refund ─────────────────────────────────────────────

    /// <summary>⭐ Scenario 4 — a buyer downgrades to a CHEAPER hull. cost is
    /// negative, so the wallet INCREASES (refund) and the MONEY_CHANGE carries
    /// dMoney > 0, exactly as native (FedSrv.CPP:6195-6207).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Downgrade_Refunds_Buyer()
    {
        Require.Artwork();
        using var h = BootDocked(PriciestHull);
        short buyerId = h.BuyerId;
        var core = h.Server.Missions[0].Core;

        LiveShip(h.Server, buyerId).RefundableHullValue = LiveShip(h.Server, buyerId).HullType.Price;
        int oldVal = ValueOf(h.Server, buyerId);   // explicitly paid trade-in
        var cheapest = EconomyLoadoutTestSetup.Sell(h.Server.Missions[0], core.HullTypes.Values.Where(hh => hh.GroupId >= 0).OrderBy(hh => hh.Price).First());
        Assert.True(cheapest.Price < oldVal, "need a hull strictly cheaper than the priciest");

        int expectedRefund = oldVal - cheapest.Price;   // = -cost
        const int funded = 500_000;
        Fund(h.Server, buyerId, funded);
        h.Buyer.ResetMoneyChanges(); h.Mate.ResetMoneyChanges();
        int preWallet = WalletOf(h.Server, buyerId);   // capture just before the send (payday-safe)

        h.Buyer.SendBuyLoadout(cheapest.HullId, fLaunch: false);

        int seen = PumpUntil(h.Server,
            () => h.Buyer.MoneyChanges.Any(mc => mc.DMoney == expectedRefund && mc.SidTo == buyerId && mc.SidFrom == -1)
               && h.Mate.MoneyChanges.Any(mc => mc.DMoney == expectedRefund && mc.SidTo == buyerId && mc.SidFrom == -1), 4000);
        Assert.True(seen < 4000, $"buyer/mate never saw the +{expectedRefund} refund MONEY_CHANGE");

        Assert.Equal(preWallet + expectedRefund, WalletOf(h.Server, buyerId));
        Assert.Equal(cheapest.HullId, LiveShip(h.Server, buyerId).HullType.HullId);
        Assert.Contains(h.Mate.MoneyChanges, mc => mc.DMoney == expectedRefund && mc.SidTo == buyerId && mc.SidFrom == -1);
        Console.WriteLine($"BUY-DOWNGRADE-LIVE: buyer {buyerId} -> cheaper hull {cheapest.HullId}({cheapest.Price}) refunded {expectedRefund} (dMoney>0 side-wide)");
    }

    private static (DataStationTypeIGC? st, Side? side) DockCtx(Srv server, short shipId)
    {
        var ship = LiveShip(server, shipId);
        var mission = server.Missions[0];
        if (ship.StationId is not { } dockId || ship.SideId is not { } sd) return (null, null);
        var side = mission.Sides.FirstOrDefault(s => s.Id == sd);
        foreach (var sec in mission.Sectors)
            foreach (var st in sec.Stations)
                if (st.StationId == dockId && mission.Core.StationTypes.TryGetValue(st.StationTypeId, out var dt))
                    return (dt, side);
        return (null, side);
    }
}
