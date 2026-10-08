using System;
using Microsoft.Xna.Framework;
using SimOrientation = Allegiance.Core.Sim.Orientation;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P580 — a pure world-space placement for one frame of an external camera
/// mode: the camera's true <see cref="Eye"/> position, the world point it
/// <see cref="LookAt"/>s, and the up vector used to build the view basis.
/// GPU-free and stateless so the native placement rules below are unit-testable
/// headless. Every placement is camera-relative by construction (the caller
/// subtracts <see cref="Eye"/> from each object position, per
/// <see cref="ChaseCamera"/>'s P207 remarks).
/// </summary>
public readonly record struct ExternalView(Vector3 Eye, Vector3 LookAt, Vector3 Up)
{
    /// <summary>Build the camera-relative view matrix from this pose: look FROM the
    /// origin toward (LookAt − Eye) so the caller can draw objects at (x − Eye).</summary>
    public Matrix View() => Matrix.CreateLookAt(Vector3.Zero, LookAt - Eye, Up);
}

/// <summary>
/// P580 — the ported native external camera placements (WinTrek.cpp per-mode
/// switch :7099-7320, helpers TrackCamera :6848-6878). Each method is a pure
/// transcription of the native rule it cites; the Game1 camera switch feeds the
/// live ship/target/warp/station positions in and places the frame each frame.
/// <para>
/// All these modes share native's freely-rotating external camera orientation
/// (<c>m_orientationExternalCamera</c>): a remembered direction the pilot pivots,
/// used as the backward axis for the fixed-offset modes and rotated continuously
/// by <see cref="ExternalOrbit"/> (native's <c>Pitch/Yaw/Roll</c> per-frame
/// deltas). This port has no mouse-look, so the free orientation starts aligned
/// to the ship's backward at mode-entry and is spun by the orbit mode (and by the
/// director's transition smoothing). Roll is carried so an orbit passes "over"
/// the ship naturally, exactly as native does.
/// </para>
/// </summary>
public static class ExternalCameras
{
    // Native default vertical FOV 50° (s_fDefaultFOV, WinTrek.h:26). Fixed-offset
    // modes share the chase camera's FOV/far so switching views keeps scale.
    public const float DefaultFovDegrees = 50f;
    public const float NearPlane = 10f;
    public const float FarPlane = 6000f;

    /// <summary>
    /// Native's min-external-distance rule for the fixed-offset modes
    /// (WinTrek.cpp:7099-7101): <c>5.0 + 2.0 × objectRadius</c>. Used as the floor
    /// for <see cref="GenericExternal"/>/<see cref="Target"/> so a huge object is
    /// never engulfed by a stale small offset.
    /// </summary>
    public static float MinExternalOffset(float objectRadius) => 5f + 2f * objectRadius;

    /// <summary>
    /// <c>cmExternal</c> / <c>cmExternalBase</c> generic external view
    /// (WinTrek.cpp:7108-7116, 7315-7320 default): the camera sits
    /// <paramref name="offset"/> units behind the object along the free external
    /// camera's <paramref name="backward"/> and looks at the object's
    /// <paramref name="centre"/>. Clamps <paramref name="offset"/> up to
    /// <see cref="MinExternalOffset"/>(<paramref name="objectRadius"/>).
    /// </summary>
    public static ExternalView GenericExternal(Vector3 centre, Vector3 backward, float offset, float objectRadius)
    {
        backward = SafeNormalize(backward, -Vector3.UnitZ);
        offset = Math.Max(offset, MinExternalOffset(objectRadius));
        var eye = centre + backward * offset;
        return new ExternalView(eye, centre, WorldUp(backward));
    }

    /// <summary>Native <c>cmExternalOrbit</c> offset (WinTrek.cpp:7196):
    /// <c>5.0 + 2.5 × HullType::Length</c>.</summary>
    public static float OrbitOffset(float hullLength) => 5f + 2.5f * hullLength;

