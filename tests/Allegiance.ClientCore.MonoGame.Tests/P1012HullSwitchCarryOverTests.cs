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
/// P1012 — what a hull change does to the pending loadout, per hull-pair class,
/// against the real PCore001. Native <c>LoadoutImpl::BuyHull</c>
/// (<c>WinTrek/load.cpp:1476-1490</c>) carries NOTHING over: it terminates every
/// pending part and rebuilds the ship from custom-loadout slot 0 for the new
/// hull, else the hull's default loadout (<c>clintlib.h:1790-1802</c>). Expected
/// part lists and amounts below are written out by hand from the core's
/// preferred-part lists, hardpoint masks and capacities.
/// </summary>
public class P1012HullSwitchCarryOverTests
{
    private static readonly string ArtRoot = ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static readonly Lazy<ShopCatalog> LazyCatalog = new(() =>
        ShopCatalog.TryLoad(ArtRoot) ?? throw new InvalidOperationException($"PCore001.igc not loadable from {ArtRoot}"));

    private static ShopCatalog Catalog => LazyCatalog.Value;

    // PCore001 hulls used below (one faction's line).
    private const short Fighter = 121;      // 2 fixed guns, magazine 20, dispenser 1, chaff 4
    private const short EnhFighter = 122;   // 3 fixed guns
    private const short AdvFighter = 123;   // 3 fixed guns, magazine 25
    private const short Bomber = 124;       // 2 fixed guns + 2 turrets, magazine 60, chaff 8, $500
    private const short Gunship = 66;       // 1 fixed gun + 2 turrets, $1
    private const short AdvScout = 13;      // 1 fixed gun, dispenser 2
    private const short HvyScout = 85;      // dispenser 4
    private const short LxyScout = 92;      // dispenser 6, $500

    // Parts.
    private const short PwGatGun = 33, EwPulseLaser = 129, PwAutoCan = 130, PeSkyCap = 12, PeAutoCan = 47;
    private const short SmShield = 21, MedShield = 22, Booster1 = 23, HvyBooster = 174;
    private const short Counter1 = 80, Counter2 = 85, MinePack1 = 13, MrmZeus = 146, SrmAntiBase1 = 157, SrpKiller1 = 116;
    private const short Sanctuary = 2;      // dispenser, $100 each, expendable size 2

    private static DataHullTypeIGC Hull(short id) => Catalog.Hulls.First(h => h.HullId == id);

    private static ExpandedPartData Part(short id, byte mount, short amount) =>
        new(id, amount, mount, ExpandedPartData.FullyMounted);

    private static (short Id, byte Mount, short Amount)[] Shape(IEnumerable<ExpandedPartData> parts) =>
        parts.Select(p => (p.PartId, p.MountId, p.Amount)).OrderBy(x => x).ToArray();

    private static (short Id, byte Mount, short Amount)[] Expect(params (short Id, byte Mount, short Amount)[] parts) =>
        parts.OrderBy(x => x).ToArray();

    /// <summary>Adv Fighter's default: Hvy Booster, Counter 1 ×4 (chaff 4 / size 1),
    /// Mine Pack 1 ×1 (dispenser 1 / 1), EW Pulse Laser in all three guns, MRM Zeus
    /// ×5 (magazine 25 / 5), Sm Shield 1.</summary>
    private static (short, byte, short)[] AdvFighterDefault => Expect(
        (HvyBooster, 0, 1), (Counter1, 0, 4), (MinePack1, 0, 1),
        (EwPulseLaser, 0, 20), (EwPulseLaser, 1, 20), (EwPulseLaser, 2, 20),
        (MrmZeus, 0, 5), (SmShield, 0, 1));

