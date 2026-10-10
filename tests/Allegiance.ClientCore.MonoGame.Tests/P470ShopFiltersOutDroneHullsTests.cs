using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P470 — the client shop's hull OFFER (the <see cref="ShopCatalog.FilteredFor"/>
/// result the docked hangar lists) excludes drone-only hulls the way native's
/// hangar does: native lists a hull only when its (signed) group id is
/// non-negative (load.cpp:1603-1606, clintlib.h:1858), and the real PCore001
/// constructor/miner/lifepod/recon/rescue drone hulls all carry GroupId = -1
/// (raw 0xFF, decoded as a signed byte).
///
/// The gate is applied to the shop OFFER only, NOT to <see cref="ShopCatalog.Hulls"/>
/// wholesale — the raw table is still read for the current ship / lifepod
/// oxygen (P377) / hull-model resolution, so it intentionally still contains
/// the pod. We assert both sides of that split here.
/// </summary>
public class P470ShopFiltersOutDroneHullsTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static readonly ShopCatalog Catalog = LoadCatalog();
    private static ShopCatalog LoadCatalog() =>
        ShopCatalog.TryLoad(ArtRoot)
        ?? throw new InvalidOperationException($"PCore001.igc not loadable from {ArtRoot}");

    // Maximally-permissive masks: every tech bit set, so the P284/P441 tech
    // containment "need ⊆ (stationLocal | dev)" is always satisfied. Under
    // these the ONLY thing that can keep a hull out of the shop offer is the
    // P470 GroupId >= 0 rule — a drone hull is absent precisely because of it,
    // and every player hull is present (so the positive assertions are real).
    private static readonly byte[] AllTechStation = Enumerable.Repeat((byte)0xFF, TechTreeBitMask.ByteCount).ToArray();
    private static readonly TechTreeBitMask AllTechDev =
        TechTreeBitMask.FromBytes(Enumerable.Repeat((byte)0xFF, TechTreeBitMask.ByteCount).ToArray());

    /// <summary>The first real drone-only hull (GroupId &lt; 0) in the core —
    /// proves the core genuinely contains them, so the offer-filter test is not
    /// vacuous. Picked from the RAW table (which still carries them).</summary>
    private static DataHullTypeIGC ADroneHull() =>
        Catalog.Hulls.First(h => h.GroupId < 0);

    /// <summary>⭐ A drone hull (constructor/miner — GroupId &lt; 0) is present
    /// in the raw core table but is NOT listed in the shop's hull offer
    /// (FilteredFor), with every tech unlocked (the only gate left is P470's
    /// GroupId rule).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Shop_offer_does_not_list_any_drone_hull()
    {
        Require.Artwork();
        // Sanity: the raw core really does carry drone-only hulls (not vacuous).
        var drone = ADroneHull();
        Assert.True(drone.GroupId < 0, $"expected a drone hull with negative group, got {drone.Name} group {drone.GroupId}");
        Assert.Contains(Catalog.Hulls, h => h.GroupId < 0);

        var offer = Catalog.FilteredFor(AllTechStation, AllTechDev);

        Assert.DoesNotContain(offer.Hulls, h => h.GroupId < 0);
        // The drone hull by id is not offered.
        Assert.DoesNotContain(offer.Hulls, h => h.HullId == drone.HullId);
        Console.WriteLine($"P470-SHOP: drone hull '{drone.Name}' (HullId {drone.HullId}, group {drone.GroupId}) is in the core table but absent from the shop offer");
    }

    /// <summary>⭐ The shop offer still lists a normal player hull (GroupId >= 0),
    /// so the GroupId gate is not over-broad.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Shop_offer_still_lists_normal_player_hulls()
    {
        Require.Artwork();
        var offer = Catalog.FilteredFor(AllTechStation, AllTechDev);

        Assert.NotEmpty(offer.Hulls);
        Assert.All(offer.Hulls, h => Assert.True(h.GroupId >= 0));
        Assert.Contains(offer.Hulls, h => h.GroupId >= 0);
        Console.WriteLine($"P470-SHOP: shop offer lists {offer.Hulls.Count} player hull(s), all GroupId >= 0");
    }

    /// <summary>⭐ The RAW table still contains the lifepod (GroupId &lt; 0), so
    /// the current-ship / lifepod-oxygen (P377) / model-resolution paths are
    /// unaffected by the shop-offer filter.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Raw_table_still_contains_the_lifepod_for_current_ship_paths()
    {
        Require.Artwork();
        var lifepod = Catalog.Hulls.FirstOrDefault(h => h.HasCapability(DataHullTypeIGC.HabmLifepod));
        Assert.False(lifepod.HullId == 0, "core must contain a lifepod hull");
        Assert.True(lifepod.GroupId < 0);
        Console.WriteLine($"P470-SHOP: raw table retains lifepod '{lifepod.Name}' (group {lifepod.GroupId}) for current-ship paths");
    }
}
