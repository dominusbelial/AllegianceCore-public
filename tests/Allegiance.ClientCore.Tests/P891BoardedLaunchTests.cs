using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

public sealed class P891BoardedLaunchTests
{
    [Fact]
    public void Parent_set_cluster_launches_child_without_transferring_ownership_or_cookie()
    {
        var world = new ClientWorld();
        ServerShipUpdate Pose(short id) => ServerShipUpdate.FromValues(id, 0, ControlData.Neutral,
            10, new Vector3(50, 60, 70), Orientation.Identity, new Vector3(30, 0, 0), [0, 0, 0], 1, 1, 1, 1, 1, 0, 1);
        world.Apply(new SetCluster { SectorId = 1, Ship = Pose(901), Cookie = 17 });
        world.Apply(new ExportShip { ShipId = 900, HullId = 792, SideId = 0 });
        world.Apply(new Allegiance.Core.Wire.LoadoutChange { SidShip = 900, HullId = 792, Parts = [],
            Passengers = [new PassengerData(901, 2)] });
        world.Apply(new ShipStatusMessage { ShipId = 901, ParentId = 900, StationId = 5,
            SectorId = 1, State = ShipStatusMessage.StateTurret });
        world.Apply(new ShipStatusMessage { ShipId = 900, ParentId = -1, StationId = 5,
            SectorId = 1, State = ShipStatusMessage.StateDocked });
        Assert.Null(world.Ships[900].Latest);
        // Native fsship.cpp:918-929 copies the parent's SET_CLUSTER to its
        // passengers; appmsg.cpp:1010,1044-1049 applies it to the source ship
        // and clears the child's station. Cookie changes only for the pilot.
        world.Apply(new SetCluster { SectorId = 2, Ship = Pose(900), Cookie = 99 });
        Assert.Equal((short)901, world.MyShipId);
        Assert.True(world.Ships[901].IsMine);
        Assert.False(world.Ships[900].IsMine);
        Assert.Equal((short)-1, world.Ships[901].StationId);
        Assert.Equal((short)2, world.Ships[901].SectorId);
        Assert.Equal((short)900, world.Ships[901].ParentId);
        Assert.Equal((short)2, world.Ships[901].BoardTurretId);
        Assert.Equal((byte)17, world.Ships[901].Cookie);
        Assert.NotNull(world.Ships[900].Latest);
        Assert.Equal(new Vector3(30, 0, 0), world.Ships[900].Latest!.Velocity);
    }
}
