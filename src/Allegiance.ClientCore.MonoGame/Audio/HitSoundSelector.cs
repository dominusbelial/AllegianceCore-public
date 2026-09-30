using System.Collections.Generic;
using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;

namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>
/// P463 — the four projectile hit sounds, chosen exactly as native
/// <c>ThingSite::RegisterHit</c> does (trekigc.cpp:2365-2393): which of
/// myShield/myHull/otherShield/otherHull plays is decided by (a) whether the
/// struck target is OUR ship and (b) whether the strike was absorbed by the
/// target's shield. Deliberately free of any MonoGame type (pure input
/// <see cref="HitSoundEntity"/> + plain tuples out), so the classification rule
/// and the native once-per-frame guard are unit-testable without a
/// graphics/audio device.
/// </summary>
/// <remarks>
/// <para><b>Native classification</b> (trekigc.cpp:2373-2385): if the target is
/// the local player's ship (<c>m_pmodel == trekClient.GetShip()->GetSourceShip()</c>)
/// play <c>myShieldHitSound</c> or <c>myHullHitSound</c> by the
/// <c>bAbsorbedByShield</c> flag; otherwise <c>otherShieldHitSound</c> /
/// <c>otherHullHitSound</c> by the same flag. The port keys "mine" on the wire's
/// <see cref="ProjectileHullHit.TargetId"/> equalling the local ship id — the
/// client never simulates combat, so it has no model pointer, only the object id
/// the P364 wire carries.</para>
/// <para><b>Once-per-frame guard</b> (native <c>m_bPlayedHitSoundThisFrame</c>,
/// trekigc.cpp:2372): each target plays at most one hit sound per frame; the
/// first strike to register that frame wins. Reduced here over each frame's
/// drained hit list (arrival order), so a target hit several times in one frame
/// sounds once.</para>
/// </remarks>
public enum HitSoundKind
{
    MyShield,
    MyHull,
    OtherShield,
    OtherHull,
}

public static class HitSoundSelector
{
    /// <summary>
    /// Reduce one frame's drained hits to the cues to play: at most one per
    /// distinct target (the native <c>m_bPlayedHitSoundThisFrame</c> guard),
    /// first strike in arrival order winning, classified
    /// my/other × shield/hull by the local ship id. A <see langword="null"/>
    /// <paramref name="myShipId"/> (no local ship yet) makes every hit "other".
    /// The <see cref="Vector3"/> on each yielded tuple is the winning strike's
    /// impact point, so the caller can attenuate "other" cues by distance.
    /// </summary>
    public static IEnumerable<(short TargetId, HitSoundKind Kind, Vector3 Position)> Select(
        IEnumerable<HitSoundEntity> hits, short? myShipId)
    {
        var seen = new HashSet<short>();
        foreach (var h in hits)
        {
            if (!seen.Add(h.TargetId)) continue;      // one cue per target per frame
            yield return (h.TargetId, Classify(h.TargetId, h.AbsorbedByShield, myShipId), h.Position);
        }
    }

    /// <summary>The pure my/other × shield/hull classification (the port of
    /// native's branch at trekigc.cpp:2373-2385).</summary>
    public static HitSoundKind Classify(short targetId, bool absorbedByShield, short? myShipId)
    {
        bool mine = myShipId is { } me && targetId == me;
        return (mine, absorbedByShield) switch
        {
            (true, true) => HitSoundKind.MyShield,
            (true, false) => HitSoundKind.MyHull,
            (false, true) => HitSoundKind.OtherShield,
            (false, false) => HitSoundKind.OtherHull,
        };
    }
}

/// <summary>
/// P463 — the pure distance → volume mapping for the hit one-shots. "My" hits
/// are cockpit-range cues (full, non-positional — native plays myShieldHitSound /
/// myHullHitSound at the full <c>SFXGain</c>); "other" hits are world sounds
/// played at the impacted target, so they attenuate with listener distance the
/// way the existing positional cues do (<see cref="MissileAmbientSoundCurve"/>),
/// and sit a touch quieter overall (native stacks the "other" pair at priority
/// −1.6, sounddef.mdl:262-264, so a dense skirmish doesn't drown the cockpit
/// bed with distant impacts).
/// </summary>
public static class HitSoundCurve
{
    /// <summary>Distance (world units) at which a distant "other" hit reaches
    /// silence. Matches the projectile/missile positional convention
    /// (<see cref="MissileAmbientSoundCurve.AudibleRadius"/>).</summary>
    public const float AudibleRadius = 6000f;

    /// <summary>Peak volume (0..1) of a MY hit one-shot — a nearby, full-volume
    /// cockpit cue (native myShieldHitSound/myHullHitSound at SFXGain).</summary>
    public const float MyVolume = 0.9f;

    /// <summary>Peak volume (0..1) of a distant-but-close "other" hit — capped
    /// below "my" to mirror the native −1.6 priority cut.</summary>
    public const float OtherPeakVolume = 0.6f;

    /// <summary>Linear listener→source falloff in [0,1], full at distance 0 to
    /// silence at <see cref="AudibleRadius"/> (negative/zero → 1, past → 0),
    /// the same shape as the existing positional curves.</summary>
    public static float Falloff(float distance)
    {
        if (distance <= 0f) return 1f;
        if (distance >= AudibleRadius) return 0f;
        return 1f - distance / AudibleRadius;
    }

    /// <summary>Volume (0..<see cref="OtherPeakVolume"/>) for an "other" hit at
    /// <paramref name="distance"/> from the listener.</summary>
    public static float OtherVolume(float distance) => Falloff(distance) * OtherPeakVolume;
}
