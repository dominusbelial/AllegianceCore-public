using Allegiance.Core.Sim;

namespace Allegiance.Core.Model;

public sealed partial class Mission
{
    /// <summary>Loss of the sole restart-capable station eliminates its side. Native
    /// FedSrv/fsmission.cpp:3698-3713,5318-5378; this objective is a port presentation.</summary>
    public Station[] LastRestartBases() => Stage != MissionStage.Started ? [] : Sectors
        .SelectMany(s => s.StationRuntimes)
        .Where(st => !st.Dead && st.Type.HasCapability(DataStationTypeIGC.SabmRestart)
            && Sides.Any(side => side.Id == st.SideId && side.Active))
        .GroupBy(st => st.SideId).Where(g => g.Count() == 1)
        .Select(g => g.Single()).OrderBy(st => st.SideId).ToArray();
}
