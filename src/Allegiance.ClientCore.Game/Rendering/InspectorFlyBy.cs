using System;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P565 — the fly-by (cinematic pass) camera for the hull inspector: a pure
/// parametric path the camera sweeps along, approaching the hull from a
/// distance, passing close beside/over/under it at a chosen offset, then
/// receding — the classic cinematic "pass" shot (Domingo, 2026-10-06). GPU-free
/// math so it is unit-testable headless.
///
/// <para>Progress <see cref="T"/> runs 0→1 over a pass. The eye walks a straight
/// segment from <c>start</c> to <c>end</c>, both on a line parallel to the
/// travel <paramref name="axis"/> and offset laterally by <see cref="SideOffset"/>
/// in the chosen <see cref="Side"/> direction. In "look at target" mode the eye
/// always aims at the hull's <see cref="Target"/>; in "look ahead" mode it aims
/// <see cref="LookAheadFraction"/> of the way along the path (so distant scenery
/// drifts past — useful for B-roll establishing passes).</para>
///
/// <para>Everything is expressed in the hull's local frame: the hull model sits at
/// <see cref="Target"/> with its nose along the port's forward (−Z). Travel axis
/// default = the hull's forward (−Z), so the default pass runs the length of the
/// ship; pass over/under instead by choosing <see cref="Side"/>. A loop wraps the
/// progress counter modulo 1 so repeated passes are seamless.</para>
/// </summary>
public sealed class InspectorFlyBy
{
    /// <summary>Where to place the camera offset around the travel axis (which flank
    /// the pass hugs the hull). Over/Under offset the eye vertically; Right/Left
    /// horizontally.</summary>
    public enum PassSide { Right, Left, Over, Under }

    /// <summary>How far (world units) the eye starts BEHIND the target along the axis
    /// and ends AHEAD of it — the approach/recede run-up.</summary>
    public float ApproachDistance = 2500f;

    /// <summary>Closest-pass lateral distance from the travel axis to the eye, world units.</summary>
    public float PassOffset = 220f;

    /// <summary>The world point being passed (the hull centre).</summary>
    public Vector3 Target;

    /// <summary>The travel direction (normalized). Default −Z (the port's forward), so
    /// the pass sweeps along the ship's length.</summary>
    public Vector3 Axis = new(0f, 0f, -1f);

    /// <summary>Which flank the pass hugs.</summary>
    public PassSide Side = PassSide.Right;

    /// <summary>Normalized pass progress in [0,1]. Advance with <see cref="Step"/>.</summary>
    public float T;

    /// <summary>Loop the pass: wrap <see cref="T"/> back to 0 after 1 so re-runs are seamless.</summary>
    public bool Loop;

    /// <summary>Look-at-target (true) vs look-ahead (false).</summary>
    public bool LookAtTarget = true;

    /// <summary>When <see cref="LookAtTarget"/> is false, aim this fraction along the path.</summary>
    public float LookAheadFraction = 0.5f;

    /// <summary>Pass duration in seconds for <see cref="Step"/>'s nominal speed.</summary>
    public float DurationSeconds = 6f;

    /// <summary>The lateral offset vector for the current <see cref="Side"/>, in the
    /// hull's frame (unit magnitude).</summary>
    public Vector3 SideOffsetVector => Side switch
    {
        PassSide.Left => new Vector3(-1f, 0f, 0f),
        PassSide.Over => new Vector3(0f, 1f, 0f),
        PassSide.Under => new Vector3(0f, -1f, 0f),
        _ => new Vector3(1f, 0f, 0f),
    };

    /// <summary>Start the pass from the beginning (progress 0).</summary>
    public void Restart() => T = 0f;

    /// <summary>Advance progress by <paramref name="dt"/> seconds at the nominal rate,
    /// wrapping to a loop when <see cref="Loop"/> is on.</summary>
    public void Step(double dt)
    {
        T += (float)(dt / Math.Max(DurationSeconds, 1e-6f));
        if (Loop)
            T -= MathF.Floor(T);
        else
            T = Math.Clamp(T, 0f, 1f);
    }

    /// <summary>The eye position at progress <see cref="T"/>: <c>target ± axis·ApproachDistance
    /// interpolated</c>, offset by <see cref="PassOffset"/> in the side direction.</summary>
    public Vector3 EyeAtT(float t)
    {
        // The travel segment endpoints sit ahead/behind the target along the axis.
        var start = Target - Axis * ApproachDistance;
        var end = Target + Axis * ApproachDistance;
        return Vector3.Lerp(start, end, t) + SideOffsetVector * PassOffset;
    }

    /// <summary>The look-at point at progress <see cref="T"/> (either the target or a
    /// point <see cref="LookAheadFraction"/> along the path).</summary>
    public Vector3 LookAtT(float t)
        => LookAtTarget ? Target : Vector3.Lerp(Target - Axis * ApproachDistance,
                                                 Target + Axis * ApproachDistance, LookAheadFraction);

    /// <summary>The full eye/look/view triplet at the current <see cref="T"/>.</summary>
    public Matrix View(out Vector3 eye, out Vector3 look)
    {
        eye = EyeAtT(T);
        look = LookAtT(T);
        return Matrix.CreateLookAt(eye, look, Vector3.Up);
    }

    /// <summary>Fraction along the path where the closest pass occurs (always 0.5 —
    /// the midpoint is where the camera is abreast of the target).</summary>
    public const float ClosestPassT = 0.5f;
}
