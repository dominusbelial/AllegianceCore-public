namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>Only the audio controls the shared Options menu needs; playback backends remain platform-specific.</summary>
public interface IMenuSound : ISoundIdEventSink
{
    bool Sound3DEnabled { get; set; }
    AudioQuality Quality { get; set; }
}
public interface IMenuMusic
{
    bool Enabled { get; }
    void Toggle();
    void VolumeUp();
    void VolumeDown();
}
