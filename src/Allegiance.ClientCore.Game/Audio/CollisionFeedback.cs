using System.Collections.Generic;
using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;

namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>
/// P463c — the pure, MonoGame-free rules that turn a drained
/// <see cref="CollisionEntity"/> list into audio/jiggle actions, mirroring how
/// <see cref="HitSoundSelector"/> isolates P463's classification. Two decisions
/// live here so both are unit-testable without a graphics/audio device:
/// (a) which jiggle intensity to feed P449b's <c>FlightJiggle</c> when the
/// local ship is one of the two parties, and (b) the once-per-collision
/// positional sound sources (both parties collapse to the single impact point).
/// </summary>
public static class CollisionFeedback
{
    /// <summary>
    /// Whether the local ship (<paramref name="myShipId"/>) is one of the two
    /// collision parties. Native only jiggles a party the player themselves is
    /// flying (<c>trekigc.cpp:4992-5001</c> applies <c>SetJiggle</c> only to its
    /// own ship), so a collision the player merely witnesses must never shake
    /// their camera — this is the "own ship only" gate.
    /// </summary>
    public static bool OwnShipIsParty(short party1, short party2, short? myShipId) =>
        myShipId is { } me && (party1 == me || party2 == me);

    /// <summary>
    /// The jiggle intensity to feed into P449b's <c>FlightJiggle.SetJiggle</c>
    /// for this collision: <paramref name="jiggle1"/> when the local ship is
    /// party1, <paramref name="jiggle2"/> when it is party2, and 0 when it is
    /// neither (a witnessed collision never shakes the local camera — the
    /// "own ship only" rule). 0 for a rock/station party is already guaranteed
    /// server-side (those are never shaken by native), and is the no-op case
    /// here.
    /// </summary>
    public static float JiggleForOwnShip(short party1, short party2,
        float jiggle1, float jiggle2, short? myShipId)
    {
        if (myShipId is not { } me) return 0f;
        if (party1 == me) return jiggle1;
        if (party2 == me) return jiggle2;
        return 0f;
    }

    /// <summary>
    /// The positional sound sources from one frame's drained collisions: the
    /// impact point once <b>per party</b> for each collision that occurred in
    /// the local ship's sector (<paramref name="mySector"/>), in arrival order.
    /// Native plays <c>collisionSound</c> once per party
    /// (<c>Igc/shipIGC.cpp:1193</c> and sibling branches for the other party),
    /// and every party of a single collision sits at the same impact point
    /// (the spot they met), so a ship-ship hit yields two sources at the same
    /// position. A null <paramref name="mySector"/> (no local ship yet) yields
    /// nothing. The second party id is ignored when it is <c>NA</c> (rock/station
    /// collisions where only the ship party produces a live sound site).
    /// </summary>
    public static IEnumerable<CollisionSoundSource> SelectSoundSources(
        IEnumerable<CollisionEntity> collisions, short? mySector)
    {
        foreach (var c in collisions)
        {
            if (mySector is not { } sec || c.ClusterId != sec) continue;
            yield return new CollisionSoundSource(c.Position, c.Party1Id);
            if (c.Party2Id >= 0)
                yield return new CollisionSoundSource(c.Position, c.Party2Id);
        }
    }

    /// <summary>A positional collision-sound source: impact point plus the
    /// party id it belongs to (used by the mixer to keep one instance per
    /// party, mirroring native's per-party <c>PlaySoundIf</c> instances).</summary>
    public readonly record struct CollisionSoundSource(Vector3 Position, short PartyId);
}

/// <summary>
/// P463c — the pure distance → volume mapping for the collision one-shot. A
/// collision is a world sound played at the impact point, so it attenuates with
/// listener distance the way the other positional cues do
/// (<see cref="HitSoundCurve"/>), and peaks reasonably loud since a crunch you
/// actually feel (own ship) or watch up close (a party) should be prominent.
/// Native's <c>ThreeDSound(100)</c> falloff (<c>collisionSound</c>, sounddef.mdl:308)
/// is not portable to this engine's unit scale, so it follows the same
/// 6000-unit audible-radius convention as every other positional cue here.
/// </summary>
public static class CollisionSoundCurve
{
    /// <summary>Distance (world units) at which a collision reaches silence.
    /// Matches the projectile/missile positional convention
    /// (<see cref="HitSoundCurve.AudibleRadius"/>).</summary>
    public const float AudibleRadius = 6000f;

    /// <summary>Peak volume (0..1) of a collision one-shot at the listener.</summary>
    public const float PeakVolume = 0.9f;

    /// <summary>Linear listener→source falloff in [0,1], full at distance 0 to
    /// silence at <see cref="AudibleRadius"/> (negative/zero → 1, past → 0), the
    /// same shape as the existing positional curves.</summary>
    public static float Falloff(float distance)
    {
        if (distance <= 0f) return 1f;
        if (distance >= AudibleRadius) return 0f;
        return 1f - distance / AudibleRadius;
    }

    /// <summary>Volume (0..<see cref="PeakVolume"/>) for a collision at
    /// <paramref name="distance"/> from the listener.</summary>
    public static float Volume(float distance) => Falloff(distance) * PeakVolume;
}
