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

    /// <summary>Native dtGameLength; zero means no timed-game countdown.</summary>
    public Allegiance.Core.Model.MissionGoals Goals { get; init; } = new();
    public float GameLengthSeconds { get => Goals.GameLengthSeconds; init => Goals.GameLengthSeconds = value; }

    /// <summary>HUD-12 — native <c>MissionParams::iLives</c>
    /// (<c>src/Igc/igc.h:1185</c>; unlimited = 0x7fff). The lives badge shows
    /// <c>ILives − mission deaths</c> only while that is below 10
    /// (<c>WinTrek/console.cpp:812-817</c>).</summary>
    public short ILives { get; init; } = Allegiance.Core.Wire.MissionDef.UnlimitedLives;

    /// <summary>The lobby team-leader side (<c>MissionDef.ISideMissionOwner</c>).</summary>
    public short SideMissionOwner { get; init; }

    /// <summary>The mission stage — one of <see cref="MissionStageMessage"/>'s
    /// <c>StageStarted</c>/<c>StageOver</c>/... constants. Whichever of
    /// <c>MISSIONDEF</c> or <c>MISSION_STAGE</c> arrived last sets it (see the
    /// comment in <see cref="ClientWorld.Apply(MissionDef)"/>).</summary>
    public int Stage { get; init; }

    /// <summary>P761 — <c>MissionParams.bLockGameOpen</c> (igc.h:1139), decoded
    /// from MISSIONDEF's parameter bitfields. Gates Auto-Accept and Lock Lobby
    /// OFF in the team lobby (native: enabled only while the game-open lock is
    /// off, teamscreen.cpp:1563-1569).</summary>
    public bool LockGameOpen { get; init; }

    /// <summary>P856 — <c>MissionParams.bAllowFriendlyFire</c>
    /// (<c>src/Igc/igc.h:1149</c>), decoded from the MISSIONDEF bitfield block.
    /// Native's client reads it from the mission definition itself
    /// (<c>MissionInfo::GetMissionParams()</c>, <c>clintlib/clintlib.h:557</c>).</summary>
    public bool AllowFriendlyFire { get; init; }

    /// <summary>P856 — <c>MissionParams.bAllowAlliedViz</c>
    /// (<c>src/Igc/igc.h:1127</c>), decoded from the MISSIONDEF bitfield block.</summary>
    public bool AllowAlliedViz { get; init; }
}
