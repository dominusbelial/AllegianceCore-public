using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Collision;

namespace Allegiance.Tools.McpSpawner;

/// <summary>
/// P798 — the scout opening: exploration, per-side intel, miner raids, support
/// calls and the resource-driven clash location.
///
/// <para><b>Sources only what the side knows.</b> Every scan below reads one
/// bot's own <see cref="ClientWorld"/> (which P797 already filtered to that
/// side's SeenBySide set, FedSrv/fsmission.cpp:4169-4250) and merges it into the
/// side's <see cref="SideIntelStore"/>. Nothing here reads the server's mission
/// or another side's world.</para>
///
/// <para><b>Why scouts first.</b> Native makes the topology and home-sector
/// flags global (fsmission.cpp:4122-4174) but the aleph positions, rocks and
/// stations per-side (P797). Native's own route finder refuses to path over an
/// unseen aleph (Igc/common.cpp:894-960, CanSee at :916), so a side must fly its
/// own scouts through what it can see before it can route anywhere. That is the
/// opening this file drives: scouts sweep frontier sectors, reveal the next
/// alephs (seeing one reveals its partner, FedSrv.CPP:10786-10800), find enemy
/// stations and miners, and only then does the fleet commit to the resource
/// area the side has actually seen.</para>
///
/// <para><b>Design (not native).</b> Native has no scout/exploration AI; its
/// miners run from attackers on their own (Igc/shipIGC.cpp:1780-1930). The
/// frontier/lane planner, the intel store, the miner support-call extension and
/// the resource clash score are P798 policy, with their tunables in
/// <see cref="ScoutOpening"/> and every native hook cited where it applies.</para>
/// </summary>
internal sealed partial class BotManager
{
    // Lazy so a BotManager created via RuntimeHelpers.GetUninitializedObject in
    // the bot internals regression tests (no field initializers run) does not NRE
    // on the support-call path: the same defensive shape _defenseIncidents uses.
    private SideIntelStore? _intelStore;
    private SideIntelStore _intel => _intelStore ??= new();
    private readonly Dictionary<(uint Cookie, short Side), ClashChoice> _clash = [];
    private readonly HashSet<(uint Cookie, short Side)> _clashLogged = [];
    private readonly HashSet<(uint Cookie, short Side)> _exploreDoneLogged = [];
    private readonly Dictionary<string, double> _laneStarted = [];
    private double _openingStartedAt = -1;
    private uint _openingCookie = uint.MaxValue;
    private double _nextIntelScan = double.NegativeInfinity;
    private double _nextExplorerAssign = double.NegativeInfinity;
    private double _nextClashCheck = double.NegativeInfinity;
    private double _nextIntelLine = double.NegativeInfinity;
    private double _nextScoutTelemetry = double.NegativeInfinity;
    // Explicit spawn_scenario target_sector: the showcase override. Null means
    // the clash sector is computed from this side's intel (the default).
    private short? _scenarioTargetOverride;
    /// <summary>P837 — the skirmish's small-fleet meeting engagement (set by
    /// <c>spawn_scenario</c>'s <c>meeting_engagement</c>): below
    /// <see cref="ScoutOpening.MeetingEngagementCombatBots"/> combat bots the side
    /// fights what it meets while it travels, skips the P539d staging barrier, and
    /// commits to its clash as soon as its scaled opening window allows. Off by
    /// default, so no other scenario changes behaviour.</summary>
    private bool _meetingEngagement;
    internal bool MeetingEngagementEnabled => _meetingEngagement;
    internal void ConfigureMeetingEngagement(bool on) => _meetingEngagement = on;

    /// <summary>P798 — the showcase/skirmish `target_sector` override. When set,
    /// no computed clash is applied (the spawn already gave every bot this target).</summary>
    internal void ConfigureScenarioTarget(short? sector)
    {
        _scenarioTargetOverride = sector;
    }

    private static uint CookieOf(ConnectionSession cs) => cs.World.Mission?.Cookie ?? 0;

    private (uint Cookie, short Side) SideKey(ActiveBot bot) => (CookieOf(bot.Cs!), bot.Side);

    /// <summary>Where the side's own home is, for the topology distance term:
    /// an observed own restart-capable station first, then the CLUSTERINFO home
    /// flags (fsmission.cpp:4122-4174). -1 when unknown.</summary>
    private static short ResolveOwnHome(SideIntel intel, ClusterGraph? graph, short side)
    {
        if (intel.OwnHome >= 0) return intel.OwnHome;
        if (graph is null) return -1;
        var homes = graph.Clusters.Where(c => c.HomeSector).Select(c => c.Id).ToArray();
        if (homes.Length == 0) return -1;
        // No own base observed yet: the side's own ships start in its home
        // sector, and every participant's own sector is marked known by the scan
        // (P797: an own model is always visible to its side, FedSrv.CPP:10907-
        // 10912), so exactly one flagged home normally matches the known set.
        var knownHomes = homes.Where(intel.KnownSectors.Contains).Distinct().ToArray();
        if (knownHomes.Length == 1) return knownHomes[0];
        // Deterministic last resort until an own station lands.
        _ = side;
        return homes.Min();
    }

    /// <summary>The enemy home sector from the CLUSTERINFO home flags
    /// (fsmission.cpp:4122-4174) — a side is TOLD which sectors are homes at
    /// mission start (the map preview), it just does not know what is inside.
    /// The first enemy station actually observed is the strongest anchor;
    /// otherwise the single home flag that is not our own. -1 when ambiguous.</summary>
    private static short ResolveEnemyHome(SideIntel intel, ClusterGraph? graph, short ownHome)
    {
        if (intel.EnemyHome >= 0) return intel.EnemyHome;
        if (graph is null) return -1;
        var homes = graph.Clusters.Where(c => c.HomeSector && c.Id != ownHome)
            .Select(c => c.Id).Order().Distinct().ToArray();
        return homes.Length == 1 ? homes[0] : (short)-1;
    }

