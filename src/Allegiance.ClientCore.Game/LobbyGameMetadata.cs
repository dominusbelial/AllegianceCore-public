using System.Globalization;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P806 — the game settings a directory listing carries, mirroring native
/// <c>MissionInfo</c>'s accessors on the client (<c>clintlib/clintlib.h:540-557</c>).
/// Native receives them inside the mission definition the lobby relays; this
/// port's HTTP directory publishes them on the listing (see
/// <c>Allegiance.Lobby.GameParamsDto</c>), so the client evaluates the server
/// list's filters and sort columns from exactly the same facts the native client
/// reads out of <c>m_pfmMissionDef-&gt;misparms</c>.
///
/// <para><b>Which values are genuinely carried.</b> Everything with a port
/// equivalent comes from the server's live mission: developments, lives, the
/// seven win conditions, the rank window, the running/waiting state. Native
/// additionally reads <c>bInvulnerableStations</c>, <c>bEjectPods</c> and
/// <c>bExperimental</c> when it classifies a game type — this port does not model
/// those mission parameters, so <see cref="GameTypeIndex"/> uses native
/// <c>MissionParams::Reset</c>'s defaults for them (invulnerable off, eject pods
/// on, not experimental — <c>Igc/igc.h:1246-1310</c>), exactly the shape every
/// port mission actually runs.</para>
/// </summary>
public sealed record GameParams(
    bool AllowDevelopments,
    int Lives,
    bool ScoresCount,
    bool ObjectModelCreated,
    bool InProgress,
    bool GoalConquest,
    bool GoalDeathmatch,
    bool GoalProsperity,
    bool GoalCountdown,
    bool GoalArtifacts,
    bool GoalFlags,
    bool GoalTerritory,
    short MinRank,
    short MaxRank,
    // P856 — the two native create-game options the Game Details summary prints
    // ("Allow Friendly Fire: Yes/No", newgamescreen.cpp:347). Optional trailing
    // members so a pre-P856 host/listing keeps its shape; native default off.
    bool AllowFriendlyFire = false,
    bool AllowAlliedViz = false,
    // P866 / SIM-0537 — the rest of the server's pfmLobbyMissionInfo payload
    // (FedSrv/fsmission.cpp:3983-4096), now carried end-to-end through the HTTP
    // directory. The two values native's game-type classifier reads
    // (bInvulnerableStations / bEjectPods) are now REAL here instead of assumed
    // Reset defaults; everything else is informational for the client.
    int NumPlayers = 0,
    int NumNoatPlayers = 0,
    short Teams = 0,
    int MaxPlayersPerGame = 0,
    short MinPlayersPerTeam = 0,
    short MaxPlayersPerTeam = 0,
    uint StartTimeOffsetMs = 0,
    bool CountdownStarted = false,
    bool SquadGame = false,
    bool EjectPods = true,
    bool InvulnerableStations = false,
    bool AnySlotsAvailable = false,
    bool GuaranteedSlotsAvailable = false,
    IReadOnlyList<string>? SquadIds = null)
{
    /// <summary><c>c_cUnlimitedLives = 0x7fff</c> (<c>Igc/igc.h:1007</c>).</summary>
    public const int UnlimitedLives = 0x7fff;

    /// <summary>Native <c>MissionInfo::LimitedLives()</c>
    /// (<c>clintlib/clintlib.h:549</c>: <c>iLives != 0x7fff</c>).</summary>
    public bool LimitedLives => Lives != UnlimitedLives;

    /// <summary>P856 — native's Game Details wording for a boolean option
    /// (<c>YesNo()</c>, <c>WinTrek/newgamescreen.cpp:347</c>).</summary>
    public static string YesNo(bool value) => value ? "Yes" : "No";

    /// <summary>The lives line: the count, or "unlimited" for the sentinel.</summary>
    public string LivesLabel() => LimitedLives
        ? Lives.ToString(CultureInfo.InvariantCulture)
        : "unlimited";

    /// <summary>The active winning conditions in native's own order
    /// (<c>Igc/igc.h:1511-1540</c>), "none" when no goal bit is set (native
    /// <c>IsNoWinningConditionsGame</c>).</summary>
    public string GoalSummary()
    {
        var goals = new List<string>(7);
        if (GoalConquest) goals.Add("conquest");
        if (GoalDeathmatch) goals.Add("death match");
        if (GoalCountdown) goals.Add("countdown");
        if (GoalProsperity) goals.Add("prosperity");
        if (GoalArtifacts) goals.Add("artifacts");
        if (GoalTerritory) goals.Add("territorial");
        if (GoalFlags) goals.Add("flags");
        return goals.Count == 0 ? "none" : string.Join(", ", goals);
    }

    /// <summary>Native's settings-sort input: the index of the first
    /// <c>GameType</c> whose <c>IsGameType</c> predicate matches
    /// (<c>GameTypeIndex</c>/<c>SettingsCompare</c>, <c>WinTrek/gamescreen.cpp:1135-1168</c>;
    /// the table is <c>WinTrek/gametypes.cpp</c>).</summary>
    public int GameTypeIndex => NativeGameTypes.IndexOf(this);

    /// <summary>The game-type name, or "custom" for native's list-end index
    /// (a mission matching none of the preset types).</summary>
    public static string GameTypeName(GameParams p) => NativeGameTypes.NameOf(p.GameTypeIndex);
}

