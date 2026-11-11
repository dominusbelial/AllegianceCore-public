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
/// P996 — the HANGAR half of the lifepod respawn loop, on the shop/loadout model
/// with the real PCore001 core. A player berthed in a lifepod hull has a hull the
/// station does not offer (pod hulls are GroupID −1), so before P996 the hangar
/// showed the pod and its Launch button sent <c>BUY_LOADOUT(pod hull, fLaunch)</c>
/// — the exact message the rig server accepted 6 ms before killing the ship of a
/// stale oxygen deadline.
///
/// <para>Native restores instead of offering the pod: the <c>LOADOUT_CHANGE</c>
/// handler's <c>pod hull &amp;&amp; GetStation()</c> branch calls
/// <c>RestoreLoadout(station)</c> (<c>appmsg.cpp:2022-2023</c> →
/// <c>clintlib.h:1876-1886</c>), which replays the cached loadout head
/// (<c>m_loadouts.first()</c>), upgrades the hull to
/// <c>pstation-&gt;GetSuccessor(hull)</c> (<c>clintlib.h:1806-1812</c>,
/// <c>stationIGC.h:399-440</c>), clones the parts that hull can mount
/// (<c>clintlib.h:1813-1848</c>), fills a hull upgrade's empty slots with the
/// hull's preferred defaults (<c>clintlib.h:1620-1624</c> → <c>TryToBuyParts</c>,
/// <c>:1341-1368</c>), and falls back to the first affordable buyable hull with
/// its default loadout when the saved hull is unavailable
/// (<c>clintlib.h:1850-1868</c>). <see cref="ShopModel.RestoreSavedLoadout"/>
/// is that rule; these tests pin it, including the request the Launch button then
/// carries (<see cref="ShopModel.EffectiveHullId"/> / <see cref="ShopModel.ChosenParts"/>).
/// </summary>
public class P996LifepodHangarRestoreTests
{
    private static readonly string ArtRoot = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
    private static readonly ShopCatalog Raw = ShopCatalog.TryLoad(ArtRoot)
        ?? throw new InvalidOperationException($"PCore001.igc not loadable from {ArtRoot}");

    // Every tech bit set: the P284/P441 containment always passes, so the ONLY
    // hulls kept out of the offer are GroupID < 0 (P470) and obsolete hulls.
    private static readonly byte[] AllTechStation = Enumerable.Repeat((byte)0xFF, TechTreeBitMask.ByteCount).ToArray();
    private static readonly TechTreeBitMask AllTechDev =
        TechTreeBitMask.FromBytes(Enumerable.Repeat((byte)0xFF, TechTreeBitMask.ByteCount).ToArray());
    private static readonly ShopCatalog Offer = Raw.FilteredFor(AllTechStation, AllTechDev);

    private static DataHullTypeIGC Lifepod() =>
        Raw.Hulls.First(h => h.HasCapability(DataHullTypeIGC.HabmLifepod) && h.GroupId < 0);

    /// <summary>A real player hull the station offers AND whose successor chain
    /// leads nowhere else buyable — so native's successor upgrade cannot move it
    /// and "restore the last hull" is exactly that hull.</summary>
    private static DataHullTypeIGC TerminalHull() =>
        Offer.Hulls.First(h => h.SuccessorHullId < 0 || Offer.Hulls.All(x => x.HullId != h.SuccessorHullId));

    private static DataPartTypeIGC WeaponFor(DataHullTypeIGC hull) =>
        Offer.Parts.First(p => p.EquipmentType == EquipmentType.Weapon && hull.CanMountPart(p, 0));

