using Allegiance.Core;
using System.Diagnostics;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class HdAtlasRendererTests
{
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public async Task Compressed_maps_upload_on_real_gl_with_mip_and_bc3_fallback()
    {
        Require.Artwork();
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AllegianceCore.slnx"))) dir = dir.Parent;
        string repo = dir?.FullName ?? throw new DirectoryNotFoundException("repo");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { Path.Combine(repo, "tools/hd2-proof/bin/Debug/net10.0/hd2-proof.dll"),
            repo, "--compressed-test", "synthetic", "--art", ExternalPaths.Require(ExternalResourceKind.Artwork) }) start.ArgumentList.Add(arg);
        start.Environment["SDL_VIDEODRIVER"] = "offscreen"; start.Environment["ALSOFT_DRIVERS"] = "null";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); Assert.Fail("HD4 compressed GL render timed out"); }
        string output = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, output);
        Assert.Contains("HD4_COMPRESSED_PASS", output);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public Task Ship_uses_atlas_materials_and_preserves_native_fallback() => Check("ship");

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public Task Station_uses_atlas_materials_and_preserves_native_fallback() => Check("station");

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public Task Missile_uses_atlas_materials_and_preserves_native_fallback() => Check("missile");

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public Task Probe_uses_atlas_materials_and_preserves_native_fallback() => Check("probe");

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public Task Projectile_uses_atlas_materials_and_preserves_native_fallback() => Check("projectile");

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public Task Asteroid_uses_atlas_materials_and_preserves_native_fallback() => Check("asteroid");

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public Task Aleph_uses_atlas_materials_and_preserves_native_fallback() => Check("aleph");

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public Task Aleph_png_import_uses_atlas_materials_and_preserves_native_fallback() => Check("aleph_png");

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public Task Treasure_uses_atlas_materials_and_preserves_native_fallback() => Check("treasure");

    private static async Task Check(string kind)
    {
        Require.Artwork();
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AllegianceCore.slnx"))) dir = dir.Parent;
        string repo = dir?.FullName ?? throw new DirectoryNotFoundException("repo");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { Path.Combine(repo, "tools/hd2-proof/bin/Debug/net10.0/hd2-proof.dll"),
            repo, "--atlas-test", kind, "--art", ExternalPaths.Require(ExternalResourceKind.Artwork) }) start.ArgumentList.Add(arg);
        start.Environment["SDL_VIDEODRIVER"] = "offscreen"; start.Environment["ALSOFT_DRIVERS"] = "null";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); Assert.Fail("HD3 GL render timed out: " + kind); }
        string output = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, output);
        Assert.Contains("HD3_ATLAS_PASS " + kind, output);
    }
}
