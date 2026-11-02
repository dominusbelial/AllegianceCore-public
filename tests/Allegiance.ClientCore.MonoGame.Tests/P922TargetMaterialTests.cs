using System.Diagnostics;
using Allegiance.Core;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P922TargetMaterialTests
{
    [SkippableTheory, Trait("Requires", "Artwork"), Trait("Requires", "Gpu"), Trait("Tier", "Live")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Selected_target_follows_live_material_mode_and_preserves_native_fallback(bool withoutPack)
    {
        Require.Artwork();
        if (!withoutPack)
            Skip.If(!File.Exists(Path.Combine(ExternalPaths.Resolve(ExternalResourceKind.HdPack), "manifest.json")), "Optional HD pack absent");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AllegianceCore.slnx"))) root = root.Parent;
        string repo = root!.FullName;
        string output = Path.Combine(repo, ".scratch/p922/tests", (withoutPack ? "native-" : "hd-") + Guid.NewGuid());
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET") ?? "dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = repo };
        start.ArgumentList.Add(Path.Combine(repo, "tools/p922-target-proof/bin/Debug/net10.0/p922-target-proof.dll"));
        start.ArgumentList.Add(output);
        start.Environment["SDL_VIDEODRIVER"] = "offscreen";
        start.Environment["ALSOFT_DRIVERS"] = "null";
        start.Environment["XDG_CONFIG_HOME"] = Path.Combine(output, "settings");
        start.Environment["XDG_DATA_HOME"] = Path.Combine(output, "data");
        if (withoutPack)
        {
            start.Environment[ExternalPaths.HdPackEnvVar] = Path.Combine(output, "absent-pack");
            start.ArgumentList.Add("--no-pack");
        }
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); Assert.Fail("P922 GPU proof timed out"); }
        string log = await stdout + await stderr;
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "proof.log"), log);
        Require.GraphicsDevice(log);
        Assert.True(process.ExitCode == 0, log);
        Assert.Contains("P922_GPU_PASS cases=24", log);
    }
}
