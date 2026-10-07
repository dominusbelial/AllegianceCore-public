using System.Collections.Generic;
using System.Linq;
using Allegiance.ClientCore.State;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>
/// P574 — the pure, headless-testable "which world explosions get voiced, and
/// as what" decision for the <c>EXPLOSION_EVENT(207)</c> sound drain. Given the
/// incoming explosion events plus the local ship's sector and position, it
/// returns the one-shots to play (category type, sound tier, distance-scaled
/// volume), applying the native rules:
/// <list type="bullet">
/// <item><b>No double-play of missiles:</b> a <c>c_etMissile</c> detonation is
/// already voiced by <c>MissileSoundPlanner</c> (which hears the missile vanish
/// from <c>world.Missiles</c>), so it is skipped here.</item>
/// <item><b>Same-sector only:</b> native plays an explosion sound only when the
/// player's current cluster is the blast's cluster (<c>trekigc.cpp:90-93</c>).</item>
/// <item><b>Positional attenuation:</b> the one-shot volume falls with listener
/// distance via <see cref="ExplosionSoundCurve"/> (native
/// <c>GenericSoundSource(position, velocity)</c>).</item>
/// <item><b>Tier per scale:</b> the cue (large/medium/small) follows the blast
/// <c>scale</c> via <see cref="ExplosionSoundTierMapper.ForScale"/>
/// (<c>trekigc.cpp:96-101</c>).</item>
/// </list>
/// Pure — no MonoGame or <c>SoundManager</c> types, so it is unit-testable
/// without an audio device.
/// </summary>
public static class ExplosionSoundRouter
{
    /// <summary>Resolve the one-shots to play. <paramref name="mySector"/>/<paramref name="anchor"/>
    /// are the local ship's sector and position (null when not flying — then no
    /// positional sound can be placed). Returns an ordered list of
    /// (category-type, tier, volume); entries with an inaudible distance are
    /// dropped.</summary>
    public static List<(int Type, ExplosionSoundTier Tier, float Volume)> Resolve(
        IEnumerable<ExplosionSoundEntity> events, short? mySector, SimVec? anchor)
    {
        var result = new List<(int, ExplosionSoundTier, float)>();
        foreach (var e in events)
        {
            // Already voiced by MissileSoundPlanner — skip to avoid double-play.
            if (e.Type == Allegiance.Core.Sim.ExplosionType.Missile) continue;
            if (mySector is not { } sec || e.ClusterId != sec) continue;   // native cluster gate
            if (anchor is not { } a) continue;                             // no listener
            float dist = (e.Position - a).Length();
            float volume = ExplosionSoundCurve.Volume(dist);
            if (volume <= 0f) continue;
            result.Add((e.Type, ExplosionSoundTierMapper.ForScale(e.Scale), volume));
        }
        return result;
    }
}
