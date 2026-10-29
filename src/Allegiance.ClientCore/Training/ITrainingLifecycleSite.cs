namespace Allegiance.ClientCore.Training;

/// <summary>Local training lifecycle bindings. Native training/trainingmission.cpp:128-149,
/// 428-512. Kept separate from the condition/action bridge.</summary>
public interface ITrainingLifecycleSite
{
    int CommanderId { get; }
    void SetupTraining(int missionId);
    void TrainingShipDied();
    void RestoreTrainingShip();
    bool TrainingShipExists(int shipId);
}
