using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.State;

public sealed partial class ClientWorld
{
    private LastBaseNotice _lastBases = new();

    /// <summary>Only seated humans consume this HUD model. Observer worlds have no own ship;
    /// the renderer additionally passes its spectate flag for a seated spectator camera.</summary>
    public IReadOnlyList<LastBaseObjective> LastBaseObjectives(bool spectating = false)
    {
        if (spectating || MyShipId is null || MySideId is not { } mine || mine < 0 || _gameOver is not null
            || _mission?.Stage != MissionStageMessage.StageStarted) return [];
        return _lastBases.Targets.Where(t => t.SideId == mine || !AreAllied(mine, t.SideId))
            .OrderBy(t => t.SideId == mine).ThenBy(t => t.SideId)
            .Select(t => new LastBaseObjective(t, t.SideId == mine, _lastBases.FinaleSecondsRemaining)).ToArray();
    }

    private void Apply(LastBaseNotice notice)
    {
        if (MyShipId is not null && MySideId is { } mine && mine >= 0 && _gameOver is null
            && _mission?.Stage == MissionStageMessage.StageStarted)
            foreach (var target in notice.Targets)
                if (target.SideId != mine && !AreAllied(mine, target.SideId)
                    && !_lastBases.Targets.Any(t => t.SideId == target.SideId && t.StationId == target.StationId))
                    RecordSoundEvent(ClientSoundEventKind.LastEnemyBase, target.StationId);
        _lastBases = notice;
    }

    private void RemoveLastBase(short stationId) => _lastBases = new()
    {
        FinaleSecondsRemaining = _lastBases.FinaleSecondsRemaining,
        Targets = _lastBases.Targets.Where(t => t.StationId != stationId).ToArray(),
    };
}

/// <summary>HUD-1 is not present; this model is shared by flight presentation and headless tests.</summary>
public sealed record LastBaseObjective(LastBaseTarget Target, bool Defending, float FinaleSecondsRemaining)
{
    public string Text => (Defending ? $"Our last {Target.Name}: defend it" : $"Enemy's last {Target.Name}: destroy it to win")
        + (FinaleSecondsRemaining >= 0 ? $" | Finale {(int)FinaleSecondsRemaining / 60}:{(int)FinaleSecondsRemaining % 60:00}" : "");
}
