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
    /// A null/empty roster (snapshot not arrived yet) yields a waiting line.</summary>
    public static IReadOnlyList<Row> Build(SideRoster? roster, int selectedIndex, bool amMissionOwner)
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

        int sel = selectedIndex < 0 ? 0 : selectedIndex % count;
        for (int i = 0; i < count; i++)
        {
            var s = roster!.Sides[i];
            char marker = i == sel ? '>' : ' ';
            rows.Add(new Row(Kind.Item,
                $"{marker} {AsciiSafe(s.Name),-20}  (players: {s.PlayerCount})",
                Highlight: i == sel));
        }

        rows.Add(Row.Blank());
        if (amMissionOwner) rows.Add(Row.Of(Kind.Hint, "R  RANDOMIZE TEAMS"));
        return rows;
    }

    /// <summary>Strip characters outside printable ASCII 32..126 (see
    /// <see cref="ScoreboardLayout.AsciiSafe"/>).</summary>
    public static string AsciiSafe(string s) => ScoreboardLayout.AsciiSafe(s);
}
