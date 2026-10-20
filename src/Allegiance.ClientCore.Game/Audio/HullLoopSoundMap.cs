using System.Collections.Generic;

namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>
/// P756 — resolves hull loop <c>SoundID</c> values read from
/// <see cref="DataHullTypeIGC"/> to the bundled wave stems used by the real
/// sound player. Native's <c>ShipSoundSite</c> calls
/// <c>pht-&gt;GetManuveringThrusterInteriorSound()</c> /
/// <c>GetManuveringThrusterExteriorSound()</c>
/// (<c>WinTrek/trekigc.cpp:1317-1319</c> and <c>:1341-1343</c>), which are
/// ids into the compiled soundbase. This checkout has no compiled soundbase, so
/// the table is derived from <c>Content/sounddef.mdl</c>'s
/// <c>soundList</c> (id → template) plus each template's <c>ImportWave</c>,
/// exactly like <see cref="WeaponSoundMap"/>.
/// </summary>
public static class HullLoopSoundMap
{
    /// <summary>SoundID → wave stem for maneuvering-thruster loops, derived
    /// from sounddef.mdl:1016-1017,1023-1024,1992-1996.</summary>
    public static readonly IReadOnlyDictionary<short, string> TurnStemBySoundId = new Dictionary<short, string>
    {
        [300] = "adjust",      // smallShipTurnInteriorSound
        [320] = "accelsmall",  // capitalShipTurnInteriorSound
        [340] = "adjust",      // smallShipTurnExteriorSound
        [360] = "accelsmall",  // capitalShipTurnExteriorSound
    };

    /// <summary>Resolve a maneuvering-thruster SoundID to its wave stem. Returns
    /// <c>null</c> when the id is unlisted (including <c>NA/0</c>).</summary>
    public static string? TurnStem(short soundId) =>
        TurnStemBySoundId.TryGetValue(soundId, out var stem) ? stem : null;
}
