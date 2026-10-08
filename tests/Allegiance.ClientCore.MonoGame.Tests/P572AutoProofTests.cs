using System;
using System.Linq;
using Allegiance.ClientCore.MonoGame.LiveProof;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P572 — pure unit tests for the live-proof automation: the flag grammar
/// (<see cref="AutoProofPlan"/>) and the sequencing state machine
/// (<see cref="AutoProofMachine"/>) that walks side → ready → launch → goto →
/// shots → dock-sweep → exit. These are the pure, headless parts of the
/// feature; the thin Game1 glue that performs each action is exercised by the
/// live proof itself (docs/reports/P572_LIVE_PROOF_HOOK.md).
/// </summary>
public class P572AutoProofTests
{
    // ── flag parsing ──────────────────────────────────────────────────────────

    [Fact]
    public void Parse_FullFlagSet_ReturnsEveryValueAndLeavesTheRest()
    {
        var plan = AutoProofPlan.Parse(new[]
        {
            "--auto-side", "2",
            "--auto-ready",
            "--auto-launch", "fighter",
            "--auto-goto", "Alderan",
            "--auto-target", "enemy",
            "--camera", "chase",
            "--hide-hud",
            "--autoshots", "5,10,20",
            "--autoshot-dir", "/tmp/proof",
            "--auto-dock-after", "15",
            "--dock-sweep", "6",
            "--exit-after", "30",
            "127.0.0.1", "6073", "monogame",
        }, out var rest);

        Assert.Equal(2, plan.AutoSide);
        Assert.True(plan.AutoReady);
        Assert.True(plan.AutoLaunchRequested);
        Assert.Equal("fighter", plan.LaunchHull);
        Assert.Equal("Alderan", plan.GotoSector);
        Assert.Equal(AutoTargetKind.Enemy, plan.Target);
        Assert.Equal("chase", plan.Camera);
        Assert.True(plan.HideHud);
        Assert.Equal(new[] { 5.0, 10.0, 20.0 }, plan.AutoshotSeconds);
        Assert.Equal("/tmp/proof", plan.AutoshotDir);
        Assert.Equal(15.0, plan.AutoDockAfter);
        Assert.Equal(6, plan.DockSweepCount);
        Assert.Equal(30.0, plan.ExitAfter);
        Assert.True(plan.AnyActive);

        Assert.Equal(new[] { "127.0.0.1", "6073", "monogame" }, rest);
    }

    [Fact]
    public void Parse_EqualsForms_WorkLikeSeparateTokens()
    {
        var plan = AutoProofPlan.Parse(new[]
        {
            "--auto-side=1", "--auto-ready=true", "--auto-launch=scout",
            "--auto-goto=Rigel", "--auto-target=friendly", "--camera=cockpit",
            "--autoshots=3,7", "--autoshot-dir=out", "--auto-dock-after=12.5",
            "--dock-sweep=4", "--exit-after=45",
        }, out var rest);

        Assert.Equal(1, plan.AutoSide);
        Assert.True(plan.AutoReady);
        Assert.True(plan.AutoLaunchRequested);
        Assert.Equal("scout", plan.LaunchHull);
        Assert.Equal("Rigel", plan.GotoSector);
        Assert.Equal(AutoTargetKind.Friendly, plan.Target);
        Assert.Equal("cockpit", plan.Camera);
        Assert.Equal(new[] { 3.0, 7.0 }, plan.AutoshotSeconds);
        Assert.Equal(12.5, plan.AutoDockAfter);
        Assert.Equal(4, plan.DockSweepCount);
        Assert.Equal(45.0, plan.ExitAfter);
        Assert.Empty(rest);
    }

    [Fact]
    public void Parse_NoFlags_IsInertAndPreservesPositionalArgs()
    {
        var plan = AutoProofPlan.Parse(new[] { "10.0.0.5", "2326", "someone" }, out var rest);

        Assert.False(plan.AnyActive);
        Assert.Null(plan.AutoSide);
        Assert.False(plan.AutoReady);
        Assert.False(plan.AutoLaunchRequested);
        Assert.Equal("", plan.LaunchHull);
        Assert.Equal("", plan.GotoSector);
        Assert.Equal(AutoTargetKind.None, plan.Target);
        Assert.Equal("cockpit", plan.Camera);
        Assert.False(plan.HideHud);
        Assert.Empty(plan.AutoshotSeconds);
        Assert.Null(plan.AutoDockAfter);
        Assert.Equal(0, plan.DockSweepCount);
        Assert.Null(plan.ExitAfter);
        Assert.Equal(new[] { "10.0.0.5", "2326", "someone" }, rest);
    }

