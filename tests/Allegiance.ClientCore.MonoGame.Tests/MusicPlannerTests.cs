using Allegiance.ClientCore.MonoGame.Audio;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="MusicPlanner"/> — the P304 toggle/volume/
/// flight-vs-station switching contract. No audio device involved: the planner
/// only emits <see cref="MusicCommand"/>s; the MonoGame <see cref="Music"/>
/// wrapper performs them. Mirrors the established pure-planner test pattern
/// (<see cref="StationSoundPlannerTests"/>).
/// </summary>
public class MusicPlannerTests
{
    [Fact]
    public void Defaults_MusicOff_VolumeStart_NoTrack()
    {
        var planner = new MusicPlanner();

        Assert.False(planner.Enabled);
        Assert.Equal(0.8f, planner.Volume, 5);
        Assert.Equal(MusicTrackKind.None, planner.Current);

        // Off → no directives at all (nothing started, nothing to stop).
        Assert.Empty(planner.Plan(isDocked: false));
        Assert.Empty(planner.Plan(isDocked: true));
    }

    [Fact]
    public void EnableUndocked_StartsFlightTrack()
    {
        var planner = new MusicPlanner();
        planner.Toggle();

        var cmds = planner.Plan(isDocked: false);

        var start = Assert.Single(cmds);
        Assert.Equal(MusicTrackKind.Flight, start.Track);
        Assert.Equal(MusicAction.Start, start.Action);
        Assert.Equal(planner.Volume, start.Volume, 5);
        Assert.Equal(MusicTrackKind.Flight, planner.Current);
    }

    [Fact]
    public void EnableWhileDocked_StartsStationTrack()
    {
        var planner = new MusicPlanner();
        planner.Toggle();

        var cmds = planner.Plan(isDocked: true);

        var start = Assert.Single(cmds);
        Assert.Equal(MusicTrackKind.Station, start.Track);
        Assert.Equal(MusicAction.Start, start.Action);
    }

    [Fact]
    public void ToggleOff_StopsTheLiveTrack()
    {
        var planner = new MusicPlanner();
        planner.Toggle();
        planner.Plan(isDocked: false);        // flight starts
        planner.Toggle();                     // off

        var cmds = planner.Plan(isDocked: false);

        var stop = Assert.Single(cmds);
        Assert.Equal(MusicTrackKind.Flight, stop.Track);
        Assert.Equal(MusicAction.Stop, stop.Action);
        Assert.Equal(MusicTrackKind.None, planner.Current);
    }

    [Fact]
    public void Dock_SwitchesFlightToStation_StopBeforeStart()
    {
        var planner = new MusicPlanner();
        planner.Toggle();
        planner.Plan(isDocked: false);        // flight live

        var cmds = planner.Plan(isDocked: true);

        // Exactly two directives, in order: stop flight, then start station.
        Assert.Equal(2, cmds.Count);
        Assert.Equal(new MusicCommand(MusicTrackKind.Flight, MusicAction.Stop, 0f), cmds[0]);
        Assert.Equal(new MusicCommand(MusicTrackKind.Station, MusicAction.Start, planner.Volume), cmds[1]);
        Assert.Equal(MusicTrackKind.Station, planner.Current);
    }

    [Fact]
    public void Undock_SwitchesStationBackToFlight()
    {
        var planner = new MusicPlanner();
        planner.Toggle();
        planner.Plan(isDocked: true);         // station live

        var cmds = planner.Plan(isDocked: false);   // undock → switch to flight

        Assert.Equal(2, cmds.Count);
        Assert.Equal(MusicTrackKind.Station, cmds[0].Track);
        Assert.Equal(MusicAction.Stop, cmds[0].Action);
        Assert.Equal(MusicTrackKind.Flight, cmds[1].Track);
        Assert.Equal(MusicAction.Start, cmds[1].Action);
        Assert.Equal(MusicTrackKind.Flight, planner.Current);
    }

    [Fact]
    public void SteadyState_EmitsSetVolume_ReflectingVolumeChanges()
    {
        var planner = new MusicPlanner();
        planner.Toggle();
        planner.Plan(isDocked: false);        // flight starts at 0.8
        planner.VolumeUp();                   // → 0.8625

        var cmds = planner.Plan(isDocked: false);

        var set = Assert.Single(cmds);
        Assert.Equal(MusicAction.SetVolume, set.Action);
        Assert.Equal(0.8f + MusicPlanner.VolumeStep, set.Volume, 5);
        Assert.Equal(set.Volume, planner.Volume, 5);
    }

    [Fact]
    public void VolumeUp_IsReproduciblyMeasurable_AcrossSteps()
    {
        var planner = new MusicPlanner();
        float[] ladder =
        {
            planner.Volume,                      // 0.8
            planner.Volume + MusicPlanner.VolumeStep,
            planner.Volume + 2 * MusicPlanner.VolumeStep,
        };
        planner.VolumeUp();
        Assert.Equal(ladder[1], planner.Volume, 5);
        planner.VolumeUp();
        Assert.Equal(ladder[2], planner.Volume, 5);
        planner.VolumeDown();
        Assert.Equal(ladder[1], planner.Volume, 5);
    }

    [Fact]
    public void Volume_ClampsAtBounds()
    {
        var planner = new MusicPlanner();
        for (int i = 0; i < 100; i++) planner.VolumeUp();
        Assert.Equal(1f, planner.Volume, 5);

        for (int i = 0; i < 200; i++) planner.VolumeDown();
        Assert.Equal(0f, planner.Volume, 5);
    }

    [Fact]
    public void Reenable_AfterTurningOff_StartsFresh_ForCurrentDockState()
    {
        var planner = new MusicPlanner();
        planner.Toggle();
        planner.Plan(isDocked: true);         // station
        planner.Toggle();                     // off → stop
        planner.Plan(isDocked: true);
        Assert.Equal(MusicTrackKind.None, planner.Current);

        planner.Toggle();                     // back on, still docked
        var cmds = planner.Plan(isDocked: true);

        var start = Assert.Single(cmds);
        Assert.Equal(MusicTrackKind.Station, start.Track);
        Assert.Equal(MusicAction.Start, start.Action);
    }

    [Fact]
    public void SetVolume_OverridesToExplicitMix()
    {
        var planner = new MusicPlanner();
        planner.SetVolume(0.3f);
        Assert.Equal(0.3f, planner.Volume, 5);
        planner.SetVolume(9f);
        Assert.Equal(1f, planner.Volume, 5);
        planner.SetVolume(-3f);
        Assert.Equal(0f, planner.Volume, 5);
    }
}
