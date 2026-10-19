using Allegiance.ClientCore.Training;
using Allegiance.SinglePlayer;
using static Allegiance.ClientCore.Training.TrainingMissionIds;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// TR-2 — the Game1 slice that drives a native training mission against the live client.
/// <see cref="BeginTrainingMission"/> starts the mission (Basic Flight / Basic Weaponry / Enemy
/// Engagement in TR-3) on the training site bound to the real session (see
/// <see cref="ClientTrainingSite"/>); <see cref="UpdateTraining"/> pumps the mission each frame and
/// tears it down on completion; the HUD overlay routed by <see cref="SetHudOverlayAction"/> is rendered
/// by the flight HUD. Inert with no session.
/// </summary>
public sealed partial class Game1
{
    private TrainingSession? _trainingSession;
    private TrainingHost? _embeddedHost;
    private string _trainingError = "";
    private double _trainingClock;

    /// <summary>Whether a training mission is currently running on this client.</summary>
    private bool TrainingActive => _trainingSession is { Finished: false };

    /// <summary>Begin a native training mission against the embedded host.</summary>
    private void BeginTrainingMission(int missionId)
    {
        _trainingError = "";
        if (missionId is not (Tm2BasicFlight or Tm3BasicWeaponry or Tm4EnemyEngagement
            or Tm5CommandView or Tm6PracticeArena or Tm8Nanite or Tm10FreeFlight))
        {
            _trainingError = "That mission isn't playable yet; it's coming soon.";
            Console.WriteLine($"[training] {_trainingError}");
            return;
        }

        // The embedded training host realises the mission's universe/creation actions (TR-2b). The
        // variant selects the native map/hull/start-sector (M2/M3: training_1/1010; M4: training_3/1030;
        // M5: training_5/1050; M6+M10: training_6/2080; M8: training_nan/1051).
        var variant = missionId switch
        {
            Tm2BasicFlight => TrainingHost.BasicFlight,
            Tm3BasicWeaponry => TrainingHost.BasicWeaponry,
            Tm4EnemyEngagement => TrainingHost.EnemyEngagement,
            Tm5CommandView => TrainingHost.CommandView,
            Tm6PracticeArena => TrainingHost.PracticeArena,
            Tm8Nanite => TrainingHost.Nanite,
            Tm10FreeFlight => TrainingHost.FreeFlight,
            _ => TrainingHost.BasicFlight,
        };
        _embeddedHost = TrainingHost.Start(variant);

        var facilities = new ClientTrainingFacilities
        {
            World = () => _session.World,
            Session = _session,
            ClockSeconds = () => _trainingClock,
            OwnOrientation = () =>
            {
                if (!_ownSim.HaveHull) return null;
                var o = _ownSim.Orientation;
                return (F: ToSys(o.GetForward()), U: ToSys(o.GetUp()), R: ToSys(o.GetRight()));
            },
            Host = _embeddedHost,
        };

        var site = new ClientTrainingSite(facilities);
        var mission = missionId switch
        {
            Tm2BasicFlight => (TrainingMission)new Mission2(site),
            Tm3BasicWeaponry => new Mission3(site),
            Tm4EnemyEngagement => new Mission4(site),
            Tm5CommandView => new Mission5(site),
            Tm6PracticeArena => new Mission6(site),
            Tm8Nanite => new Mission8(site),
            Tm10FreeFlight => new Mission10(site),
            _ => throw new InvalidOperationException(),
        };
        // Bind the active mission to the embedded host so Mission 6's kill goals read the real
        // kill-credit events the server raises (ShipKilled routing).
        _embeddedHost.SetActiveMission(mission);
        var session = new TrainingSession(site, mission);

        TrainingController.BeginMission();
        session.Start();
        _trainingSession = session;
        _trainingClock = 0;

        // Native marks training as begun so the first-run suggestion stops appearing
        // (Ui.ShowStartupTrainingSuggestion cleared, introscreen.cpp:1055 / trainingscreen.cpp:169).
        var prefs = IntroPreferences.Load();
        if (prefs.ShowStartupTrainingSuggestion)
        {
            prefs.ShowStartupTrainingSuggestion = false;
            prefs.Save();
        }

        _returnToIntroOnFailure = true;
        Console.WriteLine($"[training] Mission {missionId} ({variant.GameName}) started on the embedded host");
    }

    /// <summary>Per-frame pump of the active training mission (native TrainingMission::Frame).</summary>
    private void UpdateTraining(double dt)
    {
        if (_trainingSession is not { } session) return;
        _trainingClock += dt;
        if (session.Advance())
        {
            Console.WriteLine("[training] mission complete");
            TrainingController.EndMission();
            _embeddedHost?.Dispose();
            _embeddedHost = null;
            _trainingSession = null;
            _trainingError = "";
            _introView = IntroView.SinglePlayer;   // back to the chooser
            _state = ClientState.Intro;
        }
    }

    /// <summary>Route a real key press from the flight input into the active mission (RecordKeyPress,
    /// trainingmission.cpp:342-413). Returns whether the press was consumed.</summary>
    private bool RecordTrainingKey(int key)
    {
        return _trainingSession is { } session && session.RecordKeyPress(key);
    }

    private static System.Numerics.Vector3 ToSys(Allegiance.Core.Collision.Vector3 v)
        => new(v.X, v.Y, v.Z);
}
