using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>Native PitchSound composes frequency ratios, not random jitter.
/// soundengine/soundtemplates.cpp:282-365; effect/efsound.cpp:107-130.</summary>
public readonly record struct PitchSoundTemplate
{
    public float Factor { get; }
    public PitchSoundTemplate(float factor)
    {
        if (!float.IsFinite(factor) || factor < 0.25f || factor > 4f)
            throw new ArgumentOutOfRangeException(nameof(factor));
        Factor = factor;
    }
    public float Octaves(float runtimeFactor = 1f) => MathF.Log2(Factor * runtimeFactor);
}

/// <summary>Effect cone factory parameters (effect/efsound.cpp:82-100),
/// validated as in soundengine/ds3dvirtualbuffer.cpp:859-879.</summary>
public readonly record struct SoundCone
{
    public float Inner { get; }
    public float Outer { get; }
    public float OutsideGainDb { get; }
    public SoundCone(float inner, float outer, float outsideGainDb)
    {
        if (!float.IsFinite(inner) || !float.IsFinite(outer) || !float.IsFinite(outsideGainDb)
            || inner < 0 || outer > 360 || outer < inner || outsideGainDb < -100 || outsideGainDb > 0)
            throw new ArgumentOutOfRangeException(nameof(inner));
        Inner = inner; Outer = outer; OutsideGainDb = outsideGainDb;
    }
    public float GainDb(Vector3 facing, Vector3 toListener) => Sound3DModel.ConeGainDb(
        Inner, Outer, OutsideGainDb, Sound3DModel.ConeAngleDegrees(facing, toListener));
}

public enum AudioQuality { Low, Medium, High }

/// <summary>Software-mixer limits from soundengine/ds3dengine.cpp:507-545.
/// OpenAL owns the device format; these presets change real voice admission,
/// without pretending to change the host's sample rate/bit depth/channels.</summary>
public static class AudioQualityPresets
{
    private static readonly VoiceBudgetConfig Low = new(8, 8), Medium = new(16, 24), High = new(24, 32);
    public static AudioQuality Normalize(AudioQuality quality) =>
        Enum.IsDefined(quality) ? quality : AudioQuality.Medium;
    public static VoiceBudgetConfig Budget(AudioQuality quality) => Normalize(quality) switch
    {
        AudioQuality.Low => Low,
        AudioQuality.High => High,
        _ => Medium,
    };
    public static AudioQuality Next(AudioQuality quality) => (AudioQuality)(((int)Normalize(quality) + 1) % 3);
}
