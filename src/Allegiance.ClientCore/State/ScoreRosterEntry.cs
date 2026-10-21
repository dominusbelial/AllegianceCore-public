namespace Allegiance.ClientCore.State;

/// <summary>
/// One per-player roster row of the in-mission scoreboard (name / side /
/// kills / deaths / rank today; the other seven fields read 0 until P774b
/// gives them their native displays).
/// <para>P774 — this is no longer a wire record: the roster is DERIVED, as
/// native, from the per-ship <see cref="PlayerInfo"/> objects
/// (<c>clintlib.h:343-361</c>): PLAYERINFO(10) seeds them
/// (<c>FedSrv/FedSrv.CPP:2056-2135</c>) and KILL_SHIP(105) keeps the counters
/// current (<c>clintlib/appmsg.cpp:2766-2773,2805-2808</c>). The port-only
/// <c>ScoreRosterUpdate</c>(209)/<c>ScoreRosterDelta</c>(219) messages were
/// retired; the record keeps its 13-field shape so the UI layouts and their
/// tests barely change.</para>
/// <para><b>Fields.</b> Kills/Deaths/Ejections are the ship's PlayerInfo
/// counters (native <c>MissionKills()/MissionDeaths()/MissionEjections()</c>,
/// clintlib.h:346-348). Rank is the NA persist entry's rank
/// (clintlib.cpp:1042-1051). Score/combat rating/rescues/base kills/base
/// captures/artifacts/flags have no native in-mission source (native shows
/// score and base kills/captures only at game over from GAME_OVER_PLAYERS,
/// gameoverscreen.cpp:146-158; combat rating is never shown, igc.h:5981) and
/// stay 0 until P774b drops them from the displays.</para>
/// </summary>
public readonly record struct ScoreRosterEntry(
    string CharacterName,
    short SideId,
    short Kills,
    short Deaths,
    short Rank,
    float Score,
    float CombatRating,
    short Ejections = 0,
    short Rescues = 0,
    short BaseKills = 0,
    short BaseCaptures = 0,
    short Artifacts = 0,
    short Flags = 0);
