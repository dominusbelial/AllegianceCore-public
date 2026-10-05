using System;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P545 — pure, MonoGame-free ports of native's particle colour/size curves
/// (<c>effect/particlegeo.cpp</c>), so the exact tint mathematics can be unit
/// tested headless (no graphics device) and shared by every renderer that
/// draws a native smoke / fire / spark particle.
/// </summary>
/// <remarks>
/// <para>Each method is a literal transcription of the matching native
/// behavior's <c>Render</c> colour/size computation. The renderers feed the
/// particle's remaining-lifetime fraction and read back the RGBA tint (in the
/// native <c>Color</c> convention — values in 0..1) plus a size scale. All of
/// these native particle decals are drawn with the additive default blend
/// (<c>Context::DrawDecal</c>, blendMode default <c>BlendModeAdd</c>,
/// <c>engine/context.h:929</c>), and the dim <em>colour</em> is what keeps
/// stacked smoke grey instead of blowing out to white — see
/// <see cref="NativeEffectTextures"/>.</para>
/// </remarks>
public static class NativeParticleCurves
{
    /// <summary>Result of a fire particle's colour/size computation.</summary>
    public readonly record struct Fire(float R, float G, float B, float A, float SizeScale);

    /// <summary>
    /// Smoke tint — native <c>SmokeBehavior::Render</c>
    /// (<c>particlegeo.cpp:45-49</c>): with
    /// <c>c = 0.25 · countdown</c>, RGBA = <c>(c, c·0.8, c, c)</c>. The grey
    /// <b>green is scaled back a tad</b> to avoid a greenish hue (native
    /// comment). <paramref name="countdown"/> is the remaining-lifetime
    /// fraction in [0,1] (<c>fCountdownToFinish</c>).
    /// </summary>
    public static (float R, float G, float B, float A) SmokeColor(float countdown)
    {
        float cc = Math.Clamp(countdown, 0f, 1f);
        float c = 0.25f * cc;
        return (c, c * 0.8f, c, c);
    }

    /// <summary>
    /// Smoke size scale — native <c>SmokeBehavior::Render</c>
    /// (<c>particlegeo.cpp:51-54</c>): <c>fSize = m_fSize · (0.75 + 2.25·(1−countdown))</c>,
    /// i.e. a puff starts at 0.75× its base size and swells to 3.0× as it
    /// expires.
    /// </summary>
    public static float SmokeSizeScale(float countdown)
    {
        float cc = Math.Clamp(countdown, 0f, 1f);
        return 0.75f + 2.25f * (1f - cc);
    }

    /// <summary>
    /// Fire colour + size — native <c>FireBehavior::Render</c>
    /// (<c>particlegeo.cpp:73-129</c>). <paramref name="lifeFrac"/> is the
    /// elapsed fraction of the particle's life (<c>elapsed / duration</c>),
    /// 0 → 1. Native ramps up over the first <c>rampUpFraction = 0.1</c> of
    /// the life (a <b>white</b> puff growing 0.5→1.0× exponentially), then
    /// ramps down from yellow to red while shrinking 1.0→0.5×:
    /// <list type="bullet">
    /// <item>ramp-up colour <c>(1, 1, 1, 0.5)</c> (countdown clamped at 1).</item>
    /// <item>ramp-down colour <c>(1, c, 0.25·c, 0.5·c²)</c> where
    /// <c>c = (1−lifeFrac)/(1−0.1)</c>.</item>
    /// </list>
    /// </summary>
    public static Fire ComputeFire(float lifeFrac)
    {
        const float rampUpFraction = 0.1f;
        float lf = Math.Clamp(lifeFrac, 0f, 1f);
        if (lf < rampUpFraction)
        {
            // Ramping up: white, growing 0.5 → 1.0 (exponential 0.666).
            float rampProgress = lf / rampUpFraction;
            float sizeScale = 0.5f + 0.5f * MathF.Pow(rampProgress, 0.666f);
            return new Fire(1f, 1f, 1f, 0.5f, sizeScale);   // countdown clamped at 1
        }
        else
        {
            // Ramping down: yellow → red (1, c, 0.25c), shrinking 1.0 → 0.5.
            float c = (1f - lf) / (1f - rampUpFraction);
            float alpha = 0.5f * c * c;
            float sizeScale = 0.5f + 0.5f * MathF.Pow(c, 0.666f);
            return new Fire(1f, c, c * 0.25f, alpha, sizeScale);
        }
    }

    /// <summary>
    /// Teleport-spark tint — native <c>SparkBehavior::Render</c>
    /// (<c>particlegeo.cpp:230-236</c>): with <c>c = countdown</c> and
    /// <c>k = ½·c²</c>, RGBA = <c>(1.5k, 2.0k, c, k)</c> — hot blue fading out.
    /// </summary>
    public static (float R, float G, float B, float A) SparkColor(float countdown)
    {
        float c = Math.Clamp(countdown, 0f, 1f);
        float k = 0.5f * c * c;
        return (k * 1.5f, k * 2.0f, c, k);
    }
}
