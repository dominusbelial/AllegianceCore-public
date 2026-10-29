using System.Text.Json;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

/// <summary>
/// P871 / lobby::Create game — the mission a game server was asked to create.
/// This is the lobby's <c>GET /api/games</c>/<c>POST /api/games/{id}/claim</c>
/// payload subset (<c>Allegiance.Lobby.Games.GameEndpoints.GameDto</c>), i.e. the
/// port of the <c>CREATE_MISSION_REQ</c> the lobby forwards to the chosen server
/// (<c>lobby/messageslc.h:54-59</c>, <c>lobby/client.cpp:390-465</c>).
///
/// <para><b>The parameters are the native struct.</b> Native's request carried a
/// packed <c>MissionParams</c>; the lobby relays base64 of
/// <c>MissionParamsMsg.Encode()</c> in <see cref="ParamsWire"/>, so nothing about
/// the wire layout is re-invented here (and no JSON field-reflection is needed,
/// which keeps the server Native-AOT clean).</para>
/// </summary>
public sealed record ClaimedGame(
    string Id,
    string ServerId,
    string Core,
    string Map,
    string GameName,
    string ParamsWire,
    string State);

/// <summary>The claim POST body (<c>Allegiance.Lobby.Games.GameEndpoints.ClaimRequest</c>).</summary>
public sealed record GameClaimRequest(string ServerId, string Token);

/// <summary>
/// P871b — turning a claimed lobby game into a real, playable mission.
///
/// <para><b>Native shape.</b> The lobby picks an idle server, sends it the
/// mission parameters and the server creates the mission; the requesting client
/// then joins that server (<c>lobby/client.cpp:390-465</c>, and
/// <c>FedSrv.CPP:7867-7869</c> for the "created before its players arrive"
/// lifecycle that <c>CheckForNoShows</c> later reaps). The port's rules:</para>
/// <list type="bullet">
/// <item>the core must be the one this process runs (a mission cannot mix
/// cores — <c>CheckedCoreIGC</c>);</item>
/// <item>the map must be a file this server can load from its art path (the port
/// has no map download);</item>
/// <item>the team count follows native's <c>MissionParams::Validate</c> range
/// (<c>Igc/igc.h:1384-1390</c>: 2..<see cref="MaxSides"/> sides);</item>
/// <item>the winning conditions, per-team caps, lives, options and countdowns
/// come from the wizard's parameters (<see cref="MissionParamsMsg"/>), not from
/// the operator's <c>games[]</c> entry: the player chose them.</item>
/// </list>
/// </summary>
public static class PlayerGameProvisioning
{
    /// <summary>Native <c>c_maxSides</c> / <c>MissionParams::cSidesMax</c> (6).</summary>
    public const int MaxSides = 6;

    /// <summary>The native side-name array (<c>FedSrv/fsmission.cpp:23-32</c>),
    /// used for the side names a player-created mission never chooses.</summary>
    public static readonly string[] NativeSideNames =
        ["Team 1", "Team 2", "Team 3", "Team 4", "Team 5", "Team 6"];

    /// <summary>Decode the relayed native <c>MissionParams</c> blob.</summary>
    public static bool TryDecodeParams(string? wire, out MissionParamsMsg parameters, out string? error)
    {
        parameters = new MissionParamsMsg();
        error = null;
        if (string.IsNullOrWhiteSpace(wire)) { error = "the request carries no MissionParams"; return false; }
        try
        {
            parameters = MissionParamsMsg.Decode(Convert.FromBase64String(wire));
            return true;
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            error = "the MissionParams blob is not a native MissionParams struct";
            return false;
        }
    }

