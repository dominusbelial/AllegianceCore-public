namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>A local notice once per station type/model pair, shared by both HUD styles.</summary>
public sealed class StationFallbackWarnings
{
    private readonly HashSet<(short Type, string Stem)> _warned = [];

    public string? Take(short typeId, string typeName, string? modelStem)
    {
        string stem = string.IsNullOrWhiteSpace(modelStem) ? "<empty>" : modelStem.Trim();
        if (!_warned.Add((typeId, stem.ToLowerInvariant()))) return null;
        return $"Missing model '{stem}': {typeName} (type {typeId}); placeholder.";
    }
}
