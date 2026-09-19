using Allegiance.ClientCore.MonoGame.Audio;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="StationSoundPlanner"/> — the P267
/// enter→Start / continue→Update / leave→Stop transition contract for a
/// station's exterior ambient sound, keyed by the station's real
/// <c>StationId</c>. No audio device involved: the planner only emits
/// <see cref="StationSoundCommand"/>s; <see cref="SoundManager"/> performs them.
/// </summary>
public class StationSoundPlannerTests
{
    [Fact]
    public void NewStation_EmitsStart_WithDistanceScaledVolume()
    {
        var planner = new StationSoundPlanner();

        var cmds = planner.Plan(new[] { ((short)7, 300f) });

        var start = Assert.Single(cmds);
        Assert.Equal(StationSoundAction.Start, start.Action);
        Assert.Equal(7, start.StationId);
        Assert.Equal(StationAmbientSoundCurve.VolumeForDistance(300f), start.Volume, 5);
    }

    [Fact]
    public void CloserStation_EmitsUpdate_WithLouderVolume()
    {
        var planner = new StationSoundPlanner();
        planner.Plan(new[] { ((short)7, 3000f) });

        // Next frame the player has closed in → Update (never a second Start),
        // with a HIGHER volume than the initial 3000-unit one.
        var cmds = planner.Plan(new[] { ((short)7, 900f) });

        var update = Assert.Single(cmds);
        Assert.Equal(StationSoundAction.Update, update.Action);
        Assert.Equal(7, update.StationId);
        Assert.True(update.Volume > StationAmbientSoundCurve.VolumeForDistance(3000f),
            $"expected closer distance to be louder: {update.Volume}");
        Assert.Equal(StationAmbientSoundCurve.VolumeForDistance(900f), update.Volume, 5);
    }

    [Fact]
    public void LeftRange_EmitsStop()
    {
        var planner = new StationSoundPlanner();
        planner.Plan(new[] { ((short)7, 300f) });

        // The station leaves audible range (player flew away / transited / the
        // station was destroyed and removed) → Stop, exactly once.
        var cmds = planner.Plan(Array.Empty<(short, float)>());

        var stop = Assert.Single(cmds);
        Assert.Equal(StationSoundAction.Stop, stop.Action);
        Assert.Equal(7, stop.StationId);
    }

    [Fact]
    public void MultipleStations_TrackedIndependently()
    {
        var planner = new StationSoundPlanner();

        planner.Plan(new[] { ((short)1, 300f), ((short)2, 500f) });
        var alive = planner.Plan(new[] { ((short)1, 640f) });   // station 2 left range

        Assert.Equal(2, alive.Count);
        Assert.Contains(alive, c => c.Action == StationSoundAction.Stop && c.StationId == 2);
        Assert.Contains(alive, c => c.Action == StationSoundAction.Update && c.StationId == 1);

        Assert.Single(planner.Active);
        Assert.Contains(planner.Active, id => id == 1);
    }

    [Fact]
    public void StoppedThenReheard_EmittedAsFreshStart()
    {
        // Once a station is stopped it is forgotten, so hearing it again (e.g.
        // the player transits back into the sector) is a fresh Start.
        var planner = new StationSoundPlanner();
        planner.Plan(new[] { ((short)3, 300f) });
        planner.Plan(Array.Empty<(short, float)>());   // stop

        var cmds = planner.Plan(new[] { ((short)3, 620f) });

        var start = Assert.Single(cmds);
        Assert.Equal(StationSoundAction.Start, start.Action);
        Assert.Equal(3, start.StationId);
    }
}
