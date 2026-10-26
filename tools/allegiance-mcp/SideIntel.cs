using Allegiance.ClientCore.State;

namespace Allegiance.Tools.McpSpawner;

// ─────────────────────────────────────────────────────────────────────────────
// P798 — per-side knowledge, exploration planning and clash-location scoring.
//
// Everything in this file is pure data + pure functions: it takes only what a
// side has actually observed (never server truth, never another side's world)
// and answers three questions:
//   • which sectors are still frontiers to scout (ExplorationPlanner),
//   • which scout should take which frontier (one lane per scout),
//   • where the main clash should form, from the resources seen and the
//     topology (ClashScoring).
//
// Native knowledge model this builds on (P797):
//   • static objects are exported per side only after SeenBySide
//     (FedSrv/fsmission.cpp:4169-4250);
//   • an aleph is visible to a side only once seen (Igc/warpIGC.cpp:35) and
//     seeing one reveals its partner to that side (FedSrv.CPP:10786-10800);
//   • routing is only possible over alephs the side can see
//     (Igc/common.cpp:894-960, CanSee at :916; shipIGC.h:380-388);
//   • ore knowledge is per side (Igc/asteroidigc.h:78-323, GetOreSeenBySide
//     at :280-285) and the top-level topology/home flags always arrive
//     (fsmission.cpp:4122-4174).
//
// The bot-side policy (how many scouts, how long a sweep lasts, when the clash
// is latched) is NOT native: it is the P798 design, with every tunable in
// ScoutOpening (bottom of this file).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>One aleph this side has seen, with the sector its partner sits in.
/// Native reveals the partner the moment either end is seen
/// (FedSrv.CPP:10786-10800), so a side never needs the partner's position to
/// know the edge exists.</summary>
internal readonly record struct WarpKnowledge(short WarpId, short Sector, short PartnerSector);

/// <summary>One rock this side has seen. <see cref="Ore"/> is the ore that was
/// visible when it was seen — native's per-side oreSeen store
/// (Igc/asteroidigc.h:78-323); it is never re-read from server truth.</summary>
internal readonly record struct RockSighting(short RockId, short Sector, float Ore, float OreMax, short Capabilities)
{
    internal bool He3 => (Capabilities & 0x01) != 0;
    internal bool LotsHe3 => (Capabilities & 0x02) != 0;
    internal bool Gold => (Capabilities & 0x04) != 0;
    internal bool Buildable => (Capabilities & 0x08) != 0;
    internal bool Special => (Capabilities & 0x10) != 0;

    /// <summary>How valuable this rock is as a clash anchor: He3 (the economy's
    /// fuel) first, gold second, then station-buildable/special rocks. Pure so
    /// the weighting is unit-provable.</summary>
    internal double ResourceWeight =>
        (He3 ? (LotsHe3 ? 1.5 : 1.0) : 0)
        + (Gold ? 1.5 : 0)
        + (Buildable ? 0.8 : 0)
        + (Special ? 0.6 : 0);

    /// <summary>Weighted ore seen (ore × weight / 1000 so a rich rock is O(10)).</summary>
    internal double Value => Math.Max(0, Ore) * ResourceWeight / 1000.0;
}

/// <summary>A station this side has seen (enemy stations drive intel and the
/// finale; own stations anchor the home sector).</summary>
internal readonly record struct StationSighting(short StationId, short SideId, short StationTypeId, short Sector);

/// <summary>An enemy hull seen over time, with the loadout parts this side
/// actually observed on it (same-side equipment is not broadcast; what is not
/// on the wire is simply absent).</summary>
internal sealed record HullSighting(short ShipId, short SideId, short HullId, short Sector, double At, string Role, string? Loadout)
{
    internal HullSighting Touch(short sector, double at, string? loadout) =>
        this with { Sector = sector, At = at, Loadout = loadout ?? Loadout };
}

/// <summary>A miner seen (own or enemy) — the early economy clash happens where
/// miners work, so sightings are time-stamped and pruned.</summary>
internal sealed record MinerSighting(short ShipId, short SideId, short Sector, double At);

/// <summary>One frontier lane: a topology edge from a known sector to a sector
/// whose contents the side has not seen yet. <see cref="EntryKnown"/> is false
/// when the aleph that leads there has not been seen either — the scout must
/// first sweep <see cref="From"/> to find it (native cannot route over an
/// unseen aleph, Igc/common.cpp:916).</summary>
internal readonly record struct FrontierLane(short From, short To, short EntryWarpId, bool EntryKnown, double Score, string Why)
{
    internal LaneKey Key => new(From, To);
}

