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

/// <summary>
/// P1017 — the server half of the loadout buy, against the real
/// <c>BUY_LOADOUT</c> handler, a real core and map and an unbound transport
/// (the P653 fixture shape): passengers after a buy
/// (<c>FedSrv/FedSrv.CPP:6211-6245</c>, <c>Igc/shipIGC.cpp:1379-1399</c>), every
/// bought part armed (<c>Igc/shipIGC.h:1039-1040</c>, <c>:1051-1052</c>), and
/// free spawn hardware worth nothing on either side of the price.
/// </summary>
[Trait("Requires", "Artwork")]
public sealed class P1017LoadoutBuyServerTests
{
    private sealed class World : IDisposable
    {
        public readonly Srv Server = new();
        public readonly ServerHost Host = new();
        public readonly Mission Mission;
        public readonly Sector Sector;
        public readonly DataStationIGC Station;
        public readonly Ship Buyer;
        public readonly ConnectionId Conn = new(1);
        public readonly List<byte[]> Output = [];
        public readonly DataHullTypeIGC Wide;     // 2 fixed + 4 turrets: mounts 2..5
        public readonly DataHullTypeIGC Narrow;   // 1 fixed + 2 turrets: mounts 1..2
        public readonly DataHullTypeIGC Plain;    // 1 fixed, no turrets
        public PlayerWallet Wallet => Server.Ships.Single(l => l.Ship == Buyer).Wallet;
        private readonly List<(ConnectionId, Ship, Sector)> _peers = [];

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
            Sector = Mission.Sectors.First(s => s.Stations.Any(st => st.SideId == 0));
            Station = Sector.Stations.First(st => st.SideId == 0);

            // Three controlled hull records cut from one real turret hull: free,
            // buyable anywhere, no successor. Only the mount counts differ.
            var hulls = (Dictionary<short, DataHullTypeIGC>)core.HullTypes;
            var real = core.HullTypes.Values.First(h => h.GroupId >= 0 && h.MaxFixedWeapons == 2 && h.MaxWeapons == 6);
            DataHullTypeIGC Cut(short id, byte fixedWeapons, byte weapons) => hulls[id] = real with
            {
                HullId = id, Price = 500, TtbmRequired = new byte[50], SuccessorHullId = -1,
                MaxFixedWeapons = fixedWeapons, MaxWeapons = weapons, Hardpoints = real.Hardpoints[..weapons],
            };
            Wide = Cut(real.HullId, 2, 6);
            Narrow = Cut(20001, 1, 3);
            Plain = Cut(20002, 1, 1);

            Buyer = new Ship(100, Wide, new Vector3(100, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 0, StationId = Station.StationId };
            Mission.PlaceShip(Buyer, Sector);
            _peers.Add((Conn, Buyer, Sector));
            Server.TestSeedPeers(_peers);
            Server._testSendCapture = (conn, packet) => { if (conn == Conn) Output.AddRange(FedPacket.Unpack(packet).Select(m => m.ToArray())); };
            Host.LoadoutChange = (ship, part, change) => Call("BroadcastLoadoutChange", ship, part, change);
        }

