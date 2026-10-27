using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P837 — two master regressions in the bot live tests had the same shape: a
/// policy tuned on the rig was applied unchanged to a nine-bot skirmish and to a
/// two-bomber next-match finale, where the rig's times and sizes do not fit.
/// <list type="bullet">
/// <item><b>P727 skirmish</b> (first bad commit <c>a8896574</c>, P798): under
/// native fog a side's only early "resource" is its own miners on its own base,
/// so <see cref="ClashScoring"/> picked the side's OWN home as the clash, every
/// non-scout parked there, and the two fleets never met (0 shots in 210 s).</item>
/// <item><b>P682b rotated finale</b> (first bad commit <c>62a5b50f</c>, P831): the
/// re-arm's per-side "observed an enemy station" gate could only be released by
/// the rig's 300 s grace, which is longer than the fixture's 240 s window, and a
/// one-bot side was below <c>MinFleetForExploration</c> so nothing handed it an
/// objective to go and look with.</item>
/// </list>
/// These are the pure, fast proofs of the scaled policy; the live behaviour is
/// proven by the two live tests themselves.
/// </summary>
public sealed class P837BotEngagementScaleTests
{
    /// <summary>1(home) - 2 - 3 - 4 - 5 - 6(home), symmetric about 3.5.</summary>
    private static ClusterGraph Chain(int count = 6, short? ownHome = 1, short? enemyHome = 6)
    {
        var clusters = Enumerable.Range(1, count)
            .Select(i => new ClusterInfo.ClusterEntry((short)i, i == ownHome || i == enemyHome)).ToList();
        var warps = Enumerable.Range(1, count - 1)
            .Select(i => new ClusterInfo.WarpEdge((short)i, (short)(i + 1))).ToList();
        return new ClusterGraph { Clusters = clusters, Warps = warps };
    }

    [Fact]
    public void A_sides_own_home_is_never_the_clash_objective()
    {
        var graph = Chain();
        // The only thing this side has seen is its own miners working its own base —
        // exactly what a fogged nine-bot skirmish sees in the first two minutes.
        var choice = ClashScoring.Choose(graph, ownHome: 1, enemyHome: 6, knownSectors: [1],
            rocks: [], ownMiners: [new MinerSighting(10, 1, 1, 0)], enemyMiners: [], enemyStations: [],
            topologyFallback: true);
        Assert.NotNull(choice);
        Assert.NotEqual((short)1, choice!.Value.Sector);
        // Own miners in a FORWARD sector are still a contested area: only home is out.
        var forward = ClashScoring.Choose(graph, ownHome: 1, enemyHome: 6, knownSectors: [1, 2],
            rocks: [], ownMiners: [new MinerSighting(10, 1, 2, 0)], enemyMiners: [], enemyStations: []);
        Assert.NotNull(forward);
        Assert.Equal((short)2, forward!.Value.Sector);
        Assert.True(forward.Value.Resource <= 0 && forward.Value.OwnMiners == 1);
    }

    [Fact]
    public void Midpoint_fallback_is_the_same_sector_for_both_sides()
    {
        var graph = Chain();
        Assert.Equal((short)3, ClashScoring.Midpoint(graph, 1, 6));
        Assert.Equal((short)3, ClashScoring.Midpoint(graph, 6, 1));   // symmetric: swap cannot change it
        // A branched ring: the balanced sector, not merely the lowest id.
        var ring = new ClusterGraph
        {
            Clusters = Enumerable.Range(1, 7).Select(i => new ClusterInfo.ClusterEntry((short)i, i is 1 or 4)).ToList(),
            Warps =
            [
                new ClusterInfo.WarpEdge(1, 2), new ClusterInfo.WarpEdge(2, 3), new ClusterInfo.WarpEdge(3, 4),
                new ClusterInfo.WarpEdge(4, 5), new ClusterInfo.WarpEdge(5, 6), new ClusterInfo.WarpEdge(6, 7),
                new ClusterInfo.WarpEdge(7, 1),
            ],
        };
        short? homeA = 1, homeB = 4;
        var mid = ClashScoring.Midpoint(ring, homeA.Value, homeB.Value);
        Assert.Equal(mid, ClashScoring.Midpoint(ring, homeB.Value, homeA.Value));
        Assert.NotNull(mid);
        int own = ExplorationPlanner.Hops(ring, homeA.Value, mid!.Value);
        int enemy = ExplorationPlanner.Hops(ring, homeB.Value, mid.Value);
        Assert.InRange(Math.Abs(own - enemy), 0, 1);
    }

