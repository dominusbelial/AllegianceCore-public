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

    /// <summary>P853b — the WHOLE <c>MissionParams</c> block the server
    /// advertised in MISSIONDEF(38), i.e. the same struct native's client
    /// memcpy's out of the misdef (<c>appmsg.cpp:2416</c> →
    /// <c>clintlib.cpp:670-673</c>). The host-editing UI is the only reader: the
    /// mission owner's game-settings form starts from this snapshot and re-sends
    /// it with only the user's edits applied — the native contract
    /// (<c>newgamescreen.cpp:968-1031</c> + <c>FedSrv.CPP:7518-7597</c> refuses
    /// an echo that disagrees on the immutable fields and adopts everything else
    /// wholesale, <c>fsmission.cpp:1843</c>). Null until a MISSIONDEF carrying
    /// the block lands (a bare stage refinement keeps the previous one).
    /// Immutable by convention: callers must <see cref="Allegiance.Core.Wire.MissionParamsMsg.Clone"/>
    /// before editing.</summary>
    public Allegiance.Core.Wire.MissionParamsMsg? Params { get; init; }

    /// <summary>P853b — native <c>MissionParams.bSquadGame</c> (<c>Igc/igc.h:1142</c>).
    /// A squad game replaces the team-name edit with the squad picker
    /// (<c>WinTrek/teamscreen.cpp:960-965</c>), so the port hides the name edit
    /// there rather than sending a rename the server would treat as a squad
    /// re-point.</summary>
    public bool SquadGame => Params?[Allegiance.Core.Wire.MissionParamsMsg.BitSquadGame] ?? false;

    /// <summary>P853b — native <c>MissionParams.bLockTeamSettings</c>
    /// (<c>Igc/igc.h:1138</c>): the server silently ignores FM_CS_SET_TEAM_INFO
    /// while it is set (<c>FedSrv.CPP:7433-7436</c>) and native disables the team
    /// settings button (<c>teamscreen.cpp:1594</c>).</summary>
    public bool LockTeamSettings => Params?[Allegiance.Core.Wire.MissionParamsMsg.BitLockTeamSettings] ?? false;
}
