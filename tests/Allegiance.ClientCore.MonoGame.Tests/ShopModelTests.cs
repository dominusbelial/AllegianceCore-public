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
/// Pure-logic tests for the P519 reworked docked loadout shop model (tabs +
/// slot-driven part lists + hull cycling). No graphics device — same discipline
/// as <see cref="ScoreboardLayoutTests"/>. The catalogue is the real PCore001.igc
/// under the artwork dir, so the tests exercise real hull/part ids, names and
/// prices.
/// </summary>
public class ShopModelTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static readonly ShopCatalog Catalog = LoadCatalog();
    private static ShopCatalog LoadCatalog() =>
        ShopCatalog.TryLoad(ArtRoot)
        ?? throw new InvalidOperationException($"PCore001.igc not loadable from {ArtRoot}");

    private static ShopModel Model() =>
        new(Catalog, currentHullId: CurrentHull().HullId, currentParts: Array.Empty<ExpandedPartData>());

    private static DataHullTypeIGC CurrentHull() =>
        Catalog.Hulls.OrderBy(h => h.HullId).First();

    private static DataPartTypeIGC FirstWeapon() =>
        Catalog.Parts.First(p => p.EquipmentType == EquipmentType.Weapon);

    /// <summary>A hull that has a real Magazine socket (PmEquipment[2] != 0).</summary>
    private static DataHullTypeIGC HullWithMagazine() =>
        Catalog.Hulls.First(h => h.PmEquipment.Length > (int)EquipmentType.Magazine && h.PmEquipment[(int)EquipmentType.Magazine] != 0);

    private static DataPartTypeIGC? LookupPart(short partId)
    {
        foreach (var p in Catalog.Parts) if (p.PartId == partId) return p;
        foreach (var l in Catalog.Launchers!) if (l.Part.PartId == partId) return l.Part;
        return null;
    }

    private static ExpandedPartData Mounted(DataPartTypeIGC part, byte mount, short amount,
        byte fraction = ExpandedPartData.FullyMounted) =>
        new(part.PartId, amount, mount, fraction);

    // ── P519: the slot-driven PARTS model ───────────────────────────────────

    /// <summary>⭐ P519 — the slot list for a real hull matches its real slots:
    /// one slot per weapon hardpoint plus a single slot for each equipment type
    /// the hull actually has a socket for (native SlotValidForHull).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Slot_list_matches_the_hulls_real_slots()
    {
        Require.Artwork();
        var hull = CurrentHull();
        var m = new ShopModel(Catalog, hull.HullId, Array.Empty<ExpandedPartData>());

        var expected = new List<SlotDef>();
        for (int mount = 0; mount < hull.Hardpoints.Length; mount++)
            expected.Add(new SlotDef(EquipmentType.Weapon, (byte)mount, IsTurret: mount >= hull.MaxFixedWeapons));
        foreach (var et in new[]
        {
            EquipmentType.Magazine, EquipmentType.Dispenser,
            EquipmentType.ChaffLauncher, EquipmentType.Shield,
            EquipmentType.Cloak, EquipmentType.Afterburner,
        })
        {
            int idx = (int)et;
            if (idx < hull.PmEquipment.Length && hull.PmEquipment[idx] != 0)
                expected.Add(new SlotDef(et, 0, IsTurret: false));
        }

        Assert.Equal(expected, m.Slots);
    }

    /// <summary>⭐ P519 — the magazine slot lists ONLY missile launchers: every
    /// offered part is ET_Magazine and mountable by the hull, and at least one
    /// real missile launcher is present.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Magazine_slot_lists_only_missile_launchers()
    {
        Require.Artwork();
        var hull = HullWithMagazine();
        var m = new ShopModel(Catalog, hull.HullId, Array.Empty<ExpandedPartData>());

        m.SwitchTab(ShopTab.Parts);
        // Find the Magazine slot and open it.
        int idx = 0;
        while (m.Slots[idx].Type != EquipmentType.Magazine) idx++;
        m.MoveCursor(idx - m.Cursor);
        Assert.Equal(EquipmentType.Magazine, m.SlotUnderCursor!.Value.Type);
        m.Activate();
        Assert.True(m.SlotOpen);

        var parts = m.OpenSlotParts;
        Assert.NotEmpty(parts);
        foreach (var o in parts)
        {
            Assert.Equal(EquipmentType.Magazine, o.Part.EquipmentType);
            Assert.True(hull.CanMountPart(o.Part), "every magazine slot offer must mount the hull");
        }
        Assert.Contains(parts, o => o.IsLauncher);   // a real missile launcher offer
    }

    /// <summary>⭐ P519 — choosing a part fills the slot (mounting at the slot's
    /// mount) and returns to the slot list; the launcher carries its real ammo.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Choosing_a_magazine_part_fills_the_slot_at_mount_zero_with_real_ammo()
    {
        Require.Artwork();
        var hull = HullWithMagazine();
        var m = new ShopModel(Catalog, hull.HullId, Array.Empty<ExpandedPartData>());
        m.SwitchTab(ShopTab.Parts);

        int idx = 0;
        while (m.Slots[idx].Type != EquipmentType.Magazine) idx++;
        m.MoveCursor(idx - m.Cursor);
        m.Activate();                                   // open the magazine slot
        Assert.True(m.SlotOpen);

        var target = m.OpenSlotParts.First(o => o.IsLauncher);
        int pIdx = 0;
        while (m.OpenSlotParts[pIdx].Part.PartId != target.Part.PartId) pIdx++;
        m.MoveCursor(pIdx - m.Cursor);
        m.Activate();                                   // choose the missile launcher

        Assert.False(m.SlotOpen);                        // returned to the slot list
        var bought = m.ChosenParts.Single(p => p.PartId == target.Part.PartId);
        Assert.Equal((byte)0, bought.MountId);           // magazine single slot = mount 0
        Assert.Equal(target.Amount, bought.Amount);      // real launcher ammo reserve
    }

    // ── P519: hull cycling ──────────────────────────────────────────────────

    /// <summary>⭐ P519 — the hull cycle visits EXACTLY the station's buyable,
    /// non-obsolete, GroupID≥0 hulls, in a stable (cyclic) order. Uses a
    /// FilteredFor catalogue so the buyable set is the tech/obsolete/group
    /// filtered one (native GetNextHullType predicate).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Hull_cycle_visits_exactly_the_buyable_hulls_in_order()
    {
        Require.Artwork();
        var core = StaticCore.Load(IgcReader.ParseFile(System.IO.Path.Combine(ArtRoot, ShopCatalog.CoreFileName)));
        var station = core.StationTypes.Values.First(s => s.Name.Contains("Garrison", StringComparison.OrdinalIgnoreCase));
        var allTech = new TechTreeBitMask(Enumerable.Repeat((byte)0xFF, TechTreeBitMask.ByteCount).ToArray());
        var buyable = Catalog.FilteredFor(station.TtbmLocal, allTech);

        var start = buyable.Hulls.First().HullId;
        var m = new ShopModel(buyable, start, Array.Empty<ExpandedPartData>());

        var seen = new List<short>();
        short first = m.EffectiveHullId;
        do
        {
            m.CycleHull(true);
            seen.Add(m.EffectiveHullId);
        } while (m.EffectiveHullId != first);

        // Visited every buyable hull exactly once (stable cyclic order).
        Assert.True(buyable.Hulls.Count == seen.Count, $"visited {seen.Count} of {buyable.Hulls.Count} buyable hulls");
        Assert.Equal(buyable.Hulls.Select(h => h.HullId).OrderBy(x => x).ToHashSet(),
            seen.ToHashSet());
        // Stable order = the list order, wrapped from the start.
        var expectedOrder = buyable.Hulls.Select(h => h.HullId).ToList();
        int rot = expectedOrder.IndexOf(first);
        var rotated = expectedOrder.Skip(rot + 1).Concat(expectedOrder.Take(rot + 1)).ToList();
        Assert.Equal(rotated, seen);
        // Every visited hull satisfies the native predicate.
        foreach (var id in seen)
        {
            var h = buyable.Hulls.First(x => x.HullId == id);
            Assert.True(h.GroupId >= 0);
            Assert.False(SuccessorUpgrade.HullIsObsolete(core, h, station.TtbmLocal, allTech));
        }
    }

    /// <summary>⭐ P519 — a forward cycle wraps; the cycle does not change the
    /// chosen hull when there is only one buyable hull.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Hull_cycle_forward_and_backward()
    {
        Require.Artwork();
        var m = Model();
        short eff = m.EffectiveHullId;
        m.CycleHull(false);
        m.CycleHull(true);
        Assert.Equal(eff, m.EffectiveHullId);   // prev then next returns to start
    }

    // ── P519 (Bug 3): the launch always carries the full intended loadout ────

    /// <summary>⭐ P519 Bug 3 — launching WITHOUT touching the shop sends the
    /// ship's current mounted parts (N > 0), never 0. This is the regression
    /// that previously stripped the ship to a bare hull.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Launching_without_touching_sends_the_full_current_loadout()
    {
        Require.Artwork();
        var weapon = FirstWeapon();
        var shield = Catalog.Parts.First(p => p.EquipmentType == EquipmentType.Shield);
        var current = new[]
        {
            Mounted(weapon, 0, 20),
            Mounted(shield, 0, 1),
        };
        var m = new ShopModel(Catalog, CurrentHull().HullId, current);

        Assert.Equal(2, m.ChosenParts.Count);                    // N > 0
        Assert.Equal(current.Select(p => p.PartId).OrderBy(x => x),
            m.ChosenParts.Select(p => p.PartId).OrderBy(x => x)); // == the docked ship's parts
        Assert.Equal(m.CurrentParts.Count, m.ChosenParts.Count);
    }

    /// <summary>⭐ P519 Bug 3 — changing the hull drops the parts that don't fit
    /// the new hull (native ReplaceLoadout mounts only what fits), so the launch
    /// after a hull change sends the new hull's mountable loadout — every sent
    /// part CanMountPart the new effective hull.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Changing_hull_drops_parts_that_do_not_fit_the_new_hull()
    {
        Require.Artwork();
        var (cur, w, target) = NonFitTriple();   // w mounts cur, not target
        var m = new ShopModel(Catalog, cur.HullId, new[] { Mounted(w, 0, 20) });
        Assert.Contains(m.ChosenParts, p => p.PartId == w.PartId);

        m.CycleHullTo(target.HullId);
        Assert.Equal(target.HullId, m.EffectiveHullId);
        Assert.True(m.WillSwapHull);
        Assert.DoesNotContain(m.ChosenParts, p => p.PartId == w.PartId);   // dropped
        foreach (var p in m.ChosenParts)
        {
            var pt = LookupPart(p.PartId);
            Assert.True(pt is { } pp && target.CanMountPart(pp), $"part {p.PartId} must fit the new hull");
        }
    }

    // ── preserved behaviour (navigation via slots / cycle) ──────────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Effective_hull_defaults_to_current_until_a_hull_is_picked()
    {
        Require.Artwork();
        var m = Model();
        Assert.Equal(CurrentHull().HullId, m.EffectiveHullId);
        Assert.Null(m.ChosenHullId);
    }

    /// <summary>⭐ preserved — buying a weapon then a non-weapon keeps per-type
    /// mounts (P437): weapon at its hardpoint mount, non-weapon at its single
    /// mount 0. Uses a hull that mounts both (scanned), so no hull-cycling disturbs
    /// the pending loadout.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Buying_weapon_then_shield_keeps_per_type_mounts()
    {
        Require.Artwork();
        var weapon = FirstWeapon();
        var shield = Catalog.Parts.First(p => p.EquipmentType == EquipmentType.Shield);
        // Find a hull that mounts BOTH a weapon and a shield (scanned; the first
        // weapon + first shield don't necessarily share a mounting hull, so scan
        // for any triple that does).
        var trip = Catalog.Hulls.SelectMany(h => Catalog.Parts
            .Where(p => p.EquipmentType == EquipmentType.Weapon && h.CanMountPart(p))
            .SelectMany(w => Catalog.Parts
                .Where(sh => sh.EquipmentType == EquipmentType.Shield && h.CanMountPart(sh))
                .Select(sh => (Hull: h, Weapon: w, Shield: sh))))
            .First();
        var hull = trip.Hull;
        weapon = trip.Weapon;
        shield = trip.Shield;

        var m = new ShopModel(Catalog, hull.HullId, Array.Empty<ExpandedPartData>());
        MountViaSlots(m, weapon.PartId);
        MountViaSlots(m, shield.PartId);

        Assert.Equal(2, m.ChosenParts.Count);
        // The non-weapon (shield) occupies its single mount 0.
        Assert.Equal((byte)0, m.ChosenParts.Single(p => p.PartId == shield.PartId).MountId);
        // The weapon occupies a real weapon hardpoint (< MaxWeapons).
        byte wMount = m.ChosenParts.Single(p => p.PartId == weapon.PartId).MountId;
        Assert.True(wMount < hull.MaxWeapons, $"weapon mounted at {wMount}, not a real hardpoint");
    }

    /// <summary>⭐ preserved — the pending loadout pre-seeds from the ship's
    /// current parts (P394), verbatim including real amount + fraction.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Shop_opens_with_current_parts_pre_seeded()
    {
        Require.Artwork();
        var weapon = FirstWeapon();
        var current = new[] { Mounted(weapon, 0, 3, 100) };
        var m = new ShopModel(Catalog, CurrentHull().HullId, current);

        Assert.Single(m.ChosenParts);
        Assert.Equal((short)3, m.ChosenParts[0].Amount);
        Assert.Equal((byte)100, m.ChosenParts[0].FractionMounted);
    }

    // ── P468 — pricing: loadout value, cost, affordability, grey-out ────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Cost_of_a_hull_swap_with_a_weapon()
    {
        Require.Artwork();
        var current = CurrentHull();
        var target = Catalog.Hulls.Where(h => h.HullId != current.HullId).OrderBy(h => h.HullId).First();
        var weapon = FirstWeapon();

        var m = new ShopModel(Catalog, current.HullId, Array.Empty<ExpandedPartData>());
        m.MoveCursor(Catalog.Hulls.ToList().FindIndex(h => h.HullId == target.HullId) - m.Cursor);
        m.Activate();                                  // pick target hull
        MountViaSlots(m, weapon.PartId);               // add a weapon

        long expectedNew = (long)target.Price + weapon.Price;
        long expectedCurrent = current.Price;
        Assert.Equal(expectedCurrent, m.CurrentShipValue);
        Assert.Equal(expectedNew, m.NewLoadoutValue);
        Assert.Equal(expectedNew - expectedCurrent, m.Cost);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Affordability_flag_at_the_exact_money_boundary()
    {
        Require.Artwork();
        var current = Catalog.Hulls.OrderBy(h => h.Price).First();
        var target = Catalog.Hulls.Where(h => h.Price > current.Price).OrderBy(h => h.Price).First();
        long cost = (long)target.Price - current.Price;
        Assert.True(cost > 0);

        var m = new ShopModel(Catalog, current.HullId, Array.Empty<ExpandedPartData>(), money: (int)cost);
        m.MoveCursor(Catalog.Hulls.ToList().FindIndex(h => h.HullId == target.HullId) - m.Cursor);
        m.Activate();
        Assert.Equal(cost, m.Cost);
        Assert.True(m.CanAffordTotal);
        Assert.True(m.TryLaunch());

        var poor = new ShopModel(Catalog, current.HullId, Array.Empty<ExpandedPartData>(), money: (int)cost - 1);
        poor.MoveCursor(Catalog.Hulls.ToList().FindIndex(h => h.HullId == target.HullId) - poor.Cursor);
        poor.Activate();
        Assert.False(poor.CanAffordTotal);
        Assert.False(poor.TryLaunch());
        Assert.NotNull(poor.BlockReason);
    }

    /// <summary>⭐ preserved — the PART row grey-out (P468): an expensive,
    /// unowned part is greyed; once chosen it is not.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Expensive_unowned_part_is_greyed_but_chosen_is_not()
    {
        Require.Artwork();
        var current = CurrentHull();
        var m = new ShopModel(Catalog, current.HullId, Array.Empty<ExpandedPartData>(), money: 0);
        var pricey = Catalog.Launchers!.First(l => l.Part.Price > 0).Part;
        Assert.True(m.PartRowGreyed(pricey.PartId, pricey.Price));

        MountViaSlots(m, pricey.PartId);
        Assert.False(m.PartRowGreyed(pricey.PartId, pricey.Price));
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>Drive the P519 slot UI to mount a part by id: pick a hull that
    /// can host it if the current one can't, open the matching slot, choose it.</summary>
    internal static void MountViaSlots(ShopModel m, short partId)
    {
        var pt = LookupPart(partId)
            ?? throw new InvalidOperationException($"part {partId} not in catalogue");
        var eff = m.EffectiveHull;
        if (eff is not { } hull || !hull.CanMountPart(pt))
            m.CycleHullToMounting(partId);

        m.SwitchTab(ShopTab.Parts);
        int slotIdx = FindSlotFor(m, partId);
        m.MoveCursor(slotIdx - m.Cursor);
        m.Activate();                                    // open the slot
        // find the part in the slot's offers
        int pIdx = 0;
        while (pIdx < m.OpenSlotParts.Count && m.OpenSlotParts[pIdx].Part.PartId != partId) pIdx++;
        if (pIdx >= m.OpenSlotParts.Count)
            throw new InvalidOperationException($"part {partId} not offered in its slot for the effective hull");
        m.MoveCursor(pIdx - m.Cursor);
        m.Activate();                                    // choose it
    }

    private static int FindSlotFor(ShopModel m, short partId)
    {
        var pt = LookupPart(partId) is { } p ? p : throw new InvalidOperationException($"part {partId} not in catalogue");
        for (int i = 0; i < m.Slots.Count; i++)
        {
            var s = m.Slots[i];
            if (s.Type != pt.EquipmentType) continue;
            if (s.IsWeapon)
            {
                var hull = m.EffectiveHull!.Value;
                if (s.Mount < hull.Hardpoints.Length && (pt.PartMask & ~hull.Hardpoints[s.Mount].PartMask) == 0)
                    return i;
            }
            else
            {
                return i;
            }
        }
        throw new InvalidOperationException($"no slot for part {partId}");
    }

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

    // ── P755: loadout-menu sound events (native load.cpp:1480-1489,1880-1895) ──

    /// <summary>P755 — choosing a part in the open-slot menu raises
    /// SlotPartChosen(mounted: true); the "(make slot empty)" row raises it with
    /// mounted: false — the native mountSound / unmountSound split
    /// (load.cpp:1887/1889).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Slot_part_choice_raises_mount_and_clear_events()
    {
        Require.Artwork();
        var hull = HullWithMagazine();
        var m = new ShopModel(Catalog, hull.HullId, Array.Empty<ExpandedPartData>());

        m.SwitchTab(ShopTab.Parts);
        int idx = 0;
        while (m.Slots[idx].Type != EquipmentType.Magazine) idx++;
        m.MoveCursor(idx - m.Cursor);
        m.Activate();               // open the slot

        var events = new List<bool>();
        m.SlotPartChosen += mounted => events.Add(mounted);

        m.Activate();               // choose the first offered part
        Assert.Equal([true], events);

        // reopen and clear the slot
        m.SwitchTab(ShopTab.Parts);
        idx = 0;
        while (m.Slots[idx].Type != EquipmentType.Magazine) idx++;
        m.MoveCursor(idx - m.Cursor);
        m.Activate();
        m.MoveCursor(m.OpenSlotParts.Count - m.Cursor);   // the "(empty)" row
        m.Activate();
        Assert.Equal([true, false], events);
    }

    /// <summary>P755 — picking a different hull in the HULLS list raises
    /// HullChosen exactly when the effective hull changes (the native hull-menu
    /// selection mounts the new hull's default loadout with mountSound,
    /// load.cpp:1480-1487); re-picking the current hull does not.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Hull_pick_raises_hull_chosen_only_on_change()
    {
        Require.Artwork();
        var cur = CurrentHull();
        var other = Catalog.Hulls.First(h => h.HullId != cur.HullId);
        var m = new ShopModel(Catalog, cur.HullId, Array.Empty<ExpandedPartData>());

        int raised = 0;
        m.HullChosen += () => raised++;

        // move the cursor onto the other hull and pick it
        int idx = 0;
        foreach (var h in m.BuyableHulls) { if (h.HullId == other.HullId) break; idx++; }
        m.MoveCursor(idx - m.Cursor);
        m.Activate();
        Assert.Equal(1, raised);
        Assert.Equal(other.HullId, m.EffectiveHullId);

        // re-picking the same hull remounts its default loadout again (native
        // plays mountSound for every hull-menu selection, load.cpp:1480-1487)
        m.Activate();
        Assert.Equal(2, raised);
        Assert.Equal(other.HullId, m.EffectiveHullId);

        // clicking the CURRENT hull de-picks (keep current) without a loadout
        // change → no event
        int curIdx = 0;
        foreach (var h in m.BuyableHulls) { if (h.HullId == cur.HullId) break; curIdx++; }
        m.MoveCursor(curIdx - m.Cursor);
        m.Activate();
        Assert.Equal(2, raised);
        Assert.Equal(cur.HullId, m.EffectiveHullId);
    }
}

internal static class ShopModelTestExtensions
{
    /// <summary>P519 — cycle the hull forward until <paramref name="hullId"/> is
    /// the effective hull (used by tests to reach a target hull).</summary>
    public static void CycleHullTo(this Allegiance.ClientCore.MonoGame.Rendering.ShopModel m, short hullId)
    {
        int guard = 0;
        while (m.EffectiveHullId != hullId && guard++ < 10_000)
            m.CycleHull(true);
        if (m.EffectiveHullId != hullId)
            throw new InvalidOperationException($"could not cycle to hull {hullId}");
    }

    /// <summary>P519 — cycle forward to the first hull that can mount a part.</summary>
    public static void CycleHullToMounting(this Allegiance.ClientCore.MonoGame.Rendering.ShopModel m, short partId)
    {
        int guard = 0;
        while (guard++ < 10_000)
        {
            if (m.EffectiveHull is { } h && CanMount(m, partId, h)) return;
            m.CycleHull(true);
        }
        throw new InvalidOperationException($"no hull in the cycle mounts part {partId}");
    }

    private static bool CanMount(Allegiance.ClientCore.MonoGame.Rendering.ShopModel m, short partId, in DataHullTypeIGC hull)
    {
        foreach (var p in m.Catalog.Parts) if (p.PartId == partId) return hull.CanMountPart(p);
        foreach (var l in m.Catalog.Launchers!) if (l.Part.PartId == partId) return hull.CanMountPart(l.Part);
        return false;
    }
}
