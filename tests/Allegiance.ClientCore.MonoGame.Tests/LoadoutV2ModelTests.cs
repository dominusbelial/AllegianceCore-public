using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P1010 — the Loadout V2 view-model over <see cref="ShopModel"/>: hull rows
/// (name/class/price, buyable vs locked + human reason), slot rows per mount
/// (native <c>SlotValidForHull</c>), per-slot offers filtered by the native
/// mount masks, and the Default / Revert / Next-Previous actions.
///
/// <para>Data is the real <c>PCore001.igc</c> under the artwork dir, docked at
/// <b>Gigacorp's own station type</b> (the civ's <c>initialStationTypeId</c>,
/// <c>Igc/missionigc.cpp</c> station/civ link) with a <b>fresh side</b>
/// (the civ's base techs = the CREATE_BUCKETS initial development techs,
/// <c>Igc/sideigc.cpp:123</c>) — no synthetic fixtures, and the tests skip
/// cleanly when the corpus is absent (the <see cref="Require.Artwork"/>
/// discipline every artwork test uses).</para>
/// </summary>
public class LoadoutV2ModelTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static readonly ShopCatalog Catalog = LoadCatalog();

    private static ShopCatalog LoadCatalog() =>
        ShopCatalog.TryLoad(ArtRoot)
        ?? throw new InvalidOperationException($"PCore001.igc not loadable from {ArtRoot}");

    private static readonly StaticCore Core =
        StaticCore.Load(IgcReader.ParseFile(System.IO.Path.Combine(ArtRoot, ShopCatalog.CoreFileName)));

    private static DataCivilizationIGC Gigacorp =>
        Core.Civilizations.Values.First(c => c.Name == "Gigacorp");

    /// <summary>Gigacorp's own station type (the civ's initial station).</summary>
    private static DataStationTypeIGC GigacorpStation =>
        Core.StationTypes[Gigacorp.InitialStationTypeId];

    /// <summary>A fresh Gigacorp side: only the civ's base techs (no research,
    /// no captured/built station techs).</summary>
    private static TechTreeBitMask FreshSideTechs =>
        TechTreeBitMask.FromBytes(Gigacorp.TtbmBaseTechs);

    private static IEnumerable<DataPartTypeIGC> AllParts() =>
        Catalog.Parts.Concat(Catalog.Launchers?.Select(l => l.Part) ?? []);

    private static DataPartTypeIGC PartById(short partId) =>
        AllParts().First(p => p.PartId == partId);

    private static ExpandedPartData Mounted(DataPartTypeIGC part, byte mount, short amount) =>
        new(part.PartId, amount, mount, ExpandedPartData.FullyMounted);

    /// <summary>One hull by id out of the real core (asserts it exists so a data
    /// change fails loudly rather than silently skipping a case).</summary>
    private static DataHullTypeIGC Hull(short hullId) =>
        Core.HullTypes.TryGetValue(hullId, out var h)
            ? h
            : throw new InvalidOperationException($"PCore001 has no hull {hullId}");

    /// <summary>The native slot admission test, written out independently of the
    /// production code so the test can catch a regression in it:
    /// <c>Slot::SlotValidForHull</c> — weapon mounts below
    /// <c>GetMaxFixedWeapons()</c>, turret mounts below
    /// <c>GetMaxWeapons() - GetMaxFixedWeapons()</c>, every other type only where
    /// <c>GetPartMask(type, 0) != 0</c> (<c>WinTrek/load.cpp:112-122</c>).
    /// <para>Native numbers turret slots RELATIVELY (0..N-1); a
    /// <see cref="LoadoutV2SlotRow.Mount"/> is the absolute hardpoint index =
    /// native <c>Slot::GetMount()</c> (<c>m_mount + GetMaxFixedWeapons()</c>,
    /// <c>load.cpp:104-110</c>), matching <see cref="ShopModel"/>'s mount ids.</para></summary>
    private static bool SlotValidForHull(in DataHullTypeIGC hull, SlotDef slot)
    {
        if (slot.Type != EquipmentType.Weapon)
        {
            int idx = (int)slot.Type;
            return idx < hull.PmEquipment.Length && hull.PmEquipment[idx] != 0;
        }
        return slot.IsTurret
            ? slot.Mount >= hull.MaxFixedWeapons && slot.Mount < hull.MaxWeapons
            : slot.Mount < hull.MaxFixedWeapons;
    }

    /// <summary>The native per-mount part mask test (independent transcription):
    /// a part fits a mount when every bit of its own mask is covered by that
    /// mount's mask (<c>Igc/hullTypeIGC.h:163-176,199-213</c>).</summary>
    private static bool FitsMount(in DataHullTypeIGC hull, SlotDef slot, in DataPartTypeIGC part)
    {
        if (part.EquipmentType != slot.Type) return false;
        short mountMask = slot.Type == EquipmentType.Weapon
            ? hull.Hardpoints[slot.Mount].PartMask
            : hull.PmEquipment[(int)slot.Type];
        return (part.PartMask & ~mountMask) == 0;
    }

    private static (ShopModel Shop, LoadoutV2Model V2) GigacorpScreen(
        IReadOnlyList<ExpandedPartData>? currentParts = null, int? money = null)
    {
        var fresh = FreshSideTechs;
        var station = GigacorpStation;
        var offers = Catalog.FilteredFor(station.TtbmLocal, fresh);
        var shop = new ShopModel(offers, currentHullId: offers.Hulls.First().HullId,
            currentParts: currentParts ?? Array.Empty<ExpandedPartData>(), money: money);
        var reachable = BucketCatalog.ReachableTechs(Core, fresh);
        var v2 = new LoadoutV2Model(shop, Catalog.Hulls, reachable, station.TtbmLocal, fresh);
        return (shop, v2);
    }

    // ── 1. hull column: native order + locks with human reasons ─────────────

    /// <summary>
    /// ⭐ P1010 — the hull column is the station's buyable list in native
    /// <c>GetNextHullType</c> order (the order <see cref="ShopModel.CycleHull"/>
    /// walks, <c>WinTrek/load.cpp:1568-1636</c>), followed by the hulls the side
    /// could unlock — the native predicate's other half (<c>GroupID &gt;= 0</c>,
    /// not obsolete, not buyable now, <c>load.cpp:1603-1606</c> /
    /// <c>Igc/stationIGC.h:383-397</c>) — each with a human reason, a real class
    /// and price, and the current hull marked.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Hull_column_lists_native_order_then_unlockable_hulls_with_reasons()
    {
        Require.Artwork();
        var fresh = FreshSideTechs;
        var station = GigacorpStation;
        var (shop, v2) = GigacorpScreen();

        var rows = v2.HullRows;
        int offered = shop.BuyableHulls.Count;
        Assert.True(offered > 0);

        // Offered prefix — exactly the shop's cycle order (native GetNextHullType).
        Assert.Equal(shop.BuyableHulls.Select(h => h.HullId),
            rows.Take(offered).Select(r => r.HullId));
        foreach (var row in rows.Take(offered))
        {
            Assert.Equal(HullRowStatus.Buyable, row.Status);
            Assert.Null(row.LockReason);
            Assert.False(string.IsNullOrWhiteSpace(row.Name), "hull rows must carry a core name");
            Assert.False(string.IsNullOrWhiteSpace(row.Class), "hull rows must carry a class");
        }

        // Locked tail.
        var locked = rows.Skip(offered).ToList();
        Assert.NotEmpty(locked);
        Assert.Equal(locked.Count, locked.Select(r => r.HullId).Distinct().Count());
        Assert.Equal(locked.OrderBy(r => r.HullId).Select(r => r.HullId).ToList(),
            locked.Select(r => r.HullId).ToList());

        foreach (var row in locked)
        {
            var hull = Hull(row.HullId);
            Assert.Equal(HullRowStatus.Locked, row.Status);
            Assert.StartsWith("missing ", row.LockReason);
            Assert.False(string.IsNullOrWhiteSpace(row.Class));
            Assert.True(hull.GroupId >= 0, $"{hull.Name}: native never lists a GroupID<0 hull");
            Assert.False(SuccessorUpgrade.HullIsObsolete(Core, hull, station.TtbmLocal, fresh),
                $"{hull.Name}: an out-teched hull is hidden, not shown locked");
            Assert.False(TechTreeBitMask.RequiredTechsBuyable(hull.TtbmRequired, station.TtbmLocal, fresh),
                $"{hull.Name}: a locked row must fail native CanBuy");
            Assert.True(TechTreeBitMask.RequiredTechsBuyable(hull.TtbmRequired, null,
                    BucketCatalog.ReachableTechs(Core, fresh)),
                $"{hull.Name}: a locked row must be inside this side's reachable tech tree");
            Assert.DoesNotContain(shop.BuyableHulls, h => h.HullId == row.HullId);
            Assert.Equal(hull.Price, row.Price);
        }

        // The Gigacorp upgrade ladder is the locked set: the Enh Fighter is a
        // research step, and its reason names the research that unlocks it.
        var enh = locked.Single(r => r.Name == "Enh Fighter");
        Assert.Contains("research", enh.LockReason);
        Assert.Contains("Enh Fighter", enh.LockReason);
        Assert.Equal("Light Ship", enh.Class);

        // Current hull marked exactly once; the effective (chosen/current) hull too.
        Assert.Single(rows, r => r.IsCurrent);
        Assert.Equal(shop.CurrentHullId, rows.Single(r => r.IsCurrent).HullId);
        Assert.Single(rows, r => r.IsChosen);
        Assert.Equal(shop.EffectiveHullId, rows.Single(r => r.IsChosen).HullId);

        // No raw ids leak into the display strings.
        foreach (var row in rows)
        {
            Assert.DoesNotContain($"#{row.HullId}", row.Name);
            Assert.NotEqual(row.HullId.ToString(), row.Name);
        }
    }

    /// <summary>
    /// ⭐ P1010 — a locked hull cannot be selected, and the refusal carries the
    /// same human reason; the selection stays where it was (<c>WinTrek/load.cpp:
    /// 1603-1606</c> only ever returns a buyable hull).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Selecting_a_locked_hull_is_refused_with_its_reason()
    {
        Require.Artwork();
        var (shop, v2) = GigacorpScreen();
        var locked = v2.HullRows.First(r => r.Status == HullRowStatus.Locked);
        short before = shop.EffectiveHullId;

        Assert.False(v2.SelectHull(locked.HullId));
        Assert.Equal(before, shop.EffectiveHullId);
        Assert.Null(shop.ChosenHullId);
        Assert.NotNull(v2.BlockReason);
        Assert.Contains(locked.Name, v2.BlockReason);
        Assert.Contains(locked.LockReason!, v2.BlockReason);

        // An id outside the hull table is refused too, without a crash.
        Assert.False(v2.SelectHull(short.MaxValue));
        Assert.NotNull(v2.BlockReason);
    }

    /// <summary>
    /// P1010 — selecting an OFFERED hull goes through
    /// <see cref="ShopModel.SelectHull"/> and marks the rows: the previous hull
    /// is no longer chosen, the new one is, and the current hull stays marked.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Selecting_an_offered_hull_marks_the_rows_and_pends_the_swap()
    {
        Require.Artwork();
        // The unfiltered catalogue offers every hull, so an "offered" target that
        // is not the docked hull exists (a fresh Gigacorp side has only its Scout).
        var shop = new ShopModel(Catalog, Catalog.Hulls.First().HullId, Array.Empty<ExpandedPartData>());
        var v2 = new LoadoutV2Model(shop, Catalog.Hulls);
        var rows = v2.HullRows;
        var target = rows.First(r => r.Status == HullRowStatus.Buyable && !r.IsCurrent
                                     && Hull(r.HullId).GroupId >= 0);

        Assert.True(v2.SelectHull(target.HullId));
        Assert.Equal(target.HullId, shop.EffectiveHullId);
        Assert.True(v2.WillSwapHull);
        Assert.Equal(target.HullId, v2.HullRows.Single(r => r.IsChosen).HullId);
        Assert.Equal(shop.CurrentHullId, v2.HullRows.Single(r => r.IsCurrent).HullId);
        Assert.Null(v2.BlockReason);

        // Re-selecting the docked hull de-picks it (native NextHull's
        // pht == phtCurrent branch does not buy a hull, load.cpp:1620-1625).
        Assert.True(v2.SelectHull(shop.CurrentHullId));
        Assert.False(v2.WillSwapHull);
        Assert.Null(shop.ChosenHullId);
    }

    // ── 2. ship column: slot rows per mount ─────────────────────────────────

    /// <summary>
    /// ⭐ P1010 — the slot rows are exactly the hull's real slots: one fixed
    /// weapon row per fixed hardpoint, one gunner turret row per turret hardpoint
    /// and one row per equipment socket the hull has — native
    /// <c>SlotValidForHull</c> (<c>WinTrek/load.cpp:112-122</c>) applied to the
    /// authored slot table (<c>:2023-2044</c>), for a light fighter (Enh
    /// Fighter), a bomber, a turret hull (Gunship) and a plain Fighter.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Slot_rows_follow_slot_valid_for_hull_for_every_hull_class()
    {
        Require.Artwork();
        // Gigacorp Enh Fighter (fixed-only), Bomber + Gunship (turret hulls), and
        // the first plain Fighter in the core.
        short[] hullIds =
        [
            214,                                                    // Gigacorp Enh Fighter: 3 fixed, no turret
            204,                                                    // Gigacorp Bomber: 2 fixed + 2 turrets
            290,                                                    // Gigacorp Gunship: 1 fixed + 2 turrets
            Catalog.Hulls.Where(h => h.Name == "Fighter").OrderBy(h => h.HullId).First().HullId,
        ];

        foreach (short hullId in hullIds)
        {
            var hull = Hull(hullId);
            var shop = new ShopModel(Catalog, hullId, Array.Empty<ExpandedPartData>());
            var v2 = new LoadoutV2Model(shop, Catalog.Hulls);
            var rows = v2.SlotRows;

            int fixedExpected = Math.Min(hull.MaxFixedWeapons, hull.Hardpoints.Length);
            int turretExpected = Math.Min(hull.MaxWeapons, hull.Hardpoints.Length) - fixedExpected;
            int socketExpected = Enumerable.Range(0, hull.PmEquipment.Length)
                .Count(et => et != (int)EquipmentType.Weapon && hull.PmEquipment[et] != 0);

            Assert.Equal(fixedExpected + turretExpected + socketExpected, rows.Count);

            var fixedRows = rows.Where(r => r.Slot.Type == EquipmentType.Weapon && !r.IsTurret).ToList();
            var turretRows = rows.Where(r => r.IsTurret).ToList();
            var socketRows = rows.Where(r => r.Slot.Type != EquipmentType.Weapon).ToList();

            Assert.Equal(fixedExpected, fixedRows.Count);
            Assert.Equal(turretExpected, turretRows.Count);
            Assert.Equal(socketExpected, socketRows.Count);

            // Every row is a slot the hull really has (independent native test).
            foreach (var row in rows) Assert.True(SlotValidForHull(hull, row.Slot), $"{hull.Name} {row.Label}");
            foreach (var row in rows)
                Assert.Equal(row.Slot.Type == EquipmentType.Weapon, row.Slot.IsWeapon);

            // Fixed weapons are numbered from 1 by hardpoint; turrets are numbered
            // among turrets (native's separate turret mount space, load.cpp:104-110).
            Assert.Equal(Enumerable.Range(1, fixedExpected).Select(i => $"WEAPON {i}"),
                fixedRows.Select(r => r.Label));
            Assert.Equal(Enumerable.Range(1, turretExpected).Select(i => $"TURRET {i}"),
                turretRows.Select(r => r.Label));
            Assert.Equal(turretRows.Select(r => r.Mount),
                Enumerable.Range(hull.MaxFixedWeapons, turretExpected).Select(m => (byte)m));

            // Sockets: one per non-zero PmEquipment entry, in native ET_* order,
            // labelled with the equipment-type name.
            var socketTypes = Enumerable.Range(0, hull.PmEquipment.Length)
                .Where(et => et != (int)EquipmentType.Weapon && hull.PmEquipment[et] != 0)
                .Select(et => (EquipmentType)et);
            Assert.Equal(socketTypes, socketRows.Select(r => r.Slot.Type));
            Assert.All(socketRows, r => Assert.Equal(r.Slot.Type.ToString().ToUpperInvariant(), r.Label));
            Assert.All(socketRows, r => Assert.Equal((byte)0, r.Mount));

            // Nothing is mounted yet on a bare hull.
            Assert.All(rows, r => Assert.False(r.Filled));
            Assert.All(rows, r => Assert.Null(r.CurrentPartName));
        }
    }

    /// <summary>
    /// ⭐ P1010 — turret rows carry the seated gunner's name; a turret with no
    /// gunner stays null (native draws the gunner or "&lt;open&gt;",
    /// <c>WinTrek/load.cpp:176-205</c>), and fixed/socket rows never show one.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Turret_rows_carry_the_gunner_seat()
    {
        Require.Artwork();
        const short gunshipId = 290;                       // 1 fixed + 2 turrets
        var shop = new ShopModel(Catalog, gunshipId, Array.Empty<ExpandedPartData>());
        byte firstTurretMount = (byte)Hull(gunshipId).MaxFixedWeapons;
        var v2 = new LoadoutV2Model(shop, Catalog.Hulls,
            turretGunner: mount => mount == firstTurretMount ? "Ada" : null);

        var turrets = v2.SlotRows.Where(r => r.IsTurret).ToList();
        Assert.Equal(2, turrets.Count);
        Assert.Equal("Ada", turrets[0].GunnerName);
        Assert.Equal(firstTurretMount, turrets[0].Mount);
        Assert.Null(turrets[1].GunnerName);                 // open seat
        Assert.All(v2.SlotRows.Where(r => !r.IsTurret), r => Assert.Null(r.GunnerName));

        // No resolver = no gunner knowledge (the P1014 wiring supplies it).
        var bare = new LoadoutV2Model(new ShopModel(Catalog, gunshipId, Array.Empty<ExpandedPartData>()),
            Catalog.Hulls);
        Assert.All(bare.SlotRows.Where(r => r.IsTurret), r => Assert.Null(r.GunnerName));
    }

    /// <summary>
    /// P1010 — the V2 slot column never loses a slot the old screen shows: every
    /// <see cref="ShopModel.Slots"/> entry of the effective hull is a V2 row (the
    /// V2 column adds the pack socket and splits fixed weapons from turrets).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Slot_rows_cover_every_slot_the_old_screen_shows()
    {
        Require.Artwork();
        foreach (short hullId in new short[] { 214, 204, 290 })
        {
            var shop = new ShopModel(Catalog, hullId, Array.Empty<ExpandedPartData>());
            var v2 = new LoadoutV2Model(shop, Catalog.Hulls);
            foreach (var slot in shop.Slots)
                Assert.Contains(v2.SlotRows, r => r.Slot == slot);
        }
    }

    // ── 3. offers per slot: native mount masks + what the side can buy ──────

    /// <summary>
    /// ⭐ P1010 — every slot's offers are exactly the parts whose mask the mount
    /// accepts (<c>CanMount</c>, <c>WinTrek/load.cpp:1656-1678,1734-1743</c>), no
    /// more and no less, for every slot of a fighter, a bomber and a turret hull.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Offers_per_slot_match_the_native_mount_masks()
    {
        Require.Artwork();
        foreach (short hullId in new short[] { 214, 204, 290 })
        {
            var hull = Hull(hullId);
            var shop = new ShopModel(Catalog, hullId, Array.Empty<ExpandedPartData>());
            var v2 = new LoadoutV2Model(shop, Catalog.Hulls);

            foreach (var row in v2.SlotRows)
            {
                var expected = AllParts()
                    .Where(p => FitsMount(hull, row.Slot, p))
                    .Select(p => p.PartId)
                    .OrderBy(id => id)
                    .ToList();
                var offers = v2.OffersFor(row.Slot);

                Assert.Equal(expected, offers.Select(o => o.PartId).OrderBy(id => id).ToList());
                Assert.Equal(offers.Count, offers.Select(o => o.PartId).Distinct().Count());
                foreach (var offer in offers)
                {
                    Assert.Equal(offer.PartId, offer.Part.PartId);
                    Assert.Equal(offer.Part.EquipmentType, row.Slot.Type);
                    Assert.Equal(PartById(offer.PartId).Name, offer.Name);
                    Assert.Equal(PartById(offer.PartId).Price, offer.Price);
                    // P1011 needs the whole core record, not just a name.
                    Assert.Equal(PartById(offer.PartId).PartMask, offer.Part.PartMask);
                }
            }
        }
    }

    /// <summary>
    /// ⭐ P1010 — the docked station's tech filter is the "side can buy" gate:
    /// with a fresh side, no offer comes from a part the filtered catalogue does
    /// not sell (native <c>pstation-&gt;CanBuy</c>,
    /// <c>Igc/stationIGC.h:383-391</c>; the filter really bites).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Offers_only_include_parts_the_side_can_buy()
    {
        Require.Artwork();
        var (shop, v2) = GigacorpScreen();
        var sellable = shop.Catalog.Parts.Select(p => p.PartId)
            .Concat(shop.Catalog.Launchers?.Select(l => l.Part.PartId) ?? [])
            .ToHashSet();

        Assert.NotEmpty(shop.BuyableHulls);
        Assert.NotEmpty(v2.SlotRows);
        foreach (var row in v2.SlotRows)
            foreach (var offer in v2.OffersFor(row.Slot))
                Assert.Contains(offer.PartId, sellable);

        // Sanity: the tech filter removed something the unfiltered core has.
        Assert.True(AllParts().Count() > sellable.Count,
            "a fresh side should not be able to buy every core part");
        Assert.True(v2.SlotRows.SelectMany(r => v2.OffersFor(r.Slot)).Any());
    }

    /// <summary>
    /// ⭐ P1010 — an offer carries its price and the "already mounted" marker for
    /// the part the docked ship really carries, and the pending pick is marked
    /// once chosen.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Offers_mark_the_already_mounted_and_chosen_parts()
    {
        Require.Artwork();
        const short fighterId = 214;
        var hull = Hull(fighterId);
        var weapon = Catalog.Parts.First(p => p.EquipmentType == EquipmentType.Weapon && hull.CanMountPart(p, 0));
        var current = new[] { Mounted(weapon, 0, 20) };
        var shop = new ShopModel(Catalog, fighterId, current, money: 10_000);
        var v2 = new LoadoutV2Model(shop, Catalog.Hulls);

        var weaponRow = v2.SlotRows.First(r => r.Slot.Type == EquipmentType.Weapon && !r.IsTurret);
        Assert.Equal(weapon.Name, weaponRow.ChosenPartName);      // seeded from the docked ship
        Assert.Equal(weapon.Name, weaponRow.CurrentPartName);
        Assert.Equal((short)20, weaponRow.Amount);

        var offer = v2.OffersFor(weaponRow.Slot).First(o => o.PartId == weapon.PartId);
        Assert.True(offer.AlreadyMounted);
        Assert.True(offer.IsChosen);
        Assert.Equal(weapon.Price, offer.Price);

        // A different part in the same slot: not mounted, not chosen until picked.
        var other = v2.OffersFor(weaponRow.Slot).First(o => o.PartId != weapon.PartId);
        Assert.False(other.AlreadyMounted);
        Assert.False(other.IsChosen);

        Assert.True(v2.ChooseOffer(weaponRow.Slot, other.PartId));
        Assert.True(v2.OffersFor(weaponRow.Slot).First(o => o.PartId == other.PartId).IsChosen);
        Assert.False(v2.OffersFor(weaponRow.Slot).First(o => o.PartId == weapon.PartId).IsChosen);
        // ... but it is still what the docked ship carries.
        Assert.True(v2.OffersFor(weaponRow.Slot).First(o => o.PartId == weapon.PartId).AlreadyMounted);
    }

    // ── 4. actions: open/choose/clear, default/revert, next/previous ───────

    /// <summary>
    /// ⭐ P1010 — the inline picker: open a slot, pick an offer, and the pending
    /// loadout + price total move exactly as the old screen's slot menu moves
    /// them (same <see cref="ShopModel"/> mutation), then clear it again.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Open_choose_and_clear_a_slot_through_the_shop()
    {
        Require.Artwork();
        var hull = Hull(214);
        var shop = new ShopModel(Catalog, 214, Array.Empty<ExpandedPartData>(), money: 100_000);
        var v2 = new LoadoutV2Model(shop, Catalog.Hulls);
        var shieldRow = v2.SlotRows.First(r => r.Slot.Type == EquipmentType.Shield);
        long before = v2.PriceTotal;

        Assert.True(v2.OpenSlotFor(shieldRow.Slot));
        Assert.Equal(shieldRow.Slot, v2.OpenSlot);
        Assert.NotEmpty(v2.Offers);

        var pick = v2.Offers.First();
        Assert.True(v2.ChooseOffer(pick.PartId));
        Assert.Null(v2.OpenSlot);                                   // the picker closes on pick
        Assert.Null(v2.BlockReason);
        var mounted = shop.ChosenParts.Single(p => p.PartId == pick.PartId);
        Assert.Equal((byte)0, mounted.MountId);                     // single socket = mount 0
        Assert.Equal(pick.Amount, mounted.Amount);
        Assert.Equal(hull.Name, v2.EffectiveHull!.Value.Name);
        Assert.Equal(shop.NewLoadoutValue, v2.PriceTotal);
        Assert.Equal(before + pick.Price, v2.PriceTotal);
        Assert.True(v2.SlotRows.First(r => r.Slot == shieldRow.Slot).Filled);

        // A part that does not fit the slot is refused, not mounted.
        var weaponPart = Catalog.Parts.First(p => p.EquipmentType == EquipmentType.Weapon);
        Assert.False(v2.ChooseOffer(shieldRow.Slot, weaponPart.PartId));
        Assert.NotNull(v2.BlockReason);
        Assert.Single(shop.ChosenParts, p => p.PartId == pick.PartId);

        v2.ClearSlot(shieldRow.Slot);
        Assert.DoesNotContain(shop.ChosenParts, p => p.PartId == pick.PartId);
        Assert.Equal(before, v2.PriceTotal);
        Assert.False(v2.SlotRows.First(r => r.Slot == shieldRow.Slot).Filled);
    }

    /// <summary>
    /// ⭐ P1010 — Default then Revert round-trips: the native Default button
    /// (<c>OnButtonDefault</c>, <c>WinTrek/load.cpp:1639-1648</c>, already ported
    /// as <see cref="ShopModel.ApplyDefaultLoadout"/>) replaces the pending
    /// loadout; Revert puts back the docked ship's own hull+parts and drops the
    /// pending swap.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Default_then_revert_round_trips_the_docked_loadout()
    {
        Require.Artwork();
        var hull = Hull(214);
        var weapon = Catalog.Parts.First(p => p.EquipmentType == EquipmentType.Weapon && hull.CanMountPart(p, 0));
        var shield = Catalog.Parts.First(p => p.EquipmentType == EquipmentType.Shield && hull.CanMountPart(p));
        var current = new[] { Mounted(weapon, 0, 20), Mounted(shield, 0, 1) };

        var shop = new ShopModel(Catalog, 214, current, money: 100_000);
        var v2 = new LoadoutV2Model(shop, Catalog.Hulls);

        // Opens on the docked loadout.
        Assert.Equal(current.Select(p => p.PartId).OrderBy(x => x),
            shop.ChosenParts.Select(p => p.PartId).OrderBy(x => x));
        Assert.Equal(shop.CurrentShipValue, v2.PriceTotal);
        Assert.Equal(0L, v2.Cost);

        v2.ApplyDefault();
        Assert.NotEmpty(shop.ChosenParts);
        Assert.Equal(shop.CurrentShipValue + shop.Cost, v2.PriceTotal);

        v2.Revert();
        Assert.Null(shop.ChosenHullId);
        Assert.Equal((short)214, shop.EffectiveHullId);
        Assert.False(v2.WillSwapHull);
        Assert.Equal(current.Select(p => (p.PartId, p.MountId, p.Amount)).OrderBy(x => x.PartId),
            shop.ChosenParts.Select(p => (p.PartId, p.MountId, p.Amount)).OrderBy(x => x.PartId));
        Assert.Equal(shop.CurrentShipValue, v2.PriceTotal);
        Assert.Equal(0L, v2.Cost);
        Assert.Null(v2.BlockReason);
        Assert.Null(v2.OpenSlot);

        // Revert after a pending hull swap also cancels the swap.
        var other = Catalog.Hulls.First(h => h.HullId != 214 && h.GroupId >= 0);
        Assert.True(v2.SelectHull(other.HullId));
        Assert.True(v2.WillSwapHull);
        v2.Revert();
        Assert.Equal((short)214, shop.EffectiveHullId);
        Assert.False(v2.WillSwapHull);
        Assert.Equal(current.Select(p => p.PartId).OrderBy(x => x),
            shop.ChosenParts.Select(p => p.PartId).OrderBy(x => x));
    }

    /// <summary>
    /// P1010 — Next/Previous cycle exactly the buyable hulls (native
    /// <c>OnButtonNext</c>/<c>OnButtonPrevious</c> → <c>GetNextHullType</c>,
    /// <c>WinTrek/load.cpp:1618-1637</c>) and land back on the starting hull.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Next_and_previous_hull_cycle_the_buyable_list()
    {
        Require.Artwork();
        var shop = new ShopModel(Catalog, Catalog.Hulls.First().HullId, Array.Empty<ExpandedPartData>());
        var v2 = new LoadoutV2Model(shop, Catalog.Hulls);
        short start = shop.EffectiveHullId;
        int buyable = shop.BuyableHulls.Count;
        Assert.True(buyable > 2, "PCore001 has a multi-hull shop");

        v2.NextHull();
        Assert.NotEqual(start, shop.EffectiveHullId);
        v2.PreviousHull();
        Assert.Equal(start, shop.EffectiveHullId);

        // Walking the whole list forward visits every other buyable hull exactly
        // once and lands back on the start (native GetNextHullType's wrap).
        var seen = new List<short>();
        for (int i = 0; i < buyable - 1; i++) { v2.NextHull(); seen.Add(shop.EffectiveHullId); }
        Assert.Equal(buyable - 1, seen.Distinct().Count());
        Assert.DoesNotContain(start, seen);
        Assert.All(seen, id => Assert.Contains(shop.BuyableHulls, h => h.HullId == id));
        v2.NextHull();                                    // one more wrap -> back to the start
        Assert.Equal(start, shop.EffectiveHullId);
    }

    // ── 5. money: read straight from ShopModel ──────────────────────────────

    /// <summary>
    /// ⭐ P1010 — the price box and the refusal text are the shop's own numbers:
    /// <see cref="ShopModel.NewLoadoutValue"/> / <see cref="ShopModel.Cost"/> /
    /// <see cref="ShopModel.BuyShortfall"/> (native <c>OnButtonBuy</c>,
    /// <c>WinTrek/load.cpp:1503-1507</c>) and <see cref="ShopModel.BlockReason"/>.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Price_and_affordability_come_from_the_shop()
    {
        Require.Artwork();
        // PCore001's parts are all free; money comes from the HULL price, so the
        // case is a switch to a dearer hull with an empty wallet.
        var current = Catalog.Hulls.OrderBy(h => h.Price).First();
        var target = Catalog.Hulls.Where(h => h.Price > current.Price).OrderBy(h => h.Price).First();
        long cost = (long)target.Price - current.Price;

        var shop = new ShopModel(Catalog, current.HullId, Array.Empty<ExpandedPartData>(), money: 0);
        var v2 = new LoadoutV2Model(shop, Catalog.Hulls);

        // The row money flag is the shop's own grey-out: switching to the dearer
        // hull costs more than the wallet; the docked hull itself is never greyed.
        Assert.False(v2.HullRows.First(r => r.HullId == target.HullId).Affordable);
        Assert.True(v2.HullRows.First(r => r.HullId == current.HullId).Affordable);

        Assert.True(v2.SelectHull(target.HullId));

        Assert.True(v2.AffordabilityKnown);
        Assert.Equal(shop.NewLoadoutValue, v2.PriceTotal);
        Assert.Equal(shop.CurrentShipValue, v2.CurrentValue);
        Assert.Equal(cost, v2.Cost);
        Assert.False(v2.CanAffordTotal);
        Assert.Equal(shop.BuyShortfall, v2.MoneyNeeded);
        Assert.Equal(cost, v2.MoneyNeeded);

        // A refused launch surfaces the shop's BlockReason through the V2 model.
        Assert.False(shop.TryLaunch());
        Assert.NotNull(shop.BlockReason);
        Assert.Equal(shop.BlockReason, v2.BlockReason);

        // Once affordable the block clears (RefreshMoney = the docked SET_MONEY path).
        shop.RefreshMoney((int)v2.Cost);
        Assert.True(v2.CanAffordTotal);
        Assert.Equal(0L, v2.MoneyNeeded);
        Assert.True(shop.TryLaunch());
        Assert.Null(v2.BlockReason);
        Assert.True(v2.HullRows.First(r => r.HullId == current.HullId).Affordable);
    }

    /// <summary>
    /// P1010 — a client whose artwork core failed to load renders the V2 screen
    /// empty instead of throwing (the <see cref="ShopCatalog.Empty"/> path the
    /// docked screen already degrades to): no hull rows, no slot rows, no offers,
    /// and the actions refuse cleanly.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Empty_catalogue_renders_an_empty_screen_without_throwing()
    {
        Require.Artwork();
        var shop = new ShopModel(ShopCatalog.Empty, currentHullId: 0,
            currentParts: Array.Empty<ExpandedPartData>(), money: 0);
        var v2 = new LoadoutV2Model(shop);

        Assert.Empty(v2.HullRows);
        Assert.Empty(v2.SlotRows);
        Assert.Empty(v2.Offers);
        Assert.Null(v2.EffectiveHull);
        Assert.False(v2.SelectHull(210));
        Assert.NotNull(v2.BlockReason);
        Assert.False(v2.ChooseOffer(0));
        v2.NextHull();
        v2.PreviousHull();
        v2.ApplyDefault();
        v2.Revert();
        v2.CloseSlot();
        Assert.Equal(0L, v2.PriceTotal);
    }

    // ── 6. the old screen shares the same rule ──────────────────────────────

    /// <summary>
    /// ⭐ P1010 — the old screen's open-slot menu and the V2 picker list the same
    /// offers: <see cref="ShopModel.OpenSlotParts"/> is now
    /// <see cref="ShopModel.SlotOffers"/> for the opened slot, and both match the
    /// native mask filter — the refactor changed no behaviour.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Old_screen_open_slot_menu_and_v2_picker_agree()
    {
        Require.Artwork();
        var hull = Hull(204);                                   // Bomber: fixed + turret + sockets
        var shop = new ShopModel(Catalog, 204, Array.Empty<ExpandedPartData>());
        var v2 = new LoadoutV2Model(shop, Catalog.Hulls);

        shop.SwitchTab(ShopTab.Parts);
        Assert.True(shop.Slots.Count > 1);
        shop.Activate();                                        // open the first slot
        Assert.True(shop.SlotOpen);
        var slot = shop.OpenSlot!.Value;

        var oldList = shop.OpenSlotParts.Select(o => o.Part.PartId).ToList();
        Assert.Equal(oldList, shop.SlotOffers(slot).Select(o => o.Part.PartId).ToList());
        Assert.Equal(oldList, v2.OffersFor(slot).Select(o => o.PartId).ToList());

        var expected = AllParts().Where(p => FitsMount(hull, slot, p)).Select(p => p.PartId).OrderBy(x => x).ToList();
        Assert.Equal(expected, oldList.OrderBy(x => x).ToList());
    }
}
