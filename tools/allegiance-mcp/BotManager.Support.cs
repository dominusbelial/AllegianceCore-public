using System.Globalization;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    internal sealed record SupportSettings(double Share)
    {
        internal static SupportSettings Read(Func<string, string?> read) => new(
            double.TryParse(read("ALLEGIANCE_BOT_SUPPORT_SHARE"), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var s) && double.IsFinite(s) ? Math.Clamp(s, 0, 1) : .10);
    }

    internal static bool SupportSlot(int ordinal, int count, double share) => count > 0
        && ordinal >= 0 && ordinal < count && share > 0
        && (int)Math.Ceiling((ordinal + 1) * share) > (int)Math.Ceiling(ordinal * share);
    private static bool IsSupport(ActiveBot bot) => bot.Role.Equals("support", StringComparison.OrdinalIgnoreCase);

    // ── P791 — the phase signal support pilots read ──────────────────────────
    // The showcase/skirmish match has phases and support pilots exist to serve
    // the phase their side is in (Domingo, 2026-10-22): OPENING — fly with the
    // fighter fleet in the contested sector(s) and keep fighters (and
    // threatened miners) alive there; STRIKE — fly with the strike group /
    // finale bomber wing. This is harness policy, not native (native nanite
    // drones only ever repair what their commander ordered: shipIGC.cpp:2608-2676).
    internal enum SupportPhaseKind { Opening, Strike }
    internal readonly record struct SupportPhaseSignal(SupportPhaseKind Kind, short? AnchorSector);

    /// <summary>Pure phase composition so the rules are unit-provable.
    /// Precedence: an active finale run (bomb runs) outranks an active
    /// SC-7/8/9 strike wave, which outranks the fighter fleet's rendezvous
    /// (the opening). The rendezvous/presence lists are (sector, count) modes.</summary>
    internal static SupportPhaseSignal SupportPhaseFrom(bool finaleTriggered, short? finaleSector,
        bool strikeWaveActive, short strikeWaveSector,
        IReadOnlyList<(short Sector, int Count)> fighterRendezvous,
        IReadOnlyList<(short Sector, int Count)> fightersPresent)
    {
        if (finaleTriggered && finaleSector is { } fs && fs > 0) return new(SupportPhaseKind.Strike, fs);
        if (strikeWaveActive && strikeWaveSector > 0) return new(SupportPhaseKind.Strike, strikeWaveSector);
        (short Sector, int Count)? best = Mode(fighterRendezvous) ?? Mode(fightersPresent);
        return new(SupportPhaseKind.Opening, best is { } m && m.Sector > 0 ? m.Sector : null);
    }

    private static (short Sector, int Count)? Mode(IReadOnlyList<(short Sector, int Count)> counts)
    {
        (short, int)? best = null;
        foreach (var c in counts)
            if (c.Count > 0 && (best is not { } b || c.Count > b.Item2)) best = c;
        return best;
    }

    /// <summary>The side's current phase, read from the existing finale-run and
    /// strike-wave state plus the side's fighter fleet (minimal shared accessor,
    /// bot-side; no server/wire change). Opening anchor = the fighters'
    /// rendezvous mode, falling back to where the fighters actually are.</summary>
    internal SupportPhaseSignal SupportPhaseForSide(short side)
    {
        bool finaleTriggered = false; short? finaleSector = null;
        lock (_gate)
        {
            foreach (var run in _finales ?? [])
                if (run.TriggeredAt is not null && run.GameOverAt is null && !run.Rotated
                    && run.Sides.Contains(side) && run.Plans.TryGetValue(side, out var plan))
                { finaleTriggered = true; finaleSector = plan.Sector; break; }
        }
        bool waveActive = false; short waveSector = -1;
        if (_researchGate is not null)   // null in reflection-built managers (tests)
            lock (_researchGate)
                if (_researchTactics.TryGetValue(side, out var tactics))
                { waveActive = tactics.Policy.StrikeWaveActive; waveSector = tactics.Policy.StrikeSector; }
        List<(short Sector, int Count)> rendezvous = [], present = [];
        List<ActiveBot> fighters;
        lock (_gate)
            fighters = _bots.Where(b => b.Side == side && !IsSupport(b) && !b.IsPod && IsParticipating(b)).ToList();
        foreach (var bot in fighters)
        {
            if (bot.Role.Equals("bomber", StringComparison.OrdinalIgnoreCase)) continue; // bombers own the enemy home
            if (bot.TargetSector is { } t) CountInto(rendezvous, t);
            if (bot.Cs is { } cs) { short s = CsSector(cs); if (s >= 0) CountInto(present, s); }   // sector 0 is a valid home sector
        }
        return SupportPhaseFrom(finaleTriggered, finaleSector, waveActive, waveSector, rendezvous, present);
    }

    private static void CountInto(List<(short Sector, int Count)> counts, short sector)
    {
        int i = counts.FindIndex(c => c.Sector == sector);
        if (i >= 0) counts[i] = (sector, counts[i].Count + 1);
        else counts.Add((sector, 1));
    }
    private static bool HasNanite(ActiveBot bot) => bot.WeaponPartId is { } id
        && Catalog.RepresentativeCore().PartTypes.TryGetValue(id, out var p) && p.Type is DataPartTypeIGC.Weapon w
        && Catalog.RepresentativeCore().ProjectileTypes.TryGetValue(w.ProjectileTypeId, out var r) && r.Power < 0;

    internal readonly record struct RepairTarget(short Id, bool Station, short Sector, Vector3 Position,
        Vector3 Velocity, float Radius, bool FinaleBomber = false, bool FinalePusher = false, float HullFraction = 1f);

    // ── P791 — repair hysteresis constants (selection is harness policy; the
    // native fire law is unchanged: a nanite drone stops firing when the
    // target's GetFraction() == 1.0f, Igc/shipIGC.cpp:2668-2671) ─────────────
    /// <summary>Below this observed hull fraction a repair STARTS. At home (and
    /// for other support pilots anywhere) damage must be REAL — the 1/240 wire
    /// quantization (ClientWorld.cs BpHullFraction / 240) turns every gentle
    /// bump into a permanent-looking sub-1.0 reading, and repairing those
    /// produced the home ring (P791 report).</summary>
    internal const float RepairStartFraction = .85f;
    /// <summary>An observation older than this is STALE (native deviation-ranked
    /// updates flow at ≥ minUpdateHz for live ships; a pose older than this is
    /// not trustworthy), and the target is dropped.</summary>
    internal const double MaxSnapshotAgeSeconds = 6.0;
    /// <summary>Drop a target whose observed fraction has not improved for this
    /// long (the server-side hull may have reached full while the wire went
    /// quiet — repairing it can never complete).</summary>
    internal const double NoImproveSeconds = 10.0;
    /// <summary>Hard leash: never repair one target longer than this (light-gate:
    /// no pilot spends > 60 s on the same target).</summary>
    internal const double RepairLeashSeconds = 45.0;
    /// <summary>After a drop the same target is not re-picked for this long.</summary>
    internal const double TargetCooldownSeconds = 10.0;

    /// <summary>Pure destination composition: a damaged own station recalls the
    /// pilot first (any phase), else the phase anchor (opening: the fighter
    /// fleet's contested sector; strike: the strike group's sector) — repair
    /// happens THERE; else the pilot's own rendezvous. PhaseDriven tells the
    /// caller to anchor on the wing/companion in that sector.</summary>
    internal static (short? Sector, bool PhaseDriven) PickSupportDestination(
        short? damagedStationSector, SupportPhaseSignal phase, short? fallbackSector)
    {
        if (damagedStationSector is { } station) return (station, false);
        if (phase.AnchorSector is { } anchor) return (anchor, true);
        return (fallbackSector, false);
    }

    /// <summary>Pure hold decision for the CURRENTLY held repair target.
    /// Full first (that is a COMPLETED repair, native stop law), then the leash,
    /// stale snapshot and no-improvement drops.</summary>
    internal enum SupportHoldDecision { Keep, DroppedFull, DroppedStale, DroppedNoImprove, DroppedLeash }
    internal static SupportHoldDecision EvaluateRepairHold(double now, double startedAt, float bestFraction,
        float observedFraction, double observedAgeSeconds, double lastImproveAt,
        double maxAgeSeconds = MaxSnapshotAgeSeconds, double noImproveSeconds = NoImproveSeconds,
        double leashSeconds = RepairLeashSeconds)
    {
        if (observedFraction >= 1f) return SupportHoldDecision.DroppedFull;   // native stop law: == 1.0 (shipIGC.cpp:2668-2671)
        if (observedFraction <= 0f) return SupportHoldDecision.DroppedStale;  // dead or gone: not a completed repair
        if (now - startedAt > leashSeconds) return SupportHoldDecision.DroppedLeash;
        if (observedAgeSeconds > maxAgeSeconds) return SupportHoldDecision.DroppedStale;
        if (now - lastImproveAt > noImproveSeconds) return SupportHoldDecision.DroppedNoImprove;
        return SupportHoldDecision.Keep;
    }


    // Selection is harness policy; native repair discriminant/lead/fire law:
    // Igc/shipIGC.cpp:2608-2676. Only observed, living, damaged own assets qualify.
    // P778a — during a finale, a damaged finale bomber in the sector outranks
    // stations and other ships (it IS the mission), and damaged pushers outrank
    // them too (they pay the native friendly-ram damage, shipIGC.cpp:1166-1168,
    // on every contact). Outside a finale both lists are empty and the existing
    // station-then-ship order is unchanged.
    // P791 — phase-aware damage bars: in the side's battle sector any damaged
    // own ship (0, 1) still qualifies; at home only GENUINE damage starts a
    // repair (observed < RepairStartFraction) and never another support pilot
    // (that mutual ring is the P791 bug). A target under cooldown (just
    // dropped: stale / not improving / leashed) is skipped for this tick.
    internal static RepairTarget? PickRepairTarget(ClientWorld world, short side, short own, short sector, Vector3 position,
        IReadOnlyCollection<short>? finaleBombers = null, IReadOnlyCollection<short>? finalePushers = null,
        short? battleSector = null, IReadOnlySet<short>? supportShipIds = null, short cooldownTarget = -1,
        short? heldTarget = null)
    {
        var core = Catalog.RepresentativeCore();
        bool home = battleSector is { } b && sector != b;   // everything that is not the phase anchor is "home"
        if (finaleBombers is { Count: > 0 })
        {
            var bomber = world.Ships.Values.FirstOrDefault(s => finaleBombers.Contains(s.ShipId) && s.SideId == side
                    && s.SectorId == sector && !s.IsNotVisible && !s.Unknown && s.StationId < 0 && s.ParentId < 0
                    && !Catalog.IsLifepodHull(s.HullId) && s.Latest is { HullFraction: > 0 and < 1 } && s.ShipId != cooldownTarget);
            if (bomber?.Latest is { } bp)
                return new(bomber.ShipId, false, sector, bp.Position, bp.Velocity, HullRadius(bomber.HullId),
                    FinaleBomber: true, HullFraction: bp.HullFraction);
        }
        if (finalePushers is { Count: > 0 })
        {
            var pusher = world.Ships.Values.Where(s => finalePushers.Contains(s.ShipId) && s.SideId == side
                    && s.SectorId == sector && !s.IsNotVisible && !s.Unknown && s.StationId < 0 && s.ParentId < 0
                    && !Catalog.IsLifepodHull(s.HullId) && s.Latest is { HullFraction: > 0 and < 1 } && s.ShipId != cooldownTarget)
                .OrderBy(s => (s.Latest!.Position - position).LengthSquared()).ThenBy(s => s.ShipId).FirstOrDefault();
            if (pusher?.Latest is { } pp)
                return new(pusher.ShipId, false, sector, pp.Position, pp.Velocity, HullRadius(pusher.HullId),
                    FinalePusher: true, HullFraction: pp.HullFraction);
        }
        var station = world.Stations.Values.Where(s => s.SideId == side && s.ClusterId == sector
                && s.StationId != cooldownTarget
                && s.Position is { Length: 3 } && world.StationLive.TryGetValue(s.StationId, out var live)
                && live.HullFraction > 0 && live.HullFraction < 1)
            .OrderBy(s => (Vec3(s.Position) - position).LengthSquared()).ThenBy(s => s.StationId).FirstOrDefault();
        if (station.Position is { Length: 3 } && world.StationLive.TryGetValue(station.StationId, out var stationLive))
            return new(station.StationId, true, sector, Vec3(station.Position), Vector3.Zero,
                core.StationTypes.TryGetValue(station.StationTypeId, out var st) ? st.Radius : 100, HullFraction: stationLive.HullFraction);
        // P791 hysteresis — the held SHIP target is kept while it still qualifies:
        // nearest-first re-picking flip-flops between two equally damaged ships
        // (positions/damage order swap tick to tick) and churns holds. A damaged
        // own station still outranks it (P778a station > ship). The held ship
        // must pass the SAME bars as a fresh candidate.
        if (heldTarget is { } held && held != own && held != cooldownTarget
            && world.Ships.TryGetValue(held, out var hs) && hs.SideId == side && hs.SectorId == sector
            && !hs.IsNotVisible && !hs.Unknown && hs.StationId < 0 && hs.ParentId < 0
            && !Catalog.IsLifepodHull(hs.HullId) && hs.Latest is { HullFraction: > 0 and < 1 } hp
            && world.PoseAgeSeconds(held) <= MaxSnapshotAgeSeconds)
        {
            bool heldIsSupport = supportShipIds?.Contains(held) == true;
            float heldBar = heldIsSupport || home ? RepairStartFraction : 1f;
            if (!(heldIsSupport && home) && hp.HullFraction < heldBar)
                return new(held, false, sector, hp.Position, hp.Velocity, HullRadius(hs.HullId), HullFraction: hp.HullFraction);
        }
        // Ship bar: battle sector repairs any damage (P778a behaviour unchanged);
        // at home only genuine damage. Support pilots are candidates only in the
        // battle sector and only when genuinely damaged.
        float bar = home ? RepairStartFraction : 1f;
        var ship = world.Ships.Values.Where(s => s.ShipId != own && s.SideId == side && !s.IsNotVisible && !s.Unknown
                && s.StationId < 0 && s.ParentId < 0 && s.SectorId == sector && !Catalog.IsLifepodHull(s.HullId)
                && s.ShipId != cooldownTarget
                && s.Latest is { HullFraction: > 0 and < 1 }
                // P791: a frozen pose is a ghost (dead/docked/missed delete), not a patient.
                && world.PoseAgeSeconds(s.ShipId) <= MaxSnapshotAgeSeconds)
            .Where(s => s.Latest!.HullFraction < bar)
            .Where(s => supportShipIds is not { } supports || !supports.Contains(s.ShipId) || (!home && s.Latest!.HullFraction < RepairStartFraction))
            .OrderBy(s => (s.Latest!.Position - position).LengthSquared()).ThenBy(s => s.ShipId).FirstOrDefault();
        return ship?.Latest is { } pose ? new(ship.ShipId, false, sector, pose.Position, pose.Velocity, HullRadius(ship.HullId), HullFraction: pose.HullFraction) : null;
    }

    private bool TryDriveSupport(ActiveBot bot, double now)
    {
        if (!IsSupport(bot) || bot.Cs is not { } cs || bot.IsPod || !IsParticipating(bot)) return false;
        SupportSummaryTick(bot, now);
        if (bot.SupportRefitting)
        {
            HandleRelaunchAck(bot, cs);
            short dock;
            lock (cs.Gate) dock = cs.World.Ships.TryGetValue(bot.ShipId, out var own) ? own.StationId : (short)-1;
            if (dock >= 0) { bot.PodDocked = true; RelaunchDockedPod(bot, cs, now); return true; }
            if (bot.PodDocked)
            {
                if (!bot.RelaunchAckHandled || bot.LoadoutRefused) return true;
                ResetNewLife(bot, now, reinforcement: false);
                bot.SupportRefitting = false;
            }
        }
        if (!HasNanite(bot))
        {
            // Tech can unlock while this pilot is alive. Return for a real dock,
            // legal purchase and ACK rather than waiting for an artificial death.
            if (!bot.SupportRefitting && now >= bot.SupportCheckAt)
            {
                bot.SupportCheckAt = now + 5;
                lock (cs.Gate)
                {
                    var world = cs.World;
                    if (world.DevelopmentTechsFor(bot.Side) is { } techs && world.MyMoney is { } money)
                    {
                        foreach (var s in world.Stations.Values.Where(s => s.SideId == bot.Side))
                            techs = techs.Or(TechTreeBitMask.FromBytes(Catalog.RepresentativeCore().StationTypes[s.StationTypeId].TtbmEffects));
                        short civ = world.SideRoster?.Sides.Where(s => s.SideId == bot.Side).Select(s => (short?)s.CivId).FirstOrDefault() ?? -1;
                        var core = Catalog.RepresentativeCore();
                        if (core.Civilizations.ContainsKey(civ))
                        {
                            short cur = CsSector(cs);
                            var shops = world.Stations.Values.Where(s => s.SideId == bot.Side && s.Position is { Length: 3 }
                                    && core.StationTypes.TryGetValue(s.StationTypeId, out var type) && type.HasCapability(DataStationTypeIGC.SabmLand)
                                    && (s.ClusterId == cur || world.Graph?.FindShortestPath(cur, s.ClusterId) is { Count: >= 2 }))
                                .Select(s => (Station: s, Loadout: TechLoadoutSelector.SelectSupport(core, civ, techs, s.StationTypeId, long.MaxValue)))
                                .Where(s => s.Loadout is not null).OrderBy(s => s.Loadout!.Price).ThenBy(s => s.Station.StationId).ToArray();
                            if (shops.FirstOrDefault(s => s.Loadout!.Price <= money) is { Loadout: not null } shop)
                            {
                                bot.SupportRefitting = true;
                                bot.PodStationId = shop.Station.StationId;
                                bot.PodStationSector = shop.Station.ClusterId;
                                bot.PodStationCenter = Vec3(shop.Station.Position);
                            }
                            else if (shops.Length > 0 && world.AutoDonateTargetByShipId.ContainsKey(bot.ShipId))
                                cs.SendAutoDonate(-1); // Save native payday for a legal refit (FedSrv/FedSrv.CPP:5535-5544).
                        }
                    }
                }
            }
            if (!bot.SupportRefitting) return false;
            bot.TargetId = null;
            short station;
            lock (cs.Gate) station = cs.World.Ships.TryGetValue(bot.ShipId, out var me) ? me.StationId : (short)-1;
            if (station >= 0) { bot.PodDocked = true; RelaunchDockedPod(bot, cs, now); }
            else DrivePodHome(bot, cs, now);
            return true;
        }
        bot.SupportRefitting = false;
        // Accepted commander repair owns the named target and route before
        // autonomous support selection (shipIGC.cpp:1936-1939).
        if (bot.OrderedCommand?.CommandId == OrderChange.CidRepair && DriveCommanderOrder(bot, now)) return true;
        if (!TryCsPose(cs, bot.ShipId, out var pos, out _)) return true;
        short sector = CsSector(cs);
        // P791 — the side's phase drives everything below: opening → the fighter
        // fleet's contested sector; strike/finale → the strike group's sector.
        SupportPhaseSignal phase = SupportPhaseForSide(bot.Side);
        // P778a — during a finale the wing's damaged bombers come first, then
        // the pushers that pay collision damage; the station/ship order follows.
        List<short>? finaleBombers = null, finalePushers = null;
        if (bot.Finale is { Rotated: false } activeFinale)
            lock (_gate)
            {
                finaleBombers = activeFinale.Bots.Where(b => b.Side == bot.Side && b.FinaleBomberActive && !b.IsPod)
                    .Select(b => b.ShipId).ToList();
                finalePushers = activeFinale.Bots.Where(b => b.Side == bot.Side && b.FinaleEscort && !b.IsPod && b.PushContacts > 0)
                    .Select(b => b.ShipId).ToList();
            }
        HashSet<short> supportIds;
        lock (_gate)
            supportIds = _bots.Where(b => b.Side == bot.Side && IsSupport(b) && !b.IsPod)
                .Select(b => b.ShipId).ToHashSet();
        bool cooldownActive = bot.SupportCooldownTarget >= 0 && now < bot.SupportCooldownUntil;
        RepairTarget? target;
        lock (cs.Gate)
        {
            target = PickRepairTarget(cs.World, bot.Side, bot.ShipId, sector, pos, finaleBombers, finalePushers,
                phase.AnchorSector, supportIds, cooldownActive ? bot.SupportCooldownTarget : (short)-1,
                bot.SupportHoldTarget);
            target = ApplyRepairHold(bot, cs, target, now, sector, phase.AnchorSector);   // P791 hysteresis
        }
        if (target is { } repair)
        {
            bot.TargetId = repair.Id;
            if (bot.SupportTraceTarget != repair.Id || bot.SupportTraceStation != repair.Station)
            {
                Console.WriteLine($"[support] {bot.Name} ship={bot.ShipId} side={bot.Side} "
                    + $"repair={(repair.Station ? "station" : repair.FinaleBomber ? "finale-bomber" : repair.FinalePusher ? "pusher" : "ship")} target={repair.Id} sector={sector} "
                    + $"hull={repair.HullFraction:F3} phase={(phase.Kind == SupportPhaseKind.Strike ? "strike" : "opening")} anchor={phase.AnchorSector ?? -1}");
                bot.SupportTraceTarget = repair.Id; bot.SupportTraceStation = repair.Station;
            }
            FlyRepairTarget(bot, cs, repair);
            return true;
        }
        bot.TargetId = null; bot.SupportTraceTarget = null;
        // P791 — follow the side's phase: go to the battle sector and repair
        // THERE (the pick above already scored only the current sector). Recall
        // to observed damaged stations first; never inspect server entities or a
        // human loadout.
        ActiveBot[] companions;
        lock (_gate) companions = _bots.Where(b => b.Side == bot.Side && !IsSupport(b) && !b.IsPod && IsParticipating(b)).ToArray();
        short? damagedStationSector;
        lock (cs.Gate)
        {
            damagedStationSector = cs.World.Stations.Values.Where(s => s.SideId == bot.Side
                    && cs.World.StationLive.TryGetValue(s.StationId, out var live) && live.HullFraction is > 0 and < 1)
                .OrderBy(s => s.StationId).Select(s => (short?)s.ClusterId).FirstOrDefault();
        }
        var (destination0, phaseDriven) = PickSupportDestination(damagedStationSector, phase, bot.TargetSector);
        short? destination = destination0;
        Vector3? anchor = null;
        if (destination is { } battle && phaseDriven)
        {
            // Anchor on the side's strike wing when one is in the battle sector
            // (strike phase), else on the nearest companion already there (opening).
            lock (cs.Gate)
            {
                var wing = companions.Where(b => b.FinaleBomberActive)
                    .Select(b => cs.World.Ships.GetValueOrDefault(b.ShipId))
                    .Where(s => s?.Latest is not null && !s.IsDead && s.SectorId == battle)
                    .OrderBy(s => s!.ShipId).FirstOrDefault();
                var buddy = wing ?? companions.Select(b => cs.World.Ships.GetValueOrDefault(b.ShipId))
                    .Where(s => s?.Latest is not null && !s.IsDead && !s.IsNotVisible && s.StationId < 0 && s.SectorId == battle)
                    .OrderBy(s => (s!.Latest!.Position - pos).LengthSquared()).ThenBy(s => s!.ShipId).FirstOrDefault();
                if (buddy?.Latest is { } pose) anchor = pose.Position;
            }
        }
        if (destination is null)
        {
            // No phase signal and no own rendezvous — legacy fallback: the strike
            // wing wherever it is, else the nearest co-located companion.
            lock (cs.Gate)
            {
                var strike = companions.Where(b => b.FinaleBomberActive)
                    .Select(b => cs.World.Ships.GetValueOrDefault(b.ShipId)).Where(s => s?.Latest is not null && !s.IsDead)
                    .OrderBy(s => s!.ShipId).FirstOrDefault()
                    ?? companions.Select(b => cs.World.Ships.GetValueOrDefault(b.ShipId))
                        .Where(s => s?.Latest is not null && !s.IsNotVisible && s.StationId < 0 && s.SectorId == sector)
                        .OrderBy(s => (s!.Latest!.Position - pos).LengthSquared()).ThenBy(s => s!.ShipId).FirstOrDefault();
                if (strike?.Latest is { } pose)
                { destination = strike.SectorId; anchor = pose.Position; }
            }
        }
        if (destination is { } goal && goal != sector)
        {
            List<short>? path;
            lock (cs.Gate) path = cs.World.Graph?.FindShortestPath(sector, goal);
            if (path is { Count: >= 2 } && TransitWarp(cs, bot, sector, path[1])) return true;
        }
        if (anchor is { } center) CombatFlyTo(bot, center, 250, 600, 0);
        else LoiterCombat(bot, now);
        return true;
    }

    // P791 — hold/drop bookkeeping for the picked repair target: starts a hold
    // when the target changes, tracks the last observed fraction (improvement is
    // measured per observation), and drops the hold when the target went full
    // (completed), stale, stopped improving, or the leash expired (a cooldown
    // keeps it unpickable for a while). Returns the target to fly, or null when
    // the hold was dropped this tick.
    private RepairTarget? ApplyRepairHold(ActiveBot bot, ConnectionSession cs, RepairTarget? target, double now,
        short sector, short? battleSector)
    {
        float Observed(short id)
        {
            if (cs.World.Stations.ContainsKey(id) && cs.World.StationLive.TryGetValue(id, out var st)) return st.HullFraction;
            return cs.World.Ships.TryGetValue(id, out var ship) && ship.Latest is { } pose ? pose.HullFraction : 0f;
        }
        // Hold continuation: same target, still picked — evaluate the hold rules.
        if (target is { } repair && bot.SupportHoldTarget == repair.Id)
        {
            float fraction = Observed(repair.Id);
            double age = repair.Station ? 0 : cs.World.PoseAgeSeconds(repair.Id);   // StationLive has no timestamp
            var decision = EvaluateRepairHold(now, bot.SupportHoldSince, bot.SupportHoldLast, fraction, age, bot.SupportHoldImproveAt);
            if (decision == SupportHoldDecision.Keep)
            {
                // Progress is ANY observed change beyond half a wire quantization
                // step (hull is quantized to 1/240, ClientWorld.cs BpHullFraction
                // / 240). A ship under fire may read LOWER every tick while the
                // nanites are genuinely slowing the loss — that is progress; only
                // a FROZEN observation (repair not landing, or the wire gone
                // quiet) is a drop.
                if (MathF.Abs(fraction - bot.SupportHoldLast) > 1f / 480f)
                {
                    bot.SupportHoldImproveAt = now;
                    if (bot.SupportCooldownTarget == repair.Id) bot.SupportCooldownStrikes = 0;   // progress earns a fresh chance
                }
                bot.SupportHoldLast = fraction;
                return repair;
            }
            // Dropped (full / stale / no progress / leash). No-progress and stale
            // drops ESCALATE the per-target cooldown (10 s doubling to a 120 s cap):
            // a target that keeps reading frozen (a ghost — ship gone server-side
            // while its SHIP_STATUS re-sends keep the pose age small, P791 run 11)
            // must not be re-locked every 10 s forever.
            bot.SupportHoldTarget = null;
            short prevCooldown = bot.SupportCooldownTarget;
            bot.SupportCooldownTarget = repair.Id;
            if (decision == SupportHoldDecision.DroppedFull)
            {
                bot.SupportCooldownStrikes = 0;
                bot.SupportCooldownUntil = now + TargetCooldownSeconds;
                SupportCompleted(bot, now - bot.SupportHoldSince);
            }
            else
            {
                bot.SupportCooldownStrikes = prevCooldown == repair.Id
                    ? (short)Math.Min(bot.SupportCooldownStrikes + 1, 7) : (short)1;
                bot.SupportCooldownUntil = now + Math.Min(TargetCooldownSeconds * (1 << (bot.SupportCooldownStrikes - 1)), 120.0);
            }
            return null;
        }
        // The hold ended without a drop rule: no pick at all, or the pick moved
        // to another target. When the previous target left candidacy REPAIRED
        // (observed >= its bar: full in the battle sector, RepairStartFraction at
        // home — that is why the pick no longer returns it), the repair COMPLETED.
        if (bot.SupportHoldTarget is { } prev)
        {
            float f = Observed(prev);
            bool home = battleSector is { } b && sector != b;
            if (f >= (home ? RepairStartFraction : 1f))
                SupportCompleted(bot, now - bot.SupportHoldSince);
        }
        if (target is not { } fresh)
        {
            bot.SupportHoldTarget = null;
            return null;
        }
        bot.SupportHoldTarget = fresh.Id;
        bot.SupportHoldSince = now;
        bot.SupportHoldLast = Observed(fresh.Id);
        bot.SupportHoldImproveAt = now;
        SupportStarted(bot);
        return fresh;
    }

    // P791 — bounded telemetry: ONE line per side per minute (never per tick;
    // the console is synchronous under load). Counts this side's support pilots
    // in the phase battle sector vs travelling vs home, plus repairs
    // started/completed and the mean completed repair duration.
    private sealed class SupportTelemetry
    {
        internal double NextAt = double.NegativeInfinity;
        internal int Started, Completed;
        internal double DurationSum;
    }

    private Dictionary<short, SupportTelemetry>? _supportTelemetry;

    private SupportTelemetry SupportRow(ActiveBot bot)
    {
        var map = _supportTelemetry ??= new Dictionary<short, SupportTelemetry>();
        if (!map.TryGetValue(bot.Side, out var row)) { row = new SupportTelemetry(); map[bot.Side] = row; }
        return row;
    }

    private void SupportStarted(ActiveBot bot)
    {
        lock (SupportRow(bot)) SupportRow(bot).Started++;
    }

    private void SupportCompleted(ActiveBot bot, double duration)
    {
        lock (SupportRow(bot)) { var row = SupportRow(bot); row.Completed++; row.DurationSum += duration; }
    }

    private void SupportSummaryTick(ActiveBot bot, double now)
    {
        var row = SupportRow(bot);
        int started, completed; double durationSum; bool due;
        lock (row)
        {
            due = now >= row.NextAt;
            if (due) row.NextAt = now + 60;   // reset BEFORE the work: a slow minute never re-triggers
            started = row.Started; completed = row.Completed; durationSum = row.DurationSum;
            if (due) { row.Started = 0; row.Completed = 0; row.DurationSum = 0; }
        }
        if (!due) return;
        // No other lock is held here (the row lock above is released first —
        // SupportStarted/SupportCompleted take it from inside cs.Gate).
        SupportPhaseSignal phase = SupportPhaseForSide(bot.Side);
        List<ActiveBot> pilots;
        lock (_gate)
            pilots = _bots.Where(b => b.Side == bot.Side && IsSupport(b) && !b.IsPod && IsParticipating(b)).ToList();
        int battle = 0, travelling = 0, repairing = 0, home = 0;
        foreach (var pilot in pilots)
        {
            short s = pilot.Cs is { } cs ? CsSector(cs) : (short)-1;
            if (phase.AnchorSector is { } a && s == a) battle++;
            else if (pilot.SupportHoldTarget is not null) repairing++;   // holding a repair where it is
            else if (pilot.Path.Count > 0 || pilot.RoutePath is { Count: > 0 }) travelling++;
            else home++;
        }
        Console.WriteLine($"[support-summary] side={bot.Side} phase={(phase.Kind == SupportPhaseKind.Strike ? "strike" : "opening")} "
            + $"anchor={phase.AnchorSector ?? -1} battle={battle} travelling={travelling} home={home} repairing={repairing} "
            + $"repairsStarted={started} repairsCompleted={completed} meanRepairSeconds={(completed == 0 ? 0 : durationSum / completed):F1}");
    }

    private static void FlyRepairTarget(ActiveBot bot, ConnectionSession cs, RepairTarget target)
    {
        if (!TryCsFreshPose(cs, bot.ShipId, out var pos, out var velocity, out var orientation)) return;
        if (bot.WeaponPartId is not { } gun || !TryWeaponBallistics(gun, out float speed, out _, out float life, out bool absolute)) return;
        bool lead = TrySolveLead(pos, velocity, target.Position, target.Velocity, target.Radius, speed, absolute, out var aim, out float t);
        if (!lead) aim = target.Position - pos;
        var (yaw, pitch, angle) = SteerTowardDirection(orientation, aim);
        int state = 0;
        bool dodging = TryDodgeGoal(cs, bot, pos, out var dodge);
        if (dodging) { CombatFlyTo(bot, dodge, 15, 200, 0); return; }
        if (lead && t <= life * .25f)
        {
            if (Showcase.StrafeEnabled(bot.WingmanBehaviour)) state |= ShipControlState.LeftButton;
            if (angle < MathF.PI * .5f) state |= angle > .10f ? ShipControlState.BackwardButton : ShipControlState.ForwardButton;
        }
        // Native fire law: no nanites once the target is whole — needsNan = false
        // when GetFraction() == 1.0f (Igc/shipIGC.cpp:2668-2671). The bot mirror
        // uses the OBSERVED fraction: firing at an observed-full target is the
        // exact waste the P791 hysteresis exists to stop.
        if (lead && target.HullFraction < 1f && angle <= .10f && t <= life * .95f) state |= ShipControlState.AllWeapons;
        cs.CommandControls(new ControlData(yaw, pitch, 0, AttackThrottle(angle)), state);
    }
}
