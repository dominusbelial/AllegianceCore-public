using Allegiance.Core.Collision;

namespace Allegiance.Tools.McpSpawner;

/// <summary>P961 showcase policy. Pure decisions; no authoritative world reads.</summary>
internal static class FinaleAssembly
{
    internal readonly record struct Demand(int Required, int MinimumUseful, int Reachable,
        int ThreatExtra, StrikeVolley.Requirement Planner)
    {
        internal bool ReachableKill => Planner.LoadoutCount > 0 && Planner.CanKill
            && Reachable >= MinimumUseful;
    }

    internal static Demand Compute(StrikeVolley.StationProfile station,
        IReadOnlyList<StrikeVolley.Loadout?> loadouts, int reachable, double margin,
        int releaseLosses, int engaging, double landing, bool garrison = false)
    {
        // Search independently of the current live count. Otherwise a cap of two
        // would report "kill=2" even when those two cannot defeat the station.
        var planner = StrikeVolley.Compute(station, loadouts, StrikeVolley.MaxPlannedBombers,
            survivalMargin: margin, losses: releaseLosses, defenders: engaging, landingShare: landing);
        int minimum = Math.Max(garrison ? BotManager.FinaleMinimumStrike : 1,
            (int)Math.Ceiling(planner.KillBombers / planner.LandingShare));
        int cap = Math.Clamp(reachable, 0, StrikeVolley.MaxPlannedBombers);
        int extra = margin <= 0 ? 0 : (int)Math.Ceiling(margin * Math.Max(releaseLosses, engaging));
        return new(Math.Min(cap, planner.BombersRequired), minimum, cap, extra, planner);
    }

    internal static bool ThreatensPath(Vector3 defender, Vector3 staging, Vector3 release,
        float engagementRange, bool recentlyAttackedWing, bool canAttack = true)
    {
        if (recentlyAttackedWing) return true;
        if (!canAttack) return false;
        var path = release - staging;
        float length2 = path.LengthSquared();
        float t = length2 > 1 ? Math.Clamp((defender - staging) * path / length2, 0, 1) : 0;
        return (defender - (staging + path * t)).LengthSquared() <= engagementRange * engagementRange;
    }

    internal static bool CanArrive(bool alive, bool armed, bool reserved, bool docked,
        double travelSeconds, double now, double deadline)
        => alive && armed && reserved && !docked && double.IsFinite(travelSeconds)
            && travelSeconds >= 0 && now + travelSeconds + ResearchTactics.StrikeGoLeadSeconds
                + BotManager.FinaleWaveWindowSeconds <= deadline;

    internal static string? CommitReason(int assembled, Demand demand, double? stagingSince,
        double? lastGrowthAt, double now, double deadline, double plateauSeconds)
    {
        if (!demand.ReachableKill || assembled < demand.MinimumUseful || stagingSince is not { } since)
            return null;
        if (assembled >= demand.Required) return "massed";
        if (double.IsFinite(deadline) && now + ResearchTactics.StrikeGoLeadSeconds
            + BotManager.FinaleWaveWindowSeconds + BotManager.FinaleWaveTailSeconds >= deadline)
            return "deadline-forced";
        // Two release windows plus the go lead by default: long enough to see a
        // complete magazine/arrival cycle, measured from the last high-water mark.
        if (plateauSeconds > 0 && assembled > demand.Planner.KillBombers / demand.Planner.LandingShare
            && now - Math.Max(since, lastGrowthAt ?? since) >= plateauSeconds)
            return "plateau";
        return null;
    }

    internal static bool ShouldRefit(bool legalStrike, bool resupply, bool preparationUsed)
        => resupply || (!legalStrike && !preparationUsed);

    internal static bool OwnsDestination(bool reserved, bool escort, bool armed, bool livePlan)
        => (reserved || escort) && armed && livePlan;
}
