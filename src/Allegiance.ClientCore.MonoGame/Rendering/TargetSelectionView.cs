using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P345 — the per-frame descriptor for the player's OWN <c>T</c>/<c>G</c>
/// autopilot-target selection bracket (the "square thing" expected for the
/// click-and-go target). Unlike <see cref="LockBracketView"/> (a server-side
/// missile-<em>lock progress</em> concept: charging → full) there is no charge/lock
/// state to encode — selecting a target is an instant client-side pick. This is
/// just the target's projected on-screen centre plus whether autopilot is
/// currently ENGAGED toward it (selected-but-idle vs actively flying), which drives
/// the brighter engaged visual. Plain data only. <c>null</c> on the renderer side
/// means "no target selected or it is out of view — draw no bracket".
/// </summary>
/// <param name="Center">The selected target's projected on-screen centre, viewport px.</param>
/// <param name="Engaged">True when the player autopilot is engaged toward this
/// target; false when merely selected (<c>T</c> picked it, <c>G</c> not engaged).</param>
/// <param name="CharacterName">The target ship pilot's name; null for a non-ship
/// target (station/asteroid/warp have no pilot to show).</param>
/// <param name="HullFraction">The target ship's live hull fraction 0..1; null for
/// a non-ship target.</param>
/// <param name="ShieldFraction">The target ship's live shield fraction 0..1; null
/// for a non-ship target.</param>
public readonly record struct TargetSelectionView(
    Vector2 Center,
    bool Engaged,
    string? CharacterName = null,
    float? HullFraction = null,
    float? ShieldFraction = null);
