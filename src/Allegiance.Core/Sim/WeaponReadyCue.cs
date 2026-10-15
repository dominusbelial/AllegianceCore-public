namespace Allegiance.Core.Sim;

/// <summary>Igc/igc.h:456-464 and Igc/sounds.h:34: mountedSound=1023.
/// sounddef.mdl:287 binds that cue to ImportWave("unmount").</summary>
public static class WeaponReadyCue
{
    public const short SoundId = 1023;
    public const string SoundStem = "unmount";
    public static string? Resolve(short soundId) => soundId == SoundId ? SoundStem : null;
}
