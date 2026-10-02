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

    // ── P481 — eliminated-side picker ────────────────────────────────────────

    /// <summary>⭐ The eliminated-side picker: an inactive (P455b) side is
    /// excluded from the selectable list (native TeamScreen removes / disables
    /// an empty-inactive team, teamscreen.cpp:210/:2523), so the player can only
    /// request a still-active side.</summary>
    [Fact]
    public void SelectableSideIds_excludes_inactive_sides()
    {
        var roster = Roster((1, 3, "A"), (2, 5, "B"), (3, 0, "C"));
        bool Active(short id) => id != 3;   // side 3 is eliminated

        var pickable = SideSelectLayout.SelectableSideIds(roster, Active);
        Assert.Equal(new short[] { 1, 2 }, pickable);

        // Without a predicate (or all-active), every side is pickable.
        Assert.Equal(new short[] { 1, 2, 3 }, SideSelectLayout.SelectableSideIds(roster, null));
        Assert.Equal(new short[] { 1, 2, 3 }, SideSelectLayout.SelectableSideIds(roster, _ => true));
    }

    /// <summary>⭐ The eliminated side is rendered "(DESTROYED)" (matching the
    /// scoreboard's P455b wording / native TeamScreen's "destroyed" spelling),
    /// muted and never highlighted — so it is visible but visibly not choosable.</summary>
    [Fact]
    public void Build_marks_inactive_side_destroyed_and_never_highlights_it()
    {
        var roster = Roster((1, 3, "Alpha"), (2, 5, "Bravo"), (3, 0, "Charlie"));
        bool Active(short id) => id != 3;

        var rows = SideSelectLayout.Build(roster, selectedIndex: 0, amMissionOwner: false, isSideActive: Active);
        var joined = string.Join("\n", rows.Select(r => r.Text));

        // The inactive side is spelled destroyed and not a selectable Item row.
        Assert.Contains("Charlie", joined);
        Assert.Contains("(DESTROYED)", joined);
        Assert.DoesNotContain("(players:", string.Join("\n",
            rows.Where(r => r.Text.Contains("Charlie")).Select(r => r.Text)));
        Assert.Equal(SideSelectLayout.Kind.Muted,
            rows.Single(r => r.Text.Contains("Charlie")).Kind);
        Assert.DoesNotContain(rows, r => r.Text.Contains("Charlie") && r.Highlight);
    }
}
