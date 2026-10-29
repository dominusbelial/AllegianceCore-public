using Allegiance.Core;
using Allegiance.Core.Audio;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>
/// An <c>ISoundPositionSource</c> as the managed client can express it: a world
/// position the stereo/3-D mix places the cue at
/// (<c>WinTrek/trekigc.cpp:4945,4965-4973</c>). A null source is native's
/// <c>NULL</c> — the cue plays unpositioned (cockpit range).
/// </summary>
public readonly record struct NativeSoundSource(Vector3 Position)
{
    /// <summary>
    /// <c>ThingSiteImpl::GetSoundSource(vectOffset)</c>
    /// (<c>WinTrek/trekigc.cpp:4969</c>): the caller hands an offset that native
    /// rotates into the model's frame; here the caller passes the already-world
    /// offset and the source is that point.
    /// </summary>
    public NativeSoundSource WithWorldOffset(Vector3 offset) => new(Position + offset);
}

/// <summary>What a resolved <c>StartSound</c> asked the mixer to do.</summary>
public readonly record struct NativeSoundPlayback(string Stem, bool Looping, float Volume);

/// <summary>
/// P871 / SIM-0314 — the native numeric <c>SoundID</c> dispatch
/// (<c>WinTrek/WinTrek.cpp:11271-11280</c>):
/// <code>
/// TRef&lt;ISoundInstance&gt; StartSound(SoundID soundId, ISoundPositionSource* psource = NULL)
/// {
///     if (soundId &lt; 0 || soundId &gt; m_vSoundMap.GetCount()) { ZAssert(false); return NULL; }
///     return StartSound(m_vSoundMap[soundId], psource);
/// }
/// </code>
/// The numeric id indexes the <c>soundList</c>-derived template table
/// (<see cref="NativeSoundCatalog"/>), and the requested positional source is
/// forwarded verbatim to the template start. An out-of-range id, an empty slot
/// or a template with no wave is suppressed (native returns NULL).
/// <para>
/// <b>Deviation, stated:</b> native's upper bound is <c>&gt; GetCount()</c>, one
/// past the last index — a retail-build off-by-one that would read out of
/// bounds on <c>soundId == Count</c>. A managed index has to be
/// <c>&lt; Count</c>; the observable in-range behaviour (id 0..Count-1 resolves,
/// everything else is suppressed) is identical and the crash case is not
/// reproduced.
/// </para>
/// </summary>
public static class NativeSoundDispatch
{
    /// <summary>Native's positional-attenuation radius for generic sound-effect
    /// sources; the same 6000-unit convention the port's other positional cues
    /// use (<see cref="HitSoundCurve.AudibleRadius"/>).</summary>
    public const float AudibleRadius = 6000f;

    /// <summary>Native's range gate on the numeric id.</summary>
    public static bool IsValidId(short soundId) => soundId >= 0 && soundId < NativeSoundCatalog.Count;

    /// <summary>The numeric-id → template lookup, or null when invalid/empty.</summary>
    public static NativeSoundTemplate? Resolve(short soundId) => NativeSoundCatalog.Resolve(soundId);

    /// <summary>
    /// Resolve a numeric id and plan the start. Returns false when native's
    /// <c>StartSound</c> would return NULL (invalid id, empty slot, or a
    /// template with no playable wave); otherwise the first wave of the
    /// template, its looping flag, and the listener-distance volume (full for an
    /// unpositioned source, as native plays cockpit cues at template gain).
    /// </summary>
    public static bool TryPlan(short soundId, NativeSoundSource? source, Vector3? listener,
        out NativeSoundPlayback playback)
    {
        playback = default;
        if (!IsValidId(soundId)) return false;
        if (NativeSoundCatalog.Templates.GetValueOrDefault(soundId) is not { Playable: true } template)
            return false;
        float volume = 1f;
        if (source is { } s && listener is { } l)
        {
            float distance = (s.Position - l).Length();
            volume = Falloff(distance);
            if (volume <= 0f) return false;
        }
        playback = new NativeSoundPlayback(template.Waves[0], template.Looping, volume);
        return true;
    }

    /// <summary>Linear listener→source falloff (full at 0, silent past
    /// <see cref="AudibleRadius"/>), the shape the port's positional cues share.</summary>
    public static float Falloff(float distance)
    {
        if (distance <= 0f) return 1f;
        if (distance >= AudibleRadius) return 0f;
        return 1f - distance / AudibleRadius;
    }
}

