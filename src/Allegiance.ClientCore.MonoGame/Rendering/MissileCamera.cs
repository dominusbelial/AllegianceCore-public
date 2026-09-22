using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P295 — the external missile view: watches a just-fired missile in flight.
/// This is the port of native's <c>cmExternalMissile</c> camera, which each
/// frame tracks the pilot's last-fired missile via <c>TrackCamera</c>
/// (<c>WinTrek.cpp:7237-7268</c>; the helper itself at
/// <c>WinTrek.cpp:6848-6878</c>): the eye is set behind-and-to-the-side of the
/// missile and turned to keep it AND its target — or, in this port's case since
/// the client wire carries no missile target/shooter, the local ship it was
/// fired from — in frame together. When no missile is flying, native falls back
/// to the external chase view (<c>WinTrek.cpp:7259-7268</c>); the port does the
/// same (Game1 hands control to the chase camera).
/// </summary>
/// <remarks>
/// Shares the exact camera-relative rendering convention of
/// <see cref="ChaseCamera.Matrices"/> (see its remarks, P207): the view matrix
/// always looks FROM <c>Vector3.Zero</c>, never from the camera's true world
/// position, and callers subtract the returned <paramref name="cameraEye"/> from
/// every object's world position before building its World matrix (via
/// <see cref="PoseConverter.ToXnaWorldRelative"/>).
/// <para>
/// Framing: native passes a small fixed length (<c>pmissile-&gt;GetRadius()*3</c>)
/// to <c>TrackCamera</c> and relies on the following-frames animation to settle
/// a pleasing shot; this port has no missile radius on the wire (see
/// <see cref="Allegiance.ClientCore.State.MissileEntity"/>), so the eye-offset
/// length is instead scaled to the separation between the missile and the other
/// reference point (<paramref name="framingFactor"/> × separation). That keeps
/// both objects reliably in view at any scale — the concrete outcome native's
/// framing is aiming at — while preserving TrackCamera's exact eye-offset shape
/// (<c>position1 − axis*2L + side*0.25L</c>) and look-at-target behaviour
/// (the camera turns to face the other/reference object, so the missile flies
/// toward or away from it in view).
/// </para>
/// </remarks>
public sealed class MissileCamera
{
    private readonly float _nearPlane;
    private readonly float _farPlane;
    private readonly float _fieldOfView;
    private readonly float _framingFactor;

    /// <param name="nearPlane">Near clip (P340/U1 — reduced to 1 to match the
    /// cockpit camera family, so a just-fired missile — whose eye can sit only
    /// ~3.5 units behind the missile right after launch when separation is
    /// still small — is NOT swallowed by the near plane at the very moment the
    /// player toggles to missile-cam; the old 10 matched the chase camera and
    /// clipped that opening moment).</param>
    /// <param name="farPlane">Far clip (matched to the chase camera family so
    /// the layered <see cref="Starfield"/> has the same visible range).</param>
    /// <param name="fieldOfViewDegrees">Vertical FOV, matched to the chase
    /// camera's (75°) so switching views does not change perceived scale.</param>
    /// <param name="framingFactor">Multiple of the missile↔reference separation
    /// used as TrackCamera's eye-offset length, so both objects stay framed at
    /// any scale (see remarks).</param>
    public MissileCamera(float nearPlane = 1f, float farPlane = 6000f,
        float fieldOfViewDegrees = 75f, float framingFactor = 0.35f)
    {
        _nearPlane = nearPlane;
        _farPlane = farPlane;
        _fieldOfView = MathHelper.ToRadians(fieldOfViewDegrees);
        _framingFactor = framingFactor;
    }

    /// <summary>
    /// Produce the view/projection pair that frames the missile and the other
    /// reference point (the target, or the ship it was fired from) together,
    /// PLUS the camera's true world-space eye (<paramref name="cameraEye"/>) —
    /// subtract it from every drawn object's world position (P207).
    /// </summary>
    /// <param name="missilePosition">The missile's current world position
    /// (<c>TrackCamera</c>'s <c>position1</c> — the object the camera is rooted
    /// to, so it follows the flying missile).</param>
    /// <param name="otherPosition">The other reference point — the object the
    /// camera faces (<c>TrackCamera</c>'s <c>position2</c>; native faces the
    /// missile's target, or the firing ship in the no-target fallback).</param>
    /// <param name="aspectRatio">Viewport width / height.</param>
    public (Matrix View, Matrix Projection) Matrices(Vector3 missilePosition, Vector3 otherPosition,
        float aspectRatio, out Vector3 cameraEye)
    {
        Vector3 delta = otherPosition - missilePosition;
        float separation = delta.Length();

        // Native TrackCamera asserts the two positions differ (position2 -
        // position1 length > 0, WinTrek.cpp:6852). Rather than emit NaN when a
        // caller hands us coincident (or near-coincident) points, pick a stable
        // default axis so the frame stays valid (the two points occupy the same
        // spot visually anyway).
        Vector3 axis;
        if (separation < 1e-5f)
            axis = -Vector3.UnitZ;
        else
            axis = delta / separation;                // from missile toward other

        // Native TrackCamera biases 'up' toward the camera's own up and falls
        // back when the axis is ~parallel to it (WinTrek.cpp:6861-6871). This
        // stateless camera uses world-up, falling back to world-right when the
        // axis is ~parallel to world-up, then derives the side basis the same
        // way (side = cross(axis, up)).
        Vector3 up = Vector3.Up;
        if (Math.Abs(Vector3.Dot(up, axis)) > 0.95f)
            up = Vector3.Right;
        Vector3 side = Vector3.Cross(axis, up);
        side.Normalize();

        // TrackCamera's exact eye placement (WinTrek.cpp:6874): behind the
        // missile along the axis, nudged sideways, looking toward 'other'.
        float length = Math.Max(1f, _framingFactor * separation);
        cameraEye = missilePosition - axis * (length * 2f) + side * (length * 0.25f);

        // Camera-relative (P207): look FROM the origin toward (target - eye),
        // never from the true world-space eye.
        var view = Matrix.CreateLookAt(Vector3.Zero, otherPosition - cameraEye, up);
        var projection = Matrix.CreatePerspectiveFieldOfView(
            _fieldOfView, aspectRatio, _nearPlane, _farPlane);
        return (view, projection);
    }
}
