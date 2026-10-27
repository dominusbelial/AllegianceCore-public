namespace Allegiance.Server;

/// <summary>P629 simulation rules + P667 intra-sector parallel simulation (see
/// docs/reports/P667_PARALLEL_SECTOR_SIM.md). Settable defaults survive source-generated
/// JSON deserialization of an absent sim section or an empty sim object.</summary>
public sealed class SimConfig
{
    /// <summary>P817 experimental native NPC GotoPlan. Default off; env override wins.</summary>
    public bool NativeGotoPlan { get; set; }

    internal bool ResolveNativeGotoPlan() => ResolveNativeGotoPlan(NativeGotoPlan,
        Environment.GetEnvironmentVariable("ALLEGIANCE_SIM_NATIVE_GOTOPLAN"));

    public static bool ResolveNativeGotoPlan(bool configured, string? environment) =>
        string.IsNullOrWhiteSpace(environment) ? configured
        : environment.Trim().ToLowerInvariant() is not ("0" or "false" or "off");
    /// <summary>P788c EXPERIMENTAL: send started-mission ship/status updates before
    /// Mission.Update (native FedSrv/FedSrv.CPP:4237-4254). Default off;
    /// ALLEGIANCE_SIM_NATIVE_BROADCAST_ORDER overrides this setting.</summary>
    public bool NativeBroadcastOrder { get; set; }

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
    /// P835 — the cross-side visibility sweep's native last-spotter
    /// short-circuit (FedSrv.CPP:10896-10903). Default OFF: the sweep runs the
    /// pre-P835 full scanner loop byte-for-byte. ON: the sweep first re-tests
    /// the target's recorded spotter for the side and skips the O(scanners)
    /// loop when it still sees the target — seen flags identical to the full
    /// loop, spotter identity the native sticky one (wire-observable in
    /// OBJECT_SPOTTED when several scanners see one target, so default-off
    /// pending a rig A/B). Overridable at boot by
    /// <c>ALLEGIANCE_VIS_SPOTTER_CACHE</c> (env wins).
    /// </summary>
    public bool VisibilitySpotterCache { get; set; }

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
