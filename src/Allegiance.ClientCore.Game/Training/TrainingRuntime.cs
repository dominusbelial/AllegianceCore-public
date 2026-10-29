using Allegiance.ClientCore.Training;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>TR-2 — the client's current training HUD overlay id
/// (<c>SetHudOverlayAction</c> → <c>SetTrainingOverlay</c>, sethudoverlayaction.cpp:32).
/// 0 = none; the overlay ids are <see cref="TrainingMissionIds.Overlay*"/>.</summary>
public static class TrainingOverlay
{
    public static volatile int Current;
}

/// <summary>TR-2 — the client's current radar LOD mode id, the <see cref="Rendering.RadarMode"/>
/// integer read by <c>GetRadarLODCondition</c> (getradarlodcondition.cpp:31 —
/// <c>GetWindow()->GetRadarMode()</c>). Updated by the flight-input radar cycle
/// (Game1.FlightInput.cs:1906) so the training condition reads the real window mode.</summary>
public static class TrainingRadarLod
{
    public static volatile int Current;
}

/// <summary>TR-2 — central sink for training-host actions that need the embedded server's
/// training universe (outside this session's scope). Kept so the binding compiles and the
/// no-ops are explicit and auditable rather than silent.</summary>
public static class TrainingHostLog
{
    public static readonly System.Collections.Concurrent.ConcurrentQueue<string> Entries = new();
    public static void Log(string message)
    {
        Entries.Enqueue(message);
        System.Console.WriteLine($"[training-host] {message}");
    }
    public static int Count => Entries.Count;
}

/// <summary>
/// TR-2 — drives one native training mission against the live client each frame. Owns the
/// mission + its <see cref="ClientTrainingSite"/>; <see cref="Advance"/> pumps the mission
/// loop (native <c>TrainingMission::Frame</c>) and flags completion, and key presses from the
/// real input path are routed through <see cref="RecordKeyPress"/> so the key-disable switch
/// (trainingmission.cpp:342-413) applies.
/// </summary>
public sealed class TrainingSession
{
    private readonly ClientTrainingSite _site;
    private readonly TrainingMission _mission;
    private string? _lastGoal;
    private bool _wasDocked;

    public TrainingSession(ClientTrainingSite site, TrainingMission mission)
    {
        _site = site;
        _mission = mission;
    }

    public TrainingMission Mission => _mission;
    public ClientTrainingSite Site => _site;
    public bool Completed { get; private set; }
    public bool Finished { get; private set; }

    /// <summary>True once the mission's <c>Start()</c> (native CreateMission) has run.</summary>
    public bool Started { get; private set; }

    public void Start()
    {
        _mission.Start();
        Started = true;
        LogProgress();
    }

    /// <summary>Advance the mission by one frame (native Frame()). Returns true once complete.</summary>
    public bool Advance()
    {
        if (Finished) return true;
        if (!Started) return false;   // the mission starts once the player ship exists (Game1.UpdateTraining)
        // Native trekigc.cpp:4131 lets the training mission handle landing first;
        // Mission2/3/4 terminate instead of opening an ordinary loadout lesson.
        bool docked = _site.PlayerDocked;
        if (docked && !_wasDocked) _mission.ShipLanded();
        _wasDocked = docked;
        if (_mission.Frame())
        {
            Completed = true;
            Finished = true;
        }
        LogProgress();
        return Finished;
    }

    private void LogProgress()
    {
        string goal = _mission.ActiveGoalPath;
        if (goal == _lastGoal) return;
        _lastGoal = goal;
        Console.WriteLine($"[training] goal {goal} (mission {_mission.GetMissionID()})");
    }

    /// <summary>Route a key press from the real input path through the mission's RecordKeyPress.</summary>
    public bool RecordKeyPress(int key) => _mission.RecordKeyPress(key);

    public void Terminate()
    {
        _mission.Terminate();
        Finished = true;
    }
}