    /// <summary>
    /// <c>cmExternalOrbit</c> placement (WinTrek.cpp:7187-7196): after the free
    /// orientation has been pitched/yawed/rolled by this frame's rates (see
    /// <see cref="RotateOrientationRates"/>), the camera sits
    /// <see cref="OrbitOffset"/> behind the ship along the rotated backward and
    /// looks at the ship.
    /// </summary>
    public static ExternalView Orbit(Vector3 shipCentre, Vector3 backward, float hullLength)
    {
        return GenericExternal(shipCentre, backward, OrbitOffset(hullLength), hullLength * 0.5f);
    }

    /// <summary>Rotate a free external orientation by native's per-second orbit
    /// rates (WinTrek.cpp:7187-7189): <c>Pitch(dt·0.35), Yaw(dt·0.2), Roll(dt·0.5)</c>.
    /// Pure and testable (mutates <paramref name="o"/> and returns it).</summary>
    public static SimOrientation RotateOrbitRates(SimOrientation o, float dt)
    {
        o.Pitch(dt * 0.35f);
        o.Yaw(dt * 0.2f);
        o.Roll(dt * 0.5f);
        return o;
    }

    /// <summary>General per-frame rotation of a free orientation by the given
    /// rad/sec rates (used by the director's gentle drift too).</summary>
    public static SimOrientation RotateRates(SimOrientation o, float dt,
        float pitchRadPerSec, float yawRadPerSec, float rollRadPerSec)
    {
        o.Pitch(dt * pitchRadPerSec);
        o.Yaw(dt * yawRadPerSec);
        o.Roll(dt * rollRadPerSec);
        return o;
    }

    /// <summary>
    /// <c>cmExternalTarget</c> placement (WinTrek.cpp:7099-7111): the camera sits
    /// <c>max(5 + 2 × targetRadius, currentOffset)</c> behind the TARGET along the
    /// free external camera's backward and looks at the target. Requires a current
    /// target (native falls through to <see cref="GenericExternal"/> on the ship
    /// otherwise, WinTrek.cpp:7112).
    /// </summary>
    public static ExternalView Target(Vector3 targetCentre, Vector3 backward, float targetRadius, float offset)
    {
        backward = SafeNormalize(backward, -Vector3.UnitZ);
        offset = Math.Max(offset, MinExternalOffset(targetRadius));
        var eye = targetCentre + backward * offset;
        return new ExternalView(eye, targetCentre, WorldUp(backward));
    }

    /// <summary>
    /// Native's midpoint fly-by distance (WinTrek.cpp:7137-7163): aim at the
    /// midpoint of you and the target, then back off along <paramref name="backward"/>
    /// by <c>1.25 × max(dTarget, dMe)</c>, where each of dTarget/dMe uses native's
    /// (quirky) perpendicular-side measure <c>dSide = sqrt(|Δ|² + dBackward²)</c>
    /// and a 3× side fudge plus the axial term (WinTrek.cpp:7146-7163). Reproduced
    /// verbatim so a passing shot frames identically to native.
    /// </summary>
    public static float FlyByOffset(Vector3 targetPosition, Vector3 myPosition, Vector3 backward)
    {
        backward = SafeNormalize(backward, -Vector3.UnitZ);
        var midpoint = 0.5f * (targetPosition + myPosition);
        float dTarget = BackoffTerm(targetPosition - midpoint, backward);
        float dMe = BackoffTerm(myPosition - midpoint, backward);
        return 1.25f * Math.Max(dTarget, dMe);
    }

    private static float BackoffTerm(Vector3 delta, Vector3 backward)
    {
        float dB = Vector3.Dot(delta, backward);
        // Native: dSide = sqrt(LengthSquared + dBackward*dBackward) — note the
        // PLUS (WinTrek.cpp:7148, 7157), a faithful reproduction, not a typo fix.
        float dSide = MathF.Sqrt(delta.LengthSquared() + dB * dB);
        return dSide * 3f + dB;
    }

    /// <summary>
    /// <c>cmExternalFlyBy</c> placement (WinTrek.cpp:7137-7164): camera looks at
    /// the midpoint of the target and the ship, backed off by
    /// <see cref="FlyByOffset"/>. Degenerates to a chase-like generic external on
    /// the ship when no target is supplied (native WinTrek.cpp:7165-7170).
    /// </summary>
    public static ExternalView FlyBy(Vector3 targetPosition, Vector3 myPosition, Vector3 backward, float sourceRadius, float offset)
    {
        backward = SafeNormalize(backward, -Vector3.UnitZ);
        var midpoint = 0.5f * (targetPosition + myPosition);
        float back = FlyByOffset(targetPosition, myPosition, backward);
        var eye = midpoint + backward * back;
        return new ExternalView(eye, midpoint, WorldUp(backward));
    }

