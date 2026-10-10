using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// Table-driven verification of <see cref="Mission.Update"/> against
/// <c>CmissionIGC::Update</c> (<c>missionigc.cpp:3127-3185</c>). The cadence
/// and chunk arithmetic are the point: sides update once per Update, clusters
/// update once per chunk, and the elapsed time splits into
/// <c>n = 1 + floor(deltaT / 0.25)</c> evenly-divided chunks — not fixed 250&nbsp;ms
/// steps. Rows pin the expected chunk count <em>literally</em> rather than
/// recomputing the formula, so the boundary behaviours are actually asserted.
/// </summary>
public class MissionUpdateTests
{
    /// <summary>A representative static core, like <see cref="MissionAssemblyTests"/>.</summary>
    private static readonly Lazy<StaticCore> RepresentativeCore = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            return StaticCore.Load(file);
        }
        throw new InvalidOperationException("no static core found in corpus");
    });

    /// <summary>A map, assembled so <see cref="Mission"/>.Sectors is non-empty.</summary>
    private static readonly Lazy<MissionMap> SomeMap = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count == 0) continue;
            return map;
        }
        throw new InvalidOperationException("no non-empty map found in corpus");
    });

    /// <summary>One or two sides drawn from the core's civilizations (enough for the ordering test).</summary>
    private static IReadOnlyList<SideSpec> SideSpecs()
    {
        var core = RepresentativeCore.Value;
        var civs = core.Civilizations.Values.OrderBy(c => c.CivilizationId).Take(2).ToList();
        return civs.Count == 1
            ? [new SideSpec(0, "Team 1", civs[0].CivilizationId)]
            : [
                new SideSpec(0, "Team 1", civs[0].CivilizationId),
                new SideSpec(1, "Team 2", civs[1].CivilizationId),
            ];
    }

    /// <summary>Assemble a fresh mission bound to the given site, then start it.</summary>
    private static Mission NewMission(IIgcSite site)
    {
        var core = RepresentativeCore.Value;
        var map = SomeMap.Value;
        var mission = Mission.Assemble(core, map, SideSpecs(), site);
        mission.Stage = MissionStage.Started;
        return mission;
    }

    // The interesting boundary of `n = 1 + (int)(deltaT / c_fTimeIncrement)`
    // with c_fTimeIncrement = 0.25f (igc.h:5120): n is 1 iff deltaT < 0.25,
    // and — because `(int)` truncates — n is ALREADY 2 when deltaT == 0.25
    // exactly (quotient 1.0 → (int)1 → n=2). Each row pins n literally.
    public static TheoryData<double, int> DeltaTCases() => new()
    {
        // n = 1 + (int)(deltaT / 0.25)  (c_fTimeIncrement = 0.25f, igc.h:5120).
        // Bands: n==1 ⇔ deltaT∈(0,0.25); n==2 ⇔ [0.25,0.5); n==3 ⇔ [0.5,0.75);
        // n==4 ⇔ [0.75,1.0); … every expected n is computed by dividing the
        // value (shown in the comment) so the truncation is asserted, not
        // approximated.
        { 0.137, 1 },   // 0.137/0.25 = 0.548  → (int)0 → 1   tiny → single chunk
        { 0.239, 1 },   // 0.239/0.25 = 0.956  → (int)0 → 1   just under one increment
        { 0.548, 3 },   // 2.192 → 2 → 3
        { 0.555, 3 },   // 2.22  → 2 → 3
        { 0.578, 3 },   // 2.312 → 2 → 3
        { 0.601, 3 },   // 2.404 → 2 → 3
        { 0.621, 3 },   // 2.484 → 2 → 3
        { 0.642, 3 },   // 2.568 → 2 → 3
        { 0.731, 3 },   // 2.924 → 2 → 3
        { 0.772, 4 },   // 3.088 → 3 → 4
        { 0.778, 4 },   // 3.112 → 3 → 4
        { 0.813, 4 },   // 3.252 → 3 → 4
        { 0.815, 4 },   // 3.26  → 3 → 4
        { 0.820, 4 },   // 3.28  → 3 → 4
        { 0.850, 4 },   // 3.40  → 3 → 4
        { 0.867, 4 },   // 3.468 → 3 → 4
        { 0.910, 4 },   // 3.64  → 3 → 4
        { 1.552, 7 },   // 6.208 → 6 → 7
        { 1.577, 7 },   // 6.308 → 6 → 7
        { 1.583, 7 },   // 6.332 → 6 → 7
        { 1.590, 7 },   // 6.36  → 6 → 7
        { 5.082, 21 },  // 20.328 → 20 → 21

        // ⚠ The (int)-truncation boundary the task flags: n is ALREADY 2 when
        // deltaT == one increment exactly (0.25), because the quotient 1.0
        // truncates to 1, not to 0. (Reproduced from C++ missionigc.cpp:3156.)
        { 0.250, 2 },
    };

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [MemberData(nameof(DeltaTCases))]
    public void Chunk_count_matches_the_Cpp_boundaries(double deltaT, int expectedN)
    {
        Require.Artwork();
        var mission = NewMission(new NullIgcSite());
        int s = mission.Sectors.Count;
        Assert.True(s > 0, "test map must have at least one sector");
        Assert.True(mission.Sides.Count > 0, "test map must have at least one side");

        mission.Update(deltaT);                 // LastUpdate == 0, so elapsed == deltaT

        // Clusters update ONCE PER CHUNK → each sector sees exactly n updates.
        Assert.All(mission.Sectors, sec => Assert.Equal(expectedN, sec.UpdateCount));

        // The last chunk lands exactly on `now`, no floating-point drift.
        Assert.All(mission.Sectors, sec => Assert.Equal(deltaT, sec.LastUpdate));

        // Sides update ONCE per Update, regardless of n.
        Assert.All(mission.Sides, side => Assert.Equal(1L, side.UpdateCount));
        Assert.All(mission.Sides, side => Assert.Equal(deltaT, side.LastUpdate));

        // Postcondition on the chunked path.
        Assert.Equal(deltaT, mission.LastUpdate);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Unchanged_now_is_a_no_op()
    {
        Require.Artwork();
        var site = new RecordingIgcSite();
        var mission = NewMission(site);
        mission.Update(0.75);
        long secUpdates = mission.Sectors.Sum(s => s.UpdateCount);
        long sideUpdates = mission.Sides.Sum(s => s.UpdateCount);
        Assert.Equal(0.75, mission.LastUpdate);

        site.Reset();
        mission.Update(0.75);                  // now == LastUpdate → nothing at all

        Assert.Equal(secUpdates, mission.Sectors.Sum(s => s.UpdateCount));
        Assert.Equal(sideUpdates, mission.Sides.Sum(s => s.UpdateCount));
        Assert.False(site.Any, "now == LastUpdate must emit no site calls");
        Assert.Equal(0.75, mission.LastUpdate);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Backwards_clock_throws()
    {
        Require.Artwork();
        var mission = NewMission(new NullIgcSite());
        mission.Update(1.0);
        Assert.Throws<InvalidOperationException>(() => mission.Update(0.5));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Ten_minute_jump_throws()
    {
        Require.Artwork();
        var mission = NewMission(new NullIgcSite());
        // The C++ asserts now - m_lastUpdate < 600.0f (missionigc.cpp:3148);
        // we throw rather than silently clamp. Decision documented in README.
        var ex = Assert.Throws<InvalidOperationException>(() => mission.Update(600.0));
        Assert.Contains("600", ex.Message);
        Assert.Equal(0.0, mission.LastUpdate);  // unchanged after a refused tick
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(MissionStage.NotStarted)]
    [InlineData(MissionStage.Over)]
    public void Not_started_or_over_just_advances(MissionStage stage)
    {
        Require.Artwork();
        var site = new RecordingIgcSite();
        var mission = NewMission(site);
        mission.Stage = stage;

        mission.Update(3.75);                  // huge delta, but not-started path ignores it

        Assert.Equal(3.75, mission.LastUpdate);           // jumped straight to now
        Assert.All(mission.Sectors, sec => Assert.Equal(0L, sec.UpdateCount)); // zero ticks
        Assert.All(mission.Sides, side => Assert.Equal(0L, side.UpdateCount)); // zero ticks
        Assert.False(site.Any, "not-started path must not touch the site");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Sides_update_before_first_cluster_chunk()
    {
        Require.Artwork();
        var site = new RecordingIgcSite();
        var mission = NewMission(site);
        int sideCount = mission.Sides.Count;
        int sectorCount = mission.Sectors.Count;
        Assert.True(sideCount >= 1);
        Assert.True(sectorCount >= 1);

        mission.Update(0.756);                 // n = 1 + (int)(0.756/0.25) = 1+3 = 4 chunks

        var sideCalls = site.Calls.Where(c => c.StartsWith("SideUpdated(")).ToList();
        var clusterCalls = site.Calls.Where(c => c.StartsWith("ClusterUpdateEvent(")).ToList();

        // Sides exactly once each.
        Assert.Equal(sideCount, sideCalls.Count);
        // Cluster events exactly sectorCount per chunk × n chunks.
        Assert.Equal(sectorCount * 4, clusterCalls.Count);

        // ALL side updates precede the FIRST cluster chunk: everything recorded
        // before the first ClusterUpdateEvent call is a SideUpdated call.
        int firstClusterIndex = site.Calls.Select((c, i) => (c, i)).First(x => x.c.StartsWith("ClusterUpdateEvent(")).i;
        Assert.True(firstClusterIndex >= sideCount);
        Assert.All(site.Calls.Take(firstClusterIndex), c => Assert.StartsWith("SideUpdated(", c));
        // And the cluster pass dominates the rest (no stray side update after chunk 1).
        Assert.All(site.Calls.Skip(firstClusterIndex), c => Assert.StartsWith("ClusterUpdateEvent(", c));
    }
}
