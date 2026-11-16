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
/// P1017 — LIVE loopback proof of the loadout Buy / Launch flow: a real
/// in-process server, real client sessions, the shipping <see cref="ShopModel"/>
/// and <see cref="LoadoutBuyFlow"/> driven exactly as <c>Game1</c> drives them
/// (build the shop from the world on dock, call <see cref="LoadoutBuyFlow.Sync"/>
/// every frame, send what Buy / Launch return).
/// </summary>
[Collection("LiveNetwork")]
public sealed class P1017BuyFlowLiveTests
{
    private const short LxyScout = 92;      // $500, dispenser capacity 6
    private const short HvyScout = 85;      // $0
    private const short Sanctuary = 2;      // dispenser, $100 each, expendable size 2
    private const short Carrier = 792;      // turret hull, turret mounts [2,6)

    private static readonly string ArtRoot = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
    private static readonly TechTreeBitMask AllTechs =
        new(Enumerable.Repeat((byte)0xFF, TechTreeBitMask.ByteCount).ToArray());

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,
        Games =
        [
            new Gm { Name = "P1017 Buy Flow", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name, int n) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 1017u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 98900000000001017ul + (ulong)n, DrmHash = "p1017",
    };

    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    /// <summary>One server, its sessions and the server-side ships behind them.</summary>
    private sealed class Rig : IDisposable
    {
        public Srv Server { get; } = new();
        public List<ConnectionSession> Sessions { get; } = [];
        public Dictionary<short, Ship> Ships { get; } = [];
        public ShopCatalog Catalog { get; }

        /// <param name="equip">Called for each spawned ship with its 0-based spawn order.</param>
        /// <param name="capitalDock">Dock at a capital-ship station (the P803
        /// fixture): the turret carrier does not fit a fighter bay.</param>
        public Rig(Func<int, short> hullFor, Action<int, Ship, StaticCore>? equip = null, bool capitalDock = false)
        {
            var catalog = ShopCatalog.TryLoad(ArtRoot)
                ?? throw new InvalidOperationException($"PCore001.igc not loadable from {ArtRoot}");
            Catalog = catalog.FilteredFor(Array.Empty<byte>(), AllTechs);   // what a fully developed side is offered
            int order = 0;
            ShipSpawnerField.SetValue(Server, (StaticCore core, Mission mission, short id) =>
            {
                int n = order++;
                var hull = core.HullTypes[hullFor(n)];
                var st = mission.Sectors.SelectMany(s => s.Stations).First(s => s.SideId >= 0);
                if (capitalDock) st = P550MonoTestDockHome.CapLandHome(mission, st.SideId);
                var sector = mission.Sectors.First(s => s.Stations.Contains(st));
                var at = capitalDock
                    ? new Vector3(st.Position[0] + 25f + 10f * n, st.Position[1], st.Position[2])
                    : DockTestPlacement.Inside(mission, st, hull);
                var ship = new Ship(id, hull, at, Vector3.Zero, Orientation.Identity)
                {
                    SideId = st.SideId,
                };
                mission.PlaceShip(ship, sector);
                equip?.Invoke(n, ship, core);
                Ships[id] = ship;
                return (ship, sector);
            });
            Server.Boot(Config());
            foreach (var m in Server.Missions) m.Stage = MissionStage.Started;
        }

        public int Pump(Func<bool> done, int max = 8000)
        {
            int n = 0;
            while (!done() && n < max) { Server.TickSim(); n++; Thread.Sleep(1); }
            return n;
        }

        public void Pump(int ticks) { for (int i = 0; i < ticks; i++) { Server.TickSim(); Thread.Sleep(1); } }

        /// <summary>Connect, get a ship, end up docked with the dock sync applied.</summary>
        public ConnectionSession Join(string name)
        {
            var session = new ConnectionSession();
            Sessions.Add(session);
            session.Connect(Server.BoundPort, name);
            session.SendLogonRequest(Request(name, Sessions.Count));
            Assert.True(Pump(() => session.World.MyShipId is not null, 4000) < 4000, $"{name}: no ship assigned");
            foreach (var s in Server.Missions[0].Sides) s.DevelopmentTechs = AllTechs;
            short id = session.World.MyShipId!.Value;
            // Docked, and the dock sync (the ship's real parts) has been applied.
            Assert.True(Pump(() => Own(session).StationId != -1
                && Own(session).Loadout?.Count == Ships[id].Parts.Count) < 8000, $"{name}: never docked");
            return session;
        }

