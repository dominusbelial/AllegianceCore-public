using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.McpSpawner;

/// <summary>Showcase commander policy, independent of research funding and sockets.
/// Inputs are observed wire state and the manager's own pilots, never server entities.</summary>
internal sealed class ResearchTactics
{
    // P801 — a strike pilot also carries its real anti-base loadout (used to
    // compute the volley requirement) and whether its hull+parts can cloak
    // (legal lone wolf). Both default to null/false so SC7/SC8/SC9 fixtures
    // keep their exact legacy behaviour.
    internal sealed record Pilot(short Id, short Sector, Vector3 Position, bool Support, bool Strike,
        StrikeVolley.Loadout? Loadout = null, bool CloakCapable = false);
    internal sealed record Target(short Id, IgcObjectType Type, short Sector, Vector3 Position,
        bool Friendly, bool Damaged = false, bool Bomber = false,
        bool Garrison = false, short DestinationSector = -1,
        // SC9 — observed live station fractions (-1 = unknown), for the [strike] lines.
        float Hull = -1, float Shield = -1,
        // P801 — the real station type + its native durability profile. Null keeps
        // the legacy SC7 quorum/timeout behaviour (tests without a core profile).
        short StationTypeId = -1, StrikeVolley.StationProfile? Profile = null, bool Miner = false);
    internal sealed record Order(short Recipient, short Command, IgcObjectType Type, short Target);

    /// <summary>SC8 — one summary line per strike-wave edge (commit/end) for the rig.
    /// The side is added by the caller that owns the socket (BotManager), so the
    /// policy stays free of side/transport concepts. SC9 adds the escort count and
    /// the observed station health to every edge, and the wave's arrival numbers +
    /// duration to the end edge.</summary>
    internal enum StrikeEventKind { Commit, End, Abort }
    internal sealed record StrikeEvent(StrikeEventKind Kind, int Wave, int Members, int AtRally,
        short Target, short Sector, string Reason,
        int Escorts = 0, float StationHull = -1, float StationShield = -1,
        int Arrived = 0, int PeakOnTarget = 0, double DurationSeconds = -1,
        // P801 mass-staging fields (zero/-1 on legacy events, so old log lines
        // are byte-identical).
        int Required = 0, int Assembled = 0, double WaitSeconds = -1,
        int Visible = 0, int LoneWolves = 0, double GoInSeconds = -1);

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

    /// <summary>SC9 — how many non-strike pilots escort each wave member, capped at
    /// <see cref="MaxEscorts"/> for the whole wave. Constants so the rig can tune
    /// the escort size without a recompile.</summary>
    internal const int EscortsPerMember = 2;
    internal const int MaxEscorts = 6;

    /// <summary>SC7 review — a side short on launched bombers must not park them at
    /// the rally forever: after this long rallying, whoever has gathered commits
    /// (SC8: at least two, or every strike pilot when the side has fewer than two).</summary>
    internal const double RallyTimeoutSeconds = 90;

    /// <summary>P801 — massed strike composition (Domingo, 2026-10-23): one
    /// visible bomber element (plus its P778a escorts) and at most this many
    /// cloaked lone wolves, all on one shared go time. Tunables.</summary>
    internal const int MaxLoneWolves = 2;

    /// <summary>P801 — shared go lead: once the quota is at the staging point the
    /// visible element and the lone wolves take their attack order this many
    /// seconds later, so every release lands inside one volley window.</summary>
    internal const double StrikeGoLeadSeconds = 5;

    /// <summary>P801 — hard cap on staging. A side that cannot assemble the
    /// required volley after this long aborts the strike (never trickles); the
    /// station goes on a short cooldown and the next boundary re-picks.</summary>
    internal const double StagingHardCapSeconds = 180;

    /// <summary>P801 review — lone wolves are a bonus, never a gate: once the
    /// bombers present (visible at the rally + wolves in position) already meet the
    /// volley requirement, a late lone wolf delays the go by at most this long.</summary>
    internal const double LoneWolfGraceSeconds = 60;

