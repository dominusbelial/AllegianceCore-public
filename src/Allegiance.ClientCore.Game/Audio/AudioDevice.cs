using Microsoft.Xna.Framework.Audio;

namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>One attempt per client lifetime. Native dummysound.cpp:17-289
/// accepts every call when the hardware engine cannot be created.</summary>
public sealed class AudioDevice(Action? initialize = null, Action<string>? diagnostic = null)
{
    private bool _attempted;
    public bool Available { get; private set; }
    public bool TryInitialize()
    {
        if (_attempted) return Available;
        _attempted = true;
        try { (initialize ?? SoundEffect.Initialize)(); Available = true; }
        catch (Exception ex) { diagnostic?.Invoke($"Audio unavailable; continuing silently ({ex.GetType().Name})."); }
        return Available;
    }
}