    /// <summary>
    /// Build the <see cref="GameConfig"/> this claimed game becomes.
    /// <paramref name="serverCoreFile"/> is the IGC static file this process
    /// loaded (native's <c>CheckedCoreIGC</c>); a request for any other core is
    /// refused rather than silently substituted.
    /// </summary>
    public static GameConfig? TryCreate(ClaimedGame claim, MissionParamsMsg parameters,
        string serverCoreFile, out string? error)
    {
        error = null;
        string core = Path.GetFileName(serverCoreFile ?? "");
        if (!string.Equals(Path.GetFileName(claim.Core ?? ""), core, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(parameters.SzIgcStaticFile ?? ""), core, StringComparison.OrdinalIgnoreCase))
        {
            error = $"the request is for core '{claim.Core}', this server runs '{core}'";
            return null;
        }
        if (string.IsNullOrWhiteSpace(claim.Map))
        {
            error = "the request names no map";
            return null;
        }
        short teams = parameters.NTems;
        if (teams < 2 || teams > MaxSides)
        {
            error = $"MissionParams::Validate requires 2..{MaxSides} teams, got {teams} (Igc/igc.h:1384-1390)";
            return null;
        }
        // Native replays NewGameScreen::OnButtonCreate's Invalid(true) before the
        // request leaves the client (WinTrek/newgamescreen.cpp:1034-1041); the
        // server keeps the same gate on what it adopts.
        if (parameters.Invalid(bFromClient: true) is { } why)
        {
            error = why;
            return null;
        }

        var civIds = new int[teams];
        var sideNames = new string[teams];
        for (int i = 0; i < teams; i++)
        {
            civIds[i] = i < parameters.RgCivId.Length ? parameters.RgCivId[i] : -1;
            sideNames[i] = NativeSideNames[i];
        }

        var game = new GameConfig
        {
            Name = claim.GameName,
            Map = claim.Map.Trim(),
            Teams = teams,
            CivIds = civIds,
            SideNames = sideNames,
            MaxPlayersPerTeam = parameters.NMaxPlayersPerTeam,
            MinimumPlayersPerTeam = parameters.NMinPlayersPerTeam,
            AllowEmptyTeams = parameters[MissionParamsMsg.BitAllowEmptyTeams],
            AllowDevelopments = parameters[MissionParamsMsg.BitAllowDevelopments],
            AllowShipyardPath = parameters[MissionParamsMsg.BitAllowShipyardPath],
            AllowTacticalPath = parameters[MissionParamsMsg.BitAllowTacticalPath],
            AllowSupremacyPath = parameters[MissionParamsMsg.BitAllowSupremacyPath],
            AllowExpansionPath = parameters[MissionParamsMsg.BitAllowExpansionPath],
            AllowDefections = parameters[MissionParamsMsg.BitAllowDefections],
            AllowAlliedRip = parameters[MissionParamsMsg.BitAllowAlliedRip],
            AllowAlliedViz = parameters[MissionParamsMsg.BitAllowAlliedViz],
            AllowFriendlyFire = parameters[MissionParamsMsg.BitAllowFriendlyFire],
            ShowMap = parameters[MissionParamsMsg.BitShowMap],
            ShowHomeSector = parameters[MissionParamsMsg.BitShowHomeSector],
            SquadGame = parameters[MissionParamsMsg.BitSquadGame],
            Lives = parameters.ILives,
            InitialMinersPerTeam = parameters.NInitialMinersPerTeam,
            AutoStart = parameters[MissionParamsMsg.BitAutoStart],
            AutoRestart = parameters[MissionParamsMsg.BitAutoRestart],
            StartCountdownSeconds = parameters.FStartCountdown > 0 ? parameters.FStartCountdown : null,
            RestartCountdownSeconds = parameters.FRestartCountdown > 0 ? parameters.FRestartCountdown : null,
            GameLengthSeconds = parameters.DtGameLength > 0 ? parameters.DtGameLength : null,
            Goals = new Allegiance.Core.Model.MissionGoals
            {
                // Native's MissionParams winning conditions (Igc/igc.h:1180-1214).
                ConquestPercentage = parameters.IGoalConquestPercentage,
                TerritoryPercentage = parameters.IGoalTerritoryPercentage,
                Flags = parameters.NGoalFlagsCount,
                Artifacts = parameters.NGoalArtifactsCount,
                TeamKills = parameters.NGoalTeamKills,
                TeamMoney = parameters.FGoalTeamMoney,
                GameLengthSeconds = parameters.DtGameLength,
            },
            Treasure = new Allegiance.Core.Model.TreasureParameters
            {
                PlayerSectorTreasureRate = parameters.NPlayerSectorTreasureRate,
                NeutralSectorTreasureRate = parameters.NNeutralSectorTreasureRate,
                PlayerStartSet = parameters.TsiPlayerStart,
                NeutralStartSet = parameters.TsiNeutralStart,
                PlayerRegenerateSet = parameters.TsiPlayerRegenerate,
                NeutralRegenerateSet = parameters.TsiNeutralRegenerate,
                AllowDevelopments = parameters[MissionParamsMsg.BitAllowDevelopments],
            },
        };
        return game;
    }

    /// <summary>The claim poll response (a JSON array of the lobby's live
    /// requests) — one entry per non-cancelled request.</summary>
    public static IReadOnlyList<ClaimedGame>? ReadGames(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, ServerJsonContext.Default.ClaimedGameArray);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
