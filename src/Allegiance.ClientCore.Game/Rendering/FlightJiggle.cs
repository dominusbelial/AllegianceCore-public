using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P449b — the native camera "jiggle": a decaying accumulator that shakes the
/// in-flight camera orientation under the afterburner and on impacts. Faithful
/// port of <c>CameraImpl::Apply</c>'s jiggle block plus <c>SetJiggle</c>
/// (<c>WinTrek.cpp:374-399, 503-508</c>), extracted as a pure, testable class —
/// it has no graphics device, no camera, and no dependency on the MonoGame
/// window; it only decides <em>how much</em> the camera should tilt this frame.
/// </summary>
/// <remarks>
/// <para><b>Native algorithm</b> (<c>WinTrek.cpp:378-399</c>), reproduced
/// exactly:</para>
/// <list type="number">
/// <item>Decay the accumulator with a 0.25&nbsp;s half-life:
/// <c>m_jiggle *= pow(0.25f, dt)</c>.</item>
/// <item>If an afterburner is mounted <em>or</em> the accumulator is &gt; 1,
/// <c>power = m_jiggle + (afterburner ? afterburner-&gt;GetPower() : 0)</c>.
/// With no afterburner and an accumulator ≤ 1 the shake stays off (no power).</item>
/// <item>If <c>power != 0</c>, tilt the camera by a random yaw and pitch of up
/// to <c>RadiansFromDegrees(0.5) * power</c> each axis:
/// <c>orientation.Yaw(random(-amount, amount)); orientation.Pitch(...)</c>.</item>
/// </list>
/// <para><b>Which cameras:</b> native sets <c>bJiggle = InternalCamera(m_cm)</c>,
/// and <c>InternalCamera</c> is true only for <c>cmCockpit</c>
/// (<c>Treki.h:145</c>) — so native shakes the <b>cockpit</b> view, not the
/// external chase view. This class is camera-agnostic; the caller decides which
/// view to perturb (see the P449b report for the cockpit-only decision).</para>
/// <para><b>HUD interaction (P449b decision):</b> native rotates the whole
/// camera orientation before projecting, so everything rendered through that
/// camera — the world <em>and</em> the world-projected HUD elements (the lead
/// indicator, the lock/selection brackets) — shakes together, while the
/// screen-anchored centre reticle stays put. This model therefore returns
/// orientation offsets to apply to the shared view; it does not special-case
/// the HUD. See the report for the full reasoning.</para>
/// </remarks>
public sealed class FlightJiggle
{
    /// <summary>The accumulator's half-life, seconds (<c>jiggleHalfLife =
    /// 0.25f</c>, WinTrek.cpp:379).</summary>
    public const float HalfLifeSeconds = 0.25f;

    /// <summary>The per-axis offset ceiling per unit of power, degrees
    /// (<c>RadiansFromDegrees(0.5)</c>, WinTrek.cpp:393).</summary>
    public const float MaxOffsetDegreesPerPower = 0.5f;

    /// <summary>Native's impact-intensity coefficient: <c>c_impactJiggleCoefficient
    /// = 1/20</c> (shipIGC.cpp:624) folded with native's <c>2.0f * damage</c>
    /// into the sole impact entry point (<see cref="ImpactIntensity"/>).</summary>
    public const float ImpactJiggleCoefficient = 1f / 20f;

    private readonly ITrailRandom _rng;
    private float _accumulator;

    /// <param name="rng">Inject an <see cref="ITrailRandom"/> for deterministic
    /// tests; defaults to the live <see cref="RandomTrailSource"/>.</param>
    public FlightJiggle(ITrailRandom? rng = null) => _rng = rng ?? new RandomTrailSource();

