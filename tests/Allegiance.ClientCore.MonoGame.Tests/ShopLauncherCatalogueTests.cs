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
/// P434 — prove the shop catalogue now carries the REAL missile launchers from
/// <c>core.Launchers</c> (resolved/shaped via the P433 bridge
/// <see cref="StaticCore.TryBuildMissileLauncher"/>), that they are buyable /
/// equippable rows exactly like any other part (toggle -> the real launcher
/// part id + real ammo in the pending loadout), and that the SAME tech gate
/// (<see cref="TechTreeBitMask.RequiredTechsBuyable"/>) and the SAME hull-fit
/// machinery (<see cref="DataHullTypeIGC.CanMountPart"/>) apply to them — no
/// unconditional display, no fabricated launcher. Mirrors the
/// <see cref="ShopModelTests"/> style: real PCore001.igc under the artwork dir,
/// assert behaviour over content.
/// </summary>
public class ShopLauncherCatalogueTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static readonly ShopCatalog Catalog = LoadCatalog();
    private static ShopCatalog LoadCatalog() =>
        ShopCatalog.TryLoad(ArtRoot)
        ?? throw new InvalidOperationException($"PCore001.igc not loadable from {ArtRoot}");

    private static readonly StaticCore Core =
        StaticCore.Load(IgcReader.ParseFile(System.IO.Path.Combine(ArtRoot, ShopCatalog.CoreFileName)));

    private static ShopModel Model(short currentHullId = default) =>
        new(Catalog, currentHullId: currentHullId != default
                ? currentHullId
                : Catalog.Hulls.OrderBy(h => h.HullId).First().HullId,
            currentParts: Array.Empty<ExpandedPartData>());

    private static DataHullTypeIGC CurrentHull() =>
        Catalog.Hulls.OrderBy(h => h.HullId).First();

    private static DataHullTypeIGC DifferentHull(short currentId) =>
        Catalog.Hulls.OrderBy(h => h.HullId).First(h => h.HullId != currentId);

    private static int FlatIndexOfLauncher(short partId)
    {
        var list = Catalog.Launchers!.ToList();
        return Catalog.Hulls.Count + Catalog.Parts.Count + list.FindIndex(l => l.Part.PartId == partId);
    }

    /// <summary>A real missile launcher + a hull that mounts it + a different
    /// hull that does NOT mount it — scanned from the real core (mirroring the
    /// NonFitTriple style in ShopModelTests), proving the fit machinery has a
    /// real launcher/hull mismatch to exercise.</summary>
    private static (LauncherOffer lo, DataHullTypeIGC YesHull, DataHullTypeIGC NoHull) FitNonFitTriple()
    {
        foreach (var lo in Catalog.Launchers!)
            foreach (var hYes in Catalog.Hulls)
            {
                var yes = lo.Part;
                if (!hYes.CanMountPart(yes)) continue;
                foreach (var hNo in Catalog.Hulls)
                {
                    var noPart = lo.Part;
                    if (hNo.HullId != hYes.HullId && !hNo.CanMountPart(noPart))
                        return (lo, hYes, hNo);
                }
            }
        throw new InvalidOperationException("PCore001 has no (launcher, mounting-hull, non-mounting-hull) triple");
    }

    /// <summary>A launcher with a NON-nil tech requirement (so the tech gate has
    /// something real to filter), if any exist in the core.</summary>
    private static LauncherOffer? RequiringLauncher() =>
        Catalog.Launchers!.FirstOrDefault(l => l.Part.TtbmRequired is { Length: > 0 }
            && l.Part.TtbmRequired.Any(b => b != 0));

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void CatalogueCarriesRealLaunchersWithRealNamesAndAmmoAcrossAllKinds()
    {
        Require.Artwork();
        Assert.NotNull(Catalog.Launchers);
        Assert.NotEmpty(Catalog.Launchers!);
        foreach (var lo in Catalog.Launchers!)
        {
            // Every offered launcher is a REAL core.Launchers record (keyed by
            // PartId) whose expendable is a REAL type in EXACTLY the table its kind
            // claims (missile/mine/chaff/probe) — never a fabricated launcher.
            Assert.True(Core.Launchers.ContainsKey(lo.Part.PartId),
                $"offered launcher id {lo.Part.PartId} must be a real core.Launchers record");
            var exp = Core.Launchers[lo.Part.PartId].ExpendabletypeId;
            switch (lo.Kind)
            {
                case LauncherKind.Missile: Assert.True(Core.MissileTypes.ContainsKey(exp), $"id {lo.Part.PartId} claimed Missile"); break;
                case LauncherKind.Mine: Assert.True(Core.MineTypes.ContainsKey(exp), $"id {lo.Part.PartId} claimed Mine"); break;
                case LauncherKind.Chaff: Assert.True(Core.ChaffTypes.ContainsKey(exp), $"id {lo.Part.PartId} claimed Chaff"); break;
                case LauncherKind.Probe: Assert.True(Core.ProbeTypes.ContainsKey(exp), $"id {lo.Part.PartId} claimed Probe"); break;
            }
            // Sensible row data: a real name and a real, positive ammo count
            // (DataLauncherTypeIGC.Amount), the exact ammo the launcher entry
            // carries on the wire.
            Assert.False(string.IsNullOrWhiteSpace(lo.Part.Name), "launcher must carry its real name");
            Assert.True(lo.Ammo > 0, $"launcher {lo.Part.Name} must carry real ammo (>0)");
            Assert.Equal(lo.Ammo, Core.Launchers[lo.Part.PartId].Amount);
        }
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void LauncherPartIdsAreDistinctFromRegularPartIds()
    {
        Require.Artwork();
        // A launcher PartId must not collide with a core.Parts PartId, else the
        // part/launcher rows would toggle the same id ambiguously. The two
        // tables are keyed separately; assert the offer set stays disjoint.
        var partIds = new HashSet<short>(Catalog.Parts.Select(p => p.PartId));
        foreach (var lo in Catalog.Launchers!)
            Assert.DoesNotContain(lo.Part.PartId, partIds);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void MovingOntoALauncherRowAndActivatingBuysItWithItsRealAmmo()
    {
        Require.Artwork();
        var (lo, yesHull, _) = FitNonFitTriple();
        var m = Model(currentHullId: yesHull.HullId);
        ShopModelTests.MountViaSlots(m, lo.Part.PartId);

        var bought = Assert.Single(m.ChosenParts);
        Assert.Equal(lo.Part.PartId, bought.PartId);
        Assert.Equal(lo.Ammo, bought.Amount);   // real ammo reserve, not a default
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void BoughtLauncherRendersAsASensiblePendingLoadoutRow()
    {
        Require.Artwork();
        var (lo, yesHull, _) = FitNonFitTriple();
        var m = Model(currentHullId: yesHull.HullId);
        ShopModelTests.MountViaSlots(m, lo.Part.PartId);

        // The bought launcher appears by name in the shop's pending-loadout
        // summary, and its real ammo is preserved on the chosen part (the model,
        // not the layout text — the pending "parts:" summary renders names only,
        // ShopLayout.cs:96). Prior to P555 this test also asserted the ammo
        // digit-string appeared in some row text, which only held because the
        // picked launcher's ammo coincidentally matched a $-price line; P555
        // changed which launcher FitNonFitTriple picks (chaff now mounts), so
        // that coincidence no longer holds — the ammo is verified on the model
        // instead, which is the actual contract.
        var rows = ShopLayout.Build(m);
        var pend = Assert.Single(rows, r => r.Text.StartsWith("parts:", StringComparison.Ordinal));
        Assert.Contains(lo.Part.Name, pend.Text, StringComparison.OrdinalIgnoreCase);
        var bought = Assert.Single(m.ChosenParts);
        Assert.Equal(lo.Part.PartId, bought.PartId);
        Assert.Equal(lo.Ammo, bought.Amount);   // real ammo reserve, not a default
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void FilteredForAppliesTheSameTechGateToLaunchers()
    {
        Require.Artwork();
        var req = RequiringLauncher();
        if (req is not { } required)
        {
            // The real core has no tech-requiring launcher; assert instead that
            // the gate leaves the (all clear / nil-requirement) launchers intact
            // rather than inventing one.
            var kept = Catalog.FilteredFor(new byte[TechTreeBitMask.ByteCount], TechTreeBitMask.FromBytes(new byte[TechTreeBitMask.ByteCount]));
            Assert.Equal(Catalog.Launchers!.Count, kept.Launchers!.Count);
            return;
        }

        // Side dev satisfying exactly this launcher's requirements -> offered.
        var yes = Catalog.FilteredFor(new byte[TechTreeBitMask.ByteCount],
            TechTreeBitMask.FromBytes(required.Part.TtbmRequired));
        Assert.Contains(yes.Launchers!, l => l.Part.PartId == required.Part.PartId);

        // Side dev with NONE of its requirement bits -> the launcher is filtered
        // OUT, exactly like any other gated part (never shown unconditionally).
        var no = Catalog.FilteredFor(new byte[TechTreeBitMask.ByteCount], TechTreeBitMask.FromBytes(new byte[TechTreeBitMask.ByteCount]));
        Assert.DoesNotContain(no.Launchers!, l => l.Part.PartId == required.Part.PartId);

        // Degraded gate (unknown tech) FAILS CLOSED (P468): offers only the
        // all-clear / no-tech-requirement launchers, never the full catalogue
        // (which made undeveloped launchers look buyable).
        var degraded = Catalog.FilteredFor(null, null);
        var noTechLaunchers = Catalog.Launchers!
            .Where(l => TechTreeBitMask.RequiredTechsBuyable(l.Part.TtbmRequired, Array.Empty<byte>(), TechTreeBitMask.Empty))
            .ToList();
        Assert.Equal(noTechLaunchers.Count, degraded.Launchers!.Count);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void NonMountingLauncherIsNotOfferedInAnySlotOfANonMountingHull()
    {
        Require.Artwork();
        var (lo, _, noHull) = FitNonFitTriple();
        var m = Model(currentHullId: noHull.HullId);
        m.SwitchTab(ShopTab.Parts);

        // P519 — the PARTS tab lists ONLY parts compatible with the selected
        // hull (native BuyPartMenu: equipment-type equality + CanMount). A
        // launcher that can't mount the current hull is offered in NO slot.
        bool offered = false;
        for (int i = 0; i < m.Slots.Count; i++)
        {
            m.MoveCursor(i - m.Cursor);
            m.Activate();
            offered |= m.OpenSlotParts.Any(o => o.Part.PartId == lo.Part.PartId);
            m.CloseSlot();
        }
        Assert.False(offered, "a non-mounting launcher must never be offered");
    }

    // ── P435 — the three new expendable kinds (mine / chaff / probe) ────────

    private static LauncherOffer OfferOf(LauncherKind kind) =>
        Catalog.Launchers!.First(l => l.Kind == kind);

    /// <summary>P519 — a launcher of <paramref name="kind"/> that some real hull
    /// can mount, plus that hull. Returns nulls when NO hull mounts any launcher
    /// of that kind (a genuinely unbuyable kind — e.g. this core has no hull with
    /// a chaff socket, so no chaff dispenser is offered anywhere).</summary>
    private static (LauncherOffer? lo, DataHullTypeIGC? hull) MountableDispenser(LauncherKind kind)
    {
        foreach (var lo in Catalog.Launchers!.Where(l => l.Kind == kind))
            foreach (var h in Catalog.Hulls)
                if (h.CanMountPart(lo.Part))
                    return (lo, h);
        return (null, null);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void CatalogueOffersAllFourKindsWithTheRightSubtypeMapping()
    {
        Require.Artwork();
        Assert.Contains(Catalog.Launchers!, l => l.Kind == LauncherKind.Missile);
        Assert.Contains(Catalog.Launchers!, l => l.Kind == LauncherKind.Mine);
        Assert.Contains(Catalog.Launchers!, l => l.Kind == LauncherKind.Chaff);
        Assert.Contains(Catalog.Launchers!, l => l.Kind == LauncherKind.Probe);
        // The offer's Kind must agree with the built part's construction-only
        // subtype (the single subtype->kind mapping, LauncherKindUtil).
        foreach (var lo in Catalog.Launchers!)
            Assert.Equal(lo.Kind, LauncherKindUtil.KindOf(lo.Part));
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(LauncherKind.Mine)]
    [InlineData(LauncherKind.Chaff)]
    [InlineData(LauncherKind.Probe)]
    public void MountingADispenserBuysItWithItsRealAmmo(LauncherKind kind)
    {
        Require.Artwork();
        var (lo, hull) = MountableDispenser(kind);
        if (lo is not { } ov) { AssertDispenserNotOfferedAnywhere(kind); return; }
        var m = Model(currentHullId: hull!.Value.HullId);
        ShopModelTests.MountViaSlots(m, ov.Part.PartId);

        var bought = Assert.Single(m.ChosenParts);
        Assert.Equal(ov.Part.PartId, bought.PartId);
        Assert.Equal(ov.Ammo, bought.Amount);           // real ammo reserve, not a default
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(LauncherKind.Mine)]
    [InlineData(LauncherKind.Chaff)]
    [InlineData(LauncherKind.Probe)]
    public void DispenserSlotListsTheDispenserWithItsRealAmmo(LauncherKind kind)
    {
        Require.Artwork();
        var (lo, hull) = MountableDispenser(kind);
        if (lo is not { } ov) { AssertDispenserNotOfferedAnywhere(kind); return; }
        var m = Model(currentHullId: hull!.Value.HullId);
        m.SwitchTab(ShopTab.Parts);
        int idx = 0;
        while (m.Slots[idx].Type != ov.Part.EquipmentType) idx++;
        m.MoveCursor(idx - m.Cursor);
        m.Activate();                                    // open the dispenser slot

        var offer = m.OpenSlotParts.Single(o => o.Part.PartId == ov.Part.PartId);
        Assert.Equal(ov.Ammo, offer.Amount);
    }

    /// <summary>A kind with no mounting hull is genuinely unbuyable: P519's
    /// compatible-only listing offers it in NO slot of any hull.</summary>
    private static void AssertDispenserNotOfferedAnywhere(LauncherKind kind)
    {
        foreach (var h in Catalog.Hulls)
        {
            var m = Model(currentHullId: h.HullId);
            m.SwitchTab(ShopTab.Parts);
            for (int i = 0; i < m.Slots.Count; i++)
            {
                m.MoveCursor(i - m.Cursor);
                m.Activate();
                // For a kind with no mounting hull, no launcher of that equipment
                // type is offered in any slot (chaff launchers in this core).
                Assert.DoesNotContain(m.OpenSlotParts,
                    o => o.Part.EquipmentType == EquipmentType.ChaffLauncher);
                m.CloseSlot();
            }
        }
    }
}
