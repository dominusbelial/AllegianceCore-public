using System.Reflection;
using Allegiance.ClientCore.Training;
using Allegiance.Core.Transport;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;
using static Allegiance.ClientCore.MonoGame.Tests.P910TrainingIntegrationTests;
using V = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

[Collection("Training")]
public sealed class P932TrainingLoadoutTests
{
    // The shipping training core is free. Give only this mission private type
    // dictionaries, so prices exercise the native budget without altering a
    // cache shared with another test/server.
    private static StaticCore PrivateCore(Mission mission)
    {
        var clone = (StaticCore)typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(mission.Core, null)!;
        void SetCore(string property, object value) => typeof(StaticCore).GetField($"<{property}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(clone, value);
        SetCore("PartTypes", mission.Core.PartTypes.ToDictionary());
        SetCore("HullTypes", mission.Core.HullTypes.ToDictionary());
        SetCore("MissileTypes", mission.Core.MissileTypes.ToDictionary());
        typeof(Mission).GetField("<Core>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mission, clone);
        return clone;
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void Station_reapplication_uses_hull_equipment_and_cargo_value_before_clearing_cargo(int refundSource)
    {
        Require.Artwork();
        using var host = TrainingHost.Start(TrainingHost.BasicWeaponry);
        var core = PrivateCore(host.Mission);
        var types = (Dictionary<short, DataPartTypeIGC>)core.PartTypes;
        DataPartTypeIGC Priced(DataPartTypeIGC source, short id, int price) => source with
        { PartId = id, Price = price, SuccessorPartId = -1, TtbmRequired = new byte[50], PartMask = 1 };
        var weapon = Priced(types[92], 30000, refundSource == 0 ? 100 : 0);
        weapon = weapon with { Type = ((DataPartTypeIGC.Weapon)weapon.Type) with { CAmmoPerShot = 1 } };
        // Native clintlib.h:2228 assumes nanite 91 exists. Keep it present,
        // but make its mask incompatible with this oracle's weapon socket.
        types[91] = types[91] with { PartMask = 2, SuccessorPartId = -1 };
        var shield = Priced(types.Values.First(p => p.EquipmentType == EquipmentType.Shield), 30001, 150);
        var booster = Priced(types.Values.First(p => p.EquipmentType == EquipmentType.Afterburner), 30002, 100);
        Assert.True(core.TryBuildExpendableLauncher(150, out var launcher));
        launcher = Priced(launcher, 30003, refundSource == 1 ? 20 : 0);
        var missiles = (Dictionary<short, DataMissileTypeIGC>)core.MissileTypes;
        short expendableId = ((DataPartTypeIGC.MissileLauncher)launcher.Type).ExpendableTypeId;
        missiles[expendableId] = missiles[expendableId] with { Price = launcher.Price };
        var oldCargo = Priced(weapon, 30004, refundSource == 2 ? 100 : 0);
        var pack = Priced(types[24], 24, 1000); // packs refund zero, despite this price
        foreach (var type in new[] { weapon, shield, booster, launcher, oldCargo, pack }) types[type.PartId] = type;
        var socket = host.PlayerShip.HullType.Hardpoints[0] with { PartMask = 1, Fixed = true };
        var hull = host.PlayerShip.HullType with
        {
            Price = 100, PreferredPartsTypes = [shield.PartId, booster.PartId, -1],
            PmEquipment = Enumerable.Repeat(short.MaxValue, 8).ToArray(),
            CapacityMagazine = short.MaxValue,
            Hardpoints = [socket], MaxWeapons = 1, MaxFixedWeapons = 1,
        };
        ((Dictionary<short, DataHullTypeIGC>)core.HullTypes)[hull.HullId] = hull;
        var ship = new Ship(4900, hull, new V(10000, 0, 0), V.Zero, Orientation.Identity) { SideId = 0 };
        host.Mission.PlaceShip(ship, host.Mission.Sectors.First(s => s.Id == host.StartSectorId));
        var retained = ship.MountPart(weapon, 1, 0, ExpandedPartData.FullyMounted, freeSpawn: true);
        ship.Ammo = 0; ship.Energy = 0; ship.SetFuel(0); ship.SetShieldFraction(0);
        ship.MountPart(launcher, 5, unchecked((byte)-1), ExpandedPartData.FullyMounted, freeSpawn: true);
        ship.MountPart(oldCargo, 1, unchecked((byte)-2), ExpandedPartData.FullyMounted);
        ship.MountPart(pack, 1, unchecked((byte)-3), ExpandedPartData.FullyMounted);
        Assert.Equal(0, ship.Ammo); // no auto-reload may consume the priced pack
        Assert.Equal(1000, ship.CargoHold[2]!.Type.Price);
        Assert.False(hull.CanMountPart(types[91], 0));

        // Independent priced oracle: hull 100 + exactly one 100-credit refund
        // (equipped item / 5 * 20 launcher rounds / cargo item). Packs add 0.
        // Shield 150 fits, leaving 50; booster 100 cannot fit. Cargo is cleared
        // and rebuilt even though the 1000-credit first ammo attempt fails.
        typeof(TrainingHost).GetMethod("EquipDefaultTrainingLoadout", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [ship]);
        Assert.Same(retained, Assert.Single(ship.Parts, p => p.EquipmentType == EquipmentType.Weapon));
        Assert.Equal(shield.PartId, Assert.Single(ship.Parts, p => p.EquipmentType == EquipmentType.Shield).PartId);
        Assert.DoesNotContain(ship.Parts, p => p.EquipmentType == EquipmentType.Afterburner);
        Assert.Null(ship.CargoHold[4]); // -5 paid ammo failed; native still advances the slot
        Assert.All(ship.CargoHold.Take(4), p => Assert.Equal((short)24, p!.PartId));
        Assert.DoesNotContain(ship.CargoHold, p => p?.PartId is 30003 or 30004);
        Assert.Equal(1, ship.ShieldFraction);
        Assert.Equal(hull.MaxAmmo, ship.Ammo);
        Assert.Equal(ship.EffectiveMaxEnergy(), ship.Energy);
        Assert.Equal(hull.MaxFuel, ship.Fuel);
    }

    private static void SeedDock(ConnectionSession connection, TrainingHost host)
    {
        var ship = host.PlayerShip;
        ship.StationId = host.HomeStationId;
        connection.World.Apply(new ExportShip { ShipId = ship.Id, SideId = 0, HullId = ship.HullType.HullId });
        connection.World.Apply(new SetCluster { SectorId = host.StartSectorId, Ship = ServerShipUpdate.FromModel(ship.Id, ship, 0), Cookie = 43 });
        connection.World.Apply(new DockedNotify { StationId = host.HomeStationId });
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(HudStyle.Classic, false)] [InlineData(HudStyle.Modern, false)]
    [InlineData(HudStyle.Classic, true)] [InlineData(HudStyle.Modern, true)]
    public void Hangar_training_purchase_installs_notifies_then_launches_before_return(HudStyle style, bool launch)
    {
        Require.Artwork();
        using var host = TrainingHost.Start(TrainingHost.BasicWeaponry);
        using var connection = new ConnectionSession();
        SeedDock(connection, host);
        var game = Headless(connection, style);
        Set(game, "_embeddedHost", host);
        var chosen = new ExpandedPartData[] { new(92, 1, 0, 0), new(150, 3, unchecked((byte)-1), 0) };
        host.PlayerShip.ClearLoadout(); // no old fuel/ammo pack may auto-reload while depleting
        host.PlayerShip.Ammo = 0;
        host.PlayerShip.Energy = 0;
        host.PlayerShip.SetFuel(0);
        connection.World.Apply(new ExportShip { ShipId = 4902, HullId = 800, SideId = 1 });
        var otherPose = ServerShipUpdate.FromValues(4902, 0, default, 0, new V(1000, 0, 0), Orientation.Identity,
            V.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1);
        connection.World.Apply(new SetCluster { SectorId = host.StartSectorId, Ship = otherPose });
        var remote = connection.World.Ships[4902];
        var generation = connection.World.SectorGeneration;
        var station = host.Mission.GetStation(host.HomeStationId)!;
        var techs = host.Mission.Sides[0].DevelopmentTechs;
        var expectedWeapon = SuccessorUpgrade.PartSuccessor(host.Mission.Core, host.Mission.Core.PartTypes[92], station.Type.TtbmLocal, techs);
        Assert.True(host.Mission.Core.TryBuildExpendableLauncher(150, out var magazine));
        var expectedMagazine = SuccessorUpgrade.PartSuccessor(host.Mission.Core, magazine, station.Type.TtbmLocal, techs);
        int completed = 0;
        connection.PurchaseCompleted += success =>
        {
            Assert.True(success);
            Assert.True(Monitor.IsEntered(connection.Gate));
            Assert.Equal(host.HomeStationId, host.PlayerShip.StationId);
            Assert.Equal(0, host.PlayerShip.Ammo);
            Assert.Equal(0, host.PlayerShip.Energy);
            Assert.Equal(0, host.PlayerShip.Fuel);
            Assert.Equal(host.HomeStationId, connection.World.Ships[host.PlayerShip.Id].StationId);
            Assert.Equal(expectedWeapon.PartId, Assert.Single(host.PlayerShip.Parts).PartId);
            Assert.Equal(expectedMagazine.PartId, host.PlayerShip.CargoHold[0]!.PartId);
            Assert.Equal((short)3, host.PlayerShip.CargoHold[0]!.Amount);
            var mirror = connection.World.Ships[host.PlayerShip.Id];
            Assert.Equal(expectedWeapon.PartId, Assert.Single(mirror.Loadout!).PartId);
            Assert.Equal(expectedMagazine.PartId, Assert.Single(mirror.Cargo!).PartId);
            completed++;
        };
        Call(game, "PurchaseHangarLoadout", host.PlayerShip.HullType.HullId, chosen, launch);
        Assert.Equal(1, completed);
        Assert.Empty(connection.BuyLoadoutAcks);
        Assert.All(host.PlayerShip.Parts, p => Assert.Equal(ExpandedPartData.FullyMounted, p.FractionMounted));
        if (launch)
        {
            Assert.Null(host.PlayerShip.StationId);
            var mirror = connection.World.Ships[host.PlayerShip.Id];
            Assert.Equal(-1, mirror.StationId);
            Assert.Equal((byte)43, mirror.Cookie);
            Assert.True(mirror.HasPendingReset);
            Assert.Equal(generation, connection.World.SectorGeneration);
            Assert.Same(otherPose, remote.Latest);
            Assert.Equal(host.PlayerShip.HullType.MaxAmmo, host.PlayerShip.Ammo);
            Assert.Equal(host.PlayerShip.EffectiveMaxEnergy(), host.PlayerShip.Energy);
            Assert.Equal(host.PlayerShip.Position, mirror.Latest!.Position);
            Assert.True(host.PlayerShip.Velocity.Length() > 0);
            Assert.Equal(host.Mission.LastUpdate, host.PlayerShip.LastLaunchTime);
        }
        else Assert.Equal(host.HomeStationId, host.PlayerShip.StationId);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Local_purchase_reports_true_even_when_requested_hull_is_unavailable()
    {
        Require.Artwork(); using var host = TrainingHost.Start(TrainingHost.BasicWeaponry);
        using var connection = new ConnectionSession(); SeedDock(connection, host);
        var game = Headless(connection, HudStyle.Modern); Set(game, "_embeddedHost", host);
        var before = host.PlayerShip.ExportInventoryForAck().ToArray();
        int completed = 0;
        connection.PurchaseCompleted += success => { Assert.True(success); completed++; };
        Call(game, "PurchaseHangarLoadout", (short)short.MaxValue, Array.Empty<ExpandedPartData>(), false);
        Assert.Equal(1, completed);
        Assert.Equal(before, host.PlayerShip.ExportInventoryForAck());
    }

    private sealed class PurchasePeer : ITransportSite
    {
        internal readonly TaskCompletionSource<ConnectionId> Joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<byte[]> Received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void OnPeerJoined(ConnectionId id, string name) => Joined.TrySetResult(id);
        public void OnPeerLeft(ConnectionId id) { }
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId id, ReadOnlySpan<byte> bytes) => Received.TrySetResult(bytes.ToArray());
    }

    [Theory]
    [InlineData(HudStyle.Classic, false)] [InlineData(HudStyle.Modern, false)]
    [InlineData(HudStyle.Classic, true)] [InlineData(HudStyle.Modern, true)]
    public async Task Multiplayer_keeps_exact_buy_loadout_packet_and_waits_for_ack(HudStyle style, bool launch)
    {
        using var connection = new ConnectionSession();
        var peer = new PurchasePeer();
        var transport = new TcpTransport(peer, TransportMode.Pool);
        try
        {
            transport.Host(0);
            connection.Connect(transport.BoundPort, "p932-purchase");
            var id = await peer.Joined.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var world = connection.World;
            world.Apply(new ExportShip { ShipId = 10, SideId = 0, HullId = 414 });
            var pose = ServerShipUpdate.FromValues(10, 0, default, 0, V.Zero, Orientation.Identity,
                V.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1);
            world.Apply(new SetCluster { SectorId = 1010, Ship = pose });
            world.Apply(new DockedNotify { StationId = 1010 });
            // The handler runs on the TCP read thread: record only, assert after it returns.
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            short seenHull = 0, seenStation = 0;
            connection.PurchaseCompleted += success =>
            {
                seenHull = world.Ships[10].HullId;
                seenStation = world.Ships[10].StationId;
                completed.TrySetResult(success);
            };
            var chosen = new ExpandedPartData[] { new(92, 1, 0, ExpandedPartData.FullyMounted) };
            var game = Headless(connection, style);
            Call(game, "PurchaseHangarLoadout", (short)415, chosen, launch);
            var packet = await peer.Received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(FedPacket.Pack(new BuyLoadoutRequest { HullId = 415, FLaunch = launch, Parts = chosen }.Encode()), packet);
            Assert.False(completed.Task.IsCompleted);
            Assert.Empty(connection.BuyLoadoutAcks);
            Assert.Equal((short)414, world.Ships[10].HullId);
            // Pin the ordinary ACK event payload too: unlike local training it
            // reports the server's false value, after applying the actual parts.
            transport.Send(id, FedPacket.Pack(new BuyLoadoutAck
                { HullId = 415, Parts = chosen, FBoughtEverything = false, FLaunch = launch }.Encode()), true);
            Assert.False(await completed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal((short)415, seenHull);
            Assert.Equal((short)1010, seenStation);
            Assert.Single(connection.BuyLoadoutAcks);
            Assert.Equal(chosen, world.Ships[10].Loadout);
            Assert.Equal((short)1010, world.Ships[10].StationId); // even launch waits for SET_CLUSTER
        }
        finally { connection.Dispose(); transport.Shutdown(); }
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Mission3_station_default_then_script_removal_leaves_no_spare_for_goal09()
    {
        Require.Artwork(); using var host = TrainingHost.Start(TrainingHost.BasicWeaponry);
        var ship = new Ship(4901, host.Mission.Core.HullTypes[414], new V(10000, 0, 0), V.Zero, Orientation.Identity) { SideId = 0 };
        host.Mission.PlaceShip(ship, host.Mission.Sectors.First(s => s.Id == 1010));
        // LoadUniverse -> DefaultLoadout: trainingmission.cpp:785-788,820-821;
        // clintlib.h:2233-2245 fills the five slots before the missile filler.
        typeof(TrainingHost).GetMethod("EquipDefaultTrainingLoadout", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [ship]);
        Assert.Contains(ship.Parts, p => p.EquipmentType == EquipmentType.Magazine);
        Assert.Equal(new short[] { 14, 25, 25, 24, 24 }, ship.CargoHold.Select(p => p!.PartId));
        // mission3.cpp:161-163,180-182 TERMINATES the mounted magazine; it does
        // not move it into cargo. No inventory addition occurs before :919.
        typeof(TrainingHost).GetMethod("ApplyMissionShipSetup", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [TrainingHost.BasicWeaponry, ship]);
        Assert.DoesNotContain(ship.Parts, p => p.EquipmentType == EquipmentType.Magazine);
        Assert.DoesNotContain(ship.CargoHold, p => p?.EquipmentType == EquipmentType.Magazine);
        var before = host.PlayerShip.ExportInventoryForAck().ToArray();
        new SwapWeaponAction(new TrainingHostSite(host), (int)EquipmentType.Magazine, 0).Execute();
        Assert.Equal(before, host.PlayerShip.ExportInventoryForAck());
        Assert.DoesNotContain(host.PlayerShip.Parts, p => p.EquipmentType == EquipmentType.Magazine);
    }

    [SkippableTheory, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Connected_training_launch_remains_consistent_after_loopback_ticks_resume(HudStyle style)
    {
        Require.Artwork(); using var host = TrainingHost.Start(TrainingHost.BasicWeaponry);
        using var connection = new ConnectionSession();
        host.StartBackgroundTicker();
        connection.Connect(host.Port, "p932-local");
        connection.SendLogonRequest(new LogonRequest
        {
            CharacterName = "p932-local", ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL",
            FedsrvVer = LogonRequest.MsgVer, CrcFileList = -1,
            SteamAuthTicket = new byte[16], DrmHash = "p932",
        });
        Assert.True(connection.WaitForAck(15000).FValidated);
        connection.SendPositionReq(0);
        Assert.True(SpinWait.SpinUntil(() =>
        {
            lock (connection.Gate) return connection.World.MyShipId == host.PlayerShipId
                && connection.World.Ships[(short)host.PlayerShipId].Latest is not null;
        }, 15000));
        host.Paused = true;
        host.Mission.Site.DockWithStationEvent(host.PlayerShip, new DockStation(host.Mission.GetStation(host.HomeStationId)!.Data));
        Assert.True(SpinWait.SpinUntil(() =>
        {
            lock (connection.Gate) return connection.World.Ships[(short)host.PlayerShipId].StationId == host.HomeStationId;
        }, 3000));
        // The port's server also sends a BUY_LOADOUT_ACK on docking (native only answers a
        // purchase); let it land before subscribing, or it fires after the local launch.
        Assert.True(SpinWait.SpinUntil(() => connection.BuyLoadoutAcks.Count > 0, 3000));
        connection.ResetBuyLoadoutAcks();
        var game = Headless(connection, style); Set(game, "_embeddedHost", host);
        ExpandedPartData[] chosen = [new(94, 1, 0, 0), new(150, 3, unchecked((byte)-1), 0)];
        int completions = 0;
        bool allSucceeded = true, allDocked = true;
        connection.PurchaseCompleted += success =>
        {
            allSucceeded &= success; allDocked &= host.PlayerShip.StationId is not null;
            completions++;
        };
        uint tick;
        byte cookie;
        lock (connection.Gate)
        {
            cookie = connection.World.Ships[(short)host.PlayerShipId].Cookie;
            Call(game, "PurchaseHangarLoadout", host.PlayerShip.HullType.HullId, chosen, true);
            Assert.Equal(1, completions);
            Assert.True(allSucceeded); Assert.True(allDocked);
            Assert.Null(host.PlayerShip.StationId);
            var own = connection.World.Ships[(short)host.PlayerShipId];
            Assert.Equal(-1, own.StationId);
            Assert.Equal(cookie, own.Cookie);
            tick = own.LastUpdateTick;
        }
        host.Paused = false;
        Assert.True(SpinWait.SpinUntil(() =>
        {
            lock (connection.Gate) return connection.World.Ships[(short)host.PlayerShipId].LastUpdateTick > tick + 3;
        }, 3000));
        lock (connection.Gate)
        {
            var own = connection.World.Ships[(short)host.PlayerShipId];
            Assert.Equal(-1, own.StationId);
            Assert.Equal(cookie, own.Cookie);
            Assert.Equal(host.PlayerShip.HullType.HullId, own.HullId);
            Assert.Equal(host.PlayerShip.ExportInventoryForAck(), own.Loadout!.Concat(own.Cargo!).ToArray());
        }
    }
}
