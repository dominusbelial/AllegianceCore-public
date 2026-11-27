using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.TestInfra;
using Microsoft.Xna.Framework;
using System.Diagnostics;
using HudProof;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P1092ParticleBufferTests
{
    [Fact]
    public void Repeated_batches_reuse_arrays_and_oversize_draws_do_not_pin_them()
    {
        var buffers = new ParticleQuadBuffers();
        buffers.Get(23, out var vertices, out var indices);
        var retained = new List<int>();
        Assert.Same(retained, ParticleStaging.Begin(retained, 100, 3));
        retained.Add(123);
        Assert.NotSame(retained, ParticleStaging.Begin(retained, ParticleStaging.MaxRetainedItems, 3));
        Assert.Empty(retained);
        buffers.Get(ParticleBillboardBatcher.MaxQuadsPerDraw + 1, out var burst, out _);
        Assert.True(burst.Length > vertices.Length);
        buffers.Get(1, out var quietVertices, out var quietIndices);
        Assert.Same(vertices, quietVertices);
        Assert.Same(indices, quietIndices);
    }

    [Fact]
    public void Randomised_smoke_matches_base_texture_order_and_geometry_between_calls()
    {
        string[] frames = ["red", "green", "red"]; // aliased atlas entries share a draw
        var budget = new ParticleDensityBudget(EnhancedSmokeField.MaxSmokeOnScreen);
        var bins = new ParticleTextureBins<string>();
        var geometry = new ParticleQuadBuffers();
        var view = Matrix.CreateRotationZ(.27f) * Matrix.CreateRotationX(.15f);
        var random = new Random(1092);
        foreach (int count in new[] { 0, 1, 14, 1600, 2, 0, 63, 7 })
        {
            var puffs = ParticleOracle.Puffs(random, count);
            EnhancedSmokeField.Gather(puffs, Vector3.Zero, Matrix.Identity, null, default, default, default,
                frames, budget, bins);
            var expected = ParticleOracle.Groups(puffs, frames);
            Assert.Equal(expected.Keys, bins.Active.Select(b => b.Texture));
            Assert.Equal(Math.Min(expected.Values.Sum(list => list.Count), budget.Capacity), budget.Count);
            foreach (var (tex, group) in bins.Active)
            {
                var (vertices, indices) = ParticleOracle.Geometry(expected[tex], view);
                geometry.Get(group.Count, out var actual, out var actualIndices);
                ParticleBillboardBatcher.FillBillboardArrays(group, 0, group.Count, view, actual, actualIndices);
                Assert.True(vertices.AsSpan().SequenceEqual(actual.AsSpan(0, group.Count * 4)));
                Assert.True(indices.AsSpan().SequenceEqual(actualIndices.AsSpan(0, group.Count * 6)));
            }
            var used = bins.Active.Select(b => b.Items).ToArray();
            bins.Clear();
            Assert.All(used, bin => { Assert.Empty(bin); Assert.InRange(bin.Capacity, 0, 512); });
        }
    }

    [SkippableTheory, Trait("Requires", "Gpu")]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public async Task Smoke_and_HUD_pixels_match_the_allocating_draw(HudStyle style)
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        string configuration = output.Parent!.Name;
        var repo = output;
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "AllegianceCore.slnx"))) repo = repo.Parent;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(repo!.FullName, "tools/hud2-proof/bin", configuration, output.Name, "hud2-proof.dll"));
        start.ArgumentList.Add("--particle-pixels");
        start.ArgumentList.Add(style.ToString());
        start.Environment["SDL_VIDEODRIVER"] = "offscreen";
        start.Environment["ALSOFT_DRIVERS"] = "null";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw; }
        string log = await stdout + await stderr;
        Require.GraphicsDevice(log);
        Assert.True(process.ExitCode == 0, log);
        Assert.Contains($"Particle pixels match {style}", log);
    }
}
