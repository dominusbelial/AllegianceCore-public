using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P303 — pure-domain proofs of the <b>flags + kills win conditions</b>: the
/// <see cref="Mission.RecomputeFlagsAndKills"/> computation (the port of the
/// flag leg of <c>CheckForVictoryByFlags</c>, <c>fsmission.cpp:3883-3887</c>,
/// and <c>CheckForVictoryByKills</c>, <c>fsmission.cpp:3749-3760</c>). The live
/// loopback (<see cref="FlagsKillsLiveTests"/>) proves the full
/// flag-secure/kill-credit → GameState → GameOver chain over the wire; this
/// file pins the exact algorithm and its selection wiring deterministically.
/// Missions are assembled from the real <c>PCore001.igc</c> core + the real
/// <c>XXV_simple.igc</c> map, so the sides carry real ids (0/1) to exercise the
/// per-side thresholds.
/// </summary>
public sealed class FlagsKillsTests
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

    // ── flags ────────────────────────────────────────────────────────────

    /// <summary>⭐ A side whose <see cref="Side.Flags"/> reaches
    /// <see cref="Mission.FlagsWinGoal"/> wins the flags game — the port of
    /// <c>fsmission.cpp:3883-3887</c> (<c>GetFlags() &gt;= nGoalFlagsCount</c>,
    /// the real-flag leg of <c>CheckForVictoryByFlags</c>).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Flags_reaching_goal_declares_flags_winner()
    {
        Require.Artwork();
        var m = Assembly();
        m.FlagsEnabled = true;
        m.FlagsWinGoal = 2;

        // Below goal: 1 < 2 → no winner.
        m.Sides[0].Flags = 1;
        var (w0, c0) = m.RecomputeFlagsAndKills();
        Assert.Null(w0);
        Assert.False(c0);

        // Goal reached → side 0 wins.
        m.Sides[0].Flags = 2;
        var (w1, c1) = m.RecomputeFlagsAndKills();
        Assert.True(c1);
        Assert.NotNull(w1);
        Assert.Equal((short)0, w1!.Id);
        Console.WriteLine("FLAGS-DOMAIN: side 0 securing 2/2 enemy flags -> winner side 0");
    }

    /// <summary>⭐ Selection wiring: with <see cref="Mission.FlagsEnabled"/>
    /// false (the default), securing flags does NOT win — existing non-flags
    /// missions are unaffected.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Flags_disabled_goal_does_not_win()
    {
        Require.Artwork();
        var m = Assembly();
        m.FlagsEnabled = false;
        m.FlagsWinGoal = 1;
        m.Sides[0].Flags = 5;

        var (w, c) = m.RecomputeFlagsAndKills();
        Assert.Null(w);
        Assert.False(c);
        Console.WriteLine("FLAGS-DOMAIN-DISABLED: flags 5/1 with FlagsEnabled=false -> no winner");
    }

    // ── kills ────────────────────────────────────────────────────────────

    /// <summary>⭐ A side whose <see cref="Side.Kills"/> reaches
    /// <see cref="Mission.KillsWinGoal"/> wins the deathmatch game — the port of
    /// <c>fsmission.cpp:3749-3760</c> (<c>GetKills() &gt;= nGoalTeamKills</c>,
    /// <c>CheckForVictoryByKills</c>).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Kills_reaching_goal_declares_kills_winner()
    {
        Require.Artwork();
        var m = Assembly();
        m.KillsEnabled = true;
        m.KillsWinGoal = 3;

        // Below goal: 2 < 3 → no winner.
        m.Sides[0].Kills = 2;
        var (w0, c0) = m.RecomputeFlagsAndKills();
        Assert.Null(w0);
        Assert.False(c0);

        // Goal reached → side 0 wins.
        m.Sides[0].Kills = 3;
        var (w1, c1) = m.RecomputeFlagsAndKills();
        Assert.True(c1);
        Assert.NotNull(w1);
        Assert.Equal((short)0, w1!.Id);
        Console.WriteLine("KILLS-DOMAIN: side 0 scoring 3/3 kills -> winner side 0");
    }

    /// <summary>⭐ Selection wiring: with <see cref="Mission.KillsEnabled"/>
    /// false (the default), scoring kills does NOT win — existing non-deathmatch
    /// missions are unaffected.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Kills_disabled_goal_does_not_win()
    {
        Require.Artwork();
        var m = Assembly();
        m.KillsEnabled = false;
        m.KillsWinGoal = 1;
        m.Sides[0].Kills = 9;

        var (w, c) = m.RecomputeFlagsAndKills();
        Assert.Null(w);
        Assert.False(c);
        Console.WriteLine("KILLS-DOMAIN-DISABLED: kills 9/1 with KillsEnabled=false -> no winner");
    }

    /// <summary>⭐ Branch priority mirrors native's cascade: when both flags and
    /// kills are enabled, the flags branch is consulted first
    /// (<c>fsmission.cpp:2445-2465</c> precedes the kills tie-break) — a side
    /// that simultaneously qualifies on both is awarded by flags (lowest side
    /// id wins the flags scan).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Flags_branch_is_consulted_before_kills()
    {
        Require.Artwork();
        var m = Assembly();
        m.FlagsEnabled = true;
        m.FlagsWinGoal = 1;
        m.KillsEnabled = true;
        m.KillsWinGoal = 1;
        m.Sides[0].Flags = 1;   // qualifies on flags
        m.Sides[1].Kills = 1;   // also qualifies on kills

        var (w, c) = m.RecomputeFlagsAndKills();
        Assert.True(c);
        Assert.NotNull(w);
        Assert.Equal((short)0, w!.Id);   // the flags qualifier (lowest side id), not the kills qualifier
        Console.WriteLine("FLAGS-KILLS-DOMAIN-PRIORITY: both qualify -> flags branch wins (side 0)");
    }

    /// <summary>⭐ Native stage gate: only a Started/Starting mission recomputes —
    /// the same gate as P191/P302 (<c>fsmission.cpp:3575-3578</c>). A mission
    /// that ended (Over) never re-wins.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Not_started_or_over_mission_never_recomputes_win()
    {
        Require.Artwork();
        var m = Assembly();
        m.Stage = MissionStage.Over;
        m.FlagsEnabled = true;
        m.FlagsWinGoal = 1;
        m.KillsEnabled = true;
        m.KillsWinGoal = 1;
        m.Sides[0].Flags = 9;
        m.Sides[0].Kills = 9;

        var (w, _) = m.RecomputeFlagsAndKills();
        Assert.Null(w);
        Console.WriteLine("FLAGS-KILLS-DOMAIN-STAGE: Over mission never re-computes a winner");
    }
}
