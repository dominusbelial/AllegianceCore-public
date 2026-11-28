// Compiled against a timing-instrumented local copy by measure.py.
using System.Diagnostics;
using System.Runtime.InteropServices;
using Allegiance.SinglePlayer;
using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;

var size = Enum.Parse<SkirmishSize>(args[0]);
var options = new SkirmishOptions { Size = size, StartAsSpectator = true, Seed = 1099, StartCountdownSeconds = 1 };
using var host = await SkirmishHost.StartAsync(options, driverPath: args[1]);
var camera = new SkirmishCamera();
var world = new ClientWorld();
var timeout = Stopwatch.StartNew();
while (!(host.Stats.ValueKind == System.Text.Json.JsonValueKind.Object &&
    host.Stats.GetProperty("botCount").GetInt32() == options.TeamSize * 2 &&
    host.Observe(-1).Objects.OfType<SingleShipUpdate>().Count() >= options.TeamSize))
{
    if (timeout.Elapsed.TotalSeconds > 120 || host.Failure is not null)
        throw new InvalidOperationException("Bot population did not reach the measurement window. " + host.Failure);
    await Task.Delay(200);
}
string? epoch = null;
var rows = new List<(double Wait, double Hold, long Bytes, double Apply, int Ships, double Cpu)>(100);
for (int i = -20; i < 100; i++)
{
    if (i == 0) host.BeginMeasurement();
    double cpu = ThreadClock.Milliseconds();
    long bytes = GC.GetAllocatedBytesForCurrentThread();
    var frame = host.Observe(camera.SectorId);
    bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
    cpu = ThreadClock.Milliseconds() - cpu;
    camera.Update(frame);
    double applyCpu = ThreadClock.Milliseconds();
    long start = Stopwatch.GetTimestamp();
    if (frame.Epoch != epoch) { world.Reset(); epoch = frame.Epoch; }
#if BASELINE
    world.ApplyLocalObservation(frame.Objects, frame.Projectiles, frame.Missiles);
#else
    frame.ApplyTo(world);
#endif
    double apply = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    cpu += ThreadClock.Milliseconds() - applyCpu;
    if (i >= 0) rows.Add((host.WaitMs, host.HoldMs, bytes, apply, world.Ships.Count, cpu));
    Thread.Sleep(100);
}
var ticks = host.TickSamples;
if (ticks.Length == 0 || host.Failure is not null || rows.Any(r => r.Ships == 0))
    throw new InvalidOperationException("Incomplete live measurement. " + host.Failure);
foreach (var row in rows)
    Console.WriteLine(FormattableString.Invariant($"OBS {row.Wait:R} {row.Hold:R} {row.Bytes} {row.Apply:R} {row.Ships} {row.Cpu:R}"));
foreach (var tick in ticks) Console.WriteLine(FormattableString.Invariant($"TICK {tick:R}"));

internal static class ThreadClock
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec { public long Seconds, Nanoseconds; }

    [DllImport("libc", EntryPoint = "clock_gettime")]
    private static extern int Read(int clock, out Timespec value);

    public static double Milliseconds()
    {
        if (Read(3, out var value) != 0) throw new InvalidOperationException("Linux thread CPU clock unavailable.");
        return value.Seconds * 1000.0 + value.Nanoseconds * .000001;
    }
}
