using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Model;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    private sealed partial class ActiveBot
    {
        public int ArtifactSearchRound;
    }

    // A flag and an artifact occupy the SAME native cargo slot (FedSrv.CPP:13565-13568).
    private static bool IsObjectiveCargo(ConnectionSession cs, short flag) =>
        flag >= 0 || ArtifactsGame(cs) && flag == Side.ArtifactSentinelFlag;

    // Same offense/denial caps as flags, shared even in a combined game. Spare
    // denial slots stay in the normal fleet when no carrier is visible. Search
    // is persistent: artifact respawn is random, never a known pedestal.
    private static void PlanArtifactDuties(List<FlagCandidate> available, int offense, int denial,
        List<FlagDeployment> result)
    {
        void Assign(FlagCandidate c, FlagDuty duty, short? anchor = null, int post = 0)
        { result.Add(new(c.Cookie, c.Side, c.Name, duty, anchor, post)); available.Remove(c); }
        var returning = available.Where(c => c.Carrying).ToArray();
        foreach (var c in returning) Assign(c, FlagDuty.Return);
        offense = Math.Max(0, offense - returning.Length);
        FlagCandidate? collector = returning.FirstOrDefault();
        if (collector is null && offense > 0)
        {
            collector = available.Where(c => c.CanCarry).OrderBy(c => c.ArtifactDistance)
                .ThenBy(c => c.Scout ? 0 : 1).ThenBy(c => c.Name, StringComparer.Ordinal).FirstOrDefault();
            if (collector is not null) { Assign(collector, FlagDuty.Collect); offense--; }
        }
        foreach (var c in available.Where(c => c.Thief is not null).OrderBy(c => c.Distance)
                     .ThenBy(c => c.Name, StringComparer.Ordinal).Take(denial).ToArray())
            Assign(c, FlagDuty.Intercept, c.Thief);
        if (offense > 0 && available.Any(c => c.Flags && c.CanCarry))
        {
            var raider = available.Where(c => c.Flags && c.CanCarry).OrderBy(c => c.Scout ? 0 : 1)
                .ThenBy(c => c.Name, StringComparer.Ordinal).First();
            Assign(raider, FlagDuty.Raid); offense--;
        }
        if (offense > 0 && collector is not null && available.Count + result.Count(d =>
                d.Cookie == collector.Cookie && d.Side == collector.Side) >= 8)
        {
            var escort = available.OrderBy(c => c.Scout ? 1 : 0).ThenBy(c => c.Name, StringComparer.Ordinal).FirstOrDefault();
            if (escort is not null) { Assign(escort, FlagDuty.Escort, collector.ShipId); offense--; }
        }
        int post = 1;
        foreach (var c in available.Where(c => c.CanCarry).OrderBy(c => c.Scout ? 0 : 1)
                     .ThenBy(c => c.Name, StringComparer.Ordinal).Take(offense).ToArray())
            Assign(c, FlagDuty.Search, post: post++);
    }

    private void ObserveArtifacts(ActiveBot[] bots)
    {
        foreach (var bot in bots)
        {
            if (bot.Cs is not { } cs || !ArtifactsGame(cs)) continue;
            short sector;
            ArtifactSighting[] sightings;
            long stamp = Environment.TickCount64;
            lock (cs.Gate)
            {
                if (!cs.World.Ships.TryGetValue(bot.ShipId, out var me) || !me.StatusSectorKnown
                    || me.StationId >= 0 || me.IsDead || me.IsGone) continue;
                sector = me.SectorId;
                sightings = cs.World.Treasures.Values.Where(t => t.ClusterId == sector
                    && t.Code == TreasureCode.Flag && t.PayloadId == Side.ArtifactSentinelFlag)
                    .Select(t => { var p = t.Position; return new ArtifactSighting(t.TreasureId, sector,
                        p.X, p.Y, p.Z, stamp); }).ToArray();
            }
            _intel.Write(CookieOf(cs), bot.Side, intel => intel.ObserveArtifacts(sector, sightings, stamp));
        }
    }

    private FlagContact? NearestArtifact(ActiveBot bot)
    {
        var sightings = _intel.Read(CookieOf(bot.Cs!), bot.Side, intel => intel.Artifacts.Values
            .Where(a => a.Present && Environment.TickCount64 - a.AtMs < ArtifactSighting.RetentionMs).ToArray());
        return sightings.Select(a => new FlagContact(a.Id, a.Sector, new(a.X, a.Y, a.Z)))
            .Where(a => FlagDistance(bot, a.Sector, a.Position) < double.MaxValue)
            .OrderBy(a => FlagDistance(bot, a.Sector, a.Position)).ThenBy(a => a.Id)
            .Select(a => (FlagContact?)a).FirstOrDefault();
    }

    private bool DriveArtifactSearch(ActiveBot bot, ConnectionSession cs, double now, FlagDuty duty)
    {
        // Both searchers and collectors opportunistically recover a nearby drop.
        // The only source is a side member's TREASURE export, never server truth
        // (FedSrv/fsmission.cpp:4224-4231). GAIN_FLAG reveals identity, not position.
        if (NearestArtifact(bot) is { } artifact)
        {
            TraceFlag(bot, FlagDuty.Collect, artifact.Id, now);
            if (!FlagTravel(bot, cs, artifact.Sector, now))
                CombatFlyTo(bot, artifact.Position, 0, 40, 0);
            return true;
        }
        short cur = CsSector(cs);
        var graph = GraphOf(cs);
        var neutral = graph?.Clusters.Where(c => !c.HomeSector).OrderBy(c => c.Id)
            .Where(c => cur == c.Id || ExplorationPlanner.NextHop(graph, cur, c.Id) >= 0).Select(c => c.Id).ToArray() ?? [];
        if (neutral.Length == 0) { SweepSector(bot, cs, cur, now, null); return true; }
        short sector = neutral[(bot.FlagPost + bot.ArtifactSearchRound) % neutral.Length];
        TraceFlag(bot, duty, sector, now);
        if (FlagTravel(bot, cs, sector, now)) return true;
        int before = bot.SweepWaypoint;
        float initialRadius = SweepRadius(cs, sector, bot.Side, CookieOf(cs));
        var fc = Catalog.RepresentativeCore().Constants.FloatConstants;
        float bound = fc[1] > 0 && fc[2] > 0 ? fc[1] * MathF.Sqrt(fc[2]) : initialRadius;
        float radius = ArtifactSearchRadius(initialRadius, Math.Max(initialRadius, bound - ScoutOpening.SweepScanReach / 2),
            bot.ArtifactSearchRound, neutral.Length);
        SweepSector(bot, cs, sector, now, null, radiusOverride: radius);
        // A complete ring, not merely discovering the next mouth, completes a
        // search. Cycle forever: a secure respawns at a random neutral location
        // (FedSrv.CPP:13847-13870), including sectors searched earlier.
        if (before == Math.Max(6, ScoutOpening.SweepWaypoints) && bot.SweepWaypoint == 0)
        { bot.ArtifactSearchRound++; bot.SweepSector = -1; }
        return true;
    }
    // Native random placement increases its radius after rejected positions
    // (Igc/missionigc.cpp:4158-4187), so artifacts can lie beyond the ordinary
    // exploration ring. Empty full-map laps expand overlapping search bands,
    // bounded by the core's legal flight volume (shipIGC.cpp:368-408), then retry.
    internal static float ArtifactSearchRadius(float initial, float limit, int rounds, int sectors)
    {
        limit = Math.Max(initial, limit);
        int bands = 1 + (int)Math.Ceiling((limit - initial) / ScoutOpening.SweepScanReach);
        int band = rounds / Math.Max(1, sectors) % bands;
        return Math.Min(limit, initial + band * ScoutOpening.SweepScanReach);
    }

}
