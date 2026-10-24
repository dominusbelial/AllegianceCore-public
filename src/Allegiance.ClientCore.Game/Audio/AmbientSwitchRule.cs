namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>
/// P787b — the pure decision behind the ambient switch (SIM-0345 / SIM-0411).
/// Native's ambient loops restart exactly when the requested ambient id
/// differs from the one playing, and stop when nothing is requested:
/// <list type="bullet">
/// <item><c>TrekSoundSite</c> (docked station interior, WinTrek/trekigc.cpp:5076-5087):
/// <c>if (!m_psoundAmbient || m_idAmbient != newAmbientSound) { m_idAmbient =
/// newAmbientSound; if (m_psoundAmbient) m_psoundAmbient-&gt;Stop();
/// m_psoundAmbient = StartSound(m_idAmbient, NULL); }</c> — start when none is
/// playing, SWITCH (stop + restart) when the station type's interiorSound id
/// changed, nothing while it matches.</item>
/// <item><c>trekigc.cpp:5093-5098</c> — not docked: kill the ambient.</item>
/// <item><c>AmbientSoundSite::UpdateSounds</c> (per-model ambient,
/// trekigc.cpp:711-718): <c>if (!m_pAmbientSound) m_pAmbientSound =
/// trekClient.StartSound(m_idAmbientSound, GetSoundSource())</c> — start once,
/// keep (StopSounds resets it on cluster change, :721-725).</item>
/// </list>
/// The port's <see cref="SoundManager"/> consumes the decision for the docked
/// station interior loop (the interiorSound ids of the real PCore001 station
/// types are 420/425/430/435/440/445/450/451/452 — one per civilization
/// family, sounddef.mdl:53-230,1030-1150) and the per-model sites keep their
/// existing planners.
/// </summary>
public static class AmbientSwitchRule
{
    public enum Action { None, Start, Switch, Stop }

    /// <summary>The next action for an ambient loop: <paramref name="playingId"/>
    /// is the id the loop is currently playing (null = none), and
    /// <paramref name="requestedId"/> is the id the caller wants now (≤ 0 =
    /// none — native's NA ambient, or the not-docked kill).</summary>
    public static Action Next(short? playingId, short requestedId)
    {
        if (requestedId <= 0)
            return playingId is null ? Action.None : Action.Stop;
        if (playingId == requestedId)
            return Action.None;
        return playingId is null ? Action.Start : Action.Switch;
    }
}
