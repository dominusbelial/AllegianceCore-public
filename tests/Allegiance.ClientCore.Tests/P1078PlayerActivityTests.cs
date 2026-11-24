using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.Tests;

[Trait("Tier", "Fast")]
public sealed class P1078PlayerActivityTests
{
    [Fact]
    public void Input_recorded_before_logon_is_consumed_on_the_next_logged_on_update()
    {
        var activity = new PlayerActivity();
        activity.Update(0, true, true, false);
        activity.RecordInput(); // menu/join input, while updates are not logged on
        Assert.True(activity.Update(500, true, false, false));
        Assert.Null(activity.Update(590, true, true, false));
        Assert.False(activity.Update(590.001, true, true, false));
    }

    [Theory]
    [InlineData(true, 90)]
    [InlineData(false, 180)]
    public void Native_idle_deadline_is_strict_and_input_wakes_once(bool lobby, int seconds)
    {
        var activity = new PlayerActivity();
        Assert.True(activity.Update(10, lobby, false, false));
        Assert.Null(activity.Update(10 + seconds, lobby, true, false));
        Assert.False(activity.Update(10.001 + seconds, lobby, true, false));
        Assert.Null(activity.Update(11 + seconds, lobby, false, false));
        Assert.True(activity.Update(12 + seconds, lobby, false, true));
        Assert.Null(activity.Update(13 + seconds, lobby, true, true));
        Assert.Null(activity.Update(13 + 2 * seconds, lobby, true, false));
        Assert.False(activity.Update(13.001 + 2 * seconds, lobby, true, false));
    }

    [Fact]
    public void Side_change_and_match_reset_do_not_clear_afk_or_manual_suppression()
    {
        var activity = new PlayerActivity();
        Assert.Null(activity.Update(0, true, true, true));
        activity.ToggleManualAfk();
        Assert.Null(activity.Update(100, true, false, true));
        Assert.Null(activity.Update(400, false, false, false));
        Assert.Null(activity.Update(401, true, false, false));
        Assert.True(activity.ManualAfk);
        activity.ToggleManualAfk();
        Assert.Null(activity.Update(402, true, true, true));
        Assert.False(activity.ManualAfk);
        Assert.False(activity.Update(493, true, true, false));
        Assert.Null(activity.Update(494, false, false, false));
        Assert.True(activity.Update(495, false, false, true));
    }

    [Fact]
    public void Game_over_sets_only_the_ready_leader_afk_once_then_activity_can_wake()
    {
        var leader = new PlayerActivity();
        var pilot = new PlayerActivity();
        leader.Update(0, false, true, true); pilot.Update(0, false, true, true);
        leader.RecordLeaderGameOver();
        Assert.False(leader.Update(5, false, true, true));
        Assert.Null(pilot.Update(5, false, true, false));
        Assert.Null(leader.Update(6, false, false, false));
        Assert.True(leader.Update(7, false, false, true));
        Assert.Null(leader.Update(8, false, true, false));
        leader.RecordLeaderGameOver();
        Assert.False(leader.Update(10, false, true, false));
        leader.ToggleManualAfk();
        leader.RecordLeaderGameOver();
        Assert.False(leader.Update(11, false, true, false));

    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Game_over_leader_signal_survives_rotation_and_does_not_reclassify_new_leadership(bool wasLeader)
    {
        using var world = new ClientWorld();
        void Own(short id)
        {
            world.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.FromValues(id, 0, default, 0,
                Vector3.Zero, Orientation.Identity, Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1) });
            world.Apply(new PlayerInfo { ShipId = id, ISide = 0, CharacterName = "Pilot" });
        }
        Own(10);
        world.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageStarted });
        world.Apply(new SetTeamLeader { SideId = 0, ShipId = wasLeader ? (short)10 : (short)11 });
        world.Apply(new GameOver());
        // One receive burst can retire all old roles and the result panel before
        // the page timer runs. The NEW leader must not determine the old event.
        world.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageOver });
        world.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageStarting });
        Own(20);
        world.Apply(new SetTeamLeader { SideId = 0, ShipId = wasLeader ? (short)21 : (short)20 });
        Assert.Null(world.GameOverResult);
        Assert.Equal(wasLeader, world.TakeOwnLeaderGameOver());
        Assert.False(world.TakeOwnLeaderGameOver());
        world.Apply(new SetTeamLeader { SideId = 0, ShipId = 20 });
        world.Apply(new GameOver());
        world.Reset();
        Assert.False(world.TakeOwnLeaderGameOver());
    }

    [Fact]
    public void Explicit_join_and_team_ready_release_manual_suppression_without_resetting_idle_time()
    {
        var activity = new PlayerActivity();
        activity.Update(0, true, true, true);
        activity.ToggleManualAfk();
        Assert.Null(activity.Update(10, true, false, true));
        activity.ClearManualAfk();
        Assert.False(activity.ManualAfk);
        Assert.Null(activity.Update(100, true, true, false));
        Assert.False(activity.Update(100.001, true, true, false));
    }


}