/// <summary>A topology edge used as an exploration lane key (record so it
/// round-trips the worker coordination JSON as real properties).</summary>
internal readonly record struct LaneKey(short From, short To);

/// <summary>One scout considered by the assignment (stable bot id, current
/// sector and its hull's scanner reach). Kept simple so the planner stays pure.
/// <see cref="ScannerRange"/> lets the enemy-home recon prefer the hull that can
/// actually see a sector's contents (the fleet mix contains 400-unit
/// interceptors next to 2400-3000 unit scouts).</summary>
internal readonly record struct ScoutPost(int Id, short Sector, float ScannerRange = 0f);

/// <summary>One sector's aggregate intel, as used by the clash scoring.</summary>
internal readonly record struct ClashChoice(short Sector, double Score, string Why,
    double Resource, int OwnMiners, int EnemyMiners, bool EnemyStation, int OwnHops, int EnemyHops);

/// <summary>P798 — pure frontier enumeration and one-lane-per-scout assignment.</summary>
internal static class ExplorationPlanner
{
    /// <summary>The sectors adjacent (topology) to a known sector whose contents
    /// have not been seen. Deterministic: known sectors and neighbours sorted
    /// ascending, ties by (From, To). <paramref name="doneLanes"/> lets a lane a
    /// scout already swept (or timed out on) stay out of the pool.</summary>
    internal static List<FrontierLane> FindFrontiers(ClusterGraph? graph,
        IReadOnlyCollection<short> knownSectors, IReadOnlyDictionary<short, WarpKnowledge> warps,
        short ownHome, IReadOnlySet<LaneKey>? doneLanes = null, IReadOnlySet<short>? sweptSectors = null)
    {
        var lanes = new List<FrontierLane>();
        if (graph is null) return lanes;
        var known = knownSectors as IReadOnlySet<short> ?? knownSectors.ToHashSet();
        var adj = BuildAdjacency(graph);
        var homes = graph.Clusters.Where(c => c.HomeSector).Select(c => c.Id).ToHashSet();
        var byLane = new Dictionary<LaneKey, FrontierLane>();
        foreach (short from in known.Order())
        {
            // A sector the side has seen (e.g. a combat ship transited it) but no
            // scout has swept still needs its alephs/rocks/station recorded: a
            // self-lane means "sweep this sector". Home is covered by its station.
            if (sweptSectors is not null && from != ownHome && !sweptSectors.Contains(from)
                && (doneLanes is null || !doneLanes.Contains(new LaneKey(from, from))))
            {
                var self = new FrontierLane(from, from, -1, true, 1500, "known sector not yet swept");
                if (!byLane.ContainsKey(self.Key)) byLane[self.Key] = self;
            }
            if (!adj.TryGetValue(from, out var neighbours)) continue;
            foreach (short to in neighbours.Order())
            {
                if (known.Contains(to)) continue;
                if (doneLanes is not null && doneLanes.Contains(new LaneKey(from, to))) continue;
                // The lowest known aleph in `from` whose partner sits in `to`.
                WarpKnowledge? entry = warps.Values
                    .Where(w => w.Sector == from && w.PartnerSector == to)
                    .OrderBy(w => w.WarpId).Cast<WarpKnowledge?>().FirstOrDefault();
                bool entryKnown = entry is not null;
                var why = new List<string>();
                double score = entryKnown ? 1000 : 0;
                if (!entryKnown) why.Add("entry aleph unknown — sweep");
                else why.Add($"aleph {entry!.Value.WarpId}");
                if (homes.Contains(to)) { score += 400; why.Add("home sector (ownership unknown)"); }
                int hops = Hops(graph, ownHome, from);
                if (hops >= 0) score -= hops * 25;
                if (warps.Values.Any(w => w.Sector == to)) { score += 5; why.Add("partner alephs known"); }
                var lane = new FrontierLane(from, to, entry?.WarpId ?? -1, entryKnown, score,
                    string.Join(", ", why));
                if (!byLane.TryGetValue(lane.Key, out var old) || lane.Score > old.Score) byLane[lane.Key] = lane;
            }
        }
        lanes.AddRange(byLane.Values);
        lanes.Sort((a, b) =>
        {
            int c = b.Score.CompareTo(a.Score);
            if (c != 0) return c;
            c = a.From.CompareTo(b.From);
            return c != 0 ? c : a.To.CompareTo(b.To);
        });
        return lanes;
    }