    /// <summary>Pure staging gate (unit-tested): the visible quota and every lone wolf
    /// in position, or — after <see cref="LoneWolfGraceSeconds"/> — enough bombers of
    /// either kind present to meet the requirement on their own.</summary>
    internal static bool StagingReady(int atRally, int visibleRequired, int wolvesReady, int wolvesAssigned,
        int required, double waitedSeconds)
        => (atRally >= visibleRequired && wolvesReady == wolvesAssigned)
           || (atRally + wolvesReady >= required && waitedSeconds >= LoneWolfGraceSeconds);

    /// <summary>P801 — how long an aborted station is skipped before the wave
    /// considers it again.</summary>
    internal const double StagingAbandonSeconds = 120;

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

    // P791 — minimal shared phase signal for the support driver: whether a
    // strike wave is committed (rally gathered/committed, one shared station,
    // ResearchTactics.cs SC-7/8/9) and which sector it is headed for.
    internal bool StrikeWaveActive => _waveMembers.Count > 0 && _strikeTarget is not null;
    internal short StrikeSector => _strikeSector;
    private Target? _rally;
    private double _rallySince = double.NaN;
    private double _committedAt = double.NaN;
    private readonly HashSet<short> _waveMembers = new();
    private int _waveNumber;
    private readonly List<StrikeEvent> _events = new();

    // SC9 — target re-pick cache: the observed enemy-station signature (id + damage
    // + garrison + sector) so an unchanged set skips PickStrikeStation.
    private (short Id, bool Damaged, bool Garrison, short Sector)[] _stationSignature = [];

    // SC9 — the wave's arrival measurement and its escorts (escort id -> protectee id).
    private readonly HashSet<short> _waveArrived = new();
    private int _wavePeakOnTarget;
    private readonly Dictionary<short, short> _escorts = new();

    // P801 — mass staging. The wave first gathers `_visibleRequired` visible
    // bombers at the rally (plus up to MaxLoneWolves cloaked wolves on their own
    // entry alephs), then all release on one `_goTime`. Nothing attacks before
    // the quota is met; a short staging cap aborts instead of trickling.
    private bool _staging;
    private double _stageSince = double.NaN;
    private double _goTime = double.NaN;
    private int _required;
    private int _visibleRequired;
    private readonly Dictionary<short, Target> _loneWolfRoute = new();
    private readonly HashSet<short> _visibleMembers = new();
    private readonly Dictionary<short, double> _abandonedUntil = new();

    /// <summary>SC8 — hand the caller the strike-wave edges produced since the last
    /// call so it can print one summary line each (never one line per tick).</summary>
    internal StrikeEvent[] TakeStrikeEvents()
    {
        if (_events.Count == 0) return [];
        var events = _events.ToArray();
        _events.Clear();
        return events;
    }

    internal bool EconomicRaidActive { get; private set; }

