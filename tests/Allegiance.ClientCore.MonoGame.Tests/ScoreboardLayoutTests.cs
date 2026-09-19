using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for the P265 scoreboard layout (ordering, ASCII-safety and
/// column alignment). No graphics device — same discipline as
/// <see cref="HelpOverlayTests"/> / <see cref="StarmapLayoutTests"/>.
/// </summary>
public class ScoreboardLayoutTests
{
    private static ScoreRosterEntry E(string name, short side, short kills, short deaths) =>
        new(name, side, kills, deaths);

    /// <summary>⭐ The scoreboard orders by side (ascending) then kills
    /// (descending), so a team's best scorer sits on top — the readable default
    /// for a live roster.</summary>
    [Fact]
    public void Build_orders_by_side_then_kills_descending()
    {
        var rows = ScoreboardLayout.Build(new[]
        {
            E("Low", 0, 1, 0),
            E("Top", 0, 9, 2),
            E("Mid", 1, 4, 0),
            E("Hi", 1, 7, 1),
        });

        Assert.Equal(new[]
        {
            "Top",   // side 0, kills 9
            "Low",   // side 0, kills 1
            "Hi",    // side 1, kills 7
            "Mid",   // side 1, kills 4
        }, rows.Select(r => r.Name).ToArray());
    }

    /// <summary>⭐ Every cell is pure ASCII 32..126 so the spritefonts (which bake
    /// ASCII only) never throw on a hostile/mangled name.</summary>
    [Fact]
    public void Build_scrubs_non_ascii_from_names()
    {
        var rows = ScoreboardLayout.Build(new[]
        {
            E("H\u00e9llo\u2191", 0, 1, 0),   // é (non-ASCII) + ↑ (non-ASCII)
        });

        var name = rows[0].Name;
        Assert.DoesNotContain(name, c => c < 32 || c > 126);
        // The latin-1 0xE9 arrived as a real é; it must be scrubbed to a space.
        Assert.DoesNotContain('\u00e9', name);
    }

    /// <summary>The side renders as its numeric id (the honest real data the
    /// wire carries — this client has no side-name registry).</summary>
    [Fact]
    public void Build_renders_side_as_numeric_label()
    {
        var rows = ScoreboardLayout.Build(new[] { E("Ace", 3, 5, 1) });
        Assert.Equal("S3", rows[0].SideLabel);
    }

    /// <summary>Column widths never collapse below their header label and expand
    /// to fit the longest cell — the renderer's alignment guarantee.</summary>
    [Fact]
    public void ColumnWidths_accommodate_longest_cells_and_headers()
    {
        var rows = new[]
        {
            new ScoreboardLayout.Row("B", "S0", "9", "3"),
            new ScoreboardLayout.Row("A Very Long Pilot Name", "S12", "12345", "7"),
        };
        var w = ScoreboardLayout.ColumnWidths(rows, s => s.Length);   // monospaced stub: width = char count

        Assert.Equal(Math.Max("A Very Long Pilot Name".Length, "NAME".Length), (int)w.Name);
        Assert.Equal(Math.Max("S12".Length, "SIDE".Length), (int)w.Side);
        Assert.Equal(Math.Max("12345".Length, "K".Length), (int)w.Kills);
        Assert.Equal(Math.Max("7".Length, "D".Length), (int)w.Deaths);
    }
}
