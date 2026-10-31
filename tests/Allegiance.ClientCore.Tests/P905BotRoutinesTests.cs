using Allegiance.Tools.McpSpawner;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.Tests;

public sealed class P905BotRoutinesTests
{
    [Fact]
    public void Ordered_miner_waits_ignores_duplicate_flying_dispatch_and_accepts_after_docking()
    {
        var miner = new OrderedMinerRoutine();
        Assert.Null(miner.Cluster);
        Assert.True(miner.Dispatch(7, 8, false));
        miner.Return();
        Assert.False(miner.Dispatch(7, 8, false));
        Assert.True(miner.Returning);
        Assert.True(miner.Dispatch(7, 8, true));
        Assert.False(miner.Returning);
        Assert.Equal((short)7, miner.Cluster);
        Assert.True(miner.Dispatch(8, 8, false));
    }

    [Theory]
    [InlineData(80, false, false)]
    [InlineData(80.01f, false, true)]
    [InlineData(90, true, false)]
    public void Wopr_absolute_ore_threshold_is_strict(float ore, bool docked, bool expected) =>
        Assert.Equal(expected, new OrderedMinerRoutine().ShouldReturn(ore, docked));

    [Fact]
    public void Docker_waits_faces_before_throttle_stops_then_repeats()
    {
        var docker = new DockerRoutine(); docker.Flying(0);
        Assert.Equal(DockerAction.Wait, docker.Tick(3, false, _ => 0));
        Assert.Equal(DockerAction.Face, docker.Tick(3.001, false, _ => 0));
        Assert.Equal(DockerAction.Face, docker.Tick(20, false, _ => 0));
        Assert.Equal(DockerAction.Fly, docker.Tick(21, true, _ => 59));
        Assert.Equal(DockerAction.Fly, docker.Tick(42, true, _ => 0));
        Assert.Equal(DockerAction.Dock, docker.Tick(42.001, true, _ => 14));
        Assert.Equal(DockerAction.Wait, docker.Tick(148, false, _ => 0));
        Assert.Equal(DockerAction.Face, docker.Tick(148.002, false, _ => 0));
    }

    [Fact]
    public void Face_uses_native_angular_drift_and_quarter_radius_completion()
    {
        var hull = new DockApproach.TurnState(100, 100, 1, 1, 100, 100, 0, 0);
        var straight = PigFaceRoutine.Face(new(0, 0, -100), Orientation.Identity, 10, hull, 0, .05f);
        Assert.True(straight.Complete);
        Assert.Equal(ControlData.Neutral, straight.Controls);
        var behind = PigFaceRoutine.Face(new(0, 0, 100), Orientation.Identity, 10, hull, 0, .05f);
        Assert.False(behind.Complete);
        Assert.Equal(1, behind.Controls.Yaw);
        var outside = PigFaceRoutine.Face(new(3, 0, -100), Orientation.Identity, 10, hull, 0, .05f);
        var inside = PigFaceRoutine.Face(new(2, 0, -100), Orientation.Identity, 10, hull, 0, .05f);
        Assert.False(outside.Complete); Assert.True(inside.Complete);
        var drifting = PigFaceRoutine.Face(new(3, 0, -100), Orientation.Identity, 10,
            hull with { RateYaw = .1f }, 0, .05f);
        Assert.NotEqual(outside.Controls.Yaw, drifting.Controls.Yaw);
    }

    [Fact]
    public void Deathmatch_retains_close_target_replaces_far_missing_or_pod_without_fleet_caps()
    {
        var dm = new DeathmatchRoutine();
        dm.Flying(0, [new(1, 100, false), new(2, 400, false)]);
        Assert.Equal((short)2, dm.Target); // native initial enumeration, not fleet priority
        Assert.False(dm.Tick(1, [new(1, 100, false), new(2, 400, false)]));
        Assert.True(dm.Tick(1.001, [new(1, 100, false), new(2, 400, false)]));
        Assert.Equal((short)2, dm.Target); // nearer enemy does not steal a close valid target
        dm.Tick(2.001, [new(1, 100, false), new(2, 2250000, false)]);
        Assert.Equal((short)2, dm.Target); // exact 1500 boundary stays
        dm.Tick(3.001, [new(1, 100, false), new(2, 2250001, false)]);
        Assert.Equal((short)1, dm.Target);
        dm.Tick(4.001, [new(1, 10, true), new(2, 500, false)]);
        Assert.Equal((short)2, dm.Target);
        dm.Tick(5.001, []);
        Assert.Null(dm.Target);
        dm.Tick(8.002, [new(3, 400, false)]);
        Assert.Equal((short)3, dm.Target);
    }