    /// <summary>Assign lanes to scouts so that no two scouts in the same call
    /// take the same lane. Current assignments are kept when still valid (P830:
    /// a lane is only released when it left the pool — swept/timed out/known —
    /// or a free lane beats the held one by <see cref="ScoutOpening.LaneSwitchScoreMargin"/>,
    /// so an unchanged input assigns exactly the same lanes every pass); the
    /// rest are filled greedily (highest lane score, then fewest hops from the
    /// scout, then scout id) — deterministic and O(scouts × lanes).</summary>
    internal static Dictionary<int, FrontierLane> Assign(IReadOnlyList<FrontierLane> lanes,
        IReadOnlyList<ScoutPost> scouts, Func<short, short, int> hops,
        IReadOnlyDictionary<int, FrontierLane>? current = null)
    {
        var result = new Dictionary<int, FrontierLane>();
        var used = new HashSet<LaneKey>();
        var kept = new Dictionary<int, FrontierLane>();
        if (current is not null)
            foreach (var (id, lane) in current.OrderBy(kv => kv.Key))
            {
                if (!lanes.Any(l => l.Key == lane.Key) || used.Contains(lane.Key)) continue;
                kept[id] = lane;
                used.Add(lane.Key);
            }
        foreach (var scout in scouts.OrderBy(s => s.Id))
        {
            if (kept.ContainsKey(scout.Id) || result.ContainsKey(scout.Id)) continue;
            FrontierLane? best = null;
            double bestScore = double.NegativeInfinity;
            int bestHops = int.MaxValue;
            foreach (var lane in lanes)
            {
                if (used.Contains(lane.Key)) continue;
                int h = hops(scout.Sector, lane.From);
                if (h < 0) continue;                          // unreachable from here
                // Score dominates; distance breaks ties (a nearer lane is cheaper).
                double rank = lane.Score * 100.0 - h * 10.0;
                if (rank > bestScore || (rank == bestScore && h < bestHops))
                { best = lane; bestScore = rank; bestHops = h; }
            }
            if (best is { } chosen) { result[scout.Id] = chosen; used.Add(chosen.Key); }
        }
        // P830 — hysteresis on the kept lanes: a held lane is given up only for
        // a free lane that beats it by the switch margin (never one another
        // scout already holds — no two scouts ever fight over a lane). Ties and
        // release order are deterministic (kept lanes by scout id).
        foreach (var (id, lane) in kept.OrderBy(kv => kv.Key))
        {
            var post = scouts.FirstOrDefault(s => s.Id == id);
            FrontierLane? better = null;
            double betterRank = double.NegativeInfinity;
            int betterHops = int.MaxValue;
            if (post.Id == id)
                foreach (var cand in lanes)
                {
                    if (used.Contains(cand.Key)) continue;
                    int h = hops(post.Sector, cand.From);
                    if (h < 0) continue;
                    double rank = cand.Score * 100.0 - h * 10.0;
                    if (rank > betterRank || (rank == betterRank && h < betterHops))
                    { better = cand; betterRank = rank; betterHops = h; }
                }
            if (better is { } swap && swap.Score >= lane.Score + ScoutOpening.LaneSwitchScoreMargin)
            {
                used.Remove(lane.Key);                        // released back to the pool
                result[id] = swap;
                used.Add(swap.Key);
            }
            else result[id] = lane;
        }
        return result;
    }

