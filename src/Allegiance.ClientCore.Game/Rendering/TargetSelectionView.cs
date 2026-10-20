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
/// <param name="CharacterName">The selected ship's pilot name or station name;
/// null when the selected object has no name readout. The legacy property name
/// is retained for callers.</param>
/// <param name="HullFraction">The target ship's live hull fraction 0..1; null for
/// a non-ship target.</param>
/// <param name="ShieldFraction">The target ship's live shield fraction 0..1; null
/// for a non-ship target.</param>
/// <param name="IsEnemy">P376 — true when the target is a SHIP belonging to a
/// different side than the local player (red bracket), false when it is a same-side
/// ship (blue bracket), null for a non-ship target (station/asteroid/warp), which
/// keeps the neutral colour.</param>
/// <param name="OreFraction">P423 — the target's live remaining He3 ore as a
/// 0..1 fraction, populated ONLY for an ASTEROID target (from the live
/// ASTEROIDS_UPDATE(179) sync). Non-null selects the distinct asteroid ore
/// readout rendered alongside (not instead of) the generic health bar. Null for
/// ship/station/warp targets.</param>
public readonly record struct TargetSelectionView(
    Vector2 Center,
    bool Engaged,
    string? CharacterName = null,
    float? HullFraction = null,
    float? ShieldFraction = null,
    bool? IsEnemy = null,
    float? OreFraction = null,
    float? Distance = null,
    float? ClosingSpeed = null,
    HudColor? SideColor = null,
    float? ScreenRadius = null);
