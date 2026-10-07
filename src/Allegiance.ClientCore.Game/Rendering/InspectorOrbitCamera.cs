using System;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P565 — the offline hull-inspector's mouse orbit / zoom / pan camera. Pure
/// (no <see cref="GraphicsDevice"/>) spherical-coordinate rig around a world
/// <see cref="Target"/> point, so it is unit-testable headless exactly like
/// <see cref="ChaseCamera"/> / <see cref="MissileCamera"/>.
///
/// <para>Convention: the eye sits <see cref="Distance"/> from <see cref="Target"/>
/// at azimuth <see cref="Yaw"/> (about +Y) and elevation <see cref="Pitch"/>
/// (above/below the target plane). Left-drag orbits (yaw/pitch), right- or
/// middle-drag pans the target in the camera's view plane, the wheel zooms
/// multiplicatively, double-click focuses a point, a reset restores the default
/// framing, and an optional slow auto-orbit advances yaw over time (native's
/// hangar preview spin, <see cref="DockedHullOrbit"/>, as a camera motion).</para>
///
/// <para>The rig is intentionally separate from <see cref="ChaseCamera"/> /
/// <see cref="CockpitCamera"/> (which are follower cameras tied to the sim's own
/// pose): the inspector owns a stationary target and needs free 6-DOF review, not
/// a chase. <see cref="Reset(float,float)"/> restores native preview framing by
/// sitting the eye at <c>(0,0,distance)</c> level with the target.</para>
/// </summary>
public sealed class InspectorOrbitCamera
{
    /// <summary>Azimuth about +Y, radians (0 = camera on the +Z axis at the target's
    /// level). Positive yaw moves the camera around the target toward -X.</summary>
    public float Yaw;

    /// <summary>Elevation above the target's horizontal plane, radians. Clamped to
    /// &lt; ±π/2 so the eye never crosses the poles (a gimbal flip would invert the
    /// up vector).</summary>
    public float Pitch;

    /// <summary>Distance from the eye to <see cref="Target"/>, world units.
    /// Wheel-zoomed between <see cref="MinDistance"/> and <see cref="MaxDistance"/>.</summary>
    public float Distance;

    /// <summary>The world point the camera orbits / the fly-by tracks.</summary>
    public Vector3 Target;

    /// <summary>Slow auto-orbit: advance <see cref="Yaw"/> by
    /// <see cref="AutoOrbitSpeedRadPerSec"/> every <see cref="Step"/> when on.</summary>
    public bool AutoOrbit;

    /// <summary>Native hangar preview spin rate (1 rad/s, DockedHullOrbit.SpinRate)
    /// — the familiar review motion, exposed as the auto-orbit speed.</summary>
    public float AutoOrbitSpeedRadPerSec = DockedHullOrbit.SpinRate;

    /// <summary>Orbit sensitivity: radians of azimuth/elevation per drag pixel.</summary>
    public const float OrbitPerPixel = 0.0075f;

    /// <summary>Pan sensitivity: fraction of the current <see cref="Distance"/> moved
    /// across the camera's view plane per drag pixel. Scaling by distance keeps pan
    /// proportional to apparent zoom (you pan slowly when zoomed in tight).</summary>
    public const float PanPerPixelFraction = 0.004f;

    /// <summary>Wheel-zoom growth base: each notch multiplies <see cref="Distance"/>
    /// by <c>pow(ZoomBase, -ticks)</c>, so +1 notch zooms IN (closer).</summary>
    public const float ZoomBase = 1.055f;

    public float MinDistance = 0.4f;
    public float MaxDistance = 80000f;

    public InspectorOrbitCamera()
    {
        Distance = 750f;
    }

    /// <summary>Left-drag orbit: <paramref name="dx"/>/<paramref name="dy"/> drag deltas
    /// in pixels. Drag right (+dx) moves the camera to the right around the target
    /// (yaw decreases); drag up (+dy) raises the elevation. Clamps pitch inside ±π/2.</summary>
    public void Orbit(int dx, int dy)
    {
        Yaw -= dx * OrbitPerPixel;
        Pitch = Math.Clamp(Pitch + dy * OrbitPerPixel,
            -(float)(Math.PI / 2.0) + 0.012f, (float)(Math.PI / 2.0) - 0.012f);
    }