        public static State.ShipEntity Own(ConnectionSession session)
        {
            lock (session.Gate) return session.World.Ships[session.World.MyShipId!.Value];
        }

        public Ship ShipOf(ConnectionSession session) => Ships[session.World.MyShipId!.Value];

        private object Live(short shipId)
        {
            var dict = (System.Collections.IDictionary)typeof(Srv)
                .GetField("_peerShips", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Server)!;
            foreach (System.Collections.DictionaryEntry e in dict)
                if (((Ship)e.Value!.GetType().GetProperty("Ship")!.GetValue(e.Value)!).Id == shipId) return e.Value;
            throw new InvalidOperationException($"peer ship {shipId} not found");
        }

        private object Wallet(short shipId)
        {
            var live = Live(shipId);
            return live.GetType().GetProperty("Wallet")!.GetValue(live)!;
        }

        public int ServerMoney(ConnectionSession session)
        {
            var w = Wallet(session.World.MyShipId!.Value);
            return (int)w.GetType().GetProperty("Money")!.GetValue(w)!;
        }

        /// <summary>Change the wallet on the server only (the client is not told).</summary>
        public void SetServerMoneySilently(ConnectionSession session, int amount)
        {
            var w = Wallet(session.World.MyShipId!.Value);
            w.GetType().GetProperty("Money")!.SetValue(w, amount);
        }

        /// <summary>Set the wallet the way the server moves money: the wallet
        /// plus the side-wide <c>MONEY_CHANGE</c>, so the client sees it.</summary>
        public void SetMoney(ConnectionSession session, int amount)
        {
            short id = session.World.MyShipId!.Value;
            Assert.True(Pump(() => { lock (session.Gate) return session.World.MyMoney is not null; }) < 8000, "wallet never told");
            int clientNow;
            lock (session.Gate) clientNow = session.World.MyMoney!.Value;
            Assert.Equal(ServerMoney(session), clientNow);   // the two start in step
            SetServerMoneySilently(session, amount);
            var ship = Ships[id];
            typeof(Srv).GetMethod("BroadcastMoneyChangeSideWide", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Server, [ship.Mission!, ship.SideId!.Value, amount - clientNow, id]);
            Assert.True(Pump(() => { lock (session.Gate) return session.World.MyMoney == amount; }) < 8000, "wallet change never arrived");
        }

        /// <summary>The shop exactly as <c>Game1</c> opens it on dock.</summary>
        public (ShopModel Shop, LoadoutBuyFlow Flow) OpenShop(ConnectionSession session, ShopCustomLoadouts? store = null)
        {
            lock (session.Gate)
            {
                var world = session.World;
                var me = world.Ships[world.MyShipId!.Value];
                var shop = new ShopModel(Catalog, me.HullId, me.Loadout ?? [], world.MyMoney, store ?? new ShopCustomLoadouts(),
                    me.Cargo, world.OwnHardware.HullPaid(me.HullId), world.OwnHardware.FreeParts.ToArray());
                var flow = new LoadoutBuyFlow();
                flow.Attach(world.OwnHardware);
                return (shop, flow);
            }
        }

        /// <summary>One docked frame of <c>Game1.SyncShopWithServer</c>.</summary>
        public LoadoutBuyFlow.Outcome Frame(ConnectionSession session, ShopModel shop, LoadoutBuyFlow flow)
        {
            lock (session.Gate)
            {
                var world = session.World;
                var me = world.Ships[world.MyShipId!.Value];
                long? richest = null;
                foreach (var (shipId, money) in world.WalletByShipId)
                    if (shipId != me.ShipId && (richest is not { } best || money > best)) richest = money;
                return flow.Sync(shop, me, world.OwnHardware, world.MyMoney, richestTeammate: richest);
            }
        }

        /// <summary>Send what the flow decided, as <c>Game1</c> does.</summary>
        public void Send(ConnectionSession session, LoadoutBuyFlow.Order order)
        {
            switch (order.Step)
            {
                case LoadoutBuyFlow.Step.RequestMoney: session.SendMoneyRequest(order.Amount, order.HullId); break;
                case LoadoutBuyFlow.Step.Purchase: session.SendBuyLoadout(order.HullId, order.Launch, order.Parts); break;
            }
        }

