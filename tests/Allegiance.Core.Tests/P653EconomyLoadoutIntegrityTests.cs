using System.Reflection;
using System.Runtime.ExceptionServices;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Server;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

// The audit packets run against real handlers and a real core/map, with an
// unbound transport. No sockets, timing pumps, or test-only validation rules.
[Trait("Requires", "Artwork")]
public sealed class P653EconomyLoadoutIntegrityTests
{
    private sealed class World : IDisposable
    {
        public readonly Srv Server = new();
        public readonly ServerHost Host = new();
        public readonly Mission Mission;
        public readonly Ship Buyer;
        public readonly Ship Mate;
        public readonly ConnectionId Conn = new(1);
        public readonly List<byte[]> Output = [];
        public int Changes;
        public int Kills;
        public Sector Sector => Buyer.Sector!;
        public PlayerWallet Wallet => Server.Ships.Single(l => l.Ship == Buyer).Wallet;
        public PlayerWallet MateWallet => Server.Ships.Single(l => l.Ship == Mate).Wallet;
        public World()
        {
            Require.Artwork();
            var root = TestArtRoot.Value;
            var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(root, "PCore001.igc")));
            var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(root, "XXV_simple.igc")));
            Mission = Mission.Assemble(core, map, [new SideSpec(0, "Alpha", 1), new SideSpec(1, "Bravo", 3)], Host);
            Mission.Stage = MissionStage.Started;
            Host.Transport = new TcpTransport(Host);
            typeof(TcpTransport).GetField("_isHost", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Host.Transport, true);
            Field("_host", Host);
            Field("_config", new ServerConfig());
            Field("_missions", new List<Mission> { Mission });
            var sector = Mission.Sectors.First(s => s.Stations.Any(st => st.SideId == 0));
            var station = sector.Stations.First(st => st.SideId == 0);
            var rock = new DataAsteroidIGC(1, [5000, 0, 0], [0, 1, 0], [0, 0, 1], [0, 0, 0, 0], 0, 0, DataAsteroidIGC.AabmBuildable, 30000, 1000, 100, "", "", "", sector.Id, "Build rock", 255);
            sector.AttachAsteroids([rock]);
            var hull = core.HullTypes.Values.First(h => h.GroupId >= 0 && h.Hardpoints.Length == 1 && !h.HasCapability(DataHullTypeIGC.HabmLifepod));
            // Controlled economy values avoid depending on the artwork's free shop.
            hull = hull with { Price = 500, MaxAmmo = 100, CapacityMagazine = 10, CapacityChaffLauncher = 10, CapacityDispenser = 10 };
            ((Dictionary<short, DataHullTypeIGC>)core.HullTypes)[hull.HullId] = hull;
            Buyer = new Ship(100, hull, new Vector3(100, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 0, StationId = station.StationId };
            Mate = new Ship(101, hull, new Vector3(200, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 0, StationId = station.StationId };
            Mission.PlaceShip(Buyer, sector);
            Mission.PlaceShip(Mate, sector);
            Server.TestSeedPeers([(Conn, Buyer, sector), (new ConnectionId(2), Mate, sector)]);
            Mission.Sides[0].LeaderShipId = Buyer.Id;
            Server._testSendCapture = (conn, packet) => { if (conn == Conn) Output.AddRange(FedPacket.Unpack(packet).Select(m => m.ToArray())); };
            Host.LoadoutChange = (ship, part, change) => { Changes++; Call("BroadcastLoadoutChange", ship, part, change); };
            Host.ShipKilled = (_, _, _, _, _, _) => Kills++;
        }
        private void Field(string name, object value) => typeof(Srv).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Server, value);
        public object? Call(string name, params object?[] args)
        {
            try { return typeof(Srv).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Server, args); }
            catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
        }
        public void Send(IFedMessage msg) => Call("ProcessDatagram", Conn, FedPacket.Pack([msg.Encode()]));
        public void Buy(params ExpandedPartData[] parts) => Send(new BuyLoadoutRequest { HullId = Buyer.HullType.HullId, Parts = parts });
        public DataPartTypeIGC Gun()
        {
            var gun = Mission.Core.PartTypes.Values.First(p => p.EquipmentType == EquipmentType.Weapon) with { Price = 25, PartMask = 0, TtbmRequired = new byte[50], SuccessorPartId = -1 };
            ((Dictionary<short, DataPartTypeIGC>)Mission.Core.PartTypes)[gun.PartId] = gun;
            return gun;
        }
        public DataPartTypeIGC Pack()
        {
            var pack = Mission.Core.PartTypes.Values.First(p => p.Type is DataPartTypeIGC.Pack { PackType: 0 }) with { Price = 0, PartMask = 0, TtbmRequired = new byte[50], SuccessorPartId = -1 };
            ((Dictionary<short, DataPartTypeIGC>)Mission.Core.PartTypes)[pack.PartId] = pack;
            return pack;
        }
        public void Dispose() => Host.Transport!.Shutdown();
    }

    [SkippableTheory]
    [InlineData(-1000)]
    [InlineData(int.MinValue + 1)]
    public void H01_Negative_gifts_fail_and_positive_gifts_and_leader_toggle_work(int attack)
    {
        using var w = new World();
        w.Wallet.Money = 100;
        w.Send(new AutoDonate { SidDonateTo = w.Mate.Id, Amount = attack });
        Assert.Equal(100, w.Wallet.Money);
        Assert.Equal(0, w.MateWallet.Money);
        w.Send(new AutoDonate { SidDonateTo = w.Mate.Id, Amount = 20 });
        Assert.Equal(80, w.Wallet.Money);
        Assert.Equal(20, w.MateWallet.Money);
        // The native donation-confidence election can promote the recipient.
        w.Mission.Sides[0].LeaderShipId = w.Buyer.Id;
        w.Send(new AutoDonate { SidDonateTo = w.Mate.Id, Amount = -1 });
        Assert.Same(w.Buyer, w.MateWallet.AutoDonateTarget);
        w.Send(new AutoDonate { SidDonateTo = w.Mate.Id, Amount = -1 });
        Assert.Null(w.MateWallet.AutoDonateTarget);
    }

    [SkippableFact]
    public void H01_Overflow_and_negative_wallet_do_not_transfer()
    {
        using var w = new World();
        w.Wallet.Money = 100;
        w.MateWallet.Money = int.MaxValue;
        w.Send(new AutoDonate { SidDonateTo = w.Mate.Id, Amount = 10 });
        Assert.Equal(100, w.Wallet.Money);
        Assert.Equal(int.MaxValue, w.MateWallet.Money);
        w.Send(new PlayerDonate { ShipID = w.Mate.Id, MoneyGiven = 10 });
        Assert.Equal(100, w.Wallet.Money);
        Assert.Equal(int.MaxValue, w.MateWallet.Money);
        w.Wallet.Money = -100;
        w.MateWallet.Money = 20;
        w.Send(new AutoDonate { SidDonateTo = w.Mate.Id, Amount = 10 });
        Assert.Equal(-100, w.Wallet.Money);
        Assert.Equal(20, w.MateWallet.Money);
        w.Wallet.Money = 100;
        w.Send(new PlayerDonate { ShipID = w.Mate.Id, MoneyGiven = 10 });
        Assert.Equal(90, w.Wallet.Money);
        Assert.Equal(30, w.MateWallet.Money);
        w.Mission.Sides[0].LeaderShipId = w.Mate.Id;
        w.Send(new AutoDonate { SidDonateTo = w.Mate.Id, Amount = -1 });
        Assert.Null(w.MateWallet.AutoDonateTarget); // non-leader toggle rejected
    }

    [SkippableFact]
    public void H03_Free_hull_has_no_refund_and_paid_parts_refund_once_to_player()
    {
        using var w = new World();
        var gun = w.Gun();
        w.Wallet.Money = 100;
        w.Buy(new ExpandedPartData(gun.PartId, 1, 0, 255));
        Assert.Equal(75, w.Wallet.Money);
        long treasury = w.Mission.Sides[0].Money;
        for (int i = 0; i < 100; i++) w.Send(new BoardShip { SidParent = w.Mate.Id });
        Assert.Equal(100, w.Wallet.Money);
        Assert.Equal(treasury, w.Mission.Sides[0].Money);
        Assert.Same(w.Mate, w.Buyer.ParentShip);
        Assert.Empty(w.Buyer.Parts);
    }

    [SkippableFact]
    public void H03_Free_spawn_parts_cannot_be_sold_but_bought_parts_can()
    {
        using var w = new World();
        var gun = w.Gun();
        w.Server.ShipSpawner = (_, mission, id) =>
        {
            // A fresh DOCKED spawn (native fresh seats enter the base hangar,
            // fsmission.cpp:791-796): boarding requires both parties docked
            // (FedSrv.CPP:2342-2345), so this injected spawner docks its own
            // ship explicitly (the default-path auto-dock is bypassed for
            // injected spawners).
            var ship = new Ship(id, w.Buyer.HullType, w.Buyer.Position, Vector3.Zero, Orientation.Identity) { SideId = 0, StationId = w.Buyer.StationId };
            mission.PlaceShip(ship, w.Sector);
            ship.MountPart(gun, 1, 0, 255);
            return (ship, w.Sector);
        };
        var live = (Srv.LiveShip)w.Call("AssignShip", new LogonRequest(), (short)120, (short)0)!;
        w.Server.TestSeedPeers([(w.Conn, live.Ship, live.Sector), (new ConnectionId(2), w.Mate, w.Sector)]);
        long treasury = w.Mission.Sides[0].Money;
        w.Send(new BoardShip { SidParent = w.Mate.Id });
        Assert.Equal(treasury, w.Mission.Sides[0].Money);
        Assert.Equal(0, w.Server.Ships.Single(l => l.Ship == live.Ship).Wallet.Money);
        Assert.Empty(live.Ship.Parts);
    }

    [SkippableFact]
    public void H03_Launcher_purchase_and_refund_charge_only_remaining_units()
    {
        using var w = new World();
        var launcherId = w.Mission.Core.Launchers.Keys.First();
        Assert.True(w.Mission.Core.TryBuildExpendableLauncher(launcherId, out var launcher));
        launcher = launcher with { Price = 2, PartMask = 0, TtbmRequired = new byte[50], SuccessorPartId = -1 };
        ((Dictionary<short, DataPartTypeIGC>)w.Mission.Core.PartTypes)[launcherId] = launcher;
        int capacity = Ship.ClampPartAmount(w.Buyer.HullType, w.Mission.Core, launcher, short.MaxValue);
        Assert.True(capacity > 1);
        w.Wallet.Money = 1000;
        w.Buy(new ExpandedPartData(launcherId, (short)capacity, 0, 255));
        Assert.Equal(1000 - 2 * capacity, w.Wallet.Money);
        w.Buyer.SetPartAmount(w.Buyer.Parts.Single(), 1);
        w.Send(new BoardShip { SidParent = w.Mate.Id });
        Assert.Equal(1000 - 2 * (capacity - 1), w.Wallet.Money);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void H03_Promotion_transfers_paid_credit_and_preserves_free_parts(bool paid)
    {
        using var w = new World();
        w.Buyer.MountPart(w.Gun(), 1, 0, 255);
        w.Buyer.MountPart(w.Pack(), 2, 0, 255);
        Assert.True(w.Buyer.TrySwapPartWithCargo(EquipmentType.Pack, 0, -1));
        if (paid) w.Buyer.RefundableHullValue = 500;
        else w.Buyer.MarkSpawnLoadoutFree();
        w.Mate.ParentShip = w.Buyer;
        w.Mate.TurretId = 0;
        w.Buyer.AddChildShip(w.Mate);
        w.Mate.Promote();
        Assert.Equal(paid ? 500 : 0, w.Mate.RefundableHullValue);
        Assert.Equal(0, w.Buyer.RefundableHullValue);
        Assert.Equal(!paid, w.Mate.Parts.Single().FreeSpawn);
        Assert.NotNull(w.Mate.CargoHold[0]);
        Assert.Equal(!paid, w.Mate.CargoHold[0]!.FreeSpawn);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void H03_Teleport_refunds_paid_hardware_once_and_never_free_hull(bool paid)
    {
        using var w = new World();
        var hull = w.Buyer.HullType;
        var station = w.Sector.Stations.First(st => st.StationId == w.Buyer.StationId);
        var stationType = w.Mission.Core.StationTypes[station.StationTypeId];
        w.Wallet.Money = 1000;
        if (paid)
        {
            // Buy another real hull at a controlled, tech-free station.
            var next = w.Mission.Core.HullTypes.Values.First(h => h.GroupId >= 0 && h.HullId != hull.HullId && !h.HasCapability(DataHullTypeIGC.HabmLifepod)) with
            { Price = 200, TtbmRequired = Enumerable.Repeat((byte)255, 50).ToArray(), SuccessorHullId = -1 };
            ((Dictionary<short, DataHullTypeIGC>)w.Mission.Core.HullTypes)[next.HullId] = next;
            ((Dictionary<short, DataStationTypeIGC>)w.Mission.Core.StationTypes)[stationType.StationTypeId] = stationType with { TtbmLocal = Enumerable.Repeat((byte)255, 50).ToArray() };
            w.Send(new BuyLoadoutRequest { HullId = next.HullId });
            Assert.Equal(800, w.Wallet.Money);
            hull = next;
        }
        // A restart-capable station that cannot sell the current hull forces
        // native's bNewHull branch. Reset techs so the request cannot reuse it.
        ((Dictionary<short, DataStationTypeIGC>)w.Mission.Core.StationTypes)[stationType.StationTypeId] = stationType with { TtbmLocal = new byte[50] };
        if (!paid) w.Buyer.ChangeHull(hull with { TtbmRequired = Enumerable.Repeat((byte)255, 50).ToArray() });
        long treasury = w.Mission.Sides[0].Money;
        for (int i = 0; i < 5; i++) w.Call("PerformTeleport", w.Conn, w.Server.Ships.Single(l => l.Ship == w.Buyer), station.StationId);
        Assert.Equal(1000, w.Wallet.Money);
        Assert.Equal(treasury, w.Mission.Sides[0].Money);
        Assert.True(w.Buyer.HullType.HasCapability(DataHullTypeIGC.HabmLifepod));
    }

    [SkippableFact]
    public void H06_Initial_bot_purchase_starts_docked_and_can_launch()
    {
        using var w = new World();
        var live = (Srv.LiveShip)w.Call("AssignShip", new LogonRequest(), (short)120, (short)0)!;
        Assert.NotNull(live.Ship.StationId);
        w.Server.TestSeedPeers([(w.Conn, live.Ship, live.Sector)]);
        w.Call("SendInitialDockState", w.Conn, live.Ship);
        Assert.Equal(live.Ship.StationId, DockedNotify.Decode(w.Output.Last()).StationId);
        w.Send(new BuyLoadoutRequest { HullId = live.Ship.HullType.HullId, FLaunch = true });
        Assert.Null(live.Ship.StationId);
        Assert.True(BuyLoadoutAck.Decode(w.Output.Last(m => FedMessageHeader.Read(m).Fmid == BuyLoadoutAck.Fmid)).FLaunch);
    }

    [SkippableFact]
    public void H06_Docked_pod_can_buy_a_fighter_and_relaunch_like_MCP()
    {
        using var w = new World();
        var fighter = w.Buyer.HullType with { TtbmRequired = new byte[50], SuccessorHullId = -1 };
        ((Dictionary<short, DataHullTypeIGC>)w.Mission.Core.HullTypes)[fighter.HullId] = fighter;
        var pod = w.Mission.Core.HullTypes[w.Mission.Sides[0].Civilization.Lifepod];
        w.Buyer.ChangeHull(pod);
        w.Wallet.Money = 1000;
        var dock = w.Buyer.StationId;
        w.Buyer.StationId = null;
        w.Send(new BuyLoadoutRequest { HullId = fighter.HullId, FLaunch = true });
        Assert.Equal(pod.HullId, w.Buyer.HullType.HullId); // H-06: no in-space pod un-death
        Assert.Equal(1000, w.Wallet.Money);
        w.Buyer.StationId = dock;
        w.Send(new BuyLoadoutRequest { HullId = fighter.HullId, FLaunch = true });
        Assert.Equal(fighter.HullId, w.Buyer.HullType.HullId);
        Assert.Null(w.Buyer.StationId);
        Assert.Equal(500, w.Wallet.Money);
        Assert.True(BuyLoadoutAck.Decode(w.Output.Last(m => FedMessageHeader.Read(m).Fmid == BuyLoadoutAck.Fmid)).FLaunch);
    }

    [SkippableTheory]
    [InlineData("flying")]
    [InlineData("dead")]
    [InlineData("boarded")]
    [InlineData("missing_station")]
    public void H06_Invalid_purchase_state_fails_and_docked_purchase_launches(string state)
    {
        using var w = new World();
        var gun = w.Gun();
        w.Wallet.Money = 100;
        var station = w.Buyer.StationId;
        if (state == "flying") w.Buyer.StationId = null;
        if (state == "dead") w.Buyer.SetFraction(0);
        if (state == "boarded") w.Buyer.ParentShip = w.Mate;
        if (state == "missing_station") w.Buyer.StationId = short.MaxValue;
        w.Buy(new ExpandedPartData(gun.PartId, 1, 0, 255));
        Assert.Empty(w.Buyer.Parts);
        Assert.Equal(100, w.Wallet.Money);
        w.Buyer.StationId = station;
        w.Buyer.SetFraction(1);
        w.Buyer.ParentShip = null;
        w.Send(new BuyLoadoutRequest { HullId = w.Buyer.HullType.HullId, FLaunch = true, Parts = [new(gun.PartId, 1, 0, 255)] });
        Assert.Single(w.Buyer.Parts);
        Assert.Null(w.Buyer.StationId);
        Assert.True(BuyLoadoutAck.Decode(w.Output.Last(m => FedMessageHeader.Read(m).Fmid == BuyLoadoutAck.Fmid)).FLaunch);
    }

    [SkippableFact]
    public void H07_Invalid_and_duplicate_mounts_fail_and_real_mount_and_cargo_work()
    {
        using var w = new World();
        var gun = w.Gun();
        w.Wallet.Money = 1000;
        w.Buy(new(gun.PartId, 1, 100, 255), new(gun.PartId, 1, 0, 255), new(gun.PartId, 1, 0, 255), new(gun.PartId, 1, 255, 255));
        Assert.Single(w.Buyer.Parts);
        Assert.Equal((byte)0, w.Buyer.Parts[0].MountId);
        Assert.NotNull(w.Buyer.CargoHold[0]);
        Assert.Equal(950, w.Wallet.Money);
    }

    [SkippableFact]
    public void H08_Quantities_and_ammo_are_bounded_and_normal_reload_works()
    {
        using var w = new World();
        var pack = w.Pack();
        Assert.Null(w.Buyer.MountPart(pack, -1, 0, 255));
        var part = w.Buyer.MountPart(pack, short.MaxValue, 0, 255)!;
        Assert.Equal((short)100, part.Amount);
        w.Buyer.SetPartAmount(part, short.MaxValue);
        Assert.Equal((short)100, w.Buyer.Parts[0].Amount);
        w.Buyer.Ammo = short.MaxValue;
        Assert.Equal((short)100, w.Buyer.Ammo);
        w.Buyer.Ammo = -1;
        Assert.Equal((short)0, w.Buyer.Ammo);
        Assert.True(w.Buyer.TrySwapPartWithCargo(EquipmentType.Pack, 0, -1));
        w.Buyer.Ammo = 90;
        Assert.True(w.Buyer.TryApplyReloadEntry(-1, 10));
        Assert.Equal((short)100, w.Buyer.Ammo);
        Assert.Equal((short)90, w.Buyer.CargoHold[0]!.Amount);
        w.Buyer.StationId = null;
        w.Buyer.PendingTreasureOffer = (pack.PartId, short.MaxValue);
        w.Send(new TreasureAck { MountId = 0 });
        Assert.Equal((short)100, w.Buyer.Parts.Single().Amount);
        w.Buyer.PendingTreasureOffer = (pack.PartId, short.MaxValue);
        w.Send(new TreasureAck { MountId = 0 });
        Assert.Equal((short)100, w.Buyer.Parts.Single().Amount);
        w.Send(new DropPart { Et = EquipmentType.Pack, Mount = 0 });
        Assert.Equal((short)100, w.Sector.Models.OfType<Treasure>().Single().Data.Amount);
        // Exercise the audit's actual BUY_LOADOUT boundary as well as the
        // shared setters: bounded pack inventory, no poisoned negative rebuy.
        w.Buyer.StationId = w.Mate.StationId;
        w.Buy(new ExpandedPartData(pack.PartId, short.MaxValue, 255, 255));
        Assert.Equal((short)100, w.Buyer.CargoHold[0]!.Amount);
        w.Buy(new ExpandedPartData(pack.PartId, -1, 255, 255));
        Assert.Equal((short)100, w.Buyer.CargoHold[0]!.Amount);
        var launcherId = w.Mission.Core.Launchers.Keys.First();
        Assert.True(w.Mission.Core.TryBuildExpendableLauncher(launcherId, out var launcher));
        launcher = launcher with { Price = 0, PartMask = 0, TtbmRequired = new byte[50], SuccessorPartId = -1 };
        ((Dictionary<short, DataPartTypeIGC>)w.Mission.Core.PartTypes)[launcherId] = launcher;
        w.Buy(new ExpandedPartData(launcherId, short.MaxValue, 0, 255));
        Assert.InRange(w.Buyer.Parts.Single().Amount, (short)1, (short)10);
        w.Buy(new ExpandedPartData(launcherId, 1, 0, 255));
        Assert.Equal((short)1, w.Buyer.Parts.Single().Amount);
    }

    [SkippableFact]
    public void H09_Buy_and_swap_publish_one_final_loadout()
    {
        using var w = new World();
        w.Wallet.Money = 1000;
        var gun = w.Gun();
        var pack = w.Pack();
        w.Send(new BuyLoadoutRequest { HullId = w.Buyer.HullType.HullId, FLaunch = true,
            Parts = [new(gun.PartId, 1, 0, ExpandedPartData.FullyMounted), new(pack.PartId, 10, 0, ExpandedPartData.FullyMounted),
                new(gun.PartId, 1, 254, ExpandedPartData.FullyMounted)] });
        Assert.Equal(1, w.Changes);
        var loadout = Allegiance.Core.Wire.LoadoutChange.Decode(Assert.Single(w.Output, m => FedMessageHeader.Read(m).Fmid == Allegiance.Core.Wire.LoadoutChange.Fmid));
        Assert.Equal(3, loadout.Parts.Count);
        Assert.Contains(loadout.Parts, p => p.PartId == gun.PartId && p.MountId == 254);
        Assert.All(loadout.Parts, p => Assert.Equal(ExpandedPartData.FullyMounted, p.FractionMounted));
        // Launch can refill the pack: the one published snapshot must carry
        // the actual final inventory, not a prelaunch purchase snapshot.
        Assert.Equal(w.Buyer.Parts.Single(p => p.PartId == pack.PartId).Amount,
            loadout.Parts.Single(p => p.PartId == pack.PartId).Amount);
        w.Buyer.StationId = null;
        w.Changes = 0;
        w.Output.Clear();
        w.Send(new SwapPart { EtOld = EquipmentType.Weapon, MountOld = 0, MountNew = -1 });
        Assert.Equal(1, w.Changes);
        Assert.Single(w.Buyer.Parts);
        Assert.NotNull(w.Buyer.CargoHold[0]);
        // P660b retains the one final site event, but the command's native
        // SWAP_PART echo is the publication (FedSrv/FedSrv.CPP:6497-6498).
        Assert.DoesNotContain(w.Output, m => FedMessageHeader.Read(m).Fmid == Allegiance.Core.Wire.LoadoutChange.Fmid);
        var echo = SwapPart.Decode(Assert.Single(w.Output));
        Assert.Equal(w.Buyer.Id, echo.ShipID);
        Assert.Equal(-1, echo.MountNew);
        var final = w.Buyer.ExportInventoryForAck();
        Assert.Equal(3, final.Count);
        Assert.Contains(final, p => p.MountId == 255 && p.PartId == gun.PartId);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void P665_Flight_roundtrip_resets_readiness_preserves_refund_provenance_and_restores_publication(bool paid)
    {
        using var w = new World();
        var gun = w.Gun();
        w.Wallet.Money = 100;
        if (paid) w.Buy(new ExpandedPartData(gun.PartId, 1, 0, 240));
        else
        {
            w.Buyer.MountPart(gun, 1, 0, 240);
            w.Buyer.MarkSpawnLoadoutFree();
        }
        var dock = w.Buyer.StationId;
        w.Buyer.StationId = null;
        w.Output.Clear();
        w.Send(new SwapPart { EtOld = EquipmentType.Weapon, MountOld = 0, MountNew = 0 });
        Assert.Equal((byte)240, w.Buyer.Parts.Single().FractionMounted);
        w.Send(new SwapPart { EtOld = EquipmentType.Weapon, MountOld = 0, MountNew = -1 });
        Assert.Equal((byte)0, w.Buyer.CargoHold[0]!.FractionMounted);
        Assert.Equal(!paid, w.Buyer.CargoHold[0]!.FreeSpawn);
        w.Send(new SwapPart { EtOld = EquipmentType.Weapon, MountOld = -1, MountNew = 0 });
        Assert.Equal((byte)0, w.Buyer.Parts.Single().FractionMounted);
        Assert.Equal(!paid, w.Buyer.Parts.Single().FreeSpawn);
        Assert.Single(w.Buyer.Weapons);
        Assert.Equal(3, w.Output.Count);
        Assert.All(w.Output, m => Assert.Equal(SwapPart.Fmid, FedMessageHeader.Read(m).Fmid));
        w.Buyer.MountPart(w.Pack(), 1, 0, 255);
        var published = Allegiance.Core.Wire.LoadoutChange.Decode(w.Output.Last());
        Assert.Equal(2, published.Parts.Count);
        Assert.Equal((byte)240, published.Parts.Single(p => p.PartId != gun.PartId).FractionMounted);
        w.Buyer.StationId = dock;
        long treasury = w.Mission.Sides[0].Money;
        w.Send(new BoardShip { SidParent = w.Mate.Id });
        w.Send(new BoardShip { SidParent = w.Mate.Id });
        Assert.Equal(100, w.Wallet.Money);
        Assert.Equal(treasury, w.Mission.Sides[0].Money);
    }

    [SkippableFact]
    public void H20_Decoder_rejects_audit_sized_tail_and_accepts_native_limit()
    {
        byte[] Packet(int count) => new BuyLoadoutRequest { HullId = 92, Parts = Enumerable.Repeat(new ExpandedPartData(short.MaxValue, 1, 0, 255), count).ToArray() }.Encode();
        Assert.Throws<ArgumentException>(() => BuyLoadoutRequest.Decode(Packet(10920)));
        Assert.Equal(20, BuyLoadoutRequest.Decode(Packet(20)).Parts.Count);
    }

    [SkippableFact]
    public void H22_Docked_drop_swap_reload_fail_and_flying_actions_work()
    {
        using var w = new World();
        var gun = w.Gun();
        var pack = w.Pack();
        w.Buyer.MountPart(gun, 1, 0, 255);
        w.Buyer.MountPart(pack, 50, 0, 255);
        w.Send(new DropPart { Et = EquipmentType.Weapon, Mount = 0 });
        w.Send(new SwapPart { EtOld = EquipmentType.Weapon, MountOld = 0, MountNew = -1 });
        Assert.Equal(2, w.Buyer.Parts.Count);
        Assert.Null(w.Buyer.CargoHold[0]);
        w.Buyer.TrySwapPartWithCargo(EquipmentType.Pack, 0, -1);
        w.Buyer.Ammo = 0;
        w.Send(new Reload { Entries = [new ReloadData(10, -1)] });
        Assert.Equal((short)0, w.Buyer.Ammo);
        w.Buyer.StationId = null;
        w.Send(new SwapPart { EtOld = EquipmentType.Weapon, MountOld = 0, MountNew = 100 });
        Assert.Equal((byte)0, w.Buyer.Parts.Single().MountId);
        w.Send(new SwapPart { EtOld = EquipmentType.Weapon, MountOld = -1, MountNew = 0 });
        Assert.IsType<DataPartTypeIGC.Pack>(w.Buyer.CargoHold[0]!.Type.Type);
        w.Send(new Reload { Entries = [new ReloadData(10, -1)] });
        Assert.Equal((short)10, w.Buyer.Ammo);
        w.Send(new DropPart { Et = EquipmentType.Weapon, Mount = 0 });
        Assert.Empty(w.Buyer.Parts);
        Assert.Single(w.Sector.Models.OfType<Treasure>());
    }

    [SkippableFact]
    public void H18_Constructor_and_rock_are_used_once_and_gone_builder_aborts()
    {
        using var w = new World();
        var rock = w.Sector.Mineable.First(r => w.Mission.Core.StationTypes.Values.Any(st => r.Data.HasCapability(st.AabmBuild)));
        var st = w.Mission.Core.StationTypes.Values.First(st => rock.Data.HasCapability(st.AabmBuild));
        var builder = new Ship(102, w.Buyer.HullType, new Vector3(1000, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 0, IsNpc = true, PilotType = Ship.PilotTypeBuilder, BuilderStationType = st, WaitsForBuildOrder = true };
        w.Mission.PlaceShip(builder, w.Sector);
        var order = new OrderChange { ShipID = builder.Id, CommandId = OrderChange.CidBuild, ObjectType = IgcObjectType.Asteroid, ObjectID = rock.Data.AsteroidId, Command = OrderChange.CmdAccepted };
        w.Send(order);
        // Native build orders travel before drilling (shipIGC.cpp:2810-2827).
        // Reach the accepted rock before testing H-18's committed-state guard.
        var rp = rock.Data.Position;
        builder.Position = new Vector3(rp[0], rp[1], rp[2]);
        typeof(Ship).GetMethod("TryStartBuilderBuild", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(builder, [0d]);
        Assert.Single(w.Sector.BuildingEffects);
        var otherRock = rock.Data with { AsteroidId = 30001 };
        w.Sector.AttachAsteroids([otherRock]);
        order.ObjectID = otherRock.AsteroidId;
        w.Send(order);
        Assert.Single(w.Sector.BuildingEffects); // same constructor, different rock
        order.ObjectID = rock.Data.AsteroidId;
        w.Send(order);
        Assert.Single(w.Sector.BuildingEffects);
        var other = new Ship(103, builder.HullType, builder.Position, Vector3.Zero, Orientation.Identity) { SideId = 0, IsNpc = true, PilotType = Ship.PilotTypeBuilder, BuilderStationType = st, WaitsForBuildOrder = true };
        w.Mission.PlaceShip(other, w.Sector);
        order.ShipID = other.Id;
        w.Send(order);
        Assert.Null(other.BuilderAsteroidTarget); // occupied rock cannot be accepted, even before arrival
        Assert.Single(w.Sector.BuildingEffects);
        w.Sector.Remove(builder);
        w.Sector.Update(100);
        Assert.Empty(w.Sector.BuildingEffects);
        Assert.Contains(rock, w.Sector.Mineable);
    }

    [SkippableFact]
    public void H18_Docked_and_nonleader_orders_fail_and_commander_build_completes()
    {
        using var w = new World();
        var rock = w.Sector.Mineable.Single();
        var st = w.Mission.Core.StationTypes.Values.First(st => rock.Data.HasCapability(st.AabmBuild));
        var builder = new Ship(102, w.Buyer.HullType, new Vector3(1000, 0, 0), Vector3.Zero, Orientation.Identity)
        { SideId = 0, IsNpc = true, PilotType = Ship.PilotTypeBuilder, BuilderStationType = st, WaitsForBuildOrder = true, StationId = w.Buyer.StationId };
        w.Mission.PlaceShip(builder, w.Sector);
        var order = new OrderChange { ShipID = builder.Id, CommandId = OrderChange.CidBuild, ObjectType = IgcObjectType.Asteroid, ObjectID = rock.Data.AsteroidId };
        w.Send(order);
        Assert.Empty(w.Sector.BuildingEffects);
        Assert.Null(builder.BuilderAsteroidTarget);
        builder.StationId = null;
        w.Mission.Sides[0].LeaderShipId = w.Mate.Id;
        w.Send(order);
        Assert.Empty(w.Sector.BuildingEffects);
        Assert.Null(builder.BuilderAsteroidTarget);
        w.Mission.Sides[0].LeaderShipId = w.Buyer.Id;
        w.Send(order);
        var rp = rock.Data.Position;
        builder.Position = new Vector3(rp[0], rp[1], rp[2]);
        typeof(Ship).GetMethod("TryStartBuilderBuild", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(builder, [0d]);
        Assert.Single(w.Sector.BuildingEffects);
        int stations = w.Sector.Stations.Count;
        typeof(Sector).GetMethod("ResolveBuildingEffects", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(w.Sector, [BuildingEffect.BuildDuration]);
        Assert.Equal(stations + 1, w.Sector.Stations.Count);
        Assert.DoesNotContain(builder, w.Sector.Models);
        Assert.DoesNotContain(rock, w.Sector.Mineable);
    }

    [SkippableFact]
    public void H26_Pilot_and_pod_suicide_fail_and_child_suicide_works()
    {
        using var w = new World();
        w.Buyer.StationId = null;
        w.Send(new Suicide());
        Assert.Equal(0, w.Kills);
        w.Buyer.ChangeHull(w.Mission.Core.HullTypes[w.Mission.Sides[0].Civilization.Lifepod]);
        w.Send(new Suicide());
        Assert.Equal(0, w.Kills);
        w.Buyer.ParentShip = w.Mate;
        w.Send(new Suicide());
        Assert.Equal(1, w.Kills);
        w.Buyer.ParentShip = null;
        w.Server.Config.LiveSettings.AdminCheats = new AdminCheatsConfig { Enabled = true, AllowNames = ["seed-1"] };
        w.Send(new Suicide());
        Assert.Equal(2, w.Kills);
    }

    [SkippableFact]
    public void H30_Unplaced_ack_is_consumed_without_mutation_and_live_ack_works_once()
    {
        using var w = new World();
        var gun = w.Gun();
        w.Buyer.PendingTreasureOffer = (gun.PartId, 1);
        w.Buyer.Sector = null;
        var live = w.Server.Ships.Single(l => l.Ship == w.Buyer);
        w.Call("HandleTreasureAck", w.Conn, live, new TreasureAck { MountId = 0 });
        Assert.Null(w.Buyer.PendingTreasureOffer);
        Assert.Empty(w.Buyer.Parts);
        w.Buyer.Sector = live.Sector;
        w.Buyer.StationId = null;
        w.Buyer.PendingTreasureOffer = (gun.PartId, 1);
        w.Send(new TreasureAck { MountId = 0 });
        w.Send(new TreasureAck { MountId = 0 });
        Assert.Single(w.Buyer.Parts);
        Assert.Null(w.Buyer.PendingTreasureOffer);
    }

    [SkippableFact]
    public void H30_Send_failure_after_accept_cannot_replay_the_offer()
    {
        using var w = new World();
        w.Buyer.StationId = null;
        w.Buyer.PendingTreasureOffer = (w.Gun().PartId, 1);
        w.Server._testSendCapture = (_, _) => throw new IOException("injected send failure");
        w.Send(new TreasureAck { MountId = 0 }); // dispatch catches the send failure
        Assert.Null(w.Buyer.PendingTreasureOffer);
        Assert.Single(w.Buyer.Parts);
        w.Server._testSendCapture = null;
        w.Send(new TreasureAck { MountId = 0 });
        Assert.Single(w.Buyer.Parts);
    }
}