    /// <summary>Right-/middle-drag pan: shift <see cref="Target"/> across the camera's
    /// view plane (right = +X in the eye frame, up = +Y) by a distance proportional to
    /// <see cref="Distance"/> and the pixel drag. Scaled so panning feels uniform at
    /// any zoom.</summary>
    public void Pan(int dx, int dy)
    {
        var basis = Basis();
        float k = PanPerPixelFraction * Distance;
        // screen-up maps to -world-up of the eye frame for a y-down NDC: dragging
        // DOWN (dy&gt;0) should bring the world point toward the viewer's cursor, i.e.
        // move the target down in the camera frame → positive world-up offset.
        Target += basis.Right * (-dx * k) + basis.Up * (dy * k);
    }

    /// <summary>Wheel-zoom. Positive <paramref name="ticks"/> = scroll up = zoom IN
    /// (distance shrinks). Multiplicative so a full notch halves/roughly scales cleanly.</summary>
    public void Zoom(int ticks)
    {
        if (ticks == 0) return;
        Distance *= MathF.Pow(ZoomBase, -ticks);
        Distance = Math.Clamp(Distance, MinDistance, MaxDistance);
    }

    /// <summary>Double-click focus: repoint the rig at <paramref name="point"/> without
    /// moving the eye's direction otherwise (the orbit then revolves around the new point).</summary>
    public void Focus(Vector3 point) => Target = point;

    /// <summary>Reset to native preview framing: level (<see cref="Yaw"/>=<see cref="Pitch"/>=0),
    /// target at origin, eye <paramref name="distance"/> back on +Z. Equivalent to
    /// <see cref="DockedHullPreviewFraming"/>'s eye-on-axis view.</summary>
    public void Reset(float distance)
    {
        Yaw = 0f;
        Pitch = 0f;
        Target = Vector3.Zero;
        Distance = Math.Clamp(distance > 0f ? distance : 750f, MinDistance, MaxDistance);
        AutoOrbit = false;
    }

    /// <summary>Advance auto-orbit + a little of the fly-by-free review by elapsed seconds.</summary>
    public void Step(double dt)
    {
        if (AutoOrbit)
            Yaw += AutoOrbitSpeedRadPerSec * (float)dt;
    }

    /// <summary>Frame the rig so a sphere of <paramref name="radius"/> at the target is
    /// comfortably in view under a vertical FOV of <paramref name="fovRadians"/>, and level it.</summary>
    public void FrameRadius(float radius, float fovRadians)
    {
        float halfTan = MathF.Tan(fovRadians * 0.5f);
        float dist = radius / MathF.Max(halfTan, 0.005f) * 1.35f;
        Reset(dist);
    }

    /// <summary>The decomposed basis (eye, up, right, forward) derived from the current
    /// yaw/pitch/distance around <see cref="Target"/>. Pure, so tests can assert geometry.</summary>
    public (Vector3 Eye, Vector3 Up, Vector3 Right, Vector3 Forward) Basis()
    {
        float cp = MathF.Cos(Pitch);
        var eye = new Vector3(
            Target.X + Distance * cp * MathF.Sin(Yaw),
            Target.Y + Distance * MathF.Sin(Pitch),
            Target.Z + Distance * cp * MathF.Cos(Yaw));
        var fwd = Vector3.Normalize(Target - eye);
        var right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.Up));
        var up = Vector3.Cross(right, fwd);
        return (eye, up, right, fwd);
    }

    /// <summary>The view matrix for this rig (eye looking at <see cref="Target"/>).</summary>
    public Matrix View()
    {
        var b = Basis();
        return Matrix.CreateLookAt(b.Eye, Target, Vector3.Up);
    }

    /// <summary>Convenience: the eye position (so callers can pass cameraEye to the
    /// particle renderers without re-deriving it).</summary>
    public Vector3 Eye => Basis().Eye;
}
