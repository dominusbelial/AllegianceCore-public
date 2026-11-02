using System.Diagnostics;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P921LodRenderTests
{
    [SkippableFact, Trait("Requires", "Artwork"), Trait("Requires", "Gpu"), Trait("Tier", "Live")]
    public async Task Imported_fig30_uses_native_renderer_level_order_and_identical_pixels_across_thresholds()
    {
        Require.Artwork();
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AllegianceCore.slnx"))) dir = dir.Parent;
        string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet");
        var info = new ProcessStartInfo(host) { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Path.Combine(dir!.FullName, "tools/p921-lod-proof/bin/Debug/net10.0/p921-lod-proof.dll"));
        info.Environment["SDL_VIDEODRIVER"] = "offscreen"; info.Environment["ALSOFT_DRIVERS"] = "null";
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); Assert.Fail("LOD render proof timed out"); }
        string log = await stdout + await stderr;
        Require.GraphicsDevice(log);
        Assert.True(process.ExitCode == 0, log);
        Assert.Contains("P921_LOD_RENDER_PASS samples=11 levels=3 exactPixels=true", log);
    }
}
