using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;
using Allegiance.Tools.McpSpawner;
using Allegiance.TestInfra;
using Xunit;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P779b-1 — the pod autopilot target rule: native's eject search
/// <c>FindTarget(pShip, c_ttStation | c_ttFriendly | c_ttAnyCluster | c_ttProbe,
/// …, c_sabmRescue | c_sabmRescueAny | c_sabmLand)</c>
/// (clintlib/appmsg.cpp:230-237) gives the pod a friendly RESCUE-CAPABLE PROBE
/// as a candidate next to the restart-capable friendly stations, ordered by the
/// P560 route rule (fewest warp hops, then Euclidean distance). These are pure
/// data tests of <see cref="BotManager.PickPodTarget"/>; P560's station-only
/// <see cref="BotManager.PickPodHomeStation"/> cases must be unchanged.
/// </summary>
public class P779b1PodTargetTests
{
    private static (short Id, short Side, short Sector, Vector3 Pos, short TypeId) T(
        short id, short side, short sector, float x, float y, float z, short tid) =>
        (id, side, sector, new Vector3(x, y, z), tid);

    private static bool Restart(short tid) => tid == 1;     // c_sabmRestart
    private static bool NotRestart(short tid) => tid == 2;

    /// <summary>The real PCore001 rescue probe ids: 19 Rescue (0x2000), 51
    /// Beacon (0x2000), 2 Sanctuary (0x4000); everything else 0 (igc.h:781-782).</summary>
    private static short Eabm(short probeTypeId) => probeTypeId switch
    {
        19 or 51 => DataProbeTypeIGC.EabmRescue,
        2 => DataProbeTypeIGC.EabmRescueAny,
        _ => 0,
    };

    private static Func<short, short, bool> OwnOnly => (a, b) => a == b;

    private static Func<short, short, int> Hops(params (short A, short B)[] edges)
    {
        var adj = edges.SelectMany(e => new[] { e, (e.Item2, e.Item1) })
                       .GroupBy(e => e.Item1)
                       .ToDictionary(g => g.Key, g => g.Select(x => x.Item2).Distinct().ToList());
        return (from, to) =>
        {
            if (from == to) return 0;
            var dist = new Dictionary<short, int> { [from] = 0 };
            var q = new Queue<short>(); q.Enqueue(from);
            while (q.Count > 0)
            {
                var c = q.Dequeue();
                if (!adj.TryGetValue(c, out var nb)) continue;
                foreach (var n in nb)
                    if (!dist.ContainsKey(n)) { dist[n] = dist[c] + 1; q.Enqueue(n); }
            }
            return dist.TryGetValue(to, out var d) ? d : -1;
        };
    }

    [Fact]
    public void Own_rescue_probe_in_pod_sector_beats_a_station_two_hops_away()
    {
        // Pod in sector 1. Rescue probe (type 19, own side) in its own sector; the
        // only restart-capable station is two warp hops away.
        var stations = new[] { T(10, 0, 3, 5000, 0, 0, 1) };
        var probes = new[] { T(77, 0, 1, 900, 0, 0, 19) };
        var pick = BotManager.PickPodTarget(stations, probes, 0, 1, Vector3.Zero,
            Restart, OwnOnly, Eabm, Hops((1, 2), (2, 3)));
        Assert.NotNull(pick);
        Assert.True(pick!.Value.IsProbe);
        Assert.Equal((short)77, pick.Value.ObjectId);
        Assert.Equal((short)1, pick.Value.SectorId);
        Assert.Equal(0, pick.Value.Hops);
    }

    [Fact]
    public void Enemy_side_rescue_probe_is_ignored()
    {
        // The enemy probe is in the pod's own sector (0 hops) but is not a
        // c_ttFriendly target; the own-side station two hops away wins.
        var stations = new[] { T(10, 0, 3, 5000, 0, 0, 1) };
        var probes = new[] { T(77, 1, 1, 10, 0, 0, 19) };
        var pick = BotManager.PickPodTarget(stations, probes, 0, 1, Vector3.Zero,
            Restart, OwnOnly, Eabm, Hops((1, 2), (2, 3)));
        Assert.NotNull(pick);
        Assert.False(pick!.Value.IsProbe);
        Assert.Equal((short)10, pick.Value.ObjectId);
    }