/// <summary>
/// P871 / SIM-0407..0409 — the three native sound-routing gates
/// (<c>WinTrek/trekigc.cpp:4955-4983</c>):
/// <code>
/// void WinTrekClient::PlaySoundEffect(SoundID soundID, ImodelIGC* model)
/// { if (!model || (model-&gt;GetCluster() &amp;&amp; model-&gt;GetCluster() == GetCluster())) StartSound(soundID, model ? source : NULL); }
///
/// void WinTrekClient::PlaySoundEffect(SoundID soundID, ImodelIGC* model, const Vector&amp; vectOffset)
/// { ... same gate ... source = model ? GetSoundSource(vectOffset) : NULL; }
///
/// void WinTrekClient::PlayNotificationSound(SoundID soundID, ImodelIGC* model)
/// { if (model == GetShip() || model == GetShip()-&gt;GetSourceShip()) StartSound(soundID, source); }
/// </code>
/// <b>Effect gate (SIM-0407):</b> a null model plays unpositioned; a model whose
/// cluster is known and equals the listener's plays at the model; a detached
/// model (<c>GetCluster() == NULL</c>) and a model in another cluster are
/// suppressed. <b>Offset (SIM-0408):</b> the same gate, but the source is the
/// model position plus the caller's offset, so placement — not availability —
/// changes. <b>Notification (SIM-0409):</b> only the player's own ship or its
/// source ship (the carrier when boarded as a gunner) is voiced; every other
/// model is suppressed.
/// <para>
/// <b>Native quirk preserved:</b> the notification comparison is plain pointer
/// equality against <c>GetShip()</c>, so with no local ship (observer) a null
/// model compares equal and would be played unpositioned. The managed form keeps
/// that by comparing nullable ids (<c>null == null</c>), which means "null model
/// suppression" holds only while a local ship exists — exactly native.
/// </para>
/// </summary>
public static class SoundRouting
{
    /// <summary><c>PlaySoundEffect(SoundID, ImodelIGC*)</c> gate.</summary>
    public static bool AllowsEffect(short? modelId, short? modelCluster, short? listenerCluster)
        => modelId is null
           || (modelCluster is { } model && listenerCluster is { } mine && model == mine);

    /// <summary><c>PlaySoundEffect(SoundID, model, vectOffset)</c> gate — same
    /// availability rule as <see cref="AllowsEffect"/>.</summary>
    public static bool AllowsEffectWithOffset(short? modelId, short? modelCluster, short? listenerCluster)
        => AllowsEffect(modelId, modelCluster, listenerCluster);

    /// <summary>The offset overload's source: the model frame's point moved by
    /// the caller's (already world-rotated) offset. Native transforms the offset
    /// with <c>GetOrientation().TimesInverse(delta)</c> inside the ThingSite; the
    /// managed caller passes the same world delta, so the position is identical.</summary>
    public static NativeSoundSource OffsetSource(Vector3 modelPosition, Vector3 worldOffset)
        => new(modelPosition + worldOffset);

    /// <summary>Native <c>GetOrientation().TimesInverse(localDelta)</c> helper for
    /// the one native caller of the offset overload (<c>Igc/asteroidigc.h:154</c>
    /// passes <c>TimesInverse(position2 - position1)</c>).</summary>
    public static Vector3 LocalOffset(Orientation modelOrientation, Vector3 worldDelta)
        => modelOrientation.TimesInverse(worldDelta);

    /// <summary><c>PlayNotificationSound(SoundID, ImodelIGC*)</c> gate.</summary>
    public static bool AllowsNotification(short? modelId, short? ownShipId, short? sourceShipId)
        => modelId == ownShipId || modelId == sourceShipId;
}

/// <summary>
/// P871 / SIM-0346 — native <c>PlaySoundIf</c> (<c>WinTrek/trekigc.cpp:930-951</c>):
/// <code>
/// void PlaySoundIf(TRef&lt;ISoundInstance&gt;&amp; pSound, SoundID id, ISoundPositionSource* psource, bool bPlay)
/// {
///     if (bPlay) { if (!pSound) pSound = trekClient.StartSound(id, psource); }
///     else       { if (pSound) { pSound-&gt;Stop(); pSound = NULL; } }
/// }
/// </code>
/// The retained instance is observable state: a play request while one is
/// already held is a no-op (it does NOT restart, even for a different id), a
/// stop request releases it, and a later play starts it again.
/// </summary>
public sealed class PlaySoundIfSlot
{
    public enum Action { None, Start, Stop }

    private short? _playingId;

    /// <summary>The id currently held (null = no instance). Read-only
    /// observation of the native <c>TRef</c>.</summary>
    public short? PlayingId => _playingId;

    /// <summary>The native body. <paramref name="requestedId"/> is the id the
    /// caller would start; it is only used when the slot is empty.</summary>
    public Action Update(bool play, short requestedId)
    {
        if (play)
        {
            if (_playingId is not null) return Action.None;
            _playingId = requestedId;
            return Action.Start;
        }
        if (_playingId is null) return Action.None;
        _playingId = null;
        return Action.Stop;
    }

    /// <summary>Clear without emitting a stop — for teardown, when the host is
    /// dropping the device anyway.</summary>
    public void Reset() => _playingId = null;
}

