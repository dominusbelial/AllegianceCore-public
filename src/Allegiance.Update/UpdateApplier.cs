using System.Security.Cryptography;

namespace Allegiance.Update;

/// <summary>
/// P810 — the pending-update marker written into
/// <c>&lt;appRoot&gt;/autoupdate/pending.json</c> after a feed check staged a
/// newer version. Consumed by <see cref="UpdateApplier"/> at the next process
/// start — the port's Reloader step (native set the working dir up for a
/// post-download relaunch, <c>clintlib/AutoDownload.cpp:225-240</c>, and set
/// <c>m_bNeedToRestart</c> when files had to be moved).
/// </summary>
public sealed record UpdatePendingUpdate(
    string Version,
    /// <summary>Staged file relative paths (all verified against the manifest).</summary>
    IReadOnlyList<string> Files,
    /// <summary>Files present locally but absent from the new manifest — removed on apply.</summary>
    IReadOnlyList<string> ObsoleteFiles);

/// <summary>Reads/writes the pending marker and the installed-version record
/// under <see cref="UpdateFeed.StateDir"/>.</summary>
public static class UpdateState
{
    public static string StateDirectory(string appRoot) => Path.Combine(appRoot, UpdateFeed.StateDir);
    public static string PendingPath(string appRoot) => Path.Combine(StateDirectory(appRoot), UpdateFeed.PendingFile);
    public static string InstalledPath(string appRoot) => Path.Combine(StateDirectory(appRoot), UpdateFeed.InstalledManifestFile);

    /// <summary>The installed version (written by the applier). Null when never updated.</summary>
    public static string? ReadInstalledVersion(string appRoot)
    {
        try
        {
            var manifest = UpdateManifestBuilder.Deserialize(File.ReadAllBytes(InstalledPath(appRoot)));
            return string.IsNullOrWhiteSpace(manifest?.Version) ? null : manifest!.Version;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The installed version with a fail-safe fallback: a tree the
    /// updater has never touched reports <c>0.0.0</c> (unknown), which any
    /// advertised current/minimum version outranks — the same fail-closed
    /// direction native's unknown LOGON CRC had (<c>lobby/client.cpp:272</c>).</summary>
    /// <summary>The manifest of the release the updater last installed, or null (a
    /// fresh, hand-installed tree).</summary>
    public static UpdateManifest? ReadInstalledManifest(string appRoot)
    {
        try
        {
            var path = InstalledPath(appRoot);
            return File.Exists(path) ? UpdateManifestBuilder.Deserialize(File.ReadAllBytes(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public static string ResolveInstalledVersion(string appRoot)
        => ReadInstalledVersion(appRoot) ?? "0.0.0";

    public static UpdatePendingUpdate? ReadPending(string appRoot)
    {
        try
        {
            var bytes = File.ReadAllBytes(PendingPath(appRoot));
            return System.Text.Json.JsonSerializer.Deserialize(bytes,
                (System.Text.Json.Serialization.Metadata.JsonTypeInfo<UpdatePendingUpdate>)
                UpdateJsonSerializer.PendingInfo);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public static void WritePending(string appRoot, UpdatePendingUpdate pending)
    {
        Directory.CreateDirectory(StateDirectory(appRoot));
        AtomicWrite(PendingPath(appRoot), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            pending, UpdateJsonSerializer.PendingInfo));
    }

    public static void ClearPending(string appRoot)
    {
        try { File.Delete(PendingPath(appRoot)); }
        catch (IOException) { /* nothing staged */ }
    }

    public static void WriteInstalled(string appRoot, UpdateManifest manifest)
    {
        Directory.CreateDirectory(StateDirectory(appRoot));
        AtomicWrite(InstalledPath(appRoot), UpdateManifestBuilder.Serialize(manifest));
    }

    internal static void AtomicWrite(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }
}

internal static class UpdateJsonSerializer
{
    // Small bridge so UpdateState can use the source-generated PendingUpdate
    // serializer without exposing the internal context.
    public static System.Text.Json.Serialization.Metadata.JsonTypeInfo<UpdatePendingUpdate> PendingInfo =>
        UpdateJsonContext.Default.UpdatePendingUpdate;
}

/// <summary>
/// P810 — apply-on-restart. Called once at the very top of the client's
/// <c>Program.Main</c> (before SDL/GL/audio) and by the server host before the
/// world loads: copies every staged file over the application tree (re-verifying
/// each SHA-256 first — never trust the staging area alone), re-applies Unix
/// modes, removes files the new manifest dropped, records the installed version
/// and clears the pending marker. Fail-safe by design: any error logs and the
/// previous build boots untouched.
/// </summary>
public static class UpdateApplier
{
    public static UpdateApplyOutcome ApplyPendingIfAny(string appRoot, TextWriter? log = null)
    {
        var pending = UpdateState.ReadPending(appRoot);
        if (pending is null) return new UpdateApplyOutcome(false, null, "no pending update");
        return ApplyPending(appRoot, pending, log);
    }

    public static UpdateApplyOutcome ApplyPending(string appRoot, UpdatePendingUpdate pending, TextWriter? log = null)
    {
        try
        {
            var stateDir = UpdateState.StateDirectory(appRoot);
            // The staging manifest is the truth about what was verified at
            // download time; re-verify every staged file against it before
            // touching the application tree.
            var stagedManifest = UpdateManifestBuilder.Deserialize(
                File.ReadAllBytes(Path.Combine(stateDir, "staged-manifest.json")))
                ?? throw new InvalidDataException("staged manifest missing or unreadable");

            var byPath = stagedManifest.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
            int applied = 0;
            foreach (var rel in pending.Files)
            {
                if (!UpdatePathGuard.TryNormalize(rel, out var safe))
                    throw new InvalidDataException($"pending update names an unsafe path: {rel}");
                var entry = byPath[safe];
                var stagedPath = Path.Combine(stateDir, "staging", safe.Replace('/', Path.DirectorySeparatorChar));
                using (var fs = File.OpenRead(stagedPath))
                {
                    string sha = Convert.ToHexStringLower(SHA256.HashData(fs));
                    if (!string.Equals(sha, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"staged file {safe} failed its SHA-256 re-verification");
                }
                var target = Path.Combine(appRoot, safe.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(stagedPath, target, overwrite: true);
                UpdateManifestBuilder.ApplyMode(target, entry.UnixMode);
                applied++;
            }

            int removed = 0;
            foreach (var rel in pending.ObsoleteFiles)
            {
                if (!UpdatePathGuard.TryNormalize(rel, out var safe)) continue;
                try { File.Delete(Path.Combine(appRoot, safe.Replace('/', Path.DirectorySeparatorChar))); removed++; }
                catch (IOException) { /* already gone / locked: harmless */ }
            }

            UpdateState.WriteInstalled(appRoot, stagedManifest with { Version = pending.Version });
            UpdateState.ClearPending(appRoot);
            try { Directory.Delete(Path.Combine(stateDir, "staging"), recursive: true); }
            catch (IOException) { /* best effort */ }

            log?.WriteLine($"[update] applied {pending.Version}: {applied} file(s) replaced, {removed} removed");
            return new UpdateApplyOutcome(true, pending.Version, $"applied {applied}, removed {removed}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or KeyNotFoundException or System.Text.Json.JsonException)
        {
            log?.WriteLine($"[update] apply failed, keeping the current build: {ex.Message}");
            return new UpdateApplyOutcome(false, pending.Version, ex.Message);
        }
    }
}

/// <summary>The result of an <see cref="UpdateApplier"/> run.</summary>
public sealed record UpdateApplyOutcome(bool Applied, string? Version, string Detail);