    /// <summary>All sectors reachable from <paramref name="from"/> over the
    /// topology (native's map preview knows the whole graph, fsmission.cpp:4122-4174).</summary>
    internal static HashSet<short> Reachable(ClusterGraph? graph, short from)
    {
        var seen = new HashSet<short>();
        if (graph is null) return seen;
        var adj = BuildAdjacency(graph);
        var queue = new Queue<short>();
        seen.Add(from);
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            short cur = queue.Dequeue();
            if (!adj.TryGetValue(cur, out var nbrs)) continue;
            foreach (short n in nbrs) if (seen.Add(n)) queue.Enqueue(n);
        }
        return seen;
    }

    internal static Dictionary<short, List<short>> BuildAdjacency(ClusterGraph graph)
    {
        var adj = new Dictionary<short, List<short>>();
        void Add(short a, short b)
        {
            if (!adj.TryGetValue(a, out var list)) adj[a] = list = [];
            if (!list.Contains(b)) list.Add(b);
        }
        foreach (var w in graph.Warps) { Add(w.SourceClusterId, w.DestClusterId); Add(w.DestClusterId, w.SourceClusterId); }
        foreach (var list in adj.Values) list.Sort();
        return adj;
    }

    /// <summary>P798b — the first sector on a shortest topology path from
    /// <paramref name="from"/> to <paramref name="to"/> (the next hop a recon
    /// scout must take), or -1 when unreachable. The topology is global
    /// (<c>CLUSTERINFO(200)</c>, fsmission.cpp:4122-4174) even though the alephs
    /// inside a sector are not (P797), so this is knowledge the side really has.
    /// Pure and deterministic (BFS, neighbours ascending).</summary>
    internal static short NextHop(ClusterGraph? graph, short from, short to)
    {
        if (graph is null || from < 0 || to < 0) return -1;
        if (from == to) return from;              // already there: no hop needed
        var adj = BuildAdjacency(graph);
        var firstStep = new Dictionary<short, short>();
        var seen = new HashSet<short> { from };
        var queue = new Queue<short>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            short cur = queue.Dequeue();
            if (!adj.TryGetValue(cur, out var nbrs)) continue;
            foreach (short n in nbrs.Order())
            {
                if (!seen.Add(n)) continue;
                firstStep[n] = cur == from ? n : firstStep[cur];
                if (n == to) return firstStep[n];
                queue.Enqueue(n);
            }
        }
        return -1;
    }

    /// <summary>P798b — which scout should fly the enemy-home recon: the widest
    /// scanner first (it is what reveals a sector rim), then the fewest topology
    /// hops, then the stable bot id. Pure so the choice is unit-provable; null
    /// when the target is unknown or no scout can reach it.</summary>
    internal static int? PickReconScout(IReadOnlyList<ScoutPost> scouts, Func<short, short, int> hops, short target)
    {
        if (target < 0) return null;
        int? best = null;
        int bestHops = int.MaxValue;
        float bestScan = float.NegativeInfinity;
        foreach (var scout in scouts.OrderBy(s => s.Id))
        {
            int h = hops(scout.Sector, target);
            if (h < 0) continue;
            // Widest scanner first (it is what actually reveals a sector rim),
            // then nearest, then stable id.
            if (scout.ScannerRange > bestScan || (scout.ScannerRange == bestScan && h < bestHops))
            { best = scout.Id; bestHops = h; bestScan = scout.ScannerRange; }
        }
        return best;
    }

    internal static int Hops(ClusterGraph graph, short from, short to)
    {
        if (from < 0 || to < 0) return -1;
        if (from == to) return 0;
        var adj = BuildAdjacency(graph);
        var visited = new HashSet<short> { from };
        var queue = new Queue<(short Node, int Hops)>();
        queue.Enqueue((from, 0));
        while (queue.Count > 0)
        {
            var (cur, hops) = queue.Dequeue();
            if (!adj.TryGetValue(cur, out var nbrs)) continue;
            foreach (short n in nbrs)
            {
                if (!visited.Add(n)) continue;
                if (n == to) return hops + 1;
                queue.Enqueue((n, hops + 1));
            }
        }
        return -1;
    }
}

