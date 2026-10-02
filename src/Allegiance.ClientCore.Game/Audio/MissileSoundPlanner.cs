using Allegiance.Core.Collision;

namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>Action a <see cref="MissileSoundPlanner"/> wants taken on one missile's
/// sound this frame.</summary>
public enum MissileSoundAction
{
    /// <summary>A missile was just observed launched by the local ship → play the
    /// launch one-shot, positioned at the firing ship (native
    /// <c>clintlib.cpp:3103</c> <c>PlaySoundEffect(…, pShip)</c>).</summary>
    Launch,

    /// <summary>A missile came into range → begin its continuous in-flight ambient
    /// loop.</summary>
    AmbientStart,

    /// <summary>A missile is still in range → refresh its in-flight ambient volume
    /// to the current listener distance.</summary>
    AmbientUpdate,

    /// <summary>A missile's ambient loop should stop (it left audible range or was
    /// destroyed).</summary>
    AmbientStop,

    /// <summary>A missile detonated (impact/expiry, seen as its disappearance from
    /// the world's live set) → play the correctly-tiered explosion one-shot at the
    /// detonation site.</summary>
    Explosion,
}

/// <summary>A per-frame snapshot of one live missile fed to
/// <see cref="MissileSoundPlanner.Plan"/>: identity, its current world position,
/// its distance to the local listener, and whether it is the LOCAL ship's own
/// launch (spawn within <see cref="MissileAmbientSoundCurve.LaunchOriginRadius"/> —
/// the port's local-vs-other discrimination, mirroring
/// <see cref="ProjectileAmbientSoundCurve.IsLocalFire"/>).</summary>
/// <param name="MissileId">The missile's ObjectID (short, the planner's key — unlike
/// id-less projectiles, missiles carry a real id on the wire, P234).</param>
/// <param name="Position">Current world position (used as the detonation site if
/// the missile disappears).</param>
/// <param name="DistanceToListener">Distance in world units to the local ship.</param>
/// <param name="IsLocalLaunch">Whether this missile is the local ship's own launch.</param>
public readonly record struct MissileSoundSample(
    short MissileId,
    Vector3 Position,
    float DistanceToListener,
    bool IsLocalLaunch);

/// <summary>A per-frame directive for one missile's sound, emitted by
/// <see cref="MissileSoundPlanner.Plan"/>.</summary>
/// <param name="MissileId">The missile the directive concerns.</param>
/// <param name="Action"><see cref="MissileSoundAction"/>.</param>
/// <param name="Volume">Desired volume (0..1) for AmbientStart/Update/Explosion;
/// 0 for Launch/AmbientStop.</param>
/// <param name="Position">The missile's position at the time of the directive
/// (meaningful for Launch and Explosion).</param>
/// <param name="Tier">The explosion tier for <see cref="MissileSoundAction.Explosion"/>;
/// default for the other actions (ignored).</param>
public readonly record struct MissileSoundCommand(
    short MissileId,
    MissileSoundAction Action,
    float Volume,
    Vector3 Position,
    ExplosionSoundTier Tier);

/// <summary>
/// P236 — pure, MonoGame-free transition tracker for the three missile sound cues.
/// Fed each frame the full live set of in-sector missiles (with positions and
/// listener distances), it decides: which missile is a <i>new</i> local launch (→
/// the launch one-shot), which have an in-flight ambient to start/continue/stop,
/// and which <i>disappeared</i> (impacted/expired → the explosion one-shot at the
/// detonation site). Mirroring native's per-object <c>AmbientSoundSite</c> for the
/// in-flight loop (trekigc.cpp:694-728) and its scale-tiered explosion sound
/// (trekigc.cpp:80-101), this class owns only the <i>decision</i>; the actual audio
/// calls live in <see cref="SoundManager"/>. Being pure, the launch→spawn /
/// in-flight→expire / detonation→explosion contracts are directly unit-testable —
/// the P236 brief's explicit verification target.
/// </summary>
/// <remarks>
/// <para><b>Keying.</b> Unlike id-less projectiles (P232 used reference identity),
/// a missile carries a real <c>MissileID</c> on the wire (P234), so the planner
/// keys by that short id — clean and immune to object-identity tricks.</para>
/// <para><b>Feeding ALL in-sector missiles, not just in-range.</b> To distinguish
/// \"the missile flew out of earshot\" (→ just stop its ambient) from \"the missile
/// was destroyed\" (→ also boom), the caller must feed the <i>complete</i> live
/// missile set in the listener's sector, not a distance-filtered subset: only a
/// missile absent from the full set has genuinely detonated. The planner applies
/// the audible-radius gate itself, so an out-of-range detonation is heard as
/// silence (no boom) and an out-of-range still-flying missile merely fades its
/// ambient.</para>
/// </remarks>
public sealed class MissileSoundPlanner
{
    private readonly HashSet<short> _known = new();
    private readonly HashSet<short> _ambientActive = new();
    private readonly Dictionary<short, MissileSoundSample> _last = new();

