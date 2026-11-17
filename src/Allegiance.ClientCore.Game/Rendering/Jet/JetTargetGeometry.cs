using Allegiance.ClientCore.State;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering.Jet;

/// <summary>
/// P1037 — the pure, allocation-free target/lock/lead/order geometry of the Jet skin
/// (P972 §4.3 + §4.2, phase 3B). Nothing here touches the world, the sim or a graphics
/// device: every input is a value the shared <see cref="HudModel"/> already carries
/// (<see cref="HudTarget"/>, <see cref="HudLock"/>, <see cref="HudLead"/>,
/// <see cref="HudOrderArrows"/>, <see cref="HudTargetPane"/>), which keeps the whole
/// file unit-testable headlessly.
/// <para><b>No second hue.</b> Relationship, lock and in-range states are carried by
/// shape, line pattern and text (P972 §4.1); the palette stays the one Jet hue
/// (primary / dim / hot).</para>
/// </summary>
public static class JetTargetGeometry
{
    // ── target designator ────────────────────────────────────────────────
    /// <summary>Native <c>radiusBracket = 1.05 × radiusObject</c>
    /// (radarimage.cpp:302-311), the same factor the Classic selection bracket and
    /// lock flower size with (<c>LockBracketStyle.HalfSizeForFraction</c>).</summary>
    public const float RadiusFactor = 1.05f;

    /// <summary>The physical floor of the designator half-span: the four corner
    /// strokes stay legible at the 800×480 window floor.</summary>
    public const float MinimumHalfPx = 12f;

    /// <summary>The physical ceiling: a near hull cannot grow its designator past
    /// this at 4K either (the bracket still frames the object without filling the
    /// viewport).</summary>
    public const float MaximumHalfPx = 150f;

    /// <summary>The established fixed half-span when no projected radius is known
    /// (<c>LockBracketStyle.BaseHalfSize</c>). Scaled by the HUD density.</summary>
    public const float DefaultHalf = 34f;

    /// <summary>
    /// P1039 — the compact (800×480) designator readout's stated name width, in native px
    /// units (× the HUD scale). A long pilot name is truncated with an ASCII ellipsis at
    /// this width so the block fits a placement slot clear of the ladder numerals, <c>CUR</c>
    /// and <c>PITCH</c> at the 800×480 floor (a 72 px name keeps the block's widest row
    /// below the ladder's centre-right numeral column). The fields kept in full are the relationship word, the range,
    /// closure, hull and shield; only the name is shortened, and the compact form keeps all
    /// three rows (no field is dropped).
    /// </summary>
    public const float CompactNameWidth = 72f;

    /// <summary>Native <c>c_flBorderSide</c> / radarimage.cpp:308-319 — the gutter
    /// added when clipping a bracket to the screen edge.</summary>
    public const float EdgeGutter = 10f;

    /// <summary>
    /// Half-span of the four-corner target designator: the projected object radius
    /// × <see cref="RadiusFactor"/>, clamped to
    /// <see cref="MinimumHalfPx"/>…<see cref="MaximumHalfPx"/> physical px, then
    /// clipped to the nearest viewport edge like native's <c>radiusBracket</c>
    /// (radarimage.cpp:308-319). A null/zero projected radius falls back to
    /// <see cref="DefaultHalf"/> × the HUD scale rather than inventing a size from
    /// nothing.
    /// </summary>
    /// <param name="screenRadius">The projected object radius, px (the shared
    /// model's <c>HudTarget.ScreenRadius</c>); null when unknown.</param>
    /// <param name="scale">The HUD density scale for the current frame.</param>
    /// <param name="distanceToEdge">The target centre's distance to the nearest
    /// viewport edge, px.</param>
    public static float DesignatorHalf(float? screenRadius, float scale, float distanceToEdge)
    {
        float half = screenRadius is { } radius && radius > 0f
            ? radius * RadiusFactor
            : DefaultHalf * (scale > 0f ? scale : 1f);
        half = Math.Clamp(half, MinimumHalfPx, MaximumHalfPx);
        float limit = distanceToEdge + EdgeGutter;
        if (half > limit)
            half = MathF.Max(limit, MinimumHalfPx);
        return half;
    }

    /// <summary>How the target's relationship is drawn: by corner line pattern and
    /// word, never by hue (P972 §4.1).</summary>
    public enum TargetRelationship { Neutral, Enemy, Friend }

