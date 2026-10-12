using System;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P612 — a free-flight spectator camera: a detached 6-DOF rig with yaw/pitch
/// mouse look and WASD/strafe/up-down movement through the world, entirely
/// independent of the pilot's ship. It is a pure position + orientation model
/// (no <see cref="GraphicsDevice"/>) so it is unit-testable headless exactly like
/// <see cref="ChaseCamera"/> / <see cref="InspectorOrbitCamera"/>.
/// <para>
/// Convention: <see cref="Position"/> is the eye; <see cref="Yaw"/> rotates about
/// +Y (0 = looking along +Z) and <see cref="Pitch"/> tilts up/down (clamped to
/// ±π/2 − ε so the up vector never degenerates). <see cref="Move"/> integrates
/// input deltas (forward/right/up amounts) scaled by <see cref="Speed"/>;
/// <see cref="Look"/> applies mouse deltas (px) to yaw/pitch.
/// </para>
/// </summary>
public sealed class FreeCam
{
    /// <summary>Eye position in world space.</summary>
    public Vector3 Position;

    /// <summary>Azimuth about +Y, radians. 0 = looking along +Z (like the
    /// inspector orbit). Positive yaw turns toward +X (camera spins right).</summary>
    public float Yaw;

    /// <summary>Elevation above the horizontal plane, radians. Clamped so the
    /// camera never points straight up/down (gimbal stability).</summary>
    public float Pitch;

    /// <summary>Movement speed (world units/sec) at the default speed factor.</summary>
    public float Speed = 900f;

    /// <summary>Multiplier applied by the speed-modifier control (e.g. Shift).
    /// 1.0 = no modifier.</summary>
    public float SpeedModifier = 1f;

    /// <summary>Mouse-look sensitivity: radians of yaw per pixel.</summary>
    public const float LookPerPixel = 0.0022f;

    /// <summary>Acceleration coupling (how quickly the rig reaches commanded
    /// velocity on axis changes) — smooths out instantaneous key presses.</summary>
    public float Accel = 10f;

    // internal velocity for smooth acceleration.
    private Vector3 _velocity;

    /// <summary>Apply a mouse delta (px) to the look direction. Positive
    /// <paramref name="dx"/> (mouse right) turns the camera right; positive
    /// <paramref name="dy"/> (mouse down) pitches down.</summary>
    public void Look(int dx, int dy)
    {
        Yaw += dx * LookPerPixel;
        Pitch = Math.Clamp(Pitch - dy * LookPerPixel,
            -(float)(Math.PI / 2.0) + 0.015f, (float)(Math.PI / 2.0) - 0.015f);
    }

    /// <summary>The orthonormal basis derived from yaw/pitch.</summary>
    public (Vector3 Forward, Vector3 Right, Vector3 Up) Basis()
    {
        float cp = MathF.Cos(Pitch);
        var fwd = new Vector3(cp * MathF.Sin(Yaw), MathF.Sin(Pitch), cp * MathF.Cos(Yaw));
        fwd.Normalize();
        var right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.Up));
        var up = Vector3.Cross(right, fwd);
        up.Normalize();
        return (fwd, right, up);
    }

    /// <summary>
    /// Move by axis inputs for <paramref name="dt"/> seconds: <paramref name="forwardAmt"/>/
    /// <paramref name="rightAmt"/> (typically −1..1 from WASD/A-D) and
    /// <paramref name="upAmt"/> (Q/E). Velocity is accelerated toward the commanded
    /// direction*speed so camera motion ramps smoothly instead of stepping, then
    /// position advances by the current velocity. Returns whether the position changed.
    /// </summary>
    public bool Move(float forwardAmt, float rightAmt, float upAmt, float dt)
    {
        var (fwd, right, up) = Basis();
        float speed = Speed * SpeedModifier;
        var targetVel = (fwd * forwardAmt + right * rightAmt + up * upAmt) * speed;
        float k = 1f - MathF.Exp(-Accel * dt);
        _velocity = _velocity + (targetVel - _velocity) * k;
        var disp = _velocity * dt;
        if (disp.LengthSquared() < 1e-10f) return false;
        Position += disp;
        return true;
    }

    /// <summary>Jump the camera to <paramref name="p"/> and level it (yaw/pitch = 0).</summary>
    public void Teleport(Vector3 p)
    {
        Position = p;
        Yaw = 0f;
        Pitch = 0f;
        _velocity = Vector3.Zero;
    }

    /// <summary>View matrix for the rig (eye looking along the yaw/pitch forward).</summary>
    public Matrix View()
    {
        var b = Basis();
        return Matrix.CreateLookAt(Position, Position + b.Forward, b.Up);
    }

    /// <summary>Reset motion state (called when the camera is released).</summary>
    public void ResetVelocity() => _velocity = Vector3.Zero;
}
