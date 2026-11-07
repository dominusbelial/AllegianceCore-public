using Allegiance.Core.Sim;

namespace Allegiance.Core.Model;

public sealed partial class Mission
{
    // Telemetry only: ship losses per victim side, split into ejections (podded)
    // and true deaths, keyed by the killing blow's kind. Native keeps no such
    // tally. A fresh mission after game over starts empty.
    private readonly Dictionary<(short Side, bool Killed, HitKind Kind), int> _shipLosses = new();

    public void RecordShipLoss(short sideId, bool killed, HitKind kind)
        => _shipLosses[(sideId, killed, kind)] = _shipLosses.GetValueOrDefault((sideId, killed, kind)) + 1;

    public string[] ShipLossSummary()
        => Sides.Select(side => $"side {side.Id} {side.Name}: podded {Tally(side.Id, false)}; killed {Tally(side.Id, true)}")
            .ToArray();

    private string Tally(short sideId, bool killed)
    {
        var parts = Enum.GetValues<HitKind>()
            .Select(kind => (kind, count: _shipLosses.GetValueOrDefault((sideId, killed, kind))))
            .Where(p => p.count > 0)
            .Select(p => $"{p.kind.ToString().ToLowerInvariant()} {p.count}");
        return string.Join(", ", parts) is { Length: > 0 } text ? text : "none";
    }
}
