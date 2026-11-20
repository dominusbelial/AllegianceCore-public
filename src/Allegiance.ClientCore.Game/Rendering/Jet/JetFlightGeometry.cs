using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering.Jet;

/// <summary>
/// P1036 — the Jet attitude snapshot derived from a <see cref="HudFlightReference"/>
/// (P972 §4.2). Every number is presentation-only navigation: pitch and roll are
/// the own ship's attitude relative to the sector reference plane (the artificial
/// XY plane the native command grid uses, <c>WinTrek/WinTrek.cpp:3099</c>), and
/// heading is the own forward projected onto that plane. No altitude, G, Mach or
/// planetary horizon is invented.
/// </summary>
/// <param name="Available">True when a flight reference was supplied and its
/// attitude quaternion was usable.</param>
/// <param name="PitchDegrees">Signed pitch, −90 … +90; positive = the nose is
/// above the sector plane.</param>
/// <param name="RollDegrees">Signed bank about the ship's forward axis, −180 … +180;
/// positive = right wing down. Near the vertical singularity this carries the last
/// valid value (<paramref name="RollValid"/> is then false).</param>
/// <param name="HeadingDegrees">0 … &lt;360, own forward projected onto the plane;
/// near vertical this carries the last valid value
/// (<paramref name="HeadingValid"/> is then false) so the tape never jumps.</param>
/// <param name="HeadingValid">False when the forward is too close to the plane
/// normal for a heading to exist this frame.</param>
/// <param name="RollValid">False when the level reference is degenerate this
/// frame (forward (anti-)parallel to the plane normal).</param>
public readonly record struct JetAttitude(
    bool Available,
    float PitchDegrees,
    float RollDegrees,
    float HeadingDegrees,
    bool HeadingValid,
    bool RollValid);

/// <summary>
/// P1036 — holds the last valid heading/roll across the vertical singularity, so
/// a ship passing through ±90° pitch keeps the previous heading rather than
/// jumping to a random one (P972 §4.2). A mutable struct owned by the renderer;
/// <see cref="Update"/> allocates nothing.
/// </summary>
public struct JetAttitudeState
{
    private float _lastHeadingDegrees;
    private float _lastRollDegrees;
    private bool _haveHeading;
    private bool _haveRoll;

    /// <summary>The attitude after the most recent <see cref="Update"/>.</summary>
    public JetAttitude Current { get; private set; }

    /// <summary>The last heading this state observed as valid, even when the
    /// current frame is at the singularity (exposed for the renderer/tests).</summary>
    public float HeldHeadingDegrees => _lastHeadingDegrees;

    /// <summary>
    /// Resolve the attitude for one frame. Null (no own ship, docked, dead, or
    /// command view without a ship) clears the snapshot without inventing
    /// values. Near ±90° pitch the heading/roll flags go false and the held
    /// values are returned unchanged.
    /// </summary>
    public void Update(HudFlightReference? reference)
    {
        if (reference is not { } flight || !flight.HasAttitude)
        {
            Current = default;
            return;
        }

        JetAttitude raw = JetFlightGeometry.Attitude(flight);
        if (raw.HeadingValid) { _lastHeadingDegrees = raw.HeadingDegrees; _haveHeading = true; }
        if (raw.RollValid) { _lastRollDegrees = raw.RollDegrees; _haveRoll = true; }
        Current = raw with
        {
            HeadingDegrees = raw.HeadingValid ? raw.HeadingDegrees : (_haveHeading ? _lastHeadingDegrees : 0f),
            RollDegrees = raw.RollValid ? raw.RollDegrees : (_haveRoll ? _lastRollDegrees : 0f),
        };
    }
}

