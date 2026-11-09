using System.Text.Json;

namespace Allegiance.Update;

/// <summary>
/// P976 (P971-05) — the transaction journal written into
/// <c>&lt;appRoot&gt;/autoupdate/apply-journal.json</c> BEFORE the first file in
/// the application tree is touched. It is the record a crash, power loss or
/// kill mid-apply leaves behind, so the next start can put the tree back exactly
/// as it was (or finish an apply that had already committed) before the
/// application loads — the missing half of native's move-then-restart step
/// (<c>clintlib/AutoDownload.h:230-278</c> moved staged files one at a time with
/// no way back; only the single file it was registering was moved back,
/// <c>AutoDownload.h:262-268</c>).
///
/// Invariants:
/// <list type="bullet">
/// <item>Written before any application-tree mutation, in the
/// <c>backing-up</c> phase: the application tree is untouched until the phase is
/// flipped to <c>applying</c> after the last backup, so a crash during the
/// backups is simply abandoned (and a half-copied backup can never be restored
/// over an untouched file).</item>
/// <item><see cref="Phase"/> stays <c>applying</c> until the whole tree (and
/// <c>installed.json</c>) is new; only then is it rewritten as
/// <c>committed</c>. A recovery abandons <c>backing-up</c>, rolls back
/// <c>applying</c>, and completes <c>committed</c>.</item>
/// <item>Recovery is idempotent: restoring from a backup and deleting a created
/// file can be repeated after a crash during the recovery itself.</item>
/// </list>
/// </summary>
public sealed record UpdateApplyJournal
{
    /// <summary>Journal written, backups in progress: the app tree is still
    /// untouched, so recovery only abandons the attempt.</summary>
    public const string PhaseBackingUp = "backing-up";

    /// <summary>At least one target may be mutated: recovery must restore every backup.</summary>
    public const string PhaseApplying = "applying";

    /// <summary>The whole tree is new (and installed.json is written): recovery only cleans up.</summary>
    public const string PhaseCommitted = "committed";

    /// <summary>File name inside the state directory.</summary>
    public const string FileName = "apply-journal.json";

    /// <summary>Suffix of the same-directory temp file a replace (or a restore)
    /// writes before renaming it over the target, so a target is never observed
    /// half-written. On Linux the rename is what makes replacing a mapped,
    /// loaded assembly safe: the running image keeps the old inode.</summary>
    public const string TempSuffix = ".update-new";

    /// <summary>The release being applied (for the log and the outcome).</summary>
    public string Version { get; init; } = "";

    /// <summary><see cref="PhaseBackingUp"/>, <see cref="PhaseApplying"/> or
    /// <see cref="PhaseCommitted"/>.</summary>
    public string Phase { get; init; } = PhaseBackingUp;

    /// <summary>Files that exist and must be put back on rollback (replaced
    /// files, files queued for deletion, and <c>installed.json</c>), with the
    /// backup that holds their pre-apply bytes and Unix mode.</summary>
    public List<UpdateApplyJournalEntry> Restore { get; init; } = [];

    /// <summary>Files the apply created (they did not exist before): deleted on rollback.</summary>
    public List<string> Created { get; init; } = [];

    /// <summary>Directories the apply created (shallow first): removed on rollback when empty.</summary>
    public List<string> CreatedDirectories { get; init; } = [];

    /// <summary>True once every target is new — recovery must not undo it.</summary>
    public bool IsCommitted => string.Equals(Phase, PhaseCommitted, StringComparison.Ordinal);

    public static string PathFor(string appRoot)
        => Path.Combine(UpdateState.StateDirectory(appRoot), FileName);

    public static bool Exists(string appRoot) => File.Exists(PathFor(appRoot));

