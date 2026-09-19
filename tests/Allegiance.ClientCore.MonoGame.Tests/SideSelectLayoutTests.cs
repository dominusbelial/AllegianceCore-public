using System.Collections.Generic;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for the P281 side-select layout — every emitted row is
/// printable ASCII 32..126 (the HUD spritefonts bake ASCII only) and lists
/// each joinable side from the SIDE_ROSTER(211) snapshot (name + player count),
/// highlighting the current selection.
/// </summary>
public class SideSelectLayoutTests
{
    private static SideRoster Roster(params (short id, int players, string name)[] sides) =>
        new()
        {
            Sides = sides
                .Select(s => new SideRoster.SideRecord(s.id, CivId: 0, (short)s.players, s.name))
                .ToArray(),
        };

    private static void AssertPureAscii(IReadOnlyList<SideSelectLayout.Row> rows)
    {
        foreach (var r in rows)
            foreach (var ch in r.Text)
                Assert.InRange(ch, '\u0020', '\u007e');
    }

    [Fact]
    public void Build_yields_title_two_side_rows_and_hint()
    {
        var rows = SideSelectLayout.Build(Roster((1, 3, "Humans"), (2, 5, "Iron Coalition")), 0, false);
        AssertPureAscii(rows);

        var joined = string.Join("\n", rows.Select(r => r.Text));
        Assert.Contains("SELECT SIDE", joined);
        Assert.Contains("ENTER", joined);

        // Title, hint, blank, then the two side rows.
        Assert.Equal(SideSelectLayout.Kind.Title, rows[0].Kind);
        Assert.Equal(2, rows.Count(r => r.Kind == SideSelectLayout.Kind.Item));
        Assert.Contains("Humans", rows[3].Text);
        Assert.Contains("Iron Coalition", rows[4].Text);
        Assert.Contains("players:", rows[3].Text);
        Assert.Contains("players: 5", rows[4].Text);
    }

    [Fact]
    public void Highlights_the_selected_row()
    {
        var rows0 = SideSelectLayout.Build(Roster((1, 3, "A"), (2, 5, "B")), 0, false);
        Assert.True(rows0[3].Highlight);
        Assert.False(rows0[4].Highlight);

        var rows1 = SideSelectLayout.Build(Roster((1, 3, "A"), (2, 5, "B")), 1, false);
        Assert.False(rows1[3].Highlight);
        Assert.True(rows1[4].Highlight);
    }

    [Fact]
    public void Null_or_empty_roster_shows_wait_message()
    {
        var rowsNull = SideSelectLayout.Build(null, 0, false);
        AssertPureAscii(rowsNull);
        Assert.Contains("WAITING FOR SIDE LIST", string.Join("\n", rowsNull.Select(r => r.Text)));

        var rowsEmpty = SideSelectLayout.Build(Roster(), 0, false);
        Assert.Contains("WAITING FOR SIDE LIST", string.Join("\n", rowsEmpty.Select(r => r.Text)));
    }

    [Fact]
    public void Randomize_hint_shown_only_to_mission_owner()
    {
        var rows = SideSelectLayout.Build(Roster((1, 3, "A")), 0, amMissionOwner: true);
        var joined = string.Join("\n", rows.Select(r => r.Text));
        Assert.Contains("RANDOMIZE TEAMS", joined);

        var nonOwner = SideSelectLayout.Build(Roster((1, 3, "A")), 0, amMissionOwner: false);
        Assert.DoesNotContain("RANDOMIZE TEAMS", string.Join("\n", nonOwner.Select(r => r.Text)));
    }
}
