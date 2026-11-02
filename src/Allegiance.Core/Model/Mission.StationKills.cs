using Allegiance.Core.Sim;

namespace Allegiance.Core.Model;

public sealed partial class Mission
{
    /// <summary>Authored showcase/skirmish policy, never a player-created or LAN mission.</summary>
    public bool IsShowcasePreset { get; set; }

    private readonly Dictionary<short, int> _stationKills = new();
    public int StationKills(short sideId) => _stationKills.GetValueOrDefault(sideId);

    /// <summary>Destroyed bases without a hostile, real-side launcher (including collapse).
    /// Retained for display; these never credit an opponent or affect the ranking.</summary>
    public int UncreditedStationDestructions { get; private set; }

    // Preset design, not a native score replacement. Native's lethal transition is
    // Igc/stationIGC.cpp:189-194; side credit is FedSrv/FedSrv.CPP:12269-12273.
    // Unlike optional telemetry, this survives launcher retirement and is owned by
    // the victim's mission. A fresh mission after game over starts with zero counts.
    internal void RecordStationDestruction(Station station, Allegiance.Core.Sim.Model? launcher)
    {
        short? sideId = launcher is null ? null : EquipmentTarget.SideOf(launcher);
        if (sideId is { } id && Sides.Any(s => s.Id == id) &&
            !AreSidesFriendly(id, station.SideId))
            _stationKills[id] = StationKills(id) + 1;
        else
            UncreditedStationDestructions++;
    }

    public string[] StationKillSummary()
    {
        string tally = string.Join(", ", Sides.Select(s => $"{s.Name}={StationKills(s.Id)}"));
        string shattered = string.Join(", ", Sides.Where(s => StationKills(s.Id) > 0)
            .Select(s => $"{s.Name} ({StationKills(s.Id)})"));
        return [$"Station kills: {tally}",
            $"Enemy base shattered: {(shattered.Length == 0 ? "none" : shattered)}; uncredited={UncreditedStationDestructions}"];
    }
}