    /// <summary>One scan per second per (side, mission): read the side's own
    /// worlds and merge everything seen into its intel store. Bots on one side
    /// share their spatial world inside a worker, so the lowest-id seated bot of
    /// that side is enough and the pass stays O(objects), not O(bots×objects).</summary>
    private void ScanSideIntel(ActiveBot[] bots, double now)
    {
        // Bots on one side share their spatial world inside a worker
        // (ClientWorld.BotSharing), so scan one world per shared table rather
        // than every bot. A bot whose world was detached (side mismatch) has
        // its own table and is scanned too — otherwise a sighting made only by
        // that bot would never reach the side's intel.
        // Each bot's OWN ship is the only reliably sector-known entry in its
        // private overlay (teammates' overlays are not in the shared table), so
        // mark every participant's own sector first. This is what makes the
        // side's knowledge include sectors its scouts actually entered.
        foreach (var bot in bots)
        {
            if (!IsParticipating(bot) || bot.SpawnPending || bot.InScenario || bot.Cs is not { } ownCs) continue;
            short ownSector = -1;
            lock (ownCs.Gate)
                if (ownCs.World.MyShipId is { } me && ownCs.World.Ships.TryGetValue(me, out var mine)
                    && mine.StatusSectorKnown) ownSector = mine.SectorId;
            if (ownSector >= 0)
            {
                var ownIntel = _intel.Get(CookieOf(ownCs), bot.Side);
                lock (_intel.Sync) ownIntel.MarkSector(ownSector, now);
            }
        }

        var scannedWorlds = new Dictionary<(uint, short), HashSet<object>>();
        foreach (var bot in bots.OrderBy(b => b.ShipId))
        {
            if (!IsParticipating(bot) || bot.SpawnPending || bot.InScenario || bot.Cs is not { } cs) continue;
            var key = SideKey(bot);
            if (!scannedWorlds.TryGetValue(key, out var worlds)) scannedWorlds[key] = worlds = [];
            bool already;
            lock (cs.Gate) already = !worlds.Add(cs.World.Stations);
            if (already) continue;
            var cookie = key.Cookie;
            var side = bot.Side;
            var core = Catalog.RepresentativeCore();
            List<(short Id, short Hull, short Sector)>? friendly = null, enemies = null;
            List<WarpKnowledge>? warps = null;
            List<RockSighting>? rocks = null;
            List<StationSighting>? stations = null;
            bool ready;
            lock (cs.Gate)
            {
                ready = cs.World.WorldReady;
                if (ready)
                {
                    friendly = [];
                    enemies = [];
                    warps = [];
                    rocks = [];
                    stations = [];
                    foreach (var ship in cs.World.Ships.Values)
                    {
                        if (ship.IsNotVisible || ship.IsGone || ship.IsDead || !ship.StatusSectorKnown) continue;
                        if (ship.SideId == side) friendly.Add((ship.ShipId, ship.HullId, ship.SectorId));
                        else if (ship.SideId >= 0 && ship.Latest is not null) enemies.Add((ship.ShipId, ship.HullId, ship.SectorId));
                    }
                    foreach (var w in cs.World.Warps.Values)
                        warps.Add(new WarpKnowledge(w.WarpId, w.ClusterId,
                            cs.World.Warps.TryGetValue(w.DestinationId, out var partner) ? partner.ClusterId : (short)-1));
                    foreach (var a in cs.World.Asteroids.Values)
                        rocks.Add(new RockSighting(a.AsteroidId, a.ClusterId, a.Ore, a.OreMax, a.AabmCapabilities));
                    foreach (var st in cs.World.Stations.Values)
                        stations.Add(new StationSighting(st.StationId, st.SideId, st.StationTypeId, st.ClusterId));
                }
            }
            if (!ready) continue;
            lock (_intel.Sync)
            {
            var intel = _intel.Get(cookie, side);
            if (friendly is not null)
                foreach (var (id, hull, sector) in friendly)
                {
                    intel.MarkSector(sector, now);
                    if (core.HullTypes.TryGetValue(hull, out var h) && h.HasCapability(DataHullTypeIGC.HabmMiner))
                        intel.ObserveMiner(id, side, sector, now);
                }
            if (enemies is not null)
                foreach (var (id, hull, sector) in enemies)
                {
                    var role = CombatTargetSelector.ClassifyTargetId(hull);
                    bool miner = core.HullTypes.TryGetValue(hull, out var h) && h.HasCapability(DataHullTypeIGC.HabmMiner);
                    string? loadout = null;
                    lock (cs.Gate)
                        if (cs.World.Ships.TryGetValue(id, out var ship) && ship.Loadout is { Count: > 0 } parts)
                            loadout = string.Join(",", parts.Select(p => Catalog.PartName(p.PartId) ?? p.PartId.ToString()));
                    intel.ObserveEnemyShip(id, side, hull, sector, now, role.ToString(), loadout);
                    if (miner) intel.ObserveMiner(id, side, sector, now);
                }
            if (warps is not null)
                foreach (var w in warps) intel.ObserveWarp(w, now);
            if (rocks is not null)
                foreach (var r in rocks) intel.ObserveRock(r, now);
            if (stations is not null)
            {
                foreach (var s in stations) intel.ObserveStation(s, now);
                // Own home: prefer a restart-capable own station (the strongest anchor).
                var restart = stations.Where(s => s.SideId == side && core.StationTypes.TryGetValue(s.StationTypeId, out var t)
                    && t.HasCapability(DataStationTypeIGC.SabmRestart)).Select(s => s.Sector).ToArray();
                if (intel.OwnHome < 0 && restart.Length > 0) intel.OwnHome = restart.Min();
            }
            if (intel.OwnHome >= 0 && intel.EnemyHome < 0)
            {
                lock (cs.Gate)
                    if (cs.World.Graph is { } graph)
                    {
                        var homes = graph.Clusters.Where(c => c.HomeSector && c.Id != intel.OwnHome).Select(c => c.Id).ToArray();
                        if (homes.Length == 1) intel.EnemyHome = homes[0];
                    }
            }
            intel.Prune(now);
            }
        }
        LogOpeningNews(now);
    }

    /// <summary>First-time opening-timeline lines (first aleph, first enemy
    /// station, first miner raid, first support call) — one line per side.</summary>
    private sealed record OpeningNews(uint Cookie, short Side, int Alephs, double FirstAlephAt,
        double FirstEnemyStationAt, double FirstMinerRaidAt, double FirstSupportCallAt,
        int MinerRaids, int SupportCalls, WarpKnowledge FirstWarp, StationSighting FirstStation);

    private void LogOpeningNews(double now)
    {
        var snapshot = _intel.ReadAll(list => list.Select(i => new OpeningNews(i.Cookie, i.Side,
            i.Warps.Count, i.FirstAlephAt, i.FirstEnemyStationAt, i.FirstMinerRaidAt, i.FirstSupportCallAt,
            i.MinerRaids, i.SupportCalls,
            i.Warps.Values.OrderBy(w => w.WarpId).FirstOrDefault(),
            i.EnemyStations.Values.OrderBy(s => s.StationId).FirstOrDefault())).ToArray());
        foreach (var intel in snapshot)
        {
            string tag = $"[opening] t={now:F0}s side {intel.Side}";
            if (intel.FirstAlephAt >= 0 && intel.Alephs > 0 && _openingAlephed.Add((intel.Cookie, intel.Side)))
                Console.WriteLine($"{tag} first aleph {intel.FirstWarp.WarpId} in sector {intel.FirstWarp.Sector} "
                    + $"(partner sector {intel.FirstWarp.PartnerSector}) at {intel.FirstAlephAt:F0}s");
            if (intel.FirstEnemyStationAt >= 0 && _openingStationed.Add((intel.Cookie, intel.Side)))
                Console.WriteLine($"{tag} first enemy station {intel.FirstStation.StationId} "
                    + $"(type {intel.FirstStation.StationTypeId}) spotted in sector {intel.FirstStation.Sector} at {intel.FirstEnemyStationAt:F0}s");
            if (intel.FirstMinerRaidAt >= 0 && _openingRaided.Add((intel.Cookie, intel.Side)))
                Console.WriteLine($"{tag} first miner raid at {intel.FirstMinerRaidAt:F0}s (raids={intel.MinerRaids})");
            if (intel.FirstSupportCallAt >= 0 && _openingSupported.Add((intel.Cookie, intel.Side)))
                Console.WriteLine($"{tag} first support call at {intel.FirstSupportCallAt:F0}s (calls={intel.SupportCalls})");
        }
    }
    private readonly HashSet<(uint, short)> _openingAlephed = [];
    private readonly HashSet<(uint, short)> _openingStationed = [];
    private readonly HashSet<(uint, short)> _openingRaided = [];
    private readonly HashSet<(uint, short)> _openingSupported = [];
    private readonly HashSet<(uint, short, short)> _reconLogged = [];
    private readonly HashSet<string> _reconProgress = [];
    private readonly Dictionary<(uint, short), string> _explorerRoster = [];

