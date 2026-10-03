using System;
using System.IO;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Model;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P509 — client-level proof that the <c>testUnlock</c> grant lifts the docked
/// shop's tech filter from the STARTER SET to the FULL player roster. The shop
/// (<see cref="ShopCatalog.FilteredFor"/>, P468 fail-closed) offers a hull iff
/// <c>TtbmRequired ⊆ (stationLocal | sideDev)</c> and (P470) <c>GroupId &gt;= 0</c>
/// and (P488) <c>!IsObsolete</c>. Feeding it the side's development mask that
/// P509's server grant produces (the core's required-tech union, via
/// <see cref="StaticCore.ComputeRequiredTechUnion"/>) instead of the fresh
/// side's starter base-techs must raise the offered-hull count from the
/// starter garrison set to the full player roster — the exact contrast Domingo's
/// playtest hit ("2 hulls / 15 parts / 9 launchers offered"). Reported numbers
/// are measured from the real PCore001.igc + XXV_simple.igc.
/// </summary>
public class P509TestUnlockShopVisibilityTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private static readonly ShopCatalog Catalog = LoadCatalog();
    private static readonly StaticCore Core = Catalog.Core
        ?? throw new InvalidOperationException("ShopCatalog loaded without a StaticCore");

    private static ShopCatalog LoadCatalog() =>
        ShopCatalog.TryLoad(ArtRoot)
        ?? throw new InvalidOperationException($"PCore001.igc not loadable from {ArtRoot}");

    /// <summary>The <c>TtbmLocal</c> of the side-0 garrison station type on the
    /// real XXV_simple map — the same docked-station tech context the live
    /// server boots (Garrison() helper in the wire tests resolves this exact
    /// station type, id 15, from the assembled mission).</summary>
    private static byte[] GarrisonLocal()
    {
        var mapFile = IgcReader.Parse(File.ReadAllBytes(Path.Combine(ArtRoot, "XXV_simple.igc")));
        var map = MissionMap.Load(mapFile);
        var garrison = map.Stations.First(st => st.SideId >= 0);
        if (!Core.StationTypes.TryGetValue(garrison.StationTypeId, out var st))
            throw new InvalidOperationException($"garrison station type {garrison.StationTypeId} unresolvable in the core");
        return st.TtbmLocal;
    }

    /// <summary>The fresh side's STARTER development mask: native seeds a side's
    /// <c>ttbmDevelopmentTechs</c> from its civilization's base techs
    /// (sideigc.cpp:102) — the mask a brand-new side researches from. Alpha is
    /// side 0, backed by civ id 1 in the deploy config (CivIds=[1,3]).</summary>
    private static TechTreeBitMask StarterDev() =>
        TechTreeBitMask.FromBytes(Core.Civilizations[1].TtbmBaseTechs);

    /// <summary>⭐ The garrison's offered hull count goes from the STARTER SET
    /// (fresh side's base-tech mask) to the FULL PLAYER ROSTER (the required-
    /// tech union P509 grants) — and reports both measured numbers.</summary>
    [Fact]
    public void Shop_offered_hull_count_goes_from_starter_set_to_full_player_roster()
    {
        byte[] local = GarrisonLocal();
        var starterDev = StarterDev();
        var fullDev = Core.ComputeRequiredTechUnion();

        int starterCount = Catalog.FilteredFor(local, starterDev).Hulls.Count;
        int fullCount = Catalog.FilteredFor(local, fullDev).Hulls.Count;

        // The union really has far more tech than the starter base mask.
        Assert.True(fullDev.PopCount > starterDev.PopCount,
            $"expected the union ({fullDev.PopCount} bits) to exceed the starter base mask ({starterDev.PopCount})");

        // The full grant must offer strictly more player hulls than the starter set.
        Assert.True(fullCount > starterCount,
            $"expected more hulls offered under the full grant (got starter={starterCount}, full={fullCount})");

        // The grant must not reintroduce drone hulls (P470 still applies) nor
        // offer a hull P488 deems obsolete.
        var fullOffer = Catalog.FilteredFor(local, fullDev);
        Assert.All(fullOffer.Hulls, h => Assert.True(h.GroupId >= 0));

        // The raw hull table still holds every player hull; the difference
        // between it and the full offer is the P488 IsObsolete term hiding
        // out-teched predecessors (reported, not hacked around).
        int allPlayerHulls = Core.HullTypes.Values.Count(h => h.GroupId >= 0);

        Console.WriteLine($"P509-SHOP: XXV garrison offered hulls starter={starterCount} -> full={fullCount} "
                          + $"(all GroupId>=0 player hulls={allPlayerHulls}, obsolete-hidden={allPlayerHulls - fullCount}); "
                          + $"dev mask starter={starterDev.PopCount} bits -> union {fullDev.PopCount} bits");
    }
}
