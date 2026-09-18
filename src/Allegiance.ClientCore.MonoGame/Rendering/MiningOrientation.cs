using Allegiance.Core.Sim;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P255 — pure helper computing the <em>render</em> orientation of a ship while
/// it mines: aim its forward at the rock it is drilling, then add a gentle
/// continuous roll "for effect". This is the port's client-side mirror of
/// native's mining autopilot (<c>shipIGC.cpp:2251-2253</c>: "Keep facing the
/// target, and add a little roll for effect" — the SIM turns the mining ship to
/// face the rock while rolling, and that orientation reaches every observer's
/// client through the normal ship-status broadcast). Here the facing is a pure
/// render override of the OWN ship's pose, applied only while mining; the
/// single-axis <see cref="Orientation.Set(SimVec)"/> always yields a valid
/// orthonormal facing for a nonzero direction, and the roll is applied
/// incrementally so it accumulates across frames while mining.
/// <para>
/// Pure (no MonoGame types, no Game1 state) so it is trivially unit-testable
/// and reusable by an offscreen verification harness — the exact function
/// <see cref="Game1"/> calls when the own ship's per-tick mining target is set.
/// </para>
/// </summary>
public static class MiningOrientation
{
    /// <summary>Slow roll rate (rad/s) applied to a mining ship's rendered
    /// facing "for effect". Gentle on purpose (native sets the roll axis to full
    /// 1.0; this is a modest first-pass echo).</summary>
    public const float RollRateRadPerSec = 0.35f;

    /// <summary>Build the render orientation that faces <paramref name="rockPos"/>
    /// from <paramref name="shipPos"/>, keeping the ship's original
    /// <paramref name="original"/> orientation as the roll/level reference, then
    /// adding the incremental roll-for-effect over <paramref name="dtSeconds"/>.</summary>
    public static Orientation Facing(Orientation original, SimVec shipPos, float[] rockPos, float dtSeconds)
    {
        var toRock = new SimVec(rockPos[0], rockPos[1], rockPos[2]) - shipPos;
        var facing = new Orientation(original);
        facing.Set(toRock);                               // face the rock
        facing.Roll(dtSeconds * RollRateRadPerSec);       // roll for effect
        return facing;
    }
}
