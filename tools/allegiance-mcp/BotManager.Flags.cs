using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.Tools.McpSpawner;

/// <summary>
/// Flag tactics are bot policy, using only the observing client's native wire state.
/// Pickup: FedSrv/FedSrv.CPP:13563-13580; secure: :13113-13161;
/// carrier identities are public GAIN_FLAG, positions still require visibility.
/// </summary>
internal sealed partial class BotManager
{
    internal enum FlagDuty { None, Raid, Escort, Guard, Intercept, Return }
    private sealed partial class ActiveBot
    {
        public FlagDuty FlagTask;
        public short? FlagAnchor;
        public int FlagPost;
        public string FlagTrace = "";
    }

    // Keep the large fleet's clash/strike intact: at 200 pilots, 8 raiders and
    // 12 guards. A small fleet gets a carrier and, when possible, escorts and a guard.
    internal static (int Raiders, int Guards) FlagShares(int population) => population <= 0 ? (0, 0)
        : (Math.Min(8, Math.Max(population >= 4 ? 3 : 1, (int)Math.Ceiling(population * .10))),
            population < 2 ? 0 : Math.Min(12, Math.Max(1, (int)Math.Ceiling(population * .10))));

    private static bool FlagsGame(ConnectionSession cs) => cs.World.Mission?.Goals.IsFlagsGame == true;
    private double _nextFlagAssignment;

    private void UpdateFlagDuties(ActiveBot[] bots, double now)
    {
        // No flags goal: no mutation, random draws, controls, or order changes.
        if (!bots.Any(b => b.Cs is { } cs && FlagsGame(cs))) return;
        if (now < _nextFlagAssignment) return;
        _nextFlagAssignment = now + 1;
        foreach (var bot in bots.Where(b => b.Cs is { } cs && FlagsGame(cs)))
        { bot.FlagTask = FlagDuty.None; bot.FlagAnchor = null; }
        var deployments = Partition is null ? PlanFlagDuties(FlagCandidates(bots)) : FreshCoordination?.Flags ?? [];
        var byName = deployments.ToDictionary(d => d.Name);
        foreach (var bot in bots)
        {
            if (bot.Cs is not { } cs || !FlagsGame(cs) || !byName.TryGetValue(bot.Name, out var d)
                || d.Cookie != CookieOf(cs) || d.Side != bot.Side) continue;
            bot.FlagTask = d.Duty; bot.FlagAnchor = d.Anchor; bot.FlagPost = d.Post;
        }
    }

    private FlagCandidate[] FlagCandidates(ActiveBot[] bots)
    {
        var candidates = new List<FlagCandidate>();
        foreach (var b in bots.Where(b => b.Cs is { } cs && FlagsGame(cs) && DefenseAvailable(b)
            && !IsSupport(b) && !b.Role.Equals("bomber", StringComparison.OrdinalIgnoreCase)
            && (b.OrderedCommand is null || !HumanCommands(b.Side)) && !b.FinaleBomberActive
            && Catalog.RepresentativeCore().HullTypes.TryGetValue(ActualHullId(b), out var hull)
            && hull.HasCapability(DataHullTypeIGC.HabmFighter)))
        {
            bool carrying;
            lock (b.Cs!.Gate) carrying = b.Cs.World.Ships.TryGetValue(b.ShipId, out var me) && me.CarriedFlagSideId >= 0;
            var thief = FlagThief(b.Cs, b.Side);
            candidates.Add(new(CookieOf(b.Cs), b.Side, b.Name, b.ShipId,
                b.Role.Equals("scout", StringComparison.OrdinalIgnoreCase), carrying, thief?.Id,
                thief is { } t ? FlagDistance(b, t.Sector, t.Position) : double.MaxValue,
                // All peers are native player pilots; treasure contact also
                // requires HabmRescue (Igc/treasureIGC.h:85-87).
                Catalog.RepresentativeCore().HullTypes[ActualHullId(b)].HasCapability(DataHullTypeIGC.HabmRescue)));
        }
        return candidates.ToArray();
    }

