namespace Allegiance.Server;

/// <summary>Modern bot policy experiments. P1000 only observes; P1001 will act.</summary>
public sealed class BotsConfig
{
    public bool CivStrategy { get; set; }
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
