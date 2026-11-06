using System.Diagnostics;
using Allegiance.Core.Sim;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>The client reads the embedded training sim under the host's lock every
/// frame (CopyVisibleBuoys). The background ticker used to hold that lock through
/// Server.Tick's cadence sleep, so a frame waited out whole 50 ms ticks (training ran
/// as a slideshow). The cadence now sleeps outside the lock.</summary>
[Collection("Training")]
public sealed class TrainingHostRenderLockTests
{
    [SkippableFact, Trait("Requires", "Artwork")]
    public void Render_reads_do_not_wait_for_the_tick_cadence()
    {
        Require.Artwork();
        using var host = TrainingHost.Start(TrainingHost.BasicFlight);
        host.StartBackgroundTicker();
        var buoys = new List<Buoy.Visual>();
        var waits = new List<double>();
        var sw = new Stopwatch();
        for (int i = 0; i < 200; i++)
        {
            sw.Restart();
            host.CopyVisibleBuoys(host.StartSectorId, buoys);
            waits.Add(sw.Elapsed.TotalMilliseconds);
            Thread.Sleep(3);
        }
        waits.Sort();
        // Old behaviour: most reads waited tens of ms (up to several 50 ms ticks).
        Assert.True(waits[(int)(waits.Count * 0.95)] < 10, $"p95 wait {waits[(int)(waits.Count * 0.95)]:F1} ms");
    }
}
