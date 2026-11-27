using System.Diagnostics;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P1091HudOverlapTests
{
    public static TheoryData<int, string, string> Viewports
    {
        get
        {
            var cases = new TheoryData<int, string, string>();
            foreach (int width in new[] { 800, 1280, 1920 })
            {
                cases.Add(width, "Classic", "default");
                cases.Add(width, "ClassicArt", "default");
                foreach (string scale in new[] { "min", "default", "max", "floor", "ceiling" })
                    cases.Add(width, "Modern", scale);
            }
            return cases;
        }
    }

    [SkippableTheory, MemberData(nameof(Viewports)), Trait("Requires", "Gpu")]
    public async Task Painted_mount_rows_and_entity_readouts_are_distinct(int width, string skin, string scale)
    {
        if (skin == "ClassicArt") Require.Artwork();
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo != null && !File.Exists(Path.Combine(repo.FullName, "AllegianceCore.slnx"))) repo = repo.Parent;
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { Path.Combine(repo!.FullName, "tools", "hud2-proof", "bin", configuration, "net10.0", "hud2-proof.dll"),
                "--overlaps", width.ToString(), skin, scale }
        };
        start.Environment["SDL_VIDEODRIVER"] = "offscreen";
        start.Environment["ALSOFT_DRIVERS"] = "null";
        start.Environment["LIBGL_ALWAYS_SOFTWARE"] = "1";
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await child.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { child.Kill(true); Assert.Fail("HUD paint proof timed out"); }
        string log = await stdout + await stderr;
        Require.GraphicsDevice(log);
        Assert.True(child.ExitCode == 0, log);
        Assert.Contains("errors=0", log);

        // SHA-256 of all 2,073,600 baseline RGBA pixels. Classic's control is
        // artwork-independent so this proof also runs on a clean checkout.
        if (width == 1920 && scale == "default" && skin != "ClassicArt")
            Assert.Contains(skin == "Modern" ? "28FB84F3BD41A1886F25D682CA62FB590F10FEDF101AEB5C9D82FB98117D62E2" : "DEAF3D5912741439BF1F8487539FECD77D9B457AB629427824F5C18F0A310333", log);
    }
}
