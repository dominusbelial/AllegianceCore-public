using Microsoft.Xna.Framework;
using PureQuat = System.Numerics.Quaternion;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// A fixed chase camera: positioned behind and slightly above the local ship,
/// looking at it. Single fixed offset (no toggle per POC scope). The camera is
/// attached to the ship's <b>full</b> orientation — yaw, pitch <em>and</em>
/// roll — with a brief exponential lag, exactly like native's
/// <c>ComputeExternalChase</c> (see <see cref="Matrices"/>'s remarks for the
/// playtest history and the native citation that settled it). Its "up" is the
/// smoothed/full orientation's own up vector, never a world-up-stabilized one.
/// </summary>
public sealed class ChaseCamera
{
    /// <summary>
    /// P338 — the reference rendered ship radius the fixed
    /// <c>distanceBehind</c>/<c>heightAbove</c> offsets were originally tuned
    /// against: the pre-P333 uniform ~95-unit-radius fallback model (measured
    /// longest dimension ≈ 190 → radius ≈ 95; see <c>WarpRenderer</c>'s remarks).
    /// Post-P333 a ship renders at its real radius <c>0.5 × HullType.Length</c>,
    /// so <see cref="Matrices"/> scales the fixed offsets by
    /// <c>shipRadius / ReferenceRadius</c> — a ship of the old reference radius
    /// frames exactly as it used to, a fighter pulls in close, a capital ship
    /// pulls back, and every class fills the frame the same way. 0/unknown
    /// radius degrades to scale 1 (the old fixed framing) so no call site can
    /// collapse the camera onto the ship.
    /// </summary>
    public const float ReferenceRadius = 95f;

    private readonly float _distanceBehind;
    private readonly float _heightAbove;
    private readonly float _nearPlane;
    private readonly float _farPlane;
    private readonly float _fieldOfView;
    private readonly float _turnSmoothingTau;
    private readonly SmoothedOrientation _smoothedOrientation;

    /// <param name="distanceBehind">How far behind the nose.</param>
    /// <param name="heightAbove">How far above the ship.</param>
    /// <param name="nearPlane">Near clip.</param>
    /// <param name="farPlane">Far clip — chosen so the nearby ship occupies a
    /// healthy slice of the depth range (a huge far with a tiny near crushes
    /// the depth buffer and the geometry vanishes; P207 hit this live).</param>
    /// <param name="fieldOfViewDegrees">Vertical FOV. P449b — Domingo decided to
    /// match native's real default (s_fDefaultFOV = 50°, WinTrek.h:26), so the
    /// default is 50° (the old 75° "wider FOV increases perceived depth/speed"
    /// rationale is superseded).</param>
    /// <param name="turnSmoothingTauSeconds">Exponential time constant the
    /// camera's tracked <b>full orientation</b> lags the ship's real
    /// orientation (including roll) by. At 0 the camera locks onto the ship
    /// perfectly every frame — which, live-tested, made turns feel inert (the
    /// ship never visibly moves within the frame, so a turn reads as nothing
    /// happening until it's already over). A small positive lag lets the ship
    /// visibly swing off-center during a fast maneuver before the camera
    /// catches back up, which is what actually reads as "banking through
    /// space" rather than a rigidly locked camera rig. The lag applies to the
    /// whole rotation (see <see cref="SmoothedOrientation"/>), a documented
    /// simplification of native's turn-rate-sample delay system.</param>
    public ChaseCamera(float distanceBehind, float heightAbove, float nearPlane = 0.5f, float farPlane = 10000f,
        float fieldOfViewDegrees = 50f, float turnSmoothingTauSeconds = 0.18f)
    {
        _distanceBehind = distanceBehind;
        _heightAbove = heightAbove;
        _nearPlane = nearPlane;
        _farPlane = farPlane;
        _fieldOfView = MathHelper.ToRadians(fieldOfViewDegrees);
        _turnSmoothingTau = turnSmoothingTauSeconds;
        _smoothedOrientation = new SmoothedOrientation(turnSmoothingTauSeconds);
    }

