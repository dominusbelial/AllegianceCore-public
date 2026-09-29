using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;

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
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

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

    [Fact]
    public void CatalogueCarriesRealMissileLaunchersWithRealNamesAndAmmo()
    {
        Assert.NotNull(Catalog.Launchers);
        Assert.NotEmpty(Catalog.Launchers!);
        foreach (var lo in Catalog.Launchers!)
        {
            // Every offered launcher is a REAL core.Launchers record (keyed by
            // PartId) whose expendable is a REAL missile — never a fabricated
            // launcher, never a chaff/mine/probe dispenser (those fail the P433
            // TryBuildMissileLauncher gate and are rightly absent).
            Assert.True(Core.Launchers.ContainsKey(lo.Part.PartId),
                $"offered launcher id {lo.Part.PartId} must be a real core.Launchers record");
            Assert.True(Core.MissileTypes.ContainsKey(Core.Launchers[lo.Part.PartId].ExpendabletypeId),
                $"offered launcher id {lo.Part.PartId} must be a genuine missile launcher (not a dispenser)");
            // Sensible row data: a real name and a real, positive ammo count
            // (DataLauncherTypeIGC.Amount), the exact ammo the launcher entry
            // carries on the wire.
            Assert.False(string.IsNullOrWhiteSpace(lo.Part.Name), "launcher must carry its real name");
            Assert.True(lo.Ammo > 0, $"launcher {lo.Part.Name} must carry real ammo (>0)");
            Assert.Equal(lo.Ammo, Core.Launchers[lo.Part.PartId].Amount);
        }
    }

    [Fact]
    public void LauncherPartIdsAreDistinctFromRegularPartIds()
    {
        // A launcher PartId must not collide with a core.Parts PartId, else the
        // part/launcher rows would toggle the same id ambiguously. The two
        // tables are keyed separately; assert the offer set stays disjoint.
        var partIds = new HashSet<short>(Catalog.Parts.Select(p => p.PartId));
        foreach (var lo in Catalog.Launchers!)
            Assert.DoesNotContain(lo.Part.PartId, partIds);
    }

    [Fact]
    public void MovingOntoALauncherRowAndActivatingBuysItWithItsRealAmmo()
    {
        var lo = Catalog.Launchers!.OrderBy(l => l.Part.PartId).First();
        var m = Model();
        m.MoveCursor(FlatIndexOfLauncher(lo.Part.PartId) - m.Cursor);

        Assert.True(m.CursorOnLauncher, "cursor should rest on a launcher row");
        Assert.Equal(lo.Part.PartId, m.CursorLauncher!.Value.Part.PartId);
        Assert.False(m.CursorOnHull);
        Assert.Null(m.CursorPart);   // a launcher row is not a part row

        m.Activate(); // toggle ON
        var bought = Assert.Single(m.ChosenParts);
        Assert.Equal(lo.Part.PartId, bought.PartId);
        Assert.Equal(lo.Ammo, bought.Amount);   // real ammo reserve, not a default

        m.Activate(); // toggle OFF (same key, exactly like any other part)
        Assert.Empty(m.ChosenParts);
    }

    [Fact]
    public void BoughtLauncherRendersAsASensibleLauncherRow()
    {
        var lo = Catalog.Launchers!.OrderBy(l => l.Part.PartId).First();
        var m = Model();
        var rows = ShopLayout.Build(m);
        // The shop list must contain a [LAUNCHER] row naming the real launcher
        // with its real ammo count (the native shop has no launcher price datum,
        // so the ammo count reads in the price column rather than a fake $0).
        var launcherRows = rows.Where(r =>
            r.Text.Contains("[LAUNCHER]", StringComparison.OrdinalIgnoreCase)
            && r.Text.Contains(lo.Part.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(launcherRows);
        Assert.Contains(lo.Ammo.ToString(), launcherRows[0].Text);
    }

    [Fact]
    public void FilteredForAppliesTheSameTechGateToLaunchers()
    {
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

        // Degraded gate (unknown tech) offers everything, unchanged behaviour.
        var degraded = Catalog.FilteredFor(null, null);
        Assert.Equal(Catalog.Launchers!.Count, degraded.Launchers!.Count);
    }

    [Fact]
    public void NonMountingLauncherIsFlaggedForTheChosenHullLikeAnyOtherPart()
    {
        var (lo, yesHull, noHull) = FitNonFitTriple();
        var m = Model(currentHullId: yesHull.HullId);
        // Pick the OTHER (non-mounting) hull so a real swap occurs, then buy the
        // launcher: the same P335 fit gate that flags a non-fitting weapon must
        // flag this launcher (via LookupPart resolving it + CanMountPart).
        m.MoveCursor(Catalog.Hulls.ToList().FindIndex(h => h.HullId == noHull.HullId) - m.Cursor);
        m.Activate();                       // choose noHull
        m.MoveCursor(FlatIndexOfLauncher(lo.Part.PartId) - m.Cursor);
        m.Activate();                       // toggle launcher ON
        Assert.Contains(m.ChosenPartsThatDontFitEffectiveHull, p => p.PartId == lo.Part.PartId);
    }
}
