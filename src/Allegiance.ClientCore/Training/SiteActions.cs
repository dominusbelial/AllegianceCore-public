namespace Allegiance.ClientCore.Training;

/// <summary>
/// Output / configuration actions — the C# ports of <c>messageaction</c>, <c>playsoundaction</c>,
/// <c>soundfinishedcondition</c>, <c>setdisplaymodeaction</c>, <c>sethudoverlayaction</c> and
/// <c>setautopilotaction</c>.
/// </summary>

/// <summary>
/// Shows a rotating series of chat lines — port of <c>MessageAction</c>
/// (<c>messageaction.cpp:27-70</c>). Each message may carry an accompanying sound id
/// (TheBored 06-JUL-2007 edit). When the cursor reaches the end it wraps back to the first message, so
/// repeated <c>Execute</c> calls cycle through the list (native lines 47-52).
/// </summary>
public sealed class MessageAction : Action
{
    private readonly ITrainingSite _site;
    private readonly List<(string Text, int SoundId)> _messages = new();
    private int _cursor;

    public MessageAction(ITrainingSite site) => _site = site;

    public MessageAction(ITrainingSite site, string message)
        : this(site)
    {
        AddMessage(message);
    }

    public override void Execute()
    {
        if (_messages.Count == 0)
            return;
        if (_cursor >= _messages.Count)
            _cursor = 0;
        var (text, soundId) = _messages[_cursor++];
        if (soundId != SoundId.None)
            _site.PlaySound(soundId);
        _site.ShowMessage(text);
    }

    public void AddMessage(string message, int soundId = SoundId.None) => _messages.Add((message, soundId));
}

/// <summary>
/// Plays a sound and tracks its instance — port of <c>PlaySoundAction</c>
/// (<c>playsoundaction.cpp:21-65</c>). <c>Execute</c> starts it (once — repeated executions restart);
/// <c>Stop</c> cuts it off; <c>HasStarted</c>/<c>HasStopped</c> drive <see cref="SoundFinishedCondition"/>.
/// A <c>SoundId.None</c> sound never plays and is considered stopped.
/// </summary>
public sealed class PlaySoundAction : Action
{
    private readonly ITrainingSite _site;
    private readonly int _soundId;
    private int _instance;
    private bool _hasStarted;

    public PlaySoundAction(ITrainingSite site, int soundId)
    {
        _site = site;
        _soundId = soundId;
        _instance = -1;
    }

    public override void Execute()
    {
        if (_soundId != SoundId.None)
            _instance = _site.PlaySound(_soundId);
        _hasStarted = true;
    }

    public override void Stop()
    {
        if (_soundId != SoundId.None && _hasStarted && _instance >= 0 && _site.IsSoundPlaying(_instance))
            _site.StopSound(_instance);
    }

    public bool HasStarted => _hasStarted;

    public bool HasStopped => _soundId == SoundId.None || !_site.IsSoundPlaying(_instance);
}

/// <summary>
/// TRUE once a <see cref="PlaySoundAction"/> has started and finished — port of
/// <c>SoundFinishedCondition</c> (<c>soundfinishedcondition.cpp:21-42</c>): the first <c>Evaluate</c>
/// latches <c>HasStarted</c> and returns false, afterwards it returns <c>HasStopped</c>. This is what
/// backs <c>CreatePlaySoundGoal</c> (<c>trainingmission.cpp:896-902</c>) — a goal that completes when its
/// voice-over sound ends.
/// </summary>
public sealed class SoundFinishedCondition : Condition
{
    private readonly PlaySoundAction _action;
    private bool _hasStarted;

    public SoundFinishedCondition(PlaySoundAction action) => _action = action;

    public override bool Evaluate()
    {
        if (!_hasStarted)
        {
            _hasStarted = _action.HasStarted;
            return false;
        }
        return _action.HasStopped;
    }
}

/// <summary>
/// Sets the camera/display mode — port of <c>SetDisplayModeAction</c>
/// (<c>setdisplaymodeaction.cpp:32-40</c>). The native also flips the view mode based on whether the
/// mode is a command camera; the C# delegates the whole decision to the site.
/// </summary>
public sealed class SetDisplayModeAction : Action
{
    private readonly ITrainingSite _site;
    private readonly int _mode;

    public SetDisplayModeAction(ITrainingSite site, int mode)
    {
        _site = site;
        _mode = mode;
    }

    public override void Execute() => _site.SetDisplayMode(_mode);
}

/// <summary>
/// Sets the HUD overlay — port of <c>SetHUDOverlayAction</c> (<c>sethudoverlayaction.cpp:32-35</c>).
/// </summary>
public sealed class SetHudOverlayAction : Action
{
    private readonly ITrainingSite _site;
    private readonly int _overlay;

    public SetHudOverlayAction(ITrainingSite site, int overlay)
    {
        _site = site;
        _overlay = overlay;
    }

    public override void Execute() => _site.SetHudOverlay(_overlay);
}

/// <summary>
/// Turns a ship's autopilot on or off — port of <c>SetAutopilotAction</c>
/// (<c>setautopilotaction.cpp:42-45</c>).
/// </summary>
public sealed class SetAutopilotAction : Action
{
    private readonly ITrainingSite _site;
    private readonly TargetRef _ship;
    private readonly bool _newValue;

    public SetAutopilotAction(ITrainingSite site, TargetRef ship, bool newValue = true)
    {
        _site = site;
        _ship = ship;
        _newValue = newValue;
    }

    public override void Execute() => _site.SetAutopilot(_ship, _newValue);
}

/// <summary>
/// Terminates the active training mission so its next <see cref="TrainingMission.Frame"/> completes —
/// port of <c>EndMissionAction</c> (<c>endmissionaction.cpp:31-35</c>: <c>g_pMission->Terminate()</c>).
/// Holds the mission (the analogue of the native global) rather than the site.
/// </summary>
public sealed class EndMissionAction : Action
{
    private readonly TrainingMission _mission;

    public EndMissionAction(TrainingMission mission) => _mission = mission;

    public override void Execute() => _mission.Terminate();
}

/// <summary>Well-known sound ids referenced by the framework (native <c>SoundID</c> values in trainingsounddef).</summary>
public static class SoundId
{
    /// <summary>The native <c>NA</c> sentinel: "no sound".</summary>
    public const int None = -1;
}