    [Fact]
    public void Parse_BadValues_FallBackToDefaultsWithoutThrowing()
    {
        var plan = AutoProofPlan.Parse(new[]
        {
            "--auto-side=abc", "--auto-target=bogus", "--camera=wing",
            "--autoshots=5,notanumber,20", "--auto-dock-after=-3",
            "--dock-sweep=-1", "--exit-after=x",
        }, out _);

        Assert.Null(plan.AutoSide);
        Assert.Equal(AutoTargetKind.None, plan.Target);
        Assert.Equal("cockpit", plan.Camera);        // invalid camera ignored
        Assert.Equal(new[] { 5.0, 20.0 }, plan.AutoshotSeconds);   // bad token skipped
        Assert.Null(plan.AutoDockAfter);              // negative rejected
        Assert.Equal(0, plan.DockSweepCount);         // negative clamped to 0
        Assert.Null(plan.ExitAfter);
    }

    [Fact]
    public void ParseSecondsList_SortsDeduplicatesAndSkipsMalformed()
    {
        Assert.Equal(new[] { 2.0, 5.0, 10.0 }, AutoProofPlan.ParseSecondsList("10,2,5"));
        Assert.Equal(new[] { 1.5 }, AutoProofPlan.ParseSecondsList("1.5,1.5"));
        Assert.Equal(Array.Empty<double>(), AutoProofPlan.ParseSecondsList("abc,,x"));
        Assert.Equal(new[] { 0.25, 3.0 }, AutoProofPlan.ParseSecondsList("3,0.25,3"));
    }

    // ── auto state machine ────────────────────────────────────────────────────

    [Fact]
    public void Parse_AutoCreditsFlag_SetsCreditsAndActivatesPlan()
    {
        // P577b — the F12 Credits/Licences overlay automation flag (for the
        // real-window credits-screen proof capture).
        var plan = AutoProofPlan.Parse(new[] { "--auto-credits" }, out var rest);
        Assert.True(plan.AutoCredits);
        Assert.True(plan.AnyActive);
        Assert.Empty(rest);

        var off = AutoProofPlan.Parse(new[] { "--auto-credits=false" }, out _);
        Assert.False(off.AutoCredits);
    }

    [Fact]
    public void Machine_EmitsDeterministicInitialActions()
    {
        var plan = AutoProofPlan.Parse(new[] { "--auto-side=3", "--camera=chase", "--hide-hud" }, out _);
        var m = new AutoProofMachine(plan);

        var first = m.ConsumeDue();
        Assert.Contains(first, a => a.Kind == AutoActionKind.SetCamera && a.StrArg == "chase");
        Assert.Contains(first, a => a.Kind == AutoActionKind.HideHud);
        Assert.Contains(first, a => a.Kind == AutoActionKind.PickSide && a.IntArg == 3);
        // One-shot: nothing re-fires without a world change.
        Assert.Empty(m.ConsumeDue());
    }

