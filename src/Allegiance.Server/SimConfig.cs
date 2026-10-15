namespace Allegiance.Server;

/// <summary>P629 simulation rules. Settable defaults survive source-generated
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
}