/// <summary>P798 — the clash location comes from the resources a side has seen,
/// contested by miner activity and enemy presence, discounted by the distance
/// from both homes over the topology. No sector/civ/map name is consulted.</summary>
internal static class ClashScoring
{
    /// <summary>Score every candidate sector and pick the best. Candidates are
    /// the sectors the side has seen that carry a resource, a miner sighting or
    /// an enemy station. Returns null while nothing is known. With
    /// <paramref name="topologyFallback"/> and no resource candidate, the most
    /// central KNOWN sector (fewest own+enemy hops) is chosen, so a map whose
    /// rocks are all unscouted still produces an opening (recorded in Why).</summary>
    internal static ClashChoice? Choose(ClusterGraph? graph, short ownHome, short enemyHome,
        IReadOnlyCollection<short> knownSectors,
        IEnumerable<RockSighting> rocks, IEnumerable<MinerSighting> ownMiners, IEnumerable<MinerSighting> enemyMiners,
        IEnumerable<StationSighting> enemyStations, bool topologyFallback = false)
    {
        if (graph is null) return null;
        var known = knownSectors as IReadOnlySet<short> ?? knownSectors.ToHashSet();
        var rockList = rocks as IReadOnlyCollection<RockSighting> ?? rocks.ToArray();
        var ownList = ownMiners as IReadOnlyCollection<MinerSighting> ?? ownMiners.ToArray();
        var enemyList = enemyMiners as IReadOnlyCollection<MinerSighting> ?? enemyMiners.ToArray();
        var stationList = enemyStations as IReadOnlyCollection<StationSighting> ?? enemyStations.ToArray();

        ClashChoice? Best(IEnumerable<short> candidates, bool fallback)
        {
            ClashChoice? best = null;
            foreach (short sector in candidates.Distinct().Order())
            {
                double resource = rockList.Where(r => r.Sector == sector).Sum(r => r.Value);
                int own = ownList.Count(m => m.Sector == sector);
                int enemy = enemyList.Count(m => m.Sector == sector);
                bool station = stationList.Any(s => s.Sector == sector);
                int hopsOwn = ExplorationPlanner.Hops(graph, ownHome, sector);
                int hopsEnemy = ExplorationPlanner.Hops(graph, enemyHome, sector);
                if (hopsOwn < 0) hopsOwn = 32;                 // unreachable anchor — deprioritised
                if (hopsEnemy < 0) hopsEnemy = 32;
                double raw = 1.0 + resource + own * ScoutOpening.OwnMinerWeight
                    + enemy * ScoutOpening.EnemyMinerWeight + (station ? ScoutOpening.EnemyStationWeight : 0);
                double score = raw / (1.0 + ScoutOpening.HopPenalty * (hopsOwn + hopsEnemy));
                var why = new List<string>();
                if (resource > 0) why.Add($"resources={resource:F1}");
                if (own > 0) why.Add($"ownMiners={own}");
                if (enemy > 0) why.Add($"enemyMiners={enemy}");
                if (station) why.Add("enemyStation");
                why.Add($"hops own={hopsOwn} enemy={hopsEnemy}");
                if (fallback) why.Add("topology fallback (no resource seen yet)");
                var choice = new ClashChoice(sector, score, string.Join(", ", why), resource, own, enemy, station, hopsOwn, hopsEnemy);
                if (best is null || score > best.Value.Score || (score == best.Value.Score && sector < best.Value.Sector))
                    best = choice;
            }
            return best;
        }

        var resourced = rockList.Where(r => known.Contains(r.Sector) && r.Value > 0).Select(r => r.Sector)
            .Concat(ownList.Where(m => known.Contains(m.Sector)).Select(m => m.Sector))
            .Concat(enemyList.Where(m => known.Contains(m.Sector)).Select(m => m.Sector))
            .Concat(stationList.Where(s => known.Contains(s.Sector)).Select(s => s.Sector))
            .ToHashSet();
        var picked = Best(resourced, false);
        if (picked is not null) return picked;
        if (!topologyFallback) return null;
        // Nothing with resources known yet: fall back to the most central known
        // sector (topology only — the map preview every side has).
        var central = known.Count > 0 ? known : ExplorationPlanner.Reachable(graph, ownHome);
        return Best(central, true);
    }
}

/// <summary>P798 — one side's accumulated knowledge. Mutable, guarded by the
/// owning <see cref="SideIntelStore"/>; methods only ever add what a scan of that
/// side's OWN bot worlds handed in.</summary>
internal sealed class SideIntel(short side, uint cookie)
{
    internal short Side { get; } = side;
    internal uint Cookie { get; } = cookie;
    internal short OwnHome = -1, EnemyHome = -1;
    internal readonly HashSet<short> KnownSectors = [];
    /// <summary>Sectors a scout actually flew a full sweep of (P798 completion
    /// criterion: contents seen is not enough — the frontier's alephs/rocks must
    /// have been swept, otherwise the scouts stop before hunting the miners).</summary>
    internal readonly HashSet<short> SweptSectors = [];
    internal readonly Dictionary<short, WarpKnowledge> Warps = [];
    internal readonly Dictionary<short, RockSighting> Rocks = [];
    internal readonly Dictionary<short, StationSighting> OwnStations = [];
    internal readonly Dictionary<short, StationSighting> EnemyStations = [];
    internal readonly Dictionary<short, HullSighting> EnemyShips = [];
    internal readonly Dictionary<short, MinerSighting> OwnMiners = [];
    internal readonly Dictionary<short, MinerSighting> EnemyMiners = [];
    internal readonly HashSet<LaneKey> DoneLanes = [];
    /// <summary>Retry-after stamps for frontier lanes a scout could not finish
    /// (the mouth aleph was not found in its sweep window). Kept out of the
    /// digest: it is a local scheduling hint, not knowledge.</summary>
    internal readonly Dictionary<LaneKey, double> LaneRetryAt = [];
    // Opening timeline (first-time latches, harness clock seconds).
    internal double FirstAlephAt = -1, FirstEnemyStationAt = -1, FirstMinerRaidAt = -1, FirstSupportCallAt = -1;
    internal int MinerRaids, SupportCalls;
    internal double LastScanAt;

    internal void MarkSector(short sector, double now)
    {
        if (sector < 0) return;
        KnownSectors.Add(sector);
        LastScanAt = now;
    }

