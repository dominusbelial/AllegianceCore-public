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
    private static void StepUntil(FakeTrainingSite site, Mission2 mission, Func<bool> until, int maxSteps)
    {
        var playerObj = (FakeObject)site.PlayerShip;
        for (int i = 0; i < maxSteps && !until(); i++)
        {
            site.Advance(0.016f);
            playerObj.Forward = Vector3.Normalize(CommanderHome - playerObj.Position);
            mission.Frame();
        }
    }

    private static int CountMessages(FakeTrainingSite site, string fragment)
        => site.Messages.Count(m => m.Contains(fragment, StringComparison.Ordinal));

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