    /// <summary>P798 — set the per-side explorer roster (dedicated scouts first,
    /// otherwise a small temporary detachment) and hand each explorer a distinct
    /// frontier lane. Runs on the decision thread; rate limited.</summary>
    private void AssignExplorers(ActiveBot[] bots, double now)
    {
        foreach (var group in bots.Where(b => IsParticipating(b) && !b.SpawnPending && !b.InScenario && b.Cs is not null)
                     .GroupBy(b => (CookieOf(b.Cs!), b.Side)))
        {
            var cookie = group.Key.Item1;
            var side = group.Key.Item2;
            // A lone debug/order bot is not a fleet: leave it to its own drive —
            // unless a finale is waiting for this side to find the enemy base
            // (a small skirmish has no scouts; without recon its finale never
            // registers, rig/live test P682b 2026-10-24).
            // P837 — a ROTATED finale waiting for its re-arm needs the same
            // sighting: the fresh world leaves a small side with no objective at
            // all, so without this it never flies, never observes the enemy
            // station and the re-arm sits out P831's whole grace (live test P682b
            // timed out at 240 s while its bombers idled in mid-space).
            if (group.Count(b => !b.IsPod && !b.PodDocked && b.Behavior == "combat") < ScoutOpening.MinFleetForExploration
                && !FinalePendingFor(group) && !FinaleAwaitingRearm(group))
                continue;
            var cs = group.First().Cs!;
            ClusterGraph? graph;
            lock (cs.Gate) graph = cs.World.Graph;

            // Explorer roster: dedicated scouts (role "scout"), else temporary
            // explorers from the lightest hulls so any fleet can scout.
            // A bot under an explicit commander order keeps following it: the
            // opening must never hijack a human/commander-issued goto/attack order
            // (the order-following live tests spawn one bot per side, which would
            // otherwise become an explorer and stop obeying).
            var eligible = group.Where(b => !b.IsPod && !b.PodDocked && b.Behavior == "combat"
                    && b.OrderedCommand is null
                    && !b.Role.Equals("bomber", StringComparison.OrdinalIgnoreCase)
                    && !b.Role.Equals("support", StringComparison.OrdinalIgnoreCase)
                    && CombatTargetSelector.ClassifyTargetId(ActualHullId(b)) != BotTargetClass.Bomber
                    && CombatTargetSelector.ClassifyTargetId(ActualHullId(b)) != BotTargetClass.MinerDrone)
                .OrderBy(b => b.BotId).ToList();
            // A pending finale needs this side to find the enemy base: if the side
            // has nothing lighter, its bombers may recon (they are headed there).
            // P837 — a rotated finale awaiting its re-arm needs the same sighting.
            if (eligible.Count == 0 && (FinalePendingFor(group) || FinaleAwaitingRearm(group)))
                eligible = group.Where(b => !b.IsPod && !b.PodDocked && b.Behavior == "combat"
                        && b.OrderedCommand is null
                        && !b.Role.Equals("support", StringComparison.OrdinalIgnoreCase)
                        && CombatTargetSelector.ClassifyTargetId(ActualHullId(b)) != BotTargetClass.MinerDrone)
                    .OrderBy(b => b.BotId).ToList();
            var dedicated = eligible.Where(b => b.Role.Equals("scout", StringComparison.OrdinalIgnoreCase)).ToList();
            // A 3-sector training map needs 2 scouts; a 13-sector map needs more
            // than a 10-bot showcase's two scouts. Fill the opening detachment
            // from the lightest eligible hulls up to a third of the side (capped
            // by ScoutOpening.MaxExplorersPerSide).
            // P837 — the floor scales too: a side of three eligible hulls keeps ONE
            // scout (a 9-bot skirmish's warps are revealed, so the fleet discovers
            // on its way) and puts the other two into the clash; the rig's big
            // sides keep the two-scout floor that P830 measured.
            int want = Math.Clamp(eligible.Count / 3,
                MeetingEngagementEnabled && eligible.Count < 6 ? 1 : 2, ScoutOpening.MaxExplorersPerSide);
            var scouts = dedicated.Take(want).ToList();
            foreach (var b in eligible)
                if (scouts.Count >= want) break;
                else if (!scouts.Contains(b)) scouts.Add(b);
            if (scouts.Count == 0) scouts = eligible.Take(Math.Min(2, eligible.Count)).ToList();
            if (scouts.Count > ScoutOpening.MaxExplorersPerSide)
                scouts = scouts.Take(ScoutOpening.MaxExplorersPerSide).ToList();
            var posts = scouts.Select(b => new ScoutPost(b.BotId, CsSectorOf(b), ScannerRangeOf(b))).ToArray();

            lock (_intel.Sync)
            {
                var intel = _intel.Get(cookie, side);
                short ownHome = ResolveOwnHome(intel, graph, side);
                var reachable = ExplorationPlanner.Reachable(graph, ownHome >= 0 ? ownHome : (short)0);

                // Expire a lane a scout has held too long: it stays swept (so no
                // one else repeats it) and the scout takes the next frontier.
                foreach (var bot in scouts)
                {
                    if (bot.ExploreLane is not { } lane) continue;
                    if (!_laneStarted.TryGetValue(bot.Name, out double started)) { _laneStarted[bot.Name] = now; continue; }
                    if (now - started < ScoutOpening.LaneTimeoutSeconds) continue;
                    intel.LaneRetryAt[lane.Key] = now + ScoutOpening.LaneTimeoutSeconds;
                    Console.WriteLine($"[opening] scout {bot.Name} side {side} timed out on lane {lane.From}->{lane.To} after {now - started:F0}s — lane left for another scout");
                    bot.ExploreLane = null;
                    _laneStarted.Remove(bot.Name);
                }

                var lanes = ExplorationPlanner.FindFrontiers(graph, intel.KnownSectors, intel.Warps, ownHome,
                        intel.DoneLanes, intel.SweptSectors)
                    .Where(l => !intel.LaneRetryAt.TryGetValue(l.Key, out double retryAt) || retryAt <= now).ToList();

                // P798b — enemy-base recon. A side only learns where the enemy
                // base is by seeing it (P797: static objects are exported per
                // side only after SeenBySide, fsmission.cpp:4169-4250), and
                // generic frontier exploration is breadth-first, so the enemy
                // HOME (known from the CLUSTERINFO flag) may be the last sector
                // reached. Until a side has observed an enemy station, one
                // scout is sent along the topology path to the enemy home: the
                // topology is global knowledge, only the aleph POSITIONS inside
                // a sector are not, so it sweeps each sector on the way until
                // the next mouth is seen. Everything it reports (rocks, the
                // station, miners) is still only what its own scanners saw.
                short enemyHome = ResolveEnemyHome(intel, graph, ownHome);
                double elapsed = now - (_openingStartedAt < 0 ? 0 : _openingStartedAt);
                bool reconNeeded = enemyHome >= 0 && enemyHome != ownHome
                    && intel.FirstEnemyStationAt < 0 && elapsed <= ScoutOpening.ReconDeadlineSeconds;
                // Sticky: keep the scout that is already on the recon (that is what
                // keeps it flying its route instead of handing the mission back and
                // forth to whichever scout happens to be one hop closer this pass).
                int? reconId = null;
                if (reconNeeded)
                {
                    var already = group.FirstOrDefault(b => b.ReconTarget is not null && b.ReconTarget == enemyHome);
                    reconId = already is not null && posts.Any(p => p.Id == already.BotId)
                        ? already.BotId
                        : ExplorationPlanner.PickReconScout(posts,
                            (from, to) => graph is null ? -1 : ExplorationPlanner.Hops(graph, from, to), enemyHome);
                }
                // P798 — a sector counts as explored only once a scout has SWEPT
                // it (or it is home): merely seeing one rock/station would stop
                // the scouts before they find the enemy miners. No frontiers left
                // means the known map is covered (combat transit may have seen
                // sectors the scouts never had to sweep).
                bool allKnown = reachable.Count > 0 && reachable.All(intel.KnownSectors.Contains);
                bool complete = graph is not null && allKnown
                    && reachable.All(s2 => intel.SweptSectors.Contains(s2) || s2 == ownHome);
                if (complete && _exploreDoneLogged.Add((cookie, side)))
                    Console.WriteLine($"[opening] side {side} exploration complete: {intel.KnownSectors.Count}/{reachable.Count} reachable sectors seen, "
                        + $"{intel.Warps.Count} alephs, {intel.Rocks.Count} rocks, {intel.EnemyStations.Count} enemy station(s)");

                var chosen = scouts.Select(b => b.Name).ToHashSet();
                string roster = string.Join(",", scouts.Select(b => b.Name).Order()) + "|recon=" +
                    (reconId is { } rid2 ? scouts.FirstOrDefault(b => b.BotId == rid2)?.Name ?? "?" : "none");
                if (_explorerRoster.TryGetValue((cookie, side), out var previousRoster) ? previousRoster != roster : true)
                {
                    _explorerRoster[(cookie, side)] = roster;
                    Console.WriteLine($"[opening] t={now:F0}s side {side} explorers=[{roster}] "
                        + $"(complete={complete}, lanes={lanes.Count}, enemyStationSeen={intel.FirstEnemyStationAt >= 0})");
                }
                foreach (var bot in group)
                {
                    bool was = bot.IsExplorer;
                    // A scout mid-raid stays an explorer for a short linger after
                    // the map is complete, so its miner kill (and the victim
                    // side's support call) is not cut off at the last sweep. A
                    // recon scout stays an explorer until the enemy base is
                    // seen, even if every other sector is already swept.
                    bool raiding = now - bot.LastMinerRaidAt < ScoutOpening.RaidLingerSeconds;
                    bool recon = reconId is { } rid && bot.BotId == rid;
                    bot.ReconTarget = recon ? enemyHome : null;
                    // P830 — the opening window is a FLOOR, not a cutoff: as
                    // long as the map is not complete the scouts keep exploring
                    // after ScoutOpening.OpeningSeconds. Before P830 the window
                    // expired with sectors unswept, IsExplorer flipped false for
                    // every scout and the assignment pass cleared + re-assigned
                    // the SAME lane every 0.5 s (the ~40 % log churn on the rig)
                    // while nobody actually swept — fog never resolved and the
                    // match ran on for hours. A scout keeps its lane until the
                    // sweep completes or the lane becomes invalid (sector
                    // known/swept, scout dead/pod/rearming, recon duty) or a
                    // strictly better assignment beats it by the switch margin
                    // (ExplorationPlanner.Assign hysteresis).
                    bot.IsExplorer = (chosen.Contains(bot.Name) || recon)
                        && (!complete || raiding || recon)
                        && (OpeningActive(now) || !complete)
                        // NT-2 — a committed finale escort (P778a) is owned by the
                        // strike: the escort drive (TryDriveFinaleEscort) runs after
                        // this in the decision order, so an explorer flag on a
                        // FinaleEscort bot starves the push (the staged P778a live
                        // gate lost its pusher to the lane sweeps after P798/P830).
                        && !bot.FinaleEscort;
                    if (recon)
                    {
                        if (bot.ExploreLane is not null) _laneStarted.Remove(bot.Name);
                        bot.ExploreLane = null;
                        bot.StagingSector = -1;          // the march must not carry the recon
                        bot.TargetSector = null;
                        bot.UseStaging = false;
                    }
                    if (!bot.IsExplorer)
                    {
                        bot.ExploreLane = null;
                        if (was) _laneStarted.Remove(bot.Name);
                    }
                }
                if (reconId is { } reconBotId)
                {
                    var reconBot = scouts.FirstOrDefault(b => b.BotId == reconBotId);
                    if (reconBot is not null && _reconLogged.Add((cookie, side, enemyHome)))
                        Console.WriteLine($"[opening] t={now:F0}s side {side} recon: scout {reconBot.Name} sent to the enemy home sector {enemyHome} "
                            + $"({ExplorationPlanner.Hops(graph!, CsSectorOf(reconBot), enemyHome)} hops; no enemy station seen yet)");
                }
                if (complete || lanes.Count == 0) continue;

                // P830 — lanes are only handed to scouts that ARE explorers this
                // pass. The previous code assigned to the whole roster even when
                // IsExplorer had just cleared their lane, which is what made the
                // 'assigned lane' line fire every pass after the opening window.
                var laneScouts = (reconId is { } skip
                    ? scouts.Where(b => b.BotId != skip) : scouts)
                    .Where(b => b.IsExplorer).ToList();
                if (laneScouts.Count == 0) continue;
                var lanePosts = laneScouts.Select(b => new ScoutPost(b.BotId, CsSectorOf(b), ScannerRangeOf(b))).ToArray();
                var current = laneScouts.Where(b => b.ExploreLane is not null)
                    .ToDictionary(b => b.BotId, b => b.ExploreLane!.Value);
                var assigned = ExplorationPlanner.Assign(lanes, lanePosts,
                    (from, to) => graph is null ? -1 : ExplorationPlanner.Hops(graph, from, to), current);
                foreach (var bot in laneScouts)
                {
                    bool had = bot.ExploreLane is not null;
                    var previous = bot.ExploreLane;
                    if (!assigned.TryGetValue(bot.BotId, out var lane))
                    {
                        bot.ExploreLane = null;
                        // P830 — fire once per real invalidation (the lane left
                        // the pool: swept, timed out or its destination became
                        // known), never per pass.
                        if (had)
                            Console.WriteLine($"[opening] scout {bot.Name} side {side} lane " +
                                $"{previous!.Value.From}->{previous!.Value.To} no longer valid — taking the next frontier");
                        continue;
                    }
                    bot.ExploreLane = lane;
                    if (!had || previous!.Value.Key != lane.Key)
                    {
                        _laneStarted[bot.Name] = now;
                        Console.WriteLine($"[opening] scout {bot.Name} side {side} assigned lane {lane.From}->{lane.To} ({lane.Why}) score={lane.Score:F0}");
                    }
                }
            }
        }
    }

