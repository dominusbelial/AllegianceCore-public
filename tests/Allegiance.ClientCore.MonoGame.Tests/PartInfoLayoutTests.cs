using System;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for the P276 part-info / connector-compatibility detail
/// panel: <see cref="DataHullTypeIGC.CanMountPart"/> (the native
/// <c>CanMount</c> mount-mask test) and <see cref="PartInfoLayout"/> (the
/// highlighted-item detail rows). No graphics device — the real PCore001.igc
/// catalogue drives both a bitwise synthetic test and real fit/no-fit cases.
/// </summary>
public class PartInfoLayoutTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private static readonly ShopCatalog Catalog =
        ShopCatalog.TryLoad(ArtRoot)
        ?? throw new InvalidOperationException($"PCore001.igc not loadable from {ArtRoot}");

    /// <summary>A real playable hull that has BOTH a compatible and an
    /// incompatible real weapon in the catalogue — so the panel demonstrates a
    /// genuine fit and no-fit verdict against the same hull.</summary>
    private static DataHullTypeIGC CurrentHull() =>
        Catalog.Hulls.First(h =>
            Catalog.Parts.Any(p => p.EquipmentType == EquipmentType.Weapon && h.CanMountPart(p)) &&
            Catalog.Parts.Any(p => p.EquipmentType == EquipmentType.Weapon && !h.CanMountPart(p)));

    private static ShopModel FreshModel() =>
        new(Catalog, CurrentHull().HullId, Array.Empty<ExpandedPartData>());

    private static ShopModel ModelOnPart(DataPartTypeIGC part)
    {
        var m = FreshModel();
        var flat = Catalog.Hulls.Count + Catalog.Parts.ToList().FindIndex(p => p.PartId == part.PartId);
        m.MoveCursor(flat - m.Cursor);
        return m;
    }

    private static ShopModel ModelOnHull(DataHullTypeIGC hull)
    {
        var m = FreshModel();
        var flat = Catalog.Hulls.ToList().FindIndex(h => h.HullId == hull.HullId);
        m.MoveCursor(flat - m.Cursor);
        return m;
    }

    // Real fixtures that exist in PCore001 for the current hull: one weapon that
    // fits it and one that does not (deterministic across the real core).
    private static DataPartTypeIGC FittingWeapon() =>
        Catalog.Parts.First(p => p.EquipmentType == EquipmentType.Weapon && CurrentHull().CanMountPart(p));

    private static DataPartTypeIGC NonFittingWeapon() =>
        Catalog.Parts.First(p => p.EquipmentType == EquipmentType.Weapon && !CurrentHull().CanMountPart(p));

    // ── CanMountPart: exact native bitwise semantics ───────────────────────────

    [Fact]
    public void Weapon_fits_when_any_hardpoint_mask_covers_it()
    {
        var hull = CurrentHull() with
        {
            Hardpoints =
            [
                new DataHullTypeIGC.Hardpoint(0, 0, "", "", 0x0001, false),
                new DataHullTypeIGC.Hardpoint(0, 0, "", "", 0x0002, false),
            ],
        };
        var baseWeapon = Catalog.Parts.First(p => p.EquipmentType == EquipmentType.Weapon);

        Assert.True(hull.CanMountPart(baseWeapon with { PartMask = 0x0001 }));
        Assert.True(hull.CanMountPart(baseWeapon with { PartMask = 0x0002 })); // covered by 2nd hardpoint
        Assert.False(hull.CanMountPart(baseWeapon with { PartMask = 0x0003 })); // no single hp covers both bits
        Assert.False(hull.CanMountPart(baseWeapon with { PartMask = 0x0008 })); // no hardpoint covers bit 3
    }

    [Fact]
    public void Non_weapon_fits_via_pmEquipment_at_its_et_index()
    {
        var hull = CurrentHull() with
        {
            Hardpoints = [],
            PmEquipment = [0, 0, 0, 0, 0x0004, 0, 0, 0], // ET_Shield = 4
        };
        var shield = Catalog.Parts.First(p => p.EquipmentType == EquipmentType.Shield);

        Assert.True(hull.CanMountPart(shield with { PartMask = 0x0004 }));
        Assert.False(hull.CanMountPart(shield with { PartMask = 0x0008 })); // not covered
    }

    [Fact]
    public void Weapon_fit_is_per_hardpoint_not_pmEquipment()
    {
        var hull = CurrentHull() with
        {
            Hardpoints = [new DataHullTypeIGC.Hardpoint(0, 0, "", "", 0x0040, false)],
            PmEquipment = [0, 0, 0, 0, 0, 0, 0, 0], // pmEquipment[ET_Weapon]=0
        };
        var weapon = Catalog.Parts.First(p => p.EquipmentType == EquipmentType.Weapon);
        // A weapon still fits via the hardpoint mask even though pmEquipment[1]==0.
        Assert.True(hull.CanMountPart(weapon with { PartMask = 0x0040 }));
    }

    // ── PartInfoLayout: real-data detail rows ─────────────────────────────────

    [Fact]
    public void Part_info_shows_real_stats_and_fits_verdict()
    {
        var m = ModelOnPart(FittingWeapon());
        var rows = PartInfoLayout.Build(m);
        var texts = rows.Select(r => r.Text).ToArray();

        Assert.Contains(texts, t => t.Contains(FittingWeapon().Name, StringComparison.Ordinal));
        Assert.Contains(texts, t => t.StartsWith("cost: $", StringComparison.Ordinal) && t.Contains('$'));
        Assert.Contains(texts, t => t.StartsWith("mass ", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.StartsWith("energy/shot ", StringComparison.Ordinal));
        // Compatibility verdict present and positive for a part that really fits.
        Assert.Contains(texts, t => t.Contains("FITS current hull", StringComparison.Ordinal));
    }

    [Fact]
    public void Part_that_does_not_fit_is_marked_accordingly()
    {
        var bad = NonFittingWeapon();
        var m = ModelOnPart(bad);
        var texts = PartInfoLayout.Build(m).Select(r => r.Text).ToArray();

        Assert.Contains(texts, t => t.Contains("DOES NOT FIT current hull", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.Contains("FITS current hull", StringComparison.Ordinal));
    }

    [Fact]
    public void Both_fit_and_no_fit_cases_exist_in_the_real_core()
    {
        // Guards the premise of the two verdict tests above: the current hull
        // has at least one compatible and one incompatible real weapon.
        _ = FittingWeapon();
        _ = NonFittingWeapon();
    }

    [Fact]
    public void Hull_highlight_shows_hull_stats_and_no_compat_section()
    {
        var m = ModelOnHull(CurrentHull());
        var texts = PartInfoLayout.Build(m).Select(r => r.Text).ToArray();

        Assert.Contains(texts, t => t.StartsWith("HULL -", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.StartsWith("max speed ", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.StartsWith("hit points ", StringComparison.Ordinal));
        // Hulls have no connector-compatibility verdict.
        Assert.DoesNotContain(texts, t => t.Contains("FITS", StringComparison.Ordinal));
    }

    [Fact]
    public void Detail_rows_are_ascii_safe()
    {
        foreach (var part in new[] { FittingWeapon(), NonFittingWeapon() })
            foreach (var r in PartInfoLayout.Build(ModelOnPart(part)))
                if (r.Kind != ShopLayout.Kind.Blank)
                    Assert.DoesNotContain(r.Text, c => c < 32 || c > 126);
        foreach (var r in PartInfoLayout.Build(ModelOnHull(CurrentHull())))
            if (r.Kind != ShopLayout.Kind.Blank)
                Assert.DoesNotContain(r.Text, c => c < 32 || c > 126);
    }
}