/// <summary>
/// P1036 — the pure, allocation-free flight-symbology math for the Jet skin
/// (P972 §4.2, phase 3A). Quaternion-based attitude from
/// <see cref="HudFlightReference"/>, the native velocity-indicator gate
/// (<c>WinTrek/indicator.cpp:128-150</c>) and the ladder/tape projection
/// helpers. No MonoGame graphics, no world access: every input is a value the
/// shared HUD model already carries, so the whole file unit-tests headlessly.
/// </summary>
public static class JetFlightGeometry
{
    /// <summary>cos of the vertical-singularity cone: |forward · plane normal|
    /// at or above this is treated as ±90° pitch (about 1°). Inside the cone a
    /// heading/roll has no stable value.</summary>
    public const float VerticalCosine = 0.99985f;

    public const float RadiansToDegrees = 57.2957795f;

    /// <summary>Ladder rung pitch step (P972 §4.2: rungs every 10°).</summary>
    public const float LadderStepDegrees = 10f;

    /// <summary>The optional 5° refinement rung drawn, unlabelled, at ≥1080p
    /// (P972 §4.2: "5° at ≥ 1080p if it stays uncluttered").</summary>
    public const float LadderFineStepDegrees = 5f;

    /// <summary>Full-size ladder half-span (rungs within ±20° of the current
    /// pitch; ±12° at the 800×480 floor). Was ±40°/±20° (P972 §4.5): at 1080p
    /// (scale 1.5) ±40° spanned 960 of 1080 px, top to bottom of the screen;
    /// ±20° keeps it to ~44 % of the height around the boresight (Domingo,
    /// 2026-11-19: "should be smaller, it should not cross the screen").</summary>
    public const float LadderHalfSpanDegrees = 20f;
    public const float CompactLadderHalfSpanDegrees = 12f;

    /// <summary>The highest pitch a numeric rung may carry; beyond it the
    /// ladder shows an off-scale chevron instead of a rung.</summary>
    public const float LadderLimitDegrees = 80f;

    /// <summary>Ladder pitch scale (px per degree at the reference density).</summary>
    public const float LadderPixelsPerDegree = 8f;

    /// <summary>Speed-tape label spacing (mps).</summary>
    public const float SpeedLabelStep = 40f;
    public const float SpeedTickStep = 20f;
    public const float ThrottleLabelStep = 20f;
    public const float ThrottleTickStep = 10f;
    public const float HeadingTickStep = 10f;
    public const float HeadingLabelStep = 20f;

    /// <summary>Speed-tape scale: the tape shows ±180 mps inside the middle 60 %
    /// of the window, so the same numbers stay readable at 800×480.</summary>
    public static float SpeedTapeScale(float viewportHeight)
        => MathF.Max(0.30f, viewportHeight * 0.30f / 180f);

    /// <summary>Throttle-tape scale: ±60 % inside the middle 60 % of the window
    /// (idle 50 % therefore sits well off-centre from the readout row).</summary>
    public static float ThrottleTapeScale(float viewportHeight)
        => MathF.Max(0.75f, viewportHeight * 0.30f / 60f);

    /// <summary>Heading-tape scale: about 90° of heading across the window.</summary>
    public static float HeadingTapeScale(float viewportWidth)
        => MathF.Max(1.2f, viewportWidth / 240f);

    /// <summary>
    /// Native <c>int speed = (int)(myVelocity.Length() + 0.5f)</c>
    /// (<c>WinTrek/indicator.cpp:131</c>): truncation of the rounded length, so
    /// 0.49 m/s still counts as zero and 0.5 m/s is already moving.
    /// </summary>
    public static int RoundedSpeed(float speed) => (int)(speed + 0.5f);

    /// <summary>
    /// The native velocity-indicator gate (<c>WinTrek/indicator.cpp:132-146</c>):
    /// cockpit camera only (<c>:132</c>), rounded source speed non-zero
    /// (<c>:138</c>), <c>TransformDirectionToImage</c> succeeds (<c>:140</c>) and
    /// the transformed point is inside the view rect (<c>:144</c>). The port
    /// computes the projection and both of those tests in
    /// <c>Game1.Camera.ComputeVelocityMarker</c> (which returns null when the
    /// direction is behind the eye or outside the view), so
    /// <paramref name="projectedInsideView"/> is that result's presence and this
    /// method is the remaining two facts. Kept here so the Jet flight-path marker
    /// and the Classic velocity marker cannot disagree.
    /// </summary>
    public static bool FlightPathMarkerVisible(bool cockpitCamera, float speed, bool projectedInsideView)
        => cockpitCamera && RoundedSpeed(speed) != 0 && projectedInsideView;

