using System.Numerics;
using Allegiance.ClientCore.Training;
using Allegiance.Core.Sim;
using static Allegiance.ClientCore.Training.TrainingMissionIds;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// Runs Mission 2 (Basic Flight) end-to-end against <see cref="FakeTrainingSite"/>, scripting the
/// player's inputs the way the real cadet would, and asserts the mission completes with the expected
/// narrative milestones in order. Proves the ported goal/condition/action framework and the mission
/// data both evaluate correctly without any host.
///
/// The driver is <em>phased</em>: each gate's input is supplied only <em>after</em> the corresponding
/// prompt has appeared, which keeps the native "skip a goal whose success condition is already true"
/// rule (goal.cpp:40) from spuriously eliding the prompt we are trying to observe.
/// </summary>
[Collection("Training")]
public class TrainingMission2Tests
{
    private const int CommanderShipId = 990;
    private const int StationId = 1010;
    private static readonly Vector3 CommanderHome = new(-200f, -200f, 400f);

    private static FakeTrainingSite BuildSite()
    {
        var site = new FakeTrainingSite();
        // commander (a wingman drone) starts where the waypoint-demo buoy will be spawned
        site.AddObject(ObjectType.Ship, CommanderShipId, CommanderHome, Vector3.UnitX);
        // the home station (id = sector 1010) at the origin
        site.AddObject(ObjectType.Station, StationId, Vector3.Zero, Vector3.UnitX, radius: 50f);
        return site;
    }

    /// <summary>Advance frames (pointing the player at the commander, as goal 2's demo requires) until
    /// <paramref name="until"/> becomes true or <paramref name="maxSteps"/> frames elapse.</summary>
    private static void StepUntil(FakeTrainingSite site, Mission2 mission, Func<bool> until, int maxSteps, float seconds = .016f)
    {
        var playerObj = (FakeObject)site.PlayerShip;
        for (int i = 0; i < maxSteps && !until(); i++)
        {
            site.Advance(seconds);
            playerObj.Forward = Vector3.Normalize(CommanderHome - playerObj.Position);
            mission.Frame();
        }
    }

    private static int CountMessages(FakeTrainingSite site, string fragment)
        => site.Messages.Count(m => m.Contains(fragment, StringComparison.Ordinal));

    [Theory]
    [InlineData("3.6", Tm2_11r, true)]
    [InlineData("4.7", Tm2_16r, true)]
    [InlineData("5.3", Tm2_18r, true)]
    [InlineData("6.16", Tm2_24r, true)]
    [InlineData("7.13", Tm2_33, false)]
    public void Waiting_player_hears_only_the_native_voice_every_thirty_seconds(
        string target, int reminder, bool initialVoice)
    {
        // mission2.cpp:350-355,436-441,475-481,629-634,741-748.
        // trainingmission.cpp:927-932 + periodiccondition.cpp:52-65 and
        // ntimescondition.cpp:36-57: strict >30, drift-free, sticky/unlimited.
        var site = BuildSite();
        var mission = new Mission2(site);
        TrainingController.BeginMission();
        mission.Start();
        foreach (string path in new[] { "3.6", "4.7", "5.3", "6.16", "7.13" })
        {
            // Integral timestamps make the strict boundary exactly representable.
            StepUntil(site, mission, () => mission.ActiveGoalPath == path, 4000, seconds: 1);
            Assert.Equal(path, mission.ActiveGoalPath);
            if (path == target) break;
            AnswerPrompt(path);
        }

        float start = site.TimeSeconds;
        string[] messages = site.Messages.ToArray();
        int sounds = site.PlayedSounds.Count;
        Assert.Equal(initialVoice ? 1 : 0, site.PlayedSounds.Count(id => id == reminder));
        for (int period = 1; period <= 3; period++)
        {
            site.TimeSeconds = start + period * 30;
            mission.Frame();
            Assert.Equal(period - 1, site.PlayedSounds.Count - sounds);
            site.TimeSeconds = MathF.BitIncrement(site.TimeSeconds);
            mission.Frame();
            Assert.Equal(Enumerable.Repeat(reminder, period), site.PlayedSounds.Skip(sounds));
            Assert.Equal(messages, site.Messages); // native never reissues MessageAction
            Assert.Equal(target, mission.ActiveGoalPath);
        }
        AnswerPrompt(target);
        site.Advance(.016f);
        mission.Frame();
        Assert.NotEqual(target, mission.ActiveGoalPath);
        int total = site.PlayedSounds.Count(id => id == reminder);
        site.Advance(31);
        mission.Frame();
        Assert.Equal(total, site.PlayedSounds.Count(id => id == reminder));

        void AnswerPrompt(string path)
        {
            switch (path)
            {
                case "3.6": case "6.16": site.PressKey(KeyFireBooster); break;
                case "4.7": site.SetShipControls(1, new ControlData(0, 0, 0, 1)); break;
                case "5.3": site.SetShipControls(1, new ControlData(0, 0, 0, .3f)); break;
                case "7.13": site.SetStateBit(1, AfterburnerButton); break;
            }
        }
    }