    /// <summary>Read the journal, or null when there is none (or it is unreadable
    /// — an unreadable journal must never block the boot; the pending marker is
    /// the only thing the applier acts on then).</summary>
    public static UpdateApplyJournal? Read(string appRoot)
    {
        try
        {
            var path = PathFor(appRoot);
            if (!File.Exists(path)) return null;
            var journal = JsonSerializer.Deserialize(File.ReadAllBytes(path),
                UpdateJsonContext.Default.UpdateApplyJournal);
            // A hand-edited journal must not turn recovery into a NullReferenceException.
            return journal is null
                ? null
                : journal with
                {
                    Restore = journal.Restore ?? [],
                    Created = journal.Created ?? [],
                    CreatedDirectories = journal.CreatedDirectories ?? [],
                };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal static void Write(string appRoot, UpdateApplyJournal journal)
    {
        Directory.CreateDirectory(UpdateState.StateDirectory(appRoot));
        UpdateState.AtomicWrite(PathFor(appRoot), JsonSerializer.SerializeToUtf8Bytes(
            journal, UpdateJsonContext.Default.UpdateApplyJournal));
    }

    internal static void Delete(string appRoot)
    {
        try { File.Delete(PathFor(appRoot)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* next boot retries */ }
    }
}

/// <summary>One restorable file: its path (relative to the application root),
/// the backup copy (relative to the state directory) and the Unix mode the file
/// had before the apply.</summary>
public sealed record UpdateApplyJournalEntry(string Path, string Backup, string UnixMode);

/// <summary>
/// P976 — the boundaries a fault can be injected at (the tests drive
/// <see cref="UpdateApplyTestSeam"/> through every one of them and require the
/// tree to come back byte-identical). Internal: a production build never sets the hook.
/// </summary>
internal enum UpdateApplyStep
{
    /// <summary>A staged file finished its pre-verification (no mutation yet).</summary>
    Verify,
    /// <summary>A backup copy was written (inside the state directory only).</summary>
    Backup,
    /// <summary>The replacement temp file got its final Unix mode (target untouched).</summary>
    Mode,
    /// <summary>A target was atomically replaced.</summary>
    AfterCopy,
    /// <summary>An obsolete file was deleted.</summary>
    AfterDelete,
    /// <summary>Every target is new; the journal is still <c>applying</c>.</summary>
    BeforeCommit,
    /// <summary>The journal says <c>committed</c> but installed.json is not written yet.</summary>
    AfterInstalled,
    /// <summary>The journal says <c>committed</c> (recovery only cleans up).</summary>
    AfterCommit,
}

/// <summary>P976 — a test-only fault hook. It is the seam that lets
/// `tests/Allegiance.Update.Tests` fail the apply at every boundary (before the
/// first copy, after k copies, during deletes, during mode set) and simulate a
/// process death mid-transaction. Production never assigns it; leaving it null
/// costs one null check per file.</summary>
internal static class UpdateApplyTestSeam
{
    internal static Action<UpdateApplyStep, string>? OnStep;

    internal static void Step(UpdateApplyStep step, string path) => OnStep?.Invoke(step, path);

    internal static void Reset() => OnStep = null;
}

/// <summary>P976 — path and Unix-mode helpers shared by the apply transaction
/// and its recovery, so neither can resolve a path outside the application root.</summary>
internal static class UpdateApplyPaths
{
    private static StringComparison Comparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Resolve a slash-separated relative path under <paramref name="root"/>,
    /// refusing anything that normalizes outside it (a hand-edited journal or
    /// marker must not write outside the application root).</summary>
    public static string Resolve(string root, string relPath)
    {
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, relPath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, Comparison))
            throw new InvalidDataException($"'{relPath}' resolves outside the application root");
        return full;
    }

    /// <summary>True when <paramref name="fullPath"/> is inside <paramref name="root"/>.</summary>
    public static bool IsUnder(string root, string fullPath)
    {
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(fullPath);
        var prefix = rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, Comparison);
    }

    /// <summary>Slash-separated path relative to the application root.</summary>
    public static string Relative(string root, string fullPath)
        => Path.GetRelativePath(Path.GetFullPath(root), fullPath).Replace('\\', '/');

    /// <summary>The file's current Unix mode as the manifest's octal string ("644");
    /// Windows has no exec bits, so it records the plain-file default.</summary>
    public static string CurrentMode(string path)
        => OperatingSystem.IsWindows()
            ? "644"
            : UpdateManifestBuilder.ModeToOctal(File.GetUnixFileMode(path));

    /// <summary>Copy over a target atomically (same-directory temp + rename), so
    /// an interrupted copy can never leave a half-written application file.</summary>
    public static void Replace(string source, string target, string octalMode)
    {
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        var temp = target + UpdateApplyJournal.TempSuffix;
        try
        {
            File.Copy(source, temp, overwrite: true);
            UpdateManifestBuilder.ApplyMode(temp, octalMode);
            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            DeleteIfExists(temp);
            throw;
        }
    }

