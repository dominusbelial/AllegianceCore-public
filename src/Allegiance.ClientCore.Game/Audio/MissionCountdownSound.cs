using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Audio;

public readonly record struct MissionCountdownCue(bool Start, int? Second, string? Milestone = null);

/// <summary>The per-frame native CheckCountdownSound change detector
/// (WinTrek.cpp:11127-11210), shared by briefing and timed-game expiry.
/// Game1 replays these commands through SoundManager. No audio device needed.</summary>
public sealed class MissionCountdownSound
{
    private long _generation = -1;
    private long _version = -1;
    private bool _wasStarting;
    private int _lastSecond = int.MaxValue;
    private string? _lastMilestone;

    public void Reset()
    {
        _generation = -1;
        _version = -1;
        _wasStarting = false;
        _lastSecond = int.MaxValue;
        _lastMilestone = null;
    }

    /// <summary>Emit only the current changed value; native does not replay
    /// checkpoints skipped by a slow frame. A new briefing resets the detector
    /// (OnMissionCountdown); a late join to Started only speaks the timed tail.</summary>
    public MissionCountdownCue Update(ClientWorld world, long? localMs = null)
    {
        if (_generation != world.MissionGeneration) Reset();
        _generation = world.MissionGeneration;
        bool starting = world.Mission?.Stage == MissionStageMessage.StageStarting;
        bool start = starting && (!_wasStarting || _version != world.CountdownVersion);
        if (start) _lastSecond = int.MaxValue;
        _wasStarting = starting;
        _version = world.CountdownVersion;

        int? second = world.MissionCountdownSoundSeconds(localMs);
        if (second is null || second == _lastSecond) return new(start, null);
        _lastSecond = second.Value;

        // P711 — coarse mission-clock milestones (SIM-0300..0303, WinTrek.cpp:
        // 11157-11169) are announced before the per-second tail; a milestone is
        // emitted once on its exact value and does not suppress the tick at 10..1.
        string? milestone = CountdownSound.MilestoneAssetFor(second.Value);
        if (milestone is not null && milestone != _lastMilestone)
        {
            _lastMilestone = milestone;
            return new(start, null, milestone);
        }

        return new(start, CountdownSound.TickAssetFor(second.Value) is not null ? second : null);
    }
}