    /// <summary>Bomber's default: EW Pulse Laser in the two fixed guns, PW AutoCan in
    /// the two turrets (mounts 2 and 3), Med Shield 1, SRM Anti-Base 1 ×4
    /// (magazine 60 / 15), Counter 1 ×8.</summary>
    private static (short, byte, short)[] BomberDefault => Expect(
        (EwPulseLaser, 0, 20), (EwPulseLaser, 1, 20), (PwAutoCan, 2, 20), (PwAutoCan, 3, 20),
        (MedShield, 0, 1), (SrmAntiBase1, 0, 4), (Counter1, 0, 8));

    /// <summary>A ship docked in an Enh Fighter the player kitted out by hand — every
    /// one of these parts also mounts on the Adv Fighter at the same mount.</summary>
    private static ExpandedPartData[] HandKittedEnhFighter =>
    [
        Part(PwGatGun, 0, 20), Part(PwGatGun, 1, 20), Part(PwGatGun, 2, 20),
        Part(SmShield, 0, 1), Part(Booster1, 0, 1),
    ];

    // ── hull pair classes ───────────────────────────────────────────────────

    /// <summary>fighter → fighter. The mockup's "keep what fits" is NOT native:
    /// all five docked parts fit the Adv Fighter, yet the pending loadout is the
    /// Adv Fighter's default and the PW Gat Guns are gone.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Fighter_to_fighter_rebuilds_the_default_and_keeps_nothing_that_fits()
    {
        Require.Artwork();
        var adv = Hull(AdvFighter);
        foreach (var p in HandKittedEnhFighter)
            Assert.True(adv.CanMountPart(Catalog.Parts.First(x => x.PartId == p.PartId), p.MountId),
                $"fixture: part {p.PartId} must fit the Adv Fighter at mount {p.MountId}");

        var m = new ShopModel(Catalog, EnhFighter, HandKittedEnhFighter);
        m.CycleHullTo(AdvFighter);

        Assert.Equal(AdvFighterDefault, Shape(m.ChosenParts));
        Assert.DoesNotContain(m.ChosenParts, p => p.PartId is PwGatGun or Booster1);
        Assert.Empty(m.ChosenPartsThatDontFitEffectiveHull);
    }

    /// <summary>fighter → bomber: the bomber's default, including its two turret
    /// hardpoints (native TryToBuyParts walks every weapon mount,
    /// <c>iMountMax = GetMaxWeapons()</c>, clintlib.h:1364-1375).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Fighter_to_bomber_rebuilds_the_bomber_default_with_turret_guns()
    {
        Require.Artwork();
        var m = new ShopModel(Catalog, EnhFighter, HandKittedEnhFighter);
        m.CycleHullTo(Bomber);

        Assert.Equal(BomberDefault, Shape(m.ChosenParts));
        var turrets = m.Slots.Where(s => s.IsTurret).ToArray();
        Assert.Equal(new byte[] { 2, 3 }, turrets.Select(s => s.Mount).ToArray());
        Assert.All(turrets, s => Assert.Equal(PwAutoCan, m.PartInSlot(s)!.Value.PartId));
        Assert.Empty(m.ChosenPartsThatDontFitEffectiveHull);
    }

    /// <summary>→ turret hull (Gunship: one fixed gun, two turrets). The fixed-gun
    /// preferred part does not fit the turret masks and vice versa, so each mount
    /// gets the first preferred weapon its own mask accepts.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void To_a_turret_hull_fills_fixed_and_turret_mounts_by_their_own_masks()
    {
        Require.Artwork();
        var m = new ShopModel(Catalog, EnhFighter, HandKittedEnhFighter);
        m.CycleHullTo(Gunship);

        Assert.Equal(Expect(
            (PeAutoCan, 0, 20), (PeSkyCap, 1, 20), (PeSkyCap, 2, 20),
            (MedShield, 0, 1), (SrpKiller1, 0, 8), (Counter1, 0, 8)), Shape(m.ChosenParts));
        Assert.Equal(new[] { false, true, true },
            m.Slots.Where(s => s.IsWeapon).Select(s => s.IsTurret).ToArray());
        var gunship = Hull(Gunship);
        var skyCap = Catalog.Parts.First(p => p.PartId == PeSkyCap);
        Assert.False(gunship.CanMountPart(skyCap, 0));   // turret gun, not for the nose
        Assert.Empty(m.ChosenPartsThatDontFitEffectiveHull);
    }

