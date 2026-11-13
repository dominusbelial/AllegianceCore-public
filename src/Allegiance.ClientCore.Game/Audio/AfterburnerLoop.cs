namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>
/// P1003 — the native afterburner loop: WHICH sound and WHEN. Native keeps one
/// loop instance per bed and maintains it through <c>PlaySoundIf</c>, so the cue
/// is a state loop, not a button echo:
/// <code>
/// SoundID GetAfterburnerSoundID(bool fInterior)                       // trekigc.cpp:1115-1130
/// {
///     IafterburnerIGC* pafterburner =
///         (IafterburnerIGC*)(m_pship->GetMountedPart(ET_Afterburner, 0));
///     if (pafterburner)
///         return fInterior ? pafterburner->GetInteriorSound()
///                          : pafterburner->GetExteriorSound();
///     return NA;
/// }
///
/// float AfterburnerPower()                                            // trekigc.cpp:820-825
/// {
///     IafterburnerIGC* pafterburner =
///         (IafterburnerIGC*)(m_pship->GetMountedPart(ET_Afterburner, 0));
///     return pafterburner ? pafterburner->GetPower() : 0.0f;
/// }
///
/// PlaySoundIf(m_pAfterburnerInteriorSound, GetAfterburnerSoundID(true),
///             m_psourceEngine, AfterburnerPower() &gt; 0.0f);          // trekigc.cpp:1320-1322
/// PlaySoundIf(m_pAfterburnerExteriorSound, GetAfterburnerSoundID(false),
///             m_psourceEngine, AfterburnerPower() &gt; 0.0f);          // trekigc.cpp:1344-1347
/// </code>
/// <para>
/// Two independent conditions, both required: a MOUNTED <c>ET_Afterburner</c>
/// part (otherwise the id is <c>NA</c> — a pod has no parts at all, P998) and
/// that part's own power ramp above zero (<c>CafterburnerIGC::m_power</c>,
/// advanced by <c>afterburnerIGC.cpp:103-164</c> only while the booster button
/// is held AND the tank is non-empty, decayed on release). A ship holding the
/// button with no booster mounted, an empty tank or a still-mounting part
/// (mounted fraction &lt; 1 → power 0) is silent, and native's
/// <c>salNoFuelSound</c> one-shot is the only thing that fires
/// (<c>trekigc.cpp:829-889</c>, ported as SIM-0364/0366).
/// </para>
/// <para>
/// The port's power for the LOCAL ship is the client-side
/// <see cref="Allegiance.ClientCore.Flight.OwnShipSimulator.Power"/> ramp — the
/// same mounted/on-rate/off-rate/fuel rules as <c>CafterburnerIGC::Update</c>,
/// fed from the own loadout and the wire fuel fraction
/// (<c>Game1.Prediction.cs</c> <c>ConfigureOwnBooster</c>), which is exactly the
/// model native's own client reads (<c>m_pship</c> is the client-owned ship; the
/// server-side <c>Ship</c> keeps the authoritative copy). The
/// <c>AfterburnerButton</c> <c>StateM</c> bit is NOT a substitute: it is set
/// whenever the key is held.
/// </para>
/// </summary>
/// <param name="Mounted">A usable <c>ET_Afterburner</c> part is on the ship
/// (native <c>GetMountedPart(ET_Afterburner, 0) != NULL</c>).</param>
/// <param name="Power">The mounted part's power ramp, 0..1 (native
/// <c>GetPower()</c>).</param>
/// <param name="InteriorSoundId">The part type's <c>InteriorSound</c>
/// (<c>DataAfterburnerTypeIGC</c>, <c>Igc/igc.h:1865</c>; 680 =
/// <c>afterburner1InSound</c> → <c>iafter</c> in the retail core).</param>
/// <param name="ExteriorSoundId">The part type's <c>ExteriorSound</c>; 700 =
/// <c>afterburner1OutSound</c> → <c>xafter</c> in the retail core.</param>
public readonly record struct AfterburnerLoop(
    bool Mounted, float Power, short InteriorSoundId, short ExteriorSoundId)
{
    /// <summary>Native <c>NA</c> (<c>-1</c>): no sound id.</summary>
    public const short NA = -1;

    /// <summary>Native <c>GetAfterburnerSoundID(true)</c>: the mounted part's
    /// interior sound, or <c>NA</c> when nothing is mounted.</summary>
    public short InteriorId => Mounted ? InteriorSoundId : NA;

    /// <summary>Native <c>GetAfterburnerSoundID(false)</c>: the mounted part's
    /// exterior sound, or <c>NA</c> when nothing is mounted.</summary>
    public short ExteriorId => Mounted ? ExteriorSoundId : NA;

    /// <summary>Native's <c>PlaySoundIf</c> predicate: a mounted afterburner
    /// with power above zero. Both beds share this one gate.</summary>
    public bool Plays => Mounted && Power > 0f;

    /// <summary>No afterburner mounted and no power (silent).</summary>
    public static AfterburnerLoop Absent => default;
}
