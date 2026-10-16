using System.Collections.Concurrent;
using Allegiance.Core;
using Allegiance.Core.Model;

namespace Allegiance.ClientCore.State;

/// <summary>One immutable core per file/version, shared by bot sessions. Only
/// mission entry can load; telemetry handlers never perform file IO.</summary>
internal static class ClientMissionCoreCache
{
    private static readonly ConcurrentDictionary<string, Lazy<StaticCore>> Cores = new();

    internal static StaticCore? Resolve(string name)
    {
        // MISSIONDEF is remote input: a core name cannot escape the asset root.
        if (name.Length == 0 || name != Path.GetFileName(name) || name.Contains('\\')) return null;
        var root = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
        if (root.Length == 0) return null;
        var path = Path.Combine(root, name.EndsWith(".igc", StringComparison.OrdinalIgnoreCase) ? name : name + ".igc");
        if (!File.Exists(path)) return null;
        var stamp = File.GetLastWriteTimeUtc(path).Ticks;
        return Cores.GetOrAdd($"{path}:{stamp}", _ => new Lazy<StaticCore>(
            () => StaticCore.Load(IgcReader.Parse(File.ReadAllBytes(path))))).Value;
    }
}