    /// <summary>→ hull with fewer mounts (three guns → one): no part is left on a
    /// mount the new hull lacks, and nothing is reported as "will be dropped".</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void To_a_hull_with_fewer_mounts_leaves_no_part_on_a_missing_mount()
    {
        Require.Artwork();
        var m = new ShopModel(Catalog, EnhFighter, HandKittedEnhFighter);
        m.CycleHullTo(AdvScout);

        var scout = Hull(AdvScout);
        Assert.Single(scout.Hardpoints);
        Assert.Single(m.ChosenParts, p => p.PartId == PwGatGun && p.MountId == 0);
        foreach (var p in m.ChosenParts)
        {
            var part = Catalog.Parts.Cast<DataPartTypeIGC?>().FirstOrDefault(x => x!.Value.PartId == p.PartId)
                ?? Catalog.Launchers!.First(l => l.Part.PartId == p.PartId).Part;
            Assert.True(scout.CanMountPart(part, p.MountId), $"part {p.PartId} at mount {p.MountId}");
        }
        Assert.Empty(m.ChosenPartsThatDontFitEffectiveHull);
    }

    // ── back and forth ──────────────────────────────────────────────────────

    /// <summary>A → B → A does NOT restore: neither the docked ship's parts nor
    /// the edits made before leaving come back. Native rebuilds A the same way as
    /// any other hull — slot 0, else the default — and there is no slot 0 here.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Back_and_forth_does_not_restore_the_docked_parts_or_the_edits()
    {
        Require.Artwork();
        var docked = new[] { Part(PwGatGun, 0, 20), Part(PwGatGun, 1, 20), Part(PwGatGun, 2, 20), Part(SmShield, 0, 1) };
        var m = new ShopModel(Catalog, AdvFighter, docked);
        m.ClearSlot(m.Slots.First(s => s.Type == EquipmentType.Shield));     // an edit
        Assert.Equal(3, m.ChosenParts.Count);

        m.CycleHull(true);
        Assert.NotEqual(AdvFighter, m.EffectiveHullId);
        m.CycleHull(false);

        Assert.Equal(AdvFighter, m.EffectiveHullId);
        Assert.False(m.WillSwapHull);
        Assert.Equal(AdvFighterDefault, Shape(m.ChosenParts));
        Assert.DoesNotContain(m.ChosenParts, p => p.PartId == PwGatGun);
    }

    /// <summary>A → B → A restores what was last BOUGHT (or defaulted) for A: Buy
    /// and Default write custom-loadout slot 0 (load.cpp:1524-1527, :1643) and a
    /// hull change reads it (load.cpp:1481). Later unbought edits are not kept.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Back_and_forth_restores_the_loadout_last_bought_for_that_hull()
    {
        Require.Artwork();
        var docked = new[] { Part(PwGatGun, 0, 20), Part(PwGatGun, 1, 20), Part(PwGatGun, 2, 20), Part(SmShield, 0, 1) };
        var m = new ShopModel(Catalog, AdvFighter, docked);
        m.SaveCustomLoadout();                                               // what Buy does
        m.ClearSlot(m.Slots.First(s => s.Type == EquipmentType.Shield));     // unbought edit

        m.CycleHullTo(Bomber);
        Assert.Equal(BomberDefault, Shape(m.ChosenParts));
        m.CycleHullTo(AdvFighter);

        Assert.Equal(Shape(docked), Shape(m.ChosenParts));
    }

