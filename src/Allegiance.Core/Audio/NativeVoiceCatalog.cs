namespace Allegiance.Core.Audio;

/// <summary>A sounddef.mdl template. Gains are dB, including native's default
/// VoiceOverGain of -13 (WinTrek.cpp:2928-2931); mutexes are independent.</summary>
public sealed record NativeVoiceCue(string Name, string Stem, string Mutex,
    double Timeout, double Priority, bool SingleInstance, double GainDb, bool Available)
{
    public float Gain => (float)Math.Pow(10, GainDb / 20);
}

public sealed record NativeSonicChat(string Caption, NativeVoiceCue Cue);

public static partial class NativeVoiceCatalog
{
    /// <summary>Native trekigc.cpp:4507-4536 distinguishes null-text sonic
    /// indices from direct SoundIDs. Also accept the port's P217 caption+index
    /// format so existing clients and bots remain compatible.</summary>
    public static NativeVoiceCue? Resolve(short id, string text, bool messageIsAbsent = false)
    {
        if (messageIsAbsent)
            return SonicChats.GetValueOrDefault(id)?.Cue;
        if (SonicChats.TryGetValue(id, out var sonic) &&
            text == sonic.Caption)
            return sonic.Cue;
        return Sounds.GetValueOrDefault(id);
    }

    public static string Caption(short index) => SonicChats.GetValueOrDefault(index)?.Caption ?? "Unknown chat";
}
