using System;
using Xna = Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P519 → P526 — the hull-preview motion behind the docked HULLS/PARTS panes.
/// The scalar spin math is pure (MonoGame-free, unit-testable); the world
/// rotation is a thin XNA composition on top.
///
/// <para><b>Native reference.</b> The loadout screen's geo pane spins the ship
/// about its UP (Y) axis at <c>1 rad/s</c> and tips it by a fixed <c>π/8</c>
/// about X. <c>LoadoutImpl::CreateGeoPane</c> (WinTrek/load.cpp:877-887) nests
/// <c>TransformGeo(TransformGeo(geo, spinY), tiltX)</c>: the inner
/// <c>AnimateRotateTransform(Vector(0,1,0), Multiply(time, 1.0))</c> is a plain
/// Y-axis spin with angle = time×1.0 (engine/transform.cpp:189-197,
/// <c>SetRotate(axis, angle)</c>), and the outer
/// <c>RotateTransform(Vector(1,0,0), pi/8)</c> is a constant X tilt. The spin is
/// applied first and the tilt second, so in XNA row-vector order the world is
/// <c>RotY(spin) * RotX(π/8)</c>.</para>
///
/// <para>This replaces P519's in-flight <c>cmExternalOrbit</c> camera rates
/// (WinTrek.cpp:7186-7191 — Pitch 0.35 / Yaw 0.2 / Roll 0.5 rad/s), which
/// tumbled the model on all three axes. The loadout spin advances only the
/// single Y angle at 1 rad/s, so pitch and roll structurally never accumulate.</para>
/// </summary>
public static class DockedHullOrbit
{
    /// <summary>Native loadout spin rate about Y (rad/s). The geo's
    /// <c>AnimateRotateTransform</c> multiplies elapsed time by 1.0, so the
    /// spin angle = time × 1.0 rad/s (load.cpp:883-885).</summary>
    public const float SpinRate = 1.0f;

    /// <summary>Native fixed tilt about X: <c>RotateTransform(Vector(1,0,0), pi/8)</c>
    /// (load.cpp:886-887). π/8 ≈ 22.5°. Constant — never accumulated.</summary>
    public const float TiltRadians = (float)(Math.PI / 8.0);

    /// <summary>Advance the accumulated spin angle (radians) by
    /// <paramref name="dt"/> seconds at native's 1 rad/s loadout rate. Feed the
    /// output back in each frame. Only the Y angle spins; pitch and roll have no
    /// accumulator at all (structurally impossible to accumulate).</summary>
    public static float Advance(float spinAngle, double dt) =>
        spinAngle + SpinRate * (float)dt;

    /// <summary>The preview's world rotation for a given spin angle — native's
    /// <c>TransformGeo(TransformGeo(geo, spinY), tiltX)</c> composition: spin
    /// about Y first, then the constant π/8 X tilt (XNA row-vector order
    /// RotY · RotX, so the ship is level-spun and tipped 22.5° toward the camera,
    /// showing its top — the natural hangar view).</summary>
    public static Xna.Matrix World(float spinAngle) =>
        Xna.Matrix.CreateRotationY(spinAngle) * Xna.Matrix.CreateRotationX(TiltRadians);
}