    /// <summary>
    /// Apply the two per-axis offsets to a camera <b>view</b> matrix as a pure
    /// camera-space rotation, exactly how native perturbs the camera orientation
    /// (<c>orientation.Yaw(...); orientation.Pitch(...)</c>, WinTrek.cpp:395-396)
    /// before it is applied to the projection. Multiplying the view on the left
    /// by the composite rotation tilts the whole camera — so everything rendered
    /// through it (the world AND the world-projected HUD elements) shakes
    /// together, matching native's projection of target/lead geos against the
    /// jiggled camera. Screen-anchored overlays (the centre reticle) are drawn in
    /// screen space and are unaffected by construction.
    /// </summary>
    /// <param name="view">The camera's un-jiggled view matrix.</param>
    /// <param name="yawRadians">Yaw offset from <see cref="Step"/>, radians.</param>
    /// <param name="pitchRadians">Pitch offset from <see cref="Step"/>, radians.</param>
    public static Matrix PerturbView(Matrix view, float yawRadians, float pitchRadians)
    {
        var rotation = Matrix.CreateFromAxisAngle(Vector3.Up, yawRadians)
                     * Matrix.CreateFromAxisAngle(Vector3.Right, pitchRadians);
        return rotation * view;
    }

    /// <summary>The current residual jiggle accumulator (<c>m_jiggle</c>).</summary>
    public float Accumulator => _accumulator;

    /// <summary>
    /// Replace the accumulator outright — native's <c>SetJiggle(j)</c>
    /// (<c>WinTrek.cpp:503-508</c>), the entry point for an impact (intensity via
    /// <see cref="ImpactIntensity"/>). Asserts <c>jiggle &gt;= 0</c> in native; a
    /// negative input is clamped to 0 defensively.
    /// </summary>
    public void SetJiggle(float jiggle) => _accumulator = MathF.Max(0f, jiggle);

    /// <summary>
    /// Advance one frame and report how much to tilt the camera. Applies the
    /// native decay, then computes <c>power</c> and the per-axis random offset.
    /// </summary>
    /// <param name="dt">Real elapsed seconds since the last frame (the native
    /// <c>dt</c> into <c>pow(0.25, dt)</c>).</param>
    /// <param name="afterburnerPower">The own ship's current afterburner power
    /// in [0,1] (native <c>afterburner-&gt;GetPower()</c>). 0 when not boosting.</param>
    /// <param name="afterburnerMounted">Whether an afterburner is mounted on the
    /// own ship (native's <c>GetMountedPart(ET_Afterburner, 0) != NULL</c> gate).
    /// False also suppresses the afterburner term of <c>power</c>.</param>
    /// <returns>The yaw and pitch to add to the camera orientation, in
    /// <b>radians</b>, each within <c>±0.5° · power</c> — or <c>(0, 0)</c> when
    /// nothing should shake this frame (no afterburner and a residual ≤ 1).</returns>
    public (float YawRadians, float PitchRadians) Step(
        float dt, float afterburnerPower, bool afterburnerMounted)
    {
        _accumulator *= MathF.Pow(HalfLifeSeconds, dt);

        float power = 0f;
        if (afterburnerMounted || _accumulator > 1.0f)
            power = _accumulator + (afterburnerMounted ? afterburnerPower : 0f);

        if (power == 0f) return (0f, 0f);

        float amount = MathHelper.ToRadians(MaxOffsetDegreesPerPower) * power;
        return (_rng.Range(-amount, amount), _rng.Range(-amount, amount));
    }

    /// <summary>
    /// The jiggle intensity for a collision impact, exactly as native computes
    /// the <c>effectJiggle</c> intensity from the collision <paramref name="damage"/>:
    /// <c>2.0f * damage * c_impactJiggleCoefficient</c> (shipIGC.cpp:836, 1006;
    /// station-ramming variants fold the two parties, shipIGC.cpp:1191-1195).
    /// The impact source has no client wire signal in this port (see the P449b
    /// report, scope 4) — this is the drop-in entry point the named follow-up
    /// task (<c>SetJiggle(ImpactIntensity(damage))</c>) will call.
    /// </summary>
    public static float ImpactIntensity(float damage) =>
        2f * damage * ImpactJiggleCoefficient;
}
