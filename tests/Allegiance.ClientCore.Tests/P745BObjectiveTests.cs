using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Wire;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.Tests;

public sealed class P745BObjectiveTests
{
    private static LastBaseNotice Notice() => new()
    {
        FinaleSecondsRemaining = 89,
        Targets = [new(0, 10, 0, 1, 2, 3, "Home"), new(1, 11, 1, 4, 5, 6, "Garrison")],
    };
    private static ClientWorld Seated()
    {
        var world = new ClientWorld();
        world.Apply(new ExportShip { ShipId = -1, SideId = 0 });
        world.Apply(new SetCluster { SectorId = 0, Ship = ServerShipUpdate.FromValues(42, 0, default, 0, Vector3.Zero, Orientation.Identity, Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1) });
        world.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageStarted });
        return world;
    }

    [Fact]
    public void Notice_roundtrips_and_rejects_truncated_invalid_count_and_nonfinite_time()
    {
        var notice = Notice(); var bytes = notice.Encode(); var decoded = LastBaseNotice.Decode(bytes);
        Assert.Equal(notice.Targets, decoded.Targets); Assert.Equal(89, decoded.FinaleSecondsRemaining);
        Assert.Throws<ArgumentException>(() => LastBaseNotice.Decode(bytes[..^1]));
        bytes[8] = 7; Assert.Throws<ArgumentException>(() => LastBaseNotice.Decode(bytes));
        Assert.Throws<ArgumentException>(() => LastBaseNotice.Decode(new LastBaseNotice { FinaleSecondsRemaining = float.NaN }.Encode()));
    }

    [Fact]
    public void Hud_objectives_use_authoritative_unseen_station_and_clear_on_kill_capture_over_reset()
    {
        var world = Seated(); Assert.Empty(world.LastBaseObjectives());
        world.Apply(Notice());
        var enemy = world.LastBaseObjectives()[0];
        Assert.Equal("Enemy's last Garrison: destroy it to win | Finale 1:29", enemy.Text);
        Assert.Equal("Our last Home: defend it | Finale 1:29", world.LastBaseObjectives()[1].Text);
        Assert.EndsWith("Finale 60:00", (enemy with { FinaleSecondsRemaining = 3600 }).Text);
        Assert.DoesNotContain("Finale", (enemy with { FinaleSecondsRemaining = -1 }).Text);
        Assert.Empty(world.Stations); // Counts/positions are reliable even before discovery.
        Assert.Empty(world.LastBaseObjectives(spectating: true));
        Assert.Single(world.TakeSoundEvents(), e => e.Kind == ClientSoundEventKind.LastEnemyBase);
        world.Apply(Notice()); Assert.Empty(world.TakeSoundEvents()); // Countdown refresh does not repeat VO.
        world.Apply(new StationDestroyed { StationId = 11 }); Assert.True(Assert.Single(world.LastBaseObjectives()).Defending);
        world.Apply(Notice()); world.Apply(new StationCapture { StationId = 11, SidOld = 1, SidNew = 0 });
        Assert.Single(world.LastBaseObjectives());
        world.Apply(new LastBaseNotice()); Assert.Empty(world.LastBaseObjectives());
        world.Apply(Notice()); world.Apply(new GameOver { Sides = new SideEndgameInfo[6] });
        Assert.Empty(world.LastBaseObjectives());
        world.BeginNextMission(); Assert.Empty(world.LastBaseObjectives());
    }

    [Fact]
    public void Observers_never_receive_objective_or_cue_and_inactive_sides_clear()
    {
        var observer = new ClientWorld();
        observer.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageStarted });
        observer.Apply(Notice()); Assert.Empty(observer.LastBaseObjectives()); Assert.Empty(observer.TakeSoundEvents());
        var unseated = Seated();
        unseated.Apply(new ExportShip { ShipId = -1, SideId = -1 });
        unseated.Apply(Notice());
        Assert.Empty(unseated.LastBaseObjectives()); Assert.Empty(unseated.TakeSoundEvents());
        var pilot = Seated(); pilot.Apply(Notice());
        pilot.Apply(new SideInactive { SideId = 1, BActive = false });
        Assert.True(Assert.Single(pilot.LastBaseObjectives()).Defending);
        pilot.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageOver });
        Assert.Empty(pilot.LastBaseObjectives());
    }
}
