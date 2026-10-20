using System.Linq;
using Allegiance.ClientCore.MonoGame.Audio;
using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P463c — pure tests for the collision feedback rules
/// (<see cref="CollisionFeedback"/> and <see cref="CollisionSoundCurve"/>), the
/// MonoGame-free decisions that turn a drained <see cref="CollisionEntity"/> into
/// the positional collision sound and P449b's own-ship jiggle feed. No graphics
/// or audio device needed.
/// </summary>
public class CollisionFeedbackTests
{
    private static CollisionEntity Ev(short p1, short p2, short cluster,
        float j1 = 1f, float j2 = 2f, Vector3 pos = default) =>
        new(p1, p2, cluster, j1, j2, pos);

    // ══ the "own ship only" jiggle-feed rule ══
    [Fact]
    public void Jiggle_is_fed_only_when_local_ship_is_a_party()
    {
        // Local ship IS party1 → party1's intensity.
        Assert.Equal(5f, CollisionFeedback.JiggleForOwnShip(10, 20, 5f, 9f, 10));
        // Local ship IS party2 → party2's intensity (e.g. the light ship of a
        // ship-ship hit whose heavier partner is party1).
        Assert.Equal(9f, CollisionFeedback.JiggleForOwnShip(10, 20, 5f, 9f, 20));
        // Local ship is NEITHER party (a witnessed collision) → 0, never shake.
        Assert.Equal(0f, CollisionFeedback.JiggleForOwnShip(10, 20, 5f, 9f, 30));
        // No local ship yet → 0.
        Assert.Equal(0f, CollisionFeedback.JiggleForOwnShip(10, 20, 5f, 9f, null));
        // A ship-rock collision (party2 = the rock, id NA=-1): the local ship is
        // the ship party1 and feeds its own jiggle; the rock never contributes.
        Assert.Equal(5f, CollisionFeedback.JiggleForOwnShip(10, -1, 5f, 9f, 10));
    }

    [Fact]
    public void OwnShipIsParty_matches_either_role()
    {
        Assert.True(CollisionFeedback.OwnShipIsParty(10, 20, 10));
        Assert.True(CollisionFeedback.OwnShipIsParty(10, 20, 20));
        Assert.False(CollisionFeedback.OwnShipIsParty(10, 20, 30));
        Assert.False(CollisionFeedback.OwnShipIsParty(10, 20, null));
    }

    // ══ the collision-sound selection happens once per party ══
    [Fact]
    public void Sound_selection_yields_one_source_per_party_in_local_sector()
    {
        var inSector = Ev(1, 2, 7, pos: new Vector3(100, 0, 0));      // ship-ship (two parties, one point)
        var outSector = Ev(3, 4, 99, pos: new Vector3(0, 50, 0));     // different sector → silent here
        var rock = Ev(5, -1, 7, j2: 0, pos: new Vector3(0, 0, 200));  // ship-rock (one ship party, NA rock)

        var sources = CollisionFeedback.SelectSoundSources(new[] { inSector, outSector, rock }, 7).ToList();

        // Native plays collisionSound once per party (Igc/shipIGC.cpp:1193 and the
        // sibling branch for the other party). A ship-ship collision yields two
        // sources at the same impact point; a ship-rock collision yields one.
        Assert.Equal(3, sources.Count);
        Assert.Equal(new Vector3(100, 0, 0), sources[0].Position);
        Assert.Equal((short)1, sources[0].PartyId);
        Assert.Equal(new Vector3(100, 0, 0), sources[1].Position);
        Assert.Equal((short)2, sources[1].PartyId);
        Assert.Equal(new Vector3(0, 0, 200), sources[2].Position);
        Assert.Equal((short)5, sources[2].PartyId);
    }

    [Fact]
    public void Sound_selection_is_empty_without_a_local_sector()
    {
        Assert.Empty(CollisionFeedback.SelectSoundSources(new[] { Ev(1, 2, 7) }, null));
    }

    // ══ the volume curve ══
    [Fact]
    public void Collision_volume_falls_off_linearly_to_silence()
    {
        Assert.Equal(CollisionSoundCurve.PeakVolume, CollisionSoundCurve.Volume(0f));
        Assert.Equal(0f, CollisionSoundCurve.Volume(CollisionSoundCurve.AudibleRadius));
        Assert.Equal(0f, CollisionSoundCurve.Volume(CollisionSoundCurve.AudibleRadius * 2));
        // Halfway → half the peak (linear falloff, same shape as the hit curve).
        Assert.Equal(CollisionSoundCurve.PeakVolume * 0.5f,
            CollisionSoundCurve.Volume(CollisionSoundCurve.AudibleRadius * 0.5f), 3);
    }
}
