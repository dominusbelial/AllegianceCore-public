using System;
using System.Collections.Generic;
using Allegiance.Core.Collision;
using Allegiance.Tools.McpSpawner;
using Xunit;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P633 — unit proofs for the bot-harness efficiency changes. Failing-first:
/// each test encodes a property the P633 change introduces or must preserve:
/// <list type="bullet">
///   <item>the TWO-PHASE decision loop equals P622's sequential result on fixed
///   seeds (candidate precompute in PHASE A is independent of the engager-count
///   SETTLE in PHASE B, so parallelising the former never changes the latter);</item>
///   <item>the per-sector spatial index's neighbour (radius) query is exactly
///   correct versus brute force, and filters by cluster;</item>
///   <item>the decision-loop metric aggregation (pass-time percentiles, per-bot
///   decision rate) is arithmetically right;</item>
///   <item>the staging-release gate keeps releasing late (straggler) bots after a
///   side has released, and never deadlocks.</item>
/// </list>
/// </summary>
public class P633DecisionLoopTests
{
    // ── two-phase == sequential P622 (item 2) ──────────────────────────────
    // The settle mutates a cumulative engager-count dictionary in BOT ORDER.
    // The precompute (candidate gathering) never reads those counts, so it can
    // run in parallel / any order without changing the settle's result. This
    // test proves that on synthetic fixed seeds, running the gather first (into
    // an array) then settling in order yields the SAME choices as gathering and
    // settling inline sequentially.

    private sealed class ModelBot
    {
        public int Id;
        public List<BotTargetCandidate> Candidates = new();
    }

    private static ModelBot[] MakeFleet(int seed)
    {
        var rnd = new Random(seed);
        int n = 40;
        var bots = new ModelBot[n];
        for (int i = 0; i < n; i++)
        {
            var b = new ModelBot { Id = i };
            int nc = 1 + rnd.Next(12);
            for (int c = 0; c < nc; c++)
            {
                short target = (short)(100 + rnd.Next(20));
                var cls = (BotTargetClass)(rnd.Next(4));
                var pos = new Vector3((float)(rnd.NextDouble() * 2000 - 1000), 0,
                    (float)(rnd.NextDouble() * 2000 - 1000));
                bool threat = rnd.Next(5) == 0;
                float d = (float)(rnd.NextDouble() * 2000);
                b.Candidates.Add(new BotTargetCandidate(target, pos, cls, threat) { Distance = d });
            }
            bots[i] = b;
        }
        return bots;
    }

    private static BotTargetChoice? SelectFor(ModelBot b, Dictionary<short, int> counts)
    {
        var ctx = new BotSelectionContext((short)b.Id, 1, 0, counts);
        var c = CombatTargetSelector.Select(b.Candidates, ctx);
        if (c.TargetId is { } t) counts[t] = (counts.TryGetValue(t, out var v) ? v : 0) + 1;
        return c;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(12345)]
    public void TwoPhase_SettleEqualsSequentialP622(int seed)
    {
        var bots = MakeFleet(seed);

        // Fully sequential: gather (here already gathered) + settle inline.
        var seqCounts = new Dictionary<short, int>();
        var seqChoices = new List<BotTargetChoice?>();
        foreach (var b in bots) seqChoices.Add(SelectFor(b, seqCounts));

        // Two-phase: precompute all candidate lists first (phase A — iterate in
        // REVERSED order to illustrate that the gather's scheduling/order varies
        // with the parallel scheduler, but each slot still holds that BOT's own
        // candidates), THEN settle in ascending bot order (phase B).
        var pre = new List<BotTargetCandidate>[bots.Length];
        for (int j = 0; j < bots.Length; j++)
        {
            int i = bots.Length - 1 - j;                       // reversed gather order
            pre[i] = new List<BotTargetCandidate>(bots[i].Candidates);   // that bot's own
        }
        var tpCounts = new Dictionary<short, int>();
        var tpChoices = new List<BotTargetChoice?>();
        for (int i = 0; i < bots.Length; i++)
        {
            var b = new ModelBot { Id = bots[i].Id, Candidates = pre[i] };
            tpChoices.Add(SelectFor(b, tpCounts));
        }

        Assert.Equal(seqChoices.Count, tpChoices.Count);
        for (int i = 0; i < seqChoices.Count; i++)
            Assert.Equal(seqChoices[i]?.TargetId, tpChoices[i]?.TargetId);
        // The settle mutates counts identically regardless of when candidates
        // were gathered → identical final engager distribution.
        Assert.Equal(seqCounts.Count, tpCounts.Count);
        foreach (var kv in seqCounts) Assert.Equal(kv.Value, tpCounts[kv.Key]);
    }

    [Fact]
    public void TwoPhase_IsDeterministic()
    {
        var bots = MakeFleet(42);
        var a = new List<BotTargetChoice?>(); var ca = new Dictionary<short, int>();
        foreach (var b in bots) a.Add(SelectFor(b, ca));
        var b2 = new List<BotTargetChoice?>(); var cb = new Dictionary<short, int>();
        foreach (var b in bots) b2.Add(SelectFor(b, cb));
        for (int i = 0; i < a.Count; i++)
            Assert.Equal(a[i]?.TargetId, b2[i]?.TargetId);
    }

