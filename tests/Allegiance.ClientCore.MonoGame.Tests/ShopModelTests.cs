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

    /// <summary>A (current hull, weapon, other hull that CANNOT mount that
    /// weapon) triple that provably exists in PCore001 — found by scanning the
    /// real core, not assumed from a specific hull id (a naive "lowest hull +
    /// first fitting weapon" fixture can hit a weapon that fits every hull).</summary>
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

    /// <summary>A (current hull, weapon, other hull that DOES mount that weapon)
    /// triple that provably exists in PCore001 (scan, not assumed).</summary>
    private static (DataHullTypeIGC Cur, DataPartTypeIGC W, DataHullTypeIGC Target) FitTriple()
    {
        foreach (var cur in Catalog.Hulls)
            foreach (var w in Catalog.Parts.Where(p => p.EquipmentType == EquipmentType.Weapon))
            {
                if (!cur.CanMountPart(w)) continue;
                foreach (var t in Catalog.Hulls)
                    if (t.HullId != cur.HullId && t.CanMountPart(w))
                        return (cur, w, t);
            }
        throw new InvalidOperationException("PCore001 has no (hull,weapon,fitting-other-hull) triple");
    }

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

    // ── P335 — real hull swap: effective-hull fit prediction ───────────────

    /// <summary>⭐ P335 — WillSwapHull is true exactly when a genuinely
    /// different hull is chosen, and false before any choice or when the chosen
    /// hull equals the current one (the de-pick case).</summary>
    [Fact]
    public void WillSwapHull_is_true_only_for_a_genuinely_different_chosen_hull()
    {
        var m = Model();
        Assert.False(m.WillSwapHull);                      // nothing chosen yet

        // De-pick the current hull (Activate on its row clears the choice).
        m.MoveCursor(FlatIndexOfHull(CurrentHull().HullId) - m.Cursor);
        m.Activate();
        Assert.False(m.WillSwapHull);
        Assert.Equal(CurrentHull().HullId, m.EffectiveHullId);

        // A genuinely different hull => real swap.
        var target = DifferentHull(CurrentHull().HullId);
        m.MoveCursor(FlatIndexOfHull(target.HullId) - m.Cursor);
        m.Activate();
        Assert.True(m.WillSwapHull);
        Assert.Equal(target.HullId, m.EffectiveHullId);
    }

    /// <summary>⭐ P335 — a chosen part that can't mount the CHOSEN hull is
    /// predicted dropped (the server's P334 CanMount drop policy), so the UI can
    /// warn before launch rather than promise a part the server discards.</summary>
    [Fact]
    public void Chosen_part_that_does_not_fit_the_chosen_hull_is_predicted_dropped()
    {
        var (cur, w, target) = NonFitTriple();

        var m = new ShopModel(Catalog, cur.HullId, Array.Empty<ExpandedPartData>());
        m.MoveCursor(FlatIndexOfPart(w) - m.Cursor);
        m.Activate();                                       // toggle W on
        m.MoveCursor(FlatIndexOfHull(target.HullId) - m.Cursor);
        m.Activate();                                       // pick hull B (can't host W)

        Assert.True(m.WillSwapHull);
        Assert.Equal(target.HullId, m.EffectiveHullId);
        Assert.Contains(m.ChosenPartsThatDontFitEffectiveHull, p => p.PartId == w.PartId);
    }

    /// <summary>⭐ P335 — when the chosen part DOES fit the chosen hull (or no
    /// swap is pending) nothing is flagged as dropped — the shop never warns
    /// when the server will keep the part.</summary>
    [Fact]
    public void Chosen_part_that_fits_the_chosen_hull_is_not_flagged_dropped()
    {
        var (cur, w, target) = FitTriple();

        // No swap pending: nothing flagged even though the effective hull is the
        // current one (a same-hull loadout is free-mounted; nothing is dropped).
        var m = new ShopModel(Catalog, cur.HullId, Array.Empty<ExpandedPartData>());
        m.MoveCursor(FlatIndexOfPart(w) - m.Cursor);
        m.Activate();
        Assert.Empty(m.ChosenPartsThatDontFitEffectiveHull);

        // A different hull that DOES host the part: also nothing flagged.
        m.MoveCursor(FlatIndexOfHull(target.HullId) - m.Cursor);
        m.Activate();
        Assert.True(m.WillSwapHull);
        Assert.DoesNotContain(m.ChosenPartsThatDontFitEffectiveHull, p => p.PartId == w.PartId);
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

    /// <summary>⭐ P488 — the shop's obsolete filter (native hangar
    /// <c>!IsObsolete</c>, load.cpp:1606,1819): once the side has out-teched a
    /// hull (a buyable successor exists, stationIGC.h:394-396), the shop hides
    /// the base hull and offers only the successor. Red on unfixed code, where
    /// the P470 predicate (<c>GroupID ≥ 0 && CanBuy</c>) alone keeps both.</summary>
    [Fact]
    public void Shop_filter_hides_an_obsolete_hull_and_offers_its_successor_when_outteched()
    {
        var core = LoadStaticCore();
        var garrisonLocal = core.StationTypes.Values.First(s => s.Name.Contains("Garrison", StringComparison.OrdinalIgnoreCase)).TtbmLocal;
        var all = new TechTreeBitMask(Enumerable.Repeat((byte)0xFF, TechTreeBitMask.ByteCount).ToArray());

        // A group-0 hull with a real successor chain; walk to its leaf by id
        // (independent of the code under test).
        var baseHull = Catalog.Hulls.First(h => h.GroupId >= 0 && h.SuccessorHullId >= 0);
        short leaf = baseHull.HullId;
        var seen = new HashSet<short> { baseHull.HullId };
        while (Catalog.Hulls.First(h => h.HullId == leaf).SuccessorHullId is { } nx && nx >= 0 && seen.Add(nx))
            leaf = nx;
        Assert.NotEqual(baseHull.HullId, leaf);

        // Sanity: the base hull is present in the UNFILTERED catalogue.
        Assert.Contains(Catalog.Hulls, h => h.HullId == baseHull.HullId);

        var filtered = Catalog.FilteredFor(garrisonLocal, all);
        // Out-teched: the base hull is obsolete and therefore hidden…
        Assert.DoesNotContain(filtered.Hulls, h => h.HullId == baseHull.HullId);
        // …and its successor leaf is offered instead.
        Assert.Contains(filtered.Hulls, h => h.HullId == leaf);
        Console.WriteLine($"P488-SHOP-OBSOLETE: side out-teched -> shop hides '{baseHull.Name}' ({baseHull.HullId}) and offers '{Catalog.Hulls.First(h => h.HullId == leaf).Name}' ({leaf})");
    }

    /// <summary>⭐ P488 — negative half: with NO successor tech researched (side
    /// dev mask = civ base, and a local mask), an obsolete-filtering bug must not
    /// hide a hull that has no buyable successor — the base hull stays offered.</summary>
    [Fact]
    public void Shop_filter_keeps_a_hull_that_is_not_outteched()
    {
        var core = LoadStaticCore();
        var civ = core.Civilizations.Values.First(c => c.CivilizationId == 1);
        var garrisonLocal = core.StationTypes.Values.First(s => s.Name.Contains("Garrison", StringComparison.OrdinalIgnoreCase)).TtbmLocal;
        var sideDev = TechTreeBitMask.FromBytes(civ.TtbmBaseTechs);   // nothing researched

        // A group-0 hull with a successor that the base side has NOT researched,
        // and that the base can itself buy at the garrison. We need it to pass the
        // CanBuy term, so discover one whose TtbmRequired is satisfied by the
        // union already (i.e. not hidden by the tech filter in the first place).
        var baseHull = Catalog.Hulls.First(h => h.GroupId >= 0
            && h.SuccessorHullId >= 0
            && TechTreeBitMask.RequiredTechsBuyable(h.TtbmRequired, garrisonLocal, sideDev)
            && !TechTreeBitMask.RequiredTechsBuyable(Catalog.Hulls.First(x => x.HullId == h.SuccessorHullId).TtbmRequired, garrisonLocal, sideDev));

        var filtered = Catalog.FilteredFor(garrisonLocal, sideDev);
        Assert.Contains(filtered.Hulls, h => h.HullId == baseHull.HullId);   // base buyable, successor not → not obsolete → shown
        Console.WriteLine($"P488-SHOP-NOT-OBSOLETE: side NOT out-teched -> shop still offers '{baseHull.Name}' ({baseHull.HullId})");
    }


    /// <summary>⭐ P468 — fail CLOSED: when the side's dev mask or the station's
    /// local techs cannot be resolved (null), FilteredFor must NOT return the
    /// full unfiltered catalogue (the P268 fail-open behaviour that made
    /// undeveloped/unaffordable items look buyable). It returns exactly the
    /// items with an all-clear tech requirement — the set that is correct
    /// regardless of the unknown mask.</summary>
    [Fact]
    public void Shop_filter_fails_closed_to_no_tech_items_when_tech_unknown()
    {
        // The gated fixtures (bomber hull 25, ER Nanite part 190) must NOT be
        // offered when either tech term is unknown.
        const short erNanite2Part = 190;
        const short bomberHull = 25;
        var none = Array.Empty<byte>();
        var clear = TechTreeBitMask.Empty;

        var expectedHulls = Catalog.Hulls.Where(h => TechTreeBitMask.RequiredTechsBuyable(h.TtbmRequired, none, clear)).ToList();
        var expectedParts = Catalog.Parts.Where(p => TechTreeBitMask.RequiredTechsBuyable(p.TtbmRequired, none, clear)).ToList();

        // Unknown side dev (station known).
        var fcNullDev = Catalog.FilteredFor(new byte[TechTreeBitMask.ByteCount], null);
        Assert.NotSame(Catalog, fcNullDev);
        Assert.Equal(expectedHulls.Count, fcNullDev.Hulls.Count);
        Assert.Equal(expectedParts.Count, fcNullDev.Parts.Count);
        Assert.DoesNotContain(fcNullDev.Hulls, h => h.HullId == bomberHull);
        Assert.DoesNotContain(fcNullDev.Parts, p => p.PartId == erNanite2Part);

        // Unknown station local (side dev known).
        var fcNullLocal = Catalog.FilteredFor(null, clear);
        Assert.NotSame(Catalog, fcNullLocal);
        Assert.Equal(expectedHulls.Count, fcNullLocal.Hulls.Count);
        Assert.Equal(expectedParts.Count, fcNullLocal.Parts.Count);
        Assert.DoesNotContain(fcNullLocal.Hulls, h => h.HullId == bomberHull);
        Assert.DoesNotContain(fcNullLocal.Parts, p => p.PartId == erNanite2Part);
    }

    // ── P394 — pre-seeded loadout ──────────────────────────────────────────

    /// <summary>An <see cref="ExpandedPartData"/> for <paramref name="part"/>
    /// with explicit mount id + amount + fraction, for building a realistic
    /// "currently-mounted" loadout (the BUY_LOADOUT_ACK tail) in tests.</summary>
    private static ExpandedPartData Mounted(DataPartTypeIGC part, byte mount, short amount,
        byte fraction = ExpandedPartData.FullyMounted) =>
        new(part.PartId, amount, mount, fraction);

    /// <summary>The first catalogue part of a given equipment type (a real
    /// PCore001 part), for driving the shop's part rows in tests.</summary>
    private static DataPartTypeIGC FirstPartOf(EquipmentType et) =>
        Catalog.Parts.First(p => p.EquipmentType == et);

    /// <summary>The first real missile-launcher offer (a Magazine-type part
    /// resolved via the launcher bridge, from <c>Catalog.Launchers</c>).</summary>
    private static LauncherOffer FirstMissileLauncher() =>
        Catalog.Launchers!.First(l => l.Kind == LauncherKind.Missile);

    /// <summary>Flat row index of a launcher offer (hulls, then parts, then
    /// launchers — the same layout ShopModel exposes).</summary>
    private static int LauncherFlatIndex(short partId)
    {
        var list = Catalog.Launchers!.ToList();
        return Catalog.Hulls.Count + Catalog.Parts.Count + list.FindIndex(l => l.Part.PartId == partId);
    }

    /// <summary>The <see cref="EquipmentType"/> of a part id in this catalogue
    /// (a real core part or a launcher offer), mirroring ShopModel.LookupPart.</summary>
    private static EquipmentType TypeOf(short partId)
    {
        foreach (var p in Catalog.Parts) if (p.PartId == partId) return p.EquipmentType;
        foreach (var l in Catalog.Launchers!) if (l.Part.PartId == partId) return l.Part.EquipmentType;
        throw new InvalidOperationException($"part {partId} not in catalogue");
    }

    /// <summary>⭐ P394 — a ShopModel built with the ship's currently-mounted
    /// parts opens with them already pre-seeded into the pending loadout
    /// (ChosenParts), exactly the set L relaunches with. CurrentParts stays an
    /// untouched snapshot. Weapons AND non-weapons are both seeded.</summary>
    [Fact]
    public void Shop_opens_with_current_parts_pre_seeded()
    {
        var weapon = FirstWeapon();
        var nonWeapon = Catalog.Parts.First(p => p.EquipmentType != EquipmentType.Weapon);
        // P437 — a well-formed ACK tail already carries per-type mounts: the
        // weapon at its first hardpoint (0) and the non-weapon at ITS single
        // mount 0 (not a global position). RenumberMounts preserves these.
        var current = new[]
        {
            Mounted(weapon, 0, 20),
            Mounted(nonWeapon, 0, 1),
        };

        var m = new ShopModel(Catalog, CurrentHull().HullId, current);

        // ChosenParts == the seeded loadout, verbatim.
        Assert.Equal(2, m.ChosenParts.Count);
        Assert.Equal(current, m.ChosenParts);

        // The display snapshot is unchanged and independent.
        Assert.Equal(2, m.CurrentParts.Count);
        Assert.Equal(current, m.CurrentParts);
        Assert.NotSame(m.CurrentParts, m.ChosenParts);
    }

    /// <summary>⭐ P394 — a part that arrives pre-seeded is treated exactly like
    /// a freshly-chosen one by the toggle: the FIRST Activate press removes it
    /// (existing remove-if-present behaviour, ShopModel.cs:225-228), it does not
    /// double-add or get stuck.</summary>
    [Fact]
    public void Pre_seeded_part_toggles_off_on_first_press()
    {
        var shield = Catalog.Parts.First(p => p.EquipmentType == EquipmentType.Shield);
        var m = new ShopModel(Catalog, CurrentHull().HullId, new[] { Mounted(shield, 0, 1) });
        Assert.Single(m.ChosenParts);

        m.MoveCursor(FlatIndexOfPart(shield) - m.Cursor);
        m.Activate();

        Assert.Empty(m.ChosenParts);                       // removed, not doubled
    }

    /// <summary>⭐ P394 — seeding copies the part's REAL mounted Amount and
    /// FractionMounted rather than re-deriving the toggle-on defaults
    /// (DefaultAmount = 20 for a weapon / FullyMounted). A partly-used weapon
    /// must not be topped up to 20 ammo on every relaunch.</summary>
    [Fact]
    public void Seeding_preserves_real_amount_and_fraction()
    {
        var weapon = FirstWeapon();
        const byte partlyUsed = 100;                       // < FullyMounted (240)
        var m = new ShopModel(Catalog, CurrentHull().HullId,
            new[] { Mounted(weapon, 0, 3, partlyUsed) });

        var seeded = m.ChosenParts.Single();
        Assert.Equal((short)3, seeded.Amount);             // NOT re-derived to 20
        Assert.Equal(partlyUsed, seeded.FractionMounted);  // NOT forced to FullyMounted
    }

    /// <summary>⭐ P394 — weapons are deliberately included in the seed (the
    /// judgement call in the writeup §weapon-seeding): the pending loadout
    /// reflects "here's what you have" including the guns, idempotent with
    /// MountCombatWeapon and future-proof.</summary>
    [Fact]
    public void Weapons_are_included_in_the_seed()
    {
        var weapon = FirstWeapon();
        var m = new ShopModel(Catalog, CurrentHull().HullId, new[] { Mounted(weapon, 0, 20) });
        Assert.Contains(m.ChosenParts, p => p.PartId == weapon.PartId);
    }

    /// <summary>⭐ P394 — seeding renumbers the pending loadout to the shop's
    /// per-equipment-type mount convention (P437): every non-Weapon type lands
    /// at its single mount 0, Weapons number 0..N — regardless of the ACK tail's
    /// wire mount ids, keeping the same invariant Activate's RenumberMounts
    /// enforces.</summary>
    [Fact]
    public void Seeded_parts_renumber_to_per_type_mount_ids()
    {
        var weapon = FirstWeapon();
        var nonWeapon = Catalog.Parts.First(p => p.EquipmentType != EquipmentType.Weapon);
        // Deliberately non-contiguous / non-zero wire mount ids from a "real" ACK.
        var m = new ShopModel(Catalog, CurrentHull().HullId, new[]
        {
            Mounted(weapon, 7, 20),
            Mounted(nonWeapon, 2, 1),
        });

        // Weapon -> its first hardpoint 0; non-Weapon -> ITS single mount 0.
        Assert.Equal(0, m.ChosenParts[0].MountId);
        Assert.Equal(0, m.ChosenParts[1].MountId);
        Assert.Contains(m.ChosenParts, p => p.PartId == weapon.PartId && p.MountId == 0);
        Assert.Contains(m.ChosenParts, p => p.PartId == nonWeapon.PartId && p.MountId == 0);
    }

    /// <summary>⭐ P394 ∩ P335 — a PRE-SEEDED part that has no compatible
    /// hardpoint on a DIFFERENT chosen hull is predicted dropped exactly like a
    /// freshly-chosen incompatible part: seeding must not make it special.
    /// (w fits the current hull cur — so it's a legitimate current mount — but
    /// not the swapped-to target hull.)</summary>
    [Fact]
    public void Pre_seeded_part_that_does_not_fit_chosen_hull_is_predicted_dropped()
    {
        var (cur, w, target) = NonFitTriple();

        var m = new ShopModel(Catalog, cur.HullId, new[] { Mounted(w, 0, 20) });
        Assert.Contains(m.ChosenParts, p => p.PartId == w.PartId);   // seeded in

        m.MoveCursor(FlatIndexOfHull(target.HullId) - m.Cursor);
        m.Activate();                                                // choose hull B

        Assert.True(m.WillSwapHull);
        Assert.Equal(target.HullId, m.EffectiveHullId);
        Assert.Contains(m.ChosenPartsThatDontFitEffectiveHull, p => p.PartId == w.PartId);
    }

    /// <summary>⭐ P394 ∩ P335 — a pre-seeded part that DOES fit the chosen hull
    /// is not flagged dropped (the server keeps it), same as a freshly-chosen
    /// fitting part.</summary>
    [Fact]
    public void Pre_seeded_part_that_fits_chosen_hull_is_not_flagged_dropped()
    {
        var (cur, w, target) = FitTriple();

        var m = new ShopModel(Catalog, cur.HullId, new[] { Mounted(w, 0, 20) });
        m.MoveCursor(FlatIndexOfHull(target.HullId) - m.Cursor);
        m.Activate();                                                // choose hull B (hosts w)

        Assert.True(m.WillSwapHull);
        Assert.DoesNotContain(m.ChosenPartsThatDontFitEffectiveHull, p => p.PartId == w.PartId);
    }

    /// <summary>⭐ P394 — the WillSwapHull gate: a SAME-HULL loadout is
    /// free-mounted server-side and nothing is dropped (P334 scopes its CanMount
    /// policy to the hull-change path), so even a pre-seeded part that cannot
    /// hardpoint-mount the unchanged hull is NOT flagged "dropped" — matching the
    /// UI's own gating (ShopLayout.cs:111-123) and the server's real behaviour.
    /// Seeding must not surface a false warning for a legitimately free-mounted
    /// part on every dock.</summary>
    [Fact]
    public void SameHull_nonswap_loadout_never_flags_parts_as_dropped()
    {
        // target is a hull that does NOT CanMount w; using it as the CURRENT
        // hull with w pre-seeded (free-mounted) simulates the P394 seeding case.
        var (_, w, target) = NonFitTriple();

        var m = new ShopModel(Catalog, target.HullId, new[] { Mounted(w, 0, 20) });
        Assert.Contains(m.ChosenParts, p => p.PartId == w.PartId);   // seeded in
        Assert.False(m.WillSwapHull);                                // no hull picked
        Assert.Empty(m.ChosenPartsThatDontFitEffectiveHull);         // nothing dropped
    }

    // ── P437 — per-equipment-type mount numbering + single-slot non-weapon ──

    /// <summary>⭐ P437 — Domingo's exact reproduction: buy a weapon +
    /// afterburner + cloak + shield FIRST, then a real missile launcher. The
    /// launcher must land at MOUNT 0 (its Magazine type's single slot), NOT at
    /// global position 4 — that is the whole bug (both the lock and fire gates
    /// read ET_Magazine@0, so a launcher at any other mount is inert). Every
    /// non-weapon type occupies its single mount 0; the weapon occupies mount 0
    /// within the Weapon type.</summary>
    [Fact]
    public void Launcher_bought_after_weapon_ab_cloak_shield_lands_at_mount_zero()
    {
        var m = Model();
        var weapon = FirstWeapon();
        var afterburner = FirstPartOf(EquipmentType.Afterburner);
        var cloak = FirstPartOf(EquipmentType.Cloak);
        var shield = FirstPartOf(EquipmentType.Shield);
        var launcher = FirstMissileLauncher();

        m.MoveCursor(FlatIndexOfPart(weapon) - m.Cursor);    m.Activate();   // weapon first
        m.MoveCursor(FlatIndexOfPart(afterburner) - m.Cursor); m.Activate(); // then AB
        m.MoveCursor(FlatIndexOfPart(cloak) - m.Cursor);     m.Activate();   // then cloak
        m.MoveCursor(FlatIndexOfPart(shield) - m.Cursor);    m.Activate();   // then shield
        m.MoveCursor(LauncherFlatIndex(launcher.Part.PartId) - m.Cursor); m.Activate(); // then launcher

        // All five distinct parts are chosen (no accidental same-type replace).
        Assert.Equal(5, m.ChosenParts.Count);

        // The launcher — the Magazine part — is at mount 0, the slot lock/fire read.
        Assert.Equal((byte)0, m.ChosenParts.Single(p => p.PartId == launcher.Part.PartId).MountId);

        // Every non-weapon type lands at its single mount 0 (not global position).
        foreach (var pid in new[] { afterburner.PartId, cloak.PartId, shield.PartId })
            Assert.Equal((byte)0, m.ChosenParts.Single(p => p.PartId == pid).MountId);

        // The weapon is at mount 0 within the Weapon type.
        Assert.Equal((byte)0, m.ChosenParts.Single(p => p.PartId == weapon.PartId).MountId);
    }

    /// <summary>⭐ P437 — a second different launcher REPLACES the first (the
    /// single-slot Magazine reality), never stacking a second inert launcher —
    /// so buying 5 launchers yields 1 usable one, exactly as native's one
    /// missile-bay-per-hull behaves.</summary>
    [Fact]
    public void Choosing_a_second_launcher_replaces_the_first_single_slot()
    {
        var m = Model();
        var two = Catalog.Launchers!.Where(l => l.Kind == LauncherKind.Missile).Take(2).ToArray();
        var a = two[0];
        var b = two[1];

        m.MoveCursor(LauncherFlatIndex(a.Part.PartId) - m.Cursor);
        m.Activate();
        Assert.Single(m.ChosenParts);
        Assert.Equal(a.Part.PartId, m.ChosenParts[0].PartId);

        m.MoveCursor(LauncherFlatIndex(b.Part.PartId) - m.Cursor);
        m.Activate();

        // The first launcher is gone; exactly one Magazine part remains — the new one, at mount 0.
        var mags = m.ChosenParts.Where(p => TypeOf(p.PartId) == EquipmentType.Magazine).ToList();
        Assert.Single(mags);
        Assert.Equal(b.Part.PartId, mags[0].PartId);
        Assert.Equal((byte)0, mags[0].MountId);
    }

    /// <summary>⭐ P437 — Weapons DO stack (native's per-hardpoint scheme): two
    /// guns sit at distinct Weapon mounts 0 and 1 while the single launcher
    /// still holds its own Magazine mount 0.</summary>
    [Fact]
    public void Multiple_weapons_stack_across_distinct_weapon_mounts()
    {
        var m = Model();
        var w1 = FirstWeapon();
        var w2 = Catalog.Parts.First(p => p.EquipmentType == EquipmentType.Weapon && p.PartId != w1.PartId);
        var launcher = FirstMissileLauncher();

        m.MoveCursor(FlatIndexOfPart(w1) - m.Cursor);           m.Activate();
        m.MoveCursor(FlatIndexOfPart(w2) - m.Cursor);           m.Activate();
        m.MoveCursor(LauncherFlatIndex(launcher.Part.PartId) - m.Cursor); m.Activate();

        Assert.Equal(3, m.ChosenParts.Count);
        Assert.Equal((byte)0, m.ChosenParts.Single(p => p.PartId == w1.PartId).MountId);
        Assert.Equal((byte)1, m.ChosenParts.Single(p => p.PartId == w2.PartId).MountId);
        Assert.Equal((byte)0, m.ChosenParts.Single(p => p.PartId == launcher.Part.PartId).MountId);
    }

    // ── P468 — pricing: loadout value, cost, affordability, grey-out ────────

    /// <summary>⭐ P468 — a hull swap with parts (incl. a real launcher) has
    /// cost = (new hull + chosen parts) − (current hull + current parts), the
    /// exact formula the server (P441) charges (newShip.GetValue() −
    /// oldShip.GetValue(), shipIGC.h:396-406).</summary>
    [Fact]
    public void Cost_of_a_hull_swap_with_parts_including_a_launcher()
    {
        var current = CurrentHull();
        var target = DifferentHull(current.HullId);
        var weapon = FirstWeapon();
        var launcher = FirstMissileLauncher();

        var m = new ShopModel(Catalog, current.HullId, Array.Empty<ExpandedPartData>());
        m.MoveCursor(FlatIndexOfHull(target.HullId) - m.Cursor);
        m.Activate();                                        // pick target hull
        m.MoveCursor(FlatIndexOfPart(weapon) - m.Cursor);
        m.Activate();                                        // add a weapon
        m.MoveCursor(LauncherFlatIndex(launcher.Part.PartId) - m.Cursor);
        m.Activate();                                        // add a launcher

        long expectedNew = (long)target.Price + weapon.Price + launcher.Part.Price;
        long expectedCurrent = current.Price;                 // no mounted parts
        Assert.Equal(expectedCurrent, m.CurrentShipValue);
        Assert.Equal(expectedNew, m.NewLoadoutValue);
        Assert.Equal(expectedNew - expectedCurrent, m.Cost);
    }

    /// <summary>⭐ P468 — a cheaper hull swap has a NEGATIVE cost (a refund: the
    /// server pays the difference back, as in native).</summary>
    [Fact]
    public void Cheaper_hull_swap_yields_a_refund()
    {
        // current = an expensive hull; target = a cheaper one (scanned from the real core).
        var expensive = Catalog.Hulls.OrderByDescending(h => h.Price).First();
        var cheap = Catalog.Hulls.Where(h => h.Price < expensive.Price).OrderBy(h => h.Price).First();

        var m = new ShopModel(Catalog, expensive.HullId, Array.Empty<ExpandedPartData>());
        m.MoveCursor(FlatIndexOfHull(cheap.HullId) - m.Cursor);
        m.Activate();

        Assert.Equal((long)expensive.Price, m.CurrentShipValue);
        Assert.Equal((long)cheap.Price, m.NewLoadoutValue);
        Assert.Equal((long)cheap.Price - expensive.Price, m.Cost);
        Assert.True(m.Cost < 0, "a cheaper hull must yield a refund (negative cost)");
    }

    /// <summary>⭐ P468 — the affordability flag at the EXACT boundary: cost ==
    /// MyMoney is affordable; one credit short is not, and a refused launch sets
    /// a visible BlockReason.</summary>
    [Fact]
    public void Affordability_flag_at_the_exact_money_boundary()
    {
        // A positive-cost upgrade fixture: cheapest current → a pricier target.
        var current = Catalog.Hulls.OrderBy(h => h.Price).First();
        var target = Catalog.Hulls.Where(h => h.Price > current.Price).OrderBy(h => h.Price).First();
        long cost = (long)target.Price - current.Price;
        Assert.True(cost > 0, "fixture must be a positive-cost upgrade");

        var m = new ShopModel(Catalog, current.HullId, Array.Empty<ExpandedPartData>(), money: (int)cost);
        m.MoveCursor(FlatIndexOfHull(target.HullId) - m.Cursor);
        m.Activate();
        Assert.Equal(cost, m.Cost);

        // Exactly affording it → affordable and launchable.
        Assert.True(m.CanAffordTotal);
        Assert.True(m.TryLaunch());
        Assert.Null(m.BlockReason);

        // One credit short → unaffordable; launch refused with a visible reason.
        var poor = new ShopModel(Catalog, current.HullId, Array.Empty<ExpandedPartData>(), money: (int)cost - 1);
        poor.MoveCursor(FlatIndexOfHull(target.HullId) - poor.Cursor);
        poor.Activate();
        Assert.False(poor.CanAffordTotal);
        Assert.False(poor.TryLaunch());
        Assert.NotNull(poor.BlockReason);
        Assert.Contains("cannot afford", poor.BlockReason);

        // Changing the loadout clears the stale refusal.
        poor.Activate();   // de-pick the target hull (toggles it off) → cost 0 again
        Assert.Null(poor.BlockReason);
    }

    /// <summary>⭐ P468 — no wallet known ⇒ affordability unknown: rows are not
    /// greyed and a launch is not blocked (we can't prove it unaffordable).</summary>
    [Fact]
    public void Unknown_money_does_not_block_or_grey()
    {
        var current = CurrentHull();
        var target = DifferentHull(current.HullId);
        var m = new ShopModel(Catalog, current.HullId, Array.Empty<ExpandedPartData>());  // money null
        Assert.False(m.AffordabilityKnown);
        Assert.True(m.TryLaunch());
        Assert.False(m.HullRowGreyed(target));
        Assert.False(m.CanAffordTotal);
    }

    /// <summary>⭐ P468 — grey-out: an expensive part/launcher row not yet chosen
    /// is greyed when it exceeds MyMoney; once chosen (or the effective hull) it
    /// is not. (PCore001's Parts table is all price-0, so the grey test uses a
    /// real paid launcher — the separate Launchers table P441 priced.)</summary>
    [Fact]
    public void Expensive_unowned_part_is_greyed_but_chosen_and_effective_are_not()
    {
        var current = CurrentHull();
        var pricey = Catalog.Launchers!.First(l => l.Part.Price > 0).Part;
        var m = new ShopModel(Catalog, current.HullId, Array.Empty<ExpandedPartData>(), money: 0);
        // With $0, any positive-price unowned part is greyed.
        Assert.True(m.PartRowGreyed(pricey.PartId, pricey.Price));
        // The effective hull is never greyed (net swap cost 0).
        Assert.False(m.HullRowGreyed(current));
        // Once chosen, the part is effectively free to keep → not greyed.
        m.MoveCursor(LauncherFlatIndex(pricey.PartId) - m.Cursor);
        m.Activate();
        Assert.False(m.PartRowGreyed(pricey.PartId, pricey.Price));
    }
}

