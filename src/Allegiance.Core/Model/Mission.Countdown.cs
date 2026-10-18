using Allegiance.Core.Sim;

namespace Allegiance.Core.Model;

public sealed partial class Mission
{
    /// <summary>Optional countdown match length and absolute start, in simulation seconds.
    /// Native FedSrv/fsmission.cpp:2339-2342.</summary>
    public double? CountdownDuration { get; set; }
    public double CountdownStart { get; set; }

    /// <summary>Strict timeout guard shared with the economic tick. Native
    /// FedSrv/fsmission.cpp:2339-2342,2508-2516 returns before payday on expiry.</summary>
    internal bool HasCountdownExpired(double now) => Stage == MissionStage.Started &&
        CountdownDuration is { } duration && double.IsFinite(duration) && duration > 0 &&
        now - CountdownStart > duration;

    /// <summary>Native timeout precedence, including a draw on unresolved ties
    /// (FedSrv/fsmission.cpp:2348-2504). The next criterion compares all sides,
    /// rather than restricting the field to the previous criterion's tied leaders.</summary>
    public (bool Expired, Side? Winner, string Criterion) EvaluateCountdown(double now)
    {
        if (!HasCountdownExpired(now)) return (false, null, "");

        Side? UniqueMaximum(Func<Side, long> score)
        {
            long maximum = 0;
            Side? winner = null;
            foreach (var side in Sides)
            {
                long value = score(side);
                if (value > maximum) { maximum = value; winner = side; }
                else if (value == maximum) winner = null;
            }
            return winner;
        }

        Side? selected;
        if (ConquestEnabled && (selected = UniqueMaximum(side =>
            Sectors.Sum(s => s.StationRuntimes.Count(st => st.SideId == side.Id &&
                st.Type.HasCapability(DataStationTypeIGC.SabmFlag))))) is not null)
            return (true, selected, "conquest");
        if (TerritoryEnabled && (selected = UniqueMaximum(s => s.TerritoryCount)) is not null)
            return (true, selected, "territory");
        if (ProsperityEnabled && (selected = UniqueMaximum(s => s.Buckets
            .FirstOrDefault(b => b.Development?.DevelopmentId == TeamMoneyDevelopmentId)?.InvestedMilliseconds ?? 0)) is not null)
            return (true, selected, "prosperity");
        if (ArtifactEnabled && (selected = UniqueMaximum(s => s.Artifacts)) is not null)
            return (true, selected, "artifacts");
        if (FlagsEnabled && (selected = UniqueMaximum(s => s.Flags)) is not null)
            return (true, selected, "flags");

        int kills = -1, deaths = int.MaxValue;
        selected = null;
        foreach (var side in Sides)
        {
            if (side.Kills > kills || (side.Kills == kills && side.Deaths < deaths))
            { kills = side.Kills; deaths = side.Deaths; selected = side; }
            else if (side.Kills == kills && side.Deaths == deaths) selected = null;
        }
        return (true, selected, "kills");
    }
}