    /// <summary>Enemy / friend / neutral from the shared model's
    /// <c>IsEnemy</c> flag — null (a non-ship target) is neutral.</summary>
    public static TargetRelationship Relationship(bool? isEnemy) => isEnemy switch
    {
        true => TargetRelationship.Enemy,
        false => TargetRelationship.Friend,
        _ => TargetRelationship.Neutral,
    };

    /// <summary>The relationship word drawn beside the designator.</summary>
    public static string RelationshipWord(TargetRelationship relationship) => relationship switch
    {
        TargetRelationship.Enemy => "HOSTILE",
        TargetRelationship.Friend => "FRIEND",
        _ => "NEUTRAL",
    };

    /// <summary>True when the corner strokes of the designator use the dashed
    /// (open) pattern — the friendly relationship's shape cue.</summary>
    public static bool DashedDesignator(TargetRelationship relationship)
        => relationship == TargetRelationship.Friend;

    /// <summary>P1038 — the clearance kept between the designator's corner strokes and the
    /// lock cue, in standard-stroke units (× scale).</summary>
    public const float LockClearance = 6f;

    /// <summary>√2: the distance from a square's centre to its corner when its half-span
    /// is 1 — the factor that turns the designator's half-span into its corner distance.</summary>
    public const float Sqrt2 = 1.41421356f;

    /// <summary>
    /// P1038c — the native partial-lock closing span: <c>+40·(1−lock)</c>
    /// (radarimage.cpp:325-332). Native enlarges the partial bracket by exactly this many
    /// px as the lock builds and drops the term at full lock, so the cue visibly closes
    /// onto the target. The term is per axis in native; the Jet cue keeps it on the
    /// corner diagonal in the same units, so the same number drives the closing.
    /// </summary>
    public const float LockClosingSpan = 40f;

    /// <summary>
    /// P1038c — distance from the lock centre to one corner arc: the designator's own
    /// corner distance (<c>√2·designatorHalf</c>, so the cue hugs the native-size box)
    /// plus <see cref="LockClearance"/> plus the native
    /// <see cref="LockClosingSpan"/>·<c>(1−lock)</c> term. The designator itself never
    /// shrinks (P1038b rule); only the cue closes.
    /// </summary>
    public static float LockCueCornerDistance(float designatorHalf, float fraction, float scale)
    {
        float s = scale > 0f ? scale : 1f;
        return designatorHalf * Sqrt2 + LockClearance * s
            + LockClosingSpan * s * Math.Clamp(1f - fraction, 0f, 1f);
    }

    /// <summary>P1038c — one corner arc spans 30° (π/6) centred on the designator's
    /// diagonal, so the four arcs hug the corners and leave a 60° gap at each axis
    /// direction (top/right/bottom/left) where the readouts and the heading plate live.
    /// The arc's axis-aligned extent from the centre is therefore
    /// <c>distance·cos(15°)</c> (<see cref="LockCueExtentFraction"/>).</summary>
    public const float LockCornerSweep = MathF.PI / 6f;

    /// <summary>cos 15° — how far the corner arc reaches along an axis.</summary>
    public const float LockCueExtentFraction = 0.96592583f;

    /// <summary>Corner arcs, one per designator diagonal (top-right, top-left,
    /// bottom-left, bottom-right).</summary>
    public const int LockCornerCount = 4;

    /// <summary>Dashes in one corner arc (7.5° each). A partial lock inks every other
    /// dash, marching with the shared clock; a full lock inks the whole 30° arc.</summary>
    public const int LockCornerDashes = 4;

    /// <summary>The axis-aligned extent of the cue from the lock centre: the corner arc
    /// distance projected onto an axis (cos 15°). Readouts are placed outside this.</summary>
    public static float LockCueExtent(float cornerDistance)
        => cornerDistance * LockCueExtentFraction;

    /// <summary>Screen-space angle of corner <paramref name="corner"/>'s diagonal
    /// (45°, 135°, 225°, 315°; +Y down like every Jet screen angle).</summary>
    public static float LockCornerAngle(int corner)
        => MathF.PI * 0.25f + corner * MathF.PI * 0.5f;

    // ── lock ─────────────────────────────────────────────────────────────
    /// <summary>The presentation flash clock of the partial lock: one dash phase per
    /// this many seconds. Driven by the shared feedback time
    /// (<c>HudLock.NowSeconds</c>), never by a renderer-owned timer, and it emits no
    /// audio (the missile lock tone stays with <c>MissileLockFeedback</c> and native
    /// dispatch).</summary>
    public const double LockPhasePeriodSeconds = 0.5;

