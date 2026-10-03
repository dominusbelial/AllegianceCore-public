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
        var weapon = FirstWeapon();
        var hull = Catalog.Hulls.First(h => h.CanMountPart(weapon));
        var m = new ShopModel(Catalog, hull.HullId, Array.Empty<ExpandedPartData>());
        ShopModelTests.MountViaSlots(m, weapon.PartId);
        m.SwitchTab(ShopTab.Parts);                   // the slot list view

        var texts = ShopLayout.Build(m).Select(RowText).ToArray();
        // The filled weapon slot row shows the weapon's name in a slot ([GUN]) row.
        Assert.Contains(texts, t => t.Contains(weapon.Name, StringComparison.Ordinal) && t.Contains('['));
    }

    /// <summary>⭐ P519 — the pending loadout is compatible-only: changing to a
    /// hull that can't host a mounted part DROPS that part eagerly (native
    /// ReplaceLoadout mounts only what fits), so the layout never needs a
    /// "DROPPED on launch" warning — every part in the pending loadout fits.
    /// Under the new model a part that fits the current hull but not the new one
    /// is simply gone after the swap, with no warning (nothing to warn about).</summary>
    [Fact]
    public void Pending_loadout_drops_parts_that_do_not_fit_the_new_hull()
    {
        // A (current, weapon, non-fitting other hull) triple scanned from the
        // real core (not a naive lowest-hull fixture — see ShopModelTests).
        var (cur, w, target) = NonFitTriple();

        var m = new ShopModel(Catalog, cur.HullId, new[]
        {
            new ExpandedPartData(w.PartId, 20, 0, ExpandedPartData.FullyMounted),
        });
        Assert.Contains(m.ChosenParts, p => p.PartId == w.PartId);   // mounted on the current hull

        m.CycleHullTo(target.HullId);                                // swap to the non-mounting hull

        Assert.Equal(target.HullId, m.EffectiveHullId);
        Assert.DoesNotContain(m.ChosenParts, p => p.PartId == w.PartId);   // eagerly dropped

        var texts = ShopLayout.Build(m).Select(RowText).ToArray();
        // No "DROPPED on launch" warning is needed — the part is already gone.
        Assert.DoesNotContain(texts, t => t.Contains("DROPPED on launch", StringComparison.Ordinal));
    }

    /// <summary>A (current hull, weapon, other hull that CANNOT mount that
    /// weapon) triple that provably exists in PCore001 (scan, not assumed).</summary>
    private static (DataHullTypeIGC Cur, DataPartTypeIGC W, DataHullTypeIGC Target) NonFitTriple()
    {
        foreach (var cur in Catalog.Hulls)
            foreach (var w in Catalog.Parts.Where(p => p.EquipmentType == EquipmentType.Weapon))
            {
                if (!cur.CanMountPart(w)) continue;
                foreach (var t in Catalog.Hulls)
                    if (t.HullId != cur.HullId && !t.CanMountPart(w))
                        return (cur, w, t);
            }
        throw new InvalidOperationException("PCore001 has no (hull,weapon,non-fitting-other-hull) triple");
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

    // ── P468 — price column, summary line, grey-out ─────────────────────────

    /// <summary>⭐ P468/P519 — a real launcher's slot row shows its REAL price
    /// (P441 threaded the launcher price through From<Kind>Launcher), not the old
    /// "$n/a". Open the magazine slot and check its offered launcher rows.</summary>
    [Fact]
    public void Launcher_row_shows_a_real_price_not_n_a()
    {
        var hull = Catalog.Hulls.First(h => h.PmEquipment.Length > (int)EquipmentType.Magazine
            && h.PmEquipment[(int)EquipmentType.Magazine] != 0);
        var m = new ShopModel(Catalog, hull.HullId, Array.Empty<ExpandedPartData>());
        m.SwitchTab(ShopTab.Parts);
        int idx = 0;
        while (m.Slots[idx].Type != EquipmentType.Magazine) idx++;
        m.MoveCursor(idx - m.Cursor);
        m.Activate();                                    // open the magazine slot

        var rows = ShopLayout.Build(m);
        var partRows = rows.Where(r => r.Kind == ShopLayout.Kind.Item).ToList();
        Assert.NotEmpty(partRows);
        Assert.All(partRows, r => Assert.DoesNotContain("$n/a", r.Text, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(partRows, r => r.Text.Contains('$'));
    }

    /// <summary>⭐ P468 — the pending-loadout price summary shows new loadout
    /// value, current ship value and the cost (new − current).</summary>
    [Fact]
    public void Price_summary_lists_new_value_current_value_and_cost()
    {
        var current = CurrentHull();
        var target = DifferentHull(current.HullId);
        var hulls = Catalog.Hulls.ToList();
        var m = FreshModel();
        m.MoveCursor(hulls.FindIndex(h => h.HullId == target.HullId) - m.Cursor);
        m.Activate();

        var texts = ShopLayout.Build(m).Select(RowText).ToArray();
        Assert.Contains(texts, t => t.StartsWith("new loadout value:", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.StartsWith("current ship value:", StringComparison.Ordinal));
        // Either a cost or a refund line is present.
        Assert.Contains(texts, t => t.StartsWith("cost:", StringComparison.Ordinal) || t.StartsWith("refund:", StringComparison.Ordinal));
    }

    /// <summary>⭐ P468 — with a small wallet, unaffordable item rows are marked
    /// Greyed (the renderer styles them muted). PCore001's Parts table is all
    /// price-0, so the greyed row is a real paid launcher from the Launchers
    /// table (P441 priced it).</summary>
    [Fact]
    public void Unaffordable_item_rows_are_greyed()
    {
        var m = new ShopModel(Catalog, CurrentHull().HullId, Array.Empty<ExpandedPartData>(), money: 0);
        var rows = ShopLayout.Build(m);
        Assert.Contains(rows, r => r.Kind == ShopLayout.Kind.Item && r.Greyed);
    }

    /// <summary>⭐ P468 — a refused launch renders a visible "cannot afford"
    /// reason row so the player sees why L did nothing.</summary>
    [Fact]
    public void Refused_launch_renders_visible_reason()
    {
        var current = Catalog.Hulls.OrderBy(h => h.Price).First();
        var target = Catalog.Hulls.Where(h => h.Price > current.Price).OrderBy(h => h.Price).First();
        long cost = (long)target.Price - current.Price;
        var m = new ShopModel(Catalog, current.HullId, Array.Empty<ExpandedPartData>(), money: (int)cost - 1);
        m.MoveCursor(Catalog.Hulls.ToList().FindIndex(h => h.HullId == target.HullId) - m.Cursor);
        m.Activate();
        Assert.False(m.TryLaunch());

        var texts = ShopLayout.Build(m).Select(RowText).ToArray();
        Assert.Contains(texts, t => t.Contains("cannot afford", StringComparison.OrdinalIgnoreCase));
    }
}