    [Fact]
    public void Deathmatch_adopts_a_nearest_attacker_but_ignores_hits_within_two_seconds()
    {
        var dm = new DeathmatchRoutine();
        DeathmatchCandidate[] ships = [new(1, 100, false), new(2, 400, false)];
        dm.Flying(0, ships);
        Assert.Equal((short)2, dm.Target);
        dm.Damaged(1, 2, ships); // already the target
        dm.Damaged(2.5, 1, ships); // nearest attacker, but 1.5s after the last hit
        Assert.Equal((short)2, dm.Target);
        dm.Damaged(5, 3, ships); // attacker is not the nearest enemy
        Assert.Equal((short)2, dm.Target);
        dm.Damaged(7.5, 1, ships);
        Assert.Equal((short)1, dm.Target);
    }

    [Fact]
    public void Dispatch_bookmark_survives_a_new_life_so_old_orders_are_not_replayed()
    {
        var stack = new BotRoutineStack("miner");
        var seen = new object();
        stack.LastBuoy = seen;
        stack.NewLife();
        stack.Push("docker");
        Assert.True(stack.Pop());
        Assert.Same(seen, stack.LastBuoy);
    }

    [Fact]
    public void Deathmatch_search_and_rearm_use_their_own_native_cadences()
    {
        var dm = new DeathmatchRoutine(); dm.Flying(0, []);
        Assert.False(dm.Tick(3, [new(1, 1, false)]));
        Assert.True(dm.Tick(3.001, [new(1, 1, false)]));
        Assert.Equal((short)1, dm.Target);
        Assert.False(dm.Rearm.Pulse(3));
        Assert.True(dm.Rearm.Pulse(3.001));
        Assert.True(dm.MustRearm(.149f, 1, 1));
        Assert.True(dm.MustRearm(1, 0, 1));
        Assert.True(dm.MustRearm(.299f, 1, 1));
        dm.Tick(4.001, [new(1, 1, false)]);
        Assert.True(dm.Close);
        Assert.True(dm.Reached);
        Assert.False(dm.MustRearm(.20f, .09f, .09f));
        dm.Tick(5.001, [new(1, 2250001, false)]);
        Assert.True(dm.MustRearm(1, 1, .09f));
    }

    [Fact]
    public void Typed_stack_preserves_suspended_state_and_never_pops_the_base()
    {
        var stack = new BotRoutineStack("miner");
        var original = stack.Top;
        original.Miner.Dispatch(7, 8, false);
        original.Chat.Set(0, 3, 1);
        stack.Push("docker"); stack.Push("deathmatch");
        Assert.Equal(3, stack.Count);
        Assert.True(stack.Pop()); Assert.Equal("docker", stack.Top.Behavior);
        Assert.True(stack.Pop()); Assert.Same(original, stack.Top);
        Assert.Equal((short)7, stack.Top.Miner.Cluster);
        Assert.True(stack.Top.Chat.Pulse(20)); // suspended timer observed on reactivation
        Assert.False(stack.Pop());
        Assert.Throws<ArgumentException>(() => stack.Push("javascript"));
        stack.NewLife();
        Assert.Null(stack.Top.Miner.Cluster);
        Assert.False(stack.Top.Chat.Pulse(100));
    }

    [Fact]
    public void Timer_coalesces_missed_intervals_repeats_reschedules_and_cancels()
    {
        var timer = new RoutineTimer(); timer.Set(0, 3, 2);
        Assert.False(timer.Pulse(3));
        Assert.True(timer.Pulse(10));
        Assert.False(timer.Pulse(10));
        Assert.True(timer.Pulse(12.001));
        Assert.False(timer.Pulse(100));
        timer.Set(100, 2);
        Assert.True(timer.Pulse(102.001));
        timer.Kill(); Assert.False(timer.Pulse(200));
        timer.Set(200, 1, 1); timer.Set(200, 5, 1);
        Assert.False(timer.Pulse(202)); Assert.True(timer.Pulse(205.001));
    }

    [Fact]
    public void Four_hundred_opted_in_chatters_share_one_bounded_chat_budget()
    {
        var chat = new ScriptedBotChat();
        Assert.Equal(1, Enumerable.Range(0, 400).Count(_ => chat.TrySend(0)));
        Assert.False(chat.TrySend(29.999));
        Assert.Equal(1, Enumerable.Range(0, 400).Count(_ => chat.TrySend(30)));
        Assert.Equal(62, ScriptedBotChat.Text.Length);
    }
}
