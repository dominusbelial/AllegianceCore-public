using System.Collections.Generic;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P281 — pure (MonoGame-free) layout of the post-connect SIDE-SELECT screen.
/// The server now defers a joining peer's spawn to a side-select step: after
/// LOGONACK it pushes a SIDE_ROSTER(211) snapshot of the joinable sides and
/// holds the peer (no ship, no SET_CLUSTER) until it sends FM_C_POSITIONREQ(39)
/// naming its side. This layout turns that roster into the ASCII-safe display
/// rows <see cref="SideSelectOverlayRenderer"/> draws, following the same
/// discipline as <see cref="ShopLayout"/>/<see cref="ScoreboardLayout"/>: pure,
/// headless-testable, every string printable ASCII 32..126 (the HUD spritefonts
/// bake ASCII only — P214/p217 lesson).
/// </summary>
public static class SideSelectLayout
{
    /// <summary>Presentation bucket for a row; the renderer styles by this.</summary>
    public enum Kind
    {
        Title,
        Hint,
        Blank,
        Info,
        Item,
        Muted,
    }

    /// <summary>One display row.</summary>
    public readonly record struct Row(Kind Kind, string Text, bool Highlight = false)
    {
        public static Row Of(Kind kind, string text) => new(kind, text);
        public static Row Blank() => new(Kind.Blank, "");
    }

    /// <summary>Build the display rows for the given roster snapshot and the
    /// highlighted <paramref name="selectedIndex"/> (clamped into range).
    /// The randomize-teams hint is appended only when
    /// <paramref name="amMissionOwner"/> is true (the server owner-gates the
    /// message anyway; surfacing it only to the owner keeps the UI honest).
    /// A null/empty roster (snapshot not arrived yet) yields a waiting line.
    /// <para>P481 — an optional <paramref name="isSideActive"/> predicate lets
    /// the eliminated-side picker show native's inactive-state: an inactive
    /// side is rendered <c>(DESTROYED)</c> (native TeamScreen paints an
    /// empty-inactive team "destroyed", teamscreen.cpp:318) and excluded from
    /// selection. <paramref name="selectedIndex"/> is an index into the
    /// <b>selectable</b> (active) side list, matching how
    /// <see cref="SelectableSideIds"/> feeds the picker.</summary>
    public static IReadOnlyList<Row> Build(
        SideRoster? roster, int selectedIndex, bool amMissionOwner,
        Func<short, bool>? isSideActive = null)
    {
        var rows = new List<Row>();

        rows.Add(Row.Of(Kind.Title, "SELECT SIDE"));
        rows.Add(Row.Of(Kind.Hint, "UP/DOWN SELECT   ENTER CONFIRM"));
        rows.Add(Row.Blank());

        int count = roster?.Sides.Length ?? 0;
        if (count == 0)
        {
            rows.Add(Row.Of(Kind.Muted, "WAITING FOR SIDE LIST FROM SERVER..."));
            rows.Add(Row.Blank());
            if (amMissionOwner) rows.Add(Row.Of(Kind.Hint, "R  RANDOMIZE TEAMS"));
            return rows;
        }

        var selectable = SelectableSideIds(roster, isSideActive);
        int sel = selectedIndex < 0 ? 0 : selectedIndex % Math.Max(1, selectable.Length);
        short? selectedSide = selectable.Length > 0 ? selectable[sel] : null;

        for (int i = 0; i < count; i++)
        {
            var s = roster!.Sides[i];
            bool active = isSideActive?.Invoke(s.SideId) ?? true;
            if (!active)
            {
                rows.Add(new Row(Kind.Muted,
                    $"    {AsciiSafe(s.Name),-20}  (DESTROYED)"));
                continue;
            }
            bool isSel = s.SideId == selectedSide;
            char marker = isSel ? '>' : ' ';
            rows.Add(new Row(Kind.Item,
                $"{marker} {AsciiSafe(s.Name),-20}  (players: {s.PlayerCount})",
                Highlight: isSel));
        }

        rows.Add(Row.Blank());
        if (amMissionOwner) rows.Add(Row.Of(Kind.Hint, "R  RANDOMIZE TEAMS"));
        return rows;
    }

    /// <summary>P481 — the ordered side ids a player may pick, excluding any
    /// side reported inactive (eliminated) by the
    /// <paramref name="isSideActive"/> predicate. When the predicate is null
    /// (or a side is unknown), the side is considered active (native
    /// <c>SideActive(sideID)</c> defaults active, matching the client world's
    /// <c>IsSideActive</c>). Kept in roster order so
    /// the picker's up/down cycling is stable and deterministic.</summary>
    public static short[] SelectableSideIds(SideRoster? roster, Func<short, bool>? isSideActive = null)
    {
        if (roster is null) return Array.Empty<short>();
        return roster.Sides
            .Where(s => isSideActive?.Invoke(s.SideId) ?? true)
            .Select(s => s.SideId)
            .ToArray();
    }

    /// <summary>Strip characters outside printable ASCII 32..126 (see
    /// <see cref="ScoreboardLayout.AsciiSafe"/>).</summary>
    public static string AsciiSafe(string s) => ScoreboardLayout.AsciiSafe(s);
}