    /// <summary>Picking the hull that is already pending is not a hull change
    /// (load.cpp:1896): slot edits survive. Picking the docked hull while another
    /// is pending is one.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Picking_the_pending_hull_again_keeps_the_edits()
    {
        Require.Artwork();
        var m = new ShopModel(Catalog, AdvFighter, Array.Empty<ExpandedPartData>());
        m.CycleHullTo(Bomber);
        m.ClearSlot(m.Slots.First(s => s.Type == EquipmentType.Shield));
        var edited = Shape(m.ChosenParts);
        Assert.NotEqual(BomberDefault, edited);

        m.SwitchTab(ShopTab.Hulls);
        m.MoveCursor(m.BuyableHulls.ToList().FindIndex(h => h.HullId == Bomber) - m.Cursor);
        m.Activate();                                    // Bomber again
        Assert.Equal(edited, Shape(m.ChosenParts));

        m.MoveCursor(m.BuyableHulls.ToList().FindIndex(h => h.HullId == AdvFighter) - m.Cursor);
        m.Activate();                                    // the docked hull
        Assert.Null(m.ChosenHullId);
        Assert.Equal(AdvFighterDefault, Shape(m.ChosenParts));
    }

    // ── custom-loadout slot 0 ───────────────────────────────────────────────

    /// <summary>The slots belong to the client, not to one visit to the loadout
    /// screen (native <c>m_customLoadouts</c>, clintlib.h:826): a loadout bought at
    /// one dock is what the hull comes back with at the next.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Saved_loadouts_survive_into_the_next_dock()
    {
        Require.Artwork();
        var store = new ShopCustomLoadouts();
        var bought = new[] { Part(PwGatGun, 0, 20), Part(SmShield, 0, 1) };
        var firstDock = new ShopModel(Catalog, AdvFighter, bought, customLoadouts: store);
        firstDock.SaveCustomLoadout();
        Assert.True(store.Has(AdvFighter, 0));

        var nextDock = new ShopModel(Catalog, Bomber, Array.Empty<ExpandedPartData>(), customLoadouts: store);
        nextDock.CycleHullTo(AdvFighter);
        Assert.Equal(Shape(bought), Shape(nextDock.ChosenParts));

        var otherClient = new ShopModel(Catalog, Bomber, Array.Empty<ExpandedPartData>());
        otherClient.CycleHullTo(AdvFighter);
        Assert.Equal(AdvFighterDefault, Shape(otherClient.ChosenParts));
    }

    /// <summary>A saved loadout is bought again from what the station sells NOW
    /// (native ReplaceLoadout, clintlib.h:1824-1845): the successor when the saved
    /// part has been out-teched, the nearest buyable predecessor when it is no
    /// longer for sale (GetSimilarPart, stationIGC.h:453-470), nothing when no
    /// form of it is sold.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void A_saved_part_is_rebought_as_its_successor_or_similar_part()
    {
        Require.Artwork();
        Assert.Equal(Counter2, Catalog.Launchers!.First(l => l.Part.PartId == Counter1).Part.SuccessorPartId);

        ShopCatalog Without(params short[] ids) =>
            Catalog with { Launchers = Catalog.Launchers!.Where(l => !ids.Contains(l.Part.PartId)).ToList() };
        IEnumerable<short> Chain(short id)
        {
            for (short cur = id; cur >= 0;)
            {
                yield return cur;
                cur = Catalog.Launchers!.First(l => l.Part.PartId == cur).Part.SuccessorPartId;
            }
        }

        short[] RestoreWith(short savedId, ShopCatalog offered)
        {
            var store = new ShopCustomLoadouts();
            var m = new ShopModel(Catalog, AdvFighter, new[] { Part(savedId, 0, 4) }, customLoadouts: store);
            m.SaveCustomLoadout();
            m.CycleHullTo(Bomber);
            m.RefreshOffers(offered, money: null);
            m.CycleHullTo(AdvFighter);
            return m.ChosenParts.Select(p => p.PartId).ToArray();
        }

        // Counter 1 saved, Counter 1 out-teched (only Counter 2 on offer) → Counter 2.
        Assert.Equal(new[] { Counter2 }, RestoreWith(Counter1, Without(Chain(Counter1).Where(id => id != Counter2).ToArray())));
        // Counter 2 saved, nothing from Counter 2 up on offer → the predecessor Counter 1.
        Assert.Equal(new[] { Counter1 }, RestoreWith(Counter2, Without(Chain(Counter2).ToArray())));
        // No counter of that line on offer at all → the slot stays empty.
        Assert.Empty(RestoreWith(Counter2, Without(Chain(Counter1).ToArray())));
    }

