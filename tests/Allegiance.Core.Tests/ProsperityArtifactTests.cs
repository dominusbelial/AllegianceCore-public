using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P302 — pure-domain proofs of the <b>prosperity + artifact win conditions</b>:
/// the <see cref="Mission.RecomputeProsperityAndArtifacts"/> computation (the
/// port of the <c>c_didTeamMoney</c> economic-victory trigger,
/// <c>FedSrv.CPP:11274-11279</c>, and the artifact leg of
/// <c>CheckForVictoryByFlags</c>, <c>fsmission.cpp:3888-3894</c>). The live
/// loopback (<see cref="ProsperityArtifactLiveTests"/>) proves the full
/// dev-complete/artifact-secure → GameState → GameOver chain over the wire;
/// this file pins the exact algorithm and its selection wiring deterministically.
/// Missions are assembled from the real <c>PCore001.igc</c> core + the real
/// <c>XXV_simple.igc</c> map, so the side bucket catalogs carry the real
/// <c>c_didTeamMoney</c> (id 1) development bucket (<see cref="Mission.TeamMoneyDevelopmentId"/>).
/// </summary>
public sealed class ProsperityArtifactTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Mission Assembly()
    {
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "XXV_simple.igc")));
        var sides = new[]
        {
            new SideSpec(0, "Alpha", 1),
            new SideSpec(1, "Bravo", 3),
        };
        var m = Mission.Assemble(core, map, sides, new RecordingIgcSite());
        m.Stage = MissionStage.Started;              // native gate: only Started/Starting recompute
        return m;
    }

    /// <summary>Locate the real <c>c_didTeamMoney</c> (id 1) development bucket on
    /// a side — present because <see cref="BucketCatalog.ForSide"/> creates a
    /// bucket per non-obsolete development and the corpus's id-1 development is
    /// not tech-only/obsolete (measured).</summary>
    private static Bucket TeamMoneyBucket(Mission m, int sideIdx)
    {
        var tm = m.Sides[sideIdx].Buckets.FirstOrDefault(b =>
            b.ItemType == BucketItemType.BitDevelopment && b.Development is { } d && d.DevelopmentId == Mission.TeamMoneyDevelopmentId);
        Assert.NotNull(tm);
        return tm!;
    }

    // ── prosperity ────────────────────────────────────────────────────────

    /// <summary>⭐ Completing the team-money (<c>c_didTeamMoney</c>) development
    /// is the prosperity win — the port of <c>FedSrv.CPP:11274-11279</c> (a side
    /// whose team-money bucket is fully funded AND fully time-invested wins).
    /// The real corpus bucket is driven through the real completion gate
    /// (<see cref="Bucket.AddMoney"/> + <see cref="Bucket.Update"/> accruing
    /// build time), matching the native completion path.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Completing_team_money_development_declares_prosperity_winner()
    {
        Require.Artwork();
        var m = Assembly();
        m.Goals.TeamMoney = 1;
        var tm = TeamMoneyBucket(m, 0);

        // Below goal: bucket still building → no winner.
        var (w0, c0) = m.RecomputeProsperityAndArtifacts();
        Assert.Null(w0);
        Assert.False(c0);

        // Fully fund + accrue the full build time → the bucket completes.
        tm.AddMoney(tm.Price);
        Assert.True(tm.InvestedMoney >= tm.Price);
        double now = 0;
        for (int i = 0; i < tm.TimeToBuild + 2; i++) { now += 1.0; tm.Update(now); }
        Assert.True(tm.Completed, "the fully-funded team-money bucket must complete");

        var (w1, c1) = m.RecomputeProsperityAndArtifacts();
        Assert.True(c1);
        Assert.NotNull(w1);
        Assert.Equal((short)0, w1!.Id);
        Console.WriteLine($"PROSP-DOMAIN: side 0 completing its team-money dev bucket (id {tm.Id}) -> winner side 0");
    }

    /// <summary>⭐ Selection wiring: with <see cref="Mission.ProsperityEnabled"/>
    /// false (the default), completing the team-money development is NOT a win —
    /// existing non-prosperity missions are unaffected.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Prosperity_disabled_completion_does_not_win()
    {
        Require.Artwork();
        var m = Assembly();
        m.Goals.TeamMoney = 0;
        var tm = TeamMoneyBucket(m, 0);

        tm.AddMoney(tm.Price);
        double now = 0;
        for (int i = 0; i < tm.TimeToBuild + 2; i++) { now += 1.0; tm.Update(now); }
        Assert.True(tm.Completed);

        var (w, c) = m.RecomputeProsperityAndArtifacts();
        Assert.Null(w);
        Assert.False(c);
        Console.WriteLine("PROSP-DOMAIN-DISABLED: team-money dev completed with ProsperityEnabled=false -> no winner");
    }

    // ── artifacts ─────────────────────────────────────────────────────────

    /// <summary>⭐ A side whose <see cref="Side.Artifacts"/> reaches
    /// <see cref="Mission.ArtifactWinGoal"/> wins the artifact game — the port of
    /// <c>fsmission.cpp:3890</c> (<c>GetArtifacts() &gt;= nGoalArtifactsCount</c>).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Artifacts_reaching_goal_declares_artifact_winner()
    {
        Require.Artwork();
        var m = Assembly();
        m.Goals.Artifacts = 2;

        // Below goal: 1 < 2 → no winner.
        m.Sides[0].Artifacts = 1;
        var (w0, c0) = m.RecomputeProsperityAndArtifacts();
        Assert.Null(w0);
        Assert.False(c0);

        // Goal reached → side 0 wins.
        m.Sides[0].Artifacts = 2;
        var (w1, c1) = m.RecomputeProsperityAndArtifacts();
        Assert.True(c1);
        Assert.NotNull(w1);
        Assert.Equal((short)0, w1!.Id);
        Console.WriteLine("ARTIFACT-DOMAIN: side 0 securing 2/2 artifacts -> winner side 0");
    }

    /// <summary>⭐ Selection wiring: with <see cref="Mission.ArtifactEnabled"/>
    /// false (the default), securing artifacts does NOT win — existing
    /// non-artifact missions are unaffected.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Artifacts_disabled_goal_does_not_win()
    {
        Require.Artwork();
        var m = Assembly();
        m.Goals.Artifacts = 0;
        m.Sides[0].Artifacts = 5;

        var (w, c) = m.RecomputeProsperityAndArtifacts();
        Assert.Null(w);
        Assert.False(c);
        Console.WriteLine("ARTIFACT-DOMAIN-DISABLED: artifacts 5, goal 0 (disabled) -> no winner");
    }

    /// <summary>⭐ Native stage gate: only a Started/Starting mission recomputes —
    /// the same gate as P191 (<c>fsmission.cpp:3575-3578</c>). A mission that
    /// ended (Over) never re-wins.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Not_started_or_over_mission_never_recomputes_win()
    {
        Require.Artwork();
        var m = Assembly();
        m.Stage = MissionStage.Over;
        m.Goals.Artifacts = 1;
        m.Sides[0].Artifacts = 9;

        var (w, _) = m.RecomputeProsperityAndArtifacts();
        Assert.Null(w);
        Console.WriteLine("ARTIFACT-DOMAIN-STAGE: Over mission never re-computes a winner");
    }
}
