using System;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for the P268 docked/loadout shop model. No graphics device —
/// same discipline as <see cref="ScoreboardLayoutTests"/>. The catalogue is the
/// real PCore001.igc under the artwork dir (the same core the server boots),
/// matching the existing DockLoadoutLiveTests convention, so the tests exercise
/// real hull/part ids, names and prices.
/// </summary>
public class ShopModelTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private static readonly ShopCatalog Catalog = LoadCatalog();
    private static ShopCatalog LoadCatalog() =>
        ShopCatalog.TryLoad(ArtRoot)
        ?? throw new InvalidOperationException($"PCore001.igc not loadable from {ArtRoot}");

    private static ShopModel Model() =>
        new(Catalog, currentHullId: CurrentHull().HullId, currentParts: Array.Empty<ExpandedPartData>());

    // A hull guaranteed present in the catalogue, used as the "current ship" hull.
    private static DataHullTypeIGC CurrentHull() =>
        Catalog.Hulls.OrderBy(h => h.HullId).First();

    private static DataHullTypeIGC DifferentHull(short currentId) =>
        Catalog.Hulls.OrderBy(h => h.HullId).First(h => h.HullId != currentId);

    private static DataPartTypeIGC FirstWeapon() =>
        Catalog.Parts.First(p => p.EquipmentType == EquipmentType.Weapon);

    private static int HullIndex(short hullId)
    {
        var list = Catalog.Hulls.ToList();
        return list.FindIndex(h => h.HullId == hullId);
    }

    private static int PartIndex(short partId)
    {
        var list = Catalog.Parts.ToList();
        return list.FindIndex(p => p.PartId == partId);
    }

    private static int FlatIndexOfHull(short hullId) => HullIndex(hullId);

    private static int FlatIndexOfPart(DataPartTypeIGC part) => Catalog.Hulls.Count + PartIndex(part.PartId);

    [Fact]
    public void Cursor_clamps_at_both_bounds()
    {
        var m = Model();
        Assert.Equal(0, m.Cursor);
        m.MoveCursor(-5);
        Assert.Equal(0, m.Cursor);                   // floor
        m.MoveCursor(10_000);
        Assert.Equal(m.RowCount - 1, m.Cursor);      // ceiling
    }

    [Fact]
    public void Effective_hull_defaults_to_current_until_a_hull_is_picked()
    {
        var m = Model();
        Assert.Equal(CurrentHull().HullId, m.EffectiveHullId);
        Assert.Null(m.ChosenHullId);
    }

    /// <summary>⭐ Choosing a hull (cursor on its row + Activate) makes it the
    /// effective hull — the exact payload the launch sends.</summary>
    [Fact]
    public void Activate_on_a_hull_row_picks_that_hull()
    {
        var m = Model();
        var target = DifferentHull(CurrentHull().HullId);
        m.MoveCursor(FlatIndexOfHull(target.HullId) - m.Cursor);
        Assert.Equal(target.HullId, m.CursorHull!.Value.HullId);
        m.Activate();
        Assert.Equal(target.HullId, m.ChosenHullId);
        Assert.Equal(target.HullId, m.EffectiveHullId);
    }

    /// <summary>⭐ Toggling a part adds it to the chosen loadout (a real mounted
    /// part with a mount id + amount), and toggling again removes it.</summary>
    [Fact]
    public void Activate_toggles_a_part_in_and_out_of_the_loadout()
    {
        var m = Model();
        var weapon = FirstWeapon();
        m.MoveCursor(FlatIndexOfPart(weapon) - m.Cursor);
        Assert.Equal(weapon.PartId, m.CursorPart!.Value.PartId);

        m.Activate();
        Assert.Single(m.ChosenParts);
        Assert.Equal(weapon.PartId, m.ChosenParts[0].PartId);
        Assert.Equal(ExpandedPartData.FullyMounted, m.ChosenParts[0].FractionMounted);

        m.Activate();
        Assert.Empty(m.ChosenParts);
    }

    /// <summary>⭐ Weapons get a real ammo-reserve default amount; one-off mounts get 1.</summary>
    [Fact]
    public void Weapon_parts_get_ammo_amount_one_off_mounts_get_one()
    {
        var m = Model();
        var weapon = FirstWeapon();
        m.MoveCursor(FlatIndexOfPart(weapon) - m.Cursor);
        m.Activate();
        Assert.Equal((short)20, m.ChosenParts[0].Amount);

        var other = Catalog.Parts.First(p => p.EquipmentType != EquipmentType.Weapon);
        m.MoveCursor(FlatIndexOfPart(other) - m.Cursor);
        m.Activate();
        Assert.Equal((short)1, m.ChosenParts[1].Amount);
    }

    /// <summary>⭐ The live-proof payload: a hull different from the current one
    /// plus a real weapon part — exactly what P268's launch sends to prove a real
    /// purchase takes effect.</summary>
    [Fact]
    public void Live_proof_payload_differs_from_default_and_carries_a_real_weapon()
    {
        var m = Model();
        short current = CurrentHull().HullId;
        var target = DifferentHull(current);
        var weapon = FirstWeapon();

        m.MoveCursor(FlatIndexOfHull(target.HullId) - m.Cursor);
        m.Activate();                                   // pick a different hull
        m.MoveCursor(FlatIndexOfPart(weapon) - m.Cursor);
        m.Activate();                                   // add a real weapon part

        Assert.NotEqual(current, m.EffectiveHullId);    // genuinely different hull
        Assert.Single(m.ChosenParts);
        Assert.Equal(weapon.PartId, m.ChosenParts[0].PartId);
    }

    // ── P284 — tech-gated shop offer ─────────────────────────────────────────

    private static StaticCore LoadStaticCore() =>
        StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, ShopCatalog.CoreFileName)));

    /// <summary>⭐ P284 — the specific gap Domingo flagged: a real weapon with a
    /// non-zero TtbmRequired (part 190 'ER Nanite 2', requires exactly bit 114,
    /// unlocked by dev 87) and a real hull with a non-zero TtbmRequired (hull 25
    /// 'Bomber', requires bits 1,16,35, unlocked by dev 37) are ABSENT from the
    /// garrison's filtered shop while their governing developments are
    /// unresearched (side dev mask = civ base only), and PRESENT once the devs'
    /// effect bits are OR-ed into the side's development techs — the exact P283
    /// containment (<c>need ⊆ garrisonLocal | sideDev</c>) reused by the shop.</summary>
    [Fact]
    public void Shop_filter_excludes_dev_gated_weapon_and_hull_until_researched()
    {
        const short erNanite2Part = 190;
        const short dev87 = 87;   // 'ER Nanite 2' → adds bit 114 (unlocks part 190)
        const short bomberHull = 25;
        const short dev37 = 37;   // 'Bomber' → adds bit 35 (unlocks hull 25)

        var core = LoadStaticCore();
        var civ = core.Civilizations.Values.First(c => c.CivilizationId == 1);
        var garrisonLocal = core.StationTypes.Values.First(s => s.Name.Contains("Garrison", StringComparison.OrdinalIgnoreCase)).TtbmLocal;

        // Sanity — the two targets really sit in the UNFILTERED catalogue (the
        // filter is what hides them), and both have a real non-zero requirement.
        Assert.Contains(Catalog.Hulls, h => h.HullId == bomberHull);
        Assert.Contains(Catalog.Parts, p => p.PartId == erNanite2Part);
        Assert.NotEmpty(Catalog.Parts.First(p => p.PartId == erNanite2Part).TtbmRequired);
        Assert.NotEmpty(Catalog.Hulls.First(h => h.HullId == bomberHull).TtbmRequired);

        // BEFORE — side dev mask is just the civ's base techs (nothing researched).
        var sideDevBefore = TechTreeBitMask.FromBytes(civ.TtbmBaseTechs);
        var before = Catalog.FilteredFor(garrisonLocal, sideDevBefore);
        Assert.DoesNotContain(before.Hulls, h => h.HullId == bomberHull);
        Assert.DoesNotContain(before.Parts, p => p.PartId == erNanite2Part);

        // AFTER — complete dev 87 and dev 37 for real: their effect bits are
        // OR-ed into Side.DevelopmentTechs, the post-OR mask the client stores
        // from SIDE_TECH_CHANGE(75).
        var sideDevAfter = sideDevBefore
            .Or(TechTreeBitMask.FromBytes(core.Developments[dev87].TtbmEffects))
            .Or(TechTreeBitMask.FromBytes(core.Developments[dev37].TtbmEffects));
        var after = Catalog.FilteredFor(garrisonLocal, sideDevAfter);
        Assert.Contains(after.Hulls, h => h.HullId == bomberHull);
        Assert.Contains(after.Parts, p => p.PartId == erNanite2Part);
    }

    /// <summary>⭐ P284 — graceful degradation: when the side's dev mask or the
    /// station's local techs cannot be resolved (null), FilteredFor must return
    /// the full unfiltered catalogue (the P268 degraded behaviour), never a
    /// blanked shop.</summary>
    [Fact]
    public void Shop_filter_degrades_to_full_catalogue_when_tech_unknown()
    {
        var full = Catalog;
        Assert.Same(full, full.FilteredFor(null, TechTreeBitMask.FromBytes(new byte[TechTreeBitMask.ByteCount])));
        Assert.Same(full, full.FilteredFor(new byte[TechTreeBitMask.ByteCount], null));
    }
}
