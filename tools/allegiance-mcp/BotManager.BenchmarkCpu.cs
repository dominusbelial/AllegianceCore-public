using System.Runtime.InteropServices;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    private static readonly bool BenchmarkCpuEnabled = Environment.GetEnvironmentVariable("ALLEGIANCE_BENCHMARK_METRICS") == "1";
    private readonly struct BotCpuScope : IDisposable
    {
        private readonly ActiveBot _bot;
        private readonly long? _start;
        public BotCpuScope(ActiveBot bot) { _bot = bot; _start = BenchmarkCpuEnabled ? ThreadCpuClock.Nanoseconds() : null; }
        public void Dispose()
        {
            if (_start is { } start && ThreadCpuClock.Nanoseconds() is { } end)
                Interlocked.Add(ref _bot.DecisionCpuNanoseconds, Math.Max(0, end - start));
        }
    }
    internal object[] BenchmarkBotCpu()
    {
        lock (_gate) return _bots.Select(b => (object)new
        {
            name = b.Name, decisionCpuMs = BenchmarkCpuEnabled ? (double?)(Interlocked.Read(ref b.DecisionCpuNanoseconds) / 1_000_000d) : null
        }).ToArray();
    }
}

/// <summary>Calling-thread CPU, excluding sleep, descheduling and gate waits.
/// POSIX CLOCK_THREAD_CPUTIME_ID differs on Darwin. Unsupported platforms report unavailable.</summary>
internal static class ThreadCpuClock
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec { public long Seconds, Nanoseconds; }
    [DllImport("libc", EntryPoint = "clock_gettime")]
    private static extern int ClockGetTime(int clock, out Timespec result);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
    internal static long? Nanoseconds()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel, out long user) ? (kernel + user) * 100 : null;
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                return ClockGetTime(OperatingSystem.IsMacOS() ? 16 : 3, out var time) == 0 ? time.Seconds * 1_000_000_000 + time.Nanoseconds : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return null;
    }
}