    internal Order[] Tick(double time, bool started, Pilot[] pilots, Target[] targets, int strikeRoster = -1, double enemyProsperityProgress = 0)
    {
        EconomicRaidActive = started && enemyProsperityProgress >= .1;
        if (!started) return [];
        foreach (short id in _sent.Keys.Where(id => !pilots.Any(p => p.Id == id)).ToArray()) _sent.Remove(id);
        var result = new List<Order>();
        var friendlyStations = targets.Where(t => t.Friendly && t.Type == IgcObjectType.Station).ToArray();
        var enemyStations = targets.Where(t => !t.Friendly && t.Type == IgcObjectType.Station).ToArray();
        // Strike pilots: pure anti-base-capable hulls (never support). Support/bombers keep their repair duty.
        var strikePilots = pilots.Where(p => p.Strike && !p.Support).ToArray();
        // SC9 — the quorum is stated against the side's whole strike roster (docked
        // or launched), not just the pilots visible in this observation. -1 keeps
        // the SC8 behaviour (the launched count) for callers without a roster.
        int roster = strikeRoster < 0 ? strikePilots.Length : strikeRoster;

        // ---- SC7/SC8/SC9 strike wave: rally with goto, then focus ONE shared station ----
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
            // SC9 — re-pick the target at every wave boundary. While a wave is in
            // flight the target stays put (SC7: commit, don't flick). This is where
            // the SC-9 rig fix enters: the old policy picked the Garrison once and
            // kept it for the whole match.
            if (_waveMembers.Count == 0)
            {
                // The shared station must still exist as an enemy station.
                if (_strikeTarget is { } cur && enemyStations.All(s => s.Id != cur))
                {
                    _strikeTarget = null;
                    _rally = null;
                    _rallySince = double.NaN;
                    ResetStage();
                }
                var signature = StationSignature(enemyStations);
                if (_strikeTarget is null || !signature.SequenceEqual(_stationSignature))
                {
                    _stationSignature = signature;
                    var picked = PickStrikeStation(enemyStations, strikePilots, time, _abandonedUntil);
                    if (picked is null)
                    {
                        _strikeTarget = null;
                        _rally = null;
                        _rallySince = double.NaN;
                        ResetStage();
                    }
                    else if (picked.Id != _strikeTarget || _rally is null)
                    {
                        _strikeTarget = picked.Id;
                        _strikeSector = picked.Sector;
                        var newRally = ChooseRally(friendlyStations, targets, _strikeSector);
                        // Restart the rally window only when the rally point itself
                        // moves: candidates trading places (shield damage flickers as
                        // it regenerates) must not keep the timeout from firing.
                        if (newRally?.Id != _rally?.Id) _rallySince = time;
                        _rally = newRally;
                        ResetStage();
                    }
                }
            }
            else if (_strikeTarget is { } cur && enemyStations.All(s => s.Id != cur))
            {
                EndWave(time, "station-gone");
                _strikeTarget = null;
                _rally = null;
                _rallySince = double.NaN;
            }
            if (_strikeTarget is { } stationId && enemyStations.FirstOrDefault(s => s.Id == stationId) is { } station)
            {
                // P801 — a real station type means mass: hold at the staging point
                // until the computed volley is assembled, then release on a shared
                // go time. A target without a profile keeps the legacy SC7 quorum.
                bool mass = station.Profile is not null;
                bool aborted = false;
                if (mass && _waveMembers.Count == 0)
                {
                    if (!_staging) BeginStage(time, station, strikePilots, roster, targets);
                    if (_staging) aborted = UpdateStaging(time, station, strikePilots, pilots);
                }
                // The visible element excludes the lone wolves, which fly their own
                // entry lane and are added only on the shared go time.
                var visiblePilots = _loneWolfRoute.Count == 0
                    ? strikePilots
                    : strikePilots.Where(p => !_loneWolfRoute.ContainsKey(p.Id)).ToArray();
                Target? stagePoint = _rally ?? (mass ? station : null);
                // Positions are per-sector coordinates: only a pilot in the same
                // sector can be near the rally or the station.
                var atRallyCover = stagePoint is { } rr
                    ? visiblePilots.Where(p => p.Sector == rr.Sector
                        && (p.Position - rr.Position).LengthSquared() <= RallyGatherRadius * RallyGatherRadius).ToArray()
                    : visiblePilots;
                int atRally = atRallyCover.Length;
                if (double.IsNaN(_rallySince)) _rallySince = time;

                // SC9 — measure arrival before any end check, so the wave's last
                // observation counts in its end line (O(members) per tick).
                if (_waveMembers.Count > 0) TrackOnTarget(strikePilots, station);

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
                        EndWave(time, "members-gone", station);
                    else
                        _waveMembers.RemoveWhere(id => !strikePilots.Any(p => p.Id == id));
                }
                if (_waveMembers.Count > 0)
                {
                    bool anyMemberInTarget = strikePilots.Any(p => _waveMembers.Contains(p.Id) && p.Sector == station.Sector);
                    if (!anyMemberInTarget && time - _committedAt >= RallyTimeoutSeconds)
                        EndWave(time, "no-members-in-target", station);
                    else if (time - _committedAt >= 3 * RallyTimeoutSeconds)
                        EndWave(time, "timeout", station);   // one straggler must not hold the next group forever
                }
                // SC9 — follow/re-point escorts while the wave lives.
                if (_waveMembers.Count > 0) MaintainEscorts(pilots);
                if (!mass)
                {
                    // Legacy SC7/SC8 quorum for a target without a core profile.
                    bool timedOut = time - _rallySince >= RallyTimeoutSeconds;
                    int timeoutQuorum = Math.Min(2, roster);
                    bool fullGroup = roster >= StrikeGroupSize && atRally >= StrikeGroupSize;
                    bool commit = fullGroup || (timedOut && atRally >= timeoutQuorum);
                    if (_waveMembers.Count == 0 && commit)
                        CommitWave(time, atRallyCover, station, fullGroup ? "full" : "timeout", pilots);
                }
                // P801 — a mass wave is committed by UpdateStaging on its go tick;
                // this tick's order loop then releases every member together.

                foreach (var pilot in (aborted ? [] : strikePilots)
                    .OrderBy(p => _sent.TryGetValue(p.Id, out var sent) ? sent.Time : double.NegativeInfinity).ThenBy(p => p.Id))
                {
                    // Commit together: only the wave's members attack the shared
                    // station; a latecomer keeps (or gets) the rally goto and joins the
                    // next wave. While staging, nobody may shortcut: even a pilot
                    // already at the station holds for the mass. A lone wolf flies to
                    // its OWN entry aleph, never the visible element's lane.
                    bool stagingNow = _staging && _waveMembers.Count == 0;
                    bool wolf = _loneWolfRoute.TryGetValue(pilot.Id, out var wolfRoute);
                    bool alreadyOn = !stagingNow && pilot.Sector == station.Sector
                        && (pilot.Position - station.Position).LengthSquared() <= StrikeOnTargetRadius * StrikeOnTargetRadius;
                    bool attack = _waveMembers.Contains(pilot.Id) || alreadyOn;
                    Target? gotoPoint = stagingNow
                        ? (wolf ? wolfRoute! : stagePoint)
                        : _rally;
                    Order order = attack
                        ? new Order(pilot.Id, OrderChange.CidAttack, IgcObjectType.Station, stationId)
                        : (gotoPoint is { } r
                            ? new Order(pilot.Id, OrderChange.CidGoto, r.Type, r.Id)
                            : new Order(pilot.Id, OrderChange.CidAttack, IgcObjectType.Station, stationId));
                    // Positions are per-sector: in another sector the distance means
                    // nothing, so only a sector change counts as progress there.
                    float distance = order.Command == OrderChange.CidGoto && gotoPoint is { } rallyPoint
                        ? (pilot.Sector == rallyPoint.Sector ? (pilot.Position - rallyPoint.Position).Length() : float.MaxValue)
                        : 0;
                    if (!ShouldSend(pilot, order, time, distance, station.Sector)) continue;
                    RecordSend(pilot, order, time, distance);
                    result.Add(order);
                    if (result.Count >= MaxOrdersPerTick) break;
                }
            }
        }
        // SC9 — the wave's escort orders go out after its attack orders and before
        // the non-strike loop. MaxOrdersPerTick still bounds the whole tick.
        if (_waveMembers.Count > 0) EmitEscortOrders(time, pilots, result);
        else _escorts.Clear();
        // P832b: assign the first half of available fighters deterministically,
        // independent of ship-id parity or worker allocation. Keep the rest home.
        HashSet<short>? raiders = null;
        if (EconomicRaidActive)
        {
            var raidPilots = pilots.Where(p => !p.Strike && !p.Support && !_escorts.ContainsKey(p.Id))
                .OrderBy(p => p.Id).ToArray();
            raiders = raidPilots.Take(raidPilots.Length / 2).Select(p => p.Id).ToHashSet();
        }
        // Oldest assignments first; bound each tick so a large fleet cannot flood chat.
        foreach (var pilot in pilots.Where(p => !(p.Strike && !p.Support) && !_escorts.ContainsKey(p.Id))
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
                // P832b: retain home-bomber interception above; at most half
                // the fighters raid observed miners once the enemy invests 10%.
                // Other fighters and support retain defence/repair; bombers retain strikes.
                if (raiders?.Contains(pilot.Id) == true)
                    target ??= targets.Where(t => !t.Friendly && t.Type == IgcObjectType.Ship && t.Miner)
                        .OrderBy(t => t.Sector == pilot.Sector ? 0 : 1)
                        .ThenBy(t => t.Sector == pilot.Sector ? (t.Position - pilot.Position).LengthSquared() : 0)
                        .ThenBy(t => t.Id).FirstOrDefault();
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
    /// for the next wave. SC9 also assigns the wave's escorts here and emits one
    /// summary event for the rig log.</summary>
    private void CommitWave(double time, Pilot[] atRally, Target station, string reason, Pilot[] pilots)
    {
        _waveNumber++;
        _waveMembers.Clear();
        _visibleMembers.Clear();
        foreach (var pilot in atRally)
        {
            _waveMembers.Add(pilot.Id);
            _visibleMembers.Add(pilot.Id);
        }
        _committedAt = time;
        _waveArrived.Clear();
        _wavePeakOnTarget = 0;
        SelectEscorts(pilots);
        _events.Add(new(StrikeEventKind.Commit, _waveNumber, _waveMembers.Count, atRally.Length,
            station.Id, station.Sector, reason, Escorts: _escorts.Count,
            StationHull: station.Hull, StationShield: station.Shield));
        ResetStage();
    }

    // ── P801 — mass staging ─────────────────────────────────────────────────

    /// <summary>P801 — compute this strike's requirement and choose its members.
    /// The visible element must gather at the rally; up to
    /// <see cref="MaxLoneWolves"/> cloak-capable bombers are assigned their own
    /// entry aleph (a different lane from the visible group) and join the SAME
    /// go time. When the side fields no cloak hull the strike is all visible.</summary>
    private void BeginStage(double time, Target station, Pilot[] strikePilots, int roster, Target[] targets)
    {
        _staging = true;
        _stageSince = time;
        _goTime = double.NaN;
        _loneWolfRoute.Clear();
        _visibleMembers.Clear();
        int required = StrikeGroupSize;
        if (station.Profile is { } profile)
            required = StrikeVolley.Compute(profile,
                strikePilots.Select(p => p.Loadout).ToArray(), roster).BombersRequired;
        _required = Math.Max(1, Math.Min(required, Math.Max(1, roster)));
        int cloak = strikePilots.Count(p => !p.Support
            && (p.CloakCapable || p.Loadout is { CloakCapable: true }));
        int wolves = Math.Min(MaxLoneWolves, Math.Min(_required - 1, cloak));
        _visibleRequired = Math.Max(1, _required - wolves);
        var used = new HashSet<short>();
        if (_rally is { } visibleRally) used.Add(visibleRally.Id);
        var lanes = targets.Where(t => t.Type == IgcObjectType.Warp && t.DestinationSector == station.Sector
                && !used.Contains(t.Id))
            .OrderBy(t => t.Id).ToArray();
        int lane = 0;
        foreach (var wolf in strikePilots.Where(p => p.CloakCapable || p.Loadout is { CloakCapable: true })
            .OrderBy(p => p.Id).Take(wolves))
            _loneWolfRoute[wolf.Id] = lanes.Length > 0 ? lanes[lane++ % lanes.Length] : (_rally ?? station);
    }

    /// <summary>P801 — hold until the required visible element (and the assigned
    /// lone wolves) are at their staging points, then set one shared go time.
    /// Before the go tick nobody attacks. If the quota cannot be assembled inside
    /// <see cref="StagingHardCapSeconds"/> the strike aborts rather than
    /// trickling; the station goes on a short cooldown.</summary>
    private bool UpdateStaging(double time, Target station, Pilot[] strikePilots, Pilot[] pilots)
    {
        Target stagePoint = _rally ?? station;
        var visible = strikePilots.Where(p => !_loneWolfRoute.ContainsKey(p.Id)).ToArray();
        int atRally = visible.Count(p => p.Sector == stagePoint.Sector
            && (p.Position - stagePoint.Position).LengthSquared() <= RallyGatherRadius * RallyGatherRadius);
        int wolvesReady = _loneWolfRoute.Count(kv =>
            strikePilots.FirstOrDefault(p => p.Id == kv.Key) is { } wolf
            && (wolf.Sector == station.Sector || wolf.Sector == kv.Value.Sector));
        int assembled = atRally + wolvesReady;
        // A requirement of one is already the whole strike: no reason to hold it.
        // With no rally point at all (no known aleph) the legacy direct attack
        // is preserved, so the target is not approached as a staging point.
        bool instantLegacy = _required <= 1 && _rally is null;
        var memberPilots = instantLegacy
            ? visible
            : visible.Where(p => p.Sector == stagePoint.Sector
                && (p.Position - stagePoint.Position).LengthSquared() <= RallyGatherRadius * RallyGatherRadius).ToArray();
        bool ready = instantLegacy || StagingReady(atRally, _visibleRequired, wolvesReady, _loneWolfRoute.Count,
            _required, time - _stageSince);
        // Never issue an empty commit (that would restart staging every tick and
        // flood the [strike] log): if no visible member is actually at the staging
        // point yet, keep holding.
        if (memberPilots.Length == 0 && _loneWolfRoute.Count == 0) return false;
        if (ready)
        {
            // A one-bomber requirement has nothing to synchronise with; a real
            // mass keeps the lead so every release lands inside one window.
            if (double.IsNaN(_goTime)) _goTime = time + (_required <= 1 ? 0 : StrikeGoLeadSeconds);
        }
        else _goTime = double.NaN;

        // A wave that is ready (its go time is set) is never aborted by the cap: it
        // launches on its go tick (rig 221 batch 23b: required=1 assembled=1 aborted at 204 s).
        if (!ready && time - _stageSince >= StagingHardCapSeconds)
        {
            if (_strikeTarget is { } id) _abandonedUntil[id] = time + StagingAbandonSeconds;
            _events.Add(new(StrikeEventKind.Abort, _waveNumber, 0, 0, _strikeTarget ?? -1, _strikeSector,
                "insufficient-volley", Required: _required, Assembled: assembled,
                WaitSeconds: time - _stageSince, Visible: atRally, LoneWolves: _loneWolfRoute.Count));
            ResetStage();
            _strikeTarget = null;
            _rally = null;
            _rallySince = double.NaN;
            _stationSignature = [];
            return true;
        }
        if (double.IsNaN(_goTime) || time < _goTime) return false;

        // The shared go time: every visible member at the rally plus every
        // assigned lone wolf commits on this tick, so all releases land inside
        // one volley window. The lone wolves are members but never get escorts.
        _waveNumber++;
        _waveMembers.Clear();
        _visibleMembers.Clear();
        foreach (var pilot in memberPilots)
        {
            _waveMembers.Add(pilot.Id);
            _visibleMembers.Add(pilot.Id);
        }
        foreach (short wolf in _loneWolfRoute.Keys) _waveMembers.Add(wolf);
        _committedAt = time;
        _waveArrived.Clear();
        _wavePeakOnTarget = 0;
        SelectEscorts(pilots);
        _events.Add(new(StrikeEventKind.Commit, _waveNumber, _waveMembers.Count, _visibleMembers.Count,
            station.Id, station.Sector, "massed", Escorts: _escorts.Count,
            StationHull: station.Hull, StationShield: station.Shield,
            Required: _required, Assembled: assembled, WaitSeconds: time - _stageSince,
            Visible: _visibleMembers.Count, LoneWolves: _loneWolfRoute.Count, GoInSeconds: 0));
        _staging = false;
        _goTime = double.NaN;
        _stageSince = double.NaN;
        _loneWolfRoute.Clear();
        return false;
    }

    private void ResetStage()
    {
        _staging = false;
        _stageSince = double.NaN;
        _goTime = double.NaN;
        _required = 0;
        _visibleRequired = 0;
        _loneWolfRoute.Clear();
        _visibleMembers.Clear();
    }

    /// <summary>SC8 — close the current wave (station gone, or no member left alive
    /// and undocked in the target sector) and restart the rally timeout so the next
    /// group forms from whoever is at the rally. SC9 adds the wave's arrival record,
    /// escort count and observed station health (unknown = -1) to the end line.</summary>
    private void EndWave(double time, string reason, Target? station = null)
    {
        ResetStage();
        if (_waveMembers.Count == 0) return;
        _events.Add(new(StrikeEventKind.End, _waveNumber, _waveMembers.Count, 0, _strikeTarget ?? -1, _strikeSector, reason,
            Escorts: _escorts.Count,
            StationHull: station?.Hull ?? -1, StationShield: station?.Shield ?? -1,
            Arrived: _waveArrived.Count, PeakOnTarget: _wavePeakOnTarget,
            DurationSeconds: double.IsNaN(_committedAt) ? -1 : time - _committedAt));
        _waveMembers.Clear();
        _escorts.Clear();
        _waveArrived.Clear();
        _wavePeakOnTarget = 0;
        _committedAt = double.NaN;
        _rallySince = time;
    }

    /// <summary>SC9 — the observed enemy-station signature (id + damage + garrison +
    /// sector). An unchanged set skips the O(stations × strike pilots) re-pick.</summary>
    private static (short Id, bool Damaged, bool Garrison, short Sector)[] StationSignature(Target[] enemyStations)
        => enemyStations.Select(s => (s.Id, s.Damaged, s.Garrison, s.Sector)).ToArray();

    /// <summary>SC9 — count the wave members ever seen inside
    /// <see cref="StrikeOnTargetRadius"/> of the station, and the most seen there at
    /// once. O(members) per tick; never sends orders.</summary>
    private void TrackOnTarget(Pilot[] strikePilots, Target station)
    {
        int onTarget = 0;
        foreach (var pilot in strikePilots)
        {
            if (!_waveMembers.Contains(pilot.Id) || pilot.Sector != station.Sector) continue;
            if ((pilot.Position - station.Position).LengthSquared() > StrikeOnTargetRadius * StrikeOnTargetRadius) continue;
            onTarget++;
            _waveArrived.Add(pilot.Id);
        }
        if (onTarget > _wavePeakOnTarget) _wavePeakOnTarget = onTarget;
    }

    /// <summary>SC9 — pick the wave's escorts: the non-strike, non-support pilots
    /// nearest the rally point, preferring the rally sector; at most
    /// <see cref="EscortsPerMember"/> per member and <see cref="MaxEscorts"/> total,
    /// assigned round-robin to the members. Escort orders are native `c_cidDefend`
    /// ship orders (shipIGC.cpp:2373-2404, 2687-2696; port: BotManager.Orders.cs).</summary>
    private void SelectEscorts(Pilot[] pilots)
    {
        _escorts.Clear();
        if (_waveMembers.Count == 0) return;
        var visible = _visibleMembers.Count > 0 ? _visibleMembers : _waveMembers;
        int cap = Math.Min(MaxEscorts, visible.Count * EscortsPerMember);
        if (cap <= 0) return;
        var rally = _rally;
        var candidates = pilots.Where(p => !p.Strike && !p.Support)
            .OrderBy(p => rally is { } r && p.Sector == r.Sector ? 0 : 1)
            .ThenBy(p => rally is { } r && p.Sector == r.Sector
                ? (p.Position - r.Position).LengthSquared() : 0f)
            .ThenBy(p => p.Id)
            .Take(cap)
            .ToArray();
        var members = visible.OrderBy(id => id).ToArray();
        for (int i = 0; i < candidates.Length; i++)
            _escorts[candidates[i].Id] = members[i % members.Length];
    }

    /// <summary>SC9 — keep escort assignments valid while the wave lives: drop an
    /// escort that left the snapshot, and re-point an escort whose protectee left
    /// the wave to another live member (a changed order, so it goes through
    /// <see cref="ShouldSend"/>).</summary>
    private void MaintainEscorts(Pilot[] pilots)
    {
        if (_waveMembers.Count == 0)
        {
            _escorts.Clear();
            return;
        }
        var visible = _visibleMembers.Count > 0 ? _visibleMembers : _waveMembers;
        var live = visible.Where(id => pilots.Any(p => p.Id == id)).OrderBy(id => id).ToArray();
        if (live.Length == 0)
        {
            _escorts.Clear();
            return;
        }
        foreach (short id in _escorts.Keys.Where(id => !pilots.Any(p => p.Id == id)).ToArray()) _escorts.Remove(id);
        var liveSet = live.ToHashSet();
        int next = 0;
        foreach (short escort in _escorts.Keys.OrderBy(id => id).ToArray())
            if (!liveSet.Contains(_escorts[escort]))
                _escorts[escort] = live[next++ % live.Length];
    }

    /// <summary>SC9 — send the wave's escort orders: `c_cidDefend`, type Ship, the
    /// assigned wave member. A defend order is flown by the port's native defend
    /// rule (BotManager.Orders.cs), so the escort picks up enemies near the
    /// protectee and returns when it strays.</summary>
    private void EmitEscortOrders(double time, Pilot[] pilots, List<Order> result)
    {
        if (_waveMembers.Count == 0)
        {
            _escorts.Clear();
            return;
        }
        foreach (var (escortId, protectee) in _escorts.OrderBy(kv => kv.Key).ToArray())
        {
            if (result.Count >= MaxOrdersPerTick) break;
            var pilot = pilots.FirstOrDefault(p => p.Id == escortId);
            if (pilot is null) continue;
            var member = pilots.FirstOrDefault(p => p.Id == protectee && _waveMembers.Contains(p.Id));
            if (member is null) continue;
            var order = new Order(escortId, OrderChange.CidDefend, IgcObjectType.Ship, protectee);
            if (!ShouldSend(pilot, order, time, 0, member.Sector)) continue;
            RecordSend(pilot, order, time, 0);
            result.Add(order);
        }
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
    /// 1. NON-Garrison stations before any Garrison — a Garrison is the toughest
    ///    hull (highest HP), so it is a last resort, taken only when no other enemy
    ///    station is known (a scratched Garrison must not outrank a soft station:
    ///    that brought back the batch-15 "Garrison all match" lock);
    /// 2. within that, already-damaged stations (<see cref="Target.Damaged"/>)
    ///    first — finish the wounded;
    /// 3. then by distance from the strike group's centroid, then id.
    /// SC9 re-runs this at every wave boundary; SC7's "commit, don't flick" keeps
    /// the pick stable while a wave is in flight.</summary>
    private static Target? PickStrikeStation(Target[] enemyStations, Pilot[] strikePilots,
        double time = double.NegativeInfinity, IReadOnlyDictionary<short, double>? abandoned = null)
    {
        if (enemyStations.Length == 0) return null;
        // P801 — a station whose volley could not be assembled is skipped for a
        // short cooldown. If every known station is cooling down, use the whole
        // set anyway (the side must still be able to pick an objective).
        if (abandoned is not null)
        {
            var eligible = enemyStations.Where(s => abandoned.GetValueOrDefault(s.Id) <= time).ToArray();
            if (eligible.Length > 0) enemyStations = eligible;
        }
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
            .OrderBy(s => s.Garrison ? 1 : 0)
            .ThenByDescending(s => s.Damaged)
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
