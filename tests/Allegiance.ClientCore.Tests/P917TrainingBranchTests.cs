using System.Numerics;
using System.Reflection;
using Allegiance.ClientCore.Training;
using Allegiance.Core.Sim;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;
using static Allegiance.ClientCore.Training.TrainingMissionIds;

namespace Allegiance.ClientCore.Tests;

[Collection("Training")]
public sealed class P917TrainingBranchTests : IDisposable
{
    public void Dispose()
    {
        TrainingController.EndMission();
        TrainingControlState.Reset();
    }

    [SkippableFact, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    public void Outpost_death_leaves_enemy_defenders_for_the_native_training_end_orders()
    {
        Require.Artwork();
        using var host = TrainingHost.Start(TrainingHost.Nanite);
        int defender = host.CreateDrone("Ending regression", 9881, 385, 1, (int)Ship.PilotTypeWingman);
        var station = host.Mission.GetStation(Mission8.EnemyOutpostStationId)!;
        var damage = host.Mission.Core.Constants.DamageConstants;
        byte type = (byte)Enumerable.Range(0, damage.Length).First(i =>
            damage[i][station.Type.DefenseTypeShield] > 0 && damage[i][station.Type.DefenseTypeArmor] > 0);
        // Offline WinTrek/trekigc.cpp:4325-4362 delegates to Training, without
        // FedSrv side elimination. mission8.cpp:629-638 supplies the later idle orders.
        station.ReceiveDamage(type, 1e10f, host.Mission.LastUpdate, station.Center, station.Center, null);
        host.Mission.Sectors.Single(s => s.StationRuntimes.Contains(station)).RemoveStation(station);
        // Complete the station-loss callback normally dispatched by Sector's combat pass.
        host.Mission.Site.SideLostRestartStation(host.Mission, 1);
        Assert.True(station.Dead);
        Assert.True(host.Mission.Sides.Single(s => s.Id == 1).Active);
        Assert.True(host.TrainingShipExists(defender));
        Assert.False(host.Drones[defender].Dead);
    }

    private static Goal AuthoredGoal(TrainingMission mission, string factory, int number)
    {
        var root = (Goal)mission.GetType().GetMethod(factory, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(mission, null)!;
        var list = (GoalList)typeof(Goal).GetField("_successCondition", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(root)!;
        var goals = (List<Goal>)typeof(GoalList).GetField("_goals", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(list)!;
        return goals[number - 1];
    }

    [SkippableFact, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    public void Station_goto_replaces_a_nanite_repair_plan()
    {
        Require.Artwork();
        using var host = TrainingHost.Start(TrainingHost.Nanite);
        int id = host.CreateDrone("Nanite order regression", 9882, 385, 0, (int)Ship.PilotTypeWingman);
        host.PlaceDrone(id, 1052, Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, null, CmdDoNothing);
        var nanite = host.Drones[id];
        nanite.SetRepairOrder(host.PlayerShip);
        Assert.True(nanite.HasRepairOrder);
        // Native accepted orders replace the one plan (shipIGC.h:750-774).
        host.IssueCommand(id, CommandAccepted, null, null, 1052, CmdGoto);
        Assert.False(nanite.HasRepairOrder);
        Assert.Null(nanite.GotoWaypoint); // the station bay plan owns navigation
        Assert.NotNull(nanite.PlanTarget);
        Assert.Equal(CmdGoto, nanite.PlanCommandId);
        Assert.Equal(1052, nanite.AcceptedShipTarget!.Id);
    }

    [Fact]
    public void Killing_an_unselected_miner_advances_while_current_target_stays_in_Mars()
    {
        // mission4.cpp:821-824: ANY miner's destruction is independent of target departure.
        var site = new FakeTrainingSite();
        var mission = new Mission4(site);
        TrainingController.BeginMission();
        mission.Start();
        for (int id = 1101; id <= 1104; id++)
            site.AddObject(ObjectType.Ship, id, Vector3.Zero, Vector3.UnitX).SectorId = 1031;
        site.CommandTargets[site.PlayerShip.Ref.Id] = new TargetRef(ObjectType.Ship, 1104);
        var encounter = AuthoredGoal(mission, "CreateGoal07", 7);
        Assert.False(encounter.Start());
        Assert.False(encounter.Evaluate());
        site.DestroyShip(1101);
        Assert.True(encounter.Evaluate());
        Assert.False(site.IsShipDestroyed(new TargetRef(ObjectType.Ship, 1104)));
        Assert.Equal(1031, site.GetSector(new TargetRef(ObjectType.Ship, 1104)));
        encounter.Stop();
    }

    [SkippableFact, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    public void Bomber_keeps_its_accepted_outpost_attack_below_retreat_health()
    {
        Require.Artwork();
        using var host = TrainingHost.Start(TrainingHost.Nanite);
        int id = host.CreateDrone("Commander regression", 9871, 604, 0, (int)Ship.PilotTypeWingman);
        host.PlaceDrone(id, 1052, new Vector3(-2000, -2000, 0), Vector3.UnitX, Vector3.UnitZ, null, CmdDoNothing);
        host.IssueCommand(id, CommandAccepted, null, null, Mission8.EnemyOutpostStationId, CmdAttack);
        host.DamageShip(id, 12000); // .4 hull: ordinarily below the .6 retreat threshold
        var bomber = host.Drones[id];
        Assert.True(bomber.IsNpc);
        bomber.WingmanBehaviour = 0x22; // native CreateDroneAction default: missiles + run at .6
        Assert.InRange(bomber.Fraction, .39f, .41f);
        Assert.NotNull(bomber.AcceptedShipTarget);
        // Native shipIGC.cpp:1730-1737 exempts station targets, including attack orders.
        typeof(Ship).GetMethod("UpdateDamageRetreat", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(bomber, [100d]);
        Assert.False(bomber.IsRunningAway);
        Assert.Equal(CmdAttack, bomber.AcceptedShipCommandId);
        Assert.Equal(Mission8.EnemyOutpostStationId, bomber.AcceptedShipTarget!.Id);
        host.IssueCommand(id, CommandAccepted, host.PlayerShipId, null, null, CmdAttack);
        typeof(Ship).GetMethod("UpdateDamageRetreat", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(bomber, [100d]);
        Assert.True(bomber.IsRunningAway); // a ship target still obeys the ordinary retreat rule
    }

    [Fact]
    public void Tkela_switches_both_commands_to_bomber_only_after_player_hull_damage()
    {
        // mission8.cpp:603-613; GetShipIsDamagedCondition.h:32-34 defaults to 1.0.
        // Exercise the authored goal, including its once-only conditional actions.
        var site = new FakeTrainingSite();
        var mission = new Mission8(site);
        TrainingController.BeginMission();
        mission.Start();
        foreach (int id in new[] { mission.BomberId, mission.NanId, mission.TkelaId })
            site.AddObject(ObjectType.Ship, id, Vector3.Zero, Vector3.UnitX);
        site.AddObject(ObjectType.Station, Mission8.EnemyOutpostStationId, Vector3.Zero, Vector3.UnitX);
        var encounter = AuthoredGoal(mission, "CreateGoal03", 19);
        Assert.False(encounter.Start());
        Assert.Contains("Stay on target.", site.Messages);
        site.IssuedCommands.Clear();
        Assert.False(encounter.Evaluate());
        Assert.Empty(site.IssuedCommands);
        site.ShipFractions[site.PlayerShip.Ref.Id] = .99f;
        Assert.False(encounter.Evaluate());
        Assert.Empty(site.IssuedCommands); // native delayed execution: fire on the next evaluation
        Assert.False(encounter.Evaluate());
        foreach (int command in new[] { CommandAccepted, CommandCurrent })
            Assert.Contains(site.IssuedCommands, c => c.ShipId == mission.TkelaId && c.Command == command
                && c.Target == new TargetRef(ObjectType.Ship, mission.BomberId) && c.CommandId == CmdAttack);
        site.IssuedCommands.Clear();
        Assert.False(encounter.Evaluate());
        Assert.Empty(site.IssuedCommands);
        encounter.Stop();
    }
}
