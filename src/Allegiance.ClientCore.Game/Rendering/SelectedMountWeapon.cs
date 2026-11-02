using Allegiance.Core;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// HUD-18 — resolve the weapon on a SPECIFIC fixed mount from a granted loadout
/// and the core part catalogue. This is native
/// <c>m_ship-&gt;GetMountedPart(ET_Weapon, m_selectedWeapon)</c> behind
/// <c>trekClient.GetWeapon()</c> (<c>clintlib/clintlib.cpp:1585-1588</c>), which
/// the target pane's in-range solve uses (<c>WinTrek/indicator.cpp:194-212</c>).
/// The mount is the <b>selected</b> one — not the first weapon in the loadout,
/// which is what the server's former lead-message computation used.
/// </summary>
public static class SelectedMountWeapon
{
    /// <summary>
    /// The catalogue weapon mounted on <paramref name="mountId"/>, or null when
    /// that mount is empty, holds a non-weapon part, or the part is not in the
    /// loaded core.
    /// </summary>
    public static DataPartTypeIGC.Weapon? Resolve(
        IReadOnlyList<ExpandedPartData>? loadout,
        IEnumerable<DataPartTypeIGC> parts,
        int mountId)
    {
        if (loadout is null) return null;
        foreach (var mounted in loadout)
        {
            if (mounted.MountId != mountId) continue;
            foreach (var part in parts)
                // Mount numbers are per equipment type. A shield/dispenser
                // at mount 0 must not hide a weapon later in the loadout.
                // clintlib.cpp:1585-1588: GetMountedPart(ET_Weapon, selected).
                if (part.PartId == mounted.PartId && part.Type is DataPartTypeIGC.Weapon weapon)
                    return weapon;
        }
        return null;
    }
}