    /// <summary>
    /// Produce the view/projection pair for the current frame, PLUS the
    /// camera's true world-space eye position (<paramref name="cameraEye"/>
    /// out value).
    /// </summary>
    /// <remarks>
    /// <para><b>Bug 1 (P207):</b> this sim's world coordinates are routinely in
    /// the hundreds-to-thousands range. Building the view matrix from the
    /// ship's true absolute world position, and each ship's World matrix from
    /// its true absolute position, produced a uniformly BLANK frame on this
    /// machine's GPU/driver — even though every matrix involved was
    /// independently verified non-degenerate. Redrawing the exact same mesh at
    /// the coordinate-space origin instead of its true sim position rendered
    /// correctly, isolating the fault to large-world-coordinate precision loss
    /// in this GPU's vertex pipeline — not a logic bug in this class's math.</para>
    /// <para>
    /// The fix is the standard one for this class of problem
    /// ("camera-relative rendering" / "floating origin"): the view matrix here
    /// always looks FROM the origin, never from the camera's true world
    /// position. Callers must subtract <paramref name="cameraEye"/> from every
    /// object's true world position before building its World matrix (see
    /// <see cref="PoseConverter.ToXnaWorldRelative"/>) — this keeps every value
    /// that reaches the vertex shader small regardless of where the action is
    /// happening in the sim's world, while the relative geometry between camera
    /// and objects — the only thing that actually affects the image — is
    /// mathematically identical to the naive absolute-coordinate version.</para>
    /// <para><b>Bug 2 (post-P207/P208) — itself now known to be WRONG:</b> a
    /// second live-playtest observation reported that deriving BOTH the camera
    /// offset and its "up" from the ship's FULL orientation made the ship's own
    /// roll invisible on screen (the camera rolling with the ship by the same
    /// amount cancels the silhouette's apparent rotation), so roll was stripped
    /// out of the camera's framing — "up" came from world-up via a roll-free
    /// basis, the approach described at the time as "the standard approach
    /// nearly every flight/space game uses." <b>That correction was itself a
    /// misdiagnosis of real-game behavior.</b> Domingo remembered the actual
    /// game directly ("camera should not be fixed to any axis, it rotates when
    /// you do a full loop leaving the ship inverted, like if this was an
    /// airplane game"), and tracing the native chase-camera source this stage
    /// settled it.</para>
    /// <para>
    /// <b>Bug 3 (this task, P240) — the correction:</b> native's
    /// <c>ComputeExternalChase</c> (<c>WinTrek.cpp:6880-6944</c>, the function
    /// behind <c>cmExternalChase</c>) starts from
    /// <c>shipOrientation = pShip-&gt;GetOrientation()</c> — the ship's true,
    /// full orientation <em>including roll</em> — and then applies only a small
    /// time-lagged angular displacement
    /// (<c>orientationBfr = shipOrientation; orientationBfr.Roll(-fRollDisplacement);
    /// orientationBfr.Pitch(...); orientationBfr.Yaw(...)</c>), computed from
    /// recent turn-rate samples. It is never world-up-stabilized: the camera is
    /// rigidly attached to the ship's real orientation with a brief elastic
    /// delay, so a full loop genuinely leaves the camera (and the visible
    /// world) inverted relative to the ship for a moment — exactly what Domingo
    /// remembered. The old live-playtest read "roll invisible" from the ship's
    /// silhouette staying fixed in frame, but in a sparse starfield the roll
    /// was in fact visible through the background/world; the roll-stripping
    /// "fix" threw that away and froze the camera to world-up, which native
    /// never does. This class now implements native's behavior.</para>
    /// <para>
    /// <b>Chosen simplification (stated honestly):</b> native delays the camera
    /// by integrating a genuine historical angular displacement over a ring
    /// buffer of recent per-axis turn-rate samples. This port extends the
    /// class's pre-existing single-value exponential lag to the FULL
    /// orientation — an exponential slerp toward the ship's current full
    /// rotation (see <see cref="SmoothedOrientation"/>) — which is much simpler
    /// but faithful in spirit: a brief, frame-rate-independent elastic delay
    /// that resolves to the camera being attached to the ship's real (rolling)
    /// orientation. The roll itself is never erased, which is the whole point.
    /// The original rationale for the lag still holds unchanged: the lag makes
    /// maneuvers visibly read in frame (ship briefly swinging off-center during
    /// a turn) rather than feeling inert; adding roll back in does not undo
    /// that — a lagged-but-still-present roll is the fix, not a return to zero
    /// smoothing.</para>
    /// </remarks>
    /// <param name="shipPosition">The local ship's true world position.</param>
    /// <param name="shipRotation">The local ship's world rotation (see
    /// <see cref="PoseConverter.ToXnaRotation"/>).</param>
    /// <param name="aspectRatio">Viewport width / height.</param>
    /// <param name="deltaSeconds">Real elapsed time since the last call —
    /// drives the orientation-smoothing lag (see the constructor's
    /// <c>turnSmoothingTauSeconds</c> remarks). Pass 0 (or call once) for an
    /// immediate, unlagged first frame.</param>
    /// <param name="cameraEye">The camera's true world-space eye position —
    /// subtract this from every object's world position before drawing it.</param>
    /// <param name="shipRadius">P338 — the framed ship's REAL rendered radius
    /// (<c>0.5 × HullType.Length</c> post-P333). The fixed offset distances are
    /// scaled by <c>shipRadius / <see cref="ReferenceRadius"/></c> so the whole
    /// ship always frames comfortably regardless of class (see the constant's
    /// remarks). Pass 0 (or omit) to keep the old fixed framing for a
    /// reference-radius ship.</param>
    public (Matrix View, Matrix Projection) Matrices(Vector3 shipPosition, Matrix shipRotation, float aspectRatio, float deltaSeconds, out Vector3 cameraEye, float shipRadius = ReferenceRadius, float headYawRadians = 0f)
    {
        // Convert the ship's full world rotation (a MonoGame Matrix) into the
        // pure quaternion space the smoother operates in. The smoother tracks
        // the FULL orientation — yaw, pitch AND roll — mirroring native
        // ComputeExternalChase starting from pShip->GetOrientation(). See the
        // remarks above for why that (rather than world-up stabilization) is
        // the correct, native-faithful behavior.
        var mgQuat = Quaternion.CreateFromRotationMatrix(shipRotation);
        var pure = new PureQuat(mgQuat.X, mgQuat.Y, mgQuat.Z, mgQuat.W);
        var smoothed = _smoothedOrientation.Update(pure, deltaSeconds);
        var smoothedMg = new Quaternion(smoothed.X, smoothed.Y, smoothed.Z, smoothed.W);

        // Derive the camera basis from the smoothed FULL orientation's own
        // axes — NOT world-up. As the ship (and the lagged camera with it)
        // rolls, "up" rolls too, so a full loop genuinely leaves the camera
        // (and the visible world) inverted relative to the frame's starting
        // orientation — the camera is not fixed to any axis, exactly as
        // Domingo recalled and native does.
        Vector3 forward = Vector3.Transform(Vector3.Forward, smoothedMg);
        Vector3 up = Vector3.Transform(Vector3.Up, smoothedMg);
        forward.Normalize();
        up.Normalize();

        // P338 — P333 made ships render at their real per-hull radius, so the
        // fixed offsets that used to frame a ~95-unit ship now frame a fighter
        // (radius ~25) as a speck and a carrier (radius ~750) as a wall. Scale
        // both offsets proportionally to the framed ship's REAL rendered radius
        // (guarded: 0/unknown → scale 1, the old reference framing).
        float scale = shipRadius > 0f ? shipRadius / ReferenceRadius : 1f;
        float behind = _distanceBehind * scale;
        float above = _heightAbove * scale;

        // Offset direction uses the smoothed full orientation's own up/forward,
        // the same as native positioning the camera along orientationBfr's
        // GetUp()/GetBackward() vectors (the offset magnitudes themselves —
        // distance behind / height above — are out of scope here; P338 only
        // rescales them to the ship's real size).
        cameraEye = shipPosition + up * above + (-forward) * behind;
        Vector3 target = shipPosition;

        // P624 — look-around head yaw (TK_View*): rotate the LOOK direction
        // about the camera's up by the native head yaw, keeping the camera
        // position fixed behind the ship (native's cmExternalChase head-look).
        // Sign matches the cockpit path (positive yaw → look right).
        if (headYawRadians != 0f)
        {
            var look = target - cameraEye;
            look = Vector3.Transform(look, Matrix.CreateFromAxisAngle(up, -headYawRadians));
            target = cameraEye + look;
        }

        // Camera-relative: look FROM the origin toward (target - cameraEye),
        // never from the true world-space cameraEye. See Bug 1 (P207) above.
        var view = Matrix.CreateLookAt(Vector3.Zero, target - cameraEye, up);
        var projection = Matrix.CreatePerspectiveFieldOfView(
            _fieldOfView, aspectRatio, _nearPlane, _farPlane);
        return (view, projection);
    }
}
