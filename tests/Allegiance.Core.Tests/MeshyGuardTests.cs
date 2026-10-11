using System.Diagnostics;

namespace Allegiance.Core.Tests;

/// <summary>
/// P601 — repository-hygiene guard. Fails if any TRACKED file under <c>src/</c>
/// or <c>assets/</c> is a Meshy-generated output, because the public MIT repo
/// may not redistribute Meshy models/textures (they live in the private
/// optional asset pack). Also fails if any previously-moved Meshy screenshot /
/// render path has crept back into <c>docs/</c> (the moved set is listed in
/// <c>docs/release/moved-meshy-screenshots.txt</c>).
/// </summary>
/// <remarks>Like <see cref="PrivatePathGuardTests"/>, only <c>git ls-files</c>
/// -tracked files are inspected, so gitignored local state is correctly excluded.
/// </remarks>
public class MeshyGuardTests
{
    [Fact]
    public void NoTrackedMeshyModelTextureOrThumbnailRemains()
    {
        var root = FindRepoRoot();
        Assert.False(string.IsNullOrEmpty(root), "could not locate the repo root (AllegianceCore.slnx marker)");

        var offenders = new List<string>();

        // 1. Any .fbx/.fbm model under Content/Models (a Meshy ship / prop model).
        foreach (var rel in GitLsFiles(root, "src/Allegiance.ClientCore.MonoGame/Content/Models"))
        {
            var lower = rel.ToLowerInvariant();
            if (lower.EndsWith(".fbx") || lower.Contains(".fbm")) offenders.Add(rel);
        }

        // 2. Any file under Content/Textures/ships (Meshy ship textures).
        foreach (var rel in GitLsFiles(root, "src/Allegiance.ClientCore.MonoGame/Content/Textures/ships"))
            offenders.Add(rel);

        // 3. Any Meshy thumbnail under assets/ships/thumbs.
        foreach (var rel in GitLsFiles(root, "assets/ships/thumbs"))
            offenders.Add(rel);

        // 4. A catalogue.json entry whose source is meshy/meshy-repaired.
        var catPath = Path.Combine(root!, "assets", "ships", "catalogue.json");
        if (File.Exists(catPath))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(catPath));
                if (doc.RootElement.TryGetProperty("models", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var el in arr.EnumerateArray())
                        if (el.TryGetProperty("source", out var src))
                        {
                            var s = src.GetString();
                            if (s is not null && s.StartsWith("meshy", StringComparison.OrdinalIgnoreCase))
                                offenders.Add($"catalogue.json entry source={s}");
                        }
            }
            catch { /* malformed catalogue → covered by other tests */ }
        }

        // 5. Any previously-moved Meshy screenshot/render path that reappeared under docs/.
        var moved = Path.Combine(root!, "docs", "release", "moved-meshy-screenshots.txt");
        if (File.Exists(moved))
            foreach (var line in File.ReadAllLines(moved))
            {
                var rel = line.Trim();
                if (rel.Length == 0 || rel.StartsWith('#')) continue;
                if (GitLsFiles(root, rel).Count > 0)
                    offenders.Add($"(reappeared) {rel}");
            }

        Assert.True(offenders.Count == 0,
            "Meshy-generated output(s) found in tracked files (these must live in the private pack):\n"
            + string.Join("\n", offenders));
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
