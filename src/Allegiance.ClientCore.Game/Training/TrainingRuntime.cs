using Allegiance.ClientCore.Training;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>TR-2 — the client's current training HUD overlay id
/// (<c>SetHudOverlayAction</c> → <c>SetTrainingOverlay</c>, sethudoverlayaction.cpp:32).
/// 0 = none; the overlay ids are <see cref="TrainingMissionIds.Overlay*"/>.</summary>
public static class TrainingOverlay
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
    }

    /// <summary>Advance the mission by one frame (native Frame()). Returns true once complete.</summary>
    public bool Advance()
    {
        if (Finished) return true;
        if (!Started) return false;   // the mission starts once the player ship exists (Game1.UpdateTraining)
        if (_mission.Frame())
        {
            Completed = true;
            Finished = true;
        }
        return Finished;
    }

    /// <summary>Route a key press from the real input path through the mission's RecordKeyPress.</summary>
    public bool RecordKeyPress(int key) => _mission.RecordKeyPress(key);

    public void Terminate()
    {
        _mission.Terminate();
        Finished = true;
    }
}
