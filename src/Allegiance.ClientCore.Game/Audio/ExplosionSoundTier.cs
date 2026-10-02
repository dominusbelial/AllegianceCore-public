namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>Native's three explosion sound tiers (the <c>largeExplosionSound</c> /
/// <c>mediumExplosionSound</c> / <c>smallExplosionSound</c> cues,
/// sounddef.mdl:268-270) chosen by the blast <c>scale</c> (radius) —
/// <c>scale &gt; 100</c> large, <c>&gt; 10</c> medium, else small
/// (<c>src/WinTrek/trekigc.cpp:97-101</c>).</summary>
public enum ExplosionSoundTier
{
    /// <summary><c>scale &gt; 100</c> → <c>largeExplosionSound</c> =
    /// <c>explosionshockwave.ogg</c> (ThreeDSound 1000).</summary>
    Large,

    /// <summary><c>10 &lt; scale ≤ 100</c> → <c>mediumExplosionSound</c> =
    /// <c>ef24.ogg</c> (ThreeDSound 400). Also the generic ship-explosion tier
    /// (<c>explodeShipSound = mediumExplosionSound</c>, sounddef.mdl:276) and the
    /// port's default for a missile detonation.</summary>
    Medium,

    /// <summary><c>scale ≤ 10</c> → <c>smallExplosionSound</c> = <c>ef120.ogg</c>
    /// (ThreeDSound 200).</summary>
    Small,
}

/// <summary>P236 — pure tier selection for an explosion sound, a faithful port of
/// native's <c>scale</c> thresholds (trekigc.cpp:97-101). No MonoGame type, so the
/// &gt;100 / &gt;10 boundaries are unit-testable exactly.</summary>
public static class ExplosionSoundTierMapper
{
    /// <summary>Pick the <see cref="ExplosionSoundTier"/> for a blast of the given
    /// <c>scale</c> (radius in world units): &gt;100 large, &gt;10 medium, else
    /// small — native's threshold set (trekigc.cpp:97-101). A non-positive scale
    /// falls through to Small (nothing bigger than a bare detonation).</summary>
    public static ExplosionSoundTier ForScale(float scale)
    {
        if (scale > 100f) return ExplosionSoundTier.Large;
        if (scale > 10f) return ExplosionSoundTier.Medium;
        return ExplosionSoundTier.Small;
    }
}
