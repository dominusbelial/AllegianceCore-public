using Allegiance.ClientCore.Training;

namespace Allegiance.ClientCore.Tests;

/// <summary>A manually-toggled condition so tests can drive evaluation deterministically.</summary>
internal sealed class ManualGate : Condition
{
    public bool Value { get; set; }
    public override bool Start() => Value;
    public override bool Evaluate() => Value;
}

/// <summary>Records how many times <c>Execute</c> ran.</summary>
internal sealed class RecordingAction : Allegiance.ClientCore.Training.Action
{
    public int Executions { get; private set; }
    public override void Execute() => Executions++;
}

/// <summary>
/// Deterministic unit tests for the ported script framework: <c>GoalList</c>/<c>Goal</c> sequencing and
/// skip semantics, the boolean combinators, <c>NTimes</c>/<c>ElapsedTime</c>, and the
/// <c>ConditionalAction</c> immediate/delayed/run-once modes. These pin the native evaluation semantics
/// cited per class.
/// </summary>
[Collection("Training")]
public class TrainingFrameworkTests
{
    [Fact]
    public void GoalList_AdvancesSequentially_And_StartsNext()
    {
        var gateA = new ManualGate { Value = false };
        var gateB = new ManualGate { Value = false };
        var goalA = new Goal(gateA);
        var goalB = new Goal(gateB);
        var bStartMarker = new RecordingAction();
        goalB.AddStartAction(bStartMarker);

        var list = new GoalList();
        list.AddGoal(goalA);
        list.AddGoal(goalB);

        // Start walks: goalA starts incomplete -> the list is incomplete.
        Assert.Equal(Goal.Incomplete, list.Start());

        // goalA not complete yet.
        Assert.Equal(Goal.Incomplete, list.Evaluate());

        // Complete goalA -> the list Stop()s it, advances, and Starts goalB.
        gateA.Value = true;
        Assert.Equal(Goal.Incomplete, list.Evaluate());
        Assert.Equal(1, bStartMarker.Executions); // goalB's start action fired on its Start

        // goalB not complete.
        Assert.Equal(Goal.Incomplete, list.Evaluate());

        // Complete goalB -> list advances to end -> complete.
        gateB.Value = true;
        Assert.Equal(Goal.Complete, list.Evaluate());
    }

    [Fact]
    public void Goal_WhoseSuccessConditionAlreadyTrue_IsSkipped()
    {
        // goal.cpp:40,60 — if the success condition starts true the goal is skipped.
        var goal = new Goal(new TrueCondition());
        Assert.Equal(Goal.Complete, goal.Start());
    }

    [Fact]
    public void Goal_SkipConditionShortCircuits_BeforeStartActions()
    {
        // goal.cpp:43-46 — a true skip-goal condition skips without running start actions.
        var action = new RecordingAction();
        var goal = new Goal(new ManualGate { Value = false });
        goal.AddStartAction(action);
        goal.SetSkipGoalCondition(new TrueCondition());

        Assert.Equal(Goal.Complete, goal.Start());
        Assert.Equal(0, action.Executions);
    }

    [Fact]
    public void Goal_Evaluate_WalksConstraints_EvenWhenIncomplete()
    {
        // goal.cpp:85-86 — constraints are evaluated (for their actions) even when the goal is incomplete.
        var constraint = new ConditionalAction(new TrueCondition(), new RecordingAction());
        var action = new RecordingAction();
        var goal = new Goal(new ManualGate { Value = false });
        goal.AddConstraintCondition(constraint);
        goal.AddStartAction(action);

        goal.Start();
        goal.Evaluate();
        Assert.Equal(1, action.Executions);          // start action fired exactly once, at Start
        // the immediate conditional-action constraint fires in BOTH the Start constraint-walk
        // (conditionlist.cpp:40-42) and the Evaluate constraint-walk (conditionlist.cpp:70-72)
        Assert.Equal(2, ((RecordingAction)((ConditionalAction)constraint).InnerAction).Executions);
    }