    private static short CsSectorOf(ActiveBot bot)
    {
        if (bot.Cs is not { } cs) return -1;
        return CsSector(cs);
    }

    /// <summary>The hull scanner reach this bot contributes to a sweep
    /// (<c>c_gaScanRange</c> at 1.0; Igc/shipIGC.h:341-364). 0 when unknown, so
    /// the recon pick falls back to the fleet's widest-scanner scout.</summary>
    private static float ScannerRangeOf(ActiveBot bot) =>
        Catalog.RepresentativeCore().HullTypes.TryGetValue(ActualHullId(bot), out var hull) ? hull.ScannerRange : 0f;

    /// <summary>P798 — whether the probe already knows the whole aleph graph, so
    /// spawn-time multi-route enumeration is meaningful. Under native fog it is
    /// not (only the home alephs are known), and enumerating a partial graph can
    /// hand the fleet a route whose alephs it has never seen; in that case the
    /// bots route per tick over the topology and sweep a stalled choke instead.</summary>
    internal bool RoutesReliable
    {
        get
        {
            lock (Probe.Gate)
                return Probe.World.Graph is { } g && Probe.World.Warps.Count >= g.Warps.Count && g.Warps.Count > 0;
        }
    }

    internal bool OpeningActive(double now) => now - (_openingStartedAt < 0 ? 0 : _openingStartedAt) < ScoutOpening.OpeningSeconds;