    [Fact]
    public void Own_side_sanctuary_probe_is_a_candidate()
    {
        // Type 2 "Sanctuary" carries c_eabmRescueAny (0x4000); on the pod's own
        // side it is friendly AND rescue-capable, so it is a candidate.
        var stations = new[] { T(10, 0, 3, 5000, 0, 0, 1) };
        var probes = new[] { T(88, 0, 1, 400, 0, 0, 2) };
        var pick = BotManager.PickPodTarget(stations, probes, 0, 1, Vector3.Zero,
            Restart, OwnOnly, Eabm, Hops((1, 2), (2, 3)));
        Assert.NotNull(pick);
        Assert.True(pick!.Value.IsProbe);
        Assert.Equal((short)88, pick.Value.ObjectId);
    }

    [Fact]
    public void Allied_rescue_probe_is_a_candidate()
    {
        // Native's c_ttFriendly includes allies; an allied (side 1) Rescue probe
        // in the pod's sector is a candidate. An enemy RescueAny probe is not
        // (see the enemy test) — c_ttFriendly is still required.
        var stations = new[] { T(10, 0, 3, 5000, 0, 0, 1) };
        var probes = new[] { T(77, 1, 1, 700, 0, 0, 19) };
        var pick = BotManager.PickPodTarget(stations, probes, 0, 1, Vector3.Zero,
            Restart, (a, b) => a == b || (a == 0 && b == 1), Eabm, Hops((1, 2), (2, 3)));
        Assert.NotNull(pick);
        Assert.True(pick!.Value.IsProbe);
        Assert.Equal((short)77, pick.Value.ObjectId);
    }

    [Fact]
    public void Probe_that_cannot_rescue_is_ignored()
    {
        // A scanner probe (eabm 0, the "not a rescue device" case) in the pod's
        // own sector is not a target; the station stays the pick.
        var stations = new[] { T(10, 0, 3, 5000, 0, 0, 1) };
        var probes = new[] { T(99, 0, 1, 10, 0, 0, 22) };
        var pick = BotManager.PickPodTarget(stations, probes, 0, 1, Vector3.Zero,
            Restart, OwnOnly, Eabm, Hops((1, 2), (2, 3)));
        Assert.NotNull(pick);
        Assert.False(pick!.Value.IsProbe);
        Assert.Equal((short)10, pick.Value.ObjectId);
    }

    [Fact]
    public void Unreachable_probe_is_skipped_like_an_unreachable_station()
    {
        var stations = new[] { T(10, 0, 3, 5000, 0, 0, 1) };
        var probes = new[] { T(77, 0, 99, 1, 0, 0, 19) };      // disconnected pocket
        var pick = BotManager.PickPodTarget(stations, probes, 0, 1, Vector3.Zero,
            Restart, OwnOnly, Eabm, Hops((1, 2), (2, 3)));
        Assert.NotNull(pick);
        Assert.False(pick!.Value.IsProbe);
        Assert.Equal((short)10, pick.Value.ObjectId);
    }

    [Fact]
    public void Fewer_hops_still_beats_euclidean_distance_across_kinds()
    {
        // A station in the pod's own sector (0 hops, far away) beats a probe one
        // hop away that is Euclideantly nearer: the P560 route order is kept.
        var stations = new[] { T(10, 0, 1, 9000, 0, 0, 1) };
        var probes = new[] { T(77, 0, 2, 10, 0, 0, 19) };
        var pick = BotManager.PickPodTarget(stations, probes, 0, 1, Vector3.Zero,
            Restart, OwnOnly, Eabm, Hops((1, 2)));
        Assert.NotNull(pick);
        Assert.False(pick!.Value.IsProbe);
        Assert.Equal((short)10, pick.Value.ObjectId);
        Assert.Equal(0, pick.Value.Hops);
    }