    /// <summary>
    /// The throttle-tape percent the shared model's fraction maps to: native
    /// <c>jsValues[c_axisThrottle]*0.5+0.5</c> (WinTrek/WinTrek.cpp:8466) is exactly
    /// <c>(throttle+1)/2</c> in <see cref="HudModel"/> (HudModel.cs:421), so idle
    /// (throttle 0) is 50 %, full forward 100 %, reverse below 50 % and the turret's
    /// forced −1 (also the movement/afterburner override in the model) 100 %/0 %.
    /// </summary>
    public static int ThrottlePercent(float throttle)
        => (int)MathF.Round((Math.Clamp(throttle, -1f, 1f) + 1f) * 0.5f * 100f);

    /// <summary>Normalize a heading into 0 … &lt;360 (presentation only).</summary>
    public static float WrapHeading(float degrees)
    {
        float wrapped = degrees % 360f;
        return wrapped < 0f ? wrapped + 360f : wrapped;
    }

    /// <summary>
    /// Quaternion-based attitude relative to the sector plane. Pitch is
    /// <c>asin(forward · normal)</c>, heading is the planar projection
    /// (0° = world +X, increasing toward +Y), and roll is the signed angle from
    /// the level up axis (the plane normal made perpendicular to forward) to the
    /// ship's up axis about forward — positive when the right wing is down.
    /// </summary>
    public static JetAttitude Attitude(in HudFlightReference reference)
    {
        Vector3 forward = reference.Forward;
        Vector3 up = reference.Up;
        float forwardLength = forward.Length();
        if (forwardLength < 1e-4f || up.LengthSquared() < 1e-6f)
            return default;
        forward /= forwardLength;

        Vector3 normal = reference.SectorPlaneNormal;
        float normalLength = normal.Length();
        if (normalLength < 1e-4f) normal = HudFlightReference.SectorUp;
        else normal /= normalLength;
        up.Normalize();

        float cosine = Math.Clamp(Vector3.Dot(forward, normal), -1f, 1f);
        float pitch = MathF.Asin(cosine) * RadiansToDegrees;

        // Planar projection for the heading. Inside the vertical cone |planar|
        // collapses, so both heading and roll are reported invalid and the state
        // holder keeps the previous valid values.
        Vector3 planar = forward - normal * cosine;
        float planarLength = planar.Length();
        bool vertical = MathF.Abs(cosine) >= VerticalCosine || planarLength < 1e-4f;

        float heading = 0f;
        if (!vertical)
            heading = WrapHeading(MathF.Atan2(planar.Y, planar.X) * RadiansToDegrees);

        // Level up: the plane normal projected perpendicular to forward. Degenerate
        // inside the vertical cone (that is the singularity, not a divide-by-zero).
        Vector3 levelUp = normal - forward * cosine;
        float levelUpLength = levelUp.Length();
        float roll = 0f;
        bool rollValid = !vertical && levelUpLength >= 1e-4f;
        if (rollValid)
        {
            levelUp /= levelUpLength;
            float cosRoll = Math.Clamp(Vector3.Dot(levelUp, up), -1f, 1f);
            float sinRoll = Vector3.Dot(Vector3.Cross(levelUp, up), forward);
            roll = MathF.Atan2(sinRoll, cosRoll) * RadiansToDegrees;
        }

        return new JetAttitude(true, pitch, roll, heading, !vertical, rollValid);
    }

