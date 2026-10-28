using System.Diagnostics;
using Allegiance.Core;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P800 — every hull renders: the GPU integration gate. Runs the
/// <c>tools/p800-hull-proof</c> child process (offscreen GL) over the REAL
/// static core: 447 hulls × 5 renders each (hangar preview, flight near/mid +
/// far-tier sprite, target-pane preview), coverage-measured, then asserts the
/// tool's own classification reports <b>zero failures</b> — no hull may render
/// empty in any view. This is the render-level complement to the CPU-only
/// structural audit in <see cref="P800EveryHullRendersTests"/>: it catches what
/// only a real draw can (blend/rasterizer state, LOD selection, preview
/// framing, far-tier sprites).
/// </summary>
public class P800HullProofGpuTests
{
    private static readonly SemaphoreSlim ToolBuildLock = new(1, 1);
    private static int _toolBuildAttempted;

    /// <summary>
    /// NT-2 — locate the p800-hull-proof tool binary, building the tool project
    /// once if the fast tier's host did not run <c>tools/build-tools.sh</c> (the
    /// nightly runner builds only the solution before the fast tier, so a fresh
    /// checkout used to fail this test in ~200 ms with "dotnet-…p800-hull-proof.dll
    /// does not exist"). Returns null (with the reason in <paramref name="why"/>)
    /// when the tool cannot be produced, so the caller can SKIP with a clear
    /// reason instead of failing.
    /// </summary>
    private static string? LocateToolDll(string repo, out string? why)
    {
        string dll = Path.Combine(repo, "tools/p800-hull-proof/bin/Debug/net10.0/p800-hull-proof.dll");
        if (File.Exists(dll)) { why = null; return dll; }

        // One process-wide build attempt (parallel test classes race otherwise).
        if (Interlocked.Exchange(ref _toolBuildAttempted, 1) == 0)
        {
            ToolBuildLock.Wait();
            try
            {
                if (!File.Exists(dll))
                {
                    var psi = new ProcessStartInfo("dotnet")
                    {
                        ArgumentList = { "build", Path.Combine(repo, "tools/p800-hull-proof/p800-hull-proof.csproj"), "-v", "quiet", "--nologo" },
                        RedirectStandardOutput = true, RedirectStandardError = true,
                    };
                    using var p = Process.Start(psi)!;
                    p.WaitForExit(TimeSpan.FromMinutes(5));
                }
            }
            catch { /* fall through to the skip below */ }
            finally { ToolBuildLock.Release(); }
        }
        else ToolBuildLock.Wait(TimeSpan.FromSeconds(300));

        if (File.Exists(dll)) { why = null; return dll; }
        why = "tools/p800-hull-proof is not built and could not be built in-process " +
              "(the fast tier's host did not run tools/build-tools.sh); run tools/build-tools.sh and retry";
        return null;
    }

    [Trait("Requires", "Artwork")]
    [Trait("Requires", "Gpu")]
    [SkippableFact]
    public async Task Every_hull_of_the_real_core_renders_in_hangar_flight_and_target_pane()
    {
        Require.Artwork();
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AllegianceCore.slnx"))) dir = dir.Parent;
        string repo = dir?.FullName ?? throw new DirectoryNotFoundException("repo");
        string? toolDll = LocateToolDll(repo, out string? why);
        if (toolDll is null) Xunit.Skip.If(true, why!);   // clear skip reason, not a 200 ms failure
        string outDir = Path.Combine(repo, ".scratch", "p800-proof-test");
        Directory.CreateDirectory(outDir);
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] {
            toolDll,
            "--art", ExternalPaths.Require(ExternalResourceKind.Artwork),
            "--out", outDir, "--label", "test" }) start.ArgumentList.Add(arg);        start.Environment["SDL_VIDEODRIVER"] = "offscreen"; start.Environment["ALSOFT_DRIVERS"] = "null";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(300));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); Assert.Fail("P800 hull proof timed out"); }
        string output = await stdout + await stderr;
        Require.GraphicsDevice(output);
        Assert.True(process.ExitCode == 0, output);
        var summary = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("P800_SUMMARY ", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("no P800_SUMMARY line in proof output:\n" + output);
        // 447 hulls, 295 buyable, 198 offered under testUnlock all-tech — and
        // NO hull classified as a rendering failure in any view.
        Assert.Contains("total=447", summary);
        Assert.Contains("buyable=295", summary);
        Assert.Contains("offeredAllTech=198", summary);
        Assert.Contains("failures=0", summary);
    }
}
