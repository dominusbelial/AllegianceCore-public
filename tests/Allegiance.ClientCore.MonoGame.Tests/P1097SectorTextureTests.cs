using System.Collections.Concurrent;
using System.Diagnostics;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P1097SectorTextureTests
{
    // OpenRead can be held across a promotion/reset without sleeping in the game thread.
    private sealed class HeldSource : IAssetSource, IDisposable
    {
        public readonly ConcurrentQueue<string> Opens = new();
        public readonly ManualResetEventSlim Release = new();
        public bool Exists(string path) => true;
        public Stream OpenRead(string path)
        {
            Opens.Enqueue(path);
            if (!Release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException();
            return new MemoryStream(new byte[4]); // platform fallback
        }
        public IEnumerable<string> Enumerate(string path) => [];
        public void Dispose() => Release.Dispose();
    }

    private static SectorTexture Rock(string stem) => new(stem, false, new(StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Same_source_is_admitted_once_and_poster_conversion_is_distinct()
    {
        // A model may use a different texture override in a later sector.
        var plan = new SectorWarmupQueue();
        Assert.True(plan.Enqueue(new(WarmAssetKind.Rock, "model", "shared", 1)));
        Assert.True(plan.Enqueue(new(WarmAssetKind.Rock, "model", "second", 2)));
        Assert.False(plan.Enqueue(new(WarmAssetKind.Rock, "model", "SHARED", 3)));
        using var source = new HeldSource();
        using var preparation = new SectorTexturePreparation(source);
        try
        {
            var rock = Rock("shared");
            preparation.Offer(rock, first: true);
            preparation.Offer(Rock("SHARED"), first: true);
            preparation.Offer(rock with { Poster = true }, first: false);
            Assert.True(SpinWait.SpinUntil(() => source.Opens.Count == 2, 2000));
            Assert.Equal(new[] { "shared.mdl", "shared.mdl" }, source.Opens.ToArray());
        }
        finally { source.Release.Set(); }
    }

    [Fact]
    public void Promotion_preserves_the_head_and_only_admits_urgent_work_when_full()
    {
        using var source = new HeldSource();
        source.Release.Set();
        using var preparation = new SectorTexturePreparation(source);
        var first = Rock("one");
        var second = Rock("two");
        var urgent = Rock("three");
        preparation.Offer(first, true);
        preparation.Offer(second, false);
        Assert.True(SpinWait.SpinUntil(() => preparation.IsPrepared(first) && preparation.IsPrepared(second), 2000));
        preparation.Offer(urgent, false);
        Assert.False(preparation.IsPrepared(urgent));
        preparation.Offer(urgent, true);
        Assert.True(SpinWait.SpinUntil(() => preparation.IsPrepared(urgent), 2000));
        Assert.True(preparation.IsPrepared(first));
        Assert.False(preparation.IsPrepared(second));
        Assert.Equal(3, source.Opens.Count);
    }

    [Fact]
    public void Reset_retires_running_jobs_and_keeps_their_byte_reservations_until_exit()
    {
        using var source = new HeldSource();
        using var preparation = new SectorTexturePreparation(source);
        var old = Rock("old");
        var next = Rock("next");
        try
        {
            preparation.Offer(old, true);
            preparation.Offer(Rock("other"), false);
            Assert.True(SpinWait.SpinUntil(() => source.Opens.Count == 2, 2000));
            preparation.Reset();
            Assert.False(preparation.Offer(next, true));
            source.Release.Set();
            Assert.True(SpinWait.SpinUntil(() =>
            {
                preparation.Offer(next, true);
                return preparation.IsPrepared(next);
            }, 2000));
            Assert.False(preparation.IsPrepared(old));
            Assert.Equal(3, source.Opens.Count);
            preparation.Dispose();
            Assert.False(preparation.Offer(old, true));
            Assert.False(preparation.IsPrepared(old));
        }
        finally { source.Release.Set(); }
    }

    [Fact]
    public async Task Required_lookup_joins_the_inflight_read_before_using_platform_fallback()
    {
        using var source = new HeldSource();
        using var preparation = new SectorTexturePreparation(source);
        using var entered = new ManualResetEventSlim();
        var item = Rock("platform");
        preparation.Offer(item, true);
        Assert.True(SpinWait.SpinUntil(() => source.Opens.Count == 1, 2000));
        // An unsupported source never reaches the graphics device.
        var required = Task.Run(() =>
        {
            entered.Set();
            return preparation.Upload(null!, item, required: true);
        });
        try
        {
            Assert.True(entered.Wait(2000));
            Assert.NotSame(required, await Task.WhenAny(required, Task.Delay(50)));
        }
        finally { source.Release.Set(); }
        Assert.Equal(WarmTextureResult.Deferred, await required);
        Assert.Single(source.Opens);
    }

    [SkippableTheory, Trait("Requires", "Gpu"), Trait("Requires", "Artwork")]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public async Task First_arrival_pixels_match_for_ready_partial_and_cold_textures(HudStyle style)
    {
        Require.Artwork();
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        var repo = output;
        while (!File.Exists(Path.Combine(repo.FullName, "AllegianceCore.slnx"))) repo = repo.Parent!;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(repo.FullName, "tools/hud2-proof/bin", output.Parent!.Name, output.Name, "hud2-proof.dll"));
        start.ArgumentList.Add("--sector-pixels");
        start.ArgumentList.Add(style.ToString());
        start.Environment["SDL_VIDEODRIVER"] = "offscreen";
        start.Environment["ALSOFT_DRIVERS"] = "null";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw; }
        var log = await stdout + await stderr;
        Require.GraphicsDevice(log);
        Assert.True(process.ExitCode == 0, log);
        Assert.Contains($"Sector pixels match {style}", log);
    }
}
