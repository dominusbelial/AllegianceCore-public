namespace Allegiance.ClientCore.State;

/// <summary>
/// P373 — a one-shot "new contact" (first-sighting) sound cue the client world
/// has observed and queued for <c>Game1</c> to drain into the audio manager.
/// Mirrors the <see cref="ClientSoundEvent"/> drain-then-forget lifecycle
/// (<c>ClientWorld.TakeSoundEvents</c>): the cue is recorded when a ship first
/// becomes visible to the local peer — a brand-new ship entering the entity
/// table with a real identity, or a previously-<c>IsGone</c> (P370) ship coming
/// back into view — and is cleared once drained so it plays exactly once per
/// genuine sighting edge.
/// </summary>
/// <param name="ShipId">The newly-visible ship's id.</param>
/// <param name="IsEnemy">True if the ship is on a different, non-allied side
/// than the local player (hostile) — the port has no alliance concept, so
/// "allied" collapses to "own side only" (<c>ShipEntity.SideId !=
/// ClientWorld.MySideId</c>). False = same-side/friendly.</param>
public readonly record struct ClientNewContactCue(short ShipId, bool IsEnemy);
