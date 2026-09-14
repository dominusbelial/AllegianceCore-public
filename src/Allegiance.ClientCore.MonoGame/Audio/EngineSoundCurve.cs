namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>
/// The pure throttle/speed → volume/pitch mapping for the P213 engine loop —
/// deliberately free of any MonoGame type (same discipline as
/// <see cref="Flight.FlightInput"/> / <see cref="Flight.AutopilotSteering"/>),
/// so the curve is unit-testable without a graphics/audio device.
/// </summary>
/// <remarks>
/// <para><b>Inputs.</b> <c>throttle</c> is the ship's real
/// <c>Controls.Throttle</c> axis (−1..+1, where −1 is the idle end — see
/// <see cref="Flight.FlightInput.Translate"/>'s remarks; the sim's cruise law
/// maps −1 → 0 speed and +1 → full speed). <c>speed</c> is the ship's real
/// <c>Velocity.Length()</c> from the same <c>ServerShipUpdate</c> P211's HUD
/// already reads. <c>maxSpeed</c> is the hull's top speed (the sim's
/// <c>HullType.Speed</c>), used to normalise <c>speed</c> onto a 0..1 band.</para>
/// <para><b>Curve.</b> A single normalised "engine load" in [0,1] blends the
/// throttle (what the pilot asks for) and the achieved speed (what the hull
/// actually delivers) with a mild speed lean (weight 0.6 vs 0.4) so coasting
/// at high speed still reads as engine activity even if the pilot throttled
/// back. Volume is a linear ramp on top of a quiet floor (so an idle ship
/// keeps a faint engine presence rather than going dead silent); pitch sweeps
/// a narrow band upward with load so full thrust audibly "revs" the loop.
/// Every output is clamped to a legal range — MonoGame's
/// <c>SoundEffectInstance.Volume</c> is 0..1 and <c>Pitch</c> is −1..1.</para>
/// </remarks>
public static class EngineSoundCurve
{
    /// <summary>Default hull top speed (units/s) for the POC's spawned scout
    /// — the sim's <c>HullType.Speed = 120</c> (see <c>docs/simulation.md</c>,
    /// which drives the default hull at <c>Speed=120</c>). The curve accepts
    /// this as a parameter so it is not hard-coded in the pure logic.</summary>
    public const float MaxSpeedDefault = 120f;

    /// <summary>Minimum engine volume (0..1) at zero load — an idle ship still
    /// has a quiet presence, avoiding an abrupt on/off at the throttle floor.</summary>
    public const float BaseVolume = 0.10f;

    /// <summary>How much volume rises from <see cref="BaseVolume"/> as engine
    /// load goes 0 → 1. Peak volume = <see cref="BaseVolume"/> + this.</summary>
    public const float VolumeRamp = 0.55f;

    /// <summary>Loop pitch at zero load (MonoGame pitch range −1..1; negative =
    /// flattened/duller).</summary>
    public const float IdlePitch = -0.30f;

    /// <summary>Loop pitch at full load (audibly "revved").</summary>
    public const float FullPitch = 0.20f;

    /// <summary>Relative weight of the throttle term in <see cref="EngineLoad"/>.</summary>
    public const float ThrottleWeight = 0.4f;

    /// <summary>Relative weight of the speed term in <see cref="EngineLoad"/>
    /// (deliberately the heavier term so achieved speed dominates the mix).</summary>
    public const float SpeedWeight = 0.6f;

    /// <summary>
    /// Combine throttle (−1..+1) and speed into a single normalised engine-load
    /// scalar in [0,1]. <c>throttle</c> is mapped −1..+1 → 0..1; <c>speed</c> is
    /// divided by <c>maxSpeed</c> and clamped; the two are blended by
    /// <see cref="ThrottleWeight"/>/<see cref="SpeedWeight"/>. A non-positive
    /// <c>maxSpeed</c> contributes a 0 speed term rather than dividing by zero.
    /// </summary>
    public static float EngineLoad(float throttle, float speed, float maxSpeed)
    {
        float throttleNorm = Math.Clamp((throttle + 1f) / 2f, 0f, 1f);
        float speedNorm = maxSpeed > 0f ? Math.Clamp(speed / maxSpeed, 0f, 1f) : 0f;
        return Math.Clamp(ThrottleWeight * throttleNorm + SpeedWeight * speedNorm, 0f, 1f);
    }

    /// <summary>Map an engine-load scalar (assumed in [0,1], clamped anyway) to
    /// a MonoGame <c>Volume</c> in [0,1].</summary>
    public static float Volume(float load) =>
        Math.Clamp(BaseVolume + VolumeRamp * Math.Clamp(load, 0f, 1f), 0f, 1f);

    /// <summary>Map an engine-load scalar (assumed in [0,1], clamped anyway) to
    /// a MonoGame <c>Pitch</c> in −1..1 — a shallow upward sweep from
    /// <see cref="IdlePitch"/> to <see cref="FullPitch"/>.</summary>
    public static float Pitch(float load)
    {
        float l = Math.Clamp(load, 0f, 1f);
        return IdlePitch + (FullPitch - IdlePitch) * l;
    }

    /// <summary>Convenience: the full (volume, pitch) pair for the given real
    /// throttle/speed — what <see cref="SoundManager"/> applies to the looping
    /// engine instance each frame.</summary>
    public static (float Volume, float Pitch) Mix(float throttle, float speed, float maxSpeed)
    {
        float load = EngineLoad(throttle, speed, maxSpeed);
        return (Volume(load), Pitch(load));
    }
}