    /// <summary>P798 — the main clash objective, per side, from the intel the
    /// side has. The spawn_scenario `target_sector` is the only override. Once
    /// chosen, every bot without its own objective gets it (bombers keep the
    /// enemy home when the side has resolved one).</summary>
    private void AssignClashTargets(ActiveBot[] bots, double now)
    {
        if (_scenarioTargetOverride is not null) return;
        foreach (var group in bots.Where(b => IsParticipating(b) && !b.SpawnPending && !b.InScenario && b.Cs is not null)
                     .GroupBy(b => (CookieOf(b.Cs!), b.Side)))
        {
            var cookie = group.Key.Item1;
            var side = group.Key.Item2;
            // A lone debug/order bot is not a fleet: leave it to its own drive —
            // unless a finale is waiting for this side to find the enemy base
            // (a small skirmish has no scouts; without recon its finale never
            // registers, rig/live test P682b 2026-10-24).
            int combatBots = group.Count(b => !b.IsPod && !b.PodDocked && b.Behavior == "combat");
            // P837 — scale the opening down: a small fleet has no wing to keep
            // assembled and no minutes to spend gathering, so it engages what it
            // meets in transit (see VisibleEnemyInSector) and does not stage. The
            // rig's big fleets keep the P539d staging/transit discipline.
            bool meetingEngagement = MeetingEngagementEnabled
                && combatBots < ScoutOpening.MeetingEngagementCombatBots;
            foreach (var b in group) b.MeetingEngagement = meetingEngagement;
            if (combatBots < ScoutOpening.MinFleetForExploration
                && !FinalePendingFor(group) && !FinaleAwaitingRearm(group))
                continue;
            var cs = group.First().Cs!;
            ClusterGraph? graph;
            lock (cs.Gate) graph = cs.World.Graph;
            ClashChoice? choice;
            lock (_intel.Sync)
            {
                var intel = _intel.Get(cookie, side);
                short ownHome = ResolveOwnHome(intel, graph, side);
                double elapsed = now - (_openingStartedAt < 0 ? 0 : _openingStartedAt);
                bool have = _clash.TryGetValue((cookie, side), out var chosen);
                // P837 — the commit delay scales with the fleet (see
                // ScoutOpening.ClashDelaySecondsFor): a small side cannot wait out
                // the rig's opening window and still fight inside its battle.
                double clashDelay = meetingEngagement
                    ? ScoutOpening.ClashDelaySecondsFor(combatBots) : ScoutOpening.ClashDelaySeconds;
                bool upgrade = !have || (chosen.Resource <= 0 && elapsed >= clashDelay);
                if (upgrade)
                {
                    if (elapsed < clashDelay) continue;
                    var pick = ClashScoring.Choose(graph, ownHome, intel.EnemyHome, intel.KnownSectors,
                        intel.Rocks.Values, intel.OwnMiners.Values, intel.EnemyMiners.Values, intel.EnemyStations.Values,
                        // P837 — the midpoint fallback is scaled to the side's fleet
                        // for the skirmish's meeting engagement: the rig's ~48 combat
                        // bots keep the 420 s figure, a 9-bot skirmish commits as soon
                        // as its scaled opening window allows.
                        topologyFallback: elapsed >= (meetingEngagement
                            ? ScoutOpening.ClashFallbackSecondsFor(combatBots) : ScoutOpening.ClashFallbackSeconds));
                    if (pick is null) continue;
                    // A provisional topology fallback (no resource seen yet) is
                    // replaced the moment the side actually sees a resource area.
                    if (have && chosen.Resource > 0 && pick.Value.Resource <= 0) pick = chosen;
                    if (!have || pick.Value.Sector != chosen.Sector || pick.Value.Resource > chosen.Resource)
                    {
                        chosen = pick.Value;
                        _clash[(cookie, side)] = chosen;
                    }
                    have = true;
                }
                if (!have) continue;
                choice = chosen;
                if (_clashLogged.Add((cookie, side)))
                    Console.WriteLine($"[clash] side {side} chose sector {chosen.Sector} (score={chosen.Score:F2}, {chosen.Why}; "
                        + $"known={intel.KnownSectors.Count} rocks={intel.Rocks.Count} enemyStations={intel.EnemyStations.Count})");
                foreach (var bot in group)
                {
                    if (bot.IsPod || bot.PodDocked) continue;
                    if (bot.ReconTarget is not null) continue;   // scouts keep scouting, not marching
                    if (bot.Role.Equals("bomber", StringComparison.OrdinalIgnoreCase) && !ConquestGame(bot.Cs!) && !KillsGame(bot.Cs!))
                    {
                        // The home sector is known from the CLUSTERINFO flags even
                        // before its station has been seen; the strike itself still
                        // only targets stations this side has observed
                        // (ResearchTactics reads seen stations).
                        if (ResolveEnemyHome(intel, graph, ownHome) is var home && home >= 0) bot.TargetSector = home;
                        else bot.TargetSector ??= chosen.Sector;
                    }
                    else
                    {
                        // Follow the computed clash; only overwrite a target this
                        // manager assigned (never a finale/research objective).
                        bool mine = bot.TargetSector is null
                            || (bot.AssignedClashSector is { } prev && bot.TargetSector == prev);
                        if (mine) { bot.TargetSector = chosen.Sector; bot.AssignedClashSector = chosen.Sector; }
                    }
                    if (bot.TargetSector is not null) bot.UseStaging = !meetingEngagement;
                }
            }
        }
    }

