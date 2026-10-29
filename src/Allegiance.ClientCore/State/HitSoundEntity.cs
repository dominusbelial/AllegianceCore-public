using Allegiance.Core.Collision;

namespace Allegiance.ClientCore.State;

/// <summary>
/// P463 — a one-shot <em>hit sound</em> event: the client-side audio view of a
/// real <c>PROJECTILE_HULL_HIT(213)</c> broadcast, carrying exactly what the hit
/// sound needs and nothing the spark renderer cares about. The MonoGame client's
/// audio slice drains these via <see cref="ClientWorld.TakeHitSoundEvents"/>
/// each frame and reduces them to at most one cue per target (native's
/// <c>m_bPlayedHitSoundThisFrame</c>, trekigc.cpp:2372), choosing my/other ×
/// shield/hull and attenuating "other" cues by listener distance.
/// </summary>
/// <remarks>Kept as a <b>separate</b> stream from <see cref="HullHitEntity"/>
/// (the impact-spark buffer) so the audio pass — which runs in <c>Game1</c>'s
/// Update, <em>before</em> the renderer drains the sparks in Draw — doesn't steal
/// the renderer's hits. Both streams are fed from the same
/// <c>ClientWorld.Apply(ProjectileHullHit)</c>.</remarks>
public readonly record struct HitSoundEntity(
    short TargetId,
    short ClusterId,
    bool AbsorbedByShield,
    short SourceId,
    Vector3 Position,
    /// <summary>P871 — the wire hit-surface normal (<c>normalize(position1 −
    /// position2)</c>, <c>projectileIGC.cpp:224-225</c>), which native's
    /// credited-damage force feedback turns into the rumble direction
    /// (<c>WinTrek/trekigc.cpp:4296-4301</c>).</summary>
    Vector3 Normal = default);
