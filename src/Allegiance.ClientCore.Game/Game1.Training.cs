using Allegiance.ClientCore.Training;
using static Allegiance.ClientCore.Training.TrainingMissionIds;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// TR-2 — the Game1 slice that drives a native training mission against the live client.
/// <see cref="BeginTrainingMission"/> starts Mission 2 (Basic Flight) on the training site
/// bound to the real session (see <see cref="ClientTrainingSite"/>); <see cref="UpdateTraining"/>
/// pumps the mission each frame and tears it down on completion; the HUD overlay routed by
/// <see cref="SetHudOverlayAction"/> is rendered by the flight HUD. Inert with no session.
/// </summary>
public sealed partial class Game1
{
    private TrainingSession? _trainingSession;
    private string _trainingError = "";
    private double _trainingClock;

    /// <summary>Whether a training mission is currently running on this client.</summary>
    private bool TrainingActive => _trainingSession is { Finished: false };

    /// <summary>Begin a native training mission (Basic Flight in TR-2) against the embedded host.</summary>
    private void BeginTrainingMission(int missionId)
    {
        _trainingError = "";
        if (missionId != Tm2BasicFlight)
        {
            _trainingError = "Only Basic Flight (Mission 2) is playable in TR-2; the rest are coming soon.";
            Console.WriteLine($"[training] {_trainingError}");
            return;
        }

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
        };
        var site = new ClientTrainingSite(facilities);
        var mission = new Mission2(site);
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
        Console.WriteLine($"[training] Mission {missionId} (Basic Flight) started on the embedded host");
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
