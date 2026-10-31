using Allegiance.Core.Profiling;
using Allegiance.Core.Transport;

namespace Allegiance.Server;

public sealed partial class Server
{
    private ThreadPoolTelemetry? _threadPoolTelemetry;

    private void EmitSchedulingWindow()
    {
        if (_threadPoolTelemetry is not { } telemetry) return;
        var pool = telemetry.DrainWindow();
        Console.WriteLine(FormattableString.Invariant(
            $"[thread-pool] scope=process windowSecs={pool.Seconds:0.000} threads={pool.Threads} pendingItems={pool.PendingItems} completedPerSecond={pool.CompletedPerSecond:0.###} probeSamples={pool.Delay.Samples} probeMeanMs={pool.Delay.MeanMs:0.###} probeP95UpperMs={pool.Delay.P95UpperMs:0.###} probeMaxMs={pool.Delay.MaxMs:0.###} probePendingMs={pool.ProbePendingMs:0.###}"));
        var udp = (_host?.Transport as DualTransport)?.Udp ?? _host?.Transport as UdpTransport;
        if (udp is null) return;
        var receive = udp.DrainReceiveDelay();
        var delay = receive.CompletionToWorker;
        Console.WriteLine(FormattableString.Invariant(
            $"[udp-receive] windowSecs={pool.Seconds:0.000} asyncSamples={delay.Samples} syncReceives={receive.SynchronousReceives} completionToWorkerMeanMs={delay.MeanMs:0.###} completionToWorkerP95UpperMs={delay.P95UpperMs:0.###} completionToWorkerMaxMs={delay.MaxMs:0.###} kernelArrivalToCompletion=unavailable schedulingProxy=thread-pool-probe"));
    }
}
