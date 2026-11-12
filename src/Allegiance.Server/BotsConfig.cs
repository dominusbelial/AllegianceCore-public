namespace Allegiance.Server;

/// <summary>Default-off modern bot policy experiments.</summary>
public sealed class BotsConfig
{
    public bool CivStrategy { get; set; }
    public double CivStrategyCoverageShare { get; set; } = Allegiance.Core.Strategy.CivExecutionPlan.DefaultCoverageShare;
    public void ApplyEnvironment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        CivStrategy = value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "on" => true,
            "0" or "false" or "off" => false,
            _ => throw new InvalidDataException("ALLEGIANCE_BOTS_CIV_STRATEGY must be 0/1, false/true or off/on")
        };
    }
}