    [Fact]
    public void BooleanCombinators_MatchTruthTables()
    {
        // andcondition.cpp:52-58, orcondition.cpp:52-58, predicate.cpp:34-49
        Assert.False(new AndCondition(new ManualGate { Value = false }, new ManualGate { Value = true }).Evaluate());
        Assert.True(new AndCondition(new ManualGate { Value = true }, new ManualGate { Value = true }).Evaluate());
        Assert.True(new OrCondition(new ManualGate { Value = false }, new ManualGate { Value = true }).Evaluate());
        Assert.False(new OrCondition(new ManualGate { Value = false }, new ManualGate { Value = false }).Evaluate());
        Assert.True(NotCondition.Of(new ManualGate { Value = false }).Evaluate());
        Assert.False(NotCondition.Of(new ManualGate { Value = true }).Evaluate());
        Assert.True(new FalseCondition().Evaluate() == false);
        Assert.True(new TrueCondition().Evaluate());
    }

    [Fact]
    public void NTimesCondition_FiresExactlyAtTarget_ThenResetsUnlessSticky()
    {
        // ntimescondition.cpp:36-59
        var gate = new ManualGate();
        var nonSticky = new NTimesCondition(gate, 3);
        nonSticky.Start();

        // three successive true evaluations trip it; the count resets to 0 afterwards
        gate.Value = true;
        Assert.False(nonSticky.Evaluate());
        Assert.False(nonSticky.Evaluate());
        Assert.True(nonSticky.Evaluate());   // reached 3
        Assert.False(nonSticky.Evaluate());  // count reset -> no fire until 3 more
        Assert.False(nonSticky.Evaluate());
        Assert.True(nonSticky.Evaluate());   // second cycle

        // sticky keeps the count latched so it stays true; start with the gate off so the count is 0
        gate.Value = false;
        var sticky = new NTimesCondition(gate, 2, sticky: true);
        sticky.Start();
        gate.Value = true;
        Assert.False(sticky.Evaluate());
        Assert.True(sticky.Evaluate());
        Assert.True(sticky.Evaluate()); // remains true (latched)
    }

    [Fact]
    public void ConditionalAction_Immediate_ExecutesInsideStart()
    {
        // conditionalaction.cpp:38-43 — immediate mode runs the action during Start.
        var action = new RecordingAction();
        var ca = new ConditionalAction(new ManualGate { Value = true }, action);
        ca.Start();
        Assert.Equal(1, action.Executions);
    }

    [Fact]
    public void ConditionalAction_Delayed_FiresOnFollowingEvaluate()
    {
        // conditionalaction.cpp:54-84 — delayed mode runs the action the pass AFTER the condition is true.
        var gate = new ManualGate();
        var action = new RecordingAction();
        var ca = new ConditionalAction(gate, action, ConditionalAction.DelayedExecution);
        gate.Value = true;
        ca.Start();
        Assert.Equal(0, action.Executions); // not yet

        ca.Evaluate();
        Assert.Equal(1, action.Executions); // fired on the following pass
    }

    [Fact]
    public void ConditionalAction_RunOnce_StopsAfterSingleFire()
    {
        var gate = new ManualGate { Value = true };
        var action = new RecordingAction();
        var ca = new ConditionalAction(gate, action, runOnce: true);
        ca.Start();
        Assert.Equal(1, action.Executions);
        ca.Evaluate();
        Assert.Equal(1, action.Executions); // not re-fired
    }

    [Fact]
    public void OneTimeAction_ExecutesOnlyOnce()
    {
        // onetimeaction.cpp:34-41
        var inner = new RecordingAction();
        var oneTime = new OneTimeAction(inner);
        oneTime.Execute();
        oneTime.Execute();
        oneTime.Execute();
        Assert.Equal(1, inner.Executions);
    }

    [Fact]
    public void ElapsedTimeCondition_FiresOnce_AfterDuration()
    {
        // elapsedtimecondition.h:30 — NTimes(Periodic(True),2,true)
        var site = new FakeTrainingSite();
        var elapsed = ElapsedTimeCondition.Create(1.0f, site);
        Assert.False(elapsed.Start());
        Assert.False(elapsed.Evaluate()); // not yet
        site.Advance(1.1f);
        Assert.True(elapsed.Evaluate());
        Assert.True(elapsed.Evaluate()); // sticky -> stays true
    }
}
