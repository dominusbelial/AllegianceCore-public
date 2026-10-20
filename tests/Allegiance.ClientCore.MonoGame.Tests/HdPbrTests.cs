using System.Diagnostics;
using System.Security.Cryptography;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.SinglePlayer.Benchmark;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class HdPbrTests
{
    [Fact]
    public void Tangents_follow_native_uvs_and_mirrored_islands_have_opposite_handedness()
    {
        var vertices = new[] {
            new VertexPositionNormalTexture(Vector3.Zero, Vector3.UnitZ, Vector2.Zero),
            new VertexPositionNormalTexture(Vector3.UnitX, Vector3.UnitZ, Vector2.UnitX),
            new VertexPositionNormalTexture(Vector3.UnitY, Vector3.UnitZ, Vector2.UnitY) };
        var result = PbrVertex.Build(vertices, [0, 1, 2]);
        foreach (var v in result) { Assert.Equal(new Vector4(1, 0, 0, 1), v.Tangent); Assert.Equal(Vector3.UnitZ, v.Normal); }
        vertices[1].TextureCoordinate = -Vector2.UnitX;
        result = PbrVertex.Build(vertices, [0, 1, 2]);
        foreach (var v in result) Assert.Equal(new Vector4(-1, 0, 0, -1), v.Tangent);
    }
    [Fact]
    public void Degenerate_uvs_and_normals_produce_finite_orthonormal_tangent_frames()
    {
        var source = new[] {
            new VertexPositionNormalTexture(Vector3.Zero, Vector3.Zero, Vector2.Zero),
            new VertexPositionNormalTexture(Vector3.UnitX, Vector3.Up, Vector2.Zero),
            new VertexPositionNormalTexture(Vector3.UnitY, Vector3.UnitZ, Vector2.Zero) };
        foreach (var v in PbrVertex.Build(source, [0, 1, 2]))
        {
            var t = new Vector3(v.Tangent.X, v.Tangent.Y, v.Tangent.Z);
            Assert.InRange(t.Length(), .9999f, 1.0001f); Assert.InRange(MathF.Abs(Vector3.Dot(t, v.Normal)), 0, 1e-6f);
            Assert.Equal(1, v.Tangent.W);
        }
    }
    [Fact]
    public void Texture_budget_includes_every_mip_and_rejects_invalid_dimensions()
    {
        Assert.Equal(84, HdMaterialRenderer.MipBytes(4, 4)); Assert.Equal(28, HdMaterialRenderer.MipBytes(1, 4));
        Assert.Equal(89478484, HdMaterialRenderer.MipBytes(4096, 4096));
        Assert.Throws<ArgumentOutOfRangeException>(() => HdMaterialRenderer.MipBytes(0, 2));
    }
    [Fact]
    public void Hd_setting_roundtrips_and_old_settings_keep_pack_enabled()
    {
        string path = Path.Combine(Path.GetTempPath(), "hd2-settings-" + Guid.NewGuid(), "graphics.json");
        try
        {
            Assert.True(UpscalingSettings.Load(path).HdTextures);
            new UpscalingSettings { HdTextures = false }.Save(path);
            Assert.False(UpscalingSettings.Load(path).HdTextures);
            File.WriteAllText(path, "{}"); Assert.True(UpscalingSettings.Load(path).HdTextures);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }
    [Theory]
    [InlineData(false, false, HdDrawMode.Off)]
    [InlineData(false, true, HdDrawMode.Off)]
    [InlineData(true, false, HdDrawMode.Classic)]
    [InlineData(true, true, HdDrawMode.Pbr)]
    public void Mode_selection_per_setting(bool hdTextures, bool hdPbr, HdDrawMode expected)
        => Assert.Equal(expected, HdMaterialRenderer.ModeFor(hdTextures, hdPbr));

    [Fact]
    public void Hd_lighting_flag_defaults_off_and_roundtrips()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hd5-settings-" + Guid.NewGuid());
        string path = Path.Combine(dir, "graphics.json");
        try
        {
            // Brand new (no file) -> the new experimental default: PBR off.
            Assert.False(UpscalingSettings.Load(path).HdPbr);
            // Explicit on persists.
            new UpscalingSettings { HdTextures = true, HdPbr = true }.Save(path);
            Assert.True(UpscalingSettings.Load(path).HdPbr);
            // Explicit off persists (no migration overrides it).
            new UpscalingSettings { HdTextures = true, HdPbr = false }.Save(path);
            Assert.False(UpscalingSettings.Load(path).HdPbr);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Legacy_saved_HdPbr_migrates_to_the_new_flag()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hd5-migrate-" + Guid.NewGuid());
        string gfxPath = Path.Combine(dir, "graphics.json");
        string benchPath = Path.Combine(dir, "benchmark.json");
        string? savedEnv = Environment.GetEnvironmentVariable("ALLEGIANCE_BENCHMARK_CONFIG");
        try
        {
            Environment.SetEnvironmentVariable("ALLEGIANCE_BENCHMARK_CONFIG", benchPath);
            // No graphics.json HdPbr field, but the player applied an HdPbr=ON
            // benchmark recommendation before HD-5 -> the new flag inherits ON.
            Directory.CreateDirectory(dir);
            File.WriteAllText(gfxPath, "{\"HdTextures\": true}");
            (new BenchmarkPreferences { OfferHandled = true, Applied = new BenchmarkRecommendation { HdPbr = true, HdTextures = true } }).Save(benchPath);
            var loaded = UpscalingSettings.Load(gfxPath);
            Assert.True(loaded.HdPbr, "legacy HdPbr=on benchmark should migrate to the new experimental flag");

            // A file WITH an explicit HdPbr is authoritative (no migration).
            new UpscalingSettings { HdPbr = false }.Save(gfxPath);
            Assert.False(UpscalingSettings.Load(gfxPath).HdPbr);

            // No applied benchmark prefs at all -> the new default (off).
            File.Delete(benchPath);
            File.WriteAllText(gfxPath, "{\"HdTextures\": true}");
            Assert.False(UpscalingSettings.Load(gfxPath).HdPbr);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ALLEGIANCE_BENCHMARK_CONFIG", savedEnv);
            Directory.Delete(dir, true);
        }
    }
    [Fact]
    public void DesktopGl_shader_bytecode_matches_source()
    {
        string dir = Path.Combine(Repo(), "src/Allegiance.ClientCore.MonoGame/Content/Shaders");
        Assert.True(new FileInfo(Path.Combine(dir, "NativePbr.ogl.mgfxo")).Length > 100);
        Assert.Equal(File.ReadAllText(Path.Combine(dir, "NativePbr.fx.sha256")).Trim(),
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Repo(), "src/Allegiance.ClientCore.Game/Shaders/NativePbr.fx")))));
    }
    [Fact]
    public async Task Synthetic_material_renders_through_native_geometry_bloom_and_fsr_on_real_gl()
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(Repo(), "tools/hd2-proof/bin/Debug/net10.0/hd2-proof.dll"));
        start.ArgumentList.Add(Repo()); start.ArgumentList.Add("--test");
        start.Environment["SDL_VIDEODRIVER"] = "offscreen"; start.Environment["ALSOFT_DRIVERS"] = "null";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); Assert.Fail("HD2 GL render test timed out"); }
        string output = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, output); Assert.Contains("HD2_SELF_TEST_PASS", output);
    }
    [Fact]
    public async Task Classic_hd_uses_basecolor_with_native_lighting_and_falls_back_on_real_gl()
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(Repo(), "tools/hd2-proof/bin/Debug/net10.0/hd2-proof.dll"));
        start.ArgumentList.Add(Repo()); start.ArgumentList.Add("--classic-test");
        start.Environment["SDL_VIDEODRIVER"] = "offscreen"; start.Environment["ALSOFT_DRIVERS"] = "null";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); Assert.Fail("HD5 classic GL render test timed out"); }
        string output = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, output);
        Assert.Contains("HD5_CLASSIC_PASS", output);
    }
    private static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AllegianceCore.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo");
    }
}
