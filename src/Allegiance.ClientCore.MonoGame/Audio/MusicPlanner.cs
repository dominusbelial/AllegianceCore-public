namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>Which background-music bed should be audible. Mirrors native's two
/// named tracks — <c>SNDF_FLIGHTMUSIC</c>="flightmusic" and
/// <c>SNDF_STATIONMUSIC</c>="stationmusic" (artwork.h:284-285) — plus the
/// <c>None</c> (music off / between) state. Real native design (confirmed by
/// Domingo's memory and the source) is a simple flight-loop + station-loop,
/// not a dynamic/adaptive score, so a single flight-vs-station axis is the
/// faithful shape.</summary>
public enum MusicTrackKind
{
    /// <summary>No music bed — the toggle is off, or no applicable track.</summary>
    None,

    /// <summary>The in-flight loop (<c>flightmusic</c>) — played while the
    /// local ship is free-flying (not docked).</summary>
    Flight,

    /// <summary>The docked/station loop (<c>stationmusic</c>) — played while
    /// the local ship is docked at a station.</summary>
    Station,
}

/// <summary>Action a <see cref="MusicCommand"/> wants taken on one track this
/// frame (the direct analogue of <see cref="StationSoundAction"/> for the
/// music beds).</summary>
public enum MusicAction
{
    /// <summary>Begin looping the named track at the given volume.</summary>
    Start,

    /// <summary>Refine a live track's output volume (a volume-key press while
    /// the track is already playing).</summary>
    SetVolume,

    /// <summary>Stop (and release) the named track — the toggle went off, or
    /// the state switched to the other track.</summary>
    Stop,
}

/// <summary>A per-frame directive for one music track, emitted by
/// <see cref="MusicPlanner.Plan"/>. Replayed against the audio backend by the
/// MonoGame <see cref="Music"/> wrapper.</summary>
/// <param name="Track"><see cref="MusicTrackKind"/> the directive concerns.</param>
/// <param name="Action"><see cref="MusicAction"/>.</param>
/// <param name="Volume">Mix volume (0..1) for Start/SetVolume; 0 for Stop.</param>
public readonly record struct MusicCommand(MusicTrackKind Track, MusicAction Action, float Volume);

/// <summary>
/// P304 — pure, MonoGame-free transition tracker for the background-music
/// beds. Owns the real toggle state, the real volume control, and the real
/// flight-vs-docked track selection. Fed each frame the live "am I docked?"
/// boolean (from <c>ShipEntity.StationId != -1</c>, the exact same real
/// docked-state check <c>Game1</c> already uses), it emits the Start/Stop/
/// SetVolume directives that switch between the flight and station loops.
/// Being pure, the whole toggle/volume/switching contract is directly
/// unit-testable and live-drivable with no audio device — matching the
/// established sound-planner precedent in this directory
/// (<see cref="StationSoundPlanner"/>): the decision lives here, the actual
/// <c>SoundEffectInstance</c> calls live in the MonoGame <see cref="Music"/>
/// wrapper.
/// </summary>
/// <remarks>
/// <para><b>Native spec.</b> Native exposes a real <c>m_bMusic</c> bool toggle
/// (WinTrek.cpp:1412) and a real <c>m_pnumMusicGain</c> volume control
/// (WinTrek.cpp:1201) behind real menu commands <c>idmToggleMusic</c>(704) /
/// <c>idmMusicVolumeUp</c>(705) / <c>idmMusicVolumeDown</c>(706)
/// (WinTrek.cpp:3966-3968). The native toggle is dead scaffolding (never read
/// anywhere in WinTrek.cpp) — this port builds the <i>live</i> toggle/volume
/// of its own that native only sketched, with the two-track
/// flight/station shape as the spec.</para>
/// <para><b>Volume is discrete.</b> A fixed <see cref="VolumeStep"/> (1/16 of
/// full scale) gives a countable, measurable ladder — every press is a
/// reproducible, assertable output level, which is what makes "volume control
/// actually changing output level" live-verifiable rather than merely claimed.</para>
/// </remarks>
public sealed class MusicPlanner
{
    /// <summary>Size of one volume-up/down step (1/16 of full scale). Deliberately
    /// coarse enough to be audibly countable — matching native's stepped
    /// <c>ModifiableNumber</c> volume nudges rather than a slider.</summary>
    public const float VolumeStep = 1f / 16f;

