using Allegiance.Core.Collision;

namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>Action a <see cref="ProbeSoundPlanner"/> wants taken on one probe's
/// sound this frame.</summary>
public enum ProbeSoundAction
{
    /// <summary>A probe was just observed deployed by the LOCAL ship → play the
    /// deploy one-shot (native <c>if (pShip == m_ship) PlaySoundEffect(deployProbeSound,
    /// pShip)</c>, <c>clintlib.cpp:3245-3257</c>).</summary>
    Deploy,

    /// <summary>An own-side probe came into range → begin its continuous scanning
    /// ambient loop.</summary>
    AmbientStart,

    /// <summary>An own-side probe is still in range → refresh its ambient volume
    /// to the current listener distance.</summary>
    AmbientUpdate,

    /// <summary>An own-side probe's ambient loop should stop (it left audible
    /// range, left the local sector, or was destroyed/expired).</summary>
    AmbientStop,
}

/// <summary>A per-frame snapshot of one live probe fed to
/// <see cref="ProbeSoundPlanner.Plan"/>: identity, its (fixed) world position,
/// its distance to the local listener, whether it belongs to the player's OWN
/// side (gates the ambient loop), and whether it was the LOCAL ship's own deploy
/// (gates the deploy one-shot).</summary>
/// <param name="ProbeId">The probe's ObjectID (short, the planner's key).</param>
/// <param name="Position">The probe's fixed world position.</param>
/// <param name="DistanceToListener">Distance in world units to the local ship.</param>
/// <param name="IsOwnSide">Whether the probe is owned by the player's own side
/// (<c>ProbeEntity.SideId == ClientWorld.MySideId</c>) — only own-side probes get
/// the scanning ambient loop.</param>
/// <param name="IsOwnDeploy">Whether this probe was deployed by the LOCAL ship
/// (<c>ProbeEntity.LauncherShipId == MyShipId</c>) — only that deploys the one-shot
/// (native's <c>pShip == m_ship</c> gate).</param>
public readonly record struct ProbeSoundSample(
    short ProbeId,
    Vector3 Position,
    float DistanceToListener,
    bool IsOwnSide,
    bool IsOwnDeploy);

/// <summary>A per-frame directive for one probe's sound, emitted by
/// <see cref="ProbeSoundPlanner.Plan"/>.</summary>
/// <param name="ProbeId">The probe the directive concerns.</param>
/// <param name="Action"><see cref="ProbeSoundAction"/>.</param>
/// <param name="Volume">Desired volume (0..1) for AmbientStart/Update; 0 for
/// Deploy/AmbientStop.</param>
/// <param name="Position">The probe's position at the time of the directive
/// (meaningful for Deploy).</param>
public readonly record struct ProbeSoundCommand(
    short ProbeId,
    ProbeSoundAction Action,
    float Volume,
    Vector3 Position);

/// <summary>
/// P260 — pure, MonoGame-free transition tracker for the two probe sound cues.
/// Fed each frame the full live set of in-sector probes (with positions,
/// listener distances, own-side and own-deploy attribution), it decides: which
/// probe is a <i>new</i> local deploy (→ the deploy one-shot), which own-side
/// probes have a scanning ambient to start/continue/stop, and which
/// <i>disappeared</i> (destroyed or expired → stop the ambient). Mirroring
/// <see cref="MissileSoundPlanner"/>'s shape (minus the missile explosion cue —
/// a destroyed probe has no detonation sound, it just stops scanning), this class
/// owns only the <i>decision</i>; the actual audio calls live in
/// <see cref="SoundManager"/>. Being pure, the deploy→spawn /
/// ambient-active-while-own-side / disappear→stop contracts are directly
/// unit-testable — the P260 brief's explicit verification target.
/// </summary>
/// <remarks>
/// <para><b>Keying.</b> Like a missile, a probe carries a real <c>ProbeId</c> on
/// the wire (P224), so the planner keys by that short id.</para>
/// <para><b>Feeding ALL in-sector probes, not just in-range.</b> To distinguish
/// "the probe went out of earshot / the player left the sector" (→ just stop its
/// ambient) from "the probe was destroyed/expired" (→ also stop, no further cue),
/// the caller must feed the <i>complete</i> live probe set in the listener's
/// sector, not a distance-filtered subset: only a probe absent from the full set
/// has genuinely gone. The planner applies the audible-radius gate itself, so an
/// out-of-range own-side probe merely fades its ambient and an out-of-range
/// deploy is not heard.</para>
/// <para><b>Ambient is own-side only.</b> The ambient scanning loop plays while a
/// deployed probe <i>owned by the player's own side</i> is active in the current
/// sector (the brief's scope). An enemy probe's loop is not started here, though
/// it still occupies a planner key so it can be watched and cleaned up.</para>
/// </remarks>
public sealed class ProbeSoundPlanner
{
    private readonly HashSet<short> _known = new();
    private readonly HashSet<short> _ambientActive = new();
    private readonly Dictionary<short, ProbeSoundSample> _last = new();