    [Fact]
    public void With_no_probe_the_pick_is_the_P560_station_pick()
    {
        // The P560 cases, unchanged: unreachable pocket skipped, nearest restart
        // station by hops then Euclidean, enemy and non-restart stations ignored.
        var cases = new[]
        {
            (Stations: new[] { T(10, 0, 99, 1000, 0, 0, 1), T(20, 0, 3, 5000, 0, 0, 1) }, Edges: new (short, short)[] { (1, 2), (2, 3) }),
            (Stations: new[] { T(10, 0, 1, 10, 0, 0, 2), T(20, 0, 3, 5000, 0, 0, 1) }, Edges: new (short, short)[] { (1, 2), (2, 3) }),
            (Stations: new[] { T(10, 0, 1, 10, 0, 0, 1), T(20, 0, 3, 1, 0, 0, 1) }, Edges: new (short, short)[] { (1, 3) }),
            (Stations: new[] { T(10, 1, 1, 1, 0, 0, 1), T(20, 0, 3, 5000, 0, 0, 1) }, Edges: new (short, short)[] { (1, 2), (2, 3) }),
            (Stations: new[] { T(10, 0, 3, 5000, 0, 0, 1), T(20, 0, 3, 100, 0, 0, 1) }, Edges: new (short, short)[] { (1, 3) }),
        };
        foreach (var (stations, edges) in cases)
        {
            var hops = Hops(edges);
            var home = BotManager.PickPodHomeStation(stations, 0, 1, Vector3.Zero, Restart, hops);
            var target = BotManager.PickPodTarget(stations, [], 0, 1, Vector3.Zero,
                Restart, OwnOnly, Eabm, hops);
            Assert.NotNull(home);
            Assert.NotNull(target);
            Assert.False(target!.Value.IsProbe);
            Assert.Equal(home!.Value.StationId, target.Value.ObjectId);
            Assert.Equal(home.Value.SectorId, target.Value.SectorId);
            Assert.Equal(home.Value.Center, target.Value.Center);
        }

        // No reachable restart station at all: both return null.
        var none = new[] { T(10, 0, 99, 1000, 0, 0, 1), T(11, 0, 1, 5, 0, 0, 2) };
        Assert.Null(BotManager.PickPodHomeStation(none, 0, 1, Vector3.Zero, Restart, Hops((1, 3))));
        Assert.Null(BotManager.PickPodTarget(none, [], 0, 1, Vector3.Zero, Restart, OwnOnly, Eabm, Hops((1, 3))));
    }
}

/// <summary>
/// P779b-1 — the medevac scout (design, not parity): per side at most one bot,
/// the lowest stable bot id among that side's scout-role bots whose civ's techs
/// buy a rescue-capable probe launcher. Decided from core data only (never a civ
/// name/id). Iron Coalition (25) can buy Rescue (launcher part 19); Ga'Taraan
/// (3) cannot buy any rescue probe.
/// </summary>
public class P779b1MedevacTests
{
    private static TechTreeBitMask Techs(StaticCore core, short civ)
        => new ResearchState(new(core, civ)).Techs;

