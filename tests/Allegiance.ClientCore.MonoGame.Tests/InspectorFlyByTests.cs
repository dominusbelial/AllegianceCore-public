using System;
using Microsoft.Xna.Framework;
using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>P565 — pure tests for the fly-by (cinematic pass) camera path.</summary>
public class InspectorFlyByTests
{
    private static void AssertVec(Vector3 a, Vector3 b, float tol = 1e-3f)
        => Assert.True((a - b).Length() <= tol, $"expected {a} ~ {b}");

    [Fact]
    public void Start_is_behind_target_end_is_ahead_along_axis()
    {
        var fb = new InspectorFlyBy { Target = Vector3.Zero, Axis = new Vector3(0, 0, -1), ApproachDistance = 1000, PassOffset = 0 };
        AssertVec(fb.EyeAtT(0f), new Vector3(0, 0, 1000));    // behind along -Z axis
        AssertVec(fb.EyeAtT(1f), new Vector3(0, 0, -1000));   // ahead
    }

    [Fact]
    public void Closest_pass_midway_is_at_the_side_offset()
    {
        var fb = new InspectorFlyBy { Target = Vector3.Zero, Axis = new Vector3(0, 0, -1), ApproachDistance = 1000, PassOffset = 200 };
        var mid = fb.EyeAtT(0.5f);
        // at the midpoint the eye is abreast of the target at the lateral offset
        Assert.Equal(200f, mid.X, precision: 3);
        Assert.Equal(0f, mid.Z, precision: 3);
    }

    [Theory]
    [InlineData(InspectorFlyBy.PassSide.Right, 1f, 0f)]
    [InlineData(InspectorFlyBy.PassSide.Left, -1f, 0f)]
    [InlineData(InspectorFlyBy.PassSide.Over, 0f, 1f)]
    [InlineData(InspectorFlyBy.PassSide.Under, 0f, -1f)]
    public void Side_vector_selects_the_flank(InspectorFlyBy.PassSide side, float ex, float ey)
    {
        var fb = new InspectorFlyBy { Side = side };
        var v = fb.SideOffsetVector;
        Assert.Equal(ex, v.X, precision: 5);
        Assert.Equal(ey, v.Y, precision: 5);
    }

    [Fact]
    public void Look_at_target_aims_at_target()
    {
        var fb = new InspectorFlyBy { Target = new Vector3(0, 0, 0), Axis = new Vector3(0, 0, -1) };
        AssertVec(fb.LookAtT(0.4f), Vector3.Zero);
    }

    [Fact]
    public void Look_ahead_aims_down_the_path()
    {
        var fb = new InspectorFlyBy
        {
            Target = Vector3.Zero, Axis = new Vector3(0, 0, -1),
            ApproachDistance = 1000, LookAtTarget = false, LookAheadFraction = 0.5f,
        };
        AssertVec(fb.LookAtT(0.2f), Vector3.Zero);   // midway along the path
    }

    [Fact]
    public void Progress_steps_over_duration_and_clamps_without_loop()
    {
        var fb = new InspectorFlyBy { DurationSeconds = 4f, Loop = false };
        fb.T = 0.5f;
        fb.Step(1.0);     // +0.25s at 4s -> 0.75
        Assert.Equal(0.75f, fb.T, precision: 4);
        fb.T = 0.9f;
        fb.Step(10.0);    // overshoot clamps to 1
        Assert.Equal(1f, fb.T, precision: 4);
    }

    [Fact]
    public void Loop_wraps_progress_modulo_one()
    {
        var fb = new InspectorFlyBy { DurationSeconds = 1f, Loop = true };
        fb.T = 0.8f;
        fb.Step(0.5);     // 1.3 -> wraps to 0.3
        Assert.Equal(0.3f, fb.T, precision: 4);
    }

    [Fact]
    public void Restart_zeroes_progress()
    {
        var fb = new InspectorFlyBy { T = 0.6f };
        fb.Restart();
        Assert.Equal(0f, fb.T);
    }
}
