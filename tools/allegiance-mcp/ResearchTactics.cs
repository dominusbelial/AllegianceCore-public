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
        bool Friendly, bool Damaged = false, bool Bomber = false,
        bool Garrison = false, short DestinationSector = -1);
    internal sealed record Order(short Recipient, short Command, IgcObjectType Type, short Target);
    private readonly Dictionary<short, (Order Order, double Time)> _sent = new();

    /// <summary>SC7 — the strike-wave quorum: this many strike pilots must gather at
    /// the rally point before the wave commits to the shared station target. A human
    /// commander wants enough anti-base guns on one station in one window to
    /// outlast its shield/nanite recovery; measured rig data shows a single AB hit
    /// drops a shield by ~50% and a kill needs ~6 hits in a short window, so a solo
    /// or duo strike never lands a kill. Three is the smallest group whose
    /// concentrated fire reliably cracks a fresh station hull (justified in the
    /// SC7 report; a constant so the rig can tune it without a recompile).</summary>
    internal const int StrikeGroupSize = 3;

    /// <summary>SC7 — how close a strike pilot must be to the rally point to count
    /// as "gathered". ~3 km, matching the native defence RELEASE horizon
    /// (shipIGC.cpp:2404, a chased enemy is abandoned beyond 3500) so a gathered
    /// group is truly bunched at the aleph mouth before committing — not scattered
    /// across half a sector. A constant for the rig to tune.</summary>
    internal const float RallyGatherRadius = 3600f;   // tune surface for the rig (~3 km)

    /// <summary>SC7 — how close a strike pilot must already be to the station to
    /// attack immediately instead of continuing to rally (an on-target bomber
    /// should not double back to the aleph while its wingmates regroup). Distinctly
    /// closer than the rally radius so only a pilot effectively at the station
    /// shortcuts.</summary>
    internal const float StrikeOnTargetRadius = 2400f;

    /// <summary>SC7 review — a side short on launched bombers must not park them at
    /// the rally forever: after this long rallying, whoever has gathered commits
    /// (with fewer than <see cref="StrikeGroupSize"/> pilots too).</summary>
    internal const double RallyTimeoutSeconds = 90;

    /// <summary>At most this many orders per tick across both loops, so a large fleet
    /// cannot flood chat.</summary>
    internal const int MaxOrdersPerTick = 2;

    // SC7 — strike-wave state. One shared station target + rally point for the whole
    // group, kept stable across ticks (a human commander commits, doesn't flick).
    private short? _strikeTarget;
    private short _strikeSector = -1;
    private Target? _rally;
    private bool _gathered;
    private double _rallySince = double.NaN;
    private double _committedAt = double.NaN;

    internal Order[] Tick(double time, bool started, Pilot[] pilots, Target[] targets)
    {
        if (!started) return [];
        foreach (short id in _sent.Keys.Where(id => !pilots.Any(p => p.Id == id)).ToArray()) _sent.Remove(id);
        var result = new List<Order>();
        var friendlyStations = targets.Where(t => t.Friendly && t.Type == IgcObjectType.Station).ToArray();
        var enemyStations = targets.Where(t => !t.Friendly && t.Type == IgcObjectType.Station).ToArray();
        // Strike pilots: pure bombers (never support). Support/bombers keep their repair duty.
        var strikePilots = pilots.Where(p => p.Strike && !p.Support).ToArray();

        // ---- SC7 strike wave: rally with goto, then focus ONE shared station ----
        if (strikePilots.Length == 0) { _gathered = false; _rally = null; _rallySince = double.NaN; }
        if (strikePilots.Length > 0)
        {
            // Validate the shared station: it must still exist as an enemy station.
            if (_strikeTarget is { } cur && enemyStations.All(s => s.Id != cur))
            { _strikeTarget = null; _gathered = false; _rally = null; _rallySince = double.NaN; }
            if (_strikeTarget is null)
            {
                var picked = PickStrikeStation(enemyStations, strikePilots);
                if (picked is not null)
                {
                    _strikeTarget = picked.Id;
                    _strikeSector = picked.Sector;
                    _gathered = false;
                    _rally = ChooseRally(friendlyStations, targets, _strikeSector);
                    _rallySince = time;
                }
            }
            if (_strikeTarget is { } stationId && enemyStations.FirstOrDefault(s => s.Id == stationId) is { } station)
            {
                // Positions are per-sector coordinates: only a pilot in the same
                // sector can be near the rally or the station.
                int atRally = _rally is { } rp
                    ? strikePilots.Count(p => p.Sector == rp.Sector
                        && (p.Position - rp.Position).LengthSquared() <= RallyGatherRadius * RallyGatherRadius)
                    : strikePilots.Length;
                if (double.IsNaN(_rallySince)) _rallySince = time;
                bool timedOut = time - _rallySince >= RallyTimeoutSeconds;
                // Commit when a full group has gathered, or after the rally timeout
                // with whoever made it (a short side still attacks).
                if (!_gathered && ((strikePilots.Length >= StrikeGroupSize && atRally >= StrikeGroupSize)
                                   || (timedOut && atRally > 0)
                                   || time - _rallySince >= 2 * RallyTimeoutSeconds))
                { _gathered = true; _committedAt = time; }
                // Re-form after the committed group is gone from the target sector,
                // so reinforcements don't trickle in solo.
                // (Allow the committed group RallyTimeoutSeconds to reach the target.)
                if (_gathered && _rally is not null && !strikePilots.Any(p => p.Sector == station.Sector)
                    && time - _committedAt >= RallyTimeoutSeconds)
                { _gathered = false; _rallySince = time; _committedAt = double.NaN; }

                foreach (var pilot in strikePilots
                    .OrderBy(p => _sent.TryGetValue(p.Id, out var sent) ? sent.Time : double.NegativeInfinity).ThenBy(p => p.Id))
                {
                    // Commit together: everyone attacks the same station, or everyone
                    // routes to the rally first. A pilot already on the target attacks
                    // even while latecomers still rally.
                    bool alreadyOn = pilot.Sector == station.Sector
                        && (pilot.Position - station.Position).LengthSquared() <= StrikeOnTargetRadius * StrikeOnTargetRadius;
                    Order order = (_gathered || alreadyOn)
                        ? new Order(pilot.Id, OrderChange.CidAttack, IgcObjectType.Station, stationId)
                        : (_rally is { } r
                            ? new Order(pilot.Id, OrderChange.CidGoto, r.Type, r.Id)
                            : new Order(pilot.Id, OrderChange.CidAttack, IgcObjectType.Station, stationId));
                    if (_sent.TryGetValue(pilot.Id, out var previous)
                        && time - previous.Time < (previous.Order == order ? 15 : 2)) continue;
                    _sent[pilot.Id] = (order, time);
                    result.Add(order);
                    if (result.Count >= MaxOrdersPerTick) break;
                }
            }
        }
        // Oldest assignments first; bound each tick so a large fleet cannot flood chat.
        foreach (var pilot in pilots.Where(p => !(p.Strike && !p.Support))
            .OrderBy(p => _sent.TryGetValue(p.Id, out var sent) ? sent.Time : double.NegativeInfinity).ThenBy(p => p.Id))
        {
            if (result.Count >= MaxOrdersPerTick) break;
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
                // focus local enemies. (Strike pilots are handled above by the rally.)
                target = targets.Where(t => !t.Friendly && t.Type == IgcObjectType.Ship && t.Bomber
                        && friendlyStations.Any(s => s.Sector == t.Sector && (s.Position - t.Position).LengthSquared() <= 3500 * 3500))
                    .OrderBy(t => t.Sector == pilot.Sector ? 0 : 1)
                    .ThenBy(t => (t.Position - pilot.Position).LengthSquared()).ThenBy(t => t.Id).FirstOrDefault();
                target ??= targets.Where(t => !t.Friendly && t.Type == IgcObjectType.Ship && t.Sector == pilot.Sector)
                    .OrderBy(t => (t.Position - pilot.Position).LengthSquared()).ThenBy(t => t.Id).FirstOrDefault();
            }
            if (target is null) continue;
            var order = new Order(pilot.Id, pilot.Support ? OrderChange.CidRepair : OrderChange.CidAttack, target.Type, target.Id);
            if (_sent.TryGetValue(pilot.Id, out var previous)
                && time - previous.Time < (previous.Order == order ? 15 : 2)) continue;
            _sent[pilot.Id] = (order, time);
            result.Add(order);
            if (result.Count >= MaxOrdersPerTick) break;
        }
        return result.ToArray();
    }

    /// <summary>SC7 — pick the one enemy station the whole strike wave commits to.
    /// Preference order (race-agnostic, applied to the observed enemy stations):
    /// 1. already-damaged stations (<see cref="Target.Damaged"/>) first —
    ///    finish the wounded, whose hull is nearer the kill threshold (the rig's
    ///    side 1 kept pounding the pristine Garrison while never finishing one);
    /// 2. among candidates, NON-Garrison stations by distance from the strike
    ///    group's centroid — a Garrison is the toughest hull (highest HP), so
    ///    softer targets die first and a Garrison becomes a last resort;
    /// 3. the Garrison, by distance, only when no other enemy station exists.
    /// Stable across ticks (the shared target is kept until it dies).</summary>
    private static Target? PickStrikeStation(Target[] enemyStations, Pilot[] strikePilots)
    {
        if (enemyStations.Length == 0) return null;
        // Coordinates are per sector: measure only from strike pilots already in
        // the station's sector; stations in other sectors tie and fall to the id.
        float Distance(Target s)
        {
            var local = strikePilots.Where(p => p.Sector == s.Sector).ToArray();
            if (local.Length == 0) return float.MaxValue;
            var centroid = new Vector3(local.Average(p => p.Position.X),
                local.Average(p => p.Position.Y), local.Average(p => p.Position.Z));
            return (s.Position - centroid).LengthSquared();
        }
        return enemyStations
            .OrderByDescending(s => s.Damaged)
            .ThenBy(s => s.Garrison ? 1 : 0)
            .ThenBy(Distance)
            .ThenBy(s => s.Id)
            .FirstOrDefault();
    }

    /// <summary>SC7 — pick the strike wave's rally point: the aleph (warp) that
    /// leads INTO the target sector, on our side of the border (its own sector),
    /// preferring one whose own sector we control (a friendly station is there);
    /// fall back to any aleph into the target sector; finally to a friendly
    /// station in a sector bordering the target. Null only when no border warp or
    /// friendly station exists — the wave attacks directly in that case.</summary>
    private static Target? ChooseRally(Target[] friendlyStations, Target[] targets, short strikeSector)
    {
        var alephs = targets.Where(t => t.Type == IgcObjectType.Warp && t.DestinationSector == strikeSector).ToArray();
        var controlled = alephs.Where(a => friendlyStations.Any(f => f.Sector == a.Sector)).OrderBy(a => a.Id).FirstOrDefault();
        if (controlled is not null) return controlled;
        // No aleph from a sector we hold: prefer one whose sector has no observed
        // enemy station, so the group doesn't hold in enemy space. (A friendly
        // station is not used as a rally: goto a friendly station means docking.)
        var enemySectors = targets.Where(t => !t.Friendly && t.Type == IgcObjectType.Station)
            .Select(t => t.Sector).ToHashSet();
        return alephs.OrderBy(a => enemySectors.Contains(a.Sector) ? 1 : 0).ThenBy(a => a.Id).FirstOrDefault();
    }
}