    [Fact]
    public void Mission2_RunsToCompletion_WithExpectedMilestonesInOrder()
    {
        var site = BuildSite();
        var mission = new Mission2(site);
        TrainingController.BeginMission();
        mission.Start();

        // Phase 1 — goal 3 asks the cadet to press SPACEBAR; only then do we press it.
        StepUntil(site, mission, () => CountMessages(site, "Press the SPACEBAR") >= 1, 4000);
        Assert.True(CountMessages(site, "Press the SPACEBAR") >= 1, "goal 3 prompt never appeared");
        site.PressKey(KeyFireBooster);

        // Phase 2 — goal 4 prompts full throttle; set it full.
        StepUntil(site, mission, () => CountMessages(site, "BACKSLASH") >= 1, 4000);
        Assert.True(CountMessages(site, "BACKSLASH") >= 1, "goal 4 prompt never appeared");
        site.SetShipControls(1, new ControlData(0f, 0f, 0f, Throttle: 1.0f));

        // Phase 3 — goal 5 prompts adjusting the throttle gauge; lower it off full.
        StepUntil(site, mission, () => CountMessages(site, "scroll wheel") >= 1, 4000);
        Assert.True(CountMessages(site, "scroll wheel") >= 1, "goal 5 prompt never appeared");
        site.SetShipControls(1, new ControlData(0f, 0f, 0f, Throttle: 0.3f));

        // Phase 4 — goal 6 prompts SPACEBAR again (second occurrence).
        StepUntil(site, mission, () => CountMessages(site, "Press the SPACEBAR") >= 2, 4000);
        Assert.True(CountMessages(site, "Press the SPACEBAR") >= 2, "goal 6 prompt never appeared");
        site.PressKey(KeyFireBooster);

        // Phase 5 — goal 7 prompts holding SPACE to fire the booster; set the afterburner bit.
        StepUntil(site, mission, () => CountMessages(site, "SPACE key") >= 1, 4000);
        Assert.True(CountMessages(site, "SPACE key") >= 1, "goal 7 prompt never appeared");
        site.SetStateBit(1, AfterburnerButton);

        // Phase 6 — goal 8 autopilots home (player is already near the station); then the mission is
        // parked at goal 9 (landing). Trigger the landing hook that ends Mission 2.
        StepUntil(site, mission, () => site.IsAutopilot(1), 4000);
        Assert.True(site.IsAutopilot(1), "autopilot never engaged in goal 8");

        mission.ShipLanded();        // Mission2::ShipLanded -> Terminate
        Assert.True(mission.Frame(), "mission should report completion after landing");

        // The milestones appeared in narrative order.
        Assert.True(
            site.Messages.IndexOf(site.Messages.First(m => m.Contains("BACKSLASH"))) >
            site.Messages.IndexOf(site.Messages.First(m => m.Contains("Press the SPACEBAR"))),
            "goal 3 prompt must precede goal 4 prompt");
        Assert.True(
            site.Messages.IndexOf(site.Messages.First(m => m.Contains("SPACE key"))) >
            site.Messages.IndexOf(site.Messages.First(m => m.Contains("scroll wheel"))),
            "goal 7 prompt must come after goal 5 prompt");
    }