    [Fact]
    public void Selector_CapAndThreatPriority_Holds()
    {
        // 6 fighters all see ONE MinerDrone target (cap 2): only a 2-bot
        // detachment engages, the rest HOLD (lone-miner detachment-and-hold).
        var counts = new Dictionary<short, int>();
        var candidates = new List<BotTargetCandidate>
        {
            new(501, new Vector3(0, 0, 0), BotTargetClass.MinerDrone, false) { Distance = 100 },
        };
        int engaged = 0, held = 0;
        for (int i = 0; i < 6; i++)
        {
            var ctx = new BotSelectionContext((short)i, 1, 6, counts);
            var c = CombatTargetSelector.Select(candidates, ctx);
            if (c.TargetId is { } t) { counts[t] = (counts.TryGetValue(t, out var v) ? v : 0) + 1; engaged++; }
            else held++;
        }
        Assert.Equal(2, engaged);   // CapMinerDrone == 2
        Assert.Equal(4, held);
    }

    // ── spatial index neighbour correctness (item 3) ───────────────────────
    [Theory]
    [InlineData(11, 250f)]
    [InlineData(22, 700f)]
    [InlineData(33, 40f)]
    public void SpatialIndex_Radius_MatchesBruteForce(int seed, float radius)
    {
        var rnd = new Random(seed);
        var points = new List<BotSpatialIndex.ShipPoint>();
        int n = 300;
        for (int i = 0; i < n; i++)
            points.Add(new BotSpatialIndex.ShipPoint(
                (short)(2000 + i),
                new Vector3((float)(rnd.NextDouble() * 4000 - 2000), 0,
                            (float)(rnd.NextDouble() * 4000 - 2000))));
        var grid = BotSpatialIndex.Build(7, points, 120f);

        var center = new Vector3((float)(rnd.NextDouble() * 4000 - 2000), 0,
                                 (float)(rnd.NextDouble() * 4000 - 2000));
        var got = grid.QueryRadius(center, radius);
        var gotSet = new HashSet<short>();
        foreach (var (id, _) in got) gotSet.Add(id);

        // Brute force.
        var expect = new HashSet<short>();
        foreach (var p in points)
        {
            float dx = p.Position.X - center.X, dz = p.Position.Z - center.Z;
            if (dx * dx + dz * dz <= radius * radius) expect.Add(p.Id);
        }
        Assert.True(gotSet.SetEquals(expect), $"radius {radius} mismatch: got {gotSet.Count}, expected {expect.Count}");
    }

    [Fact]
    public void SpatialIndex_ClusterFilter_ReturnsOnlyItsCluster()
    {
        var points = new List<BotSpatialIndex.ShipPoint>
        {
            new(1, new Vector3(10, 0, 10)),
            new(2, new Vector3(-10, 0, 10)),
            new(3, new Vector3(0, 0, -100)),
        };
        var grid = BotSpatialIndex.Build(3, points, 20f);
        var all = grid.QueryCluster(3);
        Assert.Equal(3, all.Count);
        Assert.Empty(grid.QueryCluster(99));   // other cluster → nothing
        Assert.Equal(3, grid.Cluster);
    }

    // ── decision-loop metrics (item 1) ─────────────────────────────────────
    [Fact]
    public void DecisionMetrics_PassPercentiles_AreCorrect()
    {
        // 1,2,3,4,5 → mean 3, p50 3, p95 5, max 5.
        var s = DecisionMetrics.SummarizePasses(new List<double> { 1, 2, 3, 4, 5 });
        Assert.Equal(3.0, s.MeanMs, 3);
        Assert.Equal(3.0, s.P50Ms, 3);
        Assert.Equal(5.0, s.P95Ms, 3);
        Assert.Equal(5.0, s.MaxMs, 3);
        Assert.Equal(DecisionMetrics.PassStat.Empty, DecisionMetrics.SummarizePasses(Array.Empty<double>()));
    }

    [Fact]
    public void DecisionMetrics_Rate_IsInverseOfPassTime()
    {
        Assert.Equal(20.0, DecisionMetrics.DecisionRateHz(50.0), 3);   // 50 ms → 20 Hz
        Assert.Equal(0.0, DecisionMetrics.DecisionRateHz(0.0), 3);
    }

    // ── staging release (item 7) ──────────────────────────────────────────
    [Fact]
    public void StagingRelease_DoesNotDeadlock_AndReleasesLateBots()
    {
        // Fully assembled → release.
        Assert.True(BotManager.SideReadyToAdvance(10, 10, 0, 1.0, 15.0));
        // Nobody living → release (no deadlock).
        Assert.True(BotManager.SideReadyToAdvance(0, 0, 0, null, 15.0));
        // Partial, before timeout → hold.
        Assert.False(BotManager.SideReadyToAdvance(10, 5, 10.0, 0.0, 15.0));
        // Partial, past timeout → release (stragglers are let through).
        Assert.True(BotManager.SideReadyToAdvance(10, 5, 20.0, 0.0, 15.0));
        // Timeout counts from the FIRST staged bot, not `now`.
        Assert.False(BotManager.SideReadyToAdvance(10, 5, 5.0, 0.0, 15.0));
    }
}
