using System.Diagnostics;

namespace Allegiance.Core.Tests;

/// <summary>
/// P601/P601b — repository-hygiene guard, repo-wide. Fails if ANY tracked file
/// anywhere is a Meshy-generated output (a model, texture, render, screenshot,
/// contact sheet or thumbnail that shows a Meshy model, raw or repaired), because
/// the public MIT repo may not redistribute Meshy content — it lives in the private
/// optional asset pack (<c>~/Development/AllegianceCore-meshy-pack</c>,
/// <c>screenshots/&lt;repo path&gt;</c> for images).
/// </summary>
/// <remarks>
/// Detection is belt-and-suspenders, so it degrades safely if a data file goes
/// stale:
/// 1. Location — anything under <c>assets/ships/thumbs</c>, <c>Content/Models</c>
///    (<c>.fbx</c>/<c>.fbm</c>), or <c>Content/Textures/ships</c>.
/// 2. Extension — any 3D/model/toolchain output (<c>.fbx .fbm .obj .glb .gltf
///    .blend .mtl</c>) tracked anywhere outside an explicit allowlist.
/// 3. Name — any IMAGE/VIDEO/MODEL file whose path contains <c>MESHY</c> /
///    <c>meshy</c>, except the native-input renders under <c>docs/meshy_hints/</c>
///    and the (non-output) tooling under <c>tools/meshy-repair/</c>.
/// 4. Authoritative list — every path marked class <c>a</c> in
///    <c>docs/release/media-inventory.csv</c> must not be tracked.
/// As with <see cref="PrivatePathGuardTests"/>, only <c>git ls-files</c>-tracked
/// files are inspected, so gitignored local state is correctly excluded.
/// </remarks>
public class MeshyGuardTests
{
    // Meshy 3D/model/toolchain extensions (nothing native is tracked with these
    // today; extend this list if a legitimately native model is ever added).
    private static readonly string[] ModelExtensions =
        [".fbx", ".fbm", ".obj", ".glb", ".gltf", ".blend", ".mtl"];
    // Image/video/model extensions used for the name-based check.
    private static readonly string[] MediaExtensions =
        [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tga",
         ".mp4", ".webm", ".fbx", ".fbm", ".obj", ".glb", ".gltf", ".blend", ".mtl"];
    // Directories that legitimately contain Meshy-named content but no Meshy output.
    private static readonly string[] NameAllowDirPrefixes =
        ["docs/meshy_hints/", "tools/meshy-repair/"];

    [Fact]
    public void NoTrackedMeshyOutputRemainsAnywhere()
    {
        var root = FindRepoRoot();
        Assert.False(string.IsNullOrEmpty(root), "could not locate the repo root (AllegianceCore.slnx marker)");

        var offenders = new List<string>();
        // TT-1: reuse one repo-wide snapshot. Re-running git for each inventory
        // row made this Fast guard exceed ten seconds.
        var tracked = GitLsFilesAll(root);
        var trackedSet = tracked.ToHashSet(StringComparer.Ordinal);

        // 1. Location-based.
        offenders.AddRange(Locate(root, "src/Allegiance.ClientCore.MonoGame/Content/Models"));
        offenders.AddRange(Locate(root, "src/Allegiance.ClientCore.MonoGame/Content/Textures/ships"));
        offenders.AddRange(Locate(root, "assets/ships/thumbs"));

        // 2. Extension-based across the whole repo.
        foreach (var rel in tracked)
        {
            var lower = rel.ToLowerInvariant();
            if (ModelExtensions.Any(e => lower.EndsWith(e, StringComparison.Ordinal)))
                offenders.Add(rel);
        }

        // 3. Name-based across the whole repo (media files only), excluding allowed dirs.
        foreach (var rel in tracked)
        {
            var lower = rel.ToLowerInvariant();
            if (!MediaExtensions.Any(e => lower.EndsWith(e, StringComparison.Ordinal))) continue;
            if (NameAllowDirPrefixes.Any(d => rel.StartsWith(d, StringComparison.Ordinal))) continue;
            if (lower.Contains("meshy"))
                offenders.Add(rel);
        }

        // 4. Authoritative list from media-inventory.csv (class = a).
        var inv = Path.Combine(root!, "docs", "release", "media-inventory.csv");
        if (File.Exists(inv))
        {
            foreach (var line in File.ReadAllLines(inv))
            {
                if (line.StartsWith("path,")) continue; // header
                var cols = line.Split(',');
                if (cols.Length < 3) continue;
                var cls = cols[2].Trim();
                if (!string.Equals(cls, "a", StringComparison.Ordinal)) continue;
                var rel = cols[0].Trim();
                if (trackedSet.Contains(rel))
                    offenders.Add($"media-inventory.csv class=a: {rel}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Meshy-generated output(s) found in tracked files (these must live in the private pack):\n"
            + string.Join("\n", offenders));
    }

    /// <summary>Files under a location that are Meshy outputs (models/textures/thumbs).</summary>
    private static List<string> Locate(string root, string path)
        => GitLsFiles(root, path);

    private static List<string> GitLsFilesAll(string root)
    {
        var psi = new ProcessStartInfo("git", "ls-files")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                     .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
    }

    private static List<string> GitLsFiles(string root, string path)
    {
        var psi = new ProcessStartInfo("git", "ls-files -- " + ShellQuote(path))
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                     .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
    }

    private static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

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
