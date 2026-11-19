using System.Diagnostics;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P1057AntialiasingTests
{
    [Fact, Trait("Tier", "Fast")]
    public void Absent_setting_is_off_and_off_does_not_query_the_device()
    {
        Assert.Equal(Antialiasing.Off, new UpscalingSettings().Antialiasing);
        Assert.Equal(0, AntialiasingOptions.Clamp(Antialiasing.Off, _ => throw new InvalidOperationException()));
    }

    [Theory, Trait("Tier", "Fast")]
    [InlineData(Antialiasing.Off)]
    [InlineData(Antialiasing.Msaa2)]
    [InlineData(Antialiasing.Msaa4)]
    [InlineData(Antialiasing.Msaa8)]
    public void Every_level_roundtrips_without_changing_other_graphics_settings(Antialiasing level)
    {
        string directory = Path.Combine(Path.GetTempPath(), "p1057-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "graphics.json");
        try
        {
            var saved = new UpscalingSettings { Antialiasing = level, Upscaling = UpscalingMode.FSR1,
                Preset = FsrPreset.Balanced, Sharpness = .25f, HdPbr = true };
            saved.Save(path);
            var loaded = UpscalingSettings.Load(path);
            Assert.Equal(level, loaded.Antialiasing);
            Assert.Equal(UpscalingMode.FSR1, loaded.Upscaling);
            Assert.Equal(FsrPreset.Balanced, loaded.Preset);
            Assert.Equal(.25f, loaded.Sharpness);
            Assert.True(loaded.HdPbr);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory, Trait("Tier", "Fast")]
    [InlineData("\"unknown-future-level\"")]
    [InlineData("3")]
    [InlineData("-1")]
    [InlineData("99")]
    [InlineData("null")]
    [InlineData("{}")]
    public void Unknown_saved_level_selects_highest_and_preserves_other_preferences(string value)
    {
        string path = Path.Combine(Path.GetTempPath(), "p1057-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"Antialiasing\":" + value + ",\"Sharpness\":0.25,\"HdPbr\":true}");
            var loaded = UpscalingSettings.Load(path);
            Assert.Equal(Antialiasing.Msaa8, loaded.Antialiasing);
            Assert.Equal(.25f, loaded.Sharpness);
            Assert.True(loaded.HdPbr);
            Assert.Equal(4, AntialiasingOptions.Clamp(loaded.Antialiasing, n => n <= 4));
        }
        finally { File.Delete(path); }
    }

    [Theory, Trait("Tier", "Fast")]
    [InlineData(8, 8, 8)]
    [InlineData(8, 4, 4)]
    [InlineData(8, 2, 2)]
    [InlineData(8, 0, 0)]
    [InlineData(4, 8, 4)]
    [InlineData(2, 8, 2)]
    [InlineData(3, 4, 4)]
    [InlineData(-1, 2, 2)]
    public void Clamps_to_device_support(int requested, int maximum, int expected)
        => Assert.Equal(expected, AntialiasingOptions.Clamp((Antialiasing)requested, n => n <= maximum));

    [Fact, Trait("Tier", "Fast")]
    public void Cycling_visits_each_level_then_off_in_both_directions()
    {
        var value = Antialiasing.Off;
        foreach (var expected in new[] { Antialiasing.Msaa2, Antialiasing.Msaa4, Antialiasing.Msaa8, Antialiasing.Off })
        { value = AntialiasingOptions.Next(value); Assert.Equal(expected, value); }
        Assert.Equal(Antialiasing.Msaa8, AntialiasingOptions.Next(value, -1));
    }

    [Theory, Trait("Tier", "Fast")]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public void Options_label_and_activation_share_the_visuals_page(HudStyle style)
    {
        Assert.Equal(style, new HudSettings { Style = style }.Style);
        var state = new OptionsMenuModel.OptionsState(true, true, false, false, true, .1f, Antialiasing: Antialiasing.Msaa4);
        var menu = new OptionsMenuModel();
        menu.Row0 = menu.Rows(state).ToList().FindIndex(r => r.Command == OptionsMenuModel.OptionsCommand.OpenVisualsMenu);
        menu.Activate(state);
        menu.Row0 = menu.Rows(state).ToList().FindIndex(r => r.Label == "Antialiasing (4x MSAA)");
        Assert.True(menu.Row0 >= 0);
        Assert.Null(menu.VisualKeyAt(menu.Row0, state));
        Assert.Equal(OptionsMenuModel.OptionsCommand.CycleAntialiasing, menu.Activate(state));
        // Browser supplies no desktop graphics setting and retains its menu.
        Assert.DoesNotContain(menu.Rows(state with { Antialiasing = null }), r => r.Command == OptionsMenuModel.OptionsCommand.CycleAntialiasing);
    }

    [Theory, Trait("Tier", "Fast")]
    [InlineData(false, 0, 8)]
    [InlineData(false, 2, 20)]
    [InlineData(false, 4, 36)]
    [InlineData(false, 8, 68)]
    [InlineData(true, 0, 12)]
    [InlineData(true, 2, 32)]
    [InlineData(true, 4, 56)]
    [InlineData(true, 8, 104)]
    public void Memory_model_counts_resolve_colour_and_multisample_depth(bool hdr, int samples, int bytesPerPixel)
        => Assert.Equal(1920L * 1080 * bytesPerPixel, AntialiasingOptions.TargetBytes(1920, 1080, samples, hdr));

    [SkippableFact, Trait("Tier", "Live"), Trait("Requires", "Gpu"), Trait("Requires", "Artwork")]
    public async Task Real_device_resolves_colour_preserves_excursion_and_keeps_hud_exact()
    {
        Require.Artwork();
        string repo = FindRepo();
        string dll = Path.Combine(repo, "tools/p979-visual-proof/bin/Debug/net10.0/p979-visual-proof.dll");
        Skip.IfNot(File.Exists(dll), "build tools/p979-visual-proof first");
        string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var info = new ProcessStartInfo(host) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = repo };
        info.ArgumentList.Add(dll); info.ArgumentList.Add("--aa-check");
        info.ArgumentList.Add("--out"); info.ArgumentList.Add(Path.Combine(repo, ".scratch/p1057/test-proof-" + Guid.NewGuid().ToString("N")));
        info.Environment["SDL_VIDEODRIVER"] = "offscreen"; info.Environment["ALSOFT_DRIVERS"] = "null";
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); Assert.Fail("MSAA proof timed out"); }
        string log = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, log);
        Assert.Contains("P1057_RESOLVE_PASS Color", log);
        Assert.Contains("P1057_RESOLVE_PASS HalfVector4", log);
        Assert.Equal(8, log.Split("P1057_PATH_PASS").Length - 1);
        Assert.Contains("P1057_CHECK_PASS", log);
    }

    private static string FindRepo()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AllegianceCore.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }
}
