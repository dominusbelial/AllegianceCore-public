using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.MonoGame.Rendering;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P1012 — LIVE proof that the money the shop shows for a hull change is the
/// money the server takes. A docked player switches hull (the pending loadout is
/// rebuilt the native way), mounts a priced launcher, and buys over a real
/// in-process server: the wallet moves by exactly <see cref="ShopModel.Cost"/>,
/// and the buy back to a free hull refunds exactly what the shop showed
/// (native <c>FedSrv.CPP:6183-6199</c>: refund the old ship's value, charge the
/// new ship's value).
/// </summary>
[Collection("LiveNetwork")]
public sealed class P1012HullSwitchMoneyLiveTests
{
    private const short LxyScout = 92;      // $500, dispenser capacity 6, no successor
    private const short HvyScout = 85;      // $0, dispenser capacity 4, no successor
    private const short Sanctuary = 2;      // dispenser, $100 each, expendable size 2, no successor

    private static readonly string ArtRoot = ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,
        Games =
        [
            new Gm { Name = "P1012 Hull Switch Money", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 993u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 98900000000001012ul, DrmHash = "p1012",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    private static object Wallet(Srv server, short shipId)
    {
        var field = typeof(Srv).GetField("_peerShips", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var dict = (System.Collections.IDictionary)field.GetValue(server)!;
        foreach (System.Collections.DictionaryEntry e in dict)
        {
            var live = e.Value!;
            var ship = (Ship)live.GetType().GetProperty("Ship")!.GetValue(live)!;
            if (ship.Id == shipId) return live.GetType().GetProperty("Wallet")!.GetValue(live)!;
        }
        throw new InvalidOperationException($"peer ship {shipId} not found");
    }

    private static int Money(Srv server, short shipId)
    {
        var w = Wallet(server, shipId);
        return (int)w.GetType().GetProperty("Money")!.GetValue(w)!;
    }

    private static void Fund(Srv server, short shipId, int amount)
    {
        var w = Wallet(server, shipId);
        w.GetType().GetProperty("Money")!.SetValue(w, amount);
    }

    private static (Ship Ship, Sector Sector) SpawnDocked(StaticCore core, Mission mission, short shipId)
    {
        var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var st = mission.Sectors.SelectMany(s => s.Stations).First(s => s.SideId >= 0);
        var sector = mission.Sectors.First(s => s.Stations.Contains(st));
        var ship = new Ship(shipId, hull, DockTestPlacement.Inside(mission, st, hull), Vector3.Zero, Orientation.Identity)
        {
            SideId = st.SideId,
        };
        mission.PlaceShip(ship, sector);
        return (ship, sector);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Hull_switch_buy_moves_the_wallet_by_exactly_the_shown_cost_Live()
    {
        Require.Artwork();
        var all = new TechTreeBitMask(Enumerable.Repeat((byte)0xFF, TechTreeBitMask.ByteCount).ToArray());
        var baseCatalog = ShopCatalog.TryLoad(ArtRoot)
            ?? throw new InvalidOperationException($"PCore001.igc not loadable from {ArtRoot}");
        // The offer a fully developed side sees (the same filter Game1 applies on dock).
        var catalog = baseCatalog.FilteredFor(Array.Empty<byte>(), all);
        Assert.Contains(catalog.Hulls, h => h.HullId == LxyScout && h.Price == 500);
        Assert.Contains(catalog.Hulls, h => h.HullId == HvyScout && h.Price == 0);
        Assert.Contains(catalog.Launchers!, l => l.Part.PartId == Sanctuary && l.Part.Price == 100);

        using var server = new Srv();
        ShipSpawnerField.SetValue(server, (StaticCore core, Mission mission, short id) => SpawnDocked(core, mission, id));
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "p1012_money");
        session.SendLogonRequest(Request("p1012_money"));

        int join = PumpUntil(server, () => session.World.MyShipId is not null, 4000);
        Assert.True(join < 4000, "timed out waiting for ship assignment");
        short myId = session.World.MyShipId!.Value;
        foreach (var s in server.Missions[0].Sides) s.DevelopmentTechs = all;
        int docked = PumpUntil(server, () => session.World.Ships[myId].StationId != -1, 8000);
        Assert.True(docked < 8000, "ship never docked");

        const int Start = 10_000;
        Fund(server, myId, Start);
        var store = new ShopCustomLoadouts();
        ShopModel OpenShop()
        {
            var me = session.World.Ships[myId];
            return new ShopModel(catalog, me.HullId, me.Loadout ?? Array.Empty<ExpandedPartData>(),
                Money(server, myId), store, me.Cargo);
        }

        // ── leg 1: spawn hull → Lxy Scout ($500) + 3 Sanctuary ($300) ───────────
        var shop = OpenShop();
        Assert.Equal(0, shop.CurrentShipValue);          // fixture: the spawn ship is worth nothing
        shop.CycleHullTo(LxyScout);
        ShopModelTests.MountViaSlots(shop, Sanctuary);
        Assert.Equal(3, shop.ChosenParts.Single(p => p.PartId == Sanctuary).Amount);
        Assert.Equal(800, shop.Cost);
        Assert.Empty(shop.ChosenPartsThatDontFitEffectiveHull);

        var sent = shop.ChosenParts.ToArray();
        shop.SaveCustomLoadout();                        // what Buy does (load.cpp:1524-1527)
        session.SendBuyLoadout(shop.EffectiveHullId, fLaunch: false, parts: sent);
        int bought = PumpUntil(server, () => session.World.Ships[myId].HullId == LxyScout
            && (session.World.Ships[myId].Loadout?.Any(p => p.PartId == Sanctuary) ?? false), 8000);
        Assert.True(bought < 8000, "the Lxy Scout buy was never acknowledged");

        Assert.Equal(Start - 800, Money(server, myId));  // charged exactly the shown cost
        var granted = session.World.Ships[myId].Loadout!;
        Assert.Equal(sent.Length, granted.Count);        // nothing dropped
        Assert.Equal(3, granted.Single(p => p.PartId == Sanctuary).Amount);

        // ── leg 2: Lxy Scout → free Hvy Scout default: refund of everything ─────
        shop = OpenShop();
        Assert.Equal(800, shop.CurrentShipValue);
        shop.CycleHullTo(HvyScout);
        Assert.Equal(-800, shop.Cost);
        session.SendBuyLoadout(shop.EffectiveHullId, fLaunch: false, parts: shop.ChosenParts);
        int swapped = PumpUntil(server, () => session.World.Ships[myId].HullId == HvyScout, 8000);
        Assert.True(swapped < 8000, "the Hvy Scout buy was never acknowledged");
        Assert.Equal(Start, Money(server, myId));        // refunded exactly the shown refund

        // ── leg 3: back to the Lxy Scout: the loadout bought in leg 1 returns ───
        shop = OpenShop();
        shop.CycleHullTo(LxyScout);
        Assert.Equal(3, shop.ChosenParts.Single(p => p.PartId == Sanctuary).Amount);
        Assert.Equal(800, shop.Cost);
        session.SendBuyLoadout(shop.EffectiveHullId, fLaunch: false, parts: shop.ChosenParts);
        int back = PumpUntil(server, () => session.World.Ships[myId].HullId == LxyScout, 8000);
        Assert.True(back < 8000, "the return buy was never acknowledged");
        Assert.Equal(Start - 800, Money(server, myId));
    }
}
