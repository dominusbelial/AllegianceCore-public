using System;
using System.Collections.Generic;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P625 — the LIVE, data-driven keybinding table backing the H help overlay.
///
/// Replaces P257's hard-coded, drift-prone reference table. Today the table is
/// generated on demand from the live <see cref="ActionMap"/> (owned by
/// <see cref="KeyBindingStore"/>), so a binding changed in the rebind popup is
/// reflected in the help overlay immediately — the exact gap P257's doc-comment
/// called out ("It WILL drift the next time a binding is added elsewhere …
/// the very problem this task attempts to solve architecturally").
///
/// The <see cref="Entry"/> / <see cref="Group"/> shapes are preserved so the
/// existing balanced-column <see cref="HelpOverlayLayout"/> and
/// <see cref="HelpOverlayRenderer"/> keep working unchanged. Names/categories
/// come from <see cref="InputActionCatalog"/> (native TK names from
/// trekinput.cpp:1459 where they exist). Strings remain pure ASCII so the
/// baked spritefonts render every cell.
/// </summary>
public sealed class KeyBindingTable
{
    /// <summary>One binding: the current key label and what it does.</summary>
    public readonly record struct Entry(string Key, string Description);

    /// <summary>One headed group of bindings.</summary>
    public readonly record struct Group(string Title, IReadOnlyList<Entry> Entries)
    {
        public float Height =>
            HelpOverlayLayout.RowHeight + HelpOverlayLayout.TitleGap
            + Entries.Count * HelpOverlayLayout.RowHeight;
    }

    /// <summary>
    /// The full current keybinding reference, generated from the LIVE map so a
    /// rebind is reflected without a rebuild. Group order follows the catalogue
    /// (which mirrors native's command categories).
    /// </summary>
    public static IReadOnlyList<Group> All => KeyBindingStore.Instance.Map.ToHelpGroups();

    /// <summary>Count of every group (for the balanced column layout).</summary>
    public static int GroupCount => All.Count;
}
