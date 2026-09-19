using System;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for the P268 docked/loadout shop layout (row content,
/// markers, ASCII safety). No graphics device — same discipline as
/// <see cref="ScoreboardLayoutTests"/>. Uses the real PCore001 catalogue like
/// <see cref="ShopModelTests"/>.
/// </summary>
public class ShopLayoutTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private static readonly ShopCatalog Catalog =
        ShopCatalog.TryLoad(ArtRoot)
        ?? throw new InvalidOperationException($"PCore001.igc not loadable from {ArtRoot}");

    private static DataHullTypeIGC CurrentHull() => Catalog.Hulls.OrderBy(h => h.HullId).First();
    private static DataHullTypeIGC DifferentHull(short id) => Catalog.Hulls.OrderBy(h => h.HullId).First(h => h.HullId != id);
    private static DataPartTypeIGC FirstWeapon() => Catalog.Parts.First(p => p.EquipmentType == EquipmentType.Weapon);

    private static ShopModel FreshModel() => new(Catalog, CurrentHull().HullId, Array.Empty<ExpandedPartData>());

    private static string RowText(ShopLayout.Row r) => r.Text;

    [Fact]
    public void Build_shows_title_current_hull_and_price()
    {
        var rows = ShopLayout.Build(FreshModel());
        var texts = rows.Select(RowText).ToArray();

        Assert.Contains(texts, t => t.Contains("STATION DOCKED", StringComparison.OrdinalIgnoreCase));
        // Current hull's real name appears, with a real dollar price.
        Assert.Contains(texts, t => t.Contains(CurrentHull().Name, StringComparison.Ordinal) && t.Contains('$'));
        // Pending loadout line echoes the current hull until one is picked.
        Assert.Contains(texts, t => t.StartsWith("hull:", StringComparison.Ordinal) && t.Contains(CurrentHull().Name, StringComparison.Ordinal));
    }

    [Fact]
    public void Choosing_a_hull_changes_the_pending_line_and_adds_a_marker()
    {
        var m = FreshModel();
        var target = DifferentHull(CurrentHull().HullId);
        var hulls = Catalog.Hulls.ToList();
        m.MoveCursor(hulls.FindIndex(h => h.HullId == target.HullId));
        m.Activate();
        m.MoveCursor(m.RowCount - 1 - m.Cursor);   // step off the row so the '*' marker shows

        var texts = ShopLayout.Build(m).Select(RowText).ToArray();
        // The picked hull row carries the '*' chosen marker and its name is in the pending line.
        Assert.Contains(texts, t => t.Contains("* ", StringComparison.Ordinal) && t.Contains(target.Name, StringComparison.Ordinal));
        Assert.Contains(texts, t => t.StartsWith("hull:", StringComparison.Ordinal) && t.Contains(target.Name, StringComparison.Ordinal));
    }

    [Fact]
    public void Toggled_part_rows_carry_a_plus_marker()
    {
        var m = FreshModel();
        var weapon = FirstWeapon();
        var parts = Catalog.Parts.ToList();
        m.MoveCursor(Catalog.Hulls.Count + parts.FindIndex(p => p.PartId == weapon.PartId));
        m.Activate();
        m.MoveCursor(-m.Cursor);                       // step off the row so the '+' marker shows

        var texts = ShopLayout.Build(m).Select(RowText).ToArray();
        Assert.Contains(texts, t => t.Contains("+ ", StringComparison.Ordinal) && t.Contains(weapon.Name, StringComparison.Ordinal));
    }

    /// <summary>⭐ Every row is pure ASCII 32..126 (the HUD spritefonts bake ASCII
    /// only), and all rows carry a dollar-cost like the native shop row
    /// (loadout.cpp:289 "$ %d").</summary>
    [Fact]
    public void All_rows_are_ascii_safe_and_carry_cost()
    {
        var m = FreshModel();
        var rows = ShopLayout.Build(m);
        foreach (var r in rows)
        {
            if (r.Kind == ShopLayout.Kind.Blank) continue;
            Assert.DoesNotContain(r.Text, c => c < 32 || c > 126);
        }
        // Every hull/part item row has a '$' price (native loadout.cpp price col).
        var itemTexts = rows.Where(r => r.Kind == ShopLayout.Kind.Item).Select(RowText).ToArray();
        Assert.NotEmpty(itemTexts);
        Assert.All(itemTexts, t => Assert.Contains('$', t));
    }
}