    /// <summary>True when both files exist and hold the same bytes.</summary>
    public static bool SameContent(string left, string right)
    {
        if (!File.Exists(right)) return false;
        if (new FileInfo(left).Length != new FileInfo(right).Length) return false;
        using var a = File.OpenRead(left);
        using var b = File.OpenRead(right);
        Span<byte> ba = stackalloc byte[8192];
        Span<byte> bb = stackalloc byte[8192];
        while (true)
        {
            int na = a.ReadAtLeast(ba, ba.Length, throwOnEndOfStream: false);
            int nb = b.ReadAtLeast(bb, bb.Length, throwOnEndOfStream: false);
            if (na != nb) return false;
            if (na == 0) return true;
            if (!ba[..na].SequenceEqual(bb[..nb])) return false;
        }
    }

    public static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    public static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }
}

/// <summary>
/// P976 — Windows cannot replace a file that is mapped by the running process
/// (the client applies from inside its own <c>Program.Main</c>, so its own
/// assemblies are already loaded and locked; the server likewise). Detecting
/// that during pre-verification turns it into a clean "nothing changed" failure
/// instead of a mid-transaction error. Native's answer to the same lock was a
/// separate Reloader process that ran after the client exited
/// (<c>Reloader/Reloader.cpp:1-11,108-224</c>: it waited up to 25 s for the exe
/// to close its files). The port keeps that role in the packaging path: the
/// Windows installer replaces the tree while no client runs; the updater itself
/// never leaves a mixed tree and gives up after
/// <see cref="UpdateApplier.MaxApplyAttempts"/> attempts.
/// </summary>
internal static class UpdateApplyWindowsLock
{
    public static bool IsLockedByRunningProcess(string fullPath)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(fullPath)) return false;
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            foreach (System.Diagnostics.ProcessModule module in process.Modules)
            {
                if (string.Equals(module.FileName, fullPath, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                       or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception)
        {
            // Unknown => let the replace attempt decide (it fails with an
            // IOException and the transaction rolls back).
        }
        return false;
    }
}

/// <summary>What <see cref="UpdateApplyRecovery.Recover"/> did at boot.</summary>
internal enum UpdateRecoveryAction
{
    /// <summary>No journal at all: the normal path.</summary>
    None,
    /// <summary>An interrupted apply was rolled back; the tree is the old build.</summary>
    RolledBack,
    /// <summary>Rollback could not restore everything (a file stayed locked):
    /// the journal and backups are kept and the next start retries.</summary>
    RollbackIncomplete,
    /// <summary>The interrupted apply had already committed: the tree is the new build.</summary>
    Completed,
}

internal sealed record UpdateRecoveryResult(
    UpdateRecoveryAction Action, string Version, string Detail);

/// <summary>
/// P976 — boot-time recovery of an apply the previous process did not finish.
/// Runs before the pending marker is read (and therefore before anything in the
/// application loads): a <c>committed</c> journal is finished forward, an
/// <c>applying</c> journal is rolled back from its backups. A rolled-back
/// attempt counts as a failed apply for the retry cap (a crash loop must be
/// bounded exactly like a deterministic failure).
/// </summary>
internal static class UpdateApplyRecovery
{
    public static UpdateRecoveryResult Recover(string appRoot, TextWriter? log)
    {
        var journal = UpdateApplyJournal.Read(appRoot);
        if (journal is null) return new UpdateRecoveryResult(UpdateRecoveryAction.None, "", "");

        if (journal.IsCommitted)
        {
            // The whole tree and installed.json were new before the commit
            // marker: never undo a completed apply, just clear the state.
            UpdateState.ClearPending(appRoot);
            UpdateApplyJournal.Delete(appRoot);
            UpdateApplyPaths.TryDeleteDirectory(Path.Combine(UpdateState.StateDirectory(appRoot), "apply-backup"));
            UpdateApplyPaths.TryDeleteDirectory(Path.Combine(UpdateState.StateDirectory(appRoot), "staging"));
            log?.WriteLine($"[update] finished clearing an interrupted apply of {journal.Version} (the new build was committed)");
            return new UpdateRecoveryResult(UpdateRecoveryAction.Completed, journal.Version,
                "completed an already-committed apply");
        }

        if (string.Equals(journal.Phase, UpdateApplyJournal.PhaseBackingUp, StringComparison.Ordinal))
        {
            // No application file was touched yet: abandon the attempt, keep the
            // staged bytes, count it like any other failed apply.
            UpdateApplyJournal.Delete(appRoot);
            UpdateApplyPaths.TryDeleteDirectory(Path.Combine(UpdateState.StateDirectory(appRoot), "apply-backup"));
            var interrupted = UpdateState.ReadPending(appRoot);
            if (interrupted is not null) UpdateState.RecordApplyFailure(appRoot, interrupted, log);
            log?.WriteLine($"[update] abandoned the interrupted prepare of {journal.Version} " +
                           "(no application file was touched)");
            return new UpdateRecoveryResult(UpdateRecoveryAction.RolledBack, journal.Version,
                "abandoned an interrupted apply prepare; nothing was changed");
        }

        var (complete, detail) = Rollback(appRoot, journal, log);
        var pending = UpdateState.ReadPending(appRoot);
        if (pending is not null) UpdateState.RecordApplyFailure(appRoot, pending, log);

        if (!complete)
        {
            // Keep journal + backups: restoring is idempotent, the next start retries.
            log?.WriteLine($"[update] rollback of the interrupted apply of {journal.Version} is incomplete " +
                           $"({detail}); it will be retried on the next start");
            return new UpdateRecoveryResult(UpdateRecoveryAction.RollbackIncomplete, journal.Version,
                $"rollback incomplete: {detail}");
        }

        log?.WriteLine($"[update] rolled back the interrupted apply of {journal.Version}, keeping the current build");
        return new UpdateRecoveryResult(UpdateRecoveryAction.RolledBack, journal.Version,
            "rolled back an interrupted apply");
    }

