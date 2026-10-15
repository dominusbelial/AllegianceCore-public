using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Audio;

public readonly record struct MissionCountdownCue(bool Start, int? Second);

/// <summary>The per-frame native CheckCountdownSound change detector
/// (WinTrek.cpp:11127-11210), shared by briefing and timed-game expiry.
/// Game1 replays these commands through SoundManager. No audio device needed.</summary>
public sealed class MissionCountdownSound
{
    private long _generation = -1;
    private long _version = -1;
    private bool _wasStarting;
    private int _lastSecond = int.MaxValue;

    public void Reset()
    {
        _generation = -1;
        _version = -1;
        _wasStarting = false;
        _lastSecond = int.MaxValue;
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
        return new(start, CountdownSound.TickAssetFor(second.Value) is not null ? second : null);
    }
}
