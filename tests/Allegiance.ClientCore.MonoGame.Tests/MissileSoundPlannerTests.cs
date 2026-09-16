using Allegiance.ClientCore.MonoGame.Audio;
using Allegiance.Core.Collision;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="MissileSoundPlanner"/> — the P236 launch →
/// spawn / in-flight → expire / detonation → explosion transition contract,
/// keyed by the missile's real <c>MissileID</c>. No audio device involved: the
/// planner only emits <see cref="MissileSoundCommand"/>s; <see cref="SoundManager"/>
/// performs them. These are the P236 brief's explicit "sound-triggering logic is
/// pure enough to test" unit tests (the play/stop side is the SoundManager wiring
/// the live proof exercises).
/// </summary>
public class MissileSoundPlannerTests
{
    private static MissileSoundSample Ms(short id, float distance, bool local = false,
        Vector3? pos = null) =>
        new(id, pos ?? new Vector3(id, 0f, 0f), distance, local);

    [Fact]
    public void NewLocalMissile_EmitsLaunch_ThenAmbientStart()
    {
        var planner = new MissileSoundPlanner();

        var cmds = planner.Plan(new[] { Ms(1, 10f, local: true) });

        Assert.Collection(cmds,
            c => { Assert.Equal(MissileSoundAction.Launch, c.Action); Assert.Equal((short)1, c.MissileId); },
            c =>
            {
                Assert.Equal(MissileSoundAction.AmbientStart, c.Action);
                Assert.Equal(MissileAmbientSoundCurve.VolumeForDistance(10f), c.Volume, 5);
            });
    }

    [Fact]
    public void NewNonLocalMissile_EmitsAmbientStart_ButNoLaunch()
    {
        var planner = new MissileSoundPlanner();

        // A distant enemy's missile (spawn well past the launch origin radius) gets
        // an ambient but no launch one-shot (native positions the launch at the
        // firing ship — only our own fire is near enough to hear up close).
        var cmds = planner.Plan(new[] { Ms(1, MissileAmbientSoundCurve.LaunchOriginRadius + 500f) });

        var start = Assert.Single(cmds);
        Assert.Equal(MissileSoundAction.AmbientStart, start.Action);
    }

    [Fact]
    public void ContinuedMissile_EmitsAmbientUpdate_NotAnotherStartOrLaunch()
    {
        var planner = new MissileSoundPlanner();
        planner.Plan(new[] { Ms(1, 10f, local: true) });

        var cmds = planner.Plan(new[] { Ms(1, 555f) });

        var update = Assert.Single(cmds);
        Assert.Equal(MissileSoundAction.AmbientUpdate, update.Action);
        Assert.Equal(MissileAmbientSoundCurve.VolumeForDistance(555f), update.Volume, 5);
    }

    [Fact]
    public void DisappearedMissile_EmitsAmbientStop_AndExplosionAtLastSite()
    {
        var planner = new MissileSoundPlanner();
        var pos = new Vector3(1f, 2f, 3f);
        planner.Plan(new[] { Ms(7, 250f, pos: pos) });

        var cmds = planner.Plan(Array.Empty<MissileSoundSample>());

        Assert.Contains(cmds, c =>
            c.Action == MissileSoundAction.AmbientStop && c.MissileId == 7);
        Assert.Contains(cmds, c =>
            c.Action == MissileSoundAction.Explosion
            && c.MissileId == 7
            && c.Position == pos
            && Math.Abs(c.Volume - MissileAmbientSoundCurve.ExplosionVolumeForDistance(250f)) < 1e-4
            && c.Tier == ExplosionSoundTier.Medium);  // DefaultExplosionScale → medium
    }

    [Fact]
    public void DetonationBeyondAudibleRadius_NoExplosion_NoAmbientStop()
    {
        var planner = new MissileSoundPlanner();

        // A missile in the sector but far outside earshot: never started an ambient,
        // and when it disappears there's nothing to stop and no audible boom.
        planner.Plan(new[] { Ms(3, MissileAmbientSoundCurve.AudibleRadius + 7777f) });

        var cmds = planner.Plan(Array.Empty<MissileSoundSample>());

        Assert.DoesNotContain(cmds, c => c.Action == MissileSoundAction.Explosion);
        Assert.DoesNotContain(cmds, c => c.Action == MissileSoundAction.AmbientStop);
    }

    [Fact]
    public void LeavesEarshot_IsStopNotExplosion_ReturnsToRestart()
    {
        var planner = new MissileSoundPlanner();

        // In range → ambient starts.
        planner.Plan(new[] { Ms(5, 400f) });
        // Flies past the audible radius (still alive, just out of earshot) → the
        // ambient stops, but it is NOT an explosion (no detonation happened).
        var fadeCmds = planner.Plan(new[] { Ms(5, MissileAmbientSoundCurve.AudibleRadius + 1f) });
        Assert.Contains(fadeCmds, c => c.Action == MissileSoundAction.AmbientStop);
        Assert.DoesNotContain(fadeCmds, c => c.Action == MissileSoundAction.Explosion);

        // Comes back into earshot → a fresh AmbientStart (never confused with the
        // destroyed case).
        var backCmds = planner.Plan(new[] { Ms(5, 300f) });
        Assert.Contains(backCmds, c => c.Action == MissileSoundAction.AmbientStart && c.MissileId == 5);
        Assert.DoesNotContain(backCmds, c => c.Action == MissileSoundAction.Explosion);
    }

    [Fact]
    public void MultipleMissiles_TrackedIndependently_ByMissileId()
    {
        var planner = new MissileSoundPlanner();

        planner.Plan(new[] { Ms(1, 10f, local: true), Ms(2, 640f) });
        var next = planner.Plan(new[] { Ms(1, 712f) });  // 2 vanished meanwhile

        // 1 → AmbientUpdate; 2 → AmbientStop + Explosion.
        Assert.Contains(next, c => c.Action == MissileSoundAction.AmbientUpdate && c.MissileId == 1);
        Assert.Contains(next, c => c.Action == MissileSoundAction.AmbientStop && c.MissileId == 2);
        Assert.Contains(next, c => c.Action == MissileSoundAction.Explosion && c.MissileId == 2);

        Assert.Equal(new short[] { 1 }, planner.ActiveAmbients.ToArray());
    }

    [Fact]
    public void EmptyWorld_YieldsNoCommands_AndCleanState()
    {
        var planner = new MissileSoundPlanner();
        Assert.Empty(planner.Plan(Array.Empty<MissileSoundSample>()));
        Assert.Empty(planner.Plan(Array.Empty<MissileSoundSample>()));  // still clean
        Assert.Empty(planner.Known);
    }

    [Fact]
    public void GoneIdThenRespawned_DistinguishedById()
    {
        var planner = new MissileSoundPlanner();
        planner.Plan(new[] { Ms(9, 10f, local: true) });
        planner.Plan(Array.Empty<MissileSoundSample>());   // 9 detonated

        // A brand-new missile reusing id 9 is a fresh spawn → launch again.
        var cmds = planner.Plan(new[] { Ms(9, 10f, local: true) });

        Assert.Contains(cmds, c => c.Action == MissileSoundAction.Launch && c.MissileId == 9);
        Assert.Contains(cmds, c => c.Action == MissileSoundAction.AmbientStart && c.MissileId == 9);
    }
}