    // Same planner for standalone and supervisor: worker count cannot multiply
    // quotas or make a farther worker's guard outrank a nearer visible fighter.
    internal static FlagDeployment[] PlanFlagDuties(IEnumerable<FlagCandidate> candidates)
    {
        var result = new List<FlagDeployment>();
        foreach (var group in candidates.DistinctBy(c => (c.Cookie, c.Side, c.Name)).GroupBy(c => (c.Cookie, c.Side)))
        {
            var available = group.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
            var (raidCount, guardCount) = FlagShares(available.Count);
            void Assign(FlagCandidate c, FlagDuty duty, short? anchor = null, int post = 0)
            { result.Add(new(c.Cookie, c.Side, c.Name, duty, anchor, post)); available.Remove(c); }
            var carriers = available.Where(c => c.Carrying).ToArray();
            foreach (var c in carriers) Assign(c, FlagDuty.Return);
            var interceptors = available.Where(c => c.Thief is not null).OrderBy(c => c.Distance)
                .ThenBy(c => c.Name, StringComparer.Ordinal).Take(guardCount).ToArray();
            foreach (var c in interceptors) Assign(c, FlagDuty.Intercept, c.Thief);
            int post = 0;
            foreach (var c in available.OrderBy(c => c.CanCarry ? 1 : 0)
                         .Take(Math.Max(0, guardCount - interceptors.Length)).ToArray())
                Assign(c, FlagDuty.Guard, post: post++);
            var carrier = carriers.FirstOrDefault();
            int escorts = Math.Max(0, raidCount - carriers.Length);
            if (carrier is null)
            {
                carrier = available.Where(c => c.CanCarry).OrderBy(c => c.Scout ? 0 : 1)
                    .ThenBy(c => c.Name, StringComparer.Ordinal).FirstOrDefault();
                if (carrier is null) continue; // never send an incapable hull to a pickup
                Assign(carrier, FlagDuty.Raid);
                escorts--;
            }
            foreach (var c in available.OrderBy(c => c.Scout ? 0 : 1).ThenBy(c => c.Name, StringComparer.Ordinal)
                         .Take(escorts).ToArray())
                Assign(c, FlagDuty.Escort, carrier.ShipId);
        }
        return result.ToArray();
    }

    private readonly record struct FlagContact(short Id, short Sector, Vector3 Position);
    private static FlagContact? FlagThief(ConnectionSession cs, short side)
    {
        lock (cs.Gate)
        {
            var me = cs.World.MyShipId is { } id ? cs.World.Ships.GetValueOrDefault(id) : null;
            return cs.World.Ships.Values.Where(s => s.SideId >= 0 && s.SideId != side
                    && s.CarriedFlagSideId == side && FlagVisible(cs.World, s))
                .OrderBy(s => s.SectorId == me?.SectorId ? 0 : 1)
                .ThenBy(s => me?.Latest is { } p ? (s.Latest!.Position - p.Position).LengthSquared() : 0)
                .ThenBy(s => s.ShipId)
                .Select(s => (FlagContact?)new FlagContact(s.ShipId, s.SectorId, s.Latest!.Position)).FirstOrDefault();
        }
    }

    private static short? FlagEnemy(ConnectionSession cs, ActiveBot bot, short sector)
    {
        lock (cs.Gate)
            return cs.World.Ships.Values.Where(s => s.SideId >= 0 && s.SideId != bot.Side
                    && s.SectorId == sector && FlagVisible(cs.World, s)
                    && !Catalog.IsLifepodHull(s.HullId))
                .OrderBy(s => FlagDistance(bot, sector, s.Latest!.Position))
                .ThenBy(s => s.ShipId).Select(s => (short?)s.ShipId).FirstOrDefault();
    }

    private static bool FlagVisible(ClientWorld world, ShipEntity s) => !s.IsNotVisible && !s.IsGone && !s.IsDead
        && !s.Unknown && s.StatusSectorKnown && s.StationId < 0 && s.ParentId < 0
        && s.Latest is not null && world.PoseAgeSeconds(s.ShipId) <= MaxSnapshotAgeSeconds;

    private static double FlagDistance(ActiveBot bot, short sector, Vector3 point)
    {
        var cs = bot.Cs!;
        lock (cs.Gate)
        {
            if (!cs.World.Ships.TryGetValue(bot.ShipId, out var me) || me.Latest is not { } p) return double.MaxValue;
            if (me.SectorId == sector) return (p.Position - point).Length();
            var path = cs.World.Graph?.FindShortestPath(me.SectorId, sector);
            return path is { Count: > 0 } ? path.Count * 100_000.0 : double.MaxValue;
        }
    }

