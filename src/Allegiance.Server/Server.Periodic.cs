using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

public sealed partial class Server
{
    private readonly HashSet<Mission> _restartWarnings = [];

    private GameConfig GameSettings(Mission mission) => _config.Games[_missions.IndexOf(mission)];

    /// <summary>P852 — bounds-safe <see cref="GameSettings"/>: the P655-style
    /// test harnesses (and any direct-rule caller) can reach the join/ready
    /// rules with a mission whose game config entry is absent; native's
    /// MissionParams always exist, so fall back to a default config
    /// (native defaults, igc.h:1333-1334) instead of throwing.</summary>
    private GameConfig GameSettingsSafe(Mission mission)
    {
        int index = _missions.IndexOf(mission);
        return index >= 0 && index < _config.Games.Count ? _config.Games[index] : new GameConfig();
    }
    private bool HasMissionPlayers(Mission mission) =>
        _peerShips.Values.Any(p => ReferenceEquals(p.Ship.Mission, mission));
    private bool HasMinimumPlayers(Mission mission) => mission.Sides.All(side =>
        _peerShips.Values.Count(p => ReferenceEquals(p.Ship.Mission, mission) && p.Ship.SideId == side.Id)
            >= Math.Max(1, GameSettings(mission).MinimumPlayersPerTeam));

    private void SendPeriodicNotice(Mission mission, string message) => SendToMissionPeers(mission,
        FedPacket.Pack(new[] { new ChatMessage { SidSender = -1, ChatTarget = (int)ChatTarget.Everyone,
            OidRecipient = -1, Message = message, CommandID = -1 }.Encode() }));

    private void AdvancePeriodicRules(double now)
    {
        // Native increments once per outer mission tick for connected players
        // (FedSrv/FedSrv.CPP:8279-8287), even while waiting/starting. The existing
        // P654 configurable capacity/cost/refill remain dedicated-server policy.
        foreach (var peer in _peerShips)
            if (peer.Value.Ship.Mission is { Stage: < MissionStage.Over } &&
                _chatBudgets.TryGetValue(peer.Key, out var current))
                _chatBudgets[peer.Key] = ((int)Math.Min(_chatBudgetMax,
                    (long)current.Budget + _chatBudgetRefillPerTick), TickIndex);

        // Declaring a result rotates the mission in place; use a snapshot.
        foreach (var mission in _missions.ToArray())
        {
            var result = mission.EvaluateCountdown(now);
            if (!result.Expired) continue;
            string reason = result.Winner is { } winner
                ? $"The clock has stopped. The winner: {winner.Name} by {result.Criterion}"
                : "The clock has stopped. The game is a draw.";
            DeclareGameOver(mission, result.Winner?.Id ?? GameOver.NA, reason);
        }
    }
}
