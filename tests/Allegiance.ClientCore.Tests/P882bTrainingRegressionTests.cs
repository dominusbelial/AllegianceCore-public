using System.Numerics;
using Allegiance.ClientCore.Training;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

[Collection("Training")]
public sealed class P882bTrainingRegressionTests
{
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    [Trait("Tier", "Live")]
    public void Placed_armed_drone_moves_between_sectors_once_and_termination_removes_it_everywhere()
    {
        Require.Artwork();
        using var host = TrainingHost.Start(TrainingHost.CommandView);
        int id = host.CreateDrone("Transfer scout", 4300, 412, 1, 2);
        var drone = host.Drones[id];
        Assert.NotEmpty(drone.Weapons);
        var sectors = host.Mission.Sectors;

        // CreateDroneAction.cpp:75-80 -> Igc/shipIGC.h:215-227:
        // SetCluster replaces old membership; repeating the placement adds no copy.
        foreach (var destination in new[] { sectors[1], sectors[1], sectors[2], sectors[0] })
        {
            host.PlaceDrone(id, destination.Id, new Vector3(2000, 0, 0),
                Vector3.UnitX, Vector3.UnitZ, null, TrainingMissionIds.CmdDoNothing);
            Assert.Same(destination, drone.Sector);
            Assert.Single(sectors.SelectMany(s => s.Models), m => ReferenceEquals(m, drone));
            Assert.Contains(drone, destination.Models);
        }

        drone.Weapons[0].Activate();
        host.DestroyDrone(id);
        Assert.Null(drone.Sector);
        Assert.DoesNotContain(sectors.SelectMany(s => s.Models), m => ReferenceEquals(m, drone));
        Assert.False(host.TrainingShipExists(id));
        host.StepLight(); // no armed ghost remains in any sector's update list
        Assert.Equal((short)1050, host.PlayerShip.StationId);
    }
}
