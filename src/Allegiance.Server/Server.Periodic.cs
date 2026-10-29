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

        CheckForNoShows(now);
    }

    /// <summary>P872/SIM-0146 — the port of native <c>CheckForNoShows</c>
    /// (FedSrv.CPP:7871-7910, called from the periodic timer at :8088): a
    /// mission nobody ever joined is deleted once it has existed for more than
    /// ten seconds (native ages its FILETIME start time by 100000000
    /// 100 ns-ticks = 10 s), one mission per check. Lifecycle ruling of
    /// Domingo 2026-10-28: boot-configured and admin worlds persist — native
    /// itself exempts admin (<c>bObjectModelCreated</c>) and custom-core games
    /// (FedSrv.CPP:7882-7884) — so the reap applies only to
    /// <see cref="Mission.PlayerCreated"/> sessions (which the port's
    /// single-server-config core design always builds on the configured core,
    /// satisfying the native default-core condition). The native
    /// "don't show on the lobby until the creator arrives" rationale stands:
    /// an empty player-created world is invisible to joiners, so deleting it
    /// is unobservable to anyone but its creator.</summary>
    private void CheckForNoShows(double now)
    {
        foreach (var mission in _missions.ToArray())
        {
            if (!mission.PlayerCreated) continue;              // configured/admin worlds persist (FedSrv.CPP:7882-7884)
            if (HasMissionPlayers(mission)) continue;          // GetCountOfPlayers(NULL, true) == 0 (FedSrv.CPP:7885)
            if (now - mission.CreatedAt <= 10.0) continue;     // > 100000000 FILETIME ticks = 10 s (FedSrv.CPP:7902-7903)
            int index = _missions.IndexOf(mission);
            Console.WriteLine($"[periodic] no one showed up for player-created mission (cookie {index}), killing it (FedSrv.CPP:7906)");
            _missions.Remove(mission);
            // P871b — `_config.Games` and `_missions` are index-aligned (the
            // game-over rotation rebuilds from `_config.Games[idx]` and
            // `Mission.MissionId` is that index + 1), so a reaped mission's
            // config entry leaves with it. Only player-created
            // (wizard/`CREATE_MISSION_REQ`) missions ever get here, i.e. only
            // entries this server inserted itself.
            if (index >= 0 && index < _config.Games.Count && ReferenceEquals(_missionConfigs.GetValueOrDefault(mission), _config.Games[index]))
                _config.Games.RemoveAt(index);
            _missionConfigs.Remove(mission);
            _matchups.Remove(mission);
            _missionParams.Remove(mission);
            _mapGenMissions.Remove(mission);
            break;                                             // deleting 1 mission per check is plenty (FedSrv.CPP:7909)
        }
    }
}
