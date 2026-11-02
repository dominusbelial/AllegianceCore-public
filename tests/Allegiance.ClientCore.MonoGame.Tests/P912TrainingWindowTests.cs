using System.Diagnostics;
using Allegiance.TestInfra;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.MonoGame.Tests;

// Uses tools/p912-training-proof/bin (retained by build-tools.sh --drop-bins).
// Every case opens the shipping Game1, joins its real embedded host, and
// traverses every lesson in one session. The observer checks every boundary;
// the input driver supplies keys, pointer presses/releases and text only.
[Collection("Training")]
public sealed class P912TrainingWindowTests(ITestOutputHelper output)
{
    [SkippableTheory, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    [InlineData(3, "Classic")] [InlineData(3, "Modern")]
    [InlineData(4, "Classic")] [InlineData(4, "Modern")]
    [InlineData(5, "Classic")] [InlineData(5, "Modern")]
    [InlineData(8, "Classic")] [InlineData(8, "Modern")]
    [InlineData(4, "Modern", "departure")]
    [InlineData(8, "Modern", "early")] [InlineData(8, "Modern", "loss")]
    public async Task Every_lesson_completes_in_one_real_window_session(int mission, string style, string? policy = null)
        => await RunWindow(mission, style, policy);

    [SkippableTheory, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    [InlineData(3, 9)] [InlineData(3, 10)] [InlineData(3, 11)]
    [InlineData(4, 1)] [InlineData(4, 2)] [InlineData(4, 3)]
    [InlineData(4, 4)] [InlineData(4, 5)] [InlineData(4, 7)]
    [InlineData(4, 8)] [InlineData(4, 9)] [InlineData(4, 10)]
    [InlineData(5, 1)] [InlineData(5, 2)] [InlineData(5, 3)]
    [InlineData(5, 4)] [InlineData(5, 5)]
    [InlineData(8, 1)] [InlineData(8, 2)] [InlineData(8, 3)]
    public async Task Assigned_lesson_completes_in_its_own_fresh_window(int mission, int lesson)
        // Replay the native prefix rather than arranging a goal or teleporting
        // actors. Stop only after the assigned lesson's real last condition.
        => await RunWindow(mission, "Modern", $"lesson={lesson}");

    private async Task RunWindow(int mission, string style, string? policy)
    {
        Require.Artwork();
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AllegianceCore.slnx")))
            root = Directory.GetParent(root)?.FullName ?? throw new DirectoryNotFoundException("solution root");
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string proof = Path.Combine(root, "tools", "p912-training-proof", "bin", configuration, "net10.0", "p912-training-proof.dll");
        string content = Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame", "bin", configuration, "net10.0", "Content");
        Assert.True(File.Exists(proof), "Build tools/p912-training-proof before running the window tests.");
        string directory = Path.Combine(root, ".scratch", "p912", "windows", $"m{mission}-{style}-{policy ?? "default"}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string dotnet = Environment.GetEnvironmentVariable("DOTNET")
            ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet");
        var start = new ProcessStartInfo(dotnet) { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { proof, "window", content, directory, mission.ToString(), style }) start.ArgumentList.Add(argument);
        if (policy is not null) start.ArgumentList.Add(policy);
        // Use the shipping variable-frame mode with software rendering. This
        // keeps elapsed time coupled to the live host when draws miss 60 Hz.
        start.Environment["ALLEGIANCE_NOVSYNC"] = "1";
        start.Environment["SDL_VIDEODRIVER"] = "offscreen";
        start.Environment["DISPLAY"] = "";
        start.Environment["LIBGL_ALWAYS_SOFTWARE"] = "1";
        start.Environment["ALSOFT_DRIVERS"] = "null";
        start.Environment["DOTNET_gcServer"] = "1";
        start.Environment["ALLEGIANCE_BROADCAST_PARALLEL_BUILD"] = "1";
        start.Environment["ALLEGIANCE_TRANSPORT_MODE"] = "pool";
        start.Environment["XDG_CONFIG_HOME"] = Path.Combine(directory, "xdg");
        start.Environment["ALLEGIANCE_HUD_SETTINGS"] = Path.Combine(directory, "hud.json");
        using var process = Process.Start(start)!;
        using var logWriter = new StreamWriter(Path.Combine(directory, "run.log")) { AutoFlush = true };
        var logGate = new SemaphoreSlim(1);
        async Task<string> Capture(StreamReader reader)
        {
            var lines = new System.Text.StringBuilder();
            while (await reader.ReadLineAsync() is { } line)
            {
                lines.AppendLine(line);
                await logGate.WaitAsync();
                try { await logWriter.WriteLineAsync(line); } finally { logGate.Release(); }
            }
            return lines.ToString();
        }
        var stdout = Capture(process.StandardOutput);
        var stderr = Capture(process.StandardError);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(21));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        string log = await stdout + await stderr;
        Require.GraphicsDevice(log);
        output.WriteLine($"Evidence: {Path.GetRelativePath(root, directory)}");
        Assert.True(process.ExitCode == 0, $"Window failed: {directory}\n{log[^Math.Min(log.Length, 7000)..]}");
        Assert.Contains($"P912_{(policy?.StartsWith("lesson=", StringComparison.Ordinal) == true ? "LESSON" : "WINDOW")}_PASS M{mission} {style}", log);
        if (mission == 4 && policy != "lesson=1") Assert.Matches(@"quick-chat >> .* \(Everyone\)", log);
        Assert.True(File.Exists(Path.Combine(directory, "complete.txt")));
    }
}
