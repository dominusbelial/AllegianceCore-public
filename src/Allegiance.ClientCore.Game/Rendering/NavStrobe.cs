namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// One strobe light's real timing parameters — a faithful port of native
/// <c>LightData</c> (<c>effect/efapp.cpp:99-112</c>): the per-instance
/// <c>period</c> / <c>phase</c> / <c>rampUp</c> / <c>hold</c> / <c>rampDown</c>
/// parsed straight out of each <c>SS</c>-named mesh group's name by native
/// <c>LightsGeo</c>'s compiler callback (efapp.cpp:498-529, via
/// <c>Token::ReadStrobe</c> at efapp.cpp:474-482). Because the timing is
/// per-light, different lights on the same hull genuinely blink at their own
/// rates/phases — that is the real mechanism behind native's
/// red/green-alternating nav-light look, and it is preserved here.
/// </summary>
/// <remarks>
/// <para><b>Native defaults (efapp.cpp:507-511).</b> When a group name carries no
/// parseable numbers, native uses <c>period 1.25, phase 0, rampUp 0.1, hold 0,
/// rampDown 0.05</c> (seconds). <c>ReadStrobe</c> reads the first one-or-two
/// numeric tokens of the group name as <c>(phase, period)</c> and <b>negates</b>
/// them (the compiled group names encode the values as negatives — see
/// efapp.cpp:476-481), overriding the defaults. <c>rampUp</c>/<c>hold</c>/
/// <c>rampDown</c> are never overridden by the name; they stay at the defaults.</para>
/// </remarks>
public readonly record struct NavStrobeTiming(
    float Period, float Phase, float RampUp, float Hold, float RampDown)
{
    /// <summary>Native LightsGeo's default strobe timing (efapp.cpp:507-511),
    /// expressed in real seconds.</summary>
    public static readonly NavStrobeTiming Native =
        new(Period: 1.25f, Phase: 0f, RampUp: 0.1f, Hold: 0f, RampDown: 0.05f);
}

/// <summary>
/// Pure, MonoGame-free port of native <c>LightsGeo::Render</c>'s pulse-envelope
/// math (effect/efapp.cpp:171-222). Given a light's real timing and the current
/// elapsed time it returns the triangle ramp-up → hold → ramp-down → off pulse
/// value in [0,1] that native uses both to size the strobe decal
/// (<c>0.5·value</c>) and to gate drawing (<c>value &gt; 0</c>, efapp.cpp:208).
/// </summary>
public static class NavStrobeMath
{
    /// <summary>
    /// The strobe's current pulse value in [0,1] at elapsed <paramref name="time"/>.
    /// Faithful port of the native loop:
    /// <code>
    /// while (true) { value = time - phase; if (value &lt; period) break; phase += period; }
    /// if (value &lt; rampUp)      value /= rampUp;                      // ramp 0→1
    /// else { value -= rampUp;
    ///        if (value &lt; hold) value = 1;                            // hold at 1
    ///        else { value -= hold;
    ///               if (value &lt; rampDown) value = 1 - value/rampDown; // ramp 1→0
    ///               else value = 0; }}                                // off
    /// </code>
    /// (efapp.cpp:177-206). The native <c>while</c> advances <c>phase</c> by whole
    /// periods until <c>time-phase ∈ [0, period)</c>; folding that into one modulo
    /// gives the same value statelessly (C# float <c>%</c> keeps the dividend's
    /// sign, so negatives are folded back up).
    /// </summary>
    public static float Pulse(float time, in NavStrobeTiming t)
    {
        if (t.Period <= 0f) return 0f;   // defensive: a non-positive period is degenerate

        float value = (time - t.Phase) % t.Period;
        if (value < 0f) value += t.Period;

        if (value < t.RampUp)
            return t.RampUp > 0f ? value / t.RampUp : 1f;
        value -= t.RampUp;

        if (value < t.Hold)
            return 1f;
        value -= t.Hold;

        if (value < t.RampDown)
            return t.RampDown > 0f ? 1f - (value / t.RampDown) : 0f;
        return 0f;
    }
}