        /// <summary>Send, then run frames until the server's answer reached the shop.</summary>
        public LoadoutBuyFlow.Outcome SendAndAwait(ConnectionSession session, ShopModel shop, LoadoutBuyFlow flow, LoadoutBuyFlow.Order order)
        {
            Assert.Equal(LoadoutBuyFlow.Step.Purchase, order.Step);
            Send(session, order);
            LoadoutBuyFlow.Outcome outcome = default;
            Assert.True(Pump(() => (outcome = Frame(session, shop, flow)).Answered) < 8000, "the buy was never answered");
            return outcome;
        }

        public LoadoutV2Presentation Screen(ShopModel shop) =>
            LoadoutV2Presentation.Build(new LoadoutV2Model(shop, Catalog.Hulls, AllTechs, Array.Empty<byte>(), AllTechs), null, null);

        public void Dispose()
        {
            foreach (var s in Sessions) s.Dispose();
            Server.Dispose();
        }
    }

    // ── 1. an unaffordable buy ──────────────────────────────────────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Unaffordable_buy_is_refused_with_the_native_message_Live()
    {
        Require.Artwork();
        using var rig = new Rig(_ => HvyScout);
        var session = rig.Join("p1017_poor");
        var serverShip = rig.ShipOf(session);
        rig.SetMoney(session, 100);

        var (shop, flow) = rig.OpenShop(session);
        shop.CycleHullTo(LxyScout);
        long cost = shop.Cost;
        Assert.True(cost >= 500, "fixture: the Lxy Scout costs at least its $500 hull");
        var wanted = shop.ChosenParts.ToArray();

        // The screen says so before anything is pressed (native state 3 text).
        var screen = rig.Screen(shop);
        Assert.Equal(ShopModel.ShipState.Unaffordable, shop.State);
        Assert.Equal("You can't afford this loadout.", screen.Status.Text);
        Assert.Equal(LoadoutV2Ink.Worse, screen.Status.Ink);
        Assert.Equal($"Need ${cost - 100:N0}", screen.Buy.Text);
        Assert.Equal(LoadoutV2Ink.Muted, screen.Launch.Ink);          // Launch is disabled (load.cpp:2180)

        // Launch: nothing is sent (load.cpp:1500-1506), the line says why.
        Assert.Equal(LoadoutBuyFlow.Step.None, flow.Launch(shop).Step);
        Assert.StartsWith("You can't afford this loadout.", shop.BlockReason);

        // Buy: the shortfall is requested from the team, nothing is bought (load.cpp:1513-1520).
        var ask = flow.Buy(shop, nativeGate: true);
        Assert.Equal(LoadoutBuyFlow.Step.RequestMoney, ask.Step);
        Assert.Equal(cost - 100, ask.Amount);
        rig.Send(session, ask);
        rig.Pump(40);
        Assert.StartsWith("You can't afford this loadout.", rig.Screen(shop).Status.Text);
        Assert.Equal(HvyScout, serverShip.HullType.HullId);
        Assert.Equal(100, rig.ServerMoney(session));
        Assert.False(rig.Frame(session, shop, flow).Answered);

        // The server's own answer. The wallet drops while the request is on its
        // way (the player thought the buy was affordable when Buy was pressed).
        rig.SetMoney(session, (int)cost);
        rig.Frame(session, shop, flow);
        var order = flow.Buy(shop, nativeGate: true);
        Assert.Equal(LoadoutBuyFlow.Step.Purchase, order.Step);
        rig.SetMoney(session, 100);
        var outcome = rig.SendAndAwait(session, shop, flow, order);

        Assert.False(outcome.Complete);
        Assert.Equal(HvyScout, serverShip.HullType.HullId);           // server: ship untouched
        Assert.Equal(100, rig.ServerMoney(session));                  // server: wallet untouched
        Assert.Equal("You can't afford this loadout.", shop.BlockReason);
        Assert.Equal("You can't afford this loadout.", rig.Screen(shop).Status.Text);
        // Revert: the pending loadout is the ship the server says we have.
        Assert.Equal(HvyScout, shop.CurrentHullId);
        Assert.Equal(HvyScout, shop.EffectiveHullId);
        Assert.True(shop.AlreadyOwn);
        Assert.NotEqual(wanted.Select(p => p.PartId), shop.ChosenParts.Select(p => p.PartId));
        Assert.Equal(Rig.Own(session).Loadout!, shop.ChosenParts);
        Assert.Equal(0, shop.Cost);
    }