    /// <summary>⭐ The hangar opened on a berthed pod selects the player's LAST
    /// real hull with its saved parts, so the Launch request carries that hull
    /// (not the pod).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Hangar_on_a_berthed_lifepod_selects_the_last_real_loadout()
    {
        Require.Artwork();
        var hull = TerminalHull();
        var gun = WeaponFor(hull);
        var pod = Lifepod();
        var saved = new ExpandedPartData(gun.PartId, 40, 0, ExpandedPartData.FullyMounted);

        // Exactly how Game1 builds the docked shop: the CURRENT (pod) hull.
        var shop = new ShopModel(Offer, pod.HullId, Array.Empty<ExpandedPartData>());
        Assert.Equal(pod.HullId, shop.EffectiveHullId);
        Assert.Null(shop.EffectiveHull);   // the pod is not a shop hull

        Assert.True(shop.RestoreSavedLoadout(hull.HullId, [saved]));

        Assert.Equal(hull.HullId, shop.EffectiveHullId);
        Assert.Equal(hull.HullId, shop.ChosenHullId);
        Assert.NotNull(shop.EffectiveHull);
        Assert.True(shop.WillSwapHull, "the berthed pod hull is replaced by the restored hull");
        Assert.Contains(shop.ChosenParts, p => p.PartId == gun.PartId);
    }

    /// <summary>⭐ A saved hull the station has out-teched is bought as its
    /// furthest buyable successor (native <c>pstation-&gt;GetSuccessor</c>,
    /// stationIGC.h:399-440), and the upgraded hull's empty slots are filled with
    /// its default parts (or, when the successor mounts the saved parts, those).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Saved_obsolete_hull_is_restored_as_its_buyable_successor()
    {
        Require.Artwork();
        // The first REAL hull that the offer does not list (out-teched) but whose
        // successor chain does lead to an offered hull.
        var savedHull = Raw.Hulls.First(h => h.GroupId >= 0
            && Offer.Hulls.All(o => o.HullId != h.HullId)
            && h.SuccessorHullId >= 0);
        // Native's walk: keep the furthest successor the station can buy.
        short cur = savedHull.HullId;
        short? expected = null;
        for (int guard = 0; guard < 64; guard++)
        {
            if (!Raw.Core!.HullTypes.TryGetValue(cur, out var rec) || rec.SuccessorHullId < 0 || rec.SuccessorHullId == cur) break;
            cur = rec.SuccessorHullId;
            if (Offer.Hulls.Any(o => o.HullId == cur)) expected = cur;
        }
        Assert.NotNull(expected);

        var shop = new ShopModel(Offer, Lifepod().HullId, Array.Empty<ExpandedPartData>());
        Assert.True(shop.RestoreSavedLoadout(savedHull.HullId, []));

        Assert.Equal(expected!.Value, shop.EffectiveHullId);
        Assert.NotEmpty(shop.ChosenParts);   // the upgraded hull's default loadout
        Assert.NotNull(shop.EffectiveHull);  // a real, offered hull (never the pod)
    }

    /// <summary>⭐ No usable saved hull (never owned one, or it is not buyable at
    /// all): native's fallback is the first hull in core order the station can buy
    /// within budget, with its default loadout (clintlib.h:1850-1868).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Unbuyable_saved_hull_falls_back_to_the_first_affordable_hull()
    {
        Require.Artwork();
        // A drone-only hull (GroupID < 0, never buyable for a player).
        var drone = Raw.Hulls.First(h => h.GroupId < 0 && !h.HasCapability(DataHullTypeIGC.HabmLifepod));

        var shop = new ShopModel(Offer, Lifepod().HullId, Array.Empty<ExpandedPartData>());
        Assert.True(shop.RestoreSavedLoadout(drone.HullId, []));

        Assert.Equal(Offer.Hulls[0].HullId, shop.EffectiveHullId);
        Assert.NotEmpty(shop.ChosenParts);
    }

    /// <summary>Native checks the saved loadout against the wallet
    /// (<c>pcl-&gt;pht-&gt;GetPrice() &lt;= budget</c>, clintlib.h:1806). A saved hull
    /// the player cannot afford now falls back to an affordable one.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Unaffordable_saved_hull_falls_back_to_an_affordable_one()
    {
        Require.Artwork();
        var pricey = Offer.Hulls.First(h => h.Price > 0);   // offered here, but not with $0

        var shop = new ShopModel(Offer, Lifepod().HullId, Array.Empty<ExpandedPartData>(), money: 0);
        Assert.True(shop.RestoreSavedLoadout(pricey.HullId, []));

        // Budget 0: the fallback must land on a FREE hull the station offers.
        Assert.Equal(0, Offer.Hulls.First(h => h.HullId == shop.EffectiveHullId).Price);
    }
}
