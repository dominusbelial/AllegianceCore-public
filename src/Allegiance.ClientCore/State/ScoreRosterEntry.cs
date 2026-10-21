namespace Allegiance.ClientCore.State;

/// <summary>
/// One per-player roster row of the in-mission scoreboard (name / side /
/// kills / deaths / ejections / rank — exactly the fields with a native
/// in-mission source).
/// <para>P774 — this is not a wire record: the roster is DERIVED, as native,
/// from the per-ship <see cref="PlayerInfo"/> objects
/// (<c>clintlib.h:343-361</c>): PLAYERINFO(10) seeds them
/// (<c>FedSrv/FedSrv.CPP:2056-2135</c>) and KILL_SHIP(105) keeps the counters
/// current (<c>clintlib/appmsg.cpp:2766-2773,2805-2808</c>). The port-only
/// <c>ScoreRosterUpdate</c>(209)/<c>ScoreRosterDelta</c>(219) messages were
/// retired.</para>
/// <para><b>Fields.</b> Kills/Deaths/Ejections are the ship's PlayerInfo
/// counters (native <c>MissionKills()/MissionDeaths()/MissionEjections()</c>,
/// clintlib.h:346-348). Rank is the NA persist entry's rank
/// (<c>GetPersistScore().GetRank()</c>, clintlib.h:359-361). P774b dropped the
/// seven fields with no native display: score and base kills/captures are
/// game-over-only (GAME_OVER_PLAYERS, gameoverscreen.cpp:146-158), and combat
/// rating, rescues, artifacts and flags are never shown (igc.h:5981 and
/// igc.h:5999-6001 commented out; FM_S_PLAYER_RESCUED and FM_S_GAIN_FLAG carry
/// no counter, appmsg.cpp:309-340/3113-3150).</para>
/// </summary>
public readonly record struct ScoreRosterEntry(
    string CharacterName,
    short SideId,
    short Kills,
    short Deaths,
    short Ejections,
    short Rank);