    internal bool ObserveWarp(WarpKnowledge w, double now)
    {
        bool isNew = !Warps.ContainsKey(w.WarpId);
        Warps[w.WarpId] = w;
        // Knowing an aleph is NOT knowing its sector's contents: P797's
        // aleph-pair reveal exports the partner aleph (FedSrv.CPP:10786-10800),
        // so a side may hold a position in a sector it has never entered. Only
        // rocks/stations/own ships mark a sector seen.
        if (isNew && FirstAlephAt < 0) FirstAlephAt = now;
        return isNew;
    }

    internal void ObserveRock(RockSighting r, double now)
    {
        Rocks[r.RockId] = r;
        MarkSector(r.Sector, now);
    }

    internal void ObserveStation(StationSighting s, double now)
    {
        // The FIRST own station seen is the home base; a later forward base must
        // never move "own home" (the clash distance term anchors on the start).
        if (s.SideId == Side) { OwnStations[s.StationId] = s; if (OwnHome < 0) OwnHome = s.Sector; }
        else if (s.SideId >= 0)
        {
            bool isNew = !EnemyStations.ContainsKey(s.StationId);
            EnemyStations[s.StationId] = s;
            if (isNew && FirstEnemyStationAt < 0) FirstEnemyStationAt = now;
            // The enemy's restart/home station is the strongest enemy-home anchor
            // this side has actually observed.
            if (EnemyHome < 0) EnemyHome = s.Sector;
        }
        MarkSector(s.Sector, now);
    }

    internal void ObserveEnemyShip(short shipId, short sideId, short hullId, short sector, double now, string role, string? loadout)
    {
        if (sideId < 0 || sideId == Side) return;
        var row = EnemyShips.TryGetValue(shipId, out var old)
            ? old.Touch(sector, now, loadout)
            : new HullSighting(shipId, sideId, hullId, sector, now, role, loadout);
        EnemyShips[shipId] = row;
    }

    internal void ObserveMiner(short shipId, short sideId, short sector, double now)
    {
        if (sideId == Side) OwnMiners[shipId] = new MinerSighting(shipId, sideId, sector, now);
        else if (sideId >= 0) EnemyMiners[shipId] = new MinerSighting(shipId, sideId, sector, now);
    }

    /// <summary>Drop sightings older than the retention window and cap the hull
    /// table so a long run cannot leak (intel is bounded telemetry).</summary>
    internal void Prune(double now)
    {
        foreach (var id in EnemyShips.Where(kv => now - kv.Value.At > ScoutOpening.SightRetentionSeconds).Select(kv => kv.Key).ToArray())
            EnemyShips.Remove(id);
        if (EnemyShips.Count > ScoutOpening.MaxHullSightings)
            foreach (var id in EnemyShips.OrderBy(kv => kv.Value.At).Take(EnemyShips.Count - ScoutOpening.MaxHullSightings).Select(kv => kv.Key).ToArray())
                EnemyShips.Remove(id);
        foreach (var id in EnemyMiners.Where(kv => now - kv.Value.At > ScoutOpening.SightRetentionSeconds).Select(kv => kv.Key).ToArray())
            EnemyMiners.Remove(id);
        foreach (var id in OwnMiners.Where(kv => now - kv.Value.At > ScoutOpening.SightRetentionSeconds).Select(kv => kv.Key).ToArray())
            OwnMiners.Remove(id);
    }
}

