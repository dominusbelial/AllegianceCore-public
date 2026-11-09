using System.Globalization;
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
    IReadOnlyList<string> ObsoleteFiles,
    /// <summary>P976 — how many applies of this marker have failed already. A
    /// marker that keeps failing is discarded after
    /// <see cref="UpdateApplier.MaxApplyAttempts"/> so a bad stage can never
    /// boot-loop the client (0 for a freshly staged update and for markers
    /// written before P976, whose JSON simply has no <c>attempts</c> field).</summary>
    int Attempts = 0);

/// <summary>
/// P976 — the tombstone written when a staged update is discarded after
/// <see cref="UpdateApplier.MaxApplyAttempts"/> failed applies. The next feed
/// check refuses to stage that exact version again (one clear log line) instead
/// of re-downloading it every boot and re-entering the retry cycle; a newer
/// feed version stages normally and clears the tombstone.
/// </summary>
public sealed record UpdateDiscardRecord(string Version);

/// <summary>Reads/writes the pending marker and the installed-version record
/// under <see cref="UpdateFeed.StateDir"/>.</summary>
public static class UpdateState
{
    public static string StateDirectory(string appRoot) => Path.Combine(appRoot, UpdateFeed.StateDir);
    public static string PendingPath(string appRoot) => Path.Combine(StateDirectory(appRoot), UpdateFeed.PendingFile);
    public static string InstalledPath(string appRoot) => Path.Combine(StateDirectory(appRoot), UpdateFeed.InstalledManifestFile);
    public static string DiscardPath(string appRoot) => Path.Combine(StateDirectory(appRoot), UpdateFeed.DiscardFile);

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
        catch (UnauthorizedAccessException) { /* nothing staged */ }
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

    /// <summary>P976 — record one failed apply of <paramref name="pending"/>:
    /// either bump its attempt count (the next start retries with a clean tree)
    /// or, once <see cref="UpdateApplier.MaxApplyAttempts"/> is reached, discard
    /// the staged update — marker, staging bytes and staged manifest — with one
    /// clear log line, so a stage that can never apply stops occupying the boot
    /// path. The next feed check simply stages a fresh copy.</summary>
    internal static void RecordApplyFailure(string appRoot, UpdatePendingUpdate pending, TextWriter? log)
    {
        int attempts = pending.Attempts + 1;
        if (attempts >= UpdateApplier.MaxApplyAttempts)
        {
            ClearPending(appRoot);
            RecordDiscard(appRoot, pending.Version);
            if (!UpdateApplyJournal.Exists(appRoot)) ClearStaging(appRoot);
            log?.WriteLine($"[update] discarding the staged {pending.Version} update after {attempts} failed " +
                           $"attempt(s); staying on {ResolveInstalledVersion(appRoot)}. This version is not " +
                           "staged again — a newer release (or a repaired install) starts a fresh attempt");
            return;
        }

        WritePending(appRoot, pending with { Attempts = attempts });
        log?.WriteLine($"[update] apply attempt {attempts}/{UpdateApplier.MaxApplyAttempts} failed; " +
                       "the staged update will be retried on the next start");
    }

