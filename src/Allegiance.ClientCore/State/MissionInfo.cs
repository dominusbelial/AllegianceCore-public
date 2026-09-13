namespace Allegiance.ClientCore.State;

/// <summary>
/// Small persistent state about the mission itself — deliberately distinct from
/// any one ship in the entity table. Populated from the server's
/// <c>MISSIONDEF(38)</c> (static-core file/version, cookie, side-owner, stage)
/// and refined by <c>MISSION_STAGE(37)</c> (which updates just the stage).
/// An immutable snapshot: each mutation in <see cref="ClientWorld"/> replaces
/// this instance wholesale rather than mutating it in place.
/// </summary>
public sealed class MissionInfo
{
    /// <summary>The extensionless base name of the mission's static core file
    /// (e.g. "PCore001") — <c>MissionDef.SzIGCStaticFile</c>.</summary>
    public string StaticCoreFile { get; init; } = "";

    /// <summary>The static-core version word (<c>MissionDef.VerIgcCore</c>).</summary>
    public int StaticCoreVersion { get; init; }

    /// <summary>The mission cookie (<c>MissionDef.DwCookie</c>).</summary>
    public uint Cookie { get; init; }

    /// <summary>The lobby team-leader side (<c>MissionDef.ISideMissionOwner</c>).</summary>
    public short SideMissionOwner { get; init; }

    /// <summary>The mission stage — one of <see cref="MissionStageMessage"/>'s
    /// <c>StageStarted</c>/<c>StageOver</c>/... constants. Whichever of
    /// <c>MISSIONDEF</c> or <c>MISSION_STAGE</c> arrived last sets it (see the
    /// comment in <see cref="ClientWorld.Apply(MissionDef)"/>).</summary>
    public int Stage { get; init; }
}