/// <summary>Per-side intel for this process, keyed by (mission cookie, side).
/// A worker only ever stores what its own side's bots observed; the coordination
/// channel can merge the same structure from its sibling workers (see
/// BotManager.Exploration).</summary>
internal sealed class SideIntelStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(uint Cookie, short Side), SideIntel> _map = [];
    /// <summary>Coarse lock for all SideIntel mutation/read. The decision loop
    /// writes at ~1 Hz; the tool/coordinator read rarely, so one lock is cheap
    /// and removes every dictionary race by construction.</summary>
    internal object Sync => _gate;

    internal SideIntel Get(uint cookie, short side)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue((cookie, side), out var intel)) _map[(cookie, side)] = intel = new(side, cookie);
            return intel;
        }
    }

    internal SideIntel? Find(uint cookie, short side)
    {
        lock (_gate) return _map.TryGetValue((cookie, side), out var intel) ? intel : null;
    }

    internal T Read<T>(uint cookie, short side, Func<SideIntel, T> read)
    {
        lock (_gate) return read(Get(cookie, side));
    }

    internal void Write(uint cookie, short side, Action<SideIntel> write)
    {
        lock (_gate) write(Get(cookie, side));
    }

    internal List<SideIntel> All()
    {
        lock (_gate) return _map.Values.ToList();
    }

    internal T ReadAll<T>(Func<List<SideIntel>, T> read)
    {
        lock (_gate) return read(_map.Values.ToList());
    }

    /// <summary>A bounded copy of everything known, for the worker coordination
    /// channel (WorkerSupervisor). Only own-side knowledge is ever included.</summary>
    internal IntelDigest[] DigestAll()
    {
        lock (_gate) return _map.Values.Select(Snapshot).ToArray();
    }

    private static IntelDigest Snapshot(SideIntel i) => new(
        i.Cookie, i.Side, i.OwnHome, i.EnemyHome, i.KnownSectors.Order().ToArray(),
        i.SweptSectors.Order().ToArray(),
        i.DoneLanes.OrderBy(l => l.From).ThenBy(l => l.To).ToArray(),
        i.Warps.Values.OrderBy(w => w.WarpId).ToArray(),
        i.Rocks.Values.OrderBy(r => r.RockId).ToArray(),
        i.OwnStations.Values.OrderBy(s => s.StationId).ToArray(),
        i.EnemyStations.Values.OrderBy(s => s.StationId).ToArray(),
        i.OwnMiners.Values.OrderBy(m => m.ShipId).ToArray(),
        i.EnemyMiners.Values.OrderBy(m => m.ShipId).ToArray(),
        i.EnemyShips.Values.OrderBy(h => h.ShipId).ToArray(),
        i.FirstAlephAt, i.FirstEnemyStationAt, i.FirstMinerRaidAt, i.FirstSupportCallAt,
        i.MinerRaids, i.SupportCalls);

    internal void Apply(IntelDigest digest)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue((digest.Cookie, digest.Side), out var intel))
                _map[(digest.Cookie, digest.Side)] = intel = new(digest.Side, digest.Cookie);
            intel.OwnHome = intel.OwnHome >= 0 ? intel.OwnHome : digest.OwnHome;
            intel.EnemyHome = intel.EnemyHome >= 0 ? intel.EnemyHome : digest.EnemyHome;
            if (intel.FirstAlephAt < 0) intel.FirstAlephAt = digest.FirstAlephAt;
            if (intel.FirstEnemyStationAt < 0) intel.FirstEnemyStationAt = digest.FirstEnemyStationAt;
            if (intel.FirstMinerRaidAt < 0) intel.FirstMinerRaidAt = digest.FirstMinerRaidAt;
            if (intel.FirstSupportCallAt < 0) intel.FirstSupportCallAt = digest.FirstSupportCallAt;
            intel.MinerRaids = Math.Max(intel.MinerRaids, digest.MinerRaids);
            intel.SupportCalls = Math.Max(intel.SupportCalls, digest.SupportCalls);
            foreach (var sector in digest.KnownSectors) intel.KnownSectors.Add(sector);
            foreach (var sector in digest.SweptSectors) intel.SweptSectors.Add(sector);
            foreach (var lane in digest.DoneLanes) intel.DoneLanes.Add(lane);
            foreach (var w in digest.Warps) intel.Warps.TryAdd(w.WarpId, w);
            foreach (var r in digest.Rocks) intel.Rocks.TryAdd(r.RockId, r);
            foreach (var s in digest.OwnStations) intel.OwnStations.TryAdd(s.StationId, s);
            foreach (var s in digest.EnemyStations) intel.EnemyStations.TryAdd(s.StationId, s);
            foreach (var m in digest.OwnMiners) intel.OwnMiners.TryAdd(m.ShipId, m);
            foreach (var m in digest.EnemyMiners) intel.EnemyMiners.TryAdd(m.ShipId, m);
            // Hull sightings merge by freshness so a sibling worker's newer
            // observation of the same ship wins; it is still that side's own data.
            foreach (var h in digest.EnemyShips)
                if (!intel.EnemyShips.TryGetValue(h.ShipId, out var old) || h.At > old.At) intel.EnemyShips[h.ShipId] = h;
        }
    }
}

/// <summary>A bounded, JSON-serializable copy of one side's knowledge, carried
/// by the existing worker coordination channel (WorkerSupervisor) so sibling
/// workers on the same side share intel without ever reading another side's
/// data. Every collection is capped by the scan/retention rules.</summary>
internal sealed record IntelDigest(
    uint Cookie, short Side, short OwnHome, short EnemyHome, short[] KnownSectors,
    short[] SweptSectors, LaneKey[] DoneLanes, WarpKnowledge[] Warps, RockSighting[] Rocks,
    StationSighting[] OwnStations, StationSighting[] EnemyStations,
    MinerSighting[] OwnMiners, MinerSighting[] EnemyMiners, HullSighting[] EnemyShips,
    double FirstAlephAt, double FirstEnemyStationAt, double FirstMinerRaidAt, double FirstSupportCallAt,
    int MinerRaids, int SupportCalls);

