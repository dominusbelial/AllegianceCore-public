using System.Net.Http;
using System.Security.Cryptography;

namespace Allegiance.Update;

/// <summary>Options for one <see cref="UpdateClient.CheckAndStageAsync"/> run.</summary>
public sealed record UpdateClientOptions
{
    /// <summary>The feed root URL (e.g. a GitHub Release asset base or a local test server).</summary>
    public required string FeedBaseUrl { get; init; }
    /// <summary>SubjectPublicKeyInfo PEM of the ECDSA P-256 feed signing key.</summary>
    public required string PublicKeyPem { get; init; }
    /// <summary>The application tree to compare against and (after restart) to update.</summary>
    public required string AppRoot { get; init; }
    /// <summary>The version this process reports as installed. When null, the
    /// state recorded by <see cref="UpdateState.ReadInstalledVersion"/> is used
    /// (falling back to 0.0.0 — never newer than any real feed version).</summary>
    public string? InstalledVersion { get; init; }
}

/// <summary>
/// P810 — the client half of the auto-update: fetch the feed index
/// (<c>latest.json</c>), verify the manifest signature, compute the delta
/// against the local tree (native's per-file CRC analysis,
/// <c>clintlib/AutoDownload.cpp:993+</c>), download ONLY the changed/missing
/// files into the staging area, verify every byte against the manifest, and
/// leave a pending marker for <see cref="UpdateApplier"/>. A tampered manifest
/// or file aborts the whole stage; the running build is never touched.
/// </summary>
public static class UpdateClient
{
    public static async Task<UpdateCheckResult> CheckAndStageAsync(
        UpdateClientOptions options, HttpClient http, TextWriter? log = null, CancellationToken ct = default)
    {
        try
        {
            string feed = options.FeedBaseUrl.TrimEnd('/');
            var installed = options.InstalledVersion ?? UpdateState.ReadInstalledVersion(options.AppRoot) ?? "0.0.0";

            // 1. Feed index (the AUTO_UPDATE_INFO analogue).
            using var indexResponse = await http.GetAsync($"{feed}/{UpdateFeed.IndexFile}", ct);
            indexResponse.EnsureSuccessStatusCode();
            var index = UpdateManifestBuilder.DeserializeIndex(await indexResponse.Content.ReadAsByteArrayAsync(ct))
                ?? throw new InvalidDataException($"{UpdateFeed.IndexFile} is not a valid feed index");
            if (string.IsNullOrWhiteSpace(index.Manifest) || string.IsNullOrWhiteSpace(index.Signature))
                throw new InvalidDataException("feed index is missing the manifest reference or signature");

            // 2. Version gate: only strictly newer versions are staged
            //    (native's lobby compared CRCs; the port compares versions —
            //    design (modernised), see docs/P810_AUTO_UPDATE_DESIGN.md §1).
            if (!TryParseVersion(index.Version, out var feedVersion) || !TryParseVersion(installed, out var localVersion))
                throw new InvalidDataException($"unparsable version (feed '{index.Version}', local '{installed}')");
            if (feedVersion <= localVersion)
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, index.Version);

            // 3. Manifest + signature over its exact bytes.
            using var manifestResponse = await http.GetAsync($"{feed}/{index.Manifest.TrimStart('/')}", ct);
            manifestResponse.EnsureSuccessStatusCode();
            byte[] manifestBytes = await manifestResponse.Content.ReadAsByteArrayAsync(ct);
            if (!UpdateSignature.Verify(manifestBytes, index.Signature, options.PublicKeyPem))
                throw new InvalidDataException("feed manifest failed its signature verification");
            var manifest = UpdateManifestBuilder.Deserialize(manifestBytes)
                ?? throw new InvalidDataException("feed manifest is not a valid manifest");
            if (manifest.Version != index.Version)
                throw new InvalidDataException(
                    $"feed manifest version '{manifest.Version}' does not match the index '{index.Version}'");

            // 4. Delta: hash the local tree, keep what differs or is missing.
            var stagingRoot = Path.Combine(UpdateState.StateDirectory(options.AppRoot), "staging");
            if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true);
            Directory.CreateDirectory(stagingRoot);