    private bool _enabled;
    private float _volume = 0.8f;                       // sensible default music-bed mix
    private MusicTrackKind _current = MusicTrackKind.None;

    /// <summary>Whether the music beds are enabled (the live mirror of native's
    /// dead <c>m_bMusic</c>). Starts off — no music until the player asks for it.</summary>
    public bool Enabled => _enabled;

    /// <summary>The current music volume (0..1, stepped).</summary>
    public float Volume => _volume;

    /// <summary>The track currently considered live (started and not stopped).</summary>
    public MusicTrackKind Current => _current;

    /// <summary>Flip the music toggle. Off → on begins playing the track for
    /// the current dock state on the next <see cref="Plan"/>; on → off stops it.</summary>
    public void Toggle() => _enabled = !_enabled;

    /// <summary>Raise the music volume by one <see cref="VolumeStep"/> (clamped at 1).</summary>
    public void VolumeUp() => _volume = Math.Min(1f, _volume + VolumeStep);

    /// <summary>Lower the music volume by one <see cref="VolumeStep"/> (clamped at 0).</summary>
    public void VolumeDown() => _volume = Math.Max(0f, _volume - VolumeStep);

    /// <summary>Explicitly set the music volume (clamped 0..1). Not used by the
    /// stock keybindings (they use <see cref="VolumeUp"/>/<see cref="VolumeDown"/>),
    /// but available so a future settings/options screen can restore a saved mix.</summary>
    public void SetVolume(float volume) => _volume = Math.Clamp(volume, 0f, 1f);

    /// <summary>
    /// Compute the per-frame music directives for the given live dock state.
    /// Returns a fresh command list each call; the caller replays it against
    /// the audio backend. Contract:
    /// <list type="bullet">
    /// <item><b>Disabled</b> → whatever is playing stops (toggle-off), nothing starts.</item>
    /// <item><b>Enabled + docked</b> → <c>stationmusic</c> loop starts (stopping any flight loop).</item>
    /// <item><b>Enabled + undocked</b> → <c>flightmusic</c> loop starts (stopping any station loop).</item>
    /// <item><b>Already on the right track</b> → a SetVolume keeps the live track's
    /// output in sync with any volume change made this frame.</item>
    /// </list>
    /// </summary>
    /// <param name="isDocked">True when the local ship is docked
    /// (<c>ShipEntity.StationId != -1</c>).</param>
    public List<MusicCommand> Plan(bool isDocked)
    {
        MusicTrackKind desired = !_enabled
            ? MusicTrackKind.None
            : (isDocked ? MusicTrackKind.Station : MusicTrackKind.Flight);

        var commands = new List<MusicCommand>(2);

        if (desired == _current)
        {
            // No track change. If a bed is live, keep its output volume in sync
            // with the current mix (a VolumeUp/Down landed this frame, or a
            // SetVolume call) — emit SetVolume so the backend reflects it.
            if (desired != MusicTrackKind.None)
                commands.Add(new MusicCommand(desired, MusicAction.SetVolume, _volume));
            return commands;
        }

        // Transition to a different bed: stop the outgoing track, then (unless
        // we are turning OFF entirely) start the new one at the current volume.
        if (_current != MusicTrackKind.None)
            commands.Add(new MusicCommand(_current, MusicAction.Stop, Volume: 0f));
        if (desired != MusicTrackKind.None)
            commands.Add(new MusicCommand(desired, MusicAction.Start, _volume));

        _current = desired;
        return commands;
    }
}
