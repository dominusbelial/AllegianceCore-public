using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>Action a <see cref="ProjectileSoundPlanner"/> wants taken on one
/// projectile's ambient sound this frame.</summary>
public enum ProjectileSoundAction
{
    /// <summary>The projectile just came into range → begin its in-flight
    /// ambient loop (and, if <see cref="ProjectileSoundCommand.IsLocalFire"/>,
    /// play the weapon-fire one-shot that produced it).</summary>
    Start,

    /// <summary>The projectile is still in range → refresh its ambient volume
    /// to the current listener distance.</summary>
    Update,

    /// <summary>The projectile expired or left audible range → stop (and
    /// release) its ambient loop.</summary>
    Stop,
}

/// <summary>A per-frame directive for one projectile's sound, emitted by
/// <see cref="ProjectileSoundPlanner.Plan"/>.</summary>
/// <param name="Projectile">The projectile entity the directive concerns
/// (reference-identity keyed, same object across frames until pruned).</param>
/// <param name="Action"><see cref="ProjectileSoundAction"/>.</param>
/// <param name="Volume">Desired ambient volume (0..1) for Start/Update; 0 for
/// Stop.</param>
/// <param name="IsLocalFire">True only on a Start when the projectile spawned
/// within <see cref="ProjectileAmbientSoundCurve.FireOriginRadius"/> of the
/// listener — i.e. the local ship's own shot, deserving the weapon-fire
/// one-shot.</param>
public readonly record struct ProjectileSoundCommand(
    ProjectileEntity Projectile,
    ProjectileSoundAction Action,
    float Volume,
    bool IsLocalFire);

/// <summary>
/// P232 — pure, MonoGame-free transition tracker for projectile in-flight
/// ambient sounds. Fed each frame the list of live projectiles currently within
/// audible range of the listener (with their distances), it decides which
/// projectiles have <i>newly</i> entered range (→ Start), which continue (→
/// Update with the fresh distance-scaled volume), and which disappeared
/// (expired or left range → Stop). Mirroring native's per-object
/// <c>AmbientSoundSite</c> that plays a projectile's ambient while it exists
/// (trekigc.cpp:694-728), this class owns only the <i>decision</i>; the actual
/// audio calls live in <see cref="SoundManager"/>. Being pure, the
/// spawn→Start / expire→Stop contract is directly unit-testable — the P232
/// brief's explicit verification target.
/// </summary>
/// <remarks>
/// <para><b>Keying.</b> Projectiles carry no wire id (native fire-and-forget),
/// so an entity's <see cref="object"/> reference identity is the key: the same
/// <see cref="ProjectileEntity"/> object persists in <c>ClientWorld.Projectiles</c>
/// from spawn until pruned, so reference equality reliably distinguishes
/// "still the same bolt" from "a new bolt."</para>
/// <para><b>Stop semantics.</b> A projectile leaves the caller's live-within-range
/// list when it expires (pruned) <i>or</i> when it moves past the audible radius.
/// Both are correctly reflected as a Stop here, because the caller filters the
/// list to within-range projectiles — the sound stops whether the bolt died or
/// simply faded out of earshot.</para>
/// </remarks>
public sealed class ProjectileSoundPlanner
{
    private readonly HashSet<ProjectileEntity> _active = new();

    /// <summary>The projectiles currently considered to have a live ambient
    /// sound (those started and not yet stopped). Read-mostly for diagnostics.</summary>
    public IReadOnlyCollection<ProjectileEntity> Active => _active;

    /// <summary>
    /// Compute the per-frame sound directives for the given live, in-range
    /// projectile list (one entry per projectile currently audible to the
    /// listener, paired with its listener distance). Returns a fresh command
    /// list each call; the caller replays it against the audio backend. Empty
    /// input stops every tracked projectile.
    /// </summary>
    public List<ProjectileSoundCommand> Plan(IReadOnlyCollection<(ProjectileEntity Entity, float Distance)> live)
    {
        var commands = new List<ProjectileSoundCommand>(live.Count + 2);

        var seen = new HashSet<ProjectileEntity>();
        foreach (var (entity, distance) in live)
        {
            seen.Add(entity);
            float volume = ProjectileAmbientSoundCurve.VolumeForDistance(distance);
            if (_active.Add(entity))
            {
                commands.Add(new ProjectileSoundCommand(
                    entity, ProjectileSoundAction.Start, volume,
                    ProjectileAmbientSoundCurve.IsLocalFire(distance)));
            }
            else
            {
                commands.Add(new ProjectileSoundCommand(
                    entity, ProjectileSoundAction.Update, volume, IsLocalFire: false));
            }
        }

        foreach (var entity in _active.ToList())
        {
            if (!seen.Contains(entity))
            {
                _active.Remove(entity);
                commands.Add(new ProjectileSoundCommand(
                    entity, ProjectileSoundAction.Stop, Volume: 0f, IsLocalFire: false));
            }
        }

        return commands;
    }
}
