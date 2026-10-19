using System.Numerics;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.Training;
using static Allegiance.ClientCore.Training.TrainingMissionIds;

namespace Allegiance.ClientCore.Tests;

/// <summary>TR-2 — native-fidelity tests for the deferred client items: the ship-sim
/// <c>turnToFace</c> math (TurnToAction.cpp:62 / common.cpp:152-252), the full
/// <c>RecordKeyPress</c> key-disable switch + command-view gating (trainingmission.cpp:342-413),
/// and the Single Player chooser + training screen models.</summary>
public class TR2TrainingTests
{
    private static readonly TrainingTurnTo.TurnHull Hull = new(
        Mass: 1000f, TorqueMultiplier: 1f,
        TurnTorqueYaw: 1_000_000f, TurnTorquePitch: 1_000_000f,
        MaxTurnRateYaw: 1f, MaxTurnRatePitch: 1f,
        CurrentTurnRateYaw: 0f, CurrentTurnRatePitch: 0f);

    // Identity ship: forward (0,0,-1), up (0,1,0), right (1,0,0) (orientation.cpp identity).
    private static readonly Vector3 Fwd = new(0, 0, -1);
    private static readonly Vector3 Up = new(0, 1, 0);
    private static readonly Vector3 Right = new(1, 0, 0);

    [Fact]
    public void TurnToFace_TargetDeadAhead_ReturnsZeroCorrection()
    {
        var (yaw, pitch, roll) = TrainingTurnTo.Compute(Vector3.Zero, new Vector3(0, 0, -50), Fwd, Up, Right, 0.1f, Hull);
        Assert.Equal(0f, roll);
        Assert.InRange(yaw, -0.001f, 0.001f);
        Assert.InRange(pitch, -0.001f, 0.001f);
    }

    [Fact]
    public void TurnToFace_TargetToTheRight_YawsRight()
    {
        // Target at +X is to the ship's right; the corrective yaw must be negative (left-handed
        // frame: positive yaw swings the nose toward -X, so a +X target needs negative yaw).
        var (yaw, _, _) = TrainingTurnTo.Compute(Vector3.Zero, new Vector3(50, 0, 0), Fwd, Up, Right, 0.1f, Hull);
        Assert.True(yaw < 0, $"expected rightward (negative) yaw, got {yaw}");
    }

    [Fact]
    public void TurnToFace_TargetToTheLeft_YawsLeft()
    {
        var (yaw, _, _) = TrainingTurnTo.Compute(Vector3.Zero, new Vector3(-50, 0, 0), Fwd, Up, Right, 0.1f, Hull);
        Assert.True(yaw > 0, $"expected leftward (positive) yaw, got {yaw}");
    }

    [Fact]
    public void TurnToFace_TargetDirectlyBehind_YawsHardAtMax()
    {
        // common.cpp:168-176 — a target almost exactly behind just yaws at max (1.0).
        var (yaw, pitch, _) = TrainingTurnTo.Compute(Vector3.Zero, new Vector3(0, 0, 50), Fwd, Up, Right, 0.1f, Hull);
        Assert.Equal(1.0f, yaw);
        Assert.Equal(0f, pitch);
    }

    [Fact]
    public void TurnToFace_ApplyingCorrection_ReducesAngleToTarget()
    {
        // The computed corrective turn, applied to the ship's orientation, must point it more
        // toward a side target (the training TurnToAction damps by 0.5 afterwards, TurnToAction.cpp:63-65).
        var target = new Vector3(50, 0, 0);
        var (yaw, pitch, _) = TrainingTurnTo.Compute(Vector3.Zero, target, Fwd, Up, Right, 0.5f, Hull);
        var o = new Allegiance.Core.Sim.Orientation();
        o.Set(CV(Fwd), CV(Up));
        o.Yaw(yaw * 0.5f);
        o.Pitch(pitch * 0.5f);
        var before = AngleBetween(Fwd, Vector3.Normalize(target));
        var after = AngleBetween(new Vector3(o.GetForward().X, o.GetForward().Y, o.GetForward().Z), Vector3.Normalize(target));
        Assert.True(after < before, $"applying the correction should reduce the angle ({after} vs {before})");
    }

    private static float AngleBetween(Vector3 a, Vector3 b)
    {
        var d = Math.Clamp(a.X * b.X + a.Y * b.Y + a.Z * b.Z, -1f, 1f);
        return MathF.Acos(d);
    }

    private static Allegiance.Core.Collision.Vector3 CV(Vector3 v) => new(v.X, v.Y, v.Z);

    // ── RecordKeyPress key-disable switch + command-view gating ─────────────

    private sealed class TestMission : TrainingMission
    {
        private readonly int _id;
        private readonly bool _cmdView;
        public TestMission(ITrainingSite site, int id, bool cmdView) : base(site) { _id = id; _cmdView = cmdView; }
        public override int GetMissionID() => _id;
        public override int GetStartSectorId() => 1010;
        public override bool CommandViewEnabled => _cmdView;
        protected override void CreateUniverse() { }
        protected override Condition CreateMission() => new Goal(new TrueCondition());
    }

    [Fact]
    public void RecordKeyPress_HardDisabledKeys_AreSwallowedExceptFreeFlight()
    {
        var site = new FakeTrainingSite();
        var m2 = new TestMission(site, Tm2BasicFlight, cmdView: false);

        Assert.False(m2.RecordKeyPress(KeySuicide), "suicide must be swallowed in Mission 2");
        Assert.False(m2.RecordKeyPress(KeyConModeInvest), "invest must be swallowed in Mission 2");
        Assert.False(site.ConsumeKey(KeySuicide), "swallowed key must not reach the key condition queue");

        Assert.True(m2.RecordKeyPress(KeyFireBooster), "SPACEBAR is an allowed mission key");
        Assert.True(site.ConsumeKey(KeyFireBooster), "allowed key must reach the key condition queue");
    }

