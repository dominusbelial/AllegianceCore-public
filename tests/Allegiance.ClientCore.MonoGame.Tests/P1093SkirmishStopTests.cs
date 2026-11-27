using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.Session;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;
using Microsoft.Xna.Framework.Input;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.MonoGame.Tests;

[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed class P1093SkirmishStopTests(ITestOutputHelper output)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    private static string Driver => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../..", "tools/skirmish-proof/bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net10.0/skirmish-proof.dll"));

    private static string Prepare(string root, string mode)
    {
        var directory = Path.Combine(root, SkirmishRunFiles.Prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "mode"), mode);
        if (mode == "bad-log") Directory.CreateDirectory(Path.Combine(directory, "driver.log"));
        return directory;
    }

    private static void Set(Game1 game, string field, object? value) => typeof(Game1).GetField(field, Hidden)!.SetValue(game, value);
    private static object? Get(Game1 game, string field) => typeof(Game1).GetField(field, Hidden)!.GetValue(game);
    private static void Frame(Game1 game, params Keys[] keys) =>
        typeof(Game1).GetMethod("UpdateSkirmish", Hidden)!.Invoke(game, [new KeyboardState(keys)]);

    [SkippableTheory]
    [InlineData("slow")]
    [InlineData("bad-log")]
    [InlineData("paused")]
    [InlineData("tick")]
    public async Task Exit_frame_releases_match_and_supervises_stop(string mode)
    {
        Require.Artwork();
        var root = Directory.CreateTempSubdirectory("p1093-");
        try
        {
            string dir = Prepare(root.FullName, mode);
            using var host = await SkirmishHost.StartInDirectoryAsync(new(), dir, Driver);
            using var driver = Process.GetProcessById(host.BotProcessIds[0]);
            int[] pids = host.BotProcessIds;
            if (mode is "slow" or "paused") Assert.Equal(2, pids.Length);
            var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
            GC.SuppressFinalize(game); // Headless input test, no graphics resources.
            using var session = new ConnectionSession();
            var cancel = new CancellationTokenSource();
            Set(game, "_session", session);
            Set(game, "_skirmishStop", Task.CompletedTask);
            Set(game, "_skirmish", host);
            Set(game, "_skirmishCancel", cancel);
            Set(game, "_observerAutoStarted", true);
            Task? resume = null;
            if (mode == "paused")
            {
                await host.SetPausedAsync(true);
                Assert.True(host.Paused);
                resume = host.SetPausedAsync(false, cancel.Token);
                Set(game, "_skirmishPause", resume);
            }
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            if (mode == "tick")
            {
                host.Server.TickMeasured += _ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); };
                Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            }
            if (mode == "bad-log")
            {
                var deadline = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(dir, "drained")) && deadline.ElapsedMilliseconds < 3000)
                    await Task.Delay(10);
                Assert.True(File.Exists(Path.Combine(dir, "drained")), "Child blocked writing stderr");
            }
            var clock = Stopwatch.StartNew();
            Frame(game, Keys.LeftControl, Keys.F10);
            double elapsed = clock.Elapsed.TotalMilliseconds;
            output.WriteLine($"{mode}: exit frame {elapsed:F2} ms");
            var stopping = (Task)Get(game, "_skirmishStop")!;
            using var nextSession = (ConnectionSession)Get(game, "_session")!;
            double? releasedAt = null;
            try
            {
                Assert.True(elapsed < 100, $"Exit frame took {elapsed:F2} ms");
                Assert.Null(Get(game, "_skirmish"));
                Assert.Null(Get(game, "_skirmishPause"));
                Assert.Equal("Intro", Get(game, "_state")!.ToString());
                Assert.Equal("SinglePlayer", Get(game, "_introView")!.ToString());
                Frame(game, Keys.LeftControl, Keys.F10);
                typeof(Game1).GetMethod("StopSkirmish", Hidden)!.Invoke(game, null);
                Assert.Same(stopping, Get(game, "_skirmishStop"));

                if (mode == "tick")
                {
                    int port = host.Port;
                    typeof(Game1).GetMethod("BeginSkirmish", Hidden)!.Invoke(game,
                        [new SkirmishOptions { HostLan = true, HostPort = port }, "test-pilot"]);
                    var next = (Task<SkirmishHost>)Get(game, "_skirmishStart")!;
                    try
                    {
                        // Hold the real tick beyond the driver's entire grace period.
                        await Task.Delay(5200);
                        Assert.False(stopping.IsCompleted);
                        Assert.False(next.IsCompleted);
                        using var probe = new TcpListener(IPAddress.Loopback, port);
                        Assert.Equal(SocketError.AddressAlreadyInUse, Assert.Throws<SocketException>(probe.Start).SocketErrorCode);
                    }
                    finally
                    {
                        release.Set();
                        // Even a deliberately broken gate must not leak the second host.
                        await stopping.WaitAsync(TimeSpan.FromSeconds(20));
                        releasedAt = clock.Elapsed.TotalMilliseconds;
                        using var second = await next.WaitAsync(TimeSpan.FromMinutes(2));
                        Assert.Equal(port, second.Port);
                        Assert.True(second.BotsAlive);
                        ((CancellationTokenSource)Get(game, "_skirmishCancel")!).Dispose();
                    }
                }
            }
            finally
            {
                release.Set();
                await stopping.WaitAsync(TimeSpan.FromSeconds(20));
            }
            output.WriteLine($"{mode}: ownership released at {releasedAt ?? clock.Elapsed.TotalMilliseconds:F2} ms");
            Assert.True(session.IsDisposed);
            Assert.True(driver.HasExited);
            Assert.All(pids, pid => Assert.False(Running(pid), $"Owned PID {pid} survived stop"));
            Assert.Same(host.StopAsync(), host.StopAsync());
            Assert.False(Directory.Exists(dir));
            if (resume is not null) Assert.True(resume.IsCanceled || resume.IsFaulted);

        }
        finally { root.Delete(true); }
    }

    private static bool Running(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
    }

    [SkippableFact]
    public async Task Repeated_stops_and_partial_starts_bound_retained_files()
    {
        Require.Artwork();
        var root = Directory.CreateTempSubdirectory("p1093-runs-");
        try
        {
            for (int run = 0; run < 4; ++run)
            {
                string dir = Prepare(root.FullName, "normal");
                using var host = await SkirmishHost.StartInDirectoryAsync(new(), dir, Driver);
            }
            output.WriteLine($"four successes: {root.GetDirectories().Length} directories, {root.GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)} bytes");
            Assert.Empty(root.GetDirectories());
            for (int run = 0; run < 6; ++run)
            {
                string dir = Prepare(root.FullName, "partial");
                var ex = await Assert.ThrowsAsync<IOException>(() => SkirmishHost.StartInDirectoryAsync(new(), dir, Driver));
                string receipt = Path.Combine(root.FullName, "partial-worker");
                int worker = int.Parse(File.ReadAllText(receipt));
                File.Delete(receipt);
                Assert.False(Running(worker), "Unannounced worker survived partial startup cleanup");
                Assert.Equal("Bot driver exited. Exit and start a new skirmish. Log: " + dir, ex.Message);
                Assert.Equal(SkirmishRunFiles.LogBytes, new FileInfo(Path.Combine(dir, "driver.log")).Length);
                Assert.False(File.Exists(Path.Combine(dir, "server.json")));
            }
            Assert.Equal(SkirmishRunFiles.FailedRuns, root.GetDirectories().Length);
            Assert.Equal((long)SkirmishRunFiles.FailedRuns * SkirmishRunFiles.LogBytes,
                root.GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length));
            output.WriteLine($"after six failures: {root.GetDirectories().Length} directories, {root.GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)} bytes");
        }
        finally { root.Delete(true); }
    }
}
