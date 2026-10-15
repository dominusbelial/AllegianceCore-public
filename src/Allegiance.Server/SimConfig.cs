namespace Allegiance.Server;

/// <summary>P629 simulation rules + P667 intra-sector parallel simulation (see
/// docs/reports/P667_PARALLEL_SECTOR_SIM.md). Settable defaults survive source-generated
/// JSON deserialization of an absent sim section or an empty sim object.</summary>
public sealed class SimConfig
{
    /// <summary>native (default) or off. Does not disable out-of-bounds damage.</summary>
    public string CrowdingPenalty { get; set; } = "native";

    internal bool CrowdingEnabled => CrowdingPenalty switch
    {
        "native" => true,
        "off" => false,
        _ => throw new ArgumentException("sim.crowdingPenalty must be native|off")
    };

    /// <summary>
    /// Master switch. <c>false</c> (the default, and the meaning of an absent
    /// <c>sim</c> section) leaves the sector sim exactly as it ran before P667.
    /// <c>true</c> enables the parallel detection path (gated per detect).
    /// Overridable at boot by <c>ALLEGIANCE_SIM_PARALLEL_CELLS</c> (env wins).
    /// </summary>
    public bool ParallelSectorCells { get; set; }

    /// <summary>
    /// Cap on the parallel detection phase's degree of parallelism
    /// (<see cref="System.Threading.Tasks.Parallel.For"/> <c>MaxDegreeOfParallelism</c>).
    /// When set, it is clamped to <c>[1, ProcessorCount]</c>; when null, the
    /// <see cref="Allegiance.Core.Sim.ParallelSectorSim.ThreadCount"/> default
    /// (about half the logical processors) is used. Diagnostic only — it never
    /// changes which bytes are produced, only how fast.
    /// </summary>
    public int? ParallelSectorCellThreads { get; set; }
}