    /// <summary>P798 — per-minute `[intel]` line: what each side knows, where the
    /// clash is forming, and how the opening is going. Bounded, one line per side
    /// per minute (never per tick).</summary>
    private void PublishIntelLines(double now)
    {
            var snapshots = _intel.ReadAll(list => list.Select(i => new
            {
                i.Cookie, i.Side, i.OwnHome, i.EnemyHome, i.KnownSectors, i.SweptSectors, i.Warps, i.Rocks, i.OwnMiners, i.EnemyMiners, i.EnemyStations,
                i.SupportCalls, i.MinerRaids,
            }).ToArray());
        foreach (var snap in snapshots)
        {
            ClusterGraph? graph = null;
            var bot = _bots.FirstOrDefault(b => b.Cs is not null && b.Side == snap.Side && CookieOf(b.Cs) == snap.Cookie);
            if (bot?.Cs is { } cs) lock (cs.Gate) graph = cs.World.Graph;
            short ownHome = ResolveOwnHome(_intel.Get(snap.Cookie, snap.Side), graph, snap.Side);
            short enemyHome = ResolveEnemyHome(_intel.Get(snap.Cookie, snap.Side), graph, ownHome);
            var reachable = ExplorationPlanner.Reachable(graph, ownHome >= 0 ? ownHome : (short)0);
            int seen = reachable.Count(snap.KnownSectors.Contains);
            var miners = snap.EnemyMiners.Values.GroupBy(m => m.Sector)
                .OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}").ToArray();
            var stations = snap.EnemyStations.Values
                .Select(s => $"{s.StationId}@{s.Sector}").OrderBy(s => s).ToArray();
            // Per-sector rock counts: the audit trail for "were these rocks really
            // seen by this side?" (they can only come from this side's own world).
            var rocks = snap.Rocks.Values.GroupBy(r => r.Sector).OrderBy(g => g.Key)
                .Select(g => $"{g.Key}:{g.Count()}").ToArray();
            string clash = _clash.TryGetValue((snap.Cookie, snap.Side), out var c) ? $"{c.Sector} (score {c.Score:F2})" : "pending";
            Console.WriteLine($"[intel] t={now:F0}s side {snap.Side} home={ownHome}/enemyHome={enemyHome} "
                + $"sectors={seen}/{Math.Max(reachable.Count, snap.KnownSectors.Count)} swept={snap.SweptSectors.Count} "
                + $"alephs={snap.Warps.Count} rocks={snap.Rocks.Count}[{string.Join(",", rocks)}] "
                + $"enemyStations=[{string.Join(",", stations)}] "
                + $"enemyMiners=[{string.Join(",", miners)}] supportCalls={snap.SupportCalls} minerRaids={snap.MinerRaids} clash={clash}");
        }
    }

    /// <summary>P798 — called once per decision pass before bots are driven.</summary>
    private void UpdateExploration(ActiveBot[] bots, double now)
    {
        // A new mission means a new opening: the previous match's opening clock,
        // clash choices and one-line latches must not leak into it (the next
        // match is a fresh map with its own fog).
        uint cookie = 0;
        bool haveCookie = false;
        foreach (var bot in bots)
            if (bot.Cs is { } c) { cookie = CookieOf(c); haveCookie = true; break; }
        if (haveCookie && cookie != _openingCookie)
        {
            if (_openingCookie != uint.MaxValue)
                Console.WriteLine($"[opening] new mission {cookie} — resetting the scout opening");
            _openingCookie = cookie;
            _openingStartedAt = -1;
            _clash.Clear();
            _clashLogged.Clear();
            _exploreDoneLogged.Clear();
            _openingAlephed.Clear();
            _openingStationed.Clear();
            _openingRaided.Clear();
            _openingSupported.Clear();
            _reconLogged.Clear();
            _reconProgress.Clear();
            _explorerRoster.Clear();
            _laneStarted.Clear();
            _supportSectors.Clear();
            foreach (var bot in bots)
            {
                bot.IsExplorer = false;
                bot.ExploreLane = null;
                bot.ReconTarget = null;
                bot.AssignedClashSector = null;
                bot.SweepSector = -1;
                bot.SweepWaypoint = 0;
                bot.LastMinerRaidAt = double.NegativeInfinity;
                bot.RecentMinerAttackSectors.Clear();
            }
        }
        if (_openingStartedAt < 0) _openingStartedAt = now;
        if (now >= _nextIntelScan)
        {
            _nextIntelScan = now + ScoutOpening.ScanIntervalSeconds;
            ScanSideIntel(bots, now);
        }
        if (now >= _nextExplorerAssign)
        {
            _nextExplorerAssign = now + ScoutOpening.AssignIntervalSeconds;
            AssignExplorers(bots, now);
        }
        if (now >= _nextClashCheck)
        {
            _nextClashCheck = now + .5;
            AssignClashTargets(bots, now);
        }
        if (now >= _nextIntelLine)
        {
            _nextIntelLine = now + ScoutOpening.IntelLineSeconds;
            PublishIntelLines(now);
        }
        if (now >= _nextScoutTelemetry)
        {
            _nextScoutTelemetry = now + 20;
            LogScoutTelemetry(bots, now);
        }
    }

    /// <summary>P798b — bounded per-scout telemetry (one line per explorer per
    /// 20 s): proof that a sweep is actually FLYING (speed, waypoint) rather than
    /// only that the sweep code was reached. Without this the first live run
    /// could not tell "the scout never moved" from "the scanner sees nothing".</summary>
    private void LogScoutTelemetry(ActiveBot[] bots, double now)
    {
        foreach (var bot in bots)
        {
            if (!bot.IsExplorer || bot.Cs is not { } cs) continue;
            float speed = 0;
            short sector = -1;
            Vector3 pos = default;
            lock (cs.Gate)
                if (cs.World.Ships.TryGetValue(bot.ShipId, out var me) && me.Latest is { } pose)
                { speed = pose.Velocity.Length(); sector = me.SectorId; pos = pose.Position; }
            float sweepR = bot.SweepSector >= 0 ? SweepRadius(cs, bot.SweepSector, bot.Side, SideKey(bot).Cookie) : 0;
            Console.WriteLine($"[scout] t={now:F0}s side {bot.Side} {bot.Name} sec={sector} speed={speed:F0} "
                + $"pos=({pos.X:F0},{pos.Y:F0},{pos.Z:F0}) ringR={sweepR:F0} "
                + $"sweep={bot.SweepSector}:{bot.SweepWaypoint}({now - bot.SweepStartedAt:F0}s) "
                + $"lane={(bot.ExploreLane is { } l ? $"{l.From}->{l.To}" : "-")} "
                + $"recon={(bot.ReconTarget?.ToString() ?? "-")} target={bot.TargetId} staging={bot.StagingSector} phase={bot.StagePhase}");
        }
    }

    /// <summary>P798 — the scout behaviour. Returns true when this tick was spent
    /// exploring (the normal combat drive is then skipped). Priority inside the
    /// opening: raid an enemy miner this scout can see, then advance along the
    /// assigned frontier lane, then sweep the current sector for its alephs,
    /// rocks and stations.</summary>
    private bool TryDriveExploration(ActiveBot bot, ConnectionSession cs, double now)
    {
        // P830 — no OpeningActive gate here: exploration runs until the side's
        // map is complete (the stand-down below), not until the opening window
        // expires; the window only bounds the fleet-phase handover, not the
        // sweeps.
        if (!bot.IsExplorer || bot.OrderedCommand is not null) return false;
        // P837 — in a small fleet the scouts are the first ships to meet the
        // enemy (they are the ones crossing the map), so an explorer that can SEE
        // an enemy ship hands the tick to the normal combat drive instead of
        // sweeping past it: two nine-bot fleets' scouts met at 1.2 km and nobody
        // fired at all (live test P727).
        if (bot.MeetingEngagement)
        {
            short own = CsSector(cs);
            if (own >= 0 && VisibleEnemyInSector(bot, cs, own) is not null) return false;
        }
        var graph = GraphOf(cs);
        bool complete;
        lock (_intel.Sync)
        {
            var intel = _intel.Get(SideKey(bot).Cookie, bot.Side);
            short ownHome = ResolveOwnHome(intel, graph, bot.Side);
            var reachable = ExplorationPlanner.Reachable(graph, ownHome);
            complete = reachable.Count > 0 && reachable.All(s => intel.KnownSectors.Contains(s)
                && (intel.SweptSectors.Contains(s) || s == ownHome));
        }
        // A recon scout keeps flying until the side has actually seen the enemy
        // base; the rest stand down once the map is swept.
        if (complete && bot.ReconTarget is null && now - bot.LastMinerRaidAt >= ScoutOpening.RaidLingerSeconds)
        { bot.ExploreLane = null; return false; }   // opening done for this side
        if (!TryCsPose(cs, bot.ShipId, out _, out _)) return false;
        short cur = CsSector(cs);
        if (cur < 0) return false;

        // 1) Scouts hunt the enemy miners they have actually found (Domingo's
        //    opening: "scouts attacking miners"). The normal selector never
        //    picks miners (IgnoreMinerTargets), so this is an explicit raid.
        if (FindVisibleEnemyMiner(cs, bot, cur) is { } miner)
        {
            bool newRaid = bot.TargetId != miner;
            bot.TargetId = miner;
            EngageShipAt(bot, cs, miner, now, 1, 1);
            if (newRaid)
            {
                bot.LastMinerRaidAt = now;
                lock (_intel.Sync)
                {
                    var intel = _intel.Get(SideKey(bot).Cookie, bot.Side);
                    intel.MinerRaids++;
                    if (intel.FirstMinerRaidAt < 0)
                    {
                        intel.FirstMinerRaidAt = now;
                        Console.WriteLine($"[opening] t={now:F0}s side {bot.Side} scout {bot.Name} raiding enemy miner {miner} in sector {cur}");
                    }
                }
            }
            else bot.LastMinerRaidAt = now;
            return true;
        }

        // 2) Enemy-base recon: fly the topology path to the enemy home, sweeping
        //    each sector until the aleph to the next hop is seen. Nothing here
        //    reads the station/rock data of a sector that was not swept: the
        //    route is topology (CLUSTERINFO) and the sweep is this scout's own
        //    scanner (P797 per-side visibility).
        if (bot.ReconTarget is { } recon)
        {
            if (cur == recon)
            {
                // Arrived at the enemy home: sweep it for the station, its rocks
                // and any miners. The recon assignment is cleared by the next
                // AssignExplorers pass once an enemy station is in the intel.
                if (_reconProgress.Add($"{bot.Name}:at:{cur}"))
                    Console.WriteLine($"[opening] t={now:F0}s side {bot.Side} recon scout {bot.Name} reached the enemy home sector {cur} — sweeping for the station");
                SweepSector(bot, cs, cur, now, lane: null);
                return true;
            }
            short next = ExplorationPlanner.NextHop(graph, cur, recon);
            if (next >= 0 && next != cur)
            {
                if (TransitWarp(cs, bot, cur, next))
                {
                    if (_reconProgress.Add($"{bot.Name}:warp:{cur}:{next}"))
                        Console.WriteLine($"[opening] t={now:F0}s side {bot.Side} recon scout {bot.Name} flying {cur}->{next} (toward enemy home {recon})");
                    return true;
                }
                // The mouth aleph is not known yet: sweep this sector for it.
                if (_reconProgress.Add($"{bot.Name}:sweep:{cur}:{next}"))
                    Console.WriteLine($"[opening] t={now:F0}s side {bot.Side} recon scout {bot.Name} sweeping sector {cur} for the aleph to {next}");
                SweepSector(bot, cs, cur, now, lane: null, mouthTo: next);
                return true;
            }
            LoiterCombat(bot, now);
            return true;
        }

        var lane = bot.ExploreLane;
        if (lane is null) { LoiterCombat(bot, now); return true; }   // waiting for the next assignment
        short goal = lane.Value.To;
        if (cur != goal)
        {
            // If someone else has since seen the destination's contents, the lane
            // is done; move on next assignment.
            // A sector someone else has already seen only cancels a lane whose
            // DESTINATION was the unknown part. A "known but not yet swept"
            // self-lane still needs this scout to fly there and sweep it.
            bool goalKnown;
            lock (_intel.Sync) goalKnown = lane.Value.From != lane.Value.To
                && _intel.Get(SideKey(bot).Cookie, bot.Side).KnownSectors.Contains(goal);
            if (goalKnown) { bot.ExploreLane = null; return false; }
            EnsureCombatPath(bot, cur, goal, now);
            short next = bot.Path.Count > 0 ? CombatNextHop(bot, cur) : cur;
            if (next != cur && TransitWarp(cs, bot, cur, next)) return true;
            // No route or the next aleph is still unseen: sweep here to find
            // this lane's mouth (the lane's timeout/budget applies).
            SweepSector(bot, cs, cur, now, lane);
            return true;
        }
        // At the frontier: sweep it for alephs/rocks/stations.
        SweepSector(bot, cs, cur, now, lane);
        return true;
    }

    private static ClusterGraph? GraphOf(ConnectionSession cs)
    {
        lock (cs.Gate) return cs.World.Graph;
    }

    private static short? FindVisibleEnemyMiner(ConnectionSession cs, ActiveBot bot, short sector)
    {
        var core = Catalog.RepresentativeCore();
        short best = -1; float bestD = float.MaxValue;
        lock (cs.Gate)
        {
            if (!cs.World.Ships.TryGetValue(bot.ShipId, out var me) || me.Latest is not { } mine) return null;
            foreach (var ship in cs.World.Ships.Values)
            {
                if (ship.IsNotVisible || ship.IsDead || ship.IsGone || ship.SideId < 0 || ship.SideId == bot.Side) continue;
                if (ship.SectorId != sector || ship.Latest is null) continue;
                if (!core.HullTypes.TryGetValue(ship.HullId, out var h) || !h.HasCapability(DataHullTypeIGC.HabmMiner)) continue;
                float d = (ship.Latest.Position - mine.Position).Length();
                if (d < bestD) { bestD = d; best = ship.ShipId; }
            }
        }
        return best < 0 ? null : best;
    }

    /// <summary>Fly a deterministic sweep of the sector: the origin plus a ring
    /// of waypoints in the sector's X/Y plane (all object positions are
    /// sector-local; the small component is Z), wide enough that the hull's
    /// scanner reaches the rim. The ring is flown once per pattern and keeps its
    /// position across lane windows. A frontier is marked swept when its mouth
    /// aleph is seen (or its contents window ends); a mouth hunt only ends when
    /// the aleph is seen, so a ring is never abandoned half-way.</summary>
    private void SweepSector(ActiveBot bot, ConnectionSession cs, short sector, double now, FrontierLane? lane, short mouthTo = -1,
        float? radiusOverride = null)
    {
        if (bot.SweepSector != sector)
        {
            bot.SweepSector = sector;
            bot.SweepWaypoint = 0;
            bot.SweepStartedAt = now;
            bot.SweepPhaseAt = now;
        }
        int count = Math.Max(6, ScoutOpening.SweepWaypoints);
        int index = bot.SweepWaypoint % (count + 1);
        // Where a specific aleph is what the sweep is for (a frontier lane's mouth
        // or the recon scout's next topology hop), the sweep is finished the
        // moment that aleph is seen.
        short wantedMouth = mouthTo >= 0 ? mouthTo
            : lane is { } l0 && l0.From != l0.To && sector != l0.To ? l0.To : (short)-1;
        if (wantedMouth >= 0)
        {
            bool mouthKnown;
            bool timedOut = now - bot.SweepStartedAt >= ScoutOpening.LaneTimeoutSeconds;
            lock (_intel.Sync)
                mouthKnown = _intel.Get(SideKey(bot).Cookie, bot.Side).Warps.Values
                    .Any(w => w.Sector == sector && w.PartnerSector == wantedMouth);
            if (mouthKnown)
            {
                lock (_intel.Sync)
                {
                    var intel = _intel.Get(SideKey(bot).Cookie, bot.Side);
                    intel.SweptSectors.Add(sector);
                    if (lane is { } foundLane) intel.DoneLanes.Add(foundLane.Key);
                    Console.WriteLine($"[opening] scout {bot.Name} side {bot.Side} found the mouth {sector}->{wantedMouth} "
                        + $"after {now - bot.SweepStartedAt:F0}s ({intel.Warps.Values.Count(w => w.Sector == sector)} alephs, "
                        + $"{intel.Rocks.Values.Count(r => r.Sector == sector)} rocks known)");
                }
                bot.ExploreLane = null;
                _laneStarted.Remove(bot.Name);
                bot.SweepSector = -1;
                return;
            }
            if (timedOut && lane is { } retryLane)
            {
                // The lane window elapsed without the mouth. Hand the LANE back
                // for a retry later (it is not "swept" — the mouth was never
                // found here), but keep this scout's ring position: restarting the
                // pattern every window was exactly why a sweep never reached the
                // sector rim (measured: 0 mouths found in a whole 8-minute run).
                lock (_intel.Sync)
                    _intel.Get(SideKey(bot).Cookie, bot.Side).LaneRetryAt[retryLane.Key] =
                        now + ScoutOpening.LaneTimeoutSeconds;
                bot.ExploreLane = null;
                _laneStarted.Remove(bot.Name);
                // fall through and keep sweeping this sector
            }
        }
        else if (lane is { } l)
        {
            // At the frontier (or a "known sector not yet swept" self-lane):
            // sweep until every topology neighbour has a seen aleph here (the
            // sector is fully connected for this side) or the window times out.
            bool allMouths;
            var graph = GraphOf(cs);
            lock (_intel.Sync)
            {
                var intel = _intel.Get(SideKey(bot).Cookie, bot.Side);
                var adj = graph is null ? [] : ExplorationPlanner.BuildAdjacency(graph);
                allMouths = graph is not null && (!adj.TryGetValue(sector, out var nbrs) || nbrs.All(n =>
                    intel.Warps.Values.Any(w => w.Sector == sector && w.PartnerSector == n)));
            }
            if (allMouths || now - bot.SweepStartedAt >= ScoutOpening.LaneTimeoutSeconds)
            {
                lock (_intel.Sync)
                {
                    var intel = _intel.Get(SideKey(bot).Cookie, bot.Side);
                    intel.SweptSectors.Add(sector);
                    intel.DoneLanes.Add(l.Key);
                }
                Console.WriteLine($"[opening] scout {bot.Name} side {bot.Side} swept sector {sector} "
                    + $"after {now - bot.SweepStartedAt:F0}s (mouths {(allMouths ? "all seen" : "window elapsed")})");
                bot.ExploreLane = null;
                _laneStarted.Remove(bot.Name);
                bot.SweepSector = -1;
                return;
            }
        }

        float radius = radiusOverride ?? SweepRadius(cs, sector, bot.Side, SideKey(bot).Cookie);
        // Each scout starts at a different angle so two explorers do not fly the
        // same arc; an unfinished pattern keeps its progress across ticks.
        // The SECTOR PLANE IS XY — every map's warps/rocks/stations have the small
        // component in Z (e.g. m2xspiderw warp 2162 = (360, 4160, 151)). A ring in
        // X/Z flies a vertical circle far off the content plane; the first live
        // sweep did exactly that (no move fixed it would find 0 mouths in a whole
        // run while only alephs near the plane were ever seen).
        Vector3 waypoint;
        if (index == 0) waypoint = Vector3.Zero;
        else
        {
            double angle = (bot.BotId % count) * (2 * Math.PI / count) + (index - 1) * (2 * Math.PI / count);
            waypoint = new Vector3((float)(Math.Cos(angle) * radius), (float)(Math.Sin(angle) * radius), 0f);
        }
        if (!TryCsPose(cs, bot.ShipId, out var pos, out _)) return;
        var toWaypoint = waypoint - pos;
        // Advance only on real proximity. (A "waypoint is behind me" test looks
        // attractive for a fly-through, but immediately after a turn the new
        // waypoint is behind the CURRENT heading, so that test advanced a whole
        // ring in ~20 s and the scout never actually visited the rim.)
        bool reached = toWaypoint.Length() < ScoutOpening.SweepWaypointReach;
        if (reached && now - bot.SweepPhaseAt >= ScoutOpening.SweepDwellSeconds)
        {
            bot.SweepWaypoint = (bot.SweepWaypoint + 1) % (count + 1);
            bot.SweepPhaseAt = now;
        }
        // Fly THROUGH the waypoint at full throttle (only the last few hundred
        // units brake), so a ring is one continuous fast orbit rather than ten
        // stop-and-go legs.
        CombatFlyTo(bot, waypoint, 0f, ScoutOpening.SweepBrake, 0);
        bot.LastCluster = sector;
    }

    /// <summary>The ring radius. A scout's scanner reaches ~2400 u, so the ring
    /// must sit at (sector span − reach) for the ring's outer edge to cover the
    /// sector rim while the centre waypoint covers the inner disc. The span is
    /// sized from what this side has already seen in the sector (native
    /// sector-local coordinates), defaulting to the stated corpus maximum.</summary>
    private float SweepRadius(ConnectionSession cs, short sector, short side, uint cookie)
    {
        short[] warpIds;
        short[] rockIds;
        lock (_intel.Sync)
        {
            var intel = _intel.Get(cookie, side);
            warpIds = intel.Warps.Values.Where(w => w.Sector == sector).Select(w => w.WarpId).ToArray();
            rockIds = intel.Rocks.Values.Where(r => r.Sector == sector).Select(r => r.RockId).ToArray();
        }
        float max = 0;
        lock (cs.Gate)
        {
            foreach (short id in warpIds)
                if (cs.World.Warps.TryGetValue(id, out var data) && data.Position is { Length: 3 })
                    max = Math.Max(max, Vec3(data.Position).Length());
            foreach (short id in rockIds)
                if (cs.World.Asteroids.TryGetValue(id, out var data) && data.Position is { Length: 3 })
                    max = Math.Max(max, Vec3(data.Position).Length());
        }
        // Always assume the stated corpus span, not just what is already known: an
        // UNSEEN aleph may sit farther out than every object seen so far (m2xspiderw
        // 1055's mouth to Bug Nebula is at r=4782 while its only known aleph is at
        // 3990 — a ring sized from the known objects alone passed 2282 u from it,
        // inside the scanner reach only by a hair, and side 1 missed it for 8 min).
        float span = Math.Max(max, ScoutOpening.SweepSpanDefault);
        return Math.Clamp(span - ScoutOpening.SweepScanReach,
            ScoutOpening.SweepRadiusMin, ScoutOpening.SweepRadiusMax);
    }

    // ── Support calls for miners/constructors ─────────────────────────────
    // The defence incident model (BotManager.Defense) already answers "which
    // sector, which fighters". P798 extends its trigger from stations to any
    // own-side mining/construction asset that an enemy just hit. The hit stream
    // is the same real PROJECTILE_HULL_HIT the bots already drain in
    // SampleCombatStats; no server truth is read.

    /// <summary>Record an attack on a friendly miner/constructor (called from
    /// SampleCombatStats with the real hit).</summary>
    private static void NoteFriendlyAssetAttack(ActiveBot bot, short sector, double now)
    {
        if (sector < 0) return;
        bot.RecentMinerAttackSectors[sector] = now;
    }

    /// <summary>Collect the active miner support calls for a side: sectors where
    /// a friendly miner was hit recently (within the support window). Used by
    /// UpdateDefense (trigger) and TryDriveDefense (a sector with no own station
    /// is still a valid defence objective when a support call is live).</summary>
    private bool IsSupportSector(DefenseKey key, double now) =>
        _supportSectors.TryGetValue(key, out double at) && now - at <= ScoutOpening.SupportWindowSeconds;

    private Dictionary<DefenseKey, double>? _supportSectorsMap;
    private Dictionary<DefenseKey, double> _supportSectors => _supportSectorsMap ??= [];
}