    /// <summary>
    /// Screen offset of a ladder rung from the boresight, in screen pixels
    /// (+x right, +y down). The rung sits <paramref name="pixelsPerDegree"/> ×
    /// (pitch − rungPitch) along the ladder axis rotated by −roll: banking right
    /// rotates the world reference counter-clockwise in the pilot's view.
    /// </summary>
    public static Vector2 LadderOffset(float pitchDegrees, float rungPitchDegrees, float rollDegrees, float pixelsPerDegree)
    {
        float angle = -rollDegrees / RadiansToDegrees;
        // A rung at a LOWER pitch than the nose appears BELOW the boresight
        // (screen +y), hence rung − pitch.
        float distance = (rungPitchDegrees - pitchDegrees) * pixelsPerDegree;
        float cos = MathF.Cos(angle);
        float sin = MathF.Sin(angle);
        // The ladder axis is screen-up (0, −1) rotated by `angle`:
        // (0,−1)·R = (sin, −cos).
        return new Vector2(sin * distance, -cos * distance);
    }

    /// <summary>True when the ladder has run out of rungs above (a rung at
    /// pitch + half-span would be beyond ±90°) — draw an off-scale chevron.</summary>
    public static bool LadderOffScaleTop(float pitchDegrees, float halfSpanDegrees)
        => pitchDegrees + halfSpanDegrees > LadderLimitDegrees;

    /// <summary>True when the ladder has run out of rungs below.</summary>
    public static bool LadderOffScaleBottom(float pitchDegrees, float halfSpanDegrees)
        => pitchDegrees - halfSpanDegrees < -LadderLimitDegrees;

    /// <summary>
    /// P1040c — does the segment <paramref name="a"/>→<paramref name="b"/> cross
    /// <paramref name="box"/>? A Liang–Barsky slab clip: the same test the layout audit uses,
    /// so a rung the renderer withdraws is exactly a rung the audit would have counted as
    /// crossing the shared training callout. Allocation-free.
    /// </summary>
    public static bool SegmentHitsBox(Vector2 a, Vector2 b, RectangleF box)
    {
        if (box.Width <= 0f || box.Height <= 0f) return false;
        float x0 = box.X, x1 = box.X + box.Width, y0 = box.Y, y1 = box.Y + box.Height;
        float dx = b.X - a.X, dy = b.Y - a.Y;
        if (MathF.Abs(dx) < 1e-5f && MathF.Abs(dy) < 1e-5f)
            return a.X >= x0 && a.X <= x1 && a.Y >= y0 && a.Y <= y1;
        float t0 = 0f, t1 = 1f;
        if (!Clip(-dx, a.X - x0, ref t0, ref t1)) return false;
        if (!Clip(dx, x1 - a.X, ref t0, ref t1)) return false;
        if (!Clip(-dy, a.Y - y0, ref t0, ref t1)) return false;
        if (!Clip(dy, y1 - a.Y, ref t0, ref t1)) return false;
        return t0 <= t1;
    }

    private static bool Clip(float p, float q, ref float t0, ref float t1)
    {
        if (MathF.Abs(p) < 1e-6f) return q >= 0f;
        float r = q / p;
        if (p < 0f)
        {
            if (r > t1) return false;
            if (r > t0) t0 = r;
        }
        else
        {
            if (r < t0) return false;
            if (r < t1) t1 = r;
        }
        return true;
    }

    /// <summary>Offset in px of a tape tick relative to the fixed readout row:
    /// (tick − value) × px per unit. Used by both moving tapes.</summary>
    public static float TapeOffset(float value, float tickValue, float pixelsPerUnit)
        => (tickValue - value) * pixelsPerUnit;

    /// <summary>
    /// The furthest tick on a tape that can still be drawn inside
    /// <paramref name="halfSpan"/> px, so the renderer's loops stay bounded and
    /// allocation-free.
    /// </summary>
    public static int TapeTickCount(float pixelsPerUnit, float halfSpanPx, float tickStep)
    {
        if (pixelsPerUnit <= 0f || tickStep <= 0f || halfSpanPx <= 0f) return 0;
        return (int)MathF.Floor(halfSpanPx / (pixelsPerUnit * tickStep));
    }
}