    /// <summary>
    /// Native <c>TrackCamera</c> (WinTrek.cpp:6848-6878): position the eye behind
    /// and to the side of <paramref name="position1"/> looking at
    /// <paramref name="position2"/>, with the eye-offset length
    /// <c>axis·2L + side·0.25L</c>. 'Up' is biased toward world-up (native biases
    /// toward the camera orientation), falling back to world-right when the axis
    /// is ~parallel (WinTrek.cpp:6861-6871). Used by <see cref="Station"/>,
    /// <see cref="Override"/> and the missile view. Stateless: an unstable
    /// degenerate axis falls back to a fixed frame instead of emitting NaN
    /// (native asserts separation &gt; 0).
    /// </summary>
    public static ExternalView Track(Vector3 position1, Vector3 position2, float length)
    {
        Vector3 delta = position2 - position1;
        float separation = delta.Length();
        Vector3 axis;
        if (separation < 1e-5f)
            axis = -Vector3.UnitZ;
        else
            axis = delta / separation;

        Vector3 up = Vector3.Up;
        if (MathF.Abs(Vector3.Dot(up, axis)) > 0.95f)
        {
            up = Vector3.Right;
            if (MathF.Abs(Vector3.Dot(up, axis)) > 0.95f)
                up = Vector3.Forward;
        }
        Vector3 side = Vector3.Cross(axis, up);
        if (side.LengthSquared() < 1e-6f) side = Vector3.Right;
        side.Normalize();

        float L = Math.Max(length, 1f);
        var eye = position1 - axis * (L * 2f) + side * (L * 0.25f);
        return new ExternalView(eye, position2, up);
    }

    /// <summary>Native <c>cmExternalStation</c> offset (WinTrek.cpp:7213):
    /// <c>5.0 + 2.5 × shipRadius</c>.</summary>
    public static float StationOffset(float shipRadius) => 5f + 2.5f * shipRadius;

    /// <summary>
    /// <c>cmExternalStation</c> placement (WinTrek.cpp:7209-7227): TrackCamera from
    /// the ship toward the target station at <see cref="StationOffset"/>(shipRadius).
    /// </summary>
    public static ExternalView Station(Vector3 shipCentre, Vector3 targetCentre, float shipRadius)
        => Track(shipCentre, targetCentre, StationOffset(shipRadius));

    /// <summary>Native <c>cmExternalOverride</c> track length (WinTrek.cpp:7203):
    /// <c>shipRadius × 3</c>.</summary>
    public static float OverrideLength(float shipRadius) => shipRadius * 3f;

    /// <summary>
    /// <c>cmExternalOverride</c> placement (WinTrek.cpp:7199-7206): TrackCamera
    /// from the ship toward the override target at <see cref="OverrideLength"/>.
    /// </summary>
    public static ExternalView Override(Vector3 shipCentre, Vector3 overrideTarget, float shipRadius)
        => Track(shipCentre, overrideTarget, OverrideLength(shipRadius));

    /// <summary>World-up for a view looking generally along <paramref name="backward"/>:
    /// world-up, projected perpendicular; falls back to world-forward when the view is
    /// ~vertical (looking straight up/down).</summary>
    public static Vector3 WorldUp(Vector3 backward)
    {
        var b = SafeNormalize(backward, -Vector3.UnitZ);
        var up = Vector3.Up;
        if (MathF.Abs(Vector3.Dot(up, b)) > 0.98f)
            up = Vector3.Forward;
        var right = Vector3.Cross(b, up);
        if (right.LengthSquared() < 1e-6f) right = Vector3.Right;
        right.Normalize();
        up = Vector3.Cross(right, b);
        up.Normalize();
        return up;
    }

    private static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
    {
        if (v.LengthSquared() < 1e-8f) return fallback;
        v.Normalize();
        return v;
    }
}