    [Fact]
    public void Machine_SeatedThenLaunchSequencesReadyGotoTargetShotsExit()
    {
        // Full journey: ready when seated, then (airborne) goto + target, then
        // the after-launch shot schedule, then the exit deadline.
        var plan = AutoProofPlan.Parse(new[]
        {
            "--auto-ready", "--auto-goto", "Alderan", "--auto-target", "station",
            "--autoshots", "5,10", "--exit-after", "60",
        }, out _);
        var m = new AutoProofMachine(plan);
        m.ConsumeDue();                       // initial camera only

        // Not seated → nothing.
        Assert.Empty(m.ConsumeDue());

        m.NoteSeated();
        var afterSeat = m.ConsumeDue();
        Assert.Contains(afterSeat, a => a.Kind == AutoActionKind.SendReady);
        Assert.Empty(m.ConsumeDue());         // ready is one-shot

        // Airborne now; launch clock starts at the tick we call NoteLaunch.
        m.Tick(0.0);
        m.NoteLaunch();
        m.Tick(6.0);
        var airborne = m.ConsumeDue();
        Assert.Contains(airborne, a => a.Kind == AutoActionKind.GotoSector && a.StrArg == "Alderan");
        Assert.Contains(airborne, a => a.Kind == AutoActionKind.SelectTarget && a.IntArg == (int)AutoTargetKind.Station);
        // t=6 ≥ 5 → shot index 0 fired; t=6 < 10 → shot 1 not yet.
        Assert.Contains(airborne, a => a.Kind == AutoActionKind.Autoshot && a.ShotIndex == 0);
        Assert.DoesNotContain(airborne, a => a.Kind == AutoActionKind.Autoshot && a.ShotIndex == 1);
        Assert.DoesNotContain(airborne, a => a.Kind == AutoActionKind.Exit);

        // Advance past the second shot; still before the exit deadline.
        m.Tick(10.0);
        var mid = m.ConsumeDue();
        Assert.Contains(mid, a => a.Kind == AutoActionKind.Autoshot && a.ShotIndex == 1);
        Assert.DoesNotContain(mid, a => a.Kind == AutoActionKind.Exit);

        // Past the exit deadline → Exit once.
        m.Tick(61.0);
        var late = m.ConsumeDue();
        Assert.Contains(late, a => a.Kind == AutoActionKind.Exit);
        Assert.Empty(m.ConsumeDue());         // one-shot
    }

    [Fact]
    public void Machine_GotoAndTargetFireOnceEvenWithManyTicks()
    {
        var plan = AutoProofPlan.Parse(new[] { "--auto-goto=Rigel", "--auto-target=enemy" }, out _);
        var m = new AutoProofMachine(plan);
        m.ConsumeDue();

        m.NoteLaunch();
        for (int i = 0; i < 30; i++)
        {
            m.Tick(i);
            var acts = m.ConsumeDue();
            Assert.True(acts.Count(a => a.Kind == AutoActionKind.GotoSector) <= 1);
            Assert.True(acts.Count(a => a.Kind == AutoActionKind.SelectTarget) <= 1);
        }
        var all = m.ConsumeDue();
        Assert.DoesNotContain(all, a => a.Kind == AutoActionKind.GotoSector);
        Assert.DoesNotContain(all, a => a.Kind == AutoActionKind.SelectTarget);
    }

    [Fact]
    public void Machine_DockedAutoLaunch_LaunchesOnceAndBeginsSweep()
    {
        var plan = AutoProofPlan.Parse(new[] { "--auto-launch", "--dock-sweep=3" }, out _);
        var m = new AutoProofMachine(plan);
        m.ConsumeDue();

        // Not docked → no launch.
        Assert.Empty(m.ConsumeDue());

        m.Tick(0.0);
        m.NoteDocked();
        var docked = m.ConsumeDue();
        Assert.Contains(docked, a => a.Kind == AutoActionKind.LaunchHull);
        Assert.Contains(docked, a => a.Kind == AutoActionKind.SweepHull && a.IntArg == 0);
        // Both are one-shot.
        Assert.Empty(m.ConsumeDue());
    }

    [Fact]
    public void Machine_DockSweep_PacesEachHullByTheIntervalThenFinishes()
    {
        var plan = AutoProofPlan.Parse(new[] { "--dock-sweep=3" }, out _);
        Assert.Equal(1.5, plan.SweepIntervalSeconds);
        var m = new AutoProofMachine(plan);
        m.ConsumeDue();

        m.Tick(0.0);
        m.NoteDocked();
        Assert.Contains(m.ConsumeDue(), a => a.Kind == AutoActionKind.SweepHull && a.IntArg == 0);

        m.Tick(1.6);
        Assert.Contains(m.ConsumeDue(), a => a.Kind == AutoActionKind.SweepHull && a.IntArg == 1);

        m.Tick(3.2);
        Assert.Contains(m.ConsumeDue(), a => a.Kind == AutoActionKind.SweepHull && a.IntArg == 2);

        // No more hulls to step (count 3 exhausted).
        m.Tick(9.0);
        Assert.DoesNotContain(m.ConsumeDue(), a => a.Kind == AutoActionKind.SweepHull);
    }

