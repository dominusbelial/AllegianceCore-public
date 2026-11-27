using System.Diagnostics;
using Allegiance.TestInfra;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P1090SmokeAtlasTests(ITestOutputHelper output)
{
    [SkippableTheory, Trait("Requires", "Gpu")]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("invalid-grid")]
    [InlineData("partial")]
    [InlineData("enhanced")]
    [InlineData("volumetric")]
    public async Task Flight_smoke_survives_two_sectors_and_matches_reference_draws(string scenario)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "AllegianceCore.slnx")))
            root = root.Parent ?? throw new DirectoryNotFoundException("Solution root");
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = root.FullName, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(root.FullName, "tools/p1090-smoke-proof/bin", configuration,
            "net10.0/p1090-smoke-proof.dll"));
        start.ArgumentList.Add(scenario);
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
        var receipt = Assert.Single(log.Split('\n'), line => line.StartsWith("P1090_PASS "));
        output.WriteLine(receipt);
        int fallbackLogs = log.Split('\n').Count(line => line.Contains("using native smoke"));
        Assert.Equal(scenario is "enhanced" or "volumetric" ? 0 : 1, fallbackLogs);
    }
}