/// <summary>P798 tunables — the one place the opening's numbers live. Defaults
/// are stated here; the live tests pin them. A few can be overridden by env for
/// a rig experiment, never by map/civ names.</summary>
internal static class ScoutOpening
{
    internal static readonly int MaxExplorersPerSide = ReadInt("ALLEGIANCE_P798_MAX_SCOUTS", 6);
    /// <summary>The opening is a FLEET behaviour: below this many combat bots on a
    /// side the manager leaves the free-flying debug/unit bots alone (single-bot
    /// order and buy/relaunch regressions drive them directly). Every skirmish
    /// size (5/15/30/60/200 per side) and every showcase fleet is above it.</summary>
    internal const int MinFleetForExploration = 4;
    internal static readonly double AssignIntervalSeconds = .5;
    /// <summary>How long a frontier lane may be held before it is offered to
    /// another scout. The sweep pattern itself is NOT restarted on this timeout —
    /// a scout maps a whole ring (the only way to see a sector rim) even if that
    /// takes longer than the lane window.</summary>
    internal static readonly double LaneTimeoutSeconds = 120;
    internal static readonly double SweepDwellSeconds = .5;
    /// <summary>Waypoint fly-through law: a sweep must keep its speed up (a scout
    /// does 160 u/s; a ring at 3600 is 22.6k of travel), so it only brakes inside
    /// this short window and advances once the waypoint is this close.</summary>
    internal const float SweepBrake = 350f;
    internal const float SweepWaypointReach = 700f;
    // P798b — sweep geometry. A scout's scanner reach is ~2400 u (hull 14
    // "Scout" ScannerRange; Igc/shipIGC.h:341-364 InScannerRange), so a ring at
    // radius R covers [R - reach, R + reach]; the centre waypoint covers
    // [0, reach]. With R <= 4800 the two bands overlap, and a ring at 4800 sees
    // out to 7200 — every object in the corpus sectors (max observed 6959).
    internal const float SweepRadiusMin = 2500f;
    internal const float SweepRadiusMax = 4800f;
    /// <summary>Largest object radius assumed in an unswept sector (the corpus
    /// maximum is 6959 in m2xspiderw's Alderan; 6000 keeps the default ring at
    /// 3600, which sees out to 6000/7200 with the centre waypoint).</summary>
    internal const float SweepSpanDefault = 6000f;
    internal const float SweepScanReach = 2400f;
    /// <summary>How long the opening gives the enemy-home recon before it yields
    /// the scout back to ordinary frontier exploration (10 min opening budget).</summary>
    internal static readonly double ReconDeadlineSeconds = 480;
    internal static readonly int SweepWaypoints = 10;
    internal static readonly double ClashDelaySeconds = ReadDouble("ALLEGIANCE_P798_CLASH_DELAY", 75);
    internal static readonly double ClashFallbackSeconds = 420;
    internal static readonly double OpeningSeconds = 600;
    /// <summary>P830 — lane-switch hysteresis: a scout holds its current lane
    /// while the lane stays valid; it only releases it when a free lane scores
    /// this many points higher (a held 955 lane is kept against another 955 or
    /// a 1000, given up for a 1500 "known sector not yet swept"). Keeps the
    /// assignment stable across passes without freezing out genuinely better
    /// frontiers.</summary>
    internal static readonly double LaneSwitchScoreMargin = ReadDouble("ALLEGIANCE_P798_LANE_SWITCH_MARGIN", 250);
    internal static readonly double IntelLineSeconds = 60;
    internal static readonly double ScanIntervalSeconds = 1;
    internal static readonly double SupportWindowSeconds = 20;
    internal static readonly double RaidLingerSeconds = 90;
    internal static readonly double SightRetentionSeconds = 300;
    internal static readonly int MaxHullSightings = 256;
    // Clash-scoring weights (P798 design; documented in the report).
    internal static readonly double OwnMinerWeight = .4;
    internal static readonly double EnemyMinerWeight = .8;
    internal static readonly double EnemyStationWeight = 1.2;
    internal static readonly double HopPenalty = .12;

    private static int ReadInt(string name, int def) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : def;
    private static double ReadDouble(string name, double def) =>
        double.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : def;
}