    // ── amounts and money ───────────────────────────────────────────────────

    /// <summary>A launcher is bought with what the hull holds — capacity for its
    /// equipment type / expendable size, minimum 1 (launchertypeigc.h:130-142) —
    /// so the same part changes amount when the hull does.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Launcher_amounts_follow_the_hull()
    {
        Require.Artwork();
        short AmountOn(short hullId, short partId)
        {
            var m = new ShopModel(Catalog, AdvScout, Array.Empty<ExpandedPartData>());
            m.CycleHullTo(hullId);
            return m.ChosenParts.First(p => p.PartId == partId).Amount;
        }

        Assert.Equal(4, AmountOn(Fighter, MrmZeus));       // magazine 20 / size 5
        Assert.Equal(5, AmountOn(AdvFighter, MrmZeus));    // magazine 25 / size 5
        Assert.Equal(4, AmountOn(AdvFighter, Counter1));   // chaff 4 / size 1
        Assert.Equal(8, AmountOn(Bomber, Counter1));       // chaff 8 / size 1
        Assert.Equal(4, AmountOn(Bomber, SrmAntiBase1));   // magazine 60 / size 15
    }

    /// <summary>The price shown is the price charged: native GetValue adds
    /// amount × price for a launcher (magazineigc.h:85-88, dispenserigc.h:86-89),
    /// so Sanctuary ($100 each, size 2) costs $100 on an Adv Scout (dispenser 2),
    /// $200 on a Hvy Scout (4) and $300 on a Lxy Scout (6, hull $500).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(AdvScout, 1, 0, 100)]
    [InlineData(HvyScout, 2, 0, 200)]
    [InlineData(LxyScout, 3, 500, 800)]
    public void A_priced_launcher_costs_amount_times_price_on_each_hull(short hullId, short amount, int hullPrice, int value)
    {
        Require.Artwork();
        Assert.Equal(hullPrice, Hull(hullId).Price);
        var m = new ShopModel(Catalog, Fighter, Array.Empty<ExpandedPartData>(), money: 150);
        m.CycleHullTo(hullId);
        Assert.Equal(hullPrice, m.NewLoadoutValue);       // the default loadout itself is free

        ShopModelTests.MountViaSlots(m, Sanctuary);
        Assert.Equal(amount, m.ChosenParts.Single(p => p.PartId == Sanctuary).Amount);
        Assert.Equal(value, m.NewLoadoutValue);
        Assert.Equal(0, m.CurrentShipValue);              // a bare free Fighter
        Assert.Equal(value, m.Cost);
        Assert.Equal(Math.Max(0, value - 150), m.BuyShortfall);
        Assert.Equal(value <= 150, m.CanAffordTotal);
        Assert.Equal(value <= 150, m.TryLaunch());
    }

    /// <summary>Money on the way back: a docked Lxy Scout carrying three Sanctuary
    /// is worth $800; switching to the free Adv Fighter default refunds all of it,
    /// and switching back costs only the hull again minus nothing carried.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Switching_away_refunds_the_old_hull_and_its_parts()
    {
        Require.Artwork();
        var m = new ShopModel(Catalog, LxyScout, new[] { Part(Sanctuary, 0, 3) }, money: 0);
        Assert.Equal(800, m.CurrentShipValue);
        Assert.Equal(0, m.Cost);

        m.CycleHullTo(AdvFighter);
        Assert.Equal(0, m.NewLoadoutValue);
        Assert.Equal(-800, m.Cost);
        Assert.Equal(0, m.BuyShortfall);

        // Back to the Lxy Scout: its default has no Sanctuary, so the refund is
        // the three Sanctuary only.
        m.CycleHullTo(LxyScout);
        Assert.Equal(500, m.NewLoadoutValue);
        Assert.Equal(-300, m.Cost);
    }

