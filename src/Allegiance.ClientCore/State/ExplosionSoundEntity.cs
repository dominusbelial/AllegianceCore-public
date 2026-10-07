using Allegiance.Core.Collision;

namespace Allegiance.ClientCore.State;

/// <summary>
/// P574 — a pending one-shot WORLD-EXPLOSION SOUND event, one per real
/// <c>EXPLOSION_EVENT(207)</c> broadcast (the same server blast the renderer's
/// <see cref="ExplosionEntity"/> plays visually). It carries everything the
/// audio pass needs to voice the native tiered positional sound
/// (<c>trekigc.cpp:86-101</c>): the <see cref="Type"/> category (so a missile
/// detonation — already voiced by <c>MissileSoundPlanner</c> — is skipped), the
/// <see cref="ClusterId"/> (same-sector gate), the blast <see cref="Position"/>
/// (listener-distance attenuation) and the native <see cref="Scale"/>
/// (<see cref="Allegiance.ClientCore.MonoGame.Audio.ExplosionSoundTierMapper.ForScale"/>
/// picks the large/medium/small cue). A transient fire-and-forget cue, drained
/// by Game1's audio slice in Update.
/// </summary>
public sealed record ExplosionSoundEntity(int Type, short ClusterId, Vector3 Position, float Scale);
