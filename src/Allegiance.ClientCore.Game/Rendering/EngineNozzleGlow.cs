namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Pure, MonoGame-free port of the engine-nozzle glow colour/size math — native
/// <c>ThingGeoImpl::UpdateGlow</c> (<c>effect/thinggeo.cpp:440-460</c>) plus the
/// HSB→RGB conversion <c>Color::SetHSBA</c> (<c>src/zlib/color.cpp:25-33</c>) and
/// its helper <c>HueComponent</c> (<c>color.cpp:12-23</c>). Given a thrust
/// <c>value</c> in [0,1] it reproduces the exact tint (RGBA) and decal scale
/// native computes for the afterburner glow, so the port's glow decal shifts
/// colour/size with real thrust exactly like native, not as an approximation.
/// </summary>
/// <remarks>
/// <para><b>Native math (thinggeo.cpp:440-460).</b> Every frame native recomputes
/// from a <c>value</c>: <c>value = m_thrust</c> (the ship's throttle, mapped to
/// [0,1] by <c>SetThrust(0.5·(jsValues[3]+1))</c>), forced to <c>1.0</c> whenever
/// the afterburner is engaged (<c>m_fThrustPower ≠ 0</c>). Then:</para>
/// <code>
/// hue = pow(value,5)/6;
/// sat = 1 - 0.5·pow(value,20);
/// bri = 1 - pow(1-value,3);
/// scaleGlow = 1 - pow(1-value,2);
/// colour = HSBColor(hue, sat, bri)   // alpha 1
/// </code>
/// <para><b>HSB→RGB (color.cpp:12-33).</b> <c>SetHSBA</c> multiplies hue by 6 and
/// mixes <c>white = 1-saturation</c> with a triangular colour-wheel
/// <c>HueComponent</c> (<c>hue = |(hue+3) mod 6 − 3|</c>, 1 on the segment, linear
/// ramp down to 0 then 0), giving the per-channel result
/// <c>bri·(white + sat·HueComponent(hue + offset))</c> for offsets 0/−2/+2 in R/G/B.</para>
/// <para><b>Observed behaviour</b> (the real colour shift the glow must show):
/// <c>value 0 → bri 0, scale 0</c> (no glow); <c>value 0.25 → (0.578,0.001,0)</c>
/// dark red; <c>value 0.5 → (0.875,0.027,0)</c> red-orange; <c>value 0.75 →
/// (0.984,0.235,0.002)</c> orange-yellow; <c>value 1 → (1,1,0.5)</c> bright
/// yellow-white at full size. Brightness climbs monotonically while hue swings
/// red→yellow — a real, visible colour shift with thrust.</para>
/// </remarks>
public static class EngineNozzleGlow
{
    /// <summary>
    /// Port of native <c>HueComponent</c> (<c>color.cpp:12-23</c>): the triangular
    /// colour-wheel term. <c>mod</c> is C <c>fmod</c>; C#'s <c>%</c> float remainder
    /// has the same sign-of-dividend semantics, so <c>(hue+3) % 6 == fmod(hue+3,6)</c>.
    /// </summary>
    public static float HueComponent(float hue)
    {
        hue = MathF.Abs((hue + 3f) % 6f - 3f);
        if (hue > 2f) return 0f;
        if (hue > 1f) return 2f - hue;
        return 1f;
    }

    /// <summary>
    /// Port of native <c>Color::SetHSBA</c> (<c>color.cpp:25-33</c>): convert the
    /// hue/saturation/brightness triple to linear R,G,B (alpha fixed at 1).
    /// </summary>
    public static (float R, float G, float B) Hsba(float h, float s, float b)
    {
        h *= 6f;
        float white = 1f - s;
        float r = b * (white + s * HueComponent(h));
        float g = b * (white + s * HueComponent(h - 2f));
        float bl = b * (white + s * HueComponent(h + 2f));
        return (r, g, bl);
    }

    /// <summary>
    /// Port of native <c>UpdateGlow</c> (thinggeo.cpp:440-460) for one ship's
    /// current thrust <paramref name="value"/> in [0,1] (0 = no thrust, 1 = full
    /// thrust / afterburner engaged). Returns the glow's linear RGB tint and its
    /// decal scale in [0,1]. The returned scale is 0 at <c>value 0</c>, which is
    /// the native "do not render" gate (<c>RenderGlow</c> draws only while
    /// <c>m_scaleGlow &gt; 0</c>, thinggeo.cpp:1421).
    /// </summary>
    public static (float R, float G, float B, float ScaleGlow) Compute(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        float hue = MathF.Pow(value, 5f) / 6f;
        float sat = 1f - 0.5f * MathF.Pow(value, 20f);
        float bri = 1f - MathF.Pow(1f - value, 3f);
        float scale = 1f - MathF.Pow(1f - value, 2f);
        var (r, g, b) = Hsba(hue, sat, bri);
        return (r, g, b, scale);
    }
}
