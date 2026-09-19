using Allegiance.ClientCore.MonoGame.Audio;
using Allegiance.Core.Collision;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="ProbeSoundPlanner"/> — the P260 deploy →
/// spawn / own-side ambient start/update / destroy-or-depart → stop transition
/// contract, keyed by the probe's real <c>ProbeId</c>. No audio device involved:
/// the planner only emits <see cref="ProbeSoundCommand"/>s;
/// <see cref="SoundManager"/> performs them. These are the P260 brief's explicit
/// "sound-triggering logic is pure enough to test" unit tests (the play/stop side
/// is the SoundManager wiring the live proof exercises).
/// </summary>
public class ProbeSoundPlannerTests
{
    private const float Far = ProbeAmbientSoundCurve.AudibleRadius + 500f;   // clearly out of range

    private static ProbeSoundSample Ps(short id, float distance, bool ownSide = true, bool ownDeploy = false,
        Vector3? pos = null) =>
        new(id, pos ?? new Vector3(id, 0f, 0f), distance, ownSide, ownDeploy);

    [Fact]
    public void NewOwnDeployProbe_EmitsDeploy_ThenAmbientStart()
    {
        var planner = new ProbeSoundPlanner();

        var cmds = planner.Plan(new[] { Ps(1, 10f, ownSide: true, ownDeploy: true) });

        Assert.Collection(cmds,
            c => { Assert.Equal(ProbeSoundAction.Deploy, c.Action); Assert.Equal((short)1, c.ProbeId); },
            c =>
            {
                Assert.Equal(ProbeSoundAction.AmbientStart, c.Action);
                Assert.Equal(ProbeAmbientSoundCurve.VolumeForDistance(10f), c.Volume, 5);
            });
    }

    [Fact]
    public void NewOwnSideButNotOwnDeployProbe_EmitsAmbientStart_ButNoDeploy()
    {
        var planner = new ProbeSoundPlanner();

        // A teammate's probe (own side, but someone else dropped it) gets the
        // ambient but no deploy one-shot (native only plays deployProbeSound when
        // pShip == m_ship, clintlib.cpp:3245-3257).
        var cmds = planner.Plan(new[] { Ps(1, 10f, ownSide: true, ownDeploy: false) });

        var start = Assert.Single(cmds);
        Assert.Equal(ProbeSoundAction.AmbientStart, start.Action);
    }

    [Fact]
    public void EnemyProbe_EmitsNoDeployAndNoAmbient()
    {
        var planner = new ProbeSoundPlanner();

        // An enemy probe is watched (so it can be cleaned up) but plays nothing:
        // the ambient is scoped to the player's own side.
        var cmds = planner.Plan(new[] { Ps(1, 10f, ownSide: false, ownDeploy: false) });

        Assert.Empty(cmds);
    }

    [Fact]
    public void ContinuedOwnSideProbe_EmitsAmbientUpdate_NotAnotherStartOrDeploy()
    {
        var planner = new ProbeSoundPlanner();
        planner.Plan(new[] { Ps(1, 10f, ownSide: true, ownDeploy: true) });

        var cmds = planner.Plan(new[] { Ps(1, 555f, ownSide: true, ownDeploy: true) });

        var update = Assert.Single(cmds);
        Assert.Equal(ProbeSoundAction.AmbientUpdate, update.Action);
        Assert.Equal(ProbeAmbientSoundCurve.VolumeForDistance(555f), update.Volume, 5);
    }

    [Fact]
    public void ProbeGoingOutOfRange_EmitsAmbientStop()
    {
        var planner = new ProbeSoundPlanner();
        planner.Plan(new[] { Ps(1, 10f, ownSide: true) });

        var cmds = planner.Plan(new[] { Ps(1, Far, ownSide: true) });

        var stop = Assert.Single(cmds);
        Assert.Equal(ProbeSoundAction.AmbientStop, stop.Action);
    }

    [Fact]
    public void DisappearedProbe_EmitsAmbientStop()
    {
        var planner = new ProbeSoundPlanner();
        planner.Plan(new[] { Ps(7, 250f, ownSide: true) });

        // The probe vanished (destroyed / expired / left the sector): stop its loop.
        var cmds = planner.Plan(Array.Empty<ProbeSoundSample>());

        var stop = Assert.Single(cmds);
        Assert.Equal(ProbeSoundAction.AmbientStop, stop.Action);
        Assert.Equal((short)7, stop.ProbeId);
    }

    [Fact]
    public void AlreadyStoppedProbe_EmitsNothingOnRepeat()
    {
        var planner = new ProbeSoundPlanner();
        planner.Plan(new[] { Ps(3, 10f, ownSide: true) });
        planner.Plan(new[] { Ps(3, Far, ownSide: true) });   // → stop

        // Now gone entirely: no lingering stop, no phantom commands.
        var cmds = planner.Plan(Array.Empty<ProbeSoundSample>());

        Assert.Empty(cmds);
    }
}
