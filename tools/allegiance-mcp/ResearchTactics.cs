using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.McpSpawner;

/// <summary>Showcase commander policy, independent of research funding and sockets.
/// Inputs are observed wire state and the manager's own pilots, never server entities.</summary>
internal sealed class ResearchTactics
{
    internal sealed record Pilot(short Id, short Sector, Vector3 Position, bool Support, bool Strike);
    internal sealed record Target(short Id, IgcObjectType Type, short Sector, Vector3 Position,
        bool Friendly, bool Damaged = false, bool Bomber = false);
    internal sealed record Order(short Recipient, short Command, IgcObjectType Type, short Target);
    private readonly Dictionary<short, (Order Order, double Time)> _sent = new();

    internal Order[] Tick(double time, bool started, Pilot[] pilots, Target[] targets)
    {
        if (!started) return [];
        foreach (short id in _sent.Keys.Where(id => !pilots.Any(p => p.Id == id)).ToArray()) _sent.Remove(id);
        var result = new List<Order>();
        var friendlyStations = targets.Where(t => t.Friendly && t.Type == IgcObjectType.Station).ToArray();
        // Oldest assignments first; bound each tick so a large fleet cannot flood chat.
        foreach (var pilot in pilots.OrderBy(p => _sent.TryGetValue(p.Id, out var sent) ? sent.Time : double.NegativeInfinity).ThenBy(p => p.Id))
        {
            Target? target;
            if (pilot.Support)
                target = targets.Where(t => t.Friendly && t.Damaged
                        && (t.Type == IgcObjectType.Station || t.Id != pilot.Id))
                    .Where(t => t.Type == IgcObjectType.Station || t.Sector == pilot.Sector)
                    .OrderBy(t => t.Type == IgcObjectType.Station ? 0 : 1)
                    .ThenBy(t => t.Sector == pilot.Sector ? 0 : 1)
                    .ThenBy(t => (t.Position - pilot.Position).LengthSquared()).ThenBy(t => t.Id).FirstOrDefault();
            else
            {
                // Intercept observed bombers threatening a home station, otherwise
                // focus local enemies. Strike pilots keep their station objective.
                target = targets.Where(t => !t.Friendly && t.Type == IgcObjectType.Ship && t.Bomber
                        && friendlyStations.Any(s => s.Sector == t.Sector && (s.Position - t.Position).LengthSquared() <= 3500 * 3500))
                    .Where(t => !pilot.Strike).OrderBy(t => t.Sector == pilot.Sector ? 0 : 1)
                    .ThenBy(t => (t.Position - pilot.Position).LengthSquared()).ThenBy(t => t.Id).FirstOrDefault();
                target ??= pilot.Strike
                    ? targets.Where(t => !t.Friendly && t.Type == IgcObjectType.Station)
                        .OrderBy(t => t.Sector == pilot.Sector ? 0 : 1).ThenBy(t => t.Id).FirstOrDefault()
                    : targets.Where(t => !t.Friendly && t.Type == IgcObjectType.Ship && t.Sector == pilot.Sector)
                        .OrderBy(t => (t.Position - pilot.Position).LengthSquared()).ThenBy(t => t.Id).FirstOrDefault();
            }
            if (target is null) continue;
            var order = new Order(pilot.Id, pilot.Support ? OrderChange.CidRepair : OrderChange.CidAttack, target.Type, target.Id);
            if (_sent.TryGetValue(pilot.Id, out var previous)
                && time - previous.Time < (previous.Order == order ? 15 : 2)) continue;
            _sent[pilot.Id] = (order, time);
            result.Add(order);
            if (result.Count == 2) break;
        }
        return result.ToArray();
    }
}
