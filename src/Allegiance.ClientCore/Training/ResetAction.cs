namespace Allegiance.ClientCore.Training;

/// <summary>
/// Resets a goal (or terminates the mission) across a staged voice-over — port of
/// <c>ResetAction</c> (<c>resetaction.cpp:37-110</c>). The native <c>Execute</c> body is entirely
/// commented out (lines 55-88, marked <c>XXX this functionality now allows the game engine to continue</c>),
/// so the action currently performs no work; the staged reset machinery lives on as a no-op shape for
/// TR-2 to re-enable. Kept as a faithful class so mission data that references it still translates.
/// </summary>
public sealed class ResetAction : Action
{
    private readonly Goal? _goal;
    private readonly int _soundId;

    public ResetAction(Goal? goal, int soundId = SoundId.None)
    {
        _goal = goal;
        _soundId = soundId;
    }

    public override void Execute()
    {
        // native body disabled — see class doc
    }
}
