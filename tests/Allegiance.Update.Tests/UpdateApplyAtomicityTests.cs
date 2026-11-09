using System.Security.Cryptography;
using System.Text;
using Allegiance.Update;

namespace Allegiance.Update.Tests;

/// <summary>
/// P976 (P971-05) — apply-on-restart is failure-atomic. The apply must verify the
/// whole staged set first, then replace/delete through a backed-up transaction:
/// any error restores every file (content AND Unix mode) and removes every file
/// it created, so the running build is byte-identical. A journal written before
/// the first mutation lets the next boot roll back (or finish) an interrupted
/// apply. A pending update that keeps failing is discarded after
/// <see cref="UpdateApplier.MaxApplyAttempts"/> attempts.
///
/// Native intent: <c>clintlib/AutoDownload.h:230-278</c> moved staged files into
/// the application tree one at a time with no rollback (only the one file it was
/// registering was moved back), and <c>AutoDownload.cpp:964-978</c> could only
/// report "Error while moving downloaded files" — the era bug this fixes.
/// </summary>
public class UpdateApplyAtomicityTests : IDisposable
{
    private readonly string _appRoot = Path.Combine(
        Path.GetTempPath(), "p976-apply-" + Guid.NewGuid().ToString("N"));

    private readonly StringWriter _log = new();

    private static readonly string[] OldFiles = ["game.dll", "second.dll", "old.txt", "player-notes.txt", "launcher.sh"];

    public UpdateApplyAtomicityTests()
    {
        Directory.CreateDirectory(_appRoot);
        Write("game.dll", "old-game");
        Write("second.dll", "old-second");
        Write("old.txt", "old-only");
        Write("player-notes.txt", "mine");
        Write("launcher.sh", "#!/bin/sh\nrun old", "755");
        // The release this tree came from (P969 ships this in every bundle).
        UpdateState.WriteInstalled(_appRoot, new UpdateManifest
        {
            Version = "1.0.0",
            Files = OldFiles.Select(p => new UpdateFileEntry(p, 0, "")).ToList(),
        });
    }

