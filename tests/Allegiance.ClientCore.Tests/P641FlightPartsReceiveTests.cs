using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.ClientCore.Tests;

public class P641FlightPartsReceiveTests
{
    private static DataPartTypeIGC? Resolve(short id) => new DataPartTypeIGC(
        0, 0, "", "", "part", "", 0, [], [], 0, 0, id, -1, 1, "",
        id == 20 ? new DataPartTypeIGC.Afterburner(0, 0, 0, 0, 0, 0) : new DataPartTypeIGC.Weapon(0, 0, 0, 0, 0, 0, 0, 0, 0));
    private static void Feed(ConnectionSession session, IFedMessage message) =>
        ((ITransportSite)session).OnReceive(ConnectionId.Server, FedPacket.Pack([message.Encode()]));
    private static ConnectionSession Seat()
    {
        var session = new ConnectionSession();
        session.World.PartTypeResolver = Resolve;
        session.World.Apply(new SetCluster { SectorId = 1, Ship = P640CommandingReceiveTests.Pose(1) });
        session.World.Apply(new BuyLoadoutAck { HullId = 1, Parts = [new(10, 3, 0, 255), new(20, 1, 0, 255), new(11, 4, 255, 255)] });
        return session;
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(-1, 0)]
    public void Swap_echo_updates_exact_equipment_without_touching_pose_or_cookie(int from, int to)
    {
        using var session = Seat();
        var ship = session.World.Ships[1];
        var pose = ship.Latest;
        byte cookie = ship.Cookie;
        Feed(session, new SwapPart { ShipID = 1, EtOld = EquipmentType.Weapon, MountOld = (sbyte)from, MountNew = (sbyte)to });
        Assert.DoesNotContain(SwapPart.Fmid, session.UnknownFmids);
        Assert.Equal(11, ship.Loadout!.Single(p => p.PartId != 20).PartId);
        Assert.Equal(10, ship.Cargo!.Single().PartId);
        Assert.Equal(0, ship.Cargo!.Single().FractionMounted);
        Assert.Equal(0, ship.Loadout!.Single(p => p.PartId == 11).FractionMounted);
        Assert.Equal(255, ship.Loadout!.Single(p => p.PartId == 20).FractionMounted);
        Assert.Same(pose, ship.Latest);
        Assert.Equal(cookie, ship.Cookie);
        Assert.False(ship.HasPendingReset);
    }

    [Fact]
    public void Unmount_mount_and_drop_follow_server_echo_without_double_swapping()
    {
        using var session = Seat();
        Feed(session, new SwapPart { ShipID = 1, EtOld = EquipmentType.Afterburner, MountOld = 0, MountNew = -2 });
        Assert.DoesNotContain(session.World.Ships[1].Loadout!, p => p.PartId == 20);
        Feed(session, new SwapPart { ShipID = 1, EtOld = EquipmentType.Afterburner, MountOld = -2, MountNew = 0 });
        Assert.Contains(session.World.Ships[1].Loadout!, p => p.PartId == 20);
        Feed(session, new DropPart { ShipID = 1, Et = EquipmentType.Afterburner, Mount = 0 });
        Assert.DoesNotContain(session.World.Ships[1].Loadout!, p => p.PartId == 20);
        Assert.Contains(session.World.Ships[1].Loadout!, p => p.PartId == 10); // same mount, different equipment
        Feed(session, new DropPart { ShipID = 1, Mount = 255 });
        Assert.Empty(session.World.Ships[1].Cargo!);
        Feed(session, new DropPart { ShipID = 99 });
        Feed(session, new SwapPart { ShipID = 99 });
        Assert.DoesNotContain(DropPart.Fmid, session.UnknownFmids);
    }

    [Fact]
    public void Remote_exports_split_cargo_and_same_mount_is_a_noop()
    {
        using var session = Seat();
        Feed(session, new ExportShip { ShipId = 2, Parts = [new(10, 3, 0), new(11, 2, 255)] });
        var remote = session.World.Ships[2];
        Assert.Single(remote.Loadout!);
        Assert.Single(remote.Cargo!);
        Feed(session, new SwapPart { ShipID = 2, EtOld = EquipmentType.Weapon, MountOld = 0, MountNew = 0 });
        Assert.Equal(ExpandedPartData.FullyMounted, remote.Loadout![0].FractionMounted);
        Feed(session, new SwapPart { ShipID = 2, EtOld = EquipmentType.Weapon, MountOld = -1, MountNew = 0 });
        Assert.Equal(11, remote.Loadout![0].PartId);
        Assert.Equal(10, remote.Cargo![0].PartId);
    }
}
