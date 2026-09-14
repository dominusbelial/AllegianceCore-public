using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// A fixed chase camera: positioned behind and slightly above the local ship,
/// looking at it, banking with its orientation. Single fixed offset (no
/// toggle per POC scope). The offset is expressed in ship-local space — the
/// +Z (backward) and +Y (up) locals — and rotated into world space by the
/// ship's attitude each frame.
/// </summary>
public sealed class ChaseCamera
{
    private readonly Vector3 _localOffset;
    private readonly float _nearPlane;
    private readonly float _farPlane;

    /// <param name="distanceBehind">How far behind the nose (ship-local +Z / backward).</param>
    /// <param name="heightAbove">How far above the ship (ship-local +Y / up).</param>
    /// <param name="nearPlane">Near clip.</param>
    /// <param name="farPlane">Far clip — chosen so the nearby ship occupies a
    /// healthy slice of the depth range (a huge far with a tiny near crushes
    /// the depth buffer and the geometry vanishes; P207 hit this live).</param>
    public ChaseCamera(float distanceBehind, float heightAbove, float nearPlane = 0.5f, float farPlane = 10000f)
    {
        _localOffset = new Vector3(0f, heightAbove, distanceBehind);
        _nearPlane = nearPlane;
        _farPlane = farPlane;
    }

    /// <summary>
    /// Produce the view/projection pair for the current frame, PLUS the
    /// camera's true world-space eye position (<paramref name="cameraEye"/>
    /// out value).
    /// </summary>
    /// <remarks>
    /// <b>Real bug found and fixed (P207):</b> this sim's world coordinates
    /// are routinely in the hundreds-to-thousands range. Building the view
    /// matrix from the ship's true absolute world position, and each ship's
    /// World matrix from its true absolute position, produced a uniformly
    /// BLANK frame on this machine's GPU/driver — even though every matrix
    /// involved was independently verified non-degenerate (valid rotation,
    /// valid perspective projection, ship's centre projected to a valid
    /// on-screen NDC coordinate). Redrawing the exact same mesh at the
    /// coordinate-space origin instead of its true sim position rendered
    /// correctly, isolating the fault to large-world-coordinate precision
    /// loss somewhere in this GPU's vertex pipeline — not a logic bug in
    /// this class's math.
    /// <para>
    /// The fix is the standard one for this class of problem
    /// ("camera-relative rendering" / "floating origin"): the view matrix
    /// here always looks FROM the origin, never from the camera's true
    /// world position. Callers must subtract <paramref name="cameraEye"/>
    /// from every object's true world position before building its World
    /// matrix (see <see cref="PoseConverter.ToXnaWorldRelative"/>) — this
    /// keeps every value that reaches the vertex shader small regardless of
    /// where the action is happening in the sim's world, while the
    /// relative geometry between camera and objects — the only thing that
    /// actually affects the image — is mathematically identical to the
    /// naive absolute-coordinate version.
    /// </para>
    /// </remarks>
    /// <param name="shipPosition">The local ship's true world position.</param>
    /// <param name="shipRotation">The local ship's world rotation (see
    /// <see cref="PoseConverter.ToXnaRotation"/>).</param>
    /// <param name="aspectRatio">Viewport width / height.</param>
    /// <param name="cameraEye">The camera's true world-space eye position —
    /// subtract this from every object's world position before drawing it.</param>
    public (Matrix View, Matrix Projection) Matrices(Vector3 shipPosition, Matrix shipRotation, float aspectRatio, out Vector3 cameraEye)
    {
        // Rotate the ship-local camera offset into world space (row-vector
        // convention: offset * rotation).
        cameraEye = shipPosition + Vector3.Transform(_localOffset, shipRotation);
        Vector3 target = shipPosition;
        Vector3 up = Vector3.Transform(Vector3.Up, shipRotation);

        // Camera-relative: look FROM the origin toward (target - cameraEye),
        // never from the true world-space cameraEye. See remarks above.
        var view = Matrix.CreateLookAt(Vector3.Zero, target - cameraEye, up);
        var projection = Matrix.CreatePerspectiveFieldOfView(
            MathHelper.PiOver4, aspectRatio, _nearPlane, _farPlane);
        return (view, projection);
    }
}
