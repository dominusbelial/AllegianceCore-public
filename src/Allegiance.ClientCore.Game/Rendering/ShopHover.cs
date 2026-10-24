namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P787b — the docked loadout list's hover rule (SIM-0321 / SIM-0327), the pure
/// extraction of native <c>LoadoutScreenImpl::OnListHover</c>
/// (<c>WinTrek/loadout.cpp:1173-1190</c>):
/// <list type="bullet">
/// <item>Hovering row <c>nSel ≥ 0</c> shows that row's info and plays
/// <c>mouseoverSound</c> only when the hovered row CHANGED
/// (<c>if (m_nLastHoverIndex != nSel) trekClient.PlaySoundEffect(mouseoverSound)</c>,
/// loadout.cpp:1177-1178).</item>
/// <item>Hovering nothing (<c>nSel == -1</c>) clears the info pane, cues
/// nothing, and resets the last-hover index (<c>m_nLastHoverIndex = nSel</c>
/// runs for every call, loadout.cpp:1186) — so re-entering the same row later
/// cues again.</item>
/// </list>
/// The same cue covers the custom-loadout slot images' <c>MouseEnter</c>
/// (<c>WinTrek/load.cpp:233-237</c>): a slot entry is a discrete list row in
/// the port's slot-driven shop, and moving between two rows, or off-list and
/// back, produces exactly the same per-change cues native's enter/leave pairs
/// produce.
/// </summary>
public static class ShopHover
{
    /// <summary>Apply the rule: <paramref name="hoveredRow"/> is the row under
    /// the pointer (null/-1 = none). Returns the new last-hover index and
    /// whether <c>mouseoverSound</c> cues this frame.</summary>
    public static (int LastHover, bool Cue) Hover(int? hoveredRow, int lastHover)
    {
        int row = hoveredRow ?? -1;
        if (row < 0)
            return (-1, false);
        return (row, row != lastHover);
    }
}