    private void TraceFlag(ActiveBot bot, FlagDuty duty, short? anchor, double now)
    {
        string trace = $"{duty}:{anchor}";
        if (bot.FlagTrace == trace) return;
        bot.FlagTrace = trace;
        Console.WriteLine($"[flags] t={now:F1} side={bot.Side} bot={bot.Name} ship={bot.ShipId} duty={duty} anchor={anchor}");
    }

    private bool FlagTravel(ActiveBot bot, ConnectionSession cs, short sector, double now)
    {
        short cur = CsSector(cs);
        if (cur == sector) return false;
        short next = ExplorationPlanner.NextHop(GraphOf(cs), cur, sector);
        if (next >= 0 && next != cur && TransitWarp(cs, bot, cur, next)) return true;
        // Topology is global; mouth coordinates must actually be discovered.
        SweepSector(bot, cs, cur, now, null, mouthTo: next);
        return true;
    }

    private bool TryDriveFlags(ActiveBot bot, ConnectionSession cs, double now)
    {
        if (!FlagsGame(cs)) return false;
        short carried;
        lock (cs.Gate)
        {
            carried = cs.World.Ships.TryGetValue(bot.ShipId, out var me) ? me.CarriedFlagSideId : (short)-1;
            if (me is { StationId: >= 0 } && bot.FlagTrace.StartsWith("Return:", StringComparison.Ordinal))
            {
                bot.MissionLaunchPending = true;
                bot.FlagTask = FlagDuty.Raid;
                bot.FlagTrace = "";
                return true;
            }
        }
        var duty = carried >= 0 ? FlagDuty.Return : bot.FlagTask == FlagDuty.Return ? FlagDuty.Raid : bot.FlagTask;
        if (duty == FlagDuty.None || HumanCommands(bot.Side) && bot.OrderedCommand is not null && carried < 0) return false;
        bot.TargetId = null;
        short cur = CsSector(cs);
        if (cur < 0) return false;
        if (duty == FlagDuty.Return)
        {
            // Score at OUR dockable station, not an ally/pedestal. No ripcord
            // (FedSrv.CPP:7720,13388,13407); no new hull/speed limit is imposed.
            DataStationIGC[] homes;
            lock (cs.Gate) homes = cs.World.Stations.Values.Where(s => s.SideId == bot.Side
                && s.Position is { Length: 3 } && Catalog.RepresentativeCore().StationTypes.TryGetValue(s.StationTypeId, out var st)
                && Catalog.RepresentativeCore().HullTypes.TryGetValue(ActualHullId(bot), out var hull)
                && st.HasCapability(hull.HasCapability(DataHullTypeIGC.HabmFighter)
                    ? DataStationTypeIGC.SabmLand : DataStationTypeIGC.SabmCapLand)).ToArray();
            var home = homes.OrderBy(s => FlagDistance(bot, s.ClusterId, Vec3(s.Position))).FirstOrDefault();
            TraceFlag(bot, duty, home.Position is null ? null : home.StationId, now);
            if (home.Position is null) { LoiterCombat(bot, now); return true; }
            if (FlagTravel(bot, cs, home.ClusterId, now)) return true;
            if (TryDockApproachControl(cs, bot, home.StationId, out var control, out int movement))
                cs.CommandControls(control, movement);
            else CombatFlyTo(bot, Vec3(home.Position), 0, 100, 0);
            return true;
        }
        if (duty == FlagDuty.Intercept)
        {
            var thief = FlagThief(cs, bot.Side);
            if (thief is { } target)
            {
                TraceFlag(bot, duty, target.Id, now);
                if (!FlagTravel(bot, cs, target.Sector, now))
                { bot.TargetId = target.Id; EngageShipAt(bot, cs, target.Id, now, 0, 0); }
                return true;
            }
            duty = FlagDuty.Guard; // sighting lost: return to the pedestal
        }
        if (duty == FlagDuty.Escort && bot.FlagAnchor is { } anchor)
        {
            FlagContact? mate = null;
            lock (cs.Gate)
                if (cs.World.Ships.TryGetValue(anchor, out var s) && s.SideId == bot.Side && !s.IsDead && s.StatusSectorKnown
                    && s.State == Allegiance.Core.Wire.ShipStatusMessage.StateFlying && s.ParentId < 0
                    && !Catalog.IsLifepodHull(s.HullId)
                    && (s.SectorId != cur || FlagVisible(cs.World, s)))
                    mate = new(anchor, s.SectorId, s.Latest?.Position ?? Vector3.Zero);
            if (mate is { } contact)
            {
                TraceFlag(bot, duty, anchor, now);
                if (!FlagTravel(bot, cs, contact.Sector, now))
                {
                    if (FlagEnemy(cs, bot, cur) is { } enemy)
                    { bot.TargetId = enemy; EngageShipAt(bot, cs, enemy, now, 0, 0); }
                    else CombatFlyTo(bot, contact.Position, 150, 400, 0);
                }
                return true;
            }
            duty = FlagDuty.Raid; // dead/unseen carrier: reacquire dropped/respawned flag
        }
        if (duty == FlagDuty.Raid)
        {
            TreasureEntity? flag;
            lock (cs.Gate) flag = cs.World.Treasures.Values.Where(t => t.Code == TreasureCode.Flag
                && t.PayloadId >= 0 && t.PayloadId != bot.Side && t.ClusterId == cur)
                .OrderBy(t => FlagDistance(bot, t.ClusterId, t.Position)).ThenBy(t => t.TreasureId).FirstOrDefault();
            if (flag is not null)
            {
                TraceFlag(bot, duty, flag.TreasureId, now);
                CombatFlyTo(bot, flag.Position, 0, 40, 0); // contact pickup; never hold outside its radius
                return true;
            }
        }
        DataStationIGC[] pedestals;
        lock (cs.Gate) pedestals = cs.World.Stations.Values.Where(s => (duty == FlagDuty.Guard ? s.SideId == bot.Side
                : s.SideId >= 0 && s.SideId != bot.Side) && s.Position is { Length: 3 }
            && Catalog.RepresentativeCore().StationTypes.TryGetValue(s.StationTypeId, out var st)
            && st.HasCapability(DataStationTypeIGC.SabmPedestal)).ToArray();
        var pedestal = duty == FlagDuty.Guard && pedestals.Length > 0
            ? pedestals.OrderBy(s => s.StationId).ElementAt(bot.FlagPost % pedestals.Length)
            : pedestals.OrderBy(s => FlagDistance(bot, s.ClusterId, Vec3(s.Position))).FirstOrDefault();
        TraceFlag(bot, duty, pedestal.Position is null ? null : pedestal.StationId, now);
        if (pedestal.Position is not null)
        {
            if (FlagTravel(bot, cs, pedestal.ClusterId, now)) return true;
            if (duty == FlagDuty.Guard)
            {
                // Guard a bounded volume; don't chase unrelated ships across the map.
                if (FlagEnemy(cs, bot, cur) is { } enemy && TryCsPose(cs, enemy, out var pos, out _)
                    && (pos - Vec3(pedestal.Position)).LengthSquared() < 2500 * 2500)
                { bot.TargetId = enemy; EngageShipAt(bot, cs, enemy, now, 0, 0); }
                else CombatFlyTo(bot, Vec3(pedestal.Position) + new Vector3(400, 0, 0), 100, 400, 0);
            }
            else SweepSector(bot, cs, cur, now, null); // missing flag: search instead of assuming a spawn coordinate
            return true;
        }
        // Find enemy homes from native CLUSTERINFO, then sweep for the actual
        // pedestal/treasure. No map reveal, server object reads, or debug orders.
        short homeSector;
        lock (_intel.Sync)
        {
            var intel = _intel.Get(CookieOf(cs), bot.Side);
            var graph = GraphOf(cs);
            short own = ResolveOwnHome(intel, graph, bot.Side);
            homeSector = duty == FlagDuty.Guard ? own : ResolveEnemyHome(intel, graph, own);
        }
        if (homeSector >= 0 && FlagTravel(bot, cs, homeSector, now)) return true;
        SweepSector(bot, cs, cur, now, null);
        return true;
    }
}
