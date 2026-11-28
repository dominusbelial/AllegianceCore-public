using System.Diagnostics;
using Allegiance.TestInfra;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P1098SkyTests(ITestOutputHelper output)
{
    [SkippableFact, Trait("Requires", "Gpu")]
    public async Task Unload_releases_sky_handles_before_device_teardown()
    {
        // SDL offscreen needs a fresh process to create another GL context.
        await Run("lifetime", "--single-device");
        await Run("lifetime", "--single-device", "--high-density");
    }

    [SkippableTheory, Trait("Requires", "Gpu")]
    [InlineData("black")]
    [InlineData("native")]
    [InlineData("hd")]
    public Task Sky_and_both_HUDs_match_through_glimpse_warp_and_resize(string tier) => Run(tier);

    private async Task Run(string scenario, params string[] options)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "AllegianceCore.slnx")))
            root = root.Parent ?? throw new DirectoryNotFoundException("Solution root");
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = root.FullName, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(root.FullName, "tools/p1098-sky-proof/bin", configuration,
            "net10.0/p1098-sky-proof.dll"));
        start.ArgumentList.Add(scenario);
        foreach (string option in options) start.ArgumentList.Add(option);
        start.Environment["SDL_VIDEODRIVER"] = "offscreen";
        start.Environment["ALSOFT_DRIVERS"] = "null";
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        try { await child.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { child.Kill(true); throw; }
        string log = await stdout + await stderr;
        Require.GraphicsDevice(log);
        Assert.True(child.ExitCode == 0, log);
        Assert.Contains($"P1098_PASS {scenario}", log);
        foreach (string line in log.Split('\n').Where(l => l.StartsWith("ALLOC ") || l.StartsWith("LIFETIME ")))
            output.WriteLine(line);
    }
}
