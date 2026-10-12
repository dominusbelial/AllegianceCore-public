using System;
using System.Collections.Generic;
using Allegiance.Core;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P615 — client-side base build-preview orientation (#189). When the cursor is
/// the "build on" cursor over a rock (ordering a constructor to build), the
/// client shows a ghost of the yet-to-be-built base; RIGHT-CLICK rotates the
/// preview 90° clockwise (full 3D orientation later). This is PURELY CLIENT-SIDE:
/// native's build order (FM_CS_ORDER_CHANGE 93 / c_cidBuild) carries NO
/// orientation field (clintlib.cpp:2239-2265) and the server derives the base's
/// orientation at build completion, hard-coding forward=(0,0,1), up=(0,1,0),
/// yaw 0 (FedSrv.CPP:11390-11395). So the preview starts from that native
/// DEFAULT (identity rotation) and the player's rotation is a local visual
/// choice that is NOT sent on the wire (the server resets it anyway).
/// Everything here is pure math, unit-testable headless.
/// </summary>
public sealed class BuildPreviewOrientation
{
    /// <summary>Client-side preview yaw in degrees. 0 = native default
    /// (forward +Z, up +Y). Right-click rotates 90° clockwise.</summary>
    public float YawDegrees { get; private set; }

    /// <summary>Rotate the preview 90° clockwise (as viewed from above, +Y).
    /// Clockwise = a positive rotation about the +Y axis in this right-handed
    /// frame (forward +Z swings toward −X). Client-side only; never sent to the
    /// server (native's build order carries no orientation, and the server
    /// resets it to forward +Z at build completion anyway, FedSrv.CPP:11390-11395).</summary>
    public void RotateClockwise() => YawDegrees = (YawDegrees + 90f) % 360f;

    /// <summary>Reset to the native default (forward +Z, up +Y, yaw 0).</summary>
    public void Reset() => YawDegrees = 0f;

    /// <summary>The preview rotation matrix (identity at yaw 0 = the native
    /// default orientation the server assigns at build completion,
    /// FedSrv.CPP:11390-11395).</summary>
    public Matrix RotationMatrix =>
        Matrix.CreateRotationY(MathHelper.ToRadians(YawDegrees));

    /// <summary>Transform a model-space direction into build-preview world
    /// space under the current yaw (native transforms door directions by the
    /// station orientation, stationIGC.cpp:351/419-420).</summary>
    public Vector3 TransformDirection(in Vector3 dir)
        => Vector3.TransformNormal(dir, RotationMatrix);

    /// <summary>
    /// A docking-door frame name: native's docking doors are named mount FRAMES
    /// in a station .mdl (launch1..N, capgarage1A..), read at station-type load
    /// via HitTest::Load/GetFrame (stationtypeigc.cpp:40-94); the door direction
    /// is that frame's forward transformed by the station orientation
    /// (stationIGC.cpp:351/419-420).
    /// </summary>
    public static bool IsDockFrame(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var n = name.ToLowerInvariant();
        if (n.StartsWith("launch", StringComparison.Ordinal)) return true;
        if (n.Contains("garage", StringComparison.Ordinal)) return true;
        if (n.Contains("bay", StringComparison.Ordinal)) return true;
        if (n.Contains("dock", StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// Compute the world-space docking-door directions for a station model under
    /// the current preview yaw: each dock frame's forward (from the .mdl FrameData,
    /// <see cref="MdlFrame"/>) rotated by <see cref="RotationMatrix"/> — the native
    /// "door direction = frame forward · station orientation" rule
    /// (stationIGC.cpp:351/419-420). Pure; <paramref name="frames"/> is a
    /// station's parsed frame list. The caller draws door markers along each.
    /// </summary>
    public IReadOnlyList<Vector3> DoorDirections(IReadOnlyList<MdlFrame> frames)
    {
        var dirs = new List<Vector3>();
        if (frames is null) return dirs;
        foreach (var f in frames)
        {
            if (!IsDockFrame(f.Name)) continue;
            var fwd = new Vector3(f.Forward[0], f.Forward[1], f.Forward[2]);
            dirs.Add(TransformDirection(fwd));
        }
        return dirs;
    }
}
