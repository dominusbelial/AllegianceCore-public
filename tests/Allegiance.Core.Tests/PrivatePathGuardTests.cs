using System.Diagnostics;
using System.Text;

namespace Allegiance.Core.Tests;

/// <summary>
/// P597 — repository-hygiene guard. Fails if any TRACKED file under the
/// source-facing directories contains an absolute private-home path (the
/// owner's home path) or another owner-machine path, so a per-machine path can
/// never quietly creep back into a public clone. The forbidden pattern is
/// assembled from fragments below so this very file cannot trip over itself.
/// </summary>
/// <remarks>
/// Only <c>git ls-files</c>-tracked files are inspected, so gitignored
/// per-machine artifacts (the repo-root <c>local.env</c>, the untracked
/// <c>src/Allegiance.Server/server.json</c>, etc.) are correctly excluded —
/// the guard targets what would actually be committed. Historical
/// <c>docs/reports</c> and <c>docs/prompts</c> are out of scope (decided
/// separately in P597).
/// </remarks>
public class PrivatePathGuardTests
{
    private static readonly string[] TargetDirs = ["src", "tests", "scripts", "tools", "docker", ".github"];
    private static readonly string[] BinaryExtensions =
        [".dll", ".exe", ".png", ".jpg", ".jpeg", ".gif", ".ico", ".woff2", ".ogg", ".wav", ".mp3", ".ttf", ".mgfxo", ".dat", ".bin", ".pdb", ".cov"];

    [Fact]
    public void NoTrackedFileUnderSourceDirsContainsAnAbsoluteHomePath()
    {
        var root = FindRepoRoot();
        Assert.False(string.IsNullOrEmpty(root), "could not locate the repo root (AllegianceCore.slnx marker)");

        var tracked = GitLsFiles(root, TargetDirs);
        Assert.True(tracked.Count > 0, "git ls-files returned no files — is this a git checkout?");

        var offenders = new List<string>();
        foreach (var rel in tracked)
        {
            var ext = Path.GetExtension(rel).ToLowerInvariant();
            if (BinaryExtensions.Contains(ext)) continue;
            var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) continue;
            var text = File.ReadAllText(path, Encoding.UTF8);
            // The known owner-machine leak, assembled from fragments so the
            // literal never appears in this file. Extend here if another
            // absolute home pattern is ever observed.
            var privateHome = string.Concat("/home/", "domingo");
            if (text.Contains(privateHome, StringComparison.Ordinal))
                offenders.Add(rel);
        }

        Assert.True(offenders.Count == 0,
            "Absolute private-home path(s) found in tracked source files:\n" + string.Join("\n", offenders));
    }

    private static List<string> GitLsFiles(string root, IReadOnlyList<string> dirs)
    {
        var psi = new ProcessStartInfo("git", "ls-files " + string.Join(" ", dirs))
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AllegianceCore.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
