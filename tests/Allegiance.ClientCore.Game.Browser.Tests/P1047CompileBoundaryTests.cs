using System.Diagnostics;
using System.Text.Json;
using Allegiance.ClientCore.MonoGame;

namespace Allegiance.ClientCore.Browser.Tests;

public sealed class P1047CompileBoundaryTests
{
    [Fact]
    public async Task Evaluated_browser_compile_set_and_assembly_exclude_desktop_only_types()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AllegianceCore.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        string project = Path.Combine(root.FullName, "src/Allegiance.ClientCore.Game.Browser/Allegiance.ClientCore.Game.Browser.csproj");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root.FullName,
        };
        foreach (string arg in new[] { "msbuild", project, "-getItem:Compile", "-nologo" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
        using var items = JsonDocument.Parse(await output);
        var paths = items.RootElement.GetProperty("Items").GetProperty("Compile").EnumerateArray()
            .Select(e => e.GetProperty("FullPath").GetString()!).ToArray();
        Assert.Contains(paths, p => p.EndsWith("Game1.IntroMenu.cs", StringComparison.Ordinal));
        Assert.Contains(paths, p => p.EndsWith("Game1.OptionsMenu.cs", StringComparison.Ordinal));
        Assert.Contains(paths, p => p.EndsWith("HudRenderer.cs", StringComparison.Ordinal));
        Assert.Contains(paths, p => p.EndsWith("ModernHudRenderer.cs", StringComparison.Ordinal));
        string[] excluded = ["ConnectionSession", "SkirmishHost", "UpdateClient", "UpdateApplier", "DiscoveryEngine",
            "DiscoveryCoordinator", "NatTraversalClient", "GlFrameTimer", "RelativeMouse", "CapturePngSaver", "SavedGamePasswords", "TextCoreAssets", "SoundManager", "AudioVoice", "AsrSound", "TrainingVoicePcm"];
        foreach (string name in excluded)
        {
            Assert.DoesNotContain(paths, p => Path.GetFileName(p).StartsWith(name + ".", StringComparison.Ordinal));
            Assert.DoesNotContain(typeof(Game1).Assembly.GetTypes(), t => t.Name == name);
        }
        var coreTypes = typeof(Allegiance.Core.IgcFile).Assembly.GetTypes();
        foreach (string name in new[] { "NatTraversalClient", "TcpTransport", "UdpTransport", "ProcessLifetime", "ThreadPoolTelemetry" })
            Assert.DoesNotContain(coreTypes, t => t.Name == name);
        Assert.DoesNotContain(typeof(Game1).Assembly.GetReferencedAssemblies(), a => a.Name is "Allegiance.SinglePlayer" or "Allegiance.Server" or "Allegiance.Update" or "MonoGame.Framework");
    }
}
