using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

public class P698StaleShipTests
{
    public static ServerShipUpdate Pose(short id) => ServerShipUpdate.FromValues(
        id, 0, ControlData.Neutral, 1, Vector3.Zero, Orientation.Identity,
        Vector3.Zero, [0f, 0f, 0f], 1, 1, 1, 1, 1, 0, 0);

    public static HeavyShipsUpdate Heavy(short id) => new()
    {
        HeavyShipEntries = [HeavyShipUpdateEntry.From(1, Vector3.Zero, id, 0,
            ControlData.Neutral, 1, Vector3.Zero, Orientation.Identity, Vector3.Zero,
            [0f, 0f, 0f], 1, 1, 0)],
    };

    private static ClientWorld World()
    {
        var w = new ClientWorld();
        w.Apply(new SetCluster { SectorId = 1061, Ship = Pose(1) });
        w.Apply(new ExportShip { ShipId = 2, SideId = 1, HullId = 3 });
        w.Apply(Heavy(2));
        Assert.True(w.Ships[2].CanDrawInSector(1061));
        return w;
    }

    [Theory]
    [InlineData(ShipDelete.SdrLeftSector)]
    [InlineData(ShipDelete.SdrDocked)]
    [InlineData(ShipDelete.SdrKilled)]
    public void Identity_loadout_and_team_pose_cannot_resurrect_retired_sector_pose(int reason)
    {
        var w = World();
        w.Apply(new ShipDelete { ShipId = 2, Sdr = reason });
        w.Apply(new SingleShipUpdate { OidTarget = 2, Ship = Pose(2) });
        w.Apply(new ExportShip { ShipId = 2, SideId = 1, HullId = 3 });
        w.Apply(new Allegiance.Core.Wire.LoadoutChange { SidShip = 2, HullId = 3 });
        Assert.False(w.Ships[2].CanDrawInSector(1061));
        Assert.NotNull(w.Ships[2].Latest); // strategic position remains available
        w.Apply(Heavy(2));
        Assert.True(w.Ships[2].CanDrawInSector(1061));
    }

    [Fact]
    public void Hidden_status_stays_hidden_despite_in_cluster_telemetry_until_spotted()
    {
        var w = World();
        var status = new ShipStatusMessage { ShipId = 2, SectorId = 1061,
            ParentId = -1, StationId = -1, Unknown = true };
        w.Apply(status);
        w.Apply(Heavy(2)); // native also transmits undetected in-cluster ships
        Assert.False(w.Ships[2].CanDrawInSector(1061));
        status.Unknown = false;
        w.Apply(status);
        w.Apply(Heavy(2));
        Assert.True(w.Ships[2].CanDrawInSector(1061));
    }

    [Theory]
    [InlineData(ShipStatusMessage.StateDocked, 1061)]
    [InlineData(ShipStatusMessage.StateFlying, 1051)]
    public void Dock_or_new_sector_status_retires_pose_without_delete(byte state, short sector)
    {
        var w = World();
        w.Apply(new ShipStatusMessage { ShipId = 2, SectorId = sector, State = state,
            StationId = state == ShipStatusMessage.StateDocked ? (short)7 : (short)-1, ParentId = -1 });
        Assert.False(w.Ships[2].CanDrawInSector(1061));
        Assert.False(w.Ships[2].CanDrawInSector(1051));
    }

    [Theory]
    [InlineData(1061)]
    [InlineData(1051)]
    public void Set_cluster_caches_all_remote_ships_until_new_sector_pose(short sector)
    {
        var w = World();
        long generation = w.SectorGeneration;
        w.Apply(new SetCluster { SectorId = sector, Ship = Pose(1) });
        Assert.True(w.SectorGeneration > generation);
        Assert.False(w.Ships[2].CanDrawInSector(1061));
        Assert.False(w.Ships[2].CanDrawInSector(sector));
        w.Apply(new ExportShip { ShipId = 2, SideId = 1, HullId = 3 });
        Assert.False(w.Ships[2].CanDrawInSector(sector));
        w.Apply(Heavy(2));
        Assert.True(w.Ships[2].CanDrawInSector(sector));
    }

    [Fact]
    public void Docked_command_preview_accepts_fresh_team_pose_but_identity_alone_cannot_restore_it()
    {
        var w = World();
        w.Apply(new ExportCluster { Cluster = new DataClusterIGC(
            1, [0, 0, 1], 0xffffffff, 0, 0, 1051, 0, 0, "preview", "", "", 0, 0, 0, true, false) });
        w.Apply(new ShipStatusMessage { ShipId = 1, SectorId = 1061,
            State = ShipStatusMessage.StateDocked, ParentId = -1, StationId = 7 });
        Assert.False(w.Ships[1].IsGone); // own docking does not hide our own ship
        w.Apply(new ViewClusterReply { ClusterId = 1051 });
        w.Apply(new ExportShip { ShipId = 2, SideId = 0 });
        w.Apply(new ShipStatusMessage { ShipId = 2, SectorId = 1051,
            State = ShipStatusMessage.StateFlying, ParentId = -1, StationId = -1 });
        Assert.False(w.Ships[2].CanDrawInSector(1051));
        w.Apply(new SingleShipUpdate { OidTarget = 2, Ship = Pose(2) });
        Assert.True(w.Ships[2].CanDrawInSector(1051));
    }

    [Fact]
    public void Set_cluster_discards_wire_effects_including_return_to_same_sector()
    {
        var w = World();
        w.Apply(new ProjectileInstance { LauncherId = 2, Lifespan = 20 });
        w.Apply(new CreateChaff());
        var missile = new FireMissile { LauncherId = 2, MissileTypeId = 802, ClusterId = 1061,
            Entries = [new MissileLaunchData(Vector3.Zero, new Vector3(0, 0, -1), Vector3.Zero, 901)] };
        w.Apply(missile);
        w.Apply(new ExplosionEvent { ClusterId = 1061, Type = ExplosionType.SmallShip });
        w.Apply(new ProjectileHullHit { ClusterId = 1061 });
        Assert.NotEmpty(w.Projectiles);
        Assert.NotEmpty(w.Chaff);
        Assert.NotEmpty(w.Missiles);
        Assert.NotEmpty(w.Explosions);
        Assert.NotEmpty(w.HullHits);
        w.Apply(new SetCluster { SectorId = 1061, Ship = Pose(1) });
        Assert.Empty(w.Projectiles);
        Assert.Empty(w.TakeNewProjectiles());
        Assert.Empty(w.Missiles);
        Assert.Empty(w.Chaff);
        Assert.Empty(w.Explosions);
        Assert.Empty(w.HullHits);
        Assert.Empty(w.TakeNewHullHits());
        // A newly created missile in the new scene cannot retarget onto the
        // previous scene's remembered chaff, even on a same-sector re-seat.
        w.Apply(missile);
        w.Apply(new MissileSpoofed { MissileId = 901 });
        Assert.Null(w.Missiles[901].SpoofTargetPosition);
    }
}
