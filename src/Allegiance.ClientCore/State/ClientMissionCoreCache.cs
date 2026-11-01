using Allegiance.Core;
using Allegiance.Core.Model;
using System.Security.Cryptography;
using System.Text;

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
        string key;
        if (file.Exists) key = $"client:{path}:{file.LastWriteTimeUtc.Ticks}:{file.Length}";
        else
        {
            string folder = StaticCoreCache.TextDirectory(path);
            if (!File.Exists(Path.Combine(folder, "Constants.csv"))) return null;
            // Text has no version stamp beyond 1337. Track the complete table
            // revision, keeping old worlds' leases independent (P904). This IO
            // is confined to text fallback; binary load cost is unchanged.
            var revision = new StringBuilder();
            foreach (var table in Directory.EnumerateFiles(folder).Where(p => p.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(p) == ".coremeta").Order(StringComparer.Ordinal))
            {
                var info = new FileInfo(table);
                revision.Append(info.Name).Append(':').Append(info.LastWriteTimeUtc.Ticks)
                    .Append(':').Append(info.Length).Append(';');
            }
            key = $"client:{path}:text:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revision.ToString())))}";
        }
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