    /// <summary>Missile ids currently playing an in-flight ambient. Read-mostly
    /// for diagnostics.</summary>
    public IReadOnlyCollection<short> ActiveAmbients => _ambientActive;

    /// <summary>Missile ids ever seen by the planner (those currently live plus
    /// those awaiting a disappearance pass).</summary>
    public IReadOnlyCollection<short> Known => _known;

    /// <summary>
    /// Compute the per-frame sound directives for the given live, in-sector
    /// missile list (one entry per missile in the listener's sector, paired with
    /// its listener distance). Returns a fresh command list each call; the caller
    /// replays it against the audio backend. Empty input stops every tracked
    /// missile and booms any that were live.
    /// </summary>
    public List<MissileSoundCommand> Plan(IReadOnlyCollection<MissileSoundSample> live)
    {
        var commands = new List<MissileSoundCommand>(live.Count + 2);
        var seen = new HashSet<short>();

        foreach (var s in live)
        {
            seen.Add(s.MissileId);
            bool inRange = s.DistanceToListener <= MissileAmbientSoundCurve.AudibleRadius;

            if (_known.Add(s.MissileId))
            {
                // Newly observed missile → launch cue (if ours) + ambient start (if audible).
                if (s.IsLocalLaunch)
                {
                    commands.Add(new MissileSoundCommand(
                        s.MissileId, MissileSoundAction.Launch, Volume: 0f, s.Position, default));
                }
                if (inRange)
                {
                    _ambientActive.Add(s.MissileId);
                    commands.Add(new MissileSoundCommand(
                        s.MissileId, MissileSoundAction.AmbientStart,
                        MissileAmbientSoundCurve.VolumeForDistance(s.DistanceToListener), s.Position, default));
                }
            }
            else if (_ambientActive.Contains(s.MissileId))
            {
                if (inRange)
                {
                    commands.Add(new MissileSoundCommand(
                        s.MissileId, MissileSoundAction.AmbientUpdate,
                        MissileAmbientSoundCurve.VolumeForDistance(s.DistanceToListener), s.Position, default));
                }
                else
                {
                    _ambientActive.Remove(s.MissileId);
                    commands.Add(new MissileSoundCommand(
                        s.MissileId, MissileSoundAction.AmbientStop, Volume: 0f, s.Position, default));
                }
            }
            else if (inRange)
            {
                _ambientActive.Add(s.MissileId);
                commands.Add(new MissileSoundCommand(
                    s.MissileId, MissileSoundAction.AmbientStart,
                    MissileAmbientSoundCurve.VolumeForDistance(s.DistanceToListener), s.Position, default));
            }

            _last[s.MissileId] = s;
        }

        // Missiles that disappeared since the last pass (impacted / expired /
        // pruned): stop their ambient (if any) and, if the detonation was audible,
        // boom at the last-known site with the default tier.
        foreach (var id in _known.ToList())
        {
            if (seen.Contains(id)) continue;
            _known.Remove(id);
            bool had = _last.Remove(id, out var last);
            if (_ambientActive.Remove(id))
            {
                commands.Add(new MissileSoundCommand(
                    id, MissileSoundAction.AmbientStop, Volume: 0f,
                    had ? last.Position : default, default));
            }
            if (had && last.DistanceToListener <= MissileAmbientSoundCurve.AudibleRadius)
            {
                commands.Add(new MissileSoundCommand(
                    id, MissileSoundAction.Explosion,
                    MissileAmbientSoundCurve.ExplosionVolumeForDistance(last.DistanceToListener),
                    last.Position,
                    ExplosionSoundTierMapper.ForScale(MissileAmbientSoundCurve.DefaultExplosionScale)));
            }
        }

        return commands;
    }
}