    [SkippableFact]
    public void Rescue_launcher_is_chosen_from_civ_techs_and_prefers_lifespan_then_hp()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        // IC: "Rescue" (expendable 19), 1200 s, 25 hp — the real PCore001 fact P780 pinned.
        Assert.Equal((short?)19, TechLoadoutSelector.PickRescueProbeLauncher(core, Techs(core, 25)));
        // GT: no development or station grants a rescue bit, so no launcher.
        Assert.Null(TechLoadoutSelector.PickRescueProbeLauncher(core, Techs(core, 3)));
        // Belters: "Sanctuary" (expendable 2), 1800 s / 25 hp — longer lifespan wins
        // over Rescue/Beacon's 1200 s even though Beacon is cheaper.
        Assert.Equal((short?)2, TechLoadoutSelector.PickRescueProbeLauncher(core, Techs(core, 101)));
        // Nothing without the base tech bit either.
        Assert.Null(TechLoadoutSelector.PickRescueProbeLauncher(core, TechTreeBitMask.Empty));
    }

    [SkippableFact]
    public void Medevac_scout_loadout_carries_the_rescue_launcher_on_that_bot_only()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        var state = new ResearchState(new(core, 25));
        var shop = new BotManager.LoadoutShop(25, state.Tree.Civ.InitialStationTypeId, state.Techs, 1_000_000);

        var medevac = BotManager.PickTechLoadout(shop, Showcase.HullRole.Scout, medevacScout: true)!;
        Assert.Contains(medevac.Parts, p => core.TryBuildExpendableLauncher(p.PartId, out var part)
            && part.Type is DataPartTypeIGC.ProbeLauncher launcher
            && core.ProbeTypes[launcher.ExpendableTypeId].EabmCapabilities != 0
            && p.PartId == 19);
        Assert.Contains("role=Scout", medevac.Reason);

        // Every other scout keeps today's exclusion: scanner probe, no rescue bits.
        var ordinary = BotManager.PickTechLoadout(shop, Showcase.HullRole.Scout)!;
        Assert.DoesNotContain(ordinary.Parts, p => core.TryBuildExpendableLauncher(p.PartId, out var part)
            && part.Type is DataPartTypeIGC.ProbeLauncher launcher
            && (core.ProbeTypes[launcher.ExpendableTypeId].EabmCapabilities & 0x6000) != 0);
        Assert.Contains(ordinary.Parts, p => core.TryBuildExpendableLauncher(p.PartId, out var part)
            && part.Type is DataPartTypeIGC.ProbeLauncher launcher
            && core.ProbeTypes[launcher.ExpendableTypeId].ScannerRange > 0);

        // GT (civ 3) cannot buy one: the medevac flag changes nothing there.
        var gt = new ResearchState(new(core, 3));
        var gtShop = new BotManager.LoadoutShop(3, gt.Tree.Civ.InitialStationTypeId, gt.Techs, 1_000_000);
        var gtScout = BotManager.PickTechLoadout(gtShop, Showcase.HullRole.Scout, medevacScout: true);
        if (gtScout is not null)
            Assert.DoesNotContain(gtScout.Parts, p => core.TryBuildExpendableLauncher(p.PartId, out var part)
                && part.Type is DataPartTypeIGC.ProbeLauncher launcher
                && (core.ProbeTypes[launcher.ExpendableTypeId].EabmCapabilities & 0x6000) != 0);
    }

    [Fact]
    public void Lowest_id_capable_scout_per_side_is_the_only_medevac_bot_and_survives_a_respawn()
    {
        var roster = new List<BotManager.MedevacCandidate>
        {
            new(7, 0, "fighter", true),      // not a scout
            new(5, 0, "scout", true),        // lowest capable scout on side 0 -> chosen
            new(9, 0, "Scout", true),
            new(2, 1, "scout", false),       // GT side 1 cannot buy -> none
            new(4, 1, "scout", false),
            new(1, 1, "bomber", false),
        };
        var picked = BotManager.PickMedevacScouts(roster);
        Assert.Equal(new[] { 5 }, picked.OrderBy(x => x));
        // Stable across a respawn: same bot, same roster -> same single pick.
        Assert.Equal(picked.OrderBy(x => x), BotManager.PickMedevacScouts(roster).OrderBy(x => x));
        // A LATER capable scout never displaces the lower id; a side with no
        // capable civ simply has no entry.
        roster.Add(new(11, 0, "scout", true));
        Assert.Equal(new[] { 5 }, BotManager.PickMedevacScouts(roster).OrderBy(x => x));
        Assert.Single(BotManager.PickMedevacScouts(roster));
        // Two sides that can both buy get one each.
        roster.Add(new(3, 2, "scout", true));
        Assert.Equal(new[] { 3, 5 }, BotManager.PickMedevacScouts(roster).OrderBy(x => x));
    }

    [Fact]
    public void At_most_MedevacScoutsPerSide_is_one()
        => Assert.Equal(1, BotManager.MedevacScoutsPerSide);
}
