using Allegiance.Core;
using Allegiance.Core.Model;

namespace Allegiance.ClientCore.State;

/// <summary>Mission-owned named core leases. Native cache lookup precedes IO
/// and the last consumer retires the entry (missionigc.cpp:2390-2431,3033-3053).</summary>
internal static class ClientMissionCoreCache
{
    internal static StaticCoreCache.Lease? Acquire(string name)
    {
        // MISSIONDEF is remote input: a core name cannot escape the asset root.
        if (name.Length == 0 || name != Path.GetFileName(name) || name.Contains('\\')) return null;
        var root = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
        if (root.Length == 0) return null;
        var path = Path.Combine(root, name.EndsWith(".igc", StringComparison.OrdinalIgnoreCase) ? name : name + ".igc");
        // Client asset edits must reload even while another world owns the old
        // revision. Keep server named-core sharing separate from this file key.
        var file = new FileInfo(path);
        if (!file.Exists) return null;   // missing core: the resolver's null branch, not a throw
        string key = $"client:{path}:{file.LastWriteTimeUtc.Ticks}:{file.Length}";
        return StaticCoreCache.Shared.Load(key, path);
    }

    // Preserve the resolver delegate shape for custom hosts. The ordinary
    // mission-entry path acquires its lease directly, before assigning Core.
    internal static StaticCore? Resolve(string name)
    {
        using var lease = Acquire(name);
        return lease?.Core;
    }
}
