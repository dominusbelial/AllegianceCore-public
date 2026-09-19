using System.Collections.Generic;
using System.Linq;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P273 — pure (MonoGame-free) layout of the docked teleport pane. Turns the
/// <see cref="TeleportPaneModel"/> into ASCII-safe <see cref="ShopLayout.Row"/>
/// rows that <see cref="DockedScreenRenderer"/> draws (reusing the P268
/// translucent-panel renderer verbatim — the P273 pane is the same kind of
/// small key-cycled list). Same discipline as <see cref="ShopLayout"/> /
/// <see cref="ScoreboardLayout"/>: headless-testable, every string printable
/// ASCII 32..126.
///
/// <para>Mirrors the native teleport list (<c>teleportpane.cpp</c>): a
/// destinations list of boardable ships and stations, with the current row
/// highlighted. Sorting is by name (native's default <c>sortName</c>,
/// <c>teleportpane.cpp:27-30</c>); the type/sector column sorts are noted as a
/// follow-up.</para>
/// </summary>
public static class TeleportLayout
{
    /// <summary>Build the display rows for the given teleport model.</summary>
    public static IReadOnlyList<ShopLayout.Row> Build(TeleportPaneModel m)
    {
        var rows = new List<ShopLayout.Row>();

        rows.Add(ShopLayout.Row.Of(ShopLayout.Kind.Title, "STATION DOCKED - TELEPORT"));
        rows.Add(ShopLayout.Row.Of(ShopLayout.Kind.Hint, "UP/DOWN NAV   SPACE GO   B BACK"));
        rows.Add(ShopLayout.Row.Blank());

        rows.Add(ShopLayout.Row.Of(ShopLayout.Kind.Section, "BOARD A TEAMMATE'S SHIP (GUNNER)"));
        if (m.Ships.Count == 0)
        {
            rows.Add(ShopLayout.Row.Of(ShopLayout.Kind.Muted, "  (no boardable ships)"));
        }
        else
        {
            for (int i = 0; i < m.Ships.Count; i++)
            {
                var s = m.Ships[i];
                char marker = m.Cursor == i ? '>' : ' ';
                rows.Add(new ShopLayout.Row(ShopLayout.Kind.Item,
                    $"{marker} {AsciiSafe(s.Name),-28} turrets:{s.TurretSlots,-3} [SHIP]",
                    Highlight: m.Cursor == i));
            }
        }
        rows.Add(ShopLayout.Row.Blank());

        rows.Add(ShopLayout.Row.Of(ShopLayout.Kind.Section, "TELEPORT TO FRIENDLY STATION"));
        if (m.Stations.Count == 0)
        {
            rows.Add(ShopLayout.Row.Of(ShopLayout.Kind.Muted, "  (no other friendly stations)"));
        }
        else
        {
            for (int i = 0; i < m.Stations.Count; i++)
            {
                var st = m.Stations[i];
                int flat = m.Ships.Count + i;
                char marker = m.Cursor == flat ? '>' : ' ';
                rows.Add(new ShopLayout.Row(ShopLayout.Kind.Item,
                    $"{marker} {AsciiSafe(st.Name),-28}              [STATION]",
                    Highlight: m.Cursor == flat));
            }
        }
        rows.Add(ShopLayout.Row.Blank());
        rows.Add(ShopLayout.Row.Of(ShopLayout.Kind.Muted, "T returns to the hangar loadout"));

        return rows;
    }

    private static string AsciiSafe(string s) => ScoreboardLayout.AsciiSafe(s);
}