    public void Dispose()
    {
        UpdateApplyTestSeam.Reset();
        try
        {
            if (!OperatingSystem.IsWindows())
                foreach (var dir in Directory.EnumerateDirectories(_appRoot, "*", SearchOption.AllDirectories)
                             .Prepend(_appRoot))
                {
                    try
                    {
                        File.SetUnixFileMode(dir,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
        }
        catch (DirectoryNotFoundException) { }
        try { Directory.Delete(_appRoot, recursive: true); } catch (IOException) { }
    }

    /// <summary>A plain exception the applier deliberately does not catch: the
    /// test's stand-in for a power loss / kill mid-transaction.</summary>
    private sealed class SimulatedProcessDeath : Exception;

    /// <summary>The reviewer's repro: a staged set whose second file is missing
    /// must not leave the first file applied.</summary>
    [Fact]
    public void Apply_with_a_missing_staged_file_changes_nothing()
    {
        var before = Snapshot();
        Stage("2.0.0", [("game.dll", "new-game"), ("second.dll", "new-second")], obsolete: ["old.txt"]);
        File.Delete(StagedPath("second.dll"));

        var outcome = UpdateApplier.ApplyPendingIfAny(_appRoot, _log);

        Assert.False(outcome.Applied, outcome.Detail);
        AssertUnchanged(before);
        Assert.Equal("1.0.0", UpdateState.ResolveInstalledVersion(_appRoot));
    }

    /// <summary>A staged file whose bytes no longer match the signed manifest
    /// must abort before the first copy — not after the first one.</summary>
    [Fact]
    public void Apply_with_a_tampered_staged_file_changes_nothing()
    {
        var before = Snapshot();
        Stage("2.0.0", [("game.dll", "new-game"), ("second.dll", "new-second")]);
        // Same length, different bytes: the SHA-256 re-verification is what catches it.
        File.WriteAllText(StagedPath("second.dll"), "new-sec0nd");

        var outcome = UpdateApplier.ApplyPendingIfAny(_appRoot, _log);

        Assert.False(outcome.Applied, outcome.Detail);
        AssertUnchanged(before);
        Assert.Equal("1.0.0", UpdateState.ResolveInstalledVersion(_appRoot));
    }

    /// <summary>An interrupted apply (journal on disk, tree half-mutated) is
    /// rolled back on the next boot before the application loads.</summary>
    [Fact]
    public void A_journal_left_by_a_crash_is_rolled_back_on_the_next_boot()
    {
        var before = Snapshot();
        // Simulate the half-applied state a crash leaves: game.dll already
        // replaced, a new file and a new directory created, plus a stray temp
        // file — and the journal that records how to undo it.
        var backup = Path.Combine(_appRoot, "autoupdate", "apply-backup", "0");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.WriteAllText(backup, "old-game");
        UpdateManifestBuilder.ApplyMode(backup, "644");
        File.WriteAllText(Path.Combine(_appRoot, "game.dll"), "new-game");
        Directory.CreateDirectory(Path.Combine(_appRoot, "newdir"));
        File.WriteAllText(Path.Combine(_appRoot, "newdir", "created.dll"), "brand-new");
        File.WriteAllText(Path.Combine(_appRoot, "game.dll.update-new"), "half-written");
        File.WriteAllText(Path.Combine(UpdateState.StateDirectory(_appRoot), "apply-journal.json"),
            """
            {"version":"2.0.0","phase":"applying",
             "restore":[{"path":"game.dll","backup":"apply-backup/0","unixMode":"644"}],
             "created":["newdir/created.dll"],
             "createdDirectories":["newdir"]}
            """);

        UpdateApplier.ApplyPendingIfAny(_appRoot, _log);

        AssertUnchanged(before);
        Assert.False(UpdateApplyJournal.Exists(_appRoot));
        Assert.False(Directory.Exists(Path.Combine(_appRoot, "newdir")));
        Assert.False(File.Exists(Path.Combine(_appRoot, "game.dll.update-new")));
        Assert.False(Directory.Exists(Path.Combine(_appRoot, "autoupdate", "apply-backup")));
        Assert.Equal("1.0.0", UpdateState.ResolveInstalledVersion(_appRoot));
    }

    /// <summary>The success path is unchanged: files replaced, obsolete files
    /// removed, modes preserved, installed.json written, marker + journal gone.</summary>
    [Fact]
    public void The_success_path_still_applies_everything()
    {
        Stage("2.0.0",
            [("game.dll", "new-game", "644"), ("second.dll", "new-second", "644"),
             ("launcher.sh", "#!/bin/sh\nrun new", "755")],
            obsolete: ["old.txt"]);

        var outcome = UpdateApplier.ApplyPendingIfAny(_appRoot, _log);

        Assert.True(outcome.Applied, outcome.Detail);
        Assert.Equal("2.0.0", outcome.Version);
        Assert.Equal("new-game", File.ReadAllText(Path.Combine(_appRoot, "game.dll")));
        Assert.Equal("new-second", File.ReadAllText(Path.Combine(_appRoot, "second.dll")));
        Assert.Equal("#!/bin/sh\nrun new", File.ReadAllText(Path.Combine(_appRoot, "launcher.sh")));
        Assert.False(File.Exists(Path.Combine(_appRoot, "old.txt")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(_appRoot, "player-notes.txt")));
        Assert.Equal("2.0.0", UpdateState.ResolveInstalledVersion(_appRoot));
        Assert.Null(UpdateState.ReadPending(_appRoot));
        Assert.False(UpdateApplyJournal.Exists(_appRoot));
        Assert.False(Directory.Exists(Path.Combine(_appRoot, "autoupdate", "apply-backup")));
        Assert.False(File.Exists(Path.Combine(_appRoot, "game.dll.update-new")));
        if (!OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(Path.Combine(_appRoot, "launcher.sh"))
                .HasFlag(UnixFileMode.UserExecute), "the exec bit must survive the transactional replace");
    }

    /// <summary>A pending update that keeps failing is discarded (not retried
    /// on every boot forever), and installed.json keeps the old version.</summary>
    [Fact]
    public void A_pending_update_that_keeps_failing_is_discarded_after_the_cap()
    {
        var before = Snapshot();
        Stage("2.0.0", [("game.dll", "new-game")]);
        File.Delete(StagedPath("game.dll"));   // every attempt fails the same way

        for (int attempt = 1; attempt <= UpdateApplier.MaxApplyAttempts; attempt++)
        {
            var outcome = UpdateApplier.ApplyPendingIfAny(_appRoot, _log);
            Assert.False(outcome.Applied, outcome.Detail);
            AssertUnchanged(before);
        }

        Assert.Null(UpdateState.ReadPending(_appRoot));
        Assert.False(Directory.Exists(Path.Combine(_appRoot, "autoupdate", "staging")));
        Assert.False(File.Exists(Path.Combine(_appRoot, "autoupdate", "staged-manifest.json")));
        Assert.Equal("1.0.0", UpdateState.ResolveInstalledVersion(_appRoot));
        Assert.Contains("discard", _log.ToString(), StringComparison.OrdinalIgnoreCase);
        // The marker really is gone: another boot changes nothing.
        Assert.False(UpdateApplier.ApplyPendingIfAny(_appRoot, _log).Applied);
        AssertUnchanged(before);
    }

    /// <summary>Failing before the first copy (a staged file that cannot be
    /// read) must not mutate anything — the transaction has not started.</summary>
    [Fact]
    public void A_failure_before_the_first_copy_changes_nothing()
    {
        var before = Snapshot();
        Stage("2.0.0", [("game.dll", "new-game"), ("second.dll", "new-second")]);
        UpdateApplyTestSeam.OnStep = (step, _) =>
        {
            if (step == UpdateApplyStep.Verify) throw new IOException("injected: verify");
        };

        var outcome = UpdateApplier.ApplyPendingIfAny(_appRoot, _log);

        Assert.False(outcome.Applied, outcome.Detail);
        AssertUnchanged(before);
        Assert.Equal("1.0.0", UpdateState.ResolveInstalledVersion(_appRoot));
        Assert.False(UpdateApplyJournal.Exists(_appRoot));
        Assert.Equal(1, UpdateState.ReadPending(_appRoot)!.Attempts);
    }

    /// <summary>Failing after k copies restores every already-replaced file and
    /// removes every file the apply created (the reviewer's mixed-tree case,
    /// with the failure moved one boundary later).</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_failure_after_k_copies_restores_everything(int k)
    {
        var before = Snapshot();
        Stage("2.0.0",
            [("game.dll", "new-game", "644"), ("second.dll", "new-second", "644"),
             ("launcher.sh", "#!/bin/sh\nrun new", "755"), ("newdir/created.dll", "brand-new", "644")],
            obsolete: ["old.txt"]);
        int copies = 0;
        UpdateApplyTestSeam.OnStep = (step, _) =>
        {
            if (step == UpdateApplyStep.AfterCopy && ++copies == k) throw new IOException($"injected: after copy {k}");
        };

        var outcome = UpdateApplier.ApplyPendingIfAny(_appRoot, _log);

        Assert.False(outcome.Applied, outcome.Detail);
        AssertUnchanged(before);
        Assert.Equal("1.0.0", UpdateState.ResolveInstalledVersion(_appRoot));
        Assert.False(UpdateApplyJournal.Exists(_appRoot));
        Assert.False(Directory.Exists(Path.Combine(_appRoot, "autoupdate", "apply-backup")));
        Assert.False(Directory.Exists(Path.Combine(_appRoot, "newdir")), "a created directory is removed on rollback");
        Assert.Equal(1, UpdateState.ReadPending(_appRoot)!.Attempts);
    }

    /// <summary>Failing during the obsolete-file deletes restores the deleted file.</summary>
    [Fact]
    public void A_failure_during_the_deletes_restores_the_deleted_file()
    {
        var before = Snapshot();
        Stage("2.0.0", [("game.dll", "new-game")], obsolete: ["old.txt", "second.dll"]);
        UpdateApplyTestSeam.OnStep = (step, _) =>
        {
            if (step == UpdateApplyStep.AfterDelete) throw new IOException("injected: delete");
        };

        var outcome = UpdateApplier.ApplyPendingIfAny(_appRoot, _log);

        Assert.False(outcome.Applied, outcome.Detail);
        AssertUnchanged(before);
        Assert.False(UpdateApplyJournal.Exists(_appRoot));
    }

    /// <summary>An obsolete file that cannot be deleted (its directory is not
    /// writable) must roll the whole apply back — the reviewer's repro with the
    /// failure in the delete phase instead of the copy phase.</summary>
    [Fact]
    public void An_undeletable_obsolete_file_rolls_the_apply_back()
    {
        if (OperatingSystem.IsWindows()) return;   // chmod-based fault injection

        Write("locked/old.dat", "old-only-in-a-locked-dir");
        var before = Snapshot();
        Stage("2.0.0", [("game.dll", "new-game")], obsolete: ["locked/old.dat", "old.txt"]);
        var lockedDir = Path.Combine(_appRoot, "locked");
        File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var outcome = UpdateApplier.ApplyPendingIfAny(_appRoot, _log);

            Assert.False(outcome.Applied, outcome.Detail);
            AssertUnchanged(before);
            Assert.Equal("1.0.0", UpdateState.ResolveInstalledVersion(_appRoot));
            Assert.False(UpdateApplyJournal.Exists(_appRoot));
        }
        finally
        {
            File.SetUnixFileMode(lockedDir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>Failing while setting the Unix mode (before the rename) restores
    /// the earlier copies and every mode.</summary>
    [Fact]
    public void A_failure_during_the_mode_set_restores_everything()
    {
        var before = Snapshot();
        Stage("2.0.0",
            [("launcher.sh", "#!/bin/sh\nrun new", "755"), ("game.dll", "new-game", "644")]);
        UpdateApplyTestSeam.OnStep = (step, _) =>
        {
            if (step == UpdateApplyStep.Mode) throw new IOException("injected: chmod");
        };

        var outcome = UpdateApplier.ApplyPendingIfAny(_appRoot, _log);

        Assert.False(outcome.Applied, outcome.Detail);
        AssertUnchanged(before);
    }

    /// <summary>A failure while taking the backups (still inside autoupdate/)
    /// leaves the application tree untouched.</summary>
    [Fact]
    public void A_failure_during_the_backups_changes_nothing()
    {
        var before = Snapshot();
        Stage("2.0.0", [("game.dll", "new-game")]);
        UpdateApplyTestSeam.OnStep = (step, _) =>
        {
            if (step == UpdateApplyStep.Backup) throw new IOException("injected: backup");
        };

        var outcome = UpdateApplier.ApplyPendingIfAny(_appRoot, _log);

        Assert.False(outcome.Applied, outcome.Detail);
        AssertUnchanged(before);
        Assert.False(UpdateApplyJournal.Exists(_appRoot));
    }

    /// <summary>A pending marker naming the updater's own state (or an escaping
    /// path) is refused before anything is touched.</summary>
    [Theory]
    [InlineData("autoupdate/installed.json")]
    [InlineData("../escape.dll")]
    public void A_marker_naming_an_out_of_bounds_path_is_refused(string rel)
    {
        var before = Snapshot();
        Stage("2.0.0", [("game.dll", "new-game")]);
        UpdateState.WritePending(_appRoot, new UpdatePendingUpdate("2.0.0", [rel], []));

        var outcome = UpdateApplier.ApplyPendingIfAny(_appRoot, _log);

        Assert.False(outcome.Applied, outcome.Detail);
        AssertUnchanged(before);
        Assert.Equal("1.0.0", UpdateState.ResolveInstalledVersion(_appRoot));
    }

    /// <summary>A process death mid-apply leaves the journal, the tree is
    /// rolled back by the next boot, and the same update then applies cleanly.</summary>
    [Fact]
    public void A_crash_mid_apply_is_rolled_back_by_the_next_boot()
    {
        var before = Snapshot();
        Stage("2.0.0",
            [("game.dll", "new-game"), ("second.dll", "new-second"), ("newdir/created.dll", "brand-new")],
            obsolete: ["old.txt"]);
        int copies = 0;
        UpdateApplyTestSeam.OnStep = (step, _) =>
        {
            if (step == UpdateApplyStep.AfterCopy && ++copies == 2) throw new SimulatedProcessDeath();
        };

        Assert.Throws<SimulatedProcessDeath>(() => UpdateApplier.ApplyPendingIfAny(_appRoot, _log));

        // The "crash" left the half-applied tree and the journal on disk.
        Assert.True(UpdateApplyJournal.Exists(_appRoot));
        Assert.Equal("new-game", File.ReadAllText(Path.Combine(_appRoot, "game.dll")));

        UpdateApplyTestSeam.Reset();
        var boot = UpdateApplier.ApplyPendingIfAny(_appRoot, _log);

        Assert.False(boot.Applied, boot.Detail);   // rolled back, not committed
        AssertUnchanged(before);
        Assert.False(UpdateApplyJournal.Exists(_appRoot));
        Assert.False(Directory.Exists(Path.Combine(_appRoot, "newdir")));
        Assert.Equal(1, UpdateState.ReadPending(_appRoot)!.Attempts);
        Assert.Equal("1.0.0", UpdateState.ResolveInstalledVersion(_appRoot));

        // The retry (the boot after that) installs normally.
        var applied = UpdateApplier.ApplyPendingIfAny(_appRoot, _log);
        Assert.True(applied.Applied, applied.Detail);
        Assert.Equal("2.0.0", UpdateState.ResolveInstalledVersion(_appRoot));
        Assert.Equal("new-game", File.ReadAllText(Path.Combine(_appRoot, "game.dll")));
        Assert.False(File.Exists(Path.Combine(_appRoot, "old.txt")));
    }

    /// <summary>A crash during the backup copies leaves a journal in the
    /// prepare phase: the next boot abandons it WITHOUT restoring the (possibly
    /// half-copied) backups over an untouched tree.</summary>
    [Fact]
    public void A_crash_during_the_backups_is_abandoned_without_touching_the_tree()
    {
        var before = Snapshot();
        Stage("2.0.0", [("game.dll", "new-game"), ("second.dll", "new-second")]);
        UpdateApplyTestSeam.OnStep = (step, _) =>
        {
            if (step == UpdateApplyStep.Backup) throw new SimulatedProcessDeath();
        };

        Assert.Throws<SimulatedProcessDeath>(() => UpdateApplier.ApplyPendingIfAny(_appRoot, _log));
        var journal = UpdateApplyJournal.Read(_appRoot);
        Assert.NotNull(journal);
        Assert.Equal(UpdateApplyJournal.PhaseBackingUp, journal!.Phase);

        UpdateApplyTestSeam.Reset();
        var boot = UpdateApplier.ApplyPendingIfAny(_appRoot, _log);

        Assert.False(boot.Applied, boot.Detail);
        AssertUnchanged(before);
        Assert.False(UpdateApplyJournal.Exists(_appRoot));
        Assert.Equal(1, UpdateState.ReadPending(_appRoot)!.Attempts);
        Assert.Equal("1.0.0", UpdateState.ResolveInstalledVersion(_appRoot));
    }

    /// <summary>A crash after installed.json but before the commit marker rolls
    /// back BOTH the tree and installed.json (version stays the old one).</summary>
    [Fact]
    public void A_crash_after_installed_json_restores_the_old_version()
    {
        var before = Snapshot();
        Stage("2.0.0", [("game.dll", "new-game")]);
        UpdateApplyTestSeam.OnStep = (step, _) =>
        {
            if (step == UpdateApplyStep.AfterInstalled) throw new SimulatedProcessDeath();
        };

        Assert.Throws<SimulatedProcessDeath>(() => UpdateApplier.ApplyPendingIfAny(_appRoot, _log));
        Assert.Equal("2.0.0", UpdateState.ResolveInstalledVersion(_appRoot));   // half-committed

        UpdateApplyTestSeam.Reset();
        var boot = UpdateApplier.ApplyPendingIfAny(_appRoot, _log);

        Assert.False(boot.Applied, boot.Detail);
        AssertUnchanged(before);
        Assert.Equal("1.0.0", UpdateState.ResolveInstalledVersion(_appRoot));
        Assert.False(UpdateApplyJournal.Exists(_appRoot));
    }

    /// <summary>A crash after the commit marker is finished forward: the new
    /// build is kept, the marker cleared, the journal dropped.</summary>
    [Fact]
    public void A_crash_after_the_commit_marker_keeps_the_new_build()
    {
        Stage("2.0.0", [("game.dll", "new-game")], obsolete: ["old.txt"]);
        UpdateApplyTestSeam.OnStep = (step, _) =>
        {
            if (step == UpdateApplyStep.AfterCommit) throw new SimulatedProcessDeath();
        };

        Assert.Throws<SimulatedProcessDeath>(() => UpdateApplier.ApplyPendingIfAny(_appRoot, _log));
        Assert.True(UpdateApplyJournal.Exists(_appRoot));

        UpdateApplyTestSeam.Reset();
        var boot = UpdateApplier.ApplyPendingIfAny(_appRoot, _log);

        Assert.True(boot.Applied, boot.Detail);
        Assert.Equal("2.0.0", UpdateState.ResolveInstalledVersion(_appRoot));
        Assert.Equal("new-game", File.ReadAllText(Path.Combine(_appRoot, "game.dll")));
        Assert.False(File.Exists(Path.Combine(_appRoot, "old.txt")));
        Assert.Null(UpdateState.ReadPending(_appRoot));
        Assert.False(UpdateApplyJournal.Exists(_appRoot));
        Assert.False(Directory.Exists(Path.Combine(_appRoot, "autoupdate", "apply-backup")));
    }

    /// <summary>Review fix: an ordinary I/O error AFTER the commit marker (cleanup,
    /// the log line) must not roll the complete new tree back or count a failed
    /// attempt; the next boot finishes the cleanup forward.</summary>
    [Fact]
    public void An_io_error_after_the_commit_marker_keeps_the_new_build()
    {
        Stage("2.0.0", [("game.dll", "new-game"), ("added.dll", "brand-new")], obsolete: ["old.txt"]);
        UpdateApplyTestSeam.OnStep = (step, _) =>
        {
            if (step == UpdateApplyStep.AfterCommit) throw new IOException("disk hiccup during cleanup");
        };

        var outcome = UpdateApplier.ApplyPendingIfAny(_appRoot, _log);

        Assert.True(outcome.Applied, outcome.Detail);
        Assert.Equal("new-game", File.ReadAllText(Path.Combine(_appRoot, "game.dll")));
        Assert.Equal("brand-new", File.ReadAllText(Path.Combine(_appRoot, "added.dll")));
        Assert.Equal("2.0.0", UpdateState.ResolveInstalledVersion(_appRoot));

        UpdateApplyTestSeam.Reset();
        var boot = UpdateApplier.ApplyPendingIfAny(_appRoot, _log);
        Assert.True(boot.Applied, boot.Detail);
        Assert.Equal("brand-new", File.ReadAllText(Path.Combine(_appRoot, "added.dll")));
        Assert.Null(UpdateState.ReadPending(_appRoot));
        Assert.False(UpdateApplyJournal.Exists(_appRoot));
    }

    /// <summary>A rollback that cannot restore a file (the directory is not
    /// writable) keeps the journal and the backups, and the next boot finishes
    /// the restore instead of stacking more mutations on a mixed tree.</summary>
    [Fact]
    public void An_incomplete_rollback_keeps_the_journal_and_retries_on_the_next_boot()
    {
        if (OperatingSystem.IsWindows()) return;   // chmod-based fault injection

        var before = Snapshot();
        Stage("2.0.0", [("game.dll", "new-game"), ("second.dll", "new-second")]);
        var root = _appRoot;
        int copies = 0;
        var readOnly = new List<string>();
        UpdateApplyTestSeam.OnStep = (step, _) =>
        {
            if (step != UpdateApplyStep.AfterCopy || ++copies != 2) return;
            // Make the first target's directory unwritable so the rollback cannot rename over it.
            if (!OperatingSystem.IsWindows())
            {
                foreach (var dir in Directory.EnumerateDirectories(root).Where(d => !d.Contains("autoupdate")))
                {
                    File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                    readOnly.Add(dir);
                }
                File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                readOnly.Add(root);
            }
            throw new IOException("injected: rollback will fail");
        };

        var outcome = UpdateApplier.ApplyPendingIfAny(_appRoot, _log);

        Assert.False(outcome.Applied, outcome.Detail);
        Assert.True(UpdateApplyJournal.Exists(_appRoot), "an incomplete rollback must keep the journal");

        if (!OperatingSystem.IsWindows())
            foreach (var dir in readOnly)
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        UpdateApplyTestSeam.Reset();
        var boot = UpdateApplier.ApplyPendingIfAny(_appRoot, _log);

        Assert.False(boot.Applied, boot.Detail);
        AssertUnchanged(before);
        Assert.False(UpdateApplyJournal.Exists(_appRoot));
        Assert.Equal("1.0.0", UpdateState.ResolveInstalledVersion(_appRoot));
    }

    /// <summary>The replace is a same-directory rename, which is exactly what
    /// makes a self-update of an assembly the process already mapped safe on
    /// Linux (the running image keeps the old inode); the applier applies
    /// before it loads its own assemblies, and this pins the mechanism.</summary>
    [Fact]
    public void A_mapped_file_can_be_replaced_atomically()
    {
        if (OperatingSystem.IsWindows()) return;   // Windows cannot rename over a mapped file

        var path = Path.Combine(_appRoot, "game.dll");
        using (var mapped = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(
                   path, FileMode.Open, null, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read))
        using (var view = mapped.CreateViewAccessor(0, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read))
        {
            Stage("2.0.0", [("game.dll", "new-game-bytes")]);
            var outcome = UpdateApplier.ApplyPendingIfAny(_appRoot, _log);
            Assert.True(outcome.Applied, outcome.Detail);
            Assert.Equal("new-game-bytes", File.ReadAllText(path));
            // The old mapping still reads the old bytes (old inode), so nothing
            // the running process has loaded changes underneath it.
            var buffer = new byte[5];
            view.ReadArray(0, buffer, 0, buffer.Length);
            Assert.Equal("old-g"u8.ToArray(), buffer);
        }
    }

    // ---- helpers ---------------------------------------------------------

    private string StagedPath(string rel)
        => Path.Combine(UpdateState.StateDirectory(_appRoot), "staging", rel.Replace('/', Path.DirectorySeparatorChar));

    private void Write(string rel, string content, string mode = "644")
    {
        var path = Path.Combine(_appRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        UpdateManifestBuilder.ApplyMode(path, mode);
    }

    /// <summary>Stage exactly what <see cref="UpdateClient.CheckAndStageAsync"/>
    /// leaves behind: verified staging bytes, the staged manifest and the marker.</summary>
    private void Stage(string version, (string Path, string Content)[] files, string[]? obsolete = null)
        => Stage(version, files.Select(f => (f.Path, f.Content, "644")).ToArray(), obsolete);

    private void Stage(string version, (string Path, string Content, string Mode)[] files, string[]? obsolete = null)
    {
        var state = UpdateState.StateDirectory(_appRoot);
        var staging = Path.Combine(state, "staging");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);

        var entries = new List<UpdateFileEntry>();
        foreach (var (rel, content, mode) in files)
        {
            var staged = Path.Combine(staging, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            var bytes = Encoding.UTF8.GetBytes(content);
            File.WriteAllBytes(staged, bytes);
            UpdateManifestBuilder.ApplyMode(staged, mode);
            entries.Add(new UpdateFileEntry(rel, bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)), mode));
        }

        File.WriteAllBytes(Path.Combine(state, "staged-manifest.json"), UpdateManifestBuilder.Serialize(
            new UpdateManifest { Version = version, Files = entries }));
        UpdateState.WritePending(_appRoot, new UpdatePendingUpdate(
            version, entries.Select(e => e.Path).ToList(), obsolete ?? []));
    }

    /// <summary>Every file in the application tree (the state directory excluded —
    /// the updater legitimately records its own retry count there) with its
    /// SHA-256 and Unix mode: "byte-identical" in one comparison.</summary>
    private Dictionary<string, string> Snapshot()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(_appRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(_appRoot, path).Replace('\\', '/');
            if (rel.StartsWith(UpdateFeed.StateDir + "/", StringComparison.OrdinalIgnoreCase)) continue;
            string sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            string mode = OperatingSystem.IsWindows() ? "-" : ((int)File.GetUnixFileMode(path)).ToString();
            result[rel] = $"{sha}:{mode}";
        }
        return result;
    }

    private void AssertUnchanged(Dictionary<string, string> before)
    {
        var after = Snapshot();
        Assert.Equal(
            before.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList(),
            after.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList());
    }
}
