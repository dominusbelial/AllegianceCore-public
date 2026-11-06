using System.Numerics;
using System.Reflection;
using Allegiance.ClientCore.Training;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;
using V = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.Tests;

[Collection("Training")]
public sealed class P934TrainingSupportTests : IDisposable
{
    public P934TrainingSupportTests() => TrainingClock.ResetForTests();
    public void Dispose() { TrainingClock.ResetForTests(); TrainingController.EndMission(); }

    private static TrainingHost Host(int lesson)
    {
        Require.Artwork();
        var host = TrainingHost.Start(lesson == 6 ? TrainingHost.PracticeArena :
            lesson == 10 ? TrainingHost.FreeFlight : TrainingHost.Nanite);
        var site = new TrainingHostSite(host);
        host.SetActiveMission(lesson == 6 ? new Mission6(site) : lesson == 10 ? new Mission10(site) : new Mission8(site));
        return host;
    }

    private static Ship Support(TrainingHost host)
    {
        var recipe = new CreateDroneAction(new TrainingHostSite(host), "Support regression", 9340, 385, 0, Ship.PilotTypeWingman);
        recipe.SetCreatedLocation(host.StartSectorId, new Vector3(0, 0, 5000));
        recipe.SetCreatedTechLevel(-2);
        recipe.Execute();
        var ship = host.Drones[9340];
        ship.WingmanBehaviour = 0x22;
        Assert.True(ship.Weapons.Single(w => w.MountId == 0).ProjectileType.Power < 0);
        return ship;
    }

    private static void Call(Ship ship, string name) => typeof(Ship)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(ship, [100d]);

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(6)] [InlineData(8)] [InlineData(10)]
    public void Targetless_nanite_wingman_does_not_retreat_and_takes_the_normal_default_order(int lesson)
    {
        using var host = Host(lesson); var support = Support(host);
        host.PlayerShip.StationId = host.HomeStationId;
        host.CreateDrone("Default enemy", 9341, 315, 1, Ship.PilotTypeMiner);
        host.PlaceDrone(9341, host.StartSectorId, new Vector3(0, 200, 5000), Vector3.UnitX, Vector3.UnitZ, null, 0);
        var enemy = host.Drones[9341];
        enemy.SideVisibility.SetSideVisibility(0, true);
        support.SetCommand(OrderChange.CmdAccepted, null, OrderChange.CidNone);
        support.SetFraction(.2f); // fresh damage with both retreat thresholds enabled
        Assert.Null(support.RepairTarget);
        Call(support, "UpdateDamageRetreat");
        Assert.False(support.IsRunningAway);
        Call(support, "ValidateNpcPlan");
        Assert.Same(enemy, support.AttackTarget);
        Assert.Null(support.PlanTarget); // no friendly restart-base Goto
        Assert.Equal(OrderChange.CidAttack, support.AcceptedShipCommandId);
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(6)] [InlineData(8)] [InlineData(10)]
    public void A_fully_healed_repair_target_keeps_the_legal_order_and_suppresses_fire(int lesson)
    {
        using var host = Host(lesson); var support = Support(host);
        var target = host.PlayerShip;
        target.Position = new V(300, 0, 5000); target.Velocity = V.Zero;
        target.SetFraction(1);
        host.IssueCommand(support.Id, TrainingMissionIds.CommandAccepted, target.Id, null, null, OrderChange.CidRepair);
        support.SetFraction(.2f);
        Call(support, "UpdateDamageRetreat"); Call(support, "ValidateNpcPlan");
        Assert.False(support.IsRunningAway);
        Assert.Same(target, support.RepairTarget);
        Assert.Equal(OrderChange.CidRepair, support.AcceptedShipCommandId);
        support.StateM |= ShipControlState.AllWeapons;
        typeof(Ship).GetMethod("UpdateWingmanRepair", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(support, [100d, .05f]);
        Assert.Equal(0, support.StateM & ShipControlState.AllWeapons);
        Assert.Same(target, support.RepairTarget);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Lost_repair_target_without_an_enemy_clears_the_order_and_does_not_go_home()
    {
        using var host = Host(8); var support = Support(host);
        host.CreateDrone("Former repair target", 9344, 315, 0, Ship.PilotTypePlayer);
        host.PlaceDrone(9344, host.StartSectorId, new Vector3(300, 0, 5000), Vector3.UnitX, Vector3.UnitZ, null, 0);
        host.IssueCommand(support.Id, TrainingMissionIds.CommandAccepted, 9344, null, null, OrderChange.CidRepair);
        support.SetFraction(.2f);
        host.DestroyDrone(9344);
        typeof(Ship).GetMethod("UpdateWingmanRepair", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(support, [100d, .05f]);
        Call(support, "UpdateDamageRetreat"); Call(support, "ValidateNpcPlan");
        Assert.False(support.IsRunningAway); Assert.False(support.HasRepairOrder);
        Assert.Null(support.PlanTarget); Assert.Null(support.GotoWaypoint);
        Assert.Null(support.AcceptedShipTarget);
        Assert.Equal(OrderChange.CidNone, support.AcceptedShipCommandId);
    }
}