    [Fact]
    public void Mission2_Goal1_LocksAllControls_WhileIntroPlays()
    {
        var site = BuildSite();
        var mission = new Mission2(site);
        TrainingController.BeginMission();
        mission.Start();

        var playerObj = (FakeObject)site.PlayerShip;
        playerObj.Forward = Vector3.Normalize(CommanderHome - playerObj.Position);

        // Step ~3.5s (past the 2.0 + 0.7 + 0.2 + 0.5 intro waits) but before goal 3 releases controls.
        for (int i = 0; i < 300; i++)
        {
            site.Advance(0.012f);
            mission.Frame();
        }

        // goal 1 zeroes every control scalar (mission2.cpp:158-163); goal 3 (which lifts yaw/pitch/roll)
        // has not started yet, so all four are still 0.
        Assert.Equal(0.0f, TrainingControlState.AllowableControlScalars[0]);
        Assert.Equal(0.0f, TrainingControlState.AllowableControlScalars[3]);
    }

    [Fact]
    public void Motion_demo_clears_forced_throttle_before_all_axes_are_reenabled()
    {
        // training/mission2.cpp:585-590,608-613: an empty SetControlsAction,
        // then all four scalars=1. A throttle-preserving spin at 6.9 is wrong.
        var site = BuildSite();
        var mission = new Mission2(site);
        TrainingController.BeginMission(); mission.Start();
        StepUntil(site, mission, () => mission.ActiveGoalPath == "3.6", 4000);
        site.PressKey(KeyFireBooster);
        StepUntil(site, mission, () => mission.ActiveGoalPath == "4.7", 4000);
        site.SetShipControls(1, new ControlData(0, 0, 0, 1));
        StepUntil(site, mission, () => mission.ActiveGoalPath == "5.3", 4000);
        site.SetShipControls(1, new ControlData(0, 0, 0, .3f));
        StepUntil(site, mission, () => mission.ActiveGoalPath == "6.9", 4000);
        Assert.Equal("6.9", mission.ActiveGoalPath);
        Assert.All(TrainingControlState.SetInputControls, value => Assert.Equal(TrainingControlState.Na, value));
        StepUntil(site, mission, () => mission.ActiveGoalPath == "6.16", 4000);
        Assert.Equal("6.16", mission.ActiveGoalPath);
        var full = new ControlData(.4f, -.3f, .2f, 1);
        Assert.Equal(full, TrainingController.ApproveControls(full));
        Assert.Equal(-1, TrainingController.ApproveControls(full with { Throttle = -1 }).Throttle);
    }

    [Fact]
    public void GetControlActiveCondition_Throttle_FullForwardTranslation()
    {
        // getcontrolactivecondition.cpp:44-51 — throttle translated to 0..1 before the 0.1-band test
        var site = new FakeTrainingSite();
        var player = site.AddObject(ObjectType.Ship, 7, Vector3.Zero, Vector3.UnitX);

        site.SetShipControls(7, new ControlData(0f, 0f, 0f, Throttle: 1.0f));
        Assert.True(new GetControlActiveCondition(site, player.Ref, 3, 1.0f).Evaluate());

        site.SetShipControls(7, new ControlData(0f, 0f, 0f, Throttle: 0.0f)); // half -> 0.5, outside 0.1 of 1.0
        Assert.False(new GetControlActiveCondition(site, player.Ref, 3, 1.0f).Evaluate());
    }

    [Fact]
    public void GetControlActiveCondition_UseInputControls_Path()
    {
        var site = new FakeTrainingSite();
        var player = site.AddObject(ObjectType.Ship, 8, Vector3.Zero, Vector3.UnitX);
        site.InputControls = new ControlData(0f, 0.9f, 0f, -1f);
        // bUseInputControls=true reads g_inputControls (the raw player input)
        Assert.True(new GetControlActiveCondition(site, player.Ref, 1, 0.9f, useInputControls: true).Evaluate());
    }
}
