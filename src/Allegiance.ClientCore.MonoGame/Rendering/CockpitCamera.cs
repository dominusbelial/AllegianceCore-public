using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// The first-person cockpit camera: the eye sits at the local ship's own
/// position and looks along the ship's real forward direction, using the ship's
/// FULL orientation — including roll. Unlike <see cref="ChaseCamera"/> (which
/// deliberately strips roll out of its framing so a bank reads on screen),
/// being "inside" the cockpit means the horizon must roll with the ship; that is
/// the whole point of the view. Small class on purpose — this does not need its
/// own elaborate rig.
/// </summary>
/// <remarks>
/// Shares the exact camera-relative rendering convention of
/// <see cref="ChaseCamera.Matrices"/> (see its remarks, P207): the view matrix
/// always looks FROM <c>Vector3.Zero</c>, never from the camera's true world
/// position, and callers subtract the returned <paramref name="cameraEye"/> from
/// every object's world position before building its World matrix (via
/// <see cref="PoseConverter.ToXnaWorldRelative"/>). The local ship itself is not
/// drawn at all in this mode (you are inside it), so the near clip never has a
/// chance to intersect its own hull.
/// </remarks>
public sealed class CockpitCamera
{
    private readonly float _nearPlane;
    private readonly float _farPlane;
    private readonly float _fieldOfView;
    private readonly float _forwardOffset;

    /// <param name="nearPlane">Near clip. A small value keeps distant ships
    /// from popping in at the cockpit door; the own hull is never rendered in
    /// this mode so nothing sits between the eye and the near plane.</param>
    /// <param name="farPlane">Far clip — matched to <see cref="ChaseCamera"/>'s
    /// so the layered <see cref="Starfield"/> has the same visible range.</param>
    /// <param name="fieldOfViewDegrees">Vertical FOV, matched to the chase
    /// camera's (75°) so switching views does not change perceived scale.</param>
    /// <param name="forwardOffset">A small local offset pushed along the ship's
    /// forward axis so the eye is just forward of the ship's origin, keeping
    /// the near clip plane clear of the cockpit interior.</param>
    public CockpitCamera(float nearPlane = 1f, float farPlane = 6000f,
        float fieldOfViewDegrees = 75f, float forwardOffset = 14f)
    {
        _nearPlane = nearPlane;
        _farPlane = farPlane;
        _fieldOfView = MathHelper.ToRadians(fieldOfViewDegrees);
        _forwardOffset = forwardOffset;
    }

    /// <summary>
    /// Produce the view/projection pair for the current frame, PLUS the
    /// camera's true world-space eye (<paramref name="cameraEye"/> out value) —
    /// subtract it from every drawn object's world position.
    /// </summary>
    /// <param name="shipPosition">The local ship's true world position.</param>
    /// <param name="shipRotation">The local ship's world rotation, full
    /// orientation INCLUDING roll (see <see cref="PoseConverter.ToXnaRotation"/>).</param>
    /// <param name="aspectRatio">Viewport width / height.</param>
    public (Matrix View, Matrix Projection) Matrices(Vector3 shipPosition, Matrix shipRotation,
        float aspectRatio, out Vector3 cameraEye)
    {
        // Looking direction = the ship's real forward; up = the ship's real up
        // (with roll) — the two together carry the full attitude.
        Vector3 forward = Vector3.Transform(Vector3.Forward, shipRotation);
        forward.Normalize();
        Vector3 up = Vector3.Transform(Vector3.Up, shipRotation);
        up.Normalize();

        cameraEye = shipPosition + forward * _forwardOffset;

        // Camera-relative (P207): look FROM the origin toward (target - eye),
        // never from the true world-space eye. Any positive distance straight
        // ahead is a valid target; the eye-offset subtraction cancels it out of
        // the relative direction, leaving a pure forward view.
        Vector3 lookTarget = shipPosition + forward * 256f;
        var view = Matrix.CreateLookAt(Vector3.Zero, lookTarget - cameraEye, up);
        var projection = Matrix.CreatePerspectiveFieldOfView(
            _fieldOfView, aspectRatio, _nearPlane, _farPlane);
        return (view, projection);
    }
}
