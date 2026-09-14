using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// A fixed chase camera: positioned behind and slightly above the local
/// ship, looking at it. Single fixed offset (no toggle per POC scope).
/// The camera's horizontal swing tracks the ship's yaw/pitch (so it stays
/// behind the nose as the ship turns), but its "up" is <b>not</b> derived
/// from the ship's own (rolling) up vector — see <see cref="Matrices"/>'s
/// remarks for the real, live-playtest-discovered reason.
/// </summary>
public sealed class ChaseCamera
{
    private readonly float _distanceBehind;
    private readonly float _heightAbove;
    private readonly float _nearPlane;
    private readonly float _farPlane;

    /// <param name="distanceBehind">How far behind the nose.</param>
    /// <param name="heightAbove">How far above the ship.</param>
    /// <param name="nearPlane">Near clip.</param>
    /// <param name="farPlane">Far clip — chosen so the nearby ship occupies a
    /// healthy slice of the depth range (a huge far with a tiny near crushes
    /// the depth buffer and the geometry vanishes; P207 hit this live).</param>
    public ChaseCamera(float distanceBehind, float heightAbove, float nearPlane = 0.5f, float farPlane = 10000f)
    {
        _distanceBehind = distanceBehind;
        _heightAbove = heightAbove;
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
    /// <para>
    /// <b>Second real bug found live-playtesting (post-P207/P208):</b> the
    /// original version derived BOTH the camera's offset position AND its
    /// "up" vector from the ship's FULL orientation (<c>Vector3.Transform(_,
    /// shipRotation)</c> for both). That rigidly attaches the camera to the
    /// ship's own rotating frame — as the ship rolls, the camera rolls with
    /// it by the exact same amount, so the ship's silhouette never appears to
    /// rotate on screen at all (confirmed live: real, correct roll commands
    /// were reaching the server and changing the ship's true orientation —
    /// verified via a temporary diagnostic — but were completely invisible
    /// on screen). The fix strips roll out of the camera's own framing: the
    /// offset still swings with the ship's yaw/pitch (so the camera stays
    /// behind the nose as the ship turns, correct chase-cam behavior), but
    /// "up" comes from world-up via a roll-free basis, not the ship's own
    /// (rolling) up row — the standard approach nearly every flight/space
    /// game uses, so a roll now visibly banks the ship within frame instead
    /// of canceling itself out.
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
        // Roll-free basis: keep the ship's forward (yaw/pitch) direction so
        // the camera tracks where the nose is pointing, but derive up/right
        // from world-up instead of the ship's own up row — see remarks above
        // for why using the ship's full rotation (including roll) made roll
        // invisible on screen.
        Vector3 forward = Vector3.Transform(Vector3.Forward, shipRotation);
        forward.Normalize();
        Vector3 right = Vector3.Cross(Vector3.Up, forward);
        if (right.LengthSquared() < 1e-6f)
            right = Vector3.Right;   // ship pointing straight up/down — arbitrary fallback, avoids a degenerate cross product
        right.Normalize();
        Vector3 up = Vector3.Cross(forward, right);
        up.Normalize();

        cameraEye = shipPosition + up * _heightAbove + (-forward) * _distanceBehind;
        Vector3 target = shipPosition;

        // Camera-relative: look FROM the origin toward (target - cameraEye),
        // never from the true world-space cameraEye. See remarks above.
        var view = Matrix.CreateLookAt(Vector3.Zero, target - cameraEye, up);
        var projection = Matrix.CreatePerspectiveFieldOfView(
            MathHelper.PiOver4, aspectRatio, _nearPlane, _farPlane);
        return (view, projection);
    }
}
