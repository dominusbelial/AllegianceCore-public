using System.Numerics;
using Allegiance.ClientCore.Training;
using Allegiance.Core.Sim;
using static Allegiance.ClientCore.Training.TrainingMissionIds;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P885 — Mission 4 (Enemy Engagement) gates on the native TrekKey ids. Native gates the cloak
/// lesson on <c>GetKeyCondition(TK_ToggleCloak)</c> (training/mission4.cpp:707) and the miner
/// engagement on <c>GetKeyCondition(TK_TargetEnemy)</c> (mission4.cpp:798). The port now uses the
/// shared native ids (<see cref="TrainingMissionIds.KeyToggleCloak"/> = 94, igc.h:317;
/// <see cref="TrainingMissionIds.KeyTargetEnemy"/> = 80, igc.h:303) instead of the invented 111/112
/// ids Mission 4 carried before P885. These tests drive Mission 4 against
/// <see cref="FakeTrainingSite"/> (phased like <see cref="TrainingMission2Tests"/>) and prove the
/// cloak and target-enemy goals complete on the native numbers and do <em>not</em> complete on the
/// retired 111/112 ids.
/// </summary>
[Collection("Training")]
public class P885Mission4NativeKeysTests
{
    private const string CloakPrompt = "Press the G key to activate your cloak.";
    private const string TargetEnemyPrompt = "Target an enemy miner with the F key";
    private const string Goal08Prompt = "Press the INSERT key to accept the command.";

    private static FakeTrainingSite BuildSite()
    {
        var site = new FakeTrainingSite();
        // The aleph stand-in (Ship-type target carrying the aleph's object id 1030) parked at the
        // goal-3 waypoint position (800,0,0) so the "fly to the aleph" radius gate resolves.
        site.AddObject(ObjectType.Ship, 1030, new Vector3(800f, 0f, 0f), Vector3.UnitX);
        return site;
    }

    private static int Count(FakeTrainingSite site, string fragment)
        => site.Messages.Count(m => m.Contains(fragment, StringComparison.Ordinal));

    private static bool StepUntil(FakeTrainingSite site, Mission4 mission, Func<bool> until, int maxSteps)
    {
        for (int i = 0; i < maxSteps && !until(); i++)
        {
            site.Advance(0.016f);
            mission.Frame();
        }
        return until();
    }

    /// <summary>Drive the phased driver from mission start to the goal-6 cloak prompt.</summary>
    private static (FakeTrainingSite Site, Mission4 Mission) DriveToCloakPrompt()
    {
        var site = BuildSite();
        var mission = new Mission4(site);
        TrainingController.BeginMission();
        mission.Start();

        // Goal 2: chat to All, then two SPACEBAR acknowledgements.
        Assert.True(StepUntil(site, mission, () => Count(site, "Press the ENTER key to begin a chat") >= 1, 6000),
            "chat prompt never appeared");
        site.SendChat(ChatEveryone);
        Assert.True(StepUntil(site, mission, () => Count(site, "Press the SPACEBAR") >= 1, 6000),
            "SPACEBAR prompt 1 never appeared");
        mission.RecordKeyPress(KeyFireBooster);
        Assert.True(StepUntil(site, mission, () => Count(site, "Press the SPACEBAR") >= 2, 6000),
            "SPACEBAR prompt 2 never appeared");
        mission.RecordKeyPress(KeyFireBooster);

        // Goal 3: the mission's own queued goto makes the accept gate already true (the skip rule,
        // goal.cpp:40, elides that prompt on the fake site); stand on the waypoint (800,0,0) so the
        // radius goal completes.
        ((FakeObject)site.PlayerShip).Position = new Vector3(800f, 0f, 0f);

        // Goal 4: its command-clearing start actions run first, so the accept gate needs a fresh
        // goto — wait for the (first) INSERT-or prompt, then accept it.
        Assert.True(StepUntil(site, mission, () => Count(site, "Press the INSERT or '[' key") >= 1, 6000),
            "goal 4 accept prompt never appeared");
        site.IssueCommand(site.PlayerShip.Ref, 0, null, CmdGoto);

        // Goal 5: the aleph-radius gate is already true (we stand on the aleph stand-in), so the
        // native skip rule elides its prompt; wait for the transit prompt and then move to Mars.
        Assert.True(StepUntil(site, mission, () => Count(site, "Flying through the aleph") >= 1, 6000),
            "goal 5 transit prompt never appeared; goal=" + mission.ActiveGoalPath
            + "; msgs=[" + string.Join(" | ", site.Messages.TakeLast(6)) + "]");
        ((FakeObject)site.PlayerShip).SectorId = 1031;

        // Goal 6: eyeball goals, then the cloak prompt.
        Assert.True(StepUntil(site, mission, () => Count(site, CloakPrompt) >= 1, 6000),
            $"cloak prompt never appeared (goal {mission.ActiveGoalPath})");
        return (site, mission);
    }