    /// <summary>
    /// Put every backed-up file (content and Unix mode) back, delete every file
    /// the apply created, remove the directories it created and — only when
    /// nothing failed — the journal and the backup copies. Safe to run twice.
    /// </summary>
    internal static (bool Complete, string Detail) Rollback(
        string appRoot, UpdateApplyJournal journal, TextWriter? log)
    {
        var stateDir = UpdateState.StateDirectory(appRoot);
        int restored = 0, removed = 0, failures = 0;
        string firstError = "";
        // Before the phase is flipped to "applying" not one application file has
        // been touched, so its (possibly half-copied) backups must not be restored.
        bool mutated = !string.Equals(journal.Phase, UpdateApplyJournal.PhaseBackingUp, StringComparison.Ordinal);

        foreach (var entry in mutated ? journal.Restore : [])
        {
            try
            {
                var target = UpdateApplyPaths.Resolve(appRoot, entry.Path);
                var backup = UpdateApplyPaths.Resolve(stateDir, entry.Backup);
                if (File.Exists(backup))
                {
                    // A file that still holds its pre-apply bytes was never
                    // changed (the crash/failure hit before its mutation, e.g. a
                    // delete that failed): re-applying the mode is enough, and
                    // it keeps a read-only directory from turning an untouched
                    // file into a bogus "rollback incomplete".
                    if (!UpdateApplyPaths.SameContent(backup, target))
                    {
                        UpdateApplyPaths.Replace(backup, target, entry.UnixMode);
                        restored++;
                    }
                    if (!OperatingSystem.IsWindows())
                        UpdateManifestBuilder.ApplyMode(target, entry.UnixMode);
                }
                // No backup (the crash hit the backup phase): the target was
                // never mutated, so there is nothing to undo for it.
                UpdateApplyPaths.DeleteIfExists(target + UpdateApplyJournal.TempSuffix);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                failures++;
                if (firstError.Length == 0) firstError = ex.Message;
                log?.WriteLine($"[update] could not restore {entry.Path}: {ex.Message}");
            }
        }

        foreach (var rel in journal.Created)
        {
            try
            {
                var target = UpdateApplyPaths.Resolve(appRoot, rel);
                if (File.Exists(target)) { File.Delete(target); removed++; }
                UpdateApplyPaths.DeleteIfExists(target + UpdateApplyJournal.TempSuffix);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                failures++;
                if (firstError.Length == 0) firstError = ex.Message;
                log?.WriteLine($"[update] could not remove {rel}: {ex.Message}");
            }
        }

        // Deepest first, and only when empty: the player may have put files there.
        foreach (var rel in journal.CreatedDirectories
                     .OrderByDescending(d => d.Count(c => c == '/')).ThenByDescending(d => d, StringComparer.Ordinal))
        {
            try
            {
                var dir = UpdateApplyPaths.Resolve(appRoot, rel);
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // A directory left behind is cosmetic; the files are what matter.
                log?.WriteLine($"[update] left directory {rel} in place: {ex.Message}");
            }
        }

        if (failures == 0)
        {
            UpdateApplyPaths.DeleteIfExists(UpdateState.InstalledPath(appRoot) + ".tmp");
            UpdateApplyJournal.Delete(appRoot);
            UpdateApplyPaths.TryDeleteDirectory(Path.Combine(stateDir, "apply-backup"));
            return (true, $"restored {restored}, removed {removed}");
        }
        return (false, firstError);
    }
}
