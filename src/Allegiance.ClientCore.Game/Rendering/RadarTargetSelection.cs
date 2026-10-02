namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P436 — the pure decision of whether a radar contact is the player's OWN
/// currently-selected target. Kept OUT of the render loop so the flagging is
/// unit-testable without a graphics device (the port's established
/// pure-logic-layer discipline, same as <see cref="RadarBracketLayout"/>).
/// The game layer converts the selected autopilot target's kind
/// (<c>Game1.TargetKind</c>) to the matching <see cref="RadarBracketRenderer.RadarContactType"/>
/// before calling this; a selection whose kind has no radar blip (Asteroid)
/// is mapped to null by the caller and flags nothing.
/// </summary>
public static class RadarTargetSelection
{
    /// <summary>Whether the radar contact (of <paramref name="contactType"/> and
    /// <paramref name="contactId"/>) is the player's selected target (whose kind
    /// maps to <paramref name="selectedType"/> and id is <paramref name="selectedId"/>).
    /// A contact is the target only when BOTH the type AND the id match; a null
    /// selection, or an id/type mismatch, flags nothing.</summary>
    public static bool IsSelected(
        RadarBracketRenderer.RadarContactType contactType, short contactId,
        RadarBracketRenderer.RadarContactType? selectedType, short? selectedId)
    {
        if (selectedId is not { } sid || selectedType is not { } st)
            return false;
        return st == contactType && sid == contactId;
    }
}
