using System.Numerics;

namespace Allegiance.ClientCore.Training;

/// <summary>Host operations for native CreateDroneAction base data and rolled orientation.</summary>
public interface ITrainingDroneSite
{
    void AddBuoyConsumer(int buoyId);
    void CopyDroneOrientation(int shipId, int sourceId);
    void ConfigureExpendable(int shipId, int typeId);
}

/// <summary>training/TurnShipAboutCondition.cpp:33-47. Shipped lessons never instantiate it.</summary>
public sealed class TurnShipAboutCondition(ITrainingSite site, TargetRef ship) : Condition
{
    private Vector3 _startingForward;
    public override bool Start() { _startingForward = site.Resolve(ship)!.Forward; return false; }
    public override bool Evaluate() => site.Resolve(ship) is { } current
        && Vector3.Dot(current.Forward, _startingForward) < MathF.Cos(MathF.PI / 18f);
}

/// <summary>training/SuspendedSoundFinishedCondition.cpp:21-33. GC retains the action while waiting.</summary>
public sealed class SuspendedSoundFinishedCondition(PlaySoundAction action) : Condition
{
    private readonly SoundFinishedCondition _finished = new(action);
    public override bool Evaluate() => _finished.Evaluate();
}

/// <summary>training/SuspendedPlaySoundAction.cpp:40-47. Register the wait before starting playback.</summary>
public sealed class SuspendedPlaySoundAction(ITrainingSite site, TrainingMission mission, int soundId) : Action
{
    private readonly PlaySoundAction _sound = new(site, soundId);
    public override void Execute()
    {
        if (soundId == SoundId.None) return;
        mission.AddWaitCondition(new SuspendedSoundFinishedCondition(_sound));
        _sound.Execute();
    }
    public override void Stop() => _sound.Stop();
}
