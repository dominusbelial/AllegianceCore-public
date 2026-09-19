namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>
/// P267 — the pure control-input → maneuvering-thruster mapping for the local
/// ship's turn sound, free of any MonoGame type (same discipline as
/// <see cref="EngineSoundCurve"/> / <see cref="WarpAmbientSoundCurve"/>) so the
/// trigger maths is unit-testable without a graphics/audio device.
/// </summary>
/// <remarks>
/// <para><b>What this models.</b> Native's <c>ShipSoundSite</c> plays the
/// hull's maneuvering-thruster sound (<c>pht-&gt;GetManuveringThrusterInteriorSound()</c>,
/// <c>WinTrek/trekigc.cpp:1317-1319</c>; exterior twin <c>:1341-1343</c>) while the
/// turn level exceeds a small threshold (<c>m_fTurnSoundLevel &gt; 0.01f</c>),
/// modulating its pitch up with the level. The turn level itself is
/// (<c>trekigc.cpp:1136</c>):
/// <code>
///   float fNewTurnSoundLevel =
///       std::min(1.0f, std::max(TurnRate() * 20, SidewaysThrustFraction()));
///   // TurnRate() = yaw^2 + pitch^2 + roll^2  (jsValues squared, :807-817)
/// </code>
/// so the maneuvering loop swells with how hard the pilot is turning /
/// strafing, and cuts out entirely when the ship coasts straight.</para>
/// <para><b>Port approximation.</b> This client receives its own-ship
/// <c>ControlData</c> (yaw/pitch/roll/throttle) back on the wire
/// (<c>SingleShipUpdate.Controls</c>), so the <c>TurnRate()*20</c> term is
/// computed faithfully from the commanded axes. Native's second term,
/// <c>SidewaysThrustFraction()</c> (engine vector projected sideways against
/// the hull's side-multiplier), has no direct wire equivalent here — in this
/// control scheme sideways acceleration is itself produced by the yaw/pitch
/// inputs, so the port folds it into the same axis terms. The audible
/// consequence is identical: any lateral/turning control raises the loop, a
/// straight coast silences it.</para>
/// <para><b>Default asset.</b> The POC's flyable hulls are the small-ship
/// family, whose maneuvering sound is <c>adjust.ogg</c>
/// (<c>smallShipTurnInteriorSound</c>, sounddef id 300, and its exterior twin
/// <c>:340</c> both map to <c>adjust</c>, <c>sounddef.mdl:1016-1017</c>). The
/// client has no core, so it uses this single real default (see the P232
/// precedent).</para>
/// </remarks>
public static class TurnThrusterSound
{
    /// <summary>Below this turn level the maneuvering loop is silent — native's
    /// <c>m_fTurnSoundLevel &gt; 0.01f</c> play gate (trekigc.cpp:1316-1319).</summary>
    public const float Threshold = 0.01f;

    /// <summary>Map the commanded turn axes (normalized −1..1) to a turn level in
    /// [0,1]: <c>min(1, (yaw²+pitch²+roll²) · 20)</c> — the <c>TurnRate()*20</c>
    /// term of native's level formula (trekigc.cpp:1136), folded with the
    /// sideways term into the same axes (see remarks).</summary>
    public static float LevelFor(float yaw, float pitch, float roll)
    {
        float turnRate = yaw * yaw + pitch * pitch + roll * roll;
        return Math.Clamp(turnRate * 20f, 0f, 1f);
    }

    /// <summary>True when the maneuvering loop should be audible — native's
    /// <c>m_fTurnSoundLevel &gt; 0.01</c> gate (trekigc.cpp:1316-1319).</summary>
    public static bool IsAudible(float turnLevel) => turnLevel > Threshold;

    /// <summary>Pitch (semitone multiplier) for the current turn level — native
    /// <c>SetPitch(0.50f + m_fTurnSoundLevel/3)</c> (trekigc.cpp:1167-1169).</summary>
    public static float PitchFor(float turnLevel) => 0.50f + turnLevel / 3f;
}
