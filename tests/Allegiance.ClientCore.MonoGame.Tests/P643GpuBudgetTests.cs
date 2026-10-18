using System.Diagnostics;
using System.Runtime;
using System.Security.Cryptography;
using Allegiance.ClientCore.MonoGame.Diagnostics;
using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

[CollectionDefinition("P643 GC", DisableParallelization = true)]
public class P643GcCollection { }

[Collection("P643 GC")]
[Trait("Tier", "Perf")]
[Trait("Requires", "Gpu")]
public class P643GpuBudgetTests
{
    [Fact]
    public void Presets_use_fsr_ratios_and_native_rounding()
    {
        Assert.Equal(1f / 1.3f, UpscalingSettings.Scale(FsrPreset.UltraQuality));
        Assert.Equal(1f / 1.5f, UpscalingSettings.Scale(FsrPreset.Quality));
        Assert.Equal(1f / 1.7f, UpscalingSettings.Scale(FsrPreset.Balanced));
        Assert.Equal(.5f, UpscalingSettings.Scale(FsrPreset.Performance));
        Assert.Equal((1280, 720), UpscalingSettings.Dimensions(1920, 1080, UpscalingSettings.Scale(FsrPreset.Quality)));
        Assert.Equal((1280, 720), UpscalingSettings.Dimensions(2560, 1440, .5f));
        Assert.Equal((1, 1), UpscalingSettings.Dimensions(0, -1, float.NaN));
        Assert.Equal((161, 91), UpscalingSettings.Dimensions(321, 181, .5f));
    }
    [Fact]
    public void Dynamic_resolution_converges_on_gpu_budget()
    {
        var c = new DynamicResolutionController(); c.Reset(.77f);
        for (int i = 0; i < 1800; i++) c.Observe(40 * c.CurrentScale * c.CurrentScale, 16.667f, .77f);
        Assert.InRange(40 * c.CurrentScale * c.CurrentScale, 15, 18.2);
        for (int i = 0; i < 900; i++) c.Observe(2, 16.667f, .77f);
        Assert.Equal(.77f, c.CurrentScale);
        for (int i = 0; i < 900; i++) c.Observe(100, 16.667f, .77f);
        Assert.Equal(.5f, c.CurrentScale);
    }
    [Fact]
    public void Dynamic_resolution_has_hysteresis_dwell_and_ignores_missing_gpu()
    {
        var c = new DynamicResolutionController(); c.Reset(.67f);
        for (int i = 0; i < 1000; i++) c.Observe(i % 2 == 0 ? 16.2 : 17.2, 16.667f, .67f);
        Assert.Equal(.67f, c.CurrentScale);
        c.Reset(.67f);
        for (int i = 0; i < 29; i++) c.Observe(50, 16.667f, .67f);
        Assert.Equal(.67f, c.CurrentScale);
        Assert.True(c.Observe(50, 16.667f, .67f) < .67f);
        float last = c.CurrentScale;
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, 0, -1 })
            Assert.Equal(last, c.Observe(invalid, 16.667f, .67f));
    }
    [Fact]
    public void Settings_roundtrip_all_fields_and_default_off()
    {
        string path = Path.Combine(Path.GetTempPath(), $"p643-{Guid.NewGuid()}", "graphics.json");
        try
        {
            Assert.Equal(UpscalingMode.Off, UpscalingSettings.Load(path).Upscaling);
            var s = new UpscalingSettings { Upscaling = UpscalingMode.FSR1, Preset = FsrPreset.Balanced,
                Sharpness = .83f, DynamicResolution = true, TargetFrameMs = 20 };
            s.Save(path);
            var loaded = UpscalingSettings.Load(path);
            Assert.Equal(s.Upscaling, loaded.Upscaling); Assert.Equal(s.Preset, loaded.Preset);
            Assert.Equal(s.Sharpness, loaded.Sharpness); Assert.True(loaded.DynamicResolution); Assert.Equal(20, loaded.TargetFrameMs);
            loaded.Save(path); Assert.False(File.Exists(path + ".tmp"));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }
    [Fact]
    public void Corrupt_and_out_of_range_settings_fall_back_safely()
    {
        string path = Path.Combine(Path.GetTempPath(), $"p643-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(path, "{broken"); Assert.Equal(UpscalingMode.Off, UpscalingSettings.Load(path).Upscaling);
            File.WriteAllText(path, "{\"Upscaling\":999,\"Preset\":999,\"Sharpness\":5,\"TargetFrameMs\":-20}");
            var s = UpscalingSettings.Load(path);
            Assert.Equal(UpscalingMode.Off, s.Upscaling); Assert.Equal(FsrPreset.Quality, s.Preset);
            Assert.Equal(1, s.Sharpness); Assert.Equal(8, s.TargetFrameMs);
            s.Sharpness = float.NaN; s.TargetFrameMs = float.PositiveInfinity; s.Normalize();
            Assert.Equal(.5f, s.Sharpness); Assert.Equal(16.667f, s.TargetFrameMs);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void Low_latency_is_opt_in_and_restores_on_menu_and_disposal()
    {
        var original = GCSettings.LatencyMode;
        using (var off = new FlightGcLatency(false)) { off.SetFlying(true); Assert.Equal(original, GCSettings.LatencyMode); }
        using (var enabled = new FlightGcLatency(true))
        {
            enabled.SetFlying(true); Assert.Equal(GCLatencyMode.SustainedLowLatency, GCSettings.LatencyMode);
            enabled.SetFlying(false); Assert.Equal(original, GCSettings.LatencyMode);
            enabled.SetFlying(true);
        }
        Assert.Equal(original, GCSettings.LatencyMode);
    }
    [Fact]
    public void Telemetry_correlates_delayed_gpu_results_with_the_correct_frame()
    {
        string path = Path.Combine(Path.GetTempPath(), $"p643-{Guid.NewGuid()}.csv");
        try
        {
            using (var telemetry = new ClientFrameTelemetry(path))
            {
                for (int i = 1; i <= 20; i++)
                {
                    telemetry.BeginDraw(); telemetry.BeginPost(); telemetry.EndPost(); telemetry.Submitted();
                    telemetry.EndDraw(.2, 7, 1920, 1080, 1280, 720);
                    telemetry.ReceiveGpu(i, false, 4); telemetry.ReceiveGpu(i, true, 1);
                }
                Assert.Equal(5, telemetry.LatestGpuMs); Assert.Equal(20, telemetry.GpuGeneration);
            }
            var lines = File.ReadAllLines(path); Assert.Equal(21, lines.Length);
            for (int i = 1; i <= 20; i++)
            {
                var fields = lines[i].Split(','); Assert.Equal(i.ToString(), fields[0]);
                Assert.Equal("4.0000", fields[6]); Assert.Equal("1.0000", fields[7]);
                Assert.Equal(new[] { "7", "1920", "1080", "1280", "720" }, fields[10..]);
            }
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void Both_compiled_fsr_shaders_match_source_hashes()
    {
        foreach (string stem in new[] { "FsrEasu", "FsrRcas" })
        {
            string source = Path.Combine(Repo(), $"src/Allegiance.ClientCore.Game/Shaders/{stem}.fx");
            string dir = Path.Combine(Repo(), "src/Allegiance.ClientCore.MonoGame/Content/Shaders");
            Assert.Equal(File.ReadAllText(Path.Combine(dir, stem + ".fx.sha256")).Trim(),
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(source))));
            Assert.True(new FileInfo(Path.Combine(dir, stem + ".ogl.mgfxo")).Length > 100);
        }
    }
    [Fact]
    public async Task Both_shaders_load_and_render_through_real_gl_to_native_backbuffer()
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(Repo(), "tools/fsr-proof/bin/Debug/net10.0/fsr-proof.dll"));
        start.ArgumentList.Add(Repo());
        // Explicit headless EGL/GL context: usable even on CI with no DISPLAY.
        start.Environment["SDL_VIDEODRIVER"] = "offscreen";
        start.Environment["ALSOFT_DRIVERS"] = "null";
        using var p = Process.Start(start)!;
        var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { p.Kill(entireProcessTree: true); Assert.Fail("FSR GL test timed out"); }
        string output = await stdout + await stderr;
        Assert.True(p.ExitCode == 0, output); Assert.Contains("FSR_SELF_TEST_PASS", output);
    }
    private static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AllegianceCore.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo");
    }
}
