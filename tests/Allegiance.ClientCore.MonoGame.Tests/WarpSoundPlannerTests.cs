using Allegiance.ClientCore.MonoGame.Audio;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="WarpSoundPlanner"/> — the P237
/// enter→Start / continue→Update / leave→Stop transition contract for a warp's
/// ambient sound, keyed by the warp's real <c>WarpId</c>. No audio device
/// involved: the planner only emits <see cref="WarpSoundCommand"/>s;
/// <see cref="SoundManager"/> performs them. These are the P237 brief's
/// "enter/leave → play/stop calls" unit tests (the play/stop side is the
/// SoundManager wiring the live proof exercises).
/// </summary>
public class WarpSoundPlannerTests
{
    [Fact]
    public void NewWarp_EmitsStart_WithDistanceScaledVolume()
    {
        var planner = new WarpSoundPlanner();

        var cmds = planner.Plan(new[] { ((short)7, 300f) });

        var start = Assert.Single(cmds);
        Assert.Equal(WarpSoundAction.Start, start.Action);
        Assert.Equal(7, start.WarpId);
        Assert.Equal(WarpAmbientSoundCurve.VolumeForDistance(300f), start.Volume, 5);
    }

    [Fact]
    public void CloserWarp_EmitsUpdate_WithLouderVolume()
    {
        var planner = new WarpSoundPlanner();
        planner.Plan(new[] { ((short)7, 3000f) });

        // Next frame the player has closed in → Update (never a second Start),
        // with a HIGHER volume than the initial 3000-unit one (the "increases
        // as you get close" swell).
        var cmds = planner.Plan(new[] { ((short)7, 900f) });

        var update = Assert.Single(cmds);
        Assert.Equal(WarpSoundAction.Update, update.Action);
        Assert.Equal(7, update.WarpId);
        Assert.True(update.Volume > WarpAmbientSoundCurve.VolumeForDistance(3000f),
            $"expected closer distance to be louder: {update.Volume}");
        Assert.Equal(WarpAmbientSoundCurve.VolumeForDistance(900f), update.Volume, 5);
    }

    [Fact]
    public void LeftRange_EmitsStop()
    {
        var planner = new WarpSoundPlanner();
        planner.Plan(new[] { ((short)7, 300f) });

        // The warp leaves audible range (player flew away / transited) →
        // Stop, exactly once.
        var cmds = planner.Plan(Array.Empty<(short, float)>());

        var stop = Assert.Single(cmds);
        Assert.Equal(WarpSoundAction.Stop, stop.Action);
        Assert.Equal(7, stop.WarpId);
    }

    [Fact]
    public void MultipleWarps_TrackedIndependently()
    {
        var planner = new WarpSoundPlanner();

        planner.Plan(new[] { ((short)1, 300f), ((short)2, 500f) });
        var alive = planner.Plan(new[] { ((short)1, 640f) });   // warp 2 left range

        // warp 2 → Stop; warp 1 → Update. Exactly those two commands.
        Assert.Equal(2, alive.Count);
        Assert.Contains(alive, c => c.Action == WarpSoundAction.Stop && c.WarpId == 2);
        Assert.Contains(alive, c => c.Action == WarpSoundAction.Update && c.WarpId == 1);

        // Planner no longer considers warp 2 active.
        Assert.Single(planner.Active);
        Assert.Contains(planner.Active, id => id == 1);
    }

    [Fact]
    public void StoppedThenReheard_EmittedAsFreshStart()
    {
        // Once a warp is stopped it is forgotten, so hearing it again (e.g. the
        // player transits back into the sector) is a fresh Start, not a stuck
        // Update on a stopped instance.
        var planner = new WarpSoundPlanner();
        planner.Plan(new[] { ((short)3, 300f) });
        planner.Plan(Array.Empty<(short, float)>());   // stop

        var cmds = planner.Plan(new[] { ((short)3, 620f) });

        var start = Assert.Single(cmds);
        Assert.Equal(WarpSoundAction.Start, start.Action);
        Assert.Equal(3, start.WarpId);
    }
}
