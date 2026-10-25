using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Allegiance.Update;

/// <summary>
/// P810 — source-generated JSON contract for the feed types, so the AOT-published
/// server (and every client) can read/write the feed without reflection
/// serialization (the repo's AOT rule — see <c>ServerJsonContext.cs</c>).
/// Serialization is camelCase, exactly what <c>tools/feedbuild</c> writes.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UpdateManifest))]
[JsonSerializable(typeof(UpdateFeedIndex))]
[JsonSerializable(typeof(UpdatePendingUpdate))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;

/// <summary>The feed layout constants (single source of truth for the
/// client, the server, <c>tools/feedbuild</c> and the release workflow).</summary>
public static class UpdateFeed
{
    /// <summary>The feed index file name at the feed root.</summary>
    public const string IndexFile = "latest.json";

    /// <summary>Per-application state directory, relative to the application root:
    /// installed version, staging area, pending marker.</summary>
    public const string StateDir = "autoupdate";

    /// <summary>The applier writes the installed version here after an apply.</summary>
    public const string InstalledManifestFile = "installed.json";

    /// <summary>The pending-update marker (what <see cref="UpdateApplier"/> consumes).</summary>
    public const string PendingFile = "pending.json";

    /// <summary>Feed sub-tree that is NEVER part of the application manifest:
    /// the HD texture pack stays a separate, explicit, optional download (the
    /// Meshy rule, P601 — the updater must never pull it implicitly).</summary>
    public static readonly string[] ExcludedPrefixes = ["packs/", "hd/"];

    /// <summary>True when <paramref name="relPath"/> (slash-separated, relative)
    /// is inside an excluded feed sub-tree.</summary>
    public static bool IsExcluded(string relPath)
    {
        foreach (var prefix in ExcludedPrefixes)
            if (relPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}

/// <summary>
/// P810 — build an <see cref="UpdateManifest"/> from a directory tree: every
/// regular file (recursively) except the excluded feed sub-trees
/// (<see cref="UpdateFeed.ExcludedPrefixes"/>), each with its SHA-256, size and
/// Unix mode. The modernisation of native's lobby-side <c>FileList.txt</c> CRC
/// computation (<c>lobby/AutoUpdate.cpp:72-76</c>).
/// </summary>
public static class UpdateManifestBuilder
{
    public static UpdateManifest Build(string rootDirectory, string version, DateTimeOffset? generatedAtUtc = null)
        => Build(rootDirectory, version, generatedAtUtc, assetPrefix: null);

    /// <summary>Build the manifest. With <paramref name="assetPrefix"/> (the
    /// GitHub-Release flat-asset style) every entry also records the flat
    /// <see cref="UpdateFileEntry.AssetName"/> the file is fetched under at the
    /// feed root.</summary>
    public static UpdateManifest Build(string rootDirectory, string version, DateTimeOffset? generatedAtUtc, string? assetPrefix)
    {
        var root = Path.GetFullPath(rootDirectory);
        var files = new List<UpdateFileEntry>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (UpdateFeed.IsExcluded(rel)) continue;
            if (rel.StartsWith($"{UpdateFeed.StateDir}/", StringComparison.OrdinalIgnoreCase)) continue;

            using var fs = File.OpenRead(path);
            string sha = Convert.ToHexStringLower(SHA256.HashData(fs));
            // Unix-only API; Windows files carry no exec bits, so the mode is
            // the "plain file" 644 default.
            var mode = OperatingSystem.IsWindows() ? UnixFileMode.UserRead | UnixFileMode.UserWrite : File.GetUnixFileMode(path);
            files.Add(new UpdateFileEntry(
                rel,
                new FileInfo(path).Length,
                sha,
                ModeToOctal(mode),
                assetPrefix is null ? null : assetPrefix + rel.Replace('/', '_')));
        }
        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new UpdateManifest
        {
            Version = version,
            GeneratedAtUtc = generatedAtUtc ?? DateTimeOffset.UtcNow,
            Files = files,
        };
    }

    /// <summary>Serialize a manifest canonically (camelCase, no indentation) —
    /// these are the exact bytes the release pipeline signs.</summary>
    public static byte[] Serialize(UpdateManifest manifest)
        => JsonSerializer.SerializeToUtf8Bytes(manifest, UpdateJsonContext.Default.UpdateManifest);

    /// <summary>Parse a manifest; returns null when the bytes are not one.</summary>
    public static UpdateManifest? Deserialize(ReadOnlySpan<byte> bytes)
    {
        try { return JsonSerializer.Deserialize(bytes, UpdateJsonContext.Default.UpdateManifest); }
        catch (JsonException) { return null; }
    }

    /// <summary>Serialize/deserialize the feed index the same way.</summary>
    public static byte[] SerializeIndex(UpdateFeedIndex index)
        => JsonSerializer.SerializeToUtf8Bytes(index, UpdateJsonContext.Default.UpdateFeedIndex);

    public static UpdateFeedIndex? DeserializeIndex(ReadOnlySpan<byte> bytes)
    {
        try { return JsonSerializer.Deserialize(bytes, UpdateJsonContext.Default.UpdateFeedIndex); }
        catch (JsonException) { return null; }
    }

    internal static string ModeToOctal(UnixFileMode mode)
    {
        int m = 0;
        if (mode.HasFlag(UnixFileMode.UserRead)) m |= 0b100_000_000;
        if (mode.HasFlag(UnixFileMode.UserWrite)) m |= 0b010_000_000;
        if (mode.HasFlag(UnixFileMode.UserExecute)) m |= 0b001_000_000;
        if (mode.HasFlag(UnixFileMode.GroupRead)) m |= 0b000_100_000;
        if (mode.HasFlag(UnixFileMode.GroupWrite)) m |= 0b000_010_000;
        if (mode.HasFlag(UnixFileMode.GroupExecute)) m |= 0b000_001_000;
        if (mode.HasFlag(UnixFileMode.OtherRead)) m |= 0b000_000_100;
        if (mode.HasFlag(UnixFileMode.OtherWrite)) m |= 0b000_000_010;
        if (mode.HasFlag(UnixFileMode.OtherExecute)) m |= 0b000_000_001;
        return Convert.ToString(m, 8);
    }

    /// <summary>Apply an octal mode string ("755") to a file on Unix; no-op on Windows.</summary>
    public static void ApplyMode(string path, string octalMode)
    {
        if (!OperatingSystem.IsWindows() && int.TryParse(octalMode, System.Globalization.NumberStyles.Integer, null, out int m))
            File.SetUnixFileMode(path, (UnixFileMode)(Convert.ToInt32(octalMode, 8) & 0b111_111_111));
    }
}

/// <summary>Normalize a relative path to forward slashes and reject escaping,
/// absolute, empty and excluded paths (a hostile manifest must not write outside
/// the application root — packaging's own manifest rule,
/// <c>packaging/test_packaging.py</c>, follows the same shape).</summary>
public static class UpdatePathGuard
{
    public static bool TryNormalize(string relPath, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(relPath)) return false;
        var candidate = relPath.Replace('\\', '/');
        if (candidate.StartsWith('/') || candidate.Contains(':')) return false;
        var parts = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;
        foreach (var part in parts)
        {
            if (part is "." or "..") return false;
            if (!FileSystemName.MatchesWin32Expression("*", part) || part.Contains('\0')) return false;
        }
        candidate = string.Join('/', parts);
        if (UpdateFeed.IsExcluded(candidate)) return false;
        normalized = candidate;
        return true;
    }
}