    [Fact]
    public void RecordKeyPress_CommandViewKeys_GatedOnCommandViewEnabled()
    {
        var site = new FakeTrainingSite();
        var noView = new TestMission(site, Tm2BasicFlight, cmdView: false);
        Assert.False(noView.RecordKeyPress(KeyViewCommand), "command-view key blocked without command view");
        Assert.False(noView.RecordKeyPress(KeyToggleAutoPilot), "autopilot key blocked without command view");

        var withView = new TestMission(site, Tm3BasicWeaponry, cmdView: true);
        Assert.True(withView.RecordKeyPress(KeyViewCommand), "command-view key allowed when enabled");
        Assert.True(site.ConsumeKey(KeyViewCommand));
    }

    [Fact]
    public void RecordKeyPress_FreeFlight_PassesHardDisabledAndCommandViewKeys()
    {
        var site = new FakeTrainingSite();
        // Free Flight (mission 10) skips the hard-disable set; command view enables the rest.
        var free = new TestMission(site, Tm10FreeFlight, cmdView: true);
        Assert.True(free.RecordKeyPress(KeySuicide), "Free Flight may suicide");
        Assert.True(site.ConsumeKey(KeySuicide));
        Assert.True(free.RecordKeyPress(KeyToggleAutoPilot));
        Assert.True(site.ConsumeKey(KeyToggleAutoPilot));
    }

    [Fact]
    public void RecordKeyPress_PauseKey_ConsumedWithoutRouting()
    {
        var site = new FakeTrainingSite();
        var m2 = new TestMission(site, Tm2BasicFlight, cmdView: false);
        Assert.True(m2.RecordKeyPress(KeyPauseTm), "pause key is consumed");
        Assert.False(site.ConsumeKey(KeyPauseTm), "pause key does not route to a key condition");
    }

    // ── Single Player chooser + training screen models ──────────────────────

    [Fact]
    public void Chooser_OffersTrainingAndSkirmish_InThatOrder()
    {
        var chooser = new SinglePlayerChooserModel();
        Assert.Equal(2, SinglePlayerChooserModel.RowCount);
        Assert.Equal("Training", SinglePlayerChooserModel.Labels[0]);
        Assert.Equal("Skirmish", SinglePlayerChooserModel.Labels[1]);
        Assert.True(chooser.Activate(), "index 0 = Training");
        chooser.Move(1);
        Assert.False(chooser.Activate(), "index 1 = Skirmish");
        chooser.Move(-1);
        Assert.True(chooser.Activate());
    }

    [Fact]
    public void TrainingScreen_ListsNativeMissions_PlayableThroughEnemyEngagement()
    {
        var screen = new TrainingScreenModel();
        // The native training screen lists missions 1,2,3,4,5,6,8 (mission 7 removed) — trainingscreen.cpp:50-58.
        Assert.Equal(7, screen.Count);
        Assert.Contains(TrainingScreenModel.Missions, m => m.MissionId == Tm1Introduction && !m.Playable);
        Assert.Contains(TrainingScreenModel.Missions, m => m.MissionId == Tm2BasicFlight && m.Playable);
        // TR-3 enables Missions 3 (Basic Weaponry) and 4 (Enemy Engagement); the rest stay coming-soon.
        Assert.Contains(TrainingScreenModel.Missions, m => m.MissionId == Tm3BasicWeaponry && m.Playable);
        Assert.Contains(TrainingScreenModel.Missions, m => m.MissionId == Tm4EnemyEngagement && m.Playable);
        Assert.Contains(TrainingScreenModel.Missions, m => m.MissionId == Tm8Nanite && !m.Playable);
        Assert.All(TrainingScreenModel.Missions.Where(
            m => m.MissionId is not (Tm2BasicFlight or Tm3BasicWeaponry or Tm4EnemyEngagement)),
            m => Assert.False(m.Playable));

        // The playable missions are startable; a coming-soon mission is not.
        screen.Index = Array.FindIndex(TrainingScreenModel.Missions, m => m.MissionId == Tm2BasicFlight);
        Assert.True(screen.CanStart);
        screen.Index = Array.FindIndex(TrainingScreenModel.Missions, m => m.MissionId == Tm3BasicWeaponry);
        Assert.True(screen.CanStart);
        screen.Index = Array.FindIndex(TrainingScreenModel.Missions, m => m.MissionId == Tm4EnemyEngagement);
        Assert.True(screen.CanStart);
        screen.Index = Array.FindIndex(TrainingScreenModel.Missions, m => m.MissionId == Tm5CommandView);
        Assert.False(screen.CanStart);
    }

    [Fact]
    public void IntroPreferences_StartupTrainingSuggestion_DefaultsTrueAndClears()
    {
        var prefs = new IntroPreferences();
        Assert.True(prefs.ShowStartupTrainingSuggestion, "fresh install suggests training (native false == HasTrained)");
        prefs.ShowStartupTrainingSuggestion = false;
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tr2-player-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            prefs.Save(path);
            var loaded = IntroPreferences.Load(path);
            Assert.False(loaded.ShowStartupTrainingSuggestion, "cleared flag persists");
        }
        finally { System.IO.File.Delete(path); }
    }
}
