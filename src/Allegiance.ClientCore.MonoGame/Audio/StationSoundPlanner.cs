namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>Action a <see cref="StationSoundPlanner"/> wants taken on one
/// station's ambient sound this frame.</summary>
public enum StationSoundAction
{
    /// <summary>The station just came into audible range → begin its ambient
    /// <c>shipout</c> loop.</summary>
    Start,

    /// <summary>The station is still in range → refresh its ambient volume to the
    /// current listener distance (the swell on approach).</summary>
    Update,

    /// <summary>The station left audible range (or the listener left its sector,
    /// or the station was destroyed) → stop (and release) its ambient loop.</summary>
    Stop,
}

/// <summary>A per-frame directive for one station's ambient sound, emitted by
/// <see cref="StationSoundPlanner.Plan"/>.</summary>
/// <param name="StationId">The station's real <c>DataStationIGC.StationId</c>
/// (the <c>ClientWorld.Stations</c> dictionary key).</param>
/// <param name="Action"><see cref="StationSoundAction"/>.</param>
/// <param name="Volume">Desired ambient volume (0..1) for Start/Update; 0 for
/// Stop.</param>
public readonly record struct StationSoundCommand(short StationId, StationSoundAction Action, float Volume);

/// <summary>
/// P267 — pure, MonoGame-free transition tracker for station ambient sounds.
/// Fed each frame the list of stations currently within audible range of the
/// listener (with their distances), it decides which stations have <i>newly</i>
/// entered range (→ Start), which continue (→ Update with the fresh
/// distance-scaled volume), and which disappeared (left range, the listener
/// left the sector, or the station was destroyed → Stop). Mirroring native's
/// per-object <c>AmbientSoundSite</c> attached to every station
/// (<c>trekigc.cpp:1465-1468</c>), this class owns only the <i>decision</i>;
/// the actual audio calls live in <see cref="SoundManager"/>. Being pure, the
/// enter→Start / leave→Stop contract is directly unit-testable.
/// </summary>
/// <remarks>
/// <para><b>Keying.</b> Stations are static objects with a stable real
/// <c>StationId</c> (the <c>ClientWorld.Stations</c> dictionary key), like
/// warps — so the id short is a clean, unambiguous key.</para>
/// <para><b>Stop semantics.</b> A station leaves the caller's live-within-range
/// list when the listener moves past the audible radius <i>or</i> transits to
/// another sector <i>or</i> the station is destroyed and removed from the world
/// table (ClientWorld's <c>Apply(StationDestroyed)</c>). All three correctly
/// become a Stop here — the ambient fades whether the pilot flew away, jumped
/// systems, or the base blew up.</para>
/// </remarks>
public sealed class StationSoundPlanner
{
    private readonly HashSet<short> _active = new();

    /// <summary>The stations currently considered to have a live ambient sound
    /// (those started and not yet stopped). Read-mostly for diagnostics.</summary>
    public IReadOnlyCollection<short> Active => _active;

    /// <summary>
    /// Compute the per-frame sound directives for the given live, in-audible-range
    /// station list (one entry per station currently audible to the listener,
    /// paired with its listener distance). Returns a fresh command list each call;
    /// the caller replays it against the audio backend. Empty input stops every
    /// tracked station.
    /// </summary>
    public List<StationSoundCommand> Plan(IReadOnlyCollection<(short StationId, float Distance)> live)
    {
        var commands = new List<StationSoundCommand>(live.Count + 2);

        var seen = new HashSet<short>();
        foreach (var (stationId, distance) in live)
        {
            seen.Add(stationId);
            float volume = StationAmbientSoundCurve.VolumeForDistance(distance);
            if (_active.Add(stationId))
                commands.Add(new StationSoundCommand(stationId, StationSoundAction.Start, volume));
            else
                commands.Add(new StationSoundCommand(stationId, StationSoundAction.Update, volume));
        }

        foreach (var stationId in _active.ToList())
        {
            if (!seen.Contains(stationId))
            {
                _active.Remove(stationId);
                commands.Add(new StationSoundCommand(stationId, StationSoundAction.Stop, Volume: 0f));
            }
        }

        return commands;
    }
}