    // ── 2. a buy the server completes only in part ───────────────────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Part_the_station_does_not_sell_is_dropped_and_the_shop_follows_the_ack_Live()
    {
        Require.Artwork();
        using var rig = new Rig(_ => HvyScout);
        var session = rig.Join("p1017_drop");
        rig.SetMoney(session, 10_000);

        var (shop, flow) = rig.OpenShop(session);
        shop.CycleHullTo(LxyScout);
        ShopModelTests.MountViaSlots(shop, Sanctuary);
        Assert.Contains(shop.ChosenParts, p => p.PartId == Sanctuary);

        // The side loses its tech while the request is on its way.
        var order = flow.Buy(shop, nativeGate: true);
        foreach (var s in rig.Server.Missions[0].Sides) s.DevelopmentTechs = TechTreeBitMask.Empty;
        var outcome = rig.SendAndAwait(session, shop, flow, order);

        Assert.False(outcome.Complete);
        Assert.Equal(ShopModel.PartsUnavailableText, shop.BlockReason);          // load.cpp:2197-2198
        Assert.Equal("Some of the parts you requested are no longer available", rig.Screen(shop).Status.Text);
        // The pending ship is a copy of the real one (load.cpp:2201-2204).
        var real = Rig.Own(session);
        Assert.Equal(real.HullId, shop.CurrentHullId);
        Assert.Equal(real.HullId, shop.EffectiveHullId);
        Assert.Equal(real.Loadout!, shop.ChosenParts);
        Assert.True(shop.AlreadyOwn);
        // The wallet moved by what the shop now shows as the ship's value.
        Assert.Equal(10_000 - shop.CurrentShipValue, rig.ServerMoney(session));
    }

    // ── 3. hull change and passengers ────────────────────────────────────────

    private static Rig CrewedCarrier(out ConnectionSession pilot, out ConnectionSession gunner, out ConnectionSession observer)
    {
        var rig = new Rig(n => n == 0 ? Carrier : HvyScout, (n, ship, core) =>
        {
            if (n != 0) return;
            // One gun, on the first turret: the first boarder gets it, the second has to watch.
            var gun = core.PartTypes.Values.First(p => p.EquipmentType == EquipmentType.Weapon);
            ship.MountPart(gun, 1, ship.HullType.MaxFixedWeapons, ExpandedPartData.FullyMounted);
        }, capitalDock: true);
        // The deliberate observer is an allow-listed, verified loopback admin
        // (native FedSrv/FedSrv.CPP:2405-2406); the gunner remains ordinary.
        rig.Server.Config.LiveSettings ??= new();
        rig.Server.Config.LiveSettings.AdminCheats = new() { Enabled = true, AllowNames = ["p1017_observer"] };
        pilot = rig.Join("p1017_pilot");
        gunner = rig.Join("p1017_gunner");
        observer = rig.Join("p1017_observer");
        var carrier = rig.ShipOf(pilot);
        Assert.Equal(Carrier, carrier.HullType.HullId);
        var gunnerShip = rig.ShipOf(gunner);
        var observerShip = rig.ShipOf(observer);

        gunner.SendBoardShip(carrier.Id);
        Assert.True(rig.Pump(() => gunnerShip.ParentShip == carrier) < 8000, "the gunner never boarded");
        Assert.Equal((short)carrier.HullType.MaxFixedWeapons, gunnerShip.TurretId);
        observer.SendBoardShip(carrier.Id);
        Assert.True(rig.Pump(() => observerShip.ParentShip == carrier) < 8000, "the observer never boarded");
        Assert.Null(observerShip.TurretId);
        return rig;
    }

