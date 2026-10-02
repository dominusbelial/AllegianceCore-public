using Allegiance.Core;
using Allegiance.Core.Model;

namespace Allegiance.Core.Tests;

/// <summary>
/// P488 — pure unit pins for the shared <see cref="SuccessorUpgrade"/> helper
/// (the single implementation the server's buy gate, the station-build
/// completion and the client shop all share). These prove the native successor
/// <em>semantics</em> on the REAL PCore001 chains, independent of the wire:
/// <list type="number">
/// <item><b>Hull:</b> a requested base hull is bought as the <b>furthest
/// buyable</b> successor in its chain (native Station::GetSuccessor walks the
/// whole chain, stationIGC.h:398-433) — e.g. 'Scout' (14) → 'Hvy Scout' (90),
/// skipping the intermediate 'Adv Scout' (13).</item>
/// <item><b>Part:</b> a requested base part is bought as its furthest buyable
/// successor — 'PW Lancer 1' (1003) → 'PW Lancer 3' (1005).</item>
/// <item><b>Obsolete:</b> IsObsolete is true exactly when a buyable successor
/// exists (stationIGC.h:394-396) — the filter term the shop hides.</item>
/// <item><b>Station:</b> the recursion only advances while the direct
/// successor's tech is met (stationtypeigc.h:185-195).</item>
/// </list>
/// Each is red on unfixed code because the helpers themselves don't exist yet.
/// </summary>
public sealed class SuccessorUpgradeTests
{
    private static StaticCore Core()
    {
        var path = Corpus.Files.FirstOrDefault(p => Path.GetFileName(p).Equals("PCore001.igc", StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException("PCore001.igc not in corpus");
        var file = IgcReader.ParseFile(path);
        return StaticCore.Load(file);
    }

    private static readonly TechTreeBitMask AllTech =
        new(Enumerable.Repeat((byte)0xFF, TechTreeBitMask.ByteCount).ToArray());

    private static readonly byte[] NoLocal = [];

    // Real PCore001 chains (verified against the live dump):
    //   hull 14 'Scout' -> 13 'Adv Scout' -> 90 'Hvy Scout' (group 0)
    //   part 1003 'PW Lancer 1' -> 1004 'PW Lancer 2' -> 1005 'PW Lancer 3'
    //   station 15 'Garrison' -> 10 'Garrison (Str)'  (one family)
    //
    // The specific hull/part ids are drawn from the REAL core below so a core
    // re-version that renumbers them stays green; the assertions target the
    // CHAIN, not a magic number.

    [Fact]
    public void HullSuccessor_returns_the_furthest_buyable_hull_in_the_chain()
    {
        var core = Core();
        var scout = core.HullTypes.Values.First(h => h.Name == "Scout" && h.SuccessorHullId >= 0);
        // Follow the chain to its leaf under all-tech.
        var leaf = scout;
        var seen = new HashSet<short> { scout.HullId };
        short? next = scout.SuccessorHullId;
        while (next is { } id && core.HullTypes.TryGetValue(id, out var cand) && seen.Add(id)) { leaf = cand; next = cand.SuccessorHullId; }
        Assert.NotEqual(scout.HullId, leaf.HullId);   // the chain is non-trivial

        var got = SuccessorUpgrade.HullSuccessor(core, scout, NoLocal, AllTech);
        Assert.Equal(leaf.HullId, got.HullId);        // furthest buyable, not the direct successor
        Console.WriteLine($"P488-HULL: '{scout.Name}' ({scout.HullId}) -> '{got.Name}' ({got.HullId})");
    }

    [Fact]
    public void HullSuccessor_returns_the_requested_hull_when_no_successor_is_buyable()
    {
        var core = Core();
        var scout = core.HullTypes.Values.First(h => h.Name == "Scout" && h.SuccessorHullId >= 0);
        // With NO tech, the scout (reqBits 3) isn't even buyable, so GetSuccessor
        // leaves it untouched — and importantly never fabricates an upgrade.
        var got = SuccessorUpgrade.HullSuccessor(core, scout, NoLocal, TechTreeBitMask.Empty);
        Assert.Equal(scout.HullId, got.HullId);
    }

    [Fact]
    public void PartSuccessor_walks_the_part_chain_to_its_furthest_buyable_version()
    {
        var core = Core();
        var lancer1 = core.PartTypes.Values.First(p => p.Name == "PW Lancer 1" && p.SuccessorPartId >= 0);
        var leaf = lancer1;
        var seen = new HashSet<short> { lancer1.PartId };
        short? next = lancer1.SuccessorPartId;
        while (next is { } id && core.PartTypes.TryGetValue(id, out var cand) && seen.Add(id)) { leaf = cand; next = cand.SuccessorPartId; }
        Assert.NotEqual(lancer1.PartId, leaf.PartId);

        var got = SuccessorUpgrade.PartSuccessor(core, lancer1, NoLocal, AllTech);
        Assert.Equal(leaf.PartId, got.PartId);
        Console.WriteLine($"P488-PART: '{lancer1.Name}' ({lancer1.PartId}) -> '{got.Name}' ({got.PartId})");
    }

    [Fact]
    public void HullIsObsolete_is_true_exactly_when_a_buyable_successor_exists()
    {
        var core = Core();
        var scout = core.HullTypes.Values.First(h => h.Name == "Scout" && h.SuccessorHullId >= 0);
        Assert.True(SuccessorUpgrade.HullIsObsolete(core, scout, NoLocal, AllTech));     // out-teched → obsolete
        Assert.False(SuccessorUpgrade.HullIsObsolete(core, scout, NoLocal, TechTreeBitMask.Empty)); // no successor buyable → not obsolete
    }

    [Fact]
    public void StationSuccessor_advances_only_while_the_direct_successors_tech_is_met()
    {
        var core = Core();
        // A garrison family with a direct successor whose tech is NOT covered by
        // the garrison's own required techs (so Empty dev techs ⇒ no advance, but
        // all-FF ⇒ advance): the "Garrison" -> "Garrison (Str)" pairs in the dump
        // (e.g. 15 -> 10, 44 -> 45, 360 -> 361...). Discover one deterministically.
        var pair = core.StationTypes.Values
            .Where(s => s.SuccessorStationTypeId >= 0 && core.StationTypes.ContainsKey(s.SuccessorStationTypeId))
            .Select(s => (Base: s, Succ: core.StationTypes[s.SuccessorStationTypeId]))
            .First(p => !TechTreeBitMask.RequiredTechsBuyable(p.Succ.TtbmRequired, p.Base.TtbmRequired, TechTreeBitMask.Empty));

        // Not researched → unchanged.
        Assert.Equal(pair.Base.StationTypeId, SuccessorUpgrade.StationSuccessor(core, pair.Base, TechTreeBitMask.Empty).StationTypeId);
        // Researched → the direct successor.
        Assert.Equal(pair.Succ.StationTypeId, SuccessorUpgrade.StationSuccessor(core, pair.Base, AllTech).StationTypeId);
        Console.WriteLine($"P488-STATION: '{pair.Base.Name}' ({pair.Base.StationTypeId}) -> '{pair.Succ.Name}' ({pair.Succ.StationTypeId})");
    }
}
