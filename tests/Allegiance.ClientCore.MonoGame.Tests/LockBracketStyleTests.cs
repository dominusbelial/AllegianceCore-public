using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="LockBracketStyle"/> — the native continuous
/// lock sizing behind the P230/P428 missile-lock flower. No graphics device
/// (same discipline as <see cref="RockMeshFactoryTests"/>). As of P428 the
/// indicator's SHAPE is the real original sprite (see <see cref="LockTextures"/>
/// / <see cref="LockTextureSourceTests"/>), and as of HUD-19 it is placed as
/// four native DrawExpandedBlip quadrants (see <see cref="HudRenderer"/>), so
/// the style only contributes the on-screen half-span radius. The old P230
/// sinusoid pulse (<c>HalfSizeFor</c>), tint (ColorFor) and acquisition spin
/// (RotationFor) are all dead and removed — native has no pulse (radarimage.cpp
/// DrawExpandedBlip), only the continuous radiusBracket + 40·(1−lock) rule.
/// </summary>
public class LockBracketStyleTests
{
    [Fact]
    public void NoLock_radius_is_zero_and_never_drawn()
    {
        // native: if (lock != 0.0f) — a no-lock draws nothing (radarimage.cpp:330).
        Assert.Equal(0f, LockBracketStyle.HalfSizeForFraction(
            MissileLockFeedback.LockState.None, 0f, null, 1000f));
        Assert.Equal(0f, LockBracketStyle.HalfSizeForFraction(
            MissileLockFeedback.LockState.None, 0.6f, 30f, 1000f));
    }

    [Fact]
    public void Full_lock_uses_the_projected_bracket_radius_times_1_05()
    {
        // radarimage.cpp:325-332 — full lock draws the locked surface at
        // radiusBracket = 1.05 × radiusObject (radarimage.cpp:308-319).
        float full = LockBracketStyle.HalfSizeForFraction(
            MissileLockFeedback.LockState.Full, 1f, 34f, 1000f);
        Assert.Equal(34f * 1.05f, full);
        // Null screen radius falls back to the fixed base.
        Assert.Equal(LockBracketStyle.BaseHalfSize,
            LockBracketStyle.HalfSizeForFraction(MissileLockFeedback.LockState.Full, 1f, null, 1000f));
    }

    [Fact]
    public void Partial_lock_grows_with_distance_from_full_lock()
    {
        // Native: partial radius = radiusBracket + 40·(1−lock)
        // (radarimage.cpp:327-333) — the flower shrinks as the lock builds.
        float nearFull = LockBracketStyle.HalfSizeForFraction(
            MissileLockFeedback.LockState.Partial, 0.9f, 34f, 1000f);
        float halfway = LockBracketStyle.HalfSizeForFraction(
            MissileLockFeedback.LockState.Partial, 0.5f, 34f, 1000f);
        float far = LockBracketStyle.HalfSizeForFraction(
            MissileLockFeedback.LockState.Partial, 0.0f, 34f, 1000f);
        Assert.Equal(34f * 1.05f + 40f * 0.1f, nearFull);
        Assert.Equal(34f * 1.05f + 40f * 0.5f, halfway);
        Assert.Equal(34f * 1.05f + 40f * 1.0f, far);
        Assert.True(far > halfway && halfway > nearFull);
    }

    [Fact]
    public void Partial_radius_uses_the_base_when_no_screen_radius()
    {
        Assert.Equal(LockBracketStyle.BaseHalfSize + 40f,
            LockBracketStyle.HalfSizeForFraction(MissileLockFeedback.LockState.Partial, 0f, null, 1000f));
    }

    [Fact]
    public void Radius_clips_to_the_screen_edge_gutter()
    {
        // Native c_flBorderSide gutter (radarimage.cpp:308-319): if the bracket
        // would pass the edge it is clipped to distanceToEdge + BorderSide.
        float clipped = LockBracketStyle.HalfSizeForFraction(
            MissileLockFeedback.LockState.Full, 1f, 100f, 30f);
        Assert.Equal(30f + LockBracketStyle.BorderSide, clipped);
    }

    [Fact]
    public void No_sinusoid_exists_any_more()
    {
        // HUD-19 removed the dead HalfSizeFor pulse (native has none); the only
        // sizing entry point is HalfSizeForFraction. Guard the removal so a
        // future edit cannot quietly reintroduce a pulse the native lacks.
        Assert.Null(typeof(LockBracketStyle).GetMethod("HalfSizeFor"));
        Assert.NotNull(typeof(LockBracketStyle).GetMethod(nameof(LockBracketStyle.HalfSizeForFraction)));
    }
}
