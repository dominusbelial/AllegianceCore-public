using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Content;

namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>
/// P304 — the live background-music subsystem on MonoGame's audio stack,
/// built from the pure decision logic in <see cref="MusicPlanner"/> plus the
/// two looping bed instances (<c>flightmusic</c> / <c>stationmusic</c>). This
/// is the MonoGame-side half of the established
/// <c>planner-decides / manager-performs</c> split in this directory
/// (<see cref="SoundManager"/> ↔ <see cref="StationSoundPlanner"/>): the
/// planner owns toggle/volume/switch state; this class owns the actual
/// <see cref="SoundEffectInstance"/> playback.
/// </summary>
/// <remarks>
/// <para><b>Two looping beds, switched on dock/undock.</b> Mirrors native's
/// real intended design — a simple <c>flightmusic</c> loop and a
/// <c>stationmusic</c> loop (<c>artwork.h:284-285</c>) selected by whether the
/// local ship is docked (<c>ShipEntity.StationId != -1</c>), not a dynamic
/// adaptive score. Both beds loop (<c>IsLooped = true</c>), one at a time.</para>
/// <para><b>Missing beds stay silent.</b> Native names are defined in
/// ~/Development/Allegiance/src/WinTrek/artwork.h:284-285. The pinned artwork
/// has no flightmusic/stationmusic beds; native's optional score entries are
/// empty (~/Development/Allegiance/src/Igc/sounds.h:117-133). Never synthesize
/// replacement tones. Content supplied later can still load at these paths.</para>
/// <para><b>Graceful degradation.</b> Like <see cref="SoundManager"/>, music
/// is cosmetic: a host with no OpenAL/audio device (or a load failure) leaves
/// the manager inert and silent rather than throwing into the game's load
/// path — the app keeps rendering/flying. The pure planner still works (it is
/// what the tests and live harness drive), only the actual audio calls no-op.</para>
/// </remarks>
public sealed class Music : IDisposable
{
    /// <summary>Native flight bed name (WinTrek/artwork.h:284).</summary>
    public const string FlightAsset = "Sounds/flightmusic";

    /// <summary>Native station bed name (WinTrek/artwork.h:285).</summary>
    public const string StationAsset = "Sounds/stationmusic";

    private readonly MusicPlanner _planner = new();
    private readonly ContentManager _content;
    private SoundEffectInstance? _flight;    // flightmusic loop
    private SoundEffectInstance? _station;   // stationmusic loop
    private bool _warned;
    private bool _disposed;

    /// <summary>The pure decision state (toggle/volume/current track). Exposed
    /// so tests and the live harness can drive and measure it directly.</summary>
    public MusicPlanner Planner => _planner;

    /// <summary>Whether the music toggle is on (forwarded from <see cref="Planner"/>).</summary>
    public bool Enabled => _planner.Enabled;

    /// <summary>The current music volume (forwarded from <see cref="Planner"/>).</summary>
    public float Volume => _planner.Volume;

    /// <summary>The track currently selected by the planner.</summary>
    public MusicTrackKind Current => _planner.Current;

    /// <summary>Live playback state of the flight bed (diagnostics / live proof).</summary>
    public SoundState FlightState => _flight?.State ?? SoundState.Stopped;

    /// <summary>Live playback state of the station bed (diagnostics / live proof).</summary>
    public SoundState StationState => _station?.State ?? SoundState.Stopped;

    /// <summary>The actual mix volume currently on the flight bed's instance
    /// (what the listener hears). Measurable output level for live proof.</summary>
    public float FlightActualVolume => _flight?.Volume ?? 0f;

    /// <summary>The actual mix volume currently on the station bed's instance.</summary>
    public float StationActualVolume => _station?.Volume ?? 0f;

    public Music(ContentManager content) => _content = content;

    /// <summary>Load real beds when supplied. Missing beds remain silent.
    /// Playback begins only after the player enables music.</summary>
    public void Load()
    {
        _flight = Track(FlightAsset);
        _station = Track(StationAsset);
    }

    /// <summary>Drive the music mix for the current frame from the live dock
    /// state: feed it to the planner and replay the resulting directives
    /// against the two bed instances. No-op when the manager is inert (no
    /// audio device / load failure).</summary>
    /// <param name="isDocked">Whether the local ship is docked
    /// (<c>ShipEntity.StationId != -1</c>).</param>
    public void Update(bool isDocked)
    {
        foreach (var cmd in _planner.Plan(isDocked))
            Apply(cmd);
    }

    /// <summary>Flip the music toggle (mirrors native <c>idmToggleMusic</c>,
    /// 704). Playback follows on the next <see cref="Update"/>.</summary>
    public void Toggle()
    {
        _planner.Toggle();
        Console.WriteLine($"[monogame] music {( _planner.Enabled ? "ON" : "OFF" )}");
    }

    /// <summary>Raise the music volume one step (mirrors native
    /// <c>idmMusicVolumeUp</c>, 705). Applied to the live bed on the next
    /// <see cref="Update"/>.</summary>
    public void VolumeUp()
    {
        _planner.VolumeUp();
        Console.WriteLine($"[monogame] music volume -> {_planner.Volume:F3}");
    }

    /// <summary>Lower the music volume one step (mirrors native
    /// <c>idmMusicVolumeDown</c>, 706). Applied to the live bed on the next
    /// <see cref="Update"/>.</summary>
    public void VolumeDown()
    {
        _planner.VolumeDown();
        Console.WriteLine($"[monogame] music volume -> {_planner.Volume:F3}");
    }

    private void Apply(MusicCommand cmd)
    {
        var inst = cmd.Track switch
        {
            MusicTrackKind.Flight => _flight,
            MusicTrackKind.Station => _station,
            _ => null,
        };
        if (inst is null) return;

        switch (cmd.Action)
        {
            case MusicAction.Start:
                SetLoopVolume(inst, cmd.Volume);
                if (inst.State != SoundState.Playing) Safe(inst.Play);
                break;
            case MusicAction.SetVolume:
                SetLoopVolume(inst, cmd.Volume);
                break;
            case MusicAction.Stop:
                if (inst.State == SoundState.Playing) Safe(inst.Stop);
                break;
        }
    }

    private SoundEffectInstance? Track(string asset)
    {
        try { return Loop(_content.Load<SoundEffect>(asset)); }
        catch (ContentLoadException) { return null; }
        catch (Exception ex)
        {
            WarnOnce($"music failed to load, disabling: {ex.Message}");
            return null;
        }
    }

    private static SoundEffectInstance Loop(SoundEffect sfx)
    {
        var inst = sfx.CreateInstance();
        inst.IsLooped = true;
        return inst;
    }

    private static void SetLoopVolume(SoundEffectInstance inst, float volume) =>
        Safe(() => inst.Volume = Math.Clamp(volume, 0f, 1f));

    /// <summary>Wrap an audio call so a missing/broken device degrades instead
    /// of throwing into the game loop (same policy as <see cref="SoundManager"/>).</summary>
    private static void Safe(Action action)
    {
        try { action(); }
        catch (Exception) { /* manager already inert; stay silent */ }
    }

    private void WarnOnce(string message)
    {
        if (_warned) return;
        _warned = true;
        Console.WriteLine($"[monogame] {message}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _flight?.Dispose();
            _station?.Dispose();
        }
        catch { /* disposal must not throw */ }
    }
}
