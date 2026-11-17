using Allegiance.ClientCore.MonoGame.Audio;
using Microsoft.Xna.Framework.Audio;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>Gesture-enabled menu cue only. Narration, world mixing and native audio initialization are excluded.</summary>
internal sealed class MenuAudio(SoundEffect click, IntroPreferences preferences) : IMenuSound, IDisposable
{
    public bool Sound3DEnabled { get; set; } = preferences.Enable3DSound;
    public AudioQuality Quality { get; set; } = preferences.AudioQuality;
    public void StartSoundId(short soundId)
    {
        if (soundId == Allegiance.Core.Audio.NativeSoundCatalog.IdOf("mouseclickSound")) click.Play();
    }
    public void FlushAudio() { } // KNI submits the cue immediately; no desktop sound-engine flush.
    public void Dispose() => click.Dispose();
}