        private void Field(string name, object value) => typeof(Srv).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Server, value);
        public object? Call(string name, params object?[] args)
        {
            try { return typeof(Srv).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Server, args); }
            catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
        }

        public void Buy(short hullId, params ExpandedPartData[] parts) =>
            Call("ProcessDatagram", Conn, FedPacket.Pack([new BuyLoadoutRequest { HullId = hullId, Parts = parts }.Encode()]));

        public BuyLoadoutAck LastAck => BuyLoadoutAck.Decode(Output.Last(m => FedMessageHeader.Read(m).Fmid == BuyLoadoutAck.Fmid));

        /// <summary>A real weapon record that mounts anywhere, with a price.</summary>
        public DataPartTypeIGC Gun(int price = 25)
        {
            var gun = Mission.Core.PartTypes.Values.First(p => p.EquipmentType == EquipmentType.Weapon)
                with { Price = price, PartMask = 0, TtbmRequired = new byte[50], SuccessorPartId = -1 };
            ((Dictionary<short, DataPartTypeIGC>)Mission.Core.PartTypes)[gun.PartId] = gun;
            return gun;
        }

        /// <summary>A docked teammate already aboard the buyer's ship (the state
        /// BOARD_SHIP leaves, <c>FedSrv.CPP:2445-2446</c>).</summary>
        public Ship Passenger(short id, short? turret)
        {
            var pod = Mission.Core.HullTypes[Mission.Sides[0].Civilization.Lifepod];
            var ship = new Ship(id, pod, new Vector3(200, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 0, StationId = Station.StationId };
            Mission.PlaceShip(ship, Sector);
            _peers.Add((new ConnectionId((uint)id), ship, Sector));
            int money = Wallet.Money;                  // re-seeding the peers makes new wallets
            Server.TestSeedPeers(_peers);
            Wallet.Money = money;
            ship.ParentShip = Buyer;
            ship.TurretId = turret;
            Buyer.AddChildShip(ship);
            if (turret is { } mount) Buyer.Weapons.FirstOrDefault(w => w.MountId == mount)?.SetGunner(ship);
            return ship;
        }

        public void Dispose() => Host.Transport!.Shutdown();
    }

    private static ExpandedPartData Part(DataPartTypeIGC type, int mount, byte fraction = ExpandedPartData.FullyMounted) =>
        new(type.PartId, 1, unchecked((byte)mount), fraction);

    private static void AssertLeft(World w, Ship passenger)
    {
        Assert.Null(passenger.ParentShip);
        Assert.Null(passenger.TurretId);
        Assert.DoesNotContain(passenger, w.Buyer.ChildShips);
        Assert.True(passenger.HullType.HasCapability(DataHullTypeIGC.HabmLifepod));   // FedSrv.CPP:2301-2302
        Assert.Equal(w.Station.StationId, passenger.StationId);
        Assert.DoesNotContain(w.Buyer.Weapons, weapon => weapon.Gunner == passenger);
    }

    // ── passengers ───────────────────────────────────────────────────────────

    [SkippableFact]
    public void Hull_change_reseats_gunners_by_turret_number_and_boots_the_rest()
    {
        using var w = new World();
        var gun = w.Gun();
        w.Wallet.Money = 10_000;
        w.Buy(w.Wide.HullId, Part(gun, 2), Part(gun, 3), Part(gun, 5));
        var first = w.Passenger(201, 2);      // turret 0 of the wide hull
        var second = w.Passenger(202, 3);     // turret 1
        var last = w.Passenger(203, 5);       // turret 3
        var watcher = w.Passenger(204, null);

        // Narrow hull: 1 fixed gun, turrets at mounts 1 and 2. Only mount 1 gets a gun.
        w.Buy(w.Narrow.HullId, Part(gun, 0), Part(gun, 1));

        Assert.Equal(w.Narrow.HullId, w.Buyer.HullType.HullId);
        // Turret 0 -> mount 1, turret 1 -> mount 2 (shipIGC.cpp:1386-1397).
        Assert.Same(w.Buyer, first.ParentShip);
        Assert.Equal((short)1, first.TurretId);
        Assert.Same(first, w.Buyer.Weapons.Single(weapon => weapon.MountId == 1).Gunner);
        // A gunner that still has a turret stays, gun or no gun: only observers
        // are sent away on a hull change (FedSrv.CPP:6214-6224).
        Assert.Same(w.Buyer, second.ParentShip);
        Assert.Equal((short)2, second.TurretId);
        // Turret 3 does not exist on the narrow hull: observer, then out.
        AssertLeft(w, last);
        AssertLeft(w, watcher);
        // The fixed gun at mount 0 is nobody's.
        Assert.Null(w.Buyer.Weapons.Single(weapon => weapon.MountId == 0).Gunner);
        Assert.Equal(2, w.Buyer.ChildShips.Count);
    }

    [SkippableFact]
    public void Hull_change_to_a_hull_without_turrets_boots_everybody()
    {
        using var w = new World();
        var gun = w.Gun();
        w.Wallet.Money = 10_000;
        w.Buy(w.Wide.HullId, Part(gun, 2));
        var gunner = w.Passenger(201, 2);
        var watcher = w.Passenger(202, null);

        w.Buy(w.Plain.HullId, Part(gun, 0));

        AssertLeft(w, gunner);
        AssertLeft(w, watcher);
        Assert.Empty(w.Buyer.ChildShips);
        Assert.Null(w.Buyer.Weapons.Single().Gunner);
    }

    [SkippableFact]
    public void Same_hull_buy_boots_only_passengers_whose_mount_holds_no_part()
    {
        using var w = new World();
        var gun = w.Gun();
        w.Wallet.Money = 10_000;
        w.Buy(w.Wide.HullId, Part(gun, 2), Part(gun, 3));
        var keeps = w.Passenger(201, 2);
        var loses = w.Passenger(202, 3);
        var watcher = w.Passenger(203, null);

        // Same hull, the gun at mount 3 sold, a part put in the first cargo slot.
        w.Buy(w.Wide.HullId, Part(gun, 2), Part(gun, -1));

        Assert.Same(w.Buyer, keeps.ParentShip);
        Assert.Same(keeps, w.Buyer.Weapons.Single(weapon => weapon.MountId == 2).Gunner);
        AssertLeft(w, loses);                                            // FedSrv.CPP:6225-6245
        // Native's test is "a part whose mount id equals the turret id", and an
        // observer's turret id is NA = -1 = the first cargo mount.
        Assert.NotNull(w.Buyer.CargoHold[0]);
        Assert.Same(w.Buyer, watcher.ParentShip);

        // With that cargo slot empty the observer leaves as well.
        w.Buy(w.Wide.HullId, Part(gun, 2));
        Assert.Same(w.Buyer, keeps.ParentShip);
        AssertLeft(w, watcher);
    }

    [SkippableFact]
    public void Same_hull_buy_on_a_hull_without_turrets_leaves_observers_alone()
    {
        using var w = new World();
        var gun = w.Gun();
        w.Wallet.Money = 10_000;
        w.Buy(w.Plain.HullId, Part(gun, 0));
        var watcher = w.Passenger(201, null);

        w.Buy(w.Plain.HullId);                                           // FedSrv.CPP:6225: no turrets, no check

        Assert.Same(w.Buyer, watcher.ParentShip);
    }

    // ── arming ───────────────────────────────────────────────────────────────

    [SkippableTheory]
    [InlineData((byte)0)]
    [InlineData((byte)17)]
    [InlineData((byte)255)]
    public void Every_bought_part_is_armed_whatever_fraction_the_client_sends(byte fraction)
    {
        using var w = new World();
        var gun = w.Gun();
        w.Wallet.Money = 10_000;

        w.Buy(w.Wide.HullId, Part(gun, 0, fraction), Part(gun, 2, fraction), Part(gun, -1, fraction));

        Assert.Equal(2, w.Buyer.Parts.Count);
        Assert.All(w.Buyer.Parts, p => Assert.Equal(1f, p.MountedFraction));     // Arm(), weaponIGC.h:149-153
        Assert.Equal(1f, w.Buyer.CargoHold[0]!.MountedFraction);
        var ack = w.LastAck;
        Assert.True(ack.FBoughtEverything);
        Assert.Equal(3, ack.Parts.Count);
        Assert.All(ack.Parts, p => Assert.Equal(ExpandedPartData.FullyMounted, p.FractionMounted));
        Assert.Equal(10_000 - 3 * 25, w.Wallet.Money);
    }

    // ── free spawn hardware ──────────────────────────────────────────────────

    [SkippableFact]
    public void Free_spawn_hardware_costs_nothing_to_keep_and_refunds_nothing()
    {
        using var w = new World();
        var gun = w.Gun();
        w.Buyer.MountPart(gun, 1, 0, ExpandedPartData.FullyMounted);
        w.Buyer.MountPart(gun, 1, 1, ExpandedPartData.FullyMounted);
        w.Buyer.MarkSpawnLoadoutFree();
        w.Wallet.Money = 0;

        // Keeping the kit, penniless: granted, nothing charged, still free.
        w.Buy(w.Wide.HullId, Part(gun, 0), Part(gun, 1));
        Assert.True(w.LastAck.FBoughtEverything);
        Assert.Equal(0, w.Wallet.Money);
        Assert.All(w.Buyer.Parts, p => Assert.True(p.FreeSpawn));

        // A third gun is not part of the kit: it has to be paid for.
        w.Buy(w.Wide.HullId, Part(gun, 0), Part(gun, 1), Part(gun, 2));
        Assert.False(w.LastAck.FBoughtEverything);                       // refused: 25 > 0
        Assert.Equal(2, w.Buyer.Parts.Count);
        w.Wallet.Money = 100;
        w.Buy(w.Wide.HullId, Part(gun, 0), Part(gun, 1), Part(gun, 2));
        Assert.Equal(75, w.Wallet.Money);
        Assert.Equal(2, w.Buyer.Parts.Count(p => p.FreeSpawn));
        Assert.Equal(1, w.Buyer.Parts.Count(p => !p.FreeSpawn));

        // Selling everything refunds the one gun that was bought.
        w.Buy(w.Wide.HullId);
        Assert.Equal(100, w.Wallet.Money);
        Assert.Empty(w.Buyer.Parts);

        // The kit is gone: the same guns now cost money, and refund it.
        w.Buy(w.Wide.HullId, Part(gun, 0), Part(gun, 1));
        Assert.Equal(50, w.Wallet.Money);
        Assert.All(w.Buyer.Parts, p => Assert.False(p.FreeSpawn));
        w.Buy(w.Wide.HullId);
        Assert.Equal(100, w.Wallet.Money);

        // The spawn hull itself was never paid for: trading it in refunds nothing,
        // and the new hull is paid in full.
        w.Wallet.Money = 1_000;
        w.Buy(w.Narrow.HullId);
        Assert.Equal(500, w.Wallet.Money);
        w.Buy(w.Plain.HullId);
        Assert.Equal(500, w.Wallet.Money);                               // 500 back, 500 out
    }

    [Fact]
    public void Kept_matches_each_free_part_once_by_type_and_amount()
    {
        FreeHardware.Unit[] held = [new(7, 3), new(7, 3), new(9, 1)];
        Assert.Equal([true, true, false, true],
            FreeHardware.Kept(held, [new(7, 3), new(7, 2), new(7, 1), new(9, 1)]));    // two of type 7, not three
        Assert.Equal([false, true], FreeHardware.Kept(held, [new(7, 4), new(7, 3)]));  // more units than held: paid
        Assert.Equal([false], FreeHardware.Kept(held, [new(8, 1)]));
        Assert.Equal([false], FreeHardware.Kept([], [new(7, 1)]));
        Assert.Empty(FreeHardware.Kept(held, []));
    }
}
