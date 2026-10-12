using System;
using Microsoft.Xna.Framework;
using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P612 — pure tests for the free-flight spectator camera (<see cref="FreeCam"/>)
/// integration math: yaw/pitch mouse look, orthonormal basis, and WASD/strafe/
/// up-down movement with the speed modifier and smooth acceleration.
/// </summary>
public class P612FreeCamTests
{
    private static void AssertVec(Vector3 a, Vector3 b, float tol = 1e-3f)
        => Assert.True((a - b).Length() <= tol, $"expected {a} ~ {b}, diff {(a - b).Length()}");

    [Fact]
    public void Default_looks_along_plus_Z_at_level()
    {
        var cam = new FreeCam();
        var b = cam.Basis();
        AssertVec(b.Forward, Vector3.UnitZ);
        Assert.Equal(0f, cam.Pitch, precision: 5);
    }

    [Fact]
    public void Basis_is_orthonormal()
    {
        var cam = new FreeCam();
        cam.Yaw = 0.7f;
        cam.Pitch = 0.3f;
        var (f, r, u) = cam.Basis();
        Assert.Equal(1f, f.Length(), precision: 4);
        Assert.Equal(1f, r.Length(), precision: 4);
        Assert.Equal(1f, u.Length(), precision: 4);
        Assert.Equal(0f, Vector3.Dot(f, r), precision: 4);
        Assert.Equal(0f, Vector3.Dot(f, u), precision: 4);
        Assert.Equal(0f, Vector3.Dot(r, u), precision: 4);
    }

    [Fact]
    public void Look_turns_yaw_right_and_pitch_down_appropriately()
    {
        var cam = new FreeCam();
        cam.Look(100, 0);            // mouse right → yaw increases (turn right)
        Assert.True(cam.Yaw > 0f);
        cam.Look(0, 50);             // mouse down → pitch decreases
        Assert.True(cam.Pitch < 0f);
    }

    [Fact]
    public void Pitch_is_clamped_so_it_never_flips()
    {
        var cam = new FreeCam();
        cam.Look(0, 1000000);        // huge look up
        Assert.True(cam.Pitch > -(float)(Math.PI / 2.0));
        cam.Look(0, -1000000);       // huge look down
        Assert.True(cam.Pitch < (float)(Math.PI / 2.0));
    }

    [Fact]
    public void Move_forward_advances_position_along_forward()
    {
        var cam = new FreeCam { Speed = 100f, Accel = 1000f };   // very stiff accel
        var start = cam.Position;
        cam.Move(1f, 0f, 0f, 0.016f);   // forward 1 step
        var (f, _, _) = cam.Basis();
        var moved = cam.Position - start;
        Assert.True(moved.Length() > 0f, "should have moved forward");
        Assert.True(Vector3.Dot(moved, f) > 0f, "movement should be along forward");
    }

    [Fact]
    public void Speed_modifier_scales_distance_travelled()
    {
        var normal = new FreeCam { Speed = 100f, Accel = 10000f };
        var boosted = new FreeCam { Speed = 100f, Accel = 10000f, SpeedModifier = 4f };
        var p0 = normal.Position;
        var p1 = boosted.Position;
        normal.Move(1f, 0f, 0f, 0.1f);
        boosted.Move(1f, 0f, 0f, 0.1f);
        float dn = (normal.Position - p0).Length();
        float db = (boosted.Position - p1).Length();
        Assert.True(db > dn, $"boosted ({db}) should travel further than normal ({dn})");
    }

    [Fact]
    public void Strafe_and_up_move_perpendicular()
    {
        var cam = new FreeCam { Speed = 100f, Accel = 10000f };
        var start = cam.Position;
        cam.Move(0f, 1f, 0f, 0.1f);   // strafe right
        var right = (cam.Position - start);
        right.Normalize();
        var (f, _, _) = cam.Basis();
        Assert.True(Vector3.Dot(right, f) < 0.1f, "strafe should be ~perpendicular to forward");

        // up/down uses Q/E (the up axis); moving "up" (upAmt>0) raises Y.
        var y0 = cam.Position.Y;
        cam.Move(0f, 0f, 1f, 0.1f);
        Assert.True(cam.Position.Y > y0, "upAmt>0 should raise the camera");
    }

    [Fact]
    public void Teleport_sets_position_and_levels()
    {
        var cam = new FreeCam();
        cam.Yaw = 1f; cam.Pitch = 0.5f;
        cam.Teleport(new Vector3(5f, -3f, 2f));
        AssertVec(cam.Position, new Vector3(5f, -3f, 2f));
        Assert.Equal(0f, cam.Yaw);
        Assert.Equal(0f, cam.Pitch);
    }
}