    [Fact]
    public void Midpoint_fallback_needs_no_knowledge_at_all()
    {
        // The failure mode P837 fixes: NOTHING known yet (a fresh fogged mission),
        // so a known-sector fallback had no candidate at all and the fleets waited
        // the rig's 420 s. The topology + the home flags are global (fsmission.cpp:
        // 4122-4174), so the midpoint answer needs no sightings.
        var graph = Chain();
        var choice = ClashScoring.Choose(graph, ownHome: 1, enemyHome: 6, knownSectors: [],
            rocks: [], ownMiners: [], enemyMiners: [], enemyStations: [], topologyFallback: true);
        Assert.NotNull(choice);
        Assert.Equal((short)3, choice!.Value.Sector);
        Assert.Contains("midpoint", choice.Value.Why);
        Assert.Contains("fallback", choice.Value.Why);
        // Without the caller's permission the old contract stands: nothing seen, no pick.
        Assert.Null(ClashScoring.Choose(graph, 1, 6, [], [], [], [], []));
    }

    [Fact]
    public void Clash_delays_scale_from_the_skirmish_to_the_rig()
    {
        // 9-bot skirmish (4-5 combat bots on the side): commit while the battle is
        // still young instead of after the rig's 75 s opening window.
        Assert.Equal(ScoutOpening.SmallFleetClashDelaySeconds, ScoutOpening.ClashDelaySecondsFor(4));
        Assert.Equal(ScoutOpening.SmallFleetClashDelaySeconds, ScoutOpening.ClashDelaySecondsFor(2));
        // The 200v200 rig's ~48 combat bots per side keep the designed numbers.
        Assert.Equal(ScoutOpening.ClashDelaySeconds, ScoutOpening.ClashDelaySecondsFor(ScoutOpening.RigScaleCombatBots));
        Assert.Equal(ScoutOpening.ClashFallbackSeconds, ScoutOpening.ClashFallbackSecondsFor(ScoutOpening.RigScaleCombatBots));
        Assert.Equal(ScoutOpening.ClashFallbackSeconds, ScoutOpening.ClashFallbackSecondsFor(200));
        // A small fleet's topology fallback is minutes earlier than the rig's 420 s.
        Assert.True(ScoutOpening.ClashFallbackSecondsFor(4) <= 60,
            $"small-fleet fallback was {ScoutOpening.ClashFallbackSecondsFor(4)} s");
        // Monotone in between, so no fleet size loses the opening window.
        double previous = 0;
        for (int bots = 1; bots <= 64; bots++)
        {
            double delay = ScoutOpening.ClashDelaySecondsFor(bots);
            double value = ScoutOpening.ClashFallbackSecondsFor(bots);
            Assert.InRange(delay, ScoutOpening.SmallFleetClashDelaySeconds, ScoutOpening.ClashDelaySeconds);
            Assert.InRange(value, delay, ScoutOpening.ClashFallbackSeconds);
            Assert.True(value >= previous);
            previous = value;
        }
    }

    [Fact]
    public void Rearm_grace_scales_down_for_a_small_rotated_finale()
    {
        // A two-bomber next match gets a deadline inside a 240 s window; the rig
        // keeps P831's measured 300 s.
        Assert.Equal(BotManager.MinRearmGraceSeconds, BotManager.RearmGraceSecondsFor(2));
        Assert.Equal(BotManager.RearmGraceSecondsFor(1), BotManager.RearmGraceSecondsFor(4));
        Assert.Equal(BotManager.RearmGraceSeconds, BotManager.RearmGraceSecondsFor(BotManager.RigScaleRearmBots));
        Assert.Equal(BotManager.RearmGraceSeconds, BotManager.RearmGraceSecondsFor(200));
        Assert.True(BotManager.RearmGraceSecondsFor(2) < 240, "the live fixture waits 240 s");
    }

    /// <summary>P837 — the skirmish map reveal scales with the match: a Small or
    /// Medium game reveals the WARPS (native bShowMap, Igc/igc.h:1123 — warps only,
    /// Igc/mapmakerigc.cpp:1002-1017) so a nine-bot fleet can route and meet, while
    /// Large and up keep P798's native fog. Without the reveal the first aleph out
    /// of the home sector costs 100-160 s of sweeping and the skirmish fires no
    /// shots at all in its first three minutes.</summary>
    [Theory]
    [InlineData(SkirmishSize.Small, true)]
    [InlineData(SkirmishSize.Medium, true)]
    [InlineData(SkirmishSize.Large, false)]
    [InlineData(SkirmishSize.Epic, false)]
    [InlineData(SkirmishSize.Grand, false)]
    public void Skirmish_map_reveal_scales_with_the_match(SkirmishSize size, bool warpsRevealed)
    {
        Assert.Equal(warpsRevealed, new SkirmishOptions { Size = size }.CreateConfig().Games[0].ShowMap);
        // The reveal is warps-only: stations/rocks stay per-side discovery.
        Assert.False(new SkirmishOptions { Size = size }.CreateConfig().Games[0].ShowHomeSector);
    }
}
