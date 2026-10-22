using System.Diagnostics;
using System.Text.Json;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// The player bundles ship exactly the artwork named in
/// <c>packaging/artwork-roots.txt</c> / <c>artwork-manifest.txt</c>, and the
/// release workflow's package step refuses to build when they are stale
/// (<c>packaging/generate-manifest.py --check</c>). Release run 39712953386 failed
/// there after the HUD/FONT work named new native art without regenerating them.
/// This runs the same check in the Fast tier, so the light gate, the mini runner
/// and <c>tools/run-ci-gate.sh</c> catch the drift at merge time. It needs the
/// artwork corpus at the commit pinned in <c>packaging/sources.json</c>, so it
/// skips on a clean clone (and on a corpus checked out at another commit).
/// </summary>
public class PackagingManifestTests
{
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "packaging", "generate-manifest.py")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Repository packaging/generate-manifest.py not found");
    }

    private static (int ExitCode, string Output) Run(string workingDirectory, string fileName, params string[] arguments)
    {
        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(60_000), $"{fileName} timed out");
        return (process.ExitCode, output.GetAwaiter().GetResult() + errors.GetAwaiter().GetResult());
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Artwork_manifest_matches_the_code_and_the_pinned_corpus()
    {
        Require.Artwork();
        var root = RepositoryRoot();
        var art = TestArtRoot.Value;
        using var sources = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "packaging", "sources.json")));
        var pin = sources.RootElement.GetProperty("artwork").GetProperty("commit").GetString()!;
        var head = Run(art, "git", "rev-parse", "HEAD");
        Skip.If(head.ExitCode != 0 || head.Output.Trim() != pin,
            $"artwork corpus at {art} is not a git checkout of the packaging pin {pin} (HEAD: {head.Output.Trim()})");

        var check = Run(root, "python3", Path.Combine(root, "packaging", "generate-manifest.py"), "--artwork", art, "--check");
        Assert.True(check.ExitCode == 0,
            "packaging manifests are stale; run `python3 packaging/generate-manifest.py --artwork <artwork>` and commit "
            + "packaging/artwork-roots.txt + artwork-manifest.txt after reviewing the diff:\n" + check.Output);
    }
}
