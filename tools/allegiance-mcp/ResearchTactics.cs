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

    /// <summary>SC8 — one summary line per strike-wave edge (commit/end) for the rig.
    /// The side is added by the caller that owns the socket (BotManager), so the
    /// policy stays free of side/transport concepts.</summary>
    internal enum StrikeEventKind { Commit, End }
    internal sealed record StrikeEvent(StrikeEventKind Kind, int Wave, int Members, int AtRally,
        short Target, short Sector, string Reason);

    /// <summary>Per-pilot order bookkeeping. <see cref="Order"/>/<see cref="Time"/> are
    /// the last sent order and when it went out; the Mark* fields reset the
    /// no-progress window (SC8): distance to the goto point, and the sector the
    /// pilot was in, as observed when the window last showed progress.</summary>
    private sealed record Sent(Order Order, double Time, double MarkTime, float MarkDistance, short MarkSector);

    private readonly Dictionary<short, Sent> _sent = new();

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
    /// (SC8: at least two, or every strike pilot when the side has fewer than two).</summary>
    internal const double RallyTimeoutSeconds = 90;

    /// <summary>At most this many orders per tick across both loops, so a large fleet
    /// cannot flood chat.</summary>
    internal const int MaxOrdersPerTick = 2;

    /// <summary>SC8 — shortest gap between two orders to the same pilot, changed or
    /// not. An identical order is otherwise not re-sent until it stops making
    /// progress (below); a changed order may go out after this floor.</summary>
    internal const double ChangedOrderFloorSeconds = 2;

    /// <summary>SC8 — how long a pilot may show no progress on an identical order
    /// before the commander re-sends it. The SC7 policy re-sent every identical
    /// order every 15 s by design (rig: the same goto to the same aleph every
    /// ~15.5 s, 195–458 gotos/side), which is a team-chat order plus a queued server
    /// command per pilot per repeat.</summary>
    internal const double ProgressWindowSeconds = 60;

    /// <summary>SC8 — a goto counts as progressing when the distance to its
    /// destination has fallen to this fraction of the distance at the start of the
    /// window (~10% closed).</summary>
    internal const float GotoProgressFraction = 0.9f;

    // SC7/SC8 — strike-wave state. One shared station target + rally point for the
    // whole group, kept stable across ticks (a human commander commits, doesn't flick).
    // SC8 adds the wave itself: the ids that committed together, kept until the
    // station dies or no member is left alive and undocked in its sector.
    private short? _strikeTarget;
    private short _strikeSector = -1;
    private Target? _rally;
    private double _rallySince = double.NaN;
    private double _committedAt = double.NaN;
    private readonly HashSet<short> _waveMembers = new();
    private int _waveNumber;
    private readonly List<StrikeEvent> _events = new();

    /// <summary>SC8 — hand the caller the strike-wave edges produced since the last
    /// call so it can print one summary line each (never one line per tick).</summary>
    internal StrikeEvent[] TakeStrikeEvents()
    {
        if (_events.Count == 0) return [];
        var events = _events.ToArray();
        _events.Clear();
        return events;
    }

    internal Order[] Tick(double time, bool started, Pilot[] pilots, Target[] targets)
    {
        if (!started) return [];
        foreach (short id in _sent.Keys.Where(id => !pilots.Any(p => p.Id == id)).ToArray()) _sent.Remove(id);
        var result = new List<Order>();
        var friendlyStations = targets.Where(t => t.Friendly && t.Type == IgcObjectType.Station).ToArray();
        var enemyStations = targets.Where(t => !t.Friendly && t.Type == IgcObjectType.Station).ToArray();
        // Strike pilots: pure bombers (never support). Support/bombers keep their repair duty.
        var strikePilots = pilots.Where(p => p.Strike && !p.Support).ToArray();

        // ---- SC7/SC8 strike wave: rally with goto, then focus ONE shared station ----
        if (strikePilots.Length == 0)
        {
            EndWave(time, "no-strike-pilots");
            // Keep the chosen rally: bomber crews dock to reload all the time, and
            // the next launch must re-form at the same aleph (SC8). Only the timeout
            // restarts, so returning pilots rally afresh.
            _rallySince = double.NaN;
        }
        if (strikePilots.Length > 0)
        {
            // Validate the shared station: it must still exist as an enemy station.
            if (_strikeTarget is { } cur && enemyStations.All(s => s.Id != cur))
            {
                EndWave(time, "station-gone");
                _strikeTarget = null;
                _rally = null;
                _rallySince = double.NaN;
            }
            if (_strikeTarget is null)
            {
                var picked = PickStrikeStation(enemyStations, strikePilots);
                if (picked is not null)
                {
                    _strikeTarget = picked.Id;
                    _strikeSector = picked.Sector;
                    _rally = ChooseRally(friendlyStations, targets, _strikeSector);
                    _rallySince = time;
                }
            }
            if (_strikeTarget is { } stationId && enemyStations.FirstOrDefault(s => s.Id == stationId) is { } station)
            {
                // Positions are per-sector coordinates: only a pilot in the same
                // sector can be near the rally or the station.
                var atRallyCover = _rally is { } rr
                    ? strikePilots.Where(p => p.Sector == rr.Sector
                        && (p.Position - rr.Position).LengthSquared() <= RallyGatherRadius * RallyGatherRadius).ToArray()
                    : strikePilots;
                int atRally = atRallyCover.Length;
                if (double.IsNaN(_rallySince)) _rallySince = time;

                // Re-form after the committed group is gone from the target sector
                // (dead, docked or flown off), allowing RallyTimeoutSeconds for the
                // trip from the rally to the station. Ending restarts the rally clock,
                // so the next pair waits its own timeout (a full group can still
                // commit immediately below).
                if (_waveMembers.Count > 0)
                {
                    // A member that left the snapshot (docked to reload, or died) is
                    // out of the wave: relaunching under the same ship id must send it
                    // to the rally, not back to the station alone.
                    if (_waveMembers.All(id => !strikePilots.Any(p => p.Id == id)))
                        EndWave(time, "members-gone");
                    else
                        _waveMembers.RemoveWhere(id => !strikePilots.Any(p => p.Id == id));
                }
                if (_waveMembers.Count > 0)
                {
                    bool anyMemberInTarget = strikePilots.Any(p => _waveMembers.Contains(p.Id) && p.Sector == station.Sector);
                    if (!anyMemberInTarget && time - _committedAt >= RallyTimeoutSeconds)
                        EndWave(time, "no-members-in-target");
                    else if (time - _committedAt >= 3 * RallyTimeoutSeconds)
                        EndWave(time, "timeout");   // one straggler must not hold the next group forever
                }
                bool timedOut = time - _rallySince >= RallyTimeoutSeconds;
                // SC8 — no more "commit with nobody": after the timeout commit two
                // gathered pilots, or every strike pilot when the side has fewer than
                // two. Before the timeout only a full group commits. A wave of one is
                // never committed while two or more strike pilots exist.
                int timeoutQuorum = Math.Min(2, strikePilots.Length);
                bool fullGroup = strikePilots.Length >= StrikeGroupSize && atRally >= StrikeGroupSize;
                bool commit = fullGroup || (timedOut && atRally >= timeoutQuorum);
                if (_waveMembers.Count == 0 && commit)
                    CommitWave(time, atRallyCover, stationId, station.Sector, fullGroup ? "full" : "timeout");

                foreach (var pilot in strikePilots
                    .OrderBy(p => _sent.TryGetValue(p.Id, out var sent) ? sent.Time : double.NegativeInfinity).ThenBy(p => p.Id))
                {
                    // Commit together: only the wave's members attack the shared
                    // station; a latecomer keeps (or gets) the rally goto and joins the
                    // next wave. A pilot already on the target attacks immediately.
                    bool alreadyOn = pilot.Sector == station.Sector
                        && (pilot.Position - station.Position).LengthSquared() <= StrikeOnTargetRadius * StrikeOnTargetRadius;
                    bool attack = _waveMembers.Contains(pilot.Id) || alreadyOn;
                    Order order = attack
                        ? new Order(pilot.Id, OrderChange.CidAttack, IgcObjectType.Station, stationId)
                        : (_rally is { } r
                            ? new Order(pilot.Id, OrderChange.CidGoto, r.Type, r.Id)
                            : new Order(pilot.Id, OrderChange.CidAttack, IgcObjectType.Station, stationId));
                    // Positions are per-sector: in another sector the distance means
                    // nothing, so only a sector change counts as progress there.
                    float distance = order.Command == OrderChange.CidGoto && _rally is { } rallyPoint
                        ? (pilot.Sector == rallyPoint.Sector ? (pilot.Position - rallyPoint.Position).Length() : float.MaxValue)
                        : 0;
                    if (!ShouldSend(pilot, order, time, distance, station.Sector)) continue;
                    RecordSend(pilot, order, time, distance);
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
            // SC8 — same no-progress rule as the strike loop. An attack or repair
            // counts as progressing while the pilot is in the target's sector.
            if (!ShouldSend(pilot, order, time, 0, target.Sector)) continue;
            RecordSend(pilot, order, time, 0);
            result.Add(order);
            if (result.Count >= MaxOrdersPerTick) break;
        }
        return result.ToArray();
    }

    /// <summary>SC8 — one commit of a strike wave: record exactly the pilots that were
    /// at the rally so only they get the attack order; everyone else keeps rallying
    /// for the next wave. Emits one summary event for the rig log.</summary>
    private void CommitWave(double time, Pilot[] atRally, short stationId, short sector, string reason)
    {
        _waveNumber++;
        _waveMembers.Clear();
        foreach (var pilot in atRally) _waveMembers.Add(pilot.Id);
        _committedAt = time;
        _events.Add(new(StrikeEventKind.Commit, _waveNumber, _waveMembers.Count, atRally.Length, stationId, sector, reason));
    }

    /// <summary>SC8 — close the current wave (station gone, or no member left alive
    /// and undocked in the target sector) and restart the rally timeout so the next
    /// group forms from whoever is at the rally.</summary>
    private void EndWave(double time, string reason)
    {
        if (_waveMembers.Count == 0) return;
        _events.Add(new(StrikeEventKind.End, _waveNumber, _waveMembers.Count, 0, _strikeTarget ?? -1, _strikeSector, reason));
        _waveMembers.Clear();
        _committedAt = double.NaN;
        _rallySince = time;
    }

    /// <summary>SC8 — decide whether an order needs to go out. A changed order waits
    /// only for <see cref="ChangedOrderFloorSeconds"/>. An identical order is re-sent
    /// only when the pilot has made no progress for <see cref="ProgressWindowSeconds"/>:
    /// a goto that has closed &lt;10% of the distance to its destination, or an
    /// attack/repair whose pilot is not in the target's sector. Changing sector is
    /// itself progress and resets the window at the tick it is observed (so the
    /// "no sector change for 60 s" rule is measured from the last change, not from
    /// the last send). Arrival or a 10% closure only moves the baseline; never sends.</summary>
    private bool ShouldSend(Pilot pilot, Order order, double time, float distance, short targetSector)
    {
        if (!_sent.TryGetValue(pilot.Id, out var previous)) return true;
        if (previous.Order != order) return time - previous.Time >= ChangedOrderFloorSeconds;
        if (time - previous.Time < ChangedOrderFloorSeconds) return false;
        if (pilot.Sector != previous.MarkSector)
        {
            _sent[pilot.Id] = previous with { MarkTime = time, MarkDistance = distance, MarkSector = pilot.Sector };
            return false;
        }
        if (time - previous.MarkTime < ProgressWindowSeconds) return false;
        bool progress = order.Command == OrderChange.CidGoto
            ? distance <= RallyGatherRadius || distance <= previous.MarkDistance * GotoProgressFraction
            : pilot.Sector == targetSector;
        if (!progress) return true;
        _sent[pilot.Id] = previous with { MarkTime = time, MarkDistance = distance, MarkSector = pilot.Sector };
        return false;
    }

    private void RecordSend(Pilot pilot, Order order, double time, float distance)
        => _sent[pilot.Id] = new Sent(order, time, time, distance, pilot.Sector);

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
