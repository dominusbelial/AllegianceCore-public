using System;
using System.Linq;
using Allegiance.Core;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P615 (C6) — the client-side base build-preview orientation (#189): the
/// preview starts at the native default (forward +Z, up +Y, yaw 0 —
/// FedSrv.CPP:11390-11395), right-click rotates 90° clockwise, and docking-door
/// directions follow the frame-forward · orientation rule
/// (stationIGC.cpp:351/419-420). Purely client-side — nothing goes on the wire
/// (native's build order carries no orientation, clintlib.cpp:2239-2265).
/// FAILING-FIRST: the pre-P615 code had no build-preview orientation at all.
/// </summary>
public class BuildPreviewOrientationTests
{
    private static MdlFrame Frame(string name, float fx, float fy, float fz)
        => new() { Name = name, Position = new float[] { 0, 0, 0 }, Forward = new float[] { fx, fy, fz }, Up = new float[] { 0, 1, 0 } };

    /// <summary>Default (yaw 0) is the native default orientation the server
    /// assigns a newly built base (forward +Z, up +Y, yaw 0).</summary>
    [Fact]
    public void Default_orientation_is_identity_native_default()
    {
        var o = new BuildPreviewOrientation();
        Assert.Equal(0f, o.YawDegrees);
        Assert.Equal(Matrix.Identity, o.RotationMatrix);
        Assert.Equal(new Vector3(0f, 0f, 1f), o.TransformDirection(new Vector3(0f, 0f, 1f)));
        Assert.Equal(new Vector3(0f, 1f, 0f), o.TransformDirection(new Vector3(0f, 1f, 0f)));
    }

    /// <summary>Right-click rotates the preview 90° clockwise; forward +Z swings
    /// to +X after one rotation (positive rotation about +Y, viewed from above).</summary>
    [Fact]
    public void Rotate_clockwise_is_90_degrees_and_swings_forward_to_plus_x()
    {
        var o = new BuildPreviewOrientation();
        o.RotateClockwise();
        Assert.Equal(90f, o.YawDegrees);
        var fwd = o.TransformDirection(new Vector3(0f, 0f, 1f));
        Assert.True(Math.Abs(fwd.X - 1f) < 1e-3f, $"forward should swing to +X, got {fwd}");
        Assert.True(Math.Abs(fwd.Z) < 1e-3f, $"forward Z should be ~0, got {fwd}");
    }

    /// <summary>Four clockwise rotations return to the native default (full turn).</summary>
    [Fact]
    public void Four_rotations_return_to_native_default()
    {
        var o = new BuildPreviewOrientation();
        for (int i = 0; i < 4; i++) o.RotateClockwise();
        var fwd = o.TransformDirection(new Vector3(0f, 0f, 1f));
        Assert.True(Math.Abs(fwd.Z - 1f) < 1e-3f, $"after 4 rotates forward should be +Z, got {fwd}");
    }

    /// <summary>Dock-door directions follow the frame-forward · orientation rule:
    /// a 'launch1' frame (forward +Z) points +Z at yaw 0 and +X after one
    /// clockwise rotation.</summary>
    [Fact]
    public void Dock_door_directions_rotate_with_the_preview_yaw()
    {
        var o = new BuildPreviewOrientation();
        var frames = new[] { Frame("launch1", 0f, 0f, 1f), Frame("capgarage1A", 1f, 0f, 0f) };
        var dirs0 = o.DoorDirections(frames);
        Assert.Equal(2, dirs0.Count);
        Assert.True(Math.Abs(dirs0[0].Z - 1f) < 1e-3f);

        o.RotateClockwise();
        var dirs1 = o.DoorDirections(frames);
        Assert.True(Math.Abs(dirs1[0].X - 1f) < 1e-3f, $"after rotate, launch1 fwd should point +X, got {dirs1[0]}");
    }

    /// <summary>Only dock frames are recognised as doors ('launch1',
    /// 'capgarage1A' yes; 'turret1'/'hardpoint' no).</summary>
    [Fact]
    public void IsDockFrame_recognises_launch_and_garage_frames_only()
    {
        Assert.True(BuildPreviewOrientation.IsDockFrame("launch1"));
        Assert.True(BuildPreviewOrientation.IsDockFrame("launch2"));
        Assert.True(BuildPreviewOrientation.IsDockFrame("capgarage1A"));
        Assert.False(BuildPreviewOrientation.IsDockFrame("turret1"));
        Assert.False(BuildPreviewOrientation.IsDockFrame("hardpoint3"));
    }
}