    [Fact]
    public void Machine_AutoDockAfter_SendsOneHeadToDockAtTheDeadline()
    {
        var plan = AutoProofPlan.Parse(new[] { "--auto-dock-after=12" }, out _);
        var m = new AutoProofMachine(plan);
        m.ConsumeDue();

        m.NoteLaunch();
        m.Tick(5.0);
        Assert.DoesNotContain(m.ConsumeDue(), a => a.Kind == AutoActionKind.HeadToDock);

        m.Tick(12.0);
        var due = m.ConsumeDue();
        Assert.Contains(due, a => a.Kind == AutoActionKind.HeadToDock);
        Assert.Empty(m.ConsumeDue());         // one-shot
    }

    [Fact]
    public void Machine_SecondsSinceLaunch_TracksTheLaunchClock()
    {
        var plan = AutoProofPlan.Parse(new[] { "--exit-after=10" }, out _);
        var m = new AutoProofMachine(plan);
        m.ConsumeDue();

        Assert.Equal(0.0, m.SecondsSinceLaunch);
        m.Tick(100.0);
        m.NoteLaunch();
        m.Tick(104.5);
        Assert.Equal(4.5, m.SecondsSinceLaunch, precision: 3);
    }

    [Fact]
    public void Machine_AutoSuicideAt_SendsOneSuicideThenA_PostKillShot()
    {
        // --auto-suicide-at fires the K-key suicide at the deadline, then a
        // dedicated real-backbuffer shot ~2.5 s later (once the ghost/pod state
        // has been processed and drawn). Both are one-shot.
        var plan = AutoProofPlan.Parse(new[] { "--auto-suicide-at=9" }, out _);
        var m = new AutoProofMachine(plan);
        m.ConsumeDue();
        m.NoteLaunch();
        m.Tick(8.0);
        Assert.DoesNotContain(m.ConsumeDue(), a => a.Kind == AutoActionKind.Suicide);

        m.Tick(9.0);
        var due = m.ConsumeDue();
        Assert.Contains(due, a => a.Kind == AutoActionKind.Suicide);
        Assert.DoesNotContain(due, a => a.Kind == AutoActionKind.Autoshot);

        // Well before the post-kill delay elapses — no second shot yet.
        m.Tick(10.5);
        Assert.DoesNotContain(m.ConsumeDue(),
            a => a.Kind == AutoActionKind.Autoshot
                 && a.ShotIndex == AutoProofMachine.PostKillShotIndex);

        // 2.5 s after the kill the post-kill autoshot fires exactly once.
        m.Tick(11.5);
        var post = m.ConsumeDue();
        Assert.Contains(post,
            a => a.Kind == AutoActionKind.Autoshot
                 && a.ShotIndex == AutoProofMachine.PostKillShotIndex);
        Assert.Empty(m.ConsumeDue());   // one-shot: no repeat suicide nor shot

        m.Tick(20.0);
        Assert.DoesNotContain(m.ConsumeDue(), a => a.Kind == AutoActionKind.Suicide);
    }

    [Fact]
    public void Machine_AutoSuicideAt_TwoSuicides_TakeOnePostKillShotAfterTheLast()
    {
        // Two suicides (eject into the pod at t=9, then destroy the pod → ghost
        // at t=13) yield exactly two Suicide actions and ONE post-kill shot
        // taken 2.5 s after the LAST one.
        var plan = AutoProofPlan.Parse(new[] { "--auto-suicide-at=9,13" }, out _);
        var m = new AutoProofMachine(plan);
        m.ConsumeDue();
        m.NoteLaunch();

        m.Tick(9.0);
        Assert.Equal(1, m.ConsumeDue().Count(a => a.Kind == AutoActionKind.Suicide));

        // Just after the first, before the second: no post-kill shot yet.
        m.Tick(11.0);
        Assert.DoesNotContain(m.ConsumeDue(),
            a => a.Kind == AutoActionKind.Autoshot
                 && a.ShotIndex == AutoProofMachine.PostKillShotIndex);

        m.Tick(13.0);
        Assert.Equal(1, m.ConsumeDue().Count(a => a.Kind == AutoActionKind.Suicide));

        // 2.5 s after the LAST suicide: exactly one post-kill shot, no more suicides.
        m.Tick(15.5);
        var post = m.ConsumeDue();
        Assert.Equal(1, post.Count(
            a => a.Kind == AutoActionKind.Autoshot
                 && a.ShotIndex == AutoProofMachine.PostKillShotIndex));
        Assert.Empty(m.ConsumeDue());
    }
}