/// <summary>
/// P871 / SIM-0371 — native <c>GetGunnerInteriorSoundID</c>
/// (<c>WinTrek/trekigc.cpp:1096-1112</c>): only a boarded gunner has one, and it
/// is the INTERIOR SOUND OF THE MOUNTED TURRET HARDPOINT on the source ship's
/// hull:
/// <code>
/// if (trekClient.GetShip()-&gt;GetParentShip()) {
///     const IhullTypeIGC* pht = m_pship-&gt;GetHullType();
///     Mount mount = trekClient.GetShip()-&gt;GetTurretID();
///     if (mount != NA) return pht-&gt;GetHardpointData(mount).interiorSound;
/// }
/// return NA;
/// </code>
/// The loop is maintained by <c>PlaySoundIf(..., bIsGunner)</c> inside the
/// <c>bIsPlayersShip</c> block (<c>WinTrek/trekigc.cpp:1300-1314</c>), so it is
/// independent of the pilot interior loop (which plays only when
/// <c>!bIsGunner</c>) and of the camera: the interior block runs even in an
/// external view so the sound can be switched back mid-loop.
/// </summary>
public static class GunnerInteriorSound
{
    /// <summary>Resolve the gunner's interior sound id, or <c>NA</c> (-1) when
    /// the local ship is not boarded on a turret. <paramref name="hull"/> is the
    /// SOURCE ship's hull (native's <c>m_pship</c> in the sound loop).</summary>
    public static short Resolve(bool boarded, short? turretMount, global::Allegiance.Core.DataHullTypeIGC? hull)
    {
        if (!boarded || turretMount is not { } mount || mount < 0 || hull is not { } sourceHull) return -1;
        if (mount >= sourceHull.Hardpoints.Length) return -1;
        return sourceHull.Hardpoints[mount].InteriorSound;
    }

    /// <summary>Native <c>bIsGunner</c>: the player's own ship is boarded on a
    /// turret. A pure predicate so the loop gate is testable without a world.</summary>
    public static bool IsGunner(bool boarded, short? turretMount) => boarded && turretMount is >= 0;
}

/// <summary>
/// P871b — the two cockpit-view behaviours the port's <c>SoundManager.Update</c>
/// keys on, split exactly where native splits them:
/// <list type="bullet">
/// <item><b>camera view</b> (<c>TrekWindow::cmCockpit</c>) decides audibility of
/// the interior mix: native silences every internal sound with
/// <c>SetInternalVolume(-100)</c> outside cockpit view (and boosts nothing — the
/// port's small cockpit gain is its own device adaptation).</item>
/// <item><b>gunner</b> (<c>bIsGunner</c>) decides WHICH interior bed: the pilot
/// interior sound plays iff <c>!bIsGunner</c> and the gunner interior sound iff
/// <c>bIsGunner</c> (<c>WinTrek/trekigc.cpp:1310-1312</c>), independently of the
/// camera. The engine/main-thruster interior bed is NOT gunner-gated — native
/// keeps it playing for a gunner (<c>trekigc.cpp:1313-1314</c>), so a turret
/// gunner in cockpit view still hears the hull's engine loop.
/// </list>
/// </summary>
public readonly record struct CockpitAudio(bool CockpitView, bool IsGunner)
{
    /// <summary>The hull's engine/interior mix is audible (camera view only).</summary>
    public bool EngineAudible => CockpitView;

    /// <summary>Native <c>PlaySoundIf(m_pPilotInteriorSound, …, !bIsGunner)</c>
    /// (trekigc.cpp:1311-1312): the pilot interior bed belongs to the pilot, so a
    /// gunner never gets it (the gunner's own interior loop replaces it).</summary>
    public bool PilotInterior => CockpitView && !IsGunner;
}

/// <summary>
/// P871 / SIM-0412 — native <c>SoundIDEventSink</c>
/// (<c>WinTrek/trekmdl.cpp:452-478</c>): every MDL button starts its cue then
/// forces the sound engine to run immediately, because the button's own work can
/// take a fifth of a second and the click would otherwise be heard late:
/// <code>
/// bool OnEvent(IEventSource*) { GetWindow()-&gt;StartSound(m_soundId); GetWindow()-&gt;SoundEngineUpdate(); return true; }
/// </code>
/// Both entry points (the ordinary <see cref="IEventSource"/> and the integer
/// <see cref="IIntegerEventSource"/>) run the same body; a button created with
/// an arbitrary <c>SoundID</c> (the number-button pane takes one from
/// configuration, <c>trekmdl.cpp:616</c>) is no different from the built-in
/// mouseover/mouseclick ids.
/// </summary>
public static class SoundIdEventSink
{
    /// <summary>Fire a button event's cue: start the sound, then flush the audio
    /// update before the caller's slow work runs.</summary>
    public static void Fire(ISoundIdEventSink sink, short soundId)
    {
        sink.StartSoundId(soundId);
        sink.FlushAudio();
    }
}

/// <summary>The minimal sink <see cref="SoundIdEventSink"/> drives (the port's
/// production implementation is <see cref="SoundManager"/>).</summary>
public interface ISoundIdEventSink
{
    /// <summary>Native <c>StartSound(m_soundId)</c> — the numeric dispatch.</summary>
    void StartSoundId(short soundId);
    /// <summary>Native <c>SoundEngineUpdate()</c>.</summary>
    void FlushAudio();
}