    [Fact]
    public void Mission4_cloak_and_target_enemy_goals_complete_on_the_native_trek_keys()
    {
        var (site, mission) = DriveToCloakPrompt();

        // The real client path delivers TK_ToggleCloak = 94 (igc.h:317) through the
        // TrainingInputGate into TrainingMission.RecordKeyPress — the same number the
        // GetKeyCondition(mission4.cpp:707) gate waits for.
        Assert.Equal(94, KeyToggleCloak);
        mission.RecordKeyPress(KeyToggleCloak);
        Assert.True(StepUntil(site, mission, () => Count(site, TargetEnemyPrompt) >= 1, 6000),
            $"cloak goal did not complete on TK_ToggleCloak=94 (goal {mission.ActiveGoalPath})");

        // TK_TargetEnemy = 80 (igc.h:303) completes the miner-engagement gate (mission4.cpp:798);
        // afterwards the mission reaches goal 8's INSERT prompt.
        Assert.Equal(80, KeyTargetEnemy);
        mission.RecordKeyPress(KeyTargetEnemy);
        Assert.True(StepUntil(site, mission, () => Count(site, Goal08Prompt) >= 1, 6000),
            $"target-enemy goal did not complete on TK_TargetEnemy=80 (goal {mission.ActiveGoalPath})");
        Assert.StartsWith("8", mission.ActiveGoalPath);
    }

    [Fact]
    public void Mission4_cloak_goal_does_NOT_complete_on_the_retired_111_112_ids()
    {
        var (site, mission) = DriveToCloakPrompt();

        mission.RecordKeyPress(111);
        mission.RecordKeyPress(112);
        StepUntil(site, mission, () => Count(site, TargetEnemyPrompt) >= 1, 800);
        Assert.False(Count(site, TargetEnemyPrompt) >= 1,
            "the cloak goal must not complete on the retired invented ids 111/112");
        Assert.StartsWith("6", mission.ActiveGoalPath);

        // The gate is not wedged: the native key still completes it.
        mission.RecordKeyPress(KeyToggleCloak);
        Assert.True(StepUntil(site, mission, () => Count(site, TargetEnemyPrompt) >= 1, 6000),
            $"cloak goal did not complete on the native key afterwards (goal {mission.ActiveGoalPath})");
    }

    [Fact]
    public void Mission4_target_enemy_goal_does_NOT_complete_on_the_retired_111_112_ids()
    {
        var (site, mission) = DriveToCloakPrompt();
        mission.RecordKeyPress(KeyToggleCloak);
        Assert.True(StepUntil(site, mission, () => Count(site, TargetEnemyPrompt) >= 1, 6000),
            "cloak goal did not complete on the native key");

        mission.RecordKeyPress(111);
        mission.RecordKeyPress(112);
        StepUntil(site, mission, () => Count(site, Goal08Prompt) >= 1, 800);
        Assert.False(Count(site, Goal08Prompt) >= 1,
            "the target-enemy goal must not complete on the retired invented ids 111/112");
        Assert.StartsWith("7", mission.ActiveGoalPath);

        // The gate is not wedged: the native key still completes it.
        mission.RecordKeyPress(KeyTargetEnemy);
        Assert.True(StepUntil(site, mission, () => Count(site, Goal08Prompt) >= 1, 6000),
            $"target-enemy goal did not complete on the native key afterwards (goal {mission.ActiveGoalPath})");
    }
}
