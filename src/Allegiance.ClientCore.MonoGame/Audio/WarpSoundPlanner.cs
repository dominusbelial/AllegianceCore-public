namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>Action a <see cref="WarpSoundPlanner"/> wants taken on one warp's
/// ambient sound this frame.</summary>
public enum WarpSoundAction
{
    /// <summary>The warp just came into audible range → begin its ambient
    /// <c>alephloop</c> loop.</summary>
    Start,

    /// <summary>The warp is still in range → refresh its ambient volume to the
    /// current listener distance (the "swell as you get close").</summary>
    Update,

    /// <summary>The warp left audible range (or the listener left its sector) →
    /// stop (and release) its ambient loop.</summary>
    Stop,
}

/// <summary>A per-frame directive for one warp's ambient sound, emitted by
/// <see cref="WarpSoundPlanner.Plan"/>.</summary>
/// <param name="WarpId">The warp's real <c>DataWarpIGC.WarpId</c> (the
/// <c>ClientWorld.Warps</c> dictionary key).</param>
/// <param name="Action"><see cref="WarpSoundAction"/>.</param>
/// <param name="Volume">Desired ambient volume (0..1) for Start/Update; 0 for
/// Stop.</param>
public readonly record struct WarpSoundCommand(short WarpId, WarpSoundAction Action, float Volume);

/// <summary>
/// P237 — pure, MonoGame-free transition tracker for warp ambient sounds. Fed
/// each frame the list of warps currently within audible range of the listener
/// (with their distances), it decides which warps have <i>newly</i> entered range
/// (→ Start), which continue (→ Update with the fresh distance-scaled volume),
/// and which disappeared (left range or the listener left the sector → Stop).
/// Mirroring native's per-object <c>AmbientSoundSite</c> attached to every warp
/// (<c>trekigc.cpp:1436-1437</c>), this class owns only the <i>decision</i>; the
/// actual audio calls live in <see cref="SoundManager"/>. Being pure, the
/// enter→Start / leave→Stop contract is directly unit-testable — the P237
/// brief's verification target alongside the live audio proof.
/// </summary>
/// <remarks>
/// <para><b>Keying.</b> Warps are static objects with a stable real
/// <c>WarpId</c> (the <c>ClientWorld.Warps</c> dictionary key), unlike the
/// reference-identity-keyed transient projectiles of P232 — so the id short is a
/// clean, unambiguous key.</para>
/// <para><b>Stop semantics.</b> A warp leaves the caller's live-within-range
/// list when the listener moves past the audible radius <i>or</i> transits to
/// another sector (the caller filters to in-sector warps). Both correctly become
/// a Stop here — the sound fades whether the player walked away or jumped
/// systems.</para>
/// </remarks>
public sealed class WarpSoundPlanner
{
    private readonly HashSet<short> _active = new();

    /// <summary>The warps currently considered to have a live ambient sound
    /// (those started and not yet stopped). Read-mostly for diagnostics.</summary>
    public IReadOnlyCollection<short> Active => _active;

    /// <summary>
    /// Compute the per-frame sound directives for the given live, in-audible-range
    /// warp list (one entry per warp currently audible to the listener, paired with
    /// its listener distance). Returns a fresh command list each call; the caller
    /// replays it against the audio backend. Empty input stops every tracked warp.
    /// </summary>
    public List<WarpSoundCommand> Plan(IReadOnlyCollection<(short WarpId, float Distance)> live)
    {
        var commands = new List<WarpSoundCommand>(live.Count + 2);

        var seen = new HashSet<short>();
        foreach (var (warpId, distance) in live)
        {
            seen.Add(warpId);
            float volume = WarpAmbientSoundCurve.VolumeForDistance(distance);
            if (_active.Add(warpId))
                commands.Add(new WarpSoundCommand(warpId, WarpSoundAction.Start, volume));
            else
                commands.Add(new WarpSoundCommand(warpId, WarpSoundAction.Update, volume));
        }

        foreach (var warpId in _active.ToList())
        {
            if (!seen.Contains(warpId))
            {
                _active.Remove(warpId);
                commands.Add(new WarpSoundCommand(warpId, WarpSoundAction.Stop, Volume: 0f));
            }
        }

        return commands;
    }
}