    /// <summary>The server refunds the whole docked ship, cargo included
    /// (FedSrv.CPP:6183, shipIGC.h:396-406); the shop sends no cargo, so the hold
    /// is sold back and the shown cost says so. Packs are free (packigc.h:80-83).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Current_ship_value_counts_the_cargo_hold()
    {
        Require.Artwork();
        var ammoPack = Catalog.Parts.First(p => p.Type is DataPartTypeIGC.Pack);
        var cargo = new[]
        {
            new ExpandedPartData(Sanctuary, 2, unchecked((byte)-1), 0),
            new ExpandedPartData(ammoPack.PartId, 500, unchecked((byte)-2), 0),
        };
        var m = new ShopModel(Catalog, AdvScout, new[] { Part(Sanctuary, 0, 1) }, money: 0, currentCargo: cargo);

        Assert.Equal(300, m.CurrentShipValue);            // 1 mounted + 2 in the hold, pack 0
        Assert.Equal(100, m.NewLoadoutValue);
        Assert.Equal(-200, m.Cost);
    }

    /// <summary>The "will be dropped" projection uses the server's per-mount test
    /// (shipIGC.h:1029-1031): a gun that fits the hull somewhere but not the
    /// hardpoint it sits in is reported.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Dont_fit_projection_is_per_mount()
    {
        Require.Artwork();
        var gunship = Hull(Gunship);
        var skyCap = Catalog.Parts.First(p => p.PartId == PeSkyCap);
        Assert.True(gunship.CanMountPart(skyCap));        // fits the hull (a turret)
        Assert.False(gunship.CanMountPart(skyCap, 0));    // not the nose gun

        var m = new ShopModel(Catalog, Gunship, new[] { Part(PeSkyCap, 0, 20), Part(PeSkyCap, 1, 20) });
        var bad = Assert.Single(m.ChosenPartsThatDontFitEffectiveHull);
        Assert.Equal((PeSkyCap, (byte)0), (bad.PartId, bad.MountId));
    }
}

/// <summary>P1012 — native launcher amount, written independently of the port's
/// own clamp: <c>IlauncherTypeIGC::GetAmount(pship)</c>,
/// <c>launchertypeigc.h:130-142</c> = hull capacity for the equipment type /
/// expendable size, minimum 1.</summary>
internal static class P1012Native
{
    public static short LauncherAmount(StaticCore core, in DataHullTypeIGC hull, in DataPartTypeIGC launcher)
    {
        int capacity = launcher.EquipmentType switch
        {
            EquipmentType.Magazine => hull.CapacityMagazine,
            EquipmentType.Dispenser => hull.CapacityDispenser,
            EquipmentType.ChaffLauncher => hull.CapacityChaffLauncher,
            _ => throw new ArgumentException("not a launcher"),
        };
        int size = launcher.Type switch
        {
            DataPartTypeIGC.MissileLauncher m => core.MissileTypes[m.ExpendableTypeId].LauncherExpendableSize,
            DataPartTypeIGC.MineLauncher m => core.MineTypes[m.ExpendableTypeId].LauncherExpendableSize,
            DataPartTypeIGC.ProbeLauncher m => core.ProbeTypes[m.ExpendableTypeId].LauncherExpendableSize,
            DataPartTypeIGC.ChaffLauncher m => core.ChaffTypes[m.ExpendableTypeId].LauncherExpendableSize,
            _ => throw new ArgumentException("not a launcher"),
        };
        int amount = capacity / size;
        return (short)(amount == 0 ? 1 : amount);
    }
}