/// <summary>
/// P806 — the native <c>GameType</c> table (<c>WinTrek/gametypes.cpp</c>) used by
/// the server list's SETTINGS sort column. Native builds these definitions in
/// <c>VerifyGameTypeInitialization</c> and picks the first whose
/// <c>IsGameType</c> matches the mission params; a mission matching none sorts at
/// the list end (the <c>GameTypeIndex</c> loop's exit index).
/// </summary>
public static class NativeGameTypes
{
    /// <summary>One preset, reduced to the predicate <c>GameTypeImpl::IsGameType</c>
    /// actually compares (<c>WinTrek/gametypes.cpp:70-96</c>): each field is the
    /// "is it set/default" state, never a threshold value.</summary>
    public readonly record struct Definition(
        string Name,
        bool AllowDevelopments,
        bool InvulnerableStations,
        bool EjectPods,
        bool UnlimitedLives,
        bool GoalTeamMoney,      // fGoalTeamMoney != 0
        bool GoalConquest,       // iGoalConquestPercentage != 0
        bool GoalFlags,          // nGoalFlagsCount != 0
        bool GoalArtifacts,      // nGoalArtifactsCount != 0
        bool GoalTeamKills,      // nGoalTeamKills != 0
        bool GoalTerritory,      // iGoalTerritoryPercentage != 100
        bool GameLength,         // dtGameLength != 0
        bool Experimental);

    /// <summary>The native list, in order (Conquest, Death Match, Capture the
    /// Flag, Artifacts, Territorial, Experimental — <c>gametypes.cpp:118-176</c>).</summary>
    public static readonly Definition[] Types =
    {
        new("Conquest", true, false, true, true, false, true, false, false, false, false, false, false),
        new("Death Match", false, true, false, true, false, false, false, false, true, false, false, false),
        new("Capture the Flag", false, true, true, true, false, false, true, false, false, false, false, false),
        new("Artifacts", true, false, true, true, false, false, false, true, false, false, false, false),
        new("Territorial", true, false, true, true, false, false, false, false, false, true, false, false),
        new("Experimental", true, false, true, true, false, true, false, false, false, false, false, true),
    };

    /// <summary>The index of the first matching preset, or <c>Types.Length</c>
    /// (native <c>GameTypeIndex</c>'s exit index — "custom").</summary>
    public static int IndexOf(GameParams p)
    {
        for (int i = 0; i < Types.Length; i++)
        {
            var t = Types[i];
            if (p.AllowDevelopments == t.AllowDevelopments
                // P866 — bInvulnerableStations / bEjectPods now ride the listing
                // (native MissionParams bits, published by the server), so the
                // classifier uses the real values. Their defaults are native
                // Reset's (invulnerable off, eject pods on), so a pre-P866 host
                // keeps the exact pre-P866 classification. bExperimental has no
                // port mission parameter (Reset default off).
                && p.InvulnerableStations == t.InvulnerableStations
                && p.EjectPods == t.EjectPods
                && !t.Experimental
                && p.LimitedLives == !t.UnlimitedLives
                && p.GoalProsperity == t.GoalTeamMoney
                && p.GoalConquest == t.GoalConquest
                && p.GoalFlags == t.GoalFlags
                && p.GoalArtifacts == t.GoalArtifacts
                && p.GoalDeathmatch == t.GoalTeamKills
                && p.GoalTerritory == t.GoalTerritory
                && p.GoalCountdown == t.GameLength)
                return i;
        }
        return Types.Length;
    }

    /// <summary>The preset name for an index, "custom" for the list-end index.</summary>
    public static string NameOf(int index)
        => index >= 0 && index < Types.Length ? Types[index].Name : "custom";
}
