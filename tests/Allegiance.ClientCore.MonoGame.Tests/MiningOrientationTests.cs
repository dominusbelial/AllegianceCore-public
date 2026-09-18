using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="MiningOrientation"/> (P255 client-side
/// mining facing) — no graphics device involved. Pin the two behaviours that
/// make the render override faithful to native's mining autopilot
/// (<c>shipIGC.cpp:2251-2253</c>): the ship's forward actually points at the
/// rock it is drilling, and the gentle roll-for-effect accumulates over time
/// while leaving that facing (and hence the on-screen orientation cue) intact.
/// </summary>
public class MiningOrientationTests
{
    [Fact]
    public void Facing_AimsForwardAtTheRock()
    {
        var ship = new SimVec(0f, 0f, 0f);
        var rock = new[] { 0f, 0f, 100f };                       // dead ahead on +Z
        var o = MiningOrientation.Facing(Orientation.Identity, ship, rock, 0f);

        var fwd = o.GetForward();
        Assert.Equal(1f, fwd.Length(), 4);
        // Forward must point at the rock (normalized rock direction ≈ (0,0,1)).
        Assert.Equal(0f, fwd.X, 4);
        Assert.Equal(0f, fwd.Y, 4);
        Assert.Equal(1f, fwd.Z, 4);
    }

    [Fact]
    public void Facing_OrientsTowardOffAxisRock()
    {
        // Rock off to the upper-right; forward must align with that direction
        // (not with the ship's original forward).
        var ship = new SimVec(0f, 0f, 0f);
        var rock = new[] { 30f, 40f, 0f };
        var o = MiningOrientation.Facing(Orientation.Identity, ship, rock, 0f);

        var fwd = o.GetForward();
        var expected = new SimVec(30f, 40f, 0f);
        expected /= expected.Length();
        Assert.Equal(expected.X, fwd.X, 4);
        Assert.Equal(expected.Y, fwd.Y, 4);
        Assert.Equal(expected.Z, fwd.Z, 4);
    }

    [Fact]
    public void RollAccumulates_WhileKeepingFacingOnTheRock()
    {
        var ship = new SimVec(0f, 0f, 0f);
        var rock = new[] { 0f, 0f, 100f };

        var still = MiningOrientation.Facing(Orientation.Identity, ship, rock, 0f);      // no roll
        var rolling = MiningOrientation.Facing(Orientation.Identity, ship, rock, 2.0f);  // 0.7 rad roll

        // The forward (the visual facing cue) is unaffected by the roll.
        var fwd = rolling.GetForward();
        Assert.Equal(0f, fwd.X, 4);
        Assert.Equal(1f, fwd.Z, 4);

        // But the ship's body (up/right) has rotated about the long axis — the
        // incremental roll-for-effect accumulates over time.
        var stillUp = still.GetUp();
        var rollingUp = rolling.GetUp();
        Assert.True((stillUp - rollingUp).Length() > 0.1f,
            $"roll should visibly rotate the hull's up over 2s, got diff {(stillUp - rollingUp).Length()}");
    }
}
