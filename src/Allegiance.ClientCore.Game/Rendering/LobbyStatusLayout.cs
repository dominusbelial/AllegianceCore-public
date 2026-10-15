using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>Live lobby notices, appended to side-select and ready-check.
/// Mirrors appmsg.cpp:2470-2499,2602-2627 and teamscreen.cpp:3545-3658.</summary>
public static class LobbyStatusLayout
{
    // Native salPlayerWaitingSound, Igc/sounds.h:501; sounddef.mdl:824.
    public const string PlayerWaitingWave = "vo_sal_playerwaiting";

    /// <summary>Call under ConnectionSession.Gate, like the rest of the roster.</summary>
    public static IReadOnlyList<string> Build(ClientWorld world)
    {
        var lines = new List<string>
        {
            $"LOBBY: {(world.LobbyLocked ? "LOCKED" : "OPEN")}  SIDE CHANGES: {(world.SidesLocked ? "LOCKED" : "OPEN")}",
        };
        foreach (var (side, accept) in world.AutoAcceptBySideId.OrderBy(e => e.Key))
            lines.Add($"SIDE {side} AUTO ACCEPT: {(accept ? "ON" : "OFF")}");
        foreach (var request in world.PendingPositionRequests.OrderBy(r => r.SideId).ThenBy(r => r.ShipId))
        {
            string name = world.Ships.TryGetValue(request.ShipId, out var player) && !string.IsNullOrWhiteSpace(player.CharacterName)
                ? ScoreboardLayout.AsciiSafe(player.CharacterName) : $"Pilot {request.ShipId}";
            lines.Add($"{name} awaiting approval for side {request.SideId}");
        }
        if (world.MyPositionRequestResult is { } result
            && result.ShipId == (world.JoinedShipId ?? world.MyShipId)
            && result.Reason != DelPositionReqReason.Canceled)
            lines.Add(RequestFailureText(result.Reason));
        return lines;
    }

    public static string RequestFailureText(DelPositionReqReason reason) => reason switch
    {
        DelPositionReqReason.Rejected => "You have not been accepted to the team.",
        DelPositionReqReason.NoBase => "You can't join this team - it no longer owns a base.",
        DelPositionReqReason.TeamFull => "You can't join this team - it's full.",
        DelPositionReqReason.TeamBalance => "You can't join this team - it would unbalance the teams.",
        DelPositionReqReason.NoJoiners => "You can't join this team - joiners are not allowed.",
        DelPositionReqReason.NoDefections => "You can't join this team - defections are not allowed.",
        DelPositionReqReason.WrongSquad => "You can't join this team - you are not a member of this squad.",
        DelPositionReqReason.OutOfLives => "You can't join this team - you are out of lives.",
        DelPositionReqReason.Banned => "You have been removed from this team by the commander.",
        DelPositionReqReason.PrivateGame => "You are not on the invitation list for this team.",
        DelPositionReqReason.DuplicateLogin => "Someone else is already using that name.",
        DelPositionReqReason.SidesLocked => "The sides are currently locked.",
        DelPositionReqReason.SideDefeated => "This side has been defeated - wait until the game ends to join this side.",
        DelPositionReqReason.CantLeadSquad => "You must be a squad leader or assistant squad leader to lead a team in a squads game.",
        DelPositionReqReason.ServerPaused => "The server is shutting down and is not accepting new players.",
        DelPositionReqReason.BotsOnTeam0Only => "Only bots are allowed on this team.",
        DelPositionReqReason.LobbyLocked => "The lobby for this mission is locked.",
        DelPositionReqReason.SideGone => "The team you were trying to join has been deleted.",
        DelPositionReqReason.NoMission => "The mission you were trying to join has ended.",
        DelPositionReqReason.GameFull => "This server has reached its maximum number of players.",
        _ => "You were not able to join the team.",
    };
}
