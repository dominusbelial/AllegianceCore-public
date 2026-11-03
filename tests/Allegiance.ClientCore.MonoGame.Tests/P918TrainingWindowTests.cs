using System.Diagnostics;
using Allegiance.TestInfra;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.MonoGame.Tests;

[Collection("Training")]
public sealed class P918TrainingWindowTests(ITestOutputHelper output)
{
    [SkippableTheory, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    [InlineData("Classic")] [InlineData("Modern")]
    public async Task Buoy_decal_visibility_and_retirement_use_the_actual_training_window(string style)
    {
        Require.Artwork();
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AllegianceCore.slnx"))) root = Directory.GetParent(root)!.FullName;
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        // tools/p918-training-proof/bin is intentionally retained by build-tools.sh.
        string proof = Path.Combine(root, "tools", "p918-training-proof", "bin", configuration, "net10.0", "p918-training-proof.dll");
        string content = Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame", "bin", configuration, "net10.0", "Content");
        Assert.True(File.Exists(proof), "Build tools/p918-training-proof before running the window tests.");
        string evidence = Path.Combine(root, ".scratch", "p918", "windows", $"{style}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(evidence);
        string dotnet = Environment.GetEnvironmentVariable("DOTNET") ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet");
        var start = new ProcessStartInfo(dotnet) { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { proof, "window", content, evidence, style }) start.ArgumentList.Add(arg);
        start.Environment["ALLEGIANCE_NOVSYNC"] = "1";
        start.Environment["SDL_VIDEODRIVER"] = "offscreen";
        start.Environment["DISPLAY"] = "";
        start.Environment["LIBGL_ALWAYS_SOFTWARE"] = "1";
        start.Environment["ALSOFT_DRIVERS"] = "null";
        start.Environment["DOTNET_gcServer"] = "1";
        start.Environment["ALLEGIANCE_BROADCAST_PARALLEL_BUILD"] = "1";
        start.Environment["ALLEGIANCE_TRANSPORT_MODE"] = "pool";
        start.Environment["XDG_CONFIG_HOME"] = Path.Combine(evidence, "xdg");
        start.Environment["ALLEGIANCE_HUD_SETTINGS"] = Path.Combine(evidence, "hud.json");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        string log = await stdout + await stderr;
        await File.WriteAllTextAsync(Path.Combine(evidence, "run.log"), log);
        Require.GraphicsDevice(log);
        output.WriteLine($"Evidence: {Path.GetRelativePath(root, evidence)}");
        Assert.True(process.ExitCode == 0, log[^Math.Min(log.Length, 7000)..]);
        Assert.Contains($"P918_WINDOW_PASS {style}", log);
        Assert.True(File.Exists(Path.Combine(evidence, "complete.txt")));
        foreach (string state in new[] { "visible", "retired" })
            Assert.True(File.Exists(Path.Combine(evidence, $"p918-{style.ToLowerInvariant()}-{state}.png")));
    }
}