    /// <summary>The dash phase index of the partial lock at
    /// <paramref name="nowSeconds"/>: alternates 0/1 every
    /// <see cref="LockPhasePeriodSeconds"/>, so the open dashes march around the corner
    /// arcs instead of freezing.</summary>
    public static int LockPhase(double nowSeconds)
        => nowSeconds <= 0d ? 0 : (int)(nowSeconds / LockPhasePeriodSeconds) & 1;

    /// <summary>True when dash <paramref name="index"/> carries ink in this lock state:
    /// nothing for None, every other dash for Partial (marching with the shared clock),
    /// all of them for Full (closed corner arcs).</summary>
    public static bool LockSegmentVisible(int index, int segmentsPerSide,
        MissileLockFeedback.LockState state, double nowSeconds) => state switch
    {
        MissileLockFeedback.LockState.None => false,
        MissileLockFeedback.LockState.Full => true,
        _ => ((index + LockPhase(nowSeconds)) & 1) == 0,
    };


    // ── lead / aim ───────────────────────────────────────────────────────
    /// <summary>A lead marker/open circle radius (physical px at scale 1).</summary>
    public const float LeadRadius = 9f;

    /// <summary>Radii of the aim-predictor's four corner brackets.</summary>
    public const float AimRadius = 14f;

    /// <summary>
    /// The monochrome lead ramp (P972 §4.2): native's green hit-probability ramp
    /// <c>(1−g, g, 0)</c> (indicator.cpp:334-357) becomes a dim → hot interpolation
    /// in the single Jet hue, so a better solution is brighter without a second hue.
    /// </summary>
    public static float Ramp(float green) => Math.Clamp(green, 0f, 1f);

    // ── order steering ───────────────────────────────────────────────────
    /// <summary>Native arrows sit at centre + 108·normalized(target offset)
    /// (indicator.cpp:411-457); the Jet chevrons keep that anchor distance.</summary>
    public const float OrderArrowDistance = 108f;

    /// <summary>A normalized direction from the shared model's arrow vector, or
    /// null when the vector is degenerate (draw nothing rather than guessing).</summary>
    public static Vector2? Direction(HudPoint direction)
    {
        var d = new Vector2(direction.X, direction.Y);
        if (d.LengthSquared() < 1e-6f) return null;
        d.Normalize();
        return d;
    }

    /// <summary>The anchor of an order chevron: centre + direction × distance.</summary>
    public static Vector2 OrderAnchor(Vector2 center, Vector2 direction, float scale)
        => center + direction * (OrderArrowDistance * (scale > 0f ? scale : 1f));

    // ── off-screen target ────────────────────────────────────────────────
    /// <summary>Inset (px) the edge chevron keeps from the viewport border so its
    /// strokes are never clipped.</summary>
    public const float EdgeChevronInset = 26f;

    /// <summary>
    /// Where the off-screen target chevron sits: the same normalized screen-space
    /// bearing <c>Game1.ComputeTargetBearing</c> supplies for the Classic bearing
    /// arrow (<see cref="HudContext.TargetBearing"/>), intersected with the viewport
    /// rectangle inset by <see cref="EdgeChevronInset"/> × scale. A degenerate
    /// direction falls back to straight ahead (up the screen) exactly like the shared
    /// projection does (Game1.Camera.cs:660).
    /// </summary>
    public static Vector2 EdgeChevronAnchor(HudPoint bearing, float width, float height, float scale)
    {
        var d = new Vector2(bearing.X, bearing.Y);
        if (d.LengthSquared() < 1e-6f) d = new(0f, -1f);
        d.Normalize();
        float inset = EdgeChevronInset * (scale > 0f ? scale : 1f);
        float halfW = MathF.Max(1f, width * 0.5f - inset);
        float halfH = MathF.Max(1f, height * 0.5f - inset);
        float tx = MathF.Abs(d.X) > 1e-6f ? halfW / MathF.Abs(d.X) : float.MaxValue;
        float ty = MathF.Abs(d.Y) > 1e-6f ? halfH / MathF.Abs(d.Y) : float.MaxValue;
        float t = MathF.Min(tx, ty);
        return new(width * 0.5f + d.X * t, height * 0.5f + d.Y * t);
    }
}
