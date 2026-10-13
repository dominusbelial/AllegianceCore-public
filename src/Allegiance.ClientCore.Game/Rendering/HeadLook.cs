using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P624 — the look-around camera helper. Native's TK_View* keys set an
/// ABSOLUTE head yaw on the camera control (<c>SetHeadOrientation(yaw)</c>,
/// WinTrek.cpp:302-307), which rotates the ship's orientation by that yaw
/// about its own up axis (<c>currentLookOrientation = headOrientation *
/// shipOrientation</c>, WinTrek.cpp:6982) — and the look keys are HELD: when no
/// look key is pressed the head snaps back to forward (<c>SetHeadOrientation(
/// js.hat)</c> with hat centred = 0, WinTrek.cpp:8445-8462, and
/// <c>SetViewToCombatMode</c> resets to 0 at :9365-9367).
///
/// This class owns the pure angle constants + the yaw rotation so both the
/// cockpit/chase cameras and the input handler agree, and so the math is
/// unit-testable without a graphics device (Matrix is a plain struct).
/// </summary>
/// <remarks>
/// <para><b>Angles.</b> The native head-yaw angles (WinTrek.cpp:9733-9791),
/// positive = look RIGHT when viewed from above the ship (clockwise from
/// forward): Front=0, FrontRight=π/4, Right=π/2, RearRight=3π/4, Rear=π,
/// RearLeft=5π/4, Left=3π/2 (= −π/2), FrontLeft=7π/4 (= −π/4).</para>
/// <para><b>Sign.</b> The port's <c>Orientation.Yaw(+θ)</c> is a LEFT turn
/// (Orientation.cs), and XNA <c>Matrix.CreateRotationY(+θ)</c> likewise turns
/// the forward (−Z) toward −X (left) — both opposite to native's
/// <c>SetHeadOrientation</c>, which negates the stored yaw
/// (<c>m_orientationHead.Yaw(−yaw)</c>). So the rotation applied here is
/// <c>CreateRotationY(−yaw)</c>, which turns forward (+X) at π/2 = look right —
/// the parity check the P624 test pins.</para>
/// </remarks>
public static class HeadLook
{
    public const float FrontLeft = MathHelper.Pi * 1.75f;
    public const float FrontRight = MathHelper.Pi * 0.25f;
    public const float Left = MathHelper.Pi * 1.5f;
    public const float Right = MathHelper.Pi * 0.5f;
    public const float Rear = MathHelper.Pi;
    public const float RearLeft = MathHelper.Pi * 1.25f;
    public const float RearRight = MathHelper.Pi * 0.75f;

    /// <summary>Map a look action name to its native head-yaw angle (0 when the
    /// action is not a look direction — the default forward view).</summary>
    public static float AngleForAction(string action) => action switch
    {
        "ViewFrontLeft" => FrontLeft,
        "ViewFrontRight" => FrontRight,
        "ViewLeft" => Left,
        "ViewRight" => Right,
        "ViewRear" => Rear,
        "ViewRearLeft" => RearLeft,
        "ViewRearRight" => RearRight,
        _ => 0f,
    };

    /// <summary>Rotate an already-XNA ship/look rotation by the head yaw about
    /// the ship's own up axis. Identity at 0 so callers can pass the raw value
    /// unconditionally.</summary>
    public static Matrix RotateByHeadYaw(Matrix shipRotation, float headYawRadians)
        => headYawRadians == 0f ? shipRotation
           : Matrix.CreateRotationY(-headYawRadians) * shipRotation;

    /// <summary>The camera's forward direction after applying the head yaw to a
    /// ship rotation — what the cockpit/chase camera should look along.</summary>
    public static Vector3 LookForward(Matrix shipRotation, float headYawRadians)
    {
        var f = Vector3.Transform(Vector3.Forward, RotateByHeadYaw(shipRotation, headYawRadians));
        f.Normalize();
        return f;
    }
}