internal sealed partial class BotManager
{
    /// <summary>P798 — `get_side_intel`: everything each side has actually seen,
    /// per side and mission, plus the computed clash choice and its scoring
    /// inputs. Read-only; built only from the side's own bot worlds. Bounded:
    /// lists are capped and the total counts are reported alongside.</summary>
    internal object[] SideIntelReport()
    {
        return _intel.ReadAll(intels => intels
            .OrderBy(i => i.Side).ThenBy(i => i.Cookie)
            .Select(i =>
            {
                ClusterGraph? graph = null;
                var bot = _bots.FirstOrDefault(b => b.Cs is not null && b.Side == i.Side && CookieOf(b.Cs) == i.Cookie);
                if (bot?.Cs is { } cs) lock (cs.Gate) graph = cs.World.Graph;
                short ownHome = ResolveOwnHome(i, graph, i.Side);
                var reachable = ExplorationPlanner.Reachable(graph, ownHome >= 0 ? ownHome : (short)0);
                int seen = reachable.Count(i.KnownSectors.Contains);
                var rocks = i.Rocks.Values.Where(r => r.He3 || r.Gold || r.Buildable || r.Special)
                    .OrderByDescending(r => r.Value).ThenBy(r => r.RockId).Take(60)
                    .Select(r => new
                    {
                        id = r.RockId, sector = r.Sector, ore = r.Ore, oreMax = r.OreMax,
                        he3 = r.He3, lotsHe3 = r.LotsHe3, gold = r.Gold,
                        buildable = r.Buildable, special = r.Special, value = Math.Round(r.Value, 2),
                    }).ToArray();
                // P798b — every rock this side holds, so the audit of "were these
                // really SEEN by this side?" can name each one (bounded by the map).
                var rockIds = i.Rocks.Values.OrderBy(r => r.RockId).Take(400)
                    .Select(r => new { id = r.RockId, sector = r.Sector, ore = r.Ore }).ToArray();
                var rockSectors = i.Rocks.Values.GroupBy(r => r.Sector).OrderBy(g => g.Key)
                    .Select(g => new { sector = g.Key, count = g.Count() }).ToArray();
                var stations = i.EnemyStations.Values.OrderBy(s => s.StationId)
                    .Select(s => new { id = s.StationId, side = s.SideId, type = s.StationTypeId, sector = s.Sector }).ToArray();
                var hulls = i.EnemyShips.Values.OrderByDescending(h => h.At).Take(60)
                    .Select(h => new { shipId = h.ShipId, side = h.SideId, hull = h.HullId, sector = h.Sector, role = h.Role, loadout = h.Loadout, ageSeconds = Math.Round(Now() - h.At, 1) }).ToArray();
                var miners = i.EnemyMiners.Values.OrderByDescending(m => m.At).Take(40)
                    .Select(m => new { shipId = m.ShipId, side = m.SideId, sector = m.Sector, ageSeconds = Math.Round(Now() - m.At, 1) }).ToArray();
                object? clash = _clash.TryGetValue((i.Cookie, i.Side), out var c)
                    ? new { sector = c.Sector, score = Math.Round(c.Score, 3), why = c.Why,
                        resource = Math.Round(c.Resource, 2), ownMiners = c.OwnMiners, enemyMiners = c.EnemyMiners,
                        enemyStation = c.EnemyStation, ownHops = c.OwnHops, enemyHops = c.EnemyHops }
                    : null;
                return (object)new
                {
                    side = i.Side, cookie = i.Cookie, ownHome, enemyHome = i.EnemyHome,
                    knownSectors = i.KnownSectors.Order().ToArray(),
                    sweptSectors = i.SweptSectors.Order().ToArray(),
                    reachableSectors = reachable.Order().ToArray(),
                    seenReachableShare = reachable.Count == 0 ? 0 : Math.Round((double)seen / reachable.Count, 3),
                    alephs = i.Warps.Values.OrderBy(w => w.WarpId)
                        .Select(w => new { id = w.WarpId, sector = w.Sector, partnerSector = w.PartnerSector }).ToArray(),
                    rockCount = i.Rocks.Count,
                    // Per-sector rock counts + the full id list: this side's OWN
                    // scanner record, never server truth (P797 per-side export).
                    rockSectors = rockSectors,
                    rocks = rockIds,
                    // He3/tech rocks with the ore seen by this side (native's
                    // per-side oreSeen, asteroidigc.h:280-285).
                    resourceRocks = rocks,
                    enemyStations = stations,
                    enemyHullCount = i.EnemyShips.Count,
                    enemyHulls = hulls,
                    enemyMiners = miners,
                    ownMinerCount = i.OwnMiners.Count,
                    minerRaids = i.MinerRaids,
                    supportCalls = i.SupportCalls,
                    opening = new
                    {
                        firstAlephSeconds = i.FirstAlephAt < 0 ? (double?)null : Math.Round(i.FirstAlephAt, 1),
                        firstEnemyStationSeconds = i.FirstEnemyStationAt < 0 ? (double?)null : Math.Round(i.FirstEnemyStationAt, 1),
                        firstMinerRaidSeconds = i.FirstMinerRaidAt < 0 ? (double?)null : Math.Round(i.FirstMinerRaidAt, 1),
                        firstSupportCallSeconds = i.FirstSupportCallAt < 0 ? (double?)null : Math.Round(i.FirstSupportCallAt, 1),
                        explorationComplete = reachable.Count > 0 && reachable.All(s => i.KnownSectors.Contains(s)
                            && (i.SweptSectors.Contains(s) || s == ownHome)),
                    },
                    clash,
                };
            }).ToArray());
    }
}