    /// <summary>P976 — the version a previous attempt gave up on, or null.</summary>
    internal static string? ReadDiscardedVersion(string appRoot)
    {
        try
        {
            var bytes = File.ReadAllBytes(DiscardPath(appRoot));
            return System.Text.Json.JsonSerializer.Deserialize(bytes,
                (System.Text.Json.Serialization.Metadata.JsonTypeInfo<UpdateDiscardRecord>)
                UpdateJsonSerializer.DiscardInfo)?.Version;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    internal static void RecordDiscard(string appRoot, string version)
    {
        try
        {
            Directory.CreateDirectory(StateDirectory(appRoot));
            AtomicWrite(DiscardPath(appRoot), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                new UpdateDiscardRecord(version), UpdateJsonSerializer.DiscardInfo));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    internal static void ClearDiscard(string appRoot)
    {
        try { File.Delete(DiscardPath(appRoot)); }
        catch (IOException) { /* nothing recorded */ }
        catch (UnauthorizedAccessException) { /* nothing recorded */ }
    }

    /// <summary>P976 — drop the staged download (verified bytes + manifest snapshot).</summary>
    internal static void ClearStaging(string appRoot)
    {
        var stateDir = StateDirectory(appRoot);
        UpdateApplyPaths.TryDeleteDirectory(Path.Combine(stateDir, "staging"));
        try { File.Delete(Path.Combine(stateDir, "staged-manifest.json")); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }
}

internal static class UpdateJsonSerializer
{
    // Small bridge so UpdateState can use the source-generated PendingUpdate
    // serializer without exposing the internal context.
    public static System.Text.Json.Serialization.Metadata.JsonTypeInfo<UpdatePendingUpdate> PendingInfo =>
        UpdateJsonContext.Default.UpdatePendingUpdate;

    public static System.Text.Json.Serialization.Metadata.JsonTypeInfo<UpdateDiscardRecord> DiscardInfo =>
        UpdateJsonContext.Default.UpdateDiscardRecord;
}

/// <summary>
/// P810 — apply-on-restart. Called once at the very top of the client's
/// <c>Program.Main</c> (before SDL/GL/audio) and by the server host before the
/// world loads.
///
/// P976 (P971-05) made the apply <b>failure-atomic</b>, so "keeping the current
/// build" is now literally true (native had no rollback at all:
/// <c>clintlib/AutoDownload.h:230-278</c> moved files one by one and left the
/// moved ones behind on an error):
///
/// <list type="number">
/// <item><b>Verify everything first.</b> Every staged path is normalized (no
/// escape, no state-dir path), must exist in the staged manifest, must match its
/// size and SHA-256, and — on Windows — must not be a module the running
/// process has mapped. Any failure returns before the first mutation.</item>
/// <item><b>Journal, then back up.</b> A recovery journal
/// (<see cref="UpdateApplyJournal"/>) is written into <c>autoupdate/</c> and
/// every file to be replaced or deleted — plus <c>installed.json</c> — is
/// copied into <c>autoupdate/apply-backup/</c> with its Unix mode.</item>
/// <item><b>Apply.</b> Each target is replaced through a same-directory temp
/// file and an atomic rename; obsolete files are deleted.</item>
/// <item><b>Commit.</b> <c>installed.json</c> is written, the journal moves to
/// <c>committed</c>, the marker is cleared and the backups are dropped.</item>
/// <item><b>Any error</b> restores every backup (content and mode), deletes
/// every file the apply created and counts the attempt; after
/// <see cref="MaxApplyAttempts"/> failing attempts the staged update is
/// discarded instead of retried forever.</item>
/// </list>
///
/// A crash anywhere in 2–4 leaves the journal, and the next start rolls it back
/// (or finishes a committed apply) before the application loads
/// (<see cref="UpdateApplyRecovery"/>).
/// </summary>
public static class UpdateApplier
{
    /// <summary>How many times a staged update may fail to apply before it is
    /// discarded (a crash-interrupted apply counts as one attempt). Three is
    /// enough for a transient error (disk full, AV lock) to clear while still
    /// bounding a boot loop to three starts.</summary>
    public const int MaxApplyAttempts = 3;

    public static UpdateApplyOutcome ApplyPendingIfAny(string appRoot, TextWriter? log = null)
    {
        var recovery = UpdateApplyRecovery.Recover(appRoot, log);
        switch (recovery.Action)
        {
            case UpdateRecoveryAction.Completed:
                return new UpdateApplyOutcome(true, recovery.Version, recovery.Detail);
            case UpdateRecoveryAction.RolledBack:
            case UpdateRecoveryAction.RollbackIncomplete:
                // Never write to the tree again in the same boot after a
                // rollback: a deterministic failure would just repeat, and an
                // incomplete rollback must not be buried under new mutations.
                return new UpdateApplyOutcome(false, recovery.Version, recovery.Detail);
        }

        var pending = UpdateState.ReadPending(appRoot);
        if (pending is null) return new UpdateApplyOutcome(false, null, "no pending update");
        return ApplyPending(appRoot, pending, log);
    }

    public static UpdateApplyOutcome ApplyPending(string appRoot, UpdatePendingUpdate pending, TextWriter? log = null)
    {
        if (UpdateApplyJournal.Exists(appRoot))
            return new UpdateApplyOutcome(false, pending.Version,
                "an interrupted apply is still journaled; it must be recovered first");

        var stateDir = UpdateState.StateDirectory(appRoot);
        UpdateApplyJournal? journal = null;
        try
        {
            // The staging manifest is the truth about what was verified at
            // download time; re-verify every staged file against it before
            // touching the application tree.
            var stagedManifest = UpdateManifestBuilder.Deserialize(
                File.ReadAllBytes(Path.Combine(stateDir, "staged-manifest.json")))
                ?? throw new InvalidDataException("staged manifest missing or unreadable");
            var byPath = stagedManifest.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);

            // ---- 1. Pre-verify the WHOLE staged set (no mutation). --------
            var installs = new List<(UpdateFileEntry Entry, string Rel, string Staged, string Target)>();
            var restorations = new List<(string Rel, string Mode)>();
            var creations = new List<string>();
            var obsolete = new List<string>();
            var planned = new HashSet<string>(StringComparer.Ordinal);

            foreach (var named in pending.Files)
            {
                if (!UpdatePathGuard.TryNormalize(named, out var safe))
                    throw new InvalidDataException($"pending update names an unsafe path: {named}");
                if (UpdateFeed.IsStatePath(safe))
                    throw new InvalidDataException($"pending update names the updater's own state: {named}");
                if (!planned.Add(safe)) continue;
                if (!byPath.TryGetValue(safe, out var entry))
                    throw new InvalidDataException(
                        $"pending update names {safe}, which the staged manifest does not carry");

                var staged = Path.Combine(stateDir, "staging", safe.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(staged))
                    throw new InvalidDataException($"staged file {safe} is missing");
                long length = new FileInfo(staged).Length;
                if (length != entry.Size)
                    throw new InvalidDataException(
                        $"staged file {safe} is {length} bytes, the manifest says {entry.Size}");
                using (var fs = File.OpenRead(staged))
                {
                    if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(fs)), entry.Sha256,
                                       StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"staged file {safe} failed its SHA-256 re-verification");
                }

                var target = UpdateApplyPaths.Resolve(appRoot, safe);
                if (UpdateApplyWindowsLock.IsLockedByRunningProcess(target))
                    throw new IOException($"{safe} is loaded by the running process and cannot be replaced on Windows");
                if (File.Exists(target)) restorations.Add((safe, UpdateApplyPaths.CurrentMode(target)));
                else creations.Add(safe);
                installs.Add((entry, safe, staged, target));
                UpdateApplyTestSeam.Step(UpdateApplyStep.Verify, safe);
            }

            foreach (var named in pending.ObsoleteFiles)
            {
                if (!UpdatePathGuard.TryNormalize(named, out var safe)) continue; // never outside the app root
                if (UpdateFeed.IsStatePath(safe)) continue;                       // never our own state
                if (!planned.Add(safe)) continue;                                 // replaced, not deleted
                var target = UpdateApplyPaths.Resolve(appRoot, safe);
                if (!File.Exists(target)) continue;                               // already gone
                restorations.Add((safe, UpdateApplyPaths.CurrentMode(target)));
                obsolete.Add(safe);
            }

            // installed.json is only written at commit, but a rollback must be
            // able to restore the old one: back it up like any other replace.
            var installedPath = UpdateState.InstalledPath(appRoot);
            var installedRel = UpdateApplyPaths.Relative(appRoot, installedPath);
            if (File.Exists(installedPath)) restorations.Add((installedRel, UpdateApplyPaths.CurrentMode(installedPath)));
            else creations.Add(installedRel);

            // ---- 2. Journal + backups (nothing in the app tree touched yet). --
            var backupDir = Path.Combine(stateDir, "apply-backup");
            UpdateApplyPaths.TryDeleteDirectory(backupDir);   // no journal => stale leftovers
            Directory.CreateDirectory(backupDir);

            var createdDirectories = new List<string>();
            foreach (var (_, _, _, target) in installs)
                CollectMissingDirectories(appRoot, target, createdDirectories);

            journal = new UpdateApplyJournal
            {
                Version = pending.Version,
                Phase = UpdateApplyJournal.PhaseBackingUp,
                Restore = restorations
                    .Select((r, i) => new UpdateApplyJournalEntry(r.Rel, $"apply-backup/{i}", r.Mode))
                    .ToList(),
                Created = creations,
                CreatedDirectories = createdDirectories,
            };
            UpdateApplyJournal.Write(appRoot, journal);

            for (int i = 0; i < restorations.Count; i++)
            {
                var (rel, mode) = restorations[i];
                var backup = Path.Combine(backupDir, i.ToString(CultureInfo.InvariantCulture));
                File.Copy(UpdateApplyPaths.Resolve(appRoot, rel), backup, overwrite: true);
                UpdateManifestBuilder.ApplyMode(backup, mode);
                UpdateApplyTestSeam.Step(UpdateApplyStep.Backup, rel);
            }

            // Every backup is on disk now: from here on the tree may be mutated,
            // so the journal must say so before the first target changes.
            journal = journal with { Phase = UpdateApplyJournal.PhaseApplying };
            UpdateApplyJournal.Write(appRoot, journal);

            // ---- 3. Apply: atomic replace per file, then the deletions. ------
            int applied = 0;
            foreach (var (entry, rel, staged, target) in installs)
            {
                var temp = target + UpdateApplyJournal.TempSuffix;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(staged, temp, overwrite: true);
                UpdateManifestBuilder.ApplyMode(temp, entry.UnixMode);
                UpdateApplyTestSeam.Step(UpdateApplyStep.Mode, rel);
                File.Move(temp, target, overwrite: true);
                applied++;
                UpdateApplyTestSeam.Step(UpdateApplyStep.AfterCopy, rel);
            }

            int removed = 0;
            foreach (var rel in obsolete)
            {
                File.Delete(UpdateApplyPaths.Resolve(appRoot, rel));
                removed++;
                UpdateApplyTestSeam.Step(UpdateApplyStep.AfterDelete, rel);
            }

            // ---- 4. Commit: installed.json, journal phase, then cleanup. -----
            UpdateApplyTestSeam.Step(UpdateApplyStep.BeforeCommit, pending.Version);
            UpdateState.WriteInstalled(appRoot, stagedManifest with { Version = pending.Version });
            UpdateApplyTestSeam.Step(UpdateApplyStep.AfterInstalled, pending.Version);
            journal = journal with { Phase = UpdateApplyJournal.PhaseCommitted };
            UpdateApplyJournal.Write(appRoot, journal);
            UpdateApplyTestSeam.Step(UpdateApplyStep.AfterCommit, pending.Version);

            UpdateState.ClearPending(appRoot);
            UpdateApplyJournal.Delete(appRoot);
            UpdateApplyPaths.TryDeleteDirectory(backupDir);
            UpdateApplyPaths.TryDeleteDirectory(Path.Combine(stateDir, "staging"));

            log?.WriteLine($"[update] applied {pending.Version}: {applied} file(s) replaced, {removed} removed");
            return new UpdateApplyOutcome(true, pending.Version, $"applied {applied}, removed {removed}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or KeyNotFoundException or System.Text.Json.JsonException
                                       or ArgumentException)
        {
            string detail = ex.Message;
            // A failure after the journal says "committed" (cleanup, the log
            // line) must never roll a complete new tree back or re-count the
            // attempt: the next boot's recovery finishes the cleanup forward.
            if (journal is { IsCommitted: true })
            {
                try { log?.WriteLine($"[update] applied {pending.Version}; cleanup is finished on the next start ({detail})"); }
                catch (IOException) { /* the log itself may be what failed */ }
                return new UpdateApplyOutcome(true, pending.Version, $"applied; cleanup deferred: {detail}");
            }
            // "Keeping the current build" is only reported once the tree really is
            // the old build: no journal means nothing was mutated, and a journal
            // must roll back completely before the claim is made.
            bool currentBuildKept = journal is null;
            if (journal is not null)
            {
                var (complete, rollbackDetail) = UpdateApplyRecovery.Rollback(appRoot, journal, log);
                currentBuildKept = complete;
                detail = complete
                    ? $"{ex.Message} (rolled back: {rollbackDetail})"
                    : $"{ex.Message} (rollback incomplete: {rollbackDetail})";
            }

            log?.WriteLine(currentBuildKept
                ? $"[update] apply failed, keeping the current build: {detail}"
                : $"[update] apply failed and its rollback could not finish ({detail}); it is retried before the app loads on the next start");
            UpdateState.RecordApplyFailure(appRoot, pending, log);
            return new UpdateApplyOutcome(false, pending.Version, detail);
        }
    }

    /// <summary>The directories a target's path needs that do not exist yet,
    /// shallow first, relative to the application root (rolled back when empty).</summary>
    private static void CollectMissingDirectories(string appRoot, string target, List<string> created)
    {
        var missing = new List<string>();
        for (var dir = Path.GetDirectoryName(target); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (Directory.Exists(dir) || !UpdateApplyPaths.IsUnder(appRoot, dir)) break;
            var rel = UpdateApplyPaths.Relative(appRoot, dir);
            if (rel is "" or ".") break;
            missing.Add(rel);
        }
        missing.Reverse();
        foreach (var rel in missing)
            if (!created.Contains(rel, StringComparer.Ordinal)) created.Add(rel);
    }
}

/// <summary>The result of an <see cref="UpdateApplier"/> run.</summary>
public sealed record UpdateApplyOutcome(bool Applied, string? Version, string Detail);
