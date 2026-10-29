namespace Allegiance.Server;

/// <summary>P629 simulation rules + P667 intra-sector parallel simulation (see
/// docs/reports/P667_PARALLEL_SECTOR_SIM.md). Settable defaults survive source-generated
/// JSON deserialization of an absent sim section or an empty sim object.</summary>
public sealed class SimConfig
{
    /// <summary>P817 native NPC GotoPlan (Igc/common.cpp:1157-2333). Default ON since the 2026-10-28 rig A/B
    /// (b52 vs b50); false keeps the legacy port steering. Env override wins.</summary>
    public bool NativeGotoPlan { get; set; } = true;

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
}
