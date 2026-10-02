namespace Allegiance.ClientCore.MonoGame.Flight;

/// <summary>
/// Pure, stateless (MonoGame-free) mapping from a ship's real hull fraction to
/// the number of active damage "vents" that should be streaming smoke — the
/// P297 port of the gating condition that drives native's continuous
/// damage-debris particle stream (<c>AddDamageParticles</c>,
/// <c>effect/thinggeo.cpp:699-755</c>).
/// </summary>
/// <remarks>
/// <para><b>Why a sibling of <see cref="DamageFlashTracker"/> (P228), not an
/// extension of it.</b> Native models the two effects in two different
/// subsystems with two different signals and two different targets:</para>
/// <list type="bullet">
/// <item>The HUD <b>flash</b> (P228) is <em>edge</em>-triggered — a momentary,
/// 2&#8209;s on/off pulse armed the instant the local ship's hull drops
/// (WinTrek <c>SetTimeDamaged</c>/<c>GetFlash</c>, trekigc.cpp:4270-4283),
/// fed only the local ship's hull, painted as a full-screen translucent wash.</item>
/// <item>The debris <b>stream</b> is <em>level</em>-triggered — it runs
/// <em>continuously</em> while the model is "currently damaged enough"
/// (native's <c>m_iLastDamageIndex &gt;= 0 &amp;&amp; m_bShowDamage</c> gate,
/// thinggeo.cpp:701), applies to <em>any visible</em> damaged ship (not just
/// the local one), and paints in-world smoke particles off the hull.</item>
/// </list>
/// Both effects also read the <em>same underlying physical quantity</em> — the
/// ship's armour fraction — but the flash consumes a <em>falling edge</em>
/// (hull decreased since last frame) whereas the debris consumes a
/// <em>level</em> (how far below full the hull currently sits). Conflating
/// them would force a per-ship, level-semantics model onto a single-ship,
/// edge-semantics class. Native's own separation (model-level
/// <c>thinggeo</c> vs client-level <c>WinTrek</c>) is mirrored here.</para>
/// <para><b>Native condition (cited).</b> Native opens damage frames in
/// <c>AddDamage</c> (thinggeo.cpp:1358-1382) and closes them in
/// <c>RemoveDamage</c> (thinggeo.cpp:1384-1395), indexed by
/// <c>iDamageIndex = (int)((1.0f − fDamageFraction) * MAX_DAMAGE_FRAMES) − 1</c>
/// with <c>MAX_DAMAGE_FRAMES = 10</c> (thinggeo.cpp:215). The stream runs while
/// <c>m_iLastDamageIndex &gt;= 0</c> — i.e. as soon as
/// <c>(1 − fDamageFraction)·10 ≥ 1</c>, which is <c>fDamageFraction ≤ 0.9</c> —
/// and the number of concurrent emission origins equals
/// <c>m_iLastDamageIndex + 1 = (int)((1 − fraction)·10)</c>, i.e. more damage
/// = more vents (up to 10). Repair raises the fraction, <c>RemoveDamage</c>
/// lowers the index, and the stream <em>stops</em> once the ship is healed
/// above 0.9 — the stream is genuinely <b>bidirectional</b>. This port reads
/// the same quantity directly off the live per-ship <c>HullFraction</c> on the
/// <c>ServerShipUpdate</c> payload, so it reproduces that level behaviour with
/// no event plumbing. The per-event <c>c_dmgidNoDebris</c> suppression flag
/// (shipIGC.cpp:1310) is not portable to a level-based client inference (the
/// client sees only the resulting fraction, never the damage type), so it is
/// deliberately not modelled — a documented divergence.</para>
/// </remarks>
public static class DamageDebrisTracker
{
    /// <summary>Maximum number of concurrent damage vents
    /// <c>#define MAX_DAMAGE_FRAMES 10</c> (thinggeo.cpp:215) — one per 10%
    /// of hull lost.</summary>
    public const int MaxDamageFrames = 10;

    /// <summary>
    /// The highest hull fraction at which the stream is active. Native turns
    /// it on when <c>(1 − fraction)·10 − 1 ≥ 0</c>, i.e.
    /// <c>fraction ≤ 0.9</c> (thinggeo.cpp:1360-1361, 701). Above this the
    /// stream is off; at or below it the stream runs with at least one vent.
    /// </summary>
    public const float DamageThreshold = 0.9f;

    /// <summary>
    /// How many damage vents are currently streaming from a ship at the given
    /// <paramref name="hullFraction"/> (native's active damage-frame count).
    /// Returns 0 (stream off) when the hull is above <see cref="DamageThreshold"/>,
    /// otherwise 1…<see cref="MaxDamageFrames"/> scaling with damage depth —
    /// the port of native <c>(int)((1 − fraction)·MAX_DAMAGE_FRAMES)</c> where
    /// <c>MAX_DAMAGE_FRAMES = 10</c>. A destroyed ship (fraction ≤ 0) reports
    /// 0 — there is no intact hull left to vent from.
    /// </summary>
    /// <param name="hullFraction">The ship's real hull fraction (0..1).</param>
    public static int DamageFrameCount(float hullFraction)
    {
        if (hullFraction <= 0f)
            return 0;
        int frames = (int)((1f - hullFraction) * MaxDamageFrames);
        return Math.Clamp(frames, 0, MaxDamageFrames);
    }
}