    private static void AssertLeft(Rig rig, ConnectionSession pilot, ConnectionSession passenger, string who)
    {
        var ship = rig.ShipOf(passenger);
        Assert.True(rig.Pump(() => ship.ParentShip is null) < 8000, $"{who} is still aboard on the server");
        Assert.True(ship.HullType.HasCapability(DataHullTypeIGC.HabmLifepod), $"{who} did not get a lifepod hull (FedSrv.CPP:2301-2302)");
        Assert.NotNull(ship.StationId);                               // still docked
        Assert.DoesNotContain(ship, rig.ShipOf(pilot).ChildShips);
        // LEAVE_SHIP reached both ends (FedSrv.CPP:2312-2316).
        Assert.True(rig.Pump(() =>
        {
            lock (pilot.Gate) lock (passenger.Gate)
                return pilot.World.Ships[ship.Id].ParentId < 0 && passenger.World.Ships[ship.Id].ParentId < 0;
        }) < 8000, $"{who}: the clients still show it aboard");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Hull_change_boots_a_gunner_and_an_observer_Live()
    {
        Require.Artwork();
        using var rig = CrewedCarrier(out var pilot, out var gunner, out var observer);
        var carrier = rig.ShipOf(pilot);
        Assert.Equal(2, carrier.ChildShips.Count);

        // The pilot changes to a hull with no turrets: the gunner's turret is gone
        // (shipIGC.cpp:1379-1399), and every observer leaves (FedSrv.CPP:6214-6224).
        var (shop, flow) = rig.OpenShop(pilot);
        shop.CycleHullTo(HvyScout);
        var outcome = rig.SendAndAwait(pilot, shop, flow, flow.Buy(shop, nativeGate: true));

        Assert.Equal(HvyScout, carrier.HullType.HullId);
        Assert.Equal(carrier.HullType.MaxWeapons, carrier.HullType.MaxFixedWeapons);   // fixture: no turrets
        Assert.True(outcome.Answered);
        AssertLeft(rig, pilot, gunner, "gunner");
        AssertLeft(rig, pilot, observer, "observer");
        Assert.Empty(carrier.ChildShips);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Same_hull_buy_boots_the_gunner_whose_turret_lost_its_gun_Live()
    {
        Require.Artwork();
        using var rig = CrewedCarrier(out var pilot, out var gunner, out var observer);
        var carrier = rig.ShipOf(pilot);
        byte turret = carrier.HullType.MaxFixedWeapons;

        // Same hull, the turret gun sold (FedSrv.CPP:6225-6245).
        var (shop, flow) = rig.OpenShop(pilot);
        Assert.Contains(shop.ChosenParts, p => p.MountId == turret);
        shop.ClearSlot(new SlotDef(EquipmentType.Weapon, turret, IsTurret: true));
        Assert.DoesNotContain(shop.ChosenParts, p => p.MountId == turret);
        rig.SendAndAwait(pilot, shop, flow, flow.Buy(shop, nativeGate: true));

        Assert.Equal(Carrier, carrier.HullType.HullId);
        Assert.DoesNotContain(carrier.Parts, p => p.MountId == turret);
        AssertLeft(rig, pilot, gunner, "gunner");
        // Native compares the observer's turret id (NA = -1) with every part's
        // mount id, and -1 is the first cargo mount: with that slot empty the
        // observer leaves too.
        Assert.Null(carrier.CargoHold[0]);
        AssertLeft(rig, pilot, observer, "observer");
    }

    // ── 4. bought parts are armed ────────────────────────────────────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Bought_parts_arrive_fully_mounted_Live()
    {
        Require.Artwork();
        using var rig = new Rig(_ => HvyScout);
        var session = rig.Join("p1017_armed");
        var serverShip = rig.ShipOf(session);
        rig.SetMoney(session, 10_000);

        var (shop, flow) = rig.OpenShop(session);
        shop.CycleHullTo(LxyScout);
        ShopModelTests.MountViaSlots(shop, Sanctuary);
        var order = flow.Buy(shop, nativeGate: true);
        Assert.True(order.Parts.Count >= 2, "fixture: several parts");
        // A client that claims every part is unmounted (or lies about it).
        var unmounted = order.Parts.Select(p => new ExpandedPartData(p.PartId, p.Amount, p.MountId, 0)).ToArray();
        var outcome = rig.SendAndAwait(session, shop, flow, order with { Parts = unmounted });

        Assert.True(outcome.Complete);
        Assert.Equal(unmounted.Length, serverShip.Parts.Count);
        Assert.All(serverShip.Parts, p => Assert.Equal(1f, p.MountedFraction));                 // Arm(), weaponIGC.h:149-153
        var acked = Rig.Own(session).Loadout!;
        Assert.Equal(unmounted.Length, acked.Count);
        Assert.All(acked, p => Assert.Equal(ExpandedPartData.FullyMounted, p.FractionMounted));
        Assert.All(shop.CurrentParts, p => Assert.Equal(ExpandedPartData.FullyMounted, p.FractionMounted));
    }

    // ── 5. the spawn ship's refund ───────────────────────────────────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Spawn_ship_refund_shown_is_the_refund_applied_Live()
    {
        Require.Artwork();
        // A spawn ship with list value: a $500 hull carrying 3 x $100 Sanctuary.
        using var rig = new Rig(_ => LxyScout, (_, ship, core) =>
        {
            Assert.True(core.TryBuildExpendableLauncher(Sanctuary, out var sanctuary));
            Assert.NotNull(ship.MountPart(sanctuary, 3, 0, ExpandedPartData.FullyMounted));
        });
        var session = rig.Join("p1017_spawn");
        var serverShip = rig.ShipOf(session);
        rig.SetMoney(session, 0);
        var store = new ShopCustomLoadouts();

        // Nothing was paid for it, so it is worth nothing (FedSrv.CPP:902-909).
        var (shop, flow) = rig.OpenShop(session, store);
        Assert.Equal(800, shop.ValueOf(shop.CurrentHullId, shop.CurrentParts));   // its list value
        Assert.Equal(0, shop.CurrentShipValue);                                   // its refund
        Assert.Equal("Refund $0", rig.Screen(shop).Price[1].Text);

        // Keeping the free kit costs nothing, with an empty wallet.
        Assert.Equal(0, shop.Cost);
        var keep = rig.SendAndAwait(session, shop, flow, flow.Buy(shop, nativeGate: false));
        Assert.True(keep.Complete);
        Assert.Equal(0, rig.ServerMoney(session));
        Assert.Equal(3, serverShip.Parts.Single(p => p.PartId == Sanctuary).Amount);
        Assert.Equal(0, shop.CurrentShipValue);

        // Trading it in: shown refund 0, applied refund 0.
        shop.CycleHullTo(HvyScout);
        long shown = shop.Cost;
        Assert.True(shown >= 0, $"the shop promises a refund of {-shown} for a ship nobody paid for");
        rig.SetMoney(session, 5_000);
        rig.Frame(session, shop, flow);
        var traded = rig.SendAndAwait(session, shop, flow, flow.Buy(shop, nativeGate: true));
        Assert.True(traded.Complete);
        Assert.Equal(HvyScout, serverShip.HullType.HullId);
        Assert.Equal(5_000 - shown, rig.ServerMoney(session));

        // Buying the same ship for money: now it is worth what was paid.
        int before = rig.ServerMoney(session);
        shop.CycleHullTo(LxyScout);
        if (!shop.ChosenParts.Any(p => p.PartId == Sanctuary)) ShopModelTests.MountViaSlots(shop, Sanctuary);
        shown = shop.Cost;
        Assert.True(shown >= 800);
        rig.SendAndAwait(session, shop, flow, flow.Buy(shop, nativeGate: true));
        Assert.Equal(before - shown, rig.ServerMoney(session));
        Assert.True(shop.CurrentShipValue >= 800);
        Assert.Equal($"Refund ${shop.CurrentShipValue:N0}", rig.Screen(shop).Price[1].Text);

        before = rig.ServerMoney(session);
        shop.CycleHullTo(HvyScout);
        shown = shop.Cost;
        Assert.True(shown <= -800, "a paid ship is refunded");
        rig.SendAndAwait(session, shop, flow, flow.Buy(shop, nativeGate: true));
        Assert.Equal(before - shown, rig.ServerMoney(session));   // shown refund = applied refund
        lock (session.Gate) Assert.Equal(rig.ServerMoney(session), session.World.MyMoney);
    }

    // ── 6. money moves while docked ──────────────────────────────────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Money_change_while_docked_flips_need_and_buy_Live()
    {
        Require.Artwork();
        using var rig = new Rig(_ => HvyScout);
        var buyer = rig.Join("p1017_buyer");
        var mate = rig.Join("p1017_mate");
        short buyerId = buyer.World.MyShipId!.Value, mateId = mate.World.MyShipId!.Value;
        Assert.Equal(rig.Ships[buyerId].SideId, rig.Ships[mateId].SideId);
        rig.SetMoney(buyer, 100);
        rig.SetMoney(mate, 0);

        var (shop, flow) = rig.OpenShop(buyer);
        shop.CycleHullTo(LxyScout);
        long cost = shop.Cost;
        long need = cost - 100;
        rig.Frame(buyer, shop, flow);

        // Short, and nobody on the team can cover it: Buy is disabled (load.cpp:2152-2166, :2181).
        var screen = rig.Screen(shop);
        Assert.Equal($"Need ${need:N0}", screen.Buy.Text);
        Assert.Equal(LoadoutV2Ink.Muted, screen.Buy.Ink);
        Assert.False(shop.BuyEnabled);
        Assert.Equal(LoadoutBuyFlow.Step.None, flow.Buy(shop, nativeGate: true).Step);

        // The teammate gets rich: Buy is live again and asks for the shortfall.
        rig.SetMoney(mate, 5_000);
        Assert.True(rig.Pump(() => { rig.Frame(buyer, shop, flow); return shop.BuyEnabled; }) < 8000, "the teammate's money never reached the buyer");
        screen = rig.Screen(shop);
        Assert.Equal($"Need ${need:N0}", screen.Buy.Text);
        Assert.Equal(LoadoutV2Ink.Worse, screen.Buy.Ink);
        var ask = flow.Buy(shop, nativeGate: true);
        Assert.Equal(LoadoutBuyFlow.Step.RequestMoney, ask.Step);
        Assert.Equal(need, ask.Amount);
        rig.Send(buyer, ask);

        // The teammate donates exactly the shortfall (FM_C_PLAYER_DONATE): Need $X -> Buy.
        mate.SendPlayerDonation(new PlayerDonate { MoneyGiven = (int)need, ShipID = buyerId });
        Assert.True(rig.Pump(() => { rig.Frame(buyer, shop, flow); return shop.BuyShortfall == 0; }) < 8000, "the donation never reached the shop");
        Assert.Equal((int)cost, rig.ServerMoney(buyer));
        screen = rig.Screen(shop);
        Assert.Equal("Buy", screen.Buy.Text);
        Assert.Equal(LoadoutV2Ink.Accent, screen.Buy.Ink);
        Assert.Equal(LoadoutV2Ink.Accent, screen.Launch.Ink);
        Assert.Equal("Press 'buy' to purchase this loadout.", screen.Status.Text);

        // The buyer gives a dollar away: Buy -> Need $1.
        buyer.SendPlayerDonation(new PlayerDonate { MoneyGiven = 1, ShipID = mateId });
        Assert.True(rig.Pump(() => { rig.Frame(buyer, shop, flow); return shop.BuyShortfall == 1; }) < 8000, "the buyer's own donation never reached the shop");
        screen = rig.Screen(shop);
        Assert.Equal("Need $1", screen.Buy.Text);
        Assert.Equal("You can't afford this loadout.", screen.Status.Text);
        Assert.Equal(LoadoutV2Ink.Muted, screen.Launch.Ink);

        // And back: the buy then goes through for exactly the shown cost.
        mate.SendPlayerDonation(new PlayerDonate { MoneyGiven = 1, ShipID = buyerId });
        Assert.True(rig.Pump(() => { rig.Frame(buyer, shop, flow); return shop.BuyShortfall == 0; }) < 8000);
        var outcome = rig.SendAndAwait(buyer, shop, flow, flow.Buy(shop, nativeGate: true));
        Assert.True(outcome.Complete);
        Assert.Equal(0, rig.ServerMoney(buyer));
        Assert.Equal(LxyScout, rig.ShipOf(buyer).HullType.HullId);
        Assert.Equal("This is your current loadout.", rig.Screen(shop).Status.Text);
        Assert.Equal(LoadoutV2Ink.Muted, rig.Screen(shop).Buy.Ink);   // Buy disabled for the ship you own
    }
}