            var localHashes = HashLocalTree(options.AppRoot);
            var changed = new List<UpdateFileEntry>();
            long bytes = 0;
            foreach (var entry in manifest.Files)
            {
                if (!UpdatePathGuard.TryNormalize(entry.Path, out var safe))
                    throw new InvalidDataException($"manifest names an unsafe path: {entry.Path}");
                if (localHashes.TryGetValue(safe, out var localSha)
                    && string.Equals(localSha, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    continue; // unchanged — the delta: never re-downloaded

                using var fileResponse = await http.GetAsync($"{feed}/{entry.AssetName ?? safe}", ct);
                fileResponse.EnsureSuccessStatusCode();
                byte[] payload = await fileResponse.Content.ReadAsByteArrayAsync(ct);
                if (payload.Length != entry.Size)
                    throw new InvalidDataException($"{safe}: downloaded {payload.Length} bytes, manifest says {entry.Size}");
                string sha = Convert.ToHexStringLower(SHA256.HashData(payload));
                if (!string.Equals(sha, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{safe}: downloaded bytes failed their SHA-256 verification");

                var stagedPath = Path.Combine(stagingRoot, safe.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
                await File.WriteAllBytesAsync(stagedPath, payload, ct);
                UpdateManifestBuilder.ApplyMode(stagedPath, entry.UnixMode);
                changed.Add(entry with { Path = safe });
                bytes += payload.Length;
            }

            // 5. Stage: the verified manifest snapshot + pending marker, atomically.
            UpdateState.AtomicWrite(Path.Combine(UpdateState.StateDirectory(options.AppRoot), "staged-manifest.json"),
                UpdateManifestBuilder.Serialize(manifest));
            // Remove only files the PREVIOUS release installed that this one drops —
            // never a file the updater did not put there (logs, dumps, mods, local
            // config). A hand-installed tree has no installed manifest: remove nothing.
            var previous = UpdateState.ReadInstalledManifest(options.AppRoot);
            var newPaths = manifest.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            var obsolete = (previous?.Files ?? [])
                .Select(f => f.Path)
                .Where(p => !newPaths.Contains(p) && localHashes.ContainsKey(p) && !UpdateFeed.IsExcluded(p))
                .OrderBy(p => p, StringComparer.Ordinal).ToList();
            UpdateState.WritePending(options.AppRoot, new UpdatePendingUpdate(
                manifest.Version,
                changed.Select(f => f.Path).ToList(),
                obsolete));

            log?.WriteLine($"[update] staged {manifest.Version}: {changed.Count} changed file(s), {bytes} bytes; " +
                           $"{obsolete.Count} obsolete file(s) queued for removal");
            return new UpdateCheckResult(UpdateCheckStatus.Pending, manifest.Version, changed.Count, bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException
                                       or UnauthorizedAccessException or TaskCanceledException
                                       or System.Text.Json.JsonException or InvalidOperationException)
        {
            log?.WriteLine($"[update] check failed, keeping the current build: {ex.Message}");
            return new UpdateCheckResult(UpdateCheckStatus.Failed, options.FeedBaseUrl, Error: ex.Message);
        }
    }

    internal static bool TryParseVersion(string? text, out Version version)
        => Version.TryParse(text?.TrimStart('v', 'V'), out version!);

    private static Dictionary<string, string> HashLocalTree(string appRoot)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(appRoot)) return result;
        foreach (var path in Directory.EnumerateFiles(appRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(appRoot, path).Replace('\\', '/');
            if (UpdateFeed.IsExcluded(rel)) continue;
            if (rel.StartsWith($"{UpdateFeed.StateDir}/", StringComparison.OrdinalIgnoreCase)) continue;
            using var fs = File.OpenRead(path);
            result[rel] = Convert.ToHexStringLower(SHA256.HashData(fs));
        }
        return result;
    }
}