    /// <summary>Probe ids currently playing a scanning ambient. Read-mostly for
    /// diagnostics.</summary>
    public IReadOnlyCollection<short> ActiveAmbients => _ambientActive;

    /// <summary>Probe ids ever seen by the planner (those currently live plus
    /// those awaiting a disappearance pass).</summary>
    public IReadOnlyCollection<short> Known => _known;

    /// <summary>
    /// Compute the per-frame sound directives for the given live, in-sector
    /// probe list (one entry per probe in the listener's sector, paired with its
    /// listener distance and attribution). Returns a fresh command list each call;
    /// the caller replays it against the audio backend. Empty input stops every
    /// tracked ambient. <paramref name="audibleRadiusOverride"/> (P893) widens
    /// the ambient start/stop gate while 3D is on: native admits every in-sector
    /// sound and lets the positional engine + voice budget decide (the −60 dB
    /// range for the probe's ThreeDSound(200) template is 200,000 units), so the
    /// 3D path passes that range instead of the hand-tuned curve radius.</summary>
    public List<ProbeSoundCommand> Plan(IReadOnlyCollection<ProbeSoundSample> live,
        float? audibleRadiusOverride = null)
    {
        float range = audibleRadiusOverride ?? ProbeAmbientSoundCurve.AudibleRadius;
        var commands = new List<ProbeSoundCommand>(live.Count + 2);
        var seen = new HashSet<short>();

        foreach (var s in live)
        {
            seen.Add(s.ProbeId);
            bool inRange = s.DistanceToListener <= range;

            if (_known.Add(s.ProbeId))
            {
                // Newly observed probe → deploy cue (if ours) + ambient start
                // (if own side and audible).
                if (s.IsOwnDeploy)
                {
                    commands.Add(new ProbeSoundCommand(
                        s.ProbeId, ProbeSoundAction.Deploy, Volume: 0f, s.Position));
                }
                if (s.IsOwnSide && inRange)
                {
                    _ambientActive.Add(s.ProbeId);
                    commands.Add(new ProbeSoundCommand(
                        s.ProbeId, ProbeSoundAction.AmbientStart,
                        ProbeAmbientSoundCurve.VolumeForDistance(s.DistanceToListener), s.Position));
                }
            }
            else if (_ambientActive.Contains(s.ProbeId))
            {
                if (s.IsOwnSide && inRange)
                {
                    commands.Add(new ProbeSoundCommand(
                        s.ProbeId, ProbeSoundAction.AmbientUpdate,
                        ProbeAmbientSoundCurve.VolumeForDistance(s.DistanceToListener), s.Position));
                }
                else
                {
                    _ambientActive.Remove(s.ProbeId);
                    commands.Add(new ProbeSoundCommand(
                        s.ProbeId, ProbeSoundAction.AmbientStop, Volume: 0f, s.Position));
                }
            }
            else if (s.IsOwnSide && inRange)
            {
                _ambientActive.Add(s.ProbeId);
                commands.Add(new ProbeSoundCommand(
                    s.ProbeId, ProbeSoundAction.AmbientStart,
                    ProbeAmbientSoundCurve.VolumeForDistance(s.DistanceToListener), s.Position));
            }

            _last[s.ProbeId] = s;
        }

        // Probes that disappeared since the last pass (destroyed / expired /
        // left the local sector): stop their ambient (if any). No explosion cue —
        // a probe just stops scanning.
        foreach (var id in _known.ToList())
        {
            if (seen.Contains(id)) continue;
            _known.Remove(id);
            _last.Remove(id);
            if (_ambientActive.Remove(id))
            {
                commands.Add(new ProbeSoundCommand(
                    id, ProbeSoundAction.AmbientStop, Volume: 0f, default));
            }
        }

        return commands;
    }
}
